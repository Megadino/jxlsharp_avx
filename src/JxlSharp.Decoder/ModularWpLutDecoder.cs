using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using JxlSharp.Base;

namespace JxlSharp.Decoder;

// Internal WP-only LUT kernel. Adjacent pixels remain dependent; the four
// sub-predictor error lanes are interleaved and advanced together along a row.
internal static class ModularWpLutDecoder
{
    internal interface IReader
    {
        uint Read(nuint context);
    }

    internal struct AnsReader(ANSSymbolReader symbols, BitReader bits) : IReader
    {
        private ANSSymbolReader.LocalAnsReader _reader = new(symbols, bits);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Read(nuint context) => _reader.Read(context);

        internal readonly void Commit() => _reader.Commit();
    }

    internal readonly struct PrefixReader(ANSSymbolReader symbols, BitReader bits) : IReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Read(nuint context) => symbols.ReadHybridUintClustered(false, context, bits);
    }

    internal static void Decode<TReader>(Channel channel, WeightedHeader header, byte[] contextLut, ref TReader reader)
        where TReader : struct, IReader
    {
        // Known encoder presets become constants throughout the hot loop. The
        // general kernel retains support for every serialized coefficient set.
        int mode = Preset(header);
        switch (mode)
        {
            case 0: Decode<TReader, Mode0>(channel, header, contextLut, ref reader); break;
            case 1: Decode<TReader, Mode1>(channel, header, contextLut, ref reader); break;
            case 2: Decode<TReader, Mode2>(channel, header, contextLut, ref reader); break;
            case 3: Decode<TReader, Mode3>(channel, header, contextLut, ref reader); break;
            case 4: Decode<TReader, Mode4>(channel, header, contextLut, ref reader); break;
            default: Decode<TReader, GeneralMode>(channel, header, contextLut, ref reader); break;
        }
    }

    private interface IMode { static abstract int Value { get; } }
    private readonly struct Mode0 : IMode { public static int Value => 0; }
    private readonly struct Mode1 : IMode { public static int Value => 1; }
    private readonly struct Mode2 : IMode { public static int Value => 2; }
    private readonly struct Mode3 : IMode { public static int Value => 3; }
    private readonly struct Mode4 : IMode { public static int Value => 4; }
    private readonly struct GeneralMode : IMode { public static int Value => -1; }

    private static int Preset(WeightedHeader h)
    {
        if (h.W[0] != 13) return -1;
        if (h.W[1] == 12 && h.W[2] == 12 && h.W[3] == 12)
        {
            if (h.P1C == 16 && h.P2C == 10 && h.P3Ca == 7 && h.P3Cb == 7 && h.P3Cc == 7 && h.P3Cd == 0 && h.P3Ce == 0) return 0;
            if (h.P1C == 10 && h.P2C == 10 && h.P3Ca == 5 && h.P3Cb == 5 && h.P3Cc == 5 && h.P3Cd == 12 && h.P3Ce == 4) return 4;
        }
        if (h.W[1] == 12 && h.W[2] == 12 && h.W[3] == 11 && h.P1C == 8 && h.P2C == 8 && h.P3Ca == 4 && h.P3Cb == 0 && h.P3Cc == 3 && h.P3Cd == 23 && h.P3Ce == 2) return 1;
        if (h.W[1] == 12 && h.W[2] == 13 && h.W[3] == 12 && h.P1C == 10 && h.P2C == 9 && h.P3Ca == 7 && h.P3Cb == 0 && h.P3Cc == 0 && h.P3Cd == 16 && h.P3Ce == 9) return 2;
        if (h.W[1] == 13 && h.W[2] == 12 && h.W[3] == 12 && h.P1C == 16 && h.P2C == 8 && h.P3Ca == 0 && h.P3Cb == 16 && h.P3Cc == 0 && h.P3Cd == 23 && h.P3Ce == 0) return 3;
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Decode<TReader, TMode>(Channel channel, WeightedHeader header, byte[] contextLut, ref TReader reader)
        where TReader : struct, IReader where TMode : struct, IMode
    {
        TReader localReader = reader;
        int width = checked((int)channel.W);
        var errors = new Vector128<uint>[checked(width * 2)];
        var signedErrors = new int[checked(width * 2)];
        uint max0 = TMode.Value >= 0 ? 13u : header.W[0];
        uint max1 = TMode.Value >= 0 ? TMode.Value == 3 ? 13u : 12u : header.W[1];
        uint max2 = TMode.Value >= 0 ? TMode.Value == 2 ? 13u : 12u : header.W[2];
        uint max3 = TMode.Value >= 0 ? TMode.Value == 1 ? 11u : 12u : header.W[3];
        int p1c = TMode.Value switch { 0 or 3 => 16, 1 => 8, 2 or 4 => 10, _ => header.P1C };
        int p2c = TMode.Value switch { 0 or 4 => 10, 1 or 3 => 8, 2 => 9, _ => header.P2C };
        int p3a = TMode.Value switch { 0 or 2 => 7, 1 => 4, 3 => 0, 4 => 5, _ => header.P3Ca };
        int p3b = TMode.Value switch { 0 => 7, 1 or 2 => 0, 3 => 16, 4 => 5, _ => header.P3Cb };
        int p3c = TMode.Value switch { 0 => 7, 1 => 3, 2 or 3 => 0, 4 => 5, _ => header.P3Cc };
        int p3d = TMode.Value switch { 0 => 0, 1 or 3 => 23, 2 => 16, 4 => 12, _ => header.P3Cd };
        int p3e = TMode.Value switch { 0 or 3 => 0, 1 => 2, 2 => 9, 4 => 4, _ => header.P3Ce };

        for (nuint y = 0; y < channel.H; y++)
        {
            Span<int> row = channel.Row(y);
            ReadOnlySpan<int> topRow = y > 0 ? channel.ConstRow(y - 1) : row;
            ReadOnlySpan<int> topTopRow = default;
            if (p3d != 0) topTopRow = y > 1 ? channel.ConstRow(y - 2) : topRow;
            int current = (y & 1) == 0 ? width : 0;
            int previous = (y & 1) == 0 ? 0 : width;
            Span<Vector128<uint>> currentErrors = errors.AsSpan(current, width);
            ReadOnlySpan<Vector128<uint>> previousErrors = errors.AsSpan(previous, width);
            Span<int> currentSigned = signedErrors.AsSpan(current, width);
            ReadOnlySpan<int> previousSigned = signedErrors.AsSpan(previous, width);

            Vector128<uint> eNorth = previousErrors[0], eNorthWest = eNorth;
            Vector128<uint> eNorthEast = previousErrors[width > 1 ? 1 : 0];
            long north = previousSigned[0], northWest = north;
            long northEast = previousSigned[width > 1 ? 1 : 0];
            long west = 0;
            int left = y > 0 ? topRow[0] : 0;

            for (int x = 0; x < width; x++)
            {
                // Carry the preceding pixels' NE corrections in registers. The
                // old row stays read-only and only one new error vector is loaded.
                Vector128<uint> accumulated = eNorth + eNorthWest + eNorthEast;
                uint w0 = WeightedState.ErrorWeightCached(accumulated.GetElement(0), max0);
                uint w1 = WeightedState.ErrorWeightCached(accumulated.GetElement(1), max1);
                uint w2 = WeightedState.ErrorWeightCached(accumulated.GetElement(2), max2);
                uint w3 = WeightedState.ErrorWeightCached(accumulated.GetElement(3), max3);

                long n = WeightedState.AddBits(y > 0 ? topRow[x] : left);
                long w = WeightedState.AddBits(left);
                long ne = y > 0 && x + 1 < width ? WeightedState.AddBits(topRow[x + 1]) : n;
                long nw = p3e != 0 && y > 0 && x > 0 ? WeightedState.AddBits(topRow[x - 1]) : w;
                long nn = p3d != 0 && y > 1 ? WeightedState.AddBits(topTopRow[x]) : n;
                long sumWN = north + west;
                long p0 = w + ne - n;
                // Signed error values are int32, so these preset reductions cannot overflow int64.
                long p1 = n - (TMode.Value is 0 or 3 ? (sumWN + northEast) >> 1 :
                    TMode.Value == 1 ? (sumWN + northEast) >> 2 : ((sumWN + northEast) * p1c) >> 5);
                long p2 = w - (TMode.Value is 0 or 4 ? ((sumWN + northWest) * 5) >> 4 :
                    TMode.Value is 1 or 3 ? (sumWN + northWest) >> 2 : ((sumWN + northWest) * p2c) >> 5);
                long p3 = TMode.Value == 0 ? n - (((northWest + north + northEast) * 7) >> 5) :
                    n - (((northWest * p3a) + (north * p3b) + (northEast * p3c) +
                    ((nn - n) * p3d) + ((nw - w) * p3e)) >> 5);

                uint weightSum = w0 + w1 + w2 + w3;
                int shift = (int)Bits.FloorLog2Nonzero(weightSum) - 4;
                w0 >>= shift;
                w1 >>= shift;
                w2 >>= shift;
                w3 >>= shift;
                weightSum = w0 + w1 + w2 + w3;
                long sum = (weightSum >> 1) - 1 + p0 * w0 + p1 * w1 + p2 * w2 + p3 * w3;
                long prediction = (sum * WeightedState.NormalizedWeightReciprocal(weightSum)) >> 24;
                if (((north ^ west) | (north ^ northWest)) <= 0)
                    prediction = Math.Max(Math.Min(w, Math.Min(ne, n)), Math.Min(Math.Max(w, Math.Max(ne, n)), prediction));

                // Keep the four predictions in a vector across entropy decoding,
                // rather than extending four scalar register lifetimes through it.
                var predictions = Vector256.Create(p0, p1, p2, p3);
                int property = (int)WeightedState.ComputeWpProperty(west, north, northWest, northEast);
                int context = TreeLut.KPropRangeFast + Math.Clamp(property, -TreeLut.KPropRangeFast, TreeLut.KPropRangeFast - 1);
                int guess = (int)((prediction + WeightedHeader.KPredictionRound) >> WeightedHeader.KPredExtraBits);
                int pixel = unchecked(PackSigned.Unpack(localReader.Read(contextLut[context])) + guess);
                row[x] = pixel;
                left = pixel;
                long value = WeightedState.AddBits(pixel);
                west = (int)(prediction - value);
                currentSigned[x] = (int)west;
                Vector128<uint> error = PredictionErrors(predictions, value);
                currentErrors[x] = error;

                if (x + 1 < width)
                {
                    Vector128<uint> updatedNorthEast = eNorthEast + error;
                    eNorthWest = eNorth;
                    eNorth = updatedNorthEast;
                    eNorthEast = x + 2 < width ? previousErrors[x + 2] : eNorth;
                    northWest = north;
                    north = northEast;
                    northEast = x + 2 < width ? previousSigned[x + 2] : north;
                }
            }
        }
        reader = localReader;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> PredictionErrors(Vector256<long> predictions, long value)
    {
        if (Avx2.IsSupported)
        {
            var delta = Avx2.Subtract(predictions, Vector256.Create(value));
            if (Vector256.EqualsAll(delta, Vector256<long>.Zero)) return Vector128<uint>.Zero;
            var sign = Avx2.CompareGreaterThan(Vector256<long>.Zero, delta);
            var absolute = Avx2.Subtract(Avx2.Xor(delta, sign), sign);
            if (Avx.MoveMask(absolute.AsDouble()) != 0) throw new OverflowException();
            var error = Avx2.ShiftRightLogical(
                Avx2.Add(absolute, Vector256.Create(WeightedHeader.KPredictionRound)).AsUInt64(),
                WeightedHeader.KPredExtraBits);
            return Vector128.Narrow(error.GetLower(), error.GetUpper());
        }
        return Vector128.Create(
            (uint)((Math.Abs(predictions.GetElement(0) - value) + WeightedHeader.KPredictionRound) >> WeightedHeader.KPredExtraBits),
            (uint)((Math.Abs(predictions.GetElement(1) - value) + WeightedHeader.KPredictionRound) >> WeightedHeader.KPredExtraBits),
            (uint)((Math.Abs(predictions.GetElement(2) - value) + WeightedHeader.KPredictionRound) >> WeightedHeader.KPredExtraBits),
            (uint)((Math.Abs(predictions.GetElement(3) - value) + WeightedHeader.KPredictionRound) >> WeightedHeader.KPredExtraBits));
    }
}
