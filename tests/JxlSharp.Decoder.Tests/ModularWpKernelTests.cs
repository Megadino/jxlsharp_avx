using System;
using JxlSharp.Base;
using NUnit.Framework;

namespace JxlSharp.Decoder.Tests;

/// <summary>Compare the fused WP loop with the public predictor, including context selection and overflow.</summary>
[TestFixture]
public sealed class ModularWpKernelTests
{
    /// <summary>Exercise row recycling, both edges, predictor presets and all residual bit widths.</summary>
    [TestCase(1, 1)]
    [TestCase(9, 1)]
    [TestCase(9, 3)]
    [TestCase(17, 5)]
    [TestCase(257, 31)]
    public void PixelsAndContextsMatchReference(int width, int height)
    {
        var random = new Random(431);
        Span<int> properties = stackalloc int[1];
        for (int mode = 0; mode < 8; mode++)
        for (int pattern = 0; pattern < 4; pattern++)
        {
            var header = new WeightedHeader();
            if (mode < 5) header.SetPredictorMode(mode);
            else
            {
                header.P1C = random.Next(32);
                header.P2C = random.Next(32);
                header.P3Ca = random.Next(32);
                header.P3Cb = random.Next(32);
                header.P3Cc = random.Next(32);
                header.P3Cd = random.Next(32);
                header.P3Ce = random.Next(32);
                for (int i = 0; i < 4; i++) header.W[i] = (uint)random.Next(16);
            }
            byte[] lut = new byte[2 * TreeLut.KPropRangeFast];
            random.NextBytes(lut);
            uint[] residuals = new uint[width * height];
            for (int i = 0; i < residuals.Length; i++)
                residuals[i] = pattern switch
                {
                    0 => 0u,
                    1 => (uint)(i % 9),
                    2 => (uint)random.Next(129),
                    _ => (uint)random.NextInt64(0, (long)uint.MaxValue + 1),
                };
            using var expected = Channel.Create(null, (nuint)width, (nuint)height).Value;
            using var actual = Channel.Create(null, (nuint)width, (nuint)height).Value;
            byte[] contexts = new byte[residuals.Length];
            var reference = new WeightedState(header, (nuint)width, (nuint)height);
            for (int y = 0, index = 0; y < height; y++)
            {
                Span<int> row = expected.Row((nuint)y);
                ReadOnlySpan<int> top = y > 0 ? expected.ConstRow((nuint)y - 1) : row;
                ReadOnlySpan<int> topTop = y > 1 ? expected.ConstRow((nuint)y - 2) : top;
                for (int x = 0; x < width; x++, index++)
                {
                    var neighbors = ModularPredictors.GatherNeighbors(row, top, topTop, x, y);
                    int guess = (int)reference.Predict((nuint)x, (nuint)y, (nuint)width,
                        in neighbors, properties, 0, true);
                    contexts[index] = lut[TreeLut.KPropRangeFast +
                        Math.Clamp(properties[0], -TreeLut.KPropRangeFast, TreeLut.KPropRangeFast - 1)];
                    row[x] = unchecked((int)((long)PackSigned.Unpack(residuals[index]) + guess));
                    reference.UpdateErrors(row[x], (nuint)x, (nuint)y, (nuint)width);
                }
            }
            var reader = new ResidualReader(residuals, contexts);
            ModularWpLutDecoder.Decode(actual, header, lut, ref reader);
            Assert.That(reader.Index, Is.EqualTo(residuals.Length));
            for (nuint y = 0; y < (nuint)height; y++)
                Assert.That(actual.ConstRow(y).ToArray(), Is.EqualTo(expected.ConstRow(y).ToArray()),
                    $"width={width}, height={height}, mode={mode}, pattern={pattern}, y={y}");
        }
    }

    private struct ResidualReader(uint[] residuals, byte[] contexts) : ModularWpLutDecoder.IReader
    {
        internal int Index;

        public uint Read(nuint context)
        {
            Assert.That(context, Is.EqualTo((nuint)contexts[Index]), $"context at pixel {Index}");
            return residuals[Index++];
        }
    }
}
