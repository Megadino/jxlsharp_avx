using System;
using System.Runtime.CompilerServices;
using JxlSharp.Base;

namespace JxlSharp.Decoder;

// AC metadata often uses small thresholds on row, top and left, with simple
// predictors. Compile that whole decision into a compact table once per channel.
internal static class ModularSmallTreeDecoder
{
    internal sealed class Table
    {
        internal readonly MATreeLookup.LookupResult[] Leaves;
        internal readonly int YMin, YMax, TopMin, TopMax, LeftMin, LeftMax;
        internal readonly int YCount, TopCount, LeftCount;

        internal Table(int[] min, int[] max, int[] counts)
        {
            YMin = min[0]; YMax = max[0]; YCount = counts[0];
            TopMin = min[1]; TopMax = max[1]; TopCount = counts[1];
            LeftMin = min[2]; LeftMax = max[2]; LeftCount = counts[2];
            Leaves = new MATreeLookup.LookupResult[YCount * TopCount * LeftCount];
        }
    }

    internal static Table? Compile(FlatDecisionNode[] tree, ReadOnlySpan<int> staticProperties)
    {
        int[] min = { int.MaxValue, int.MaxValue, int.MaxValue };
        int[] max = { int.MinValue, int.MinValue, int.MinValue };
        bool Mark(int property, int split)
        {
            int index = property switch { 0 or 1 => -1, 2 => 0, 6 => 1, 7 => 2, _ => -2 };
            if (index == -2) return false;
            if (index >= 0) { min[index] = Math.Min(min[index], split); max[index] = Math.Max(max[index], split); }
            return true;
        }
        foreach (ref readonly var node in tree.AsSpan())
        {
            if (node.Property0 < 0)
            {
                if (node.Predictor is not (Predictor.Zero or Predictor.Left or Predictor.Top)) return null;
            }
            else if (!Mark(node.Property0, node.SplitVal0) || !Mark(node.PropertyChild0, node.SplitValChild0) ||
                     !Mark(node.PropertyChild1, node.SplitValChild1)) return null;
        }
        int[] counts = new int[3];
        int total = 1;
        for (int i = 0; i < 3; i++)
        {
            if (min[i] == int.MaxValue && max[i] == int.MinValue) { min[i] = max[i] = 0; counts[i] = 1; }
            else
            {
                long count = (long)max[i] - min[i] + 2;
                if (count > 32 || max[i] == int.MaxValue) return null;
                counts[i] = (int)count;
                max[i]++;
            }
            total *= counts[i];
            if (total > 1024) return null;
        }
        var table = new Table(min, max, counts);
        var lookup = new MATreeLookup(tree);
        Span<int> properties = stackalloc int[8];
        staticProperties.CopyTo(properties);
        int offset = 0;
        for (int y = 0; y < counts[0]; y++)
        for (int top = 0; top < counts[1]; top++)
        for (int left = 0; left < counts[2]; left++)
        {
            properties[2] = min[0] + y;
            properties[6] = min[1] + top;
            properties[7] = min[2] + left;
            table.Leaves[offset++] = lookup.Lookup(properties);
        }
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Decode<TReader>(Channel channel, Table table, ref TReader reader)
        where TReader : struct, ModularWpLutDecoder.IReader
    {
        TReader localReader = reader;
        int width = (int)channel.W;
        for (nuint y = 0; y < channel.H; y++)
        {
            Span<int> row = channel.Row(y);
            ReadOnlySpan<int> topRow = y > 0 ? channel.ConstRow(y - 1) : row;
            int yIndex = table.YCount == 1 ? 0 : Math.Clamp((int)y, table.YMin, table.YMax) - table.YMin;
            int rowOffset = yIndex * table.TopCount * table.LeftCount;
            int left = y > 0 ? topRow[0] : 0;
            for (int x = 0; x < width; x++)
            {
                int top = y > 0 ? topRow[x] : left;
                int ti = table.TopCount == 1 ? 0 : Math.Clamp(top, table.TopMin, table.TopMax) - table.TopMin;
                int li = table.LeftCount == 1 ? 0 : Math.Clamp(left, table.LeftMin, table.LeftMax) - table.LeftMin;
                ref readonly var leaf = ref table.Leaves[rowOffset + ti * table.LeftCount + li];
                long guess = leaf.Offset + (long)(leaf.Predictor == Predictor.Left ? left :
                    leaf.Predictor == Predictor.Top ? top : 0);
                uint residual = localReader.Read(leaf.Context);
                left = unchecked((int)((long)PackSigned.Unpack(residual) * leaf.Multiplier + guess));
                row[x] = left;
            }
        }
        reader = localReader;
    }
}
