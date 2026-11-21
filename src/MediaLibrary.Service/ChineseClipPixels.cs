using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaLibrary.Service;

internal static class ChineseClipPixels
{
    internal const int Size = 224;
    internal static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    internal static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    public static float[] Decode(Stream compressed)
    {
        // The reference Pillow pipeline uses decoded RGB values without applying embedded ICC profiles.
        var options = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
        var decoder = BitmapDecoder.Create(compressed, options, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 60_000_000)
            throw new InvalidDataException("Photo exceeds the image decoder budget.");

        var rgb = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);
        var pixels = new byte[width * height * 3];
        rgb.CopyPixels(pixels, width * 3, 0);
        var resized = Resize(pixels, width, height);
        var tensor = new float[resized.Length];
        for (var channel = 0; channel < 3; channel++)
        for (var offset = 0; offset < Size * Size; offset++)
            tensor[channel * Size * Size + offset] = (resized[offset * 3 + channel] / 255f - Mean[channel]) / Std[channel];
        return tensor;
    }

    private static byte[] Resize(byte[] pixels, int width, int height)
    {
        // Match Pillow's bicubic kernel and two-pass rounding. We keep one RGB
        // row for the horizontal pass, then apply the vertical coefficients.
        var horizontal = Weights(width);
        var vertical = Weights(height);
        var rowStride = Size * 3;
        var intermediate = new byte[height * rowStride];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < Size; x++)
        for (var channel = 0; channel < 3; channel++)
        {
            var filter = horizontal[x];
            var start = (y * width + filter.Start) * 3 + channel;
            var sum = 1 << 21;
            for (var sample = 0; sample < filter.Values.Length; sample++)
                sum += pixels[start + sample * 3] * filter.Values[sample];
            intermediate[y * rowStride + x * 3 + channel] = (byte)Math.Clamp(sum >> 22, 0, 255);
        }

        var output = new byte[Size * rowStride];
        for (var y = 0; y < Size; y++)
        {
            var filter = vertical[y];
            for (var x = 0; x < rowStride; x++)
            {
                var sum = 1 << 21;
                for (var sample = 0; sample < filter.Values.Length; sample++)
                    sum += intermediate[(filter.Start + sample) * rowStride + x] * filter.Values[sample];
                output[y * rowStride + x] = (byte)Math.Clamp(sum >> 22, 0, 255);
            }
        }
        return output;
    }

    private static (int Start, int[] Values)[] Weights(int length)
    {
        var scale = (double)length / Size;
        var filterScale = Math.Max(1, scale);
        var support = 2 * filterScale;
        var weights = new (int, int[])[Size];
        for (var index = 0; index < Size; index++)
        {
            var centre = (index + 0.5) * scale;
            var first = Math.Max(0, (int)(centre - support + 0.5));
            var last = Math.Min(length, (int)(centre + support + 0.5));
            var samples = Enumerable.Range(first, last - first)
                .Select(pixel => Cubic(Math.Abs((pixel + 0.5 - centre) / filterScale))).ToArray();
            var sum = samples.Sum();
            weights[index] = (first, samples.Select(value => (int)Math.Round(value / sum * (1 << 22),
                MidpointRounding.AwayFromZero)).ToArray());
        }
        return weights;
    }

    private static double Cubic(double value) => value switch
    {
        < 1 => ((1.5 * value - 2.5) * value) * value + 1,
        < 2 => ((-0.5 * value + 2.5) * value - 4) * value + 2,
        _ => 0
    };
}
