using Sdcb.LibRaw;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaLibrary.Service;

internal static class RawPhoto
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".raf", ".rw2", ".orf", ".pef", ".dng"
    };

    public static bool Supports(string extension) => Extensions.Contains(extension);

    public static BitmapSource Decode(string path, int maximum)
    {
        try
        {
            using var raw = RawContext.OpenFile(path);
            var preview = EmbeddedPreview(raw);
            if (preview is not null && Math.Max(preview.PixelWidth, preview.PixelHeight) >= Math.Min(maximum, 1024))
                return preview;

            // Bound the expensive demosaic when the file provides only a small thumbnail.
            if ((long)raw.RawWidth * raw.RawHeight > 160_000_000)
                return preview ?? throw new InvalidDataException("RAW dimensions exceed the preview budget.");

            try
            {
                using var image = raw.ExportRawImage(options =>
                {
                    options.HalfSize = true;
                    options.UseCameraWb = true;
                    options.OutputBps = 8;
                });
                return ToRgbBitmap(image);
            }
            catch (Exception error) when (preview is not null && error is LibRawException or InvalidDataException)
            {
                return preview;
            }
        }
        catch (Exception error) when (error is LibRawException or DllNotFoundException or
            BadImageFormatException or EntryPointNotFoundException or TypeInitializationException)
        {
            throw new InvalidDataException("The RAW decoder could not open this file.", error);
        }
    }

    private static BitmapSource? EmbeddedPreview(RawContext raw)
    {
        try
        {
            using var image = raw.ExportThumbnail(thumbnailIndex: 0);
            if (image.ImageType == ProcessedImageType.Bitmap) return ToRgbBitmap(image);
            if (image.ImageType != ProcessedImageType.Jpeg) return null;

            using var jpeg = new MemoryStream(image.AsSpan<byte>().ToArray(), writable: false);
            var frame = BitmapDecoder.Create(jpeg, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var orientation = (frame.Metadata as BitmapMetadata)?.GetQuery("/app1/ifd/{ushort=274}");
            return orientation switch
            {
                (ushort)2 => new TransformedBitmap(frame, new ScaleTransform(-1, 1)),
                (ushort)3 => new TransformedBitmap(frame, new RotateTransform(180)),
                (ushort)4 => new TransformedBitmap(frame, new ScaleTransform(1, -1)),
                (ushort)5 => new TransformedBitmap(frame, new TransformGroup
                    { Children = [new ScaleTransform(-1, 1), new RotateTransform(270)] }),
                (ushort)6 => new TransformedBitmap(frame, new RotateTransform(90)),
                (ushort)7 => new TransformedBitmap(frame, new TransformGroup
                    { Children = [new ScaleTransform(-1, 1), new RotateTransform(90)] }),
                (ushort)8 => new TransformedBitmap(frame, new RotateTransform(270)),
                _ => frame
            };
        }
        catch (Exception error) when (error is LibRawException or IOException or InvalidDataException or NotSupportedException or
            ArgumentException or InvalidOperationException or FormatException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static BitmapSource ToRgbBitmap(ProcessedImage image)
    {
        if (image.Channels != 3 || image.Bits != 8 || image.Width == 0 || image.Height == 0 ||
            image.DataSize != (long)image.Width * image.Height * 3)
            throw new InvalidDataException("Unsupported RAW preview pixel layout.");

        return BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Rgb24, null,
            image.AsSpan<byte>().ToArray(), image.Width * 3);
    }
}
