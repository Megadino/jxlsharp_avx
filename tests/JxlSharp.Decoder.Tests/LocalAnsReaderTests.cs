using System;
using System.Buffers.Binary;
using JxlSharp.Base;
using NUnit.Framework;

namespace JxlSharp.Decoder.Tests;

/// <summary>Local ANS state must preserve symbols, bit consumption and the committed shared state.</summary>
[TestFixture]
public sealed class LocalAnsReaderTests
{
    /// <summary>Exercise multiple histograms, hybrid configurations, renormalization and truncated input.</summary>
    [TestCase(8)]
    [TestCase(64)]
    [TestCase(4096)]
    public void LocalReaderMatchesClassReader(int length)
    {
        var code = new ANSCode
        {
            LogAlphaSize = 5,
            AliasTables = new AliasTable.Entry[3 * 32],
            UintConfig = new[] { new HybridUintConfig(2, 1, 0), new HybridUintConfig(3, 1, 1), new HybridUintConfig(4, 0, 0) },
        };
        for (int context = 0; context < 3; context++)
        {
            int[] distribution = new int[32];
            for (int i = 0; i < distribution.Length; i++)
                distribution[i] = context == 0 ? 128 : context == 1 ? i < 16 ? 256 : 0 : i == 0 ? 2112 : 64;
            Assert.That(AliasTable.InitAliasTable(distribution, AnsParams.AnsLogTabSize, 5,
                code.AliasTables.AsSpan(context * 32, 32)).IsOk, Is.True);
        }
        byte[] data = new byte[length];
        new Random(351).NextBytes(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data, AnsParams.AnsSignature << 16);
        using var expectedBits = new BitReader(data);
        using var actualBits = new BitReader(data);
        var expected = ANSSymbolReader.Create(code, expectedBits).Value;
        var owner = ANSSymbolReader.Create(code, actualBits).Value;
        var actual = new ANSSymbolReader.LocalAnsReader(owner, actualBits);
        var expectedState = new ANSSymbolReader.Checkpoint();
        var actualState = new ANSSymbolReader.Checkpoint();
        for (int i = 0; i < 512; i++)
        {
            nuint context = (nuint)((i * 7) % 3);
            Assert.That(actual.Read(context), Is.EqualTo(expected.ReadHybridUintANS(context, expectedBits)));
            Assert.That(actualBits.TotalBitsConsumed(), Is.EqualTo(expectedBits.TotalBitsConsumed()));
            if (i % 31 == 0)
            {
                actual.Commit();
                expected.Save(expectedState);
                owner.Save(actualState);
                Assert.That(actualState.State, Is.EqualTo(expectedState.State));
            }
        }
        actual.Commit();
        expected.Save(expectedState);
        owner.Save(actualState);
        Assert.That(actualState.State, Is.EqualTo(expectedState.State));
        Assert.That(actualBits.AllReadsWithinBounds(), Is.EqualTo(expectedBits.AllReadsWithinBounds()));
    }
}
