using System.Numerics;

namespace Flipper.Core.Reader;

internal static class VoiceSoundFeatures
{
    internal const int MinimumFrames = 5;
    internal const int MaximumFrames = 155;
    internal const int CoefficientCount = 12;
    internal const float MaximumCoefficient = 64;
    private const int WindowSize = 400;
    private const int Step = 320;
    private const int FftSize = 512;
    private const int MelBandCount = 24;
    private static readonly float[] Window = Enumerable.Range(0, WindowSize)
        .Select(i => 0.54f - 0.46f * MathF.Cos(2 * MathF.PI * i / (WindowSize - 1))).ToArray();
    private static readonly Complex[] Roots = Enumerable.Range(0, FftSize / 2)
        .Select(i => Complex.FromPolarCoordinates(1, -2 * Math.PI * i / FftSize)).ToArray();
    private static readonly float[] MelBins = Enumerable.Range(0, MelBandCount + 2)
        .Select(i => (float)((700 * (Math.Pow(1 + 7600.0 / 700, (double)i / (MelBandCount + 1)) - 1))
            * FftSize / VoiceSoundCalibration.SampleRate)).ToArray();
    private static readonly float[][] Cosines = Enumerable.Range(1, CoefficientCount)
        .Select(coefficient => Enumerable.Range(0, MelBandCount)
            .Select(band => MathF.Sqrt(2f / MelBandCount) * MathF.Cos(MathF.PI * coefficient * (band + 0.5f) / MelBandCount))
            .ToArray()).ToArray();

    internal static VoiceSoundTemplate Extract(ReadOnlySpan<float> audio)
    {
        var frameCount = audio.Length / Step;
        var coefficients = new float[frameCount * CoefficientCount];
        Span<Complex> spectrum = stackalloc Complex[FftSize];
        Span<double> logBands = stackalloc double[MelBandCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            spectrum.Clear();
            var offset = frame * Step;
            var previous = offset == 0 ? 0 : audio[offset - 1];
            for (var i = 0; i < WindowSize && offset + i < audio.Length; i++)
            {
                var sample = audio[offset + i];
                spectrum[i] = (sample - 0.95f * previous) * Window[i];
                previous = sample;
            }

            Transform(spectrum);
            var peakBand = 0d;
            for (var band = 0; band < MelBandCount; band++)
            {
                var left = MelBins[band];
                var centre = MelBins[band + 1];
                var right = MelBins[band + 2];
                var power = 0d;
                for (var bin = (int)Math.Ceiling(left); bin < right; bin++)
                {
                    var weight = Math.Min((bin - left) / (centre - left), (right - bin) / (right - centre));
                    var complex = spectrum[bin];
                    power += weight * (complex.Real * complex.Real + complex.Imaginary * complex.Imaginary);
                }
                logBands[band] = power;
                peakBand = Math.Max(peakBand, power);
            }

            // A relative floor and omission of C0 retain spectral shape across changes in volume.
            var floor = Math.Max(peakBand * 0.0001, 1e-12);
            for (var band = 0; band < MelBandCount; band++)
            {
                logBands[band] = Math.Log(Math.Max(logBands[band], floor));
            }
            for (var coefficient = 0; coefficient < CoefficientCount; coefficient++)
            {
                var value = 0d;
                for (var band = 0; band < MelBandCount; band++)
                {
                    value += logBands[band] * Cosines[coefficient][band];
                }
                coefficients[frame * CoefficientCount + coefficient] = (float)value;
            }
        }

        return new VoiceSoundTemplate { FrameCount = frameCount, Features = coefficients };
    }

    internal static float Distance(VoiceSoundTemplate first, VoiceSoundTemplate second)
    {
        var rows = first.FrameCount;
        var columns = second.FrameCount;
        if (Math.Min(rows, columns) < 0.55f * Math.Max(rows, columns))
        {
            return float.PositiveInfinity;
        }

        Span<float> previous = stackalloc float[columns + 1];
        Span<float> current = stackalloc float[columns + 1];
        previous.Fill(float.PositiveInfinity);
        previous[0] = 0;
        var width = Math.Max(2, (int)Math.Ceiling(columns * 0.20));
        for (var row = 1; row <= rows; row++)
        {
            current.Fill(float.PositiveInfinity);
            var centre = row * columns / rows;
            for (var column = Math.Max(1, centre - width); column <= Math.Min(columns, centre + width); column++)
            {
                var cost = FrameDistance(first.Features, row - 1, second.Features, column - 1);
                var diagonal = previous[column - 1] + 2 * cost;
                var warped = Math.Min(previous[column], current[column - 1]) + cost + 0.10f;
                current[column] = Math.Min(diagonal, warped);
            }

            var completed = previous;
            previous = current;
            current = completed;
        }

        // Each step pays for the frames it uses, so every complete path has weight rows + columns.
        return previous[columns] / (rows + columns);
    }

    private static float FrameDistance(float[] first, int row, float[] second, int column)
    {
        var squared = 0f;
        for (var i = 0; i < CoefficientCount; i++)
        {
            var difference = first[row * CoefficientCount + i] - second[column * CoefficientCount + i];
            squared += difference * difference;
        }
        return MathF.Sqrt(squared / CoefficientCount);
    }

    private static void Transform(Span<Complex> values)
    {
        for (int i = 1, reverse = 0; i < FftSize; i++)
        {
            var bit = FftSize >> 1;
            while ((reverse & bit) != 0)
            {
                reverse ^= bit;
                bit >>= 1;
            }
            reverse ^= bit;
            if (i < reverse)
            {
                (values[i], values[reverse]) = (values[reverse], values[i]);
            }
        }

        for (var size = 2; size <= FftSize; size *= 2)
        {
            var half = size / 2;
            for (var start = 0; start < FftSize; start += size)
            {
                for (var i = 0; i < half; i++)
                {
                    var even = values[start + i];
                    var odd = values[start + i + half] * Roots[i * FftSize / size];
                    values[start + i] = even + odd;
                    values[start + i + half] = even - odd;
                }
            }
        }
    }
}
