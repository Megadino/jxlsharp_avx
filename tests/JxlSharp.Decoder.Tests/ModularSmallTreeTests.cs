using System;
using JxlSharp.Base;
using NUnit.Framework;

namespace JxlSharp.Decoder.Tests;

/// <summary>Compiled metadata decisions must retain tree traversal, prediction and integer overflow semantics.</summary>
[TestFixture]
public sealed class ModularSmallTreeTests
{
    /// <summary>Compare every decoded pixel and chosen context across row/neighbor thresholds.</summary>
    [TestCase(1, 1)]
    [TestCase(9, 3)]
    [TestCase(257, 7)]
    public void TableMatchesGeneralPrediction(int width, int height)
    {
        for (int variant = 0; variant < 3; variant++)
        {
            var tree = Tree(variant);
            int[] staticProps = { 2, 5 };
            var table = ModularSmallTreeDecoder.Compile(tree, staticProps);
            Assert.That(table, Is.Not.Null);
            using var expected = Channel.Create(null, (nuint)width, (nuint)height).Value;
            using var actual = Channel.Create(null, (nuint)width, (nuint)height).Value;
            uint[] residuals = new uint[width * height];
            uint[] contexts = new uint[residuals.Length];
            var random = new Random(501);
            for (int i = 0; i < residuals.Length; i++) residuals[i] = (uint)random.NextInt64(0, (long)uint.MaxValue + 1);
            var lookup = new MATreeLookup(tree);
            int[] properties = new int[ModularPredictors.KNumNonrefProperties];
            for (int y = 0, index = 0; y < height; y++)
            {
                Span<int> row = expected.Row((nuint)y);
                ReadOnlySpan<int> top = y > 0 ? expected.ConstRow((nuint)y - 1) : row;
                ReadOnlySpan<int> topTop = y > 1 ? expected.ConstRow((nuint)y - 2) : top;
                ModularPredictors.InitPropsRow(properties, staticProps, y);
                for (int x = 0; x < width; x++, index++)
                {
                    var neighbors = ModularPredictors.GatherNeighbors(row, top, topTop, x, y);
                    ModularPredictors.ComputeSpatialProperties(properties, x, in neighbors);
                    var leaf = lookup.Lookup(properties);
                    contexts[index] = leaf.Context;
                    long guess = leaf.Offset + ModularPredictors.PredictOne(leaf.Predictor, in neighbors, 0);
                    row[x] = unchecked((int)((long)PackSigned.Unpack(residuals[index]) * leaf.Multiplier + guess));
                }
            }
            var reader = new ResidualReader(residuals, contexts);
            ModularSmallTreeDecoder.Decode(actual, table!, ref reader);
            Assert.That(reader.Index, Is.EqualTo(residuals.Length));
            for (nuint y = 0; y < (nuint)height; y++)
                Assert.That(actual.ConstRow(y).ToArray(), Is.EqualTo(expected.ConstRow(y).ToArray()));
        }
    }

    /// <summary>Unsupported predictors/properties and large threshold ranges retain the general path.</summary>
    [Test]
    public void UnsupportedTreesAreRejected()
    {
        int[] props = { 0, 0 };
        var tree = Tree(0);
        tree[1].Predictor = Predictor.Gradient;
        Assert.That(ModularSmallTreeDecoder.Compile(tree, props), Is.Null);
        tree = Tree(0);
        tree[0].Property0 = 9;
        Assert.That(ModularSmallTreeDecoder.Compile(tree, props), Is.Null);
        tree = Tree(0);
        tree[0].SplitVal0 = int.MaxValue;
        Assert.That(ModularSmallTreeDecoder.Compile(tree, props), Is.Null);
    }

    private static FlatDecisionNode[] Tree(int variant)
    {
        var nodes = new FlatDecisionNode[5];
        nodes[0] = new FlatDecisionNode { Property0 = variant == 0 ? 2 : 7, SplitVal0 = variant == 0 ? 0 : 3,
            PropertyChild0 = 7, SplitValChild0 = 5, PropertyChild1 = (short)(variant == 2 ? 7 : 6),
            SplitValChild1 = 1, ChildID = 1 };
        for (int i = 1; i < 5; i++)
            nodes[i] = new FlatDecisionNode { Property0 = -1, ChildID = (uint)(i - 1),
                Predictor = i % 3 == 0 ? Predictor.Top : i % 3 == 1 ? Predictor.Left : Predictor.Zero,
                PredictorOffset = i == 4 ? int.MaxValue : i - 7, Multiplier = i };
        return nodes;
    }

    private struct ResidualReader(uint[] tokens, uint[] contexts) : ModularWpLutDecoder.IReader
    {
        internal int Index;
        public uint Read(nuint context)
        {
            Assert.That(context, Is.EqualTo((nuint)contexts[Index]));
            return tokens[Index++];
        }
    }
}
