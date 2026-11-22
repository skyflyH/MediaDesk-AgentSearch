using System.Collections.Concurrent;

namespace MediaLibrary.Service;

internal sealed record PropertyRange(string Field, double? Min = null, double? Max = null,
    bool MinExclusive = false, bool MaxExclusive = false);

public sealed partial class LibraryDatabase
{
    internal static readonly Dictionary<string, string> SearchPropertyNames = new()
    {
        ["exposureTimeSeconds"] = "System.Photo.ExposureTime", ["fNumber"] = "System.Photo.FNumber",
        ["iso"] = "System.Photo.ISOSpeed", ["focalLengthMm"] = "System.Photo.FocalLength",
        ["exposureBiasEv"] = "System.Photo.ExposureBias", ["width"] = "System.Image.HorizontalSize",
        ["height"] = "System.Image.VerticalSize", ["durationSeconds"] = "System.Media.Duration",
        ["frameRate"] = "System.Video.FrameRate", ["bitrate"] = "System.Video.TotalBitrate"
    };
    private static readonly ConcurrentDictionary<string, (long Length, long Modified, Dictionary<string, double> Values)> propertyCache = new();

    internal static string DescribeSearchProperty(string field, double value)
    {
        var number = value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        return field switch {
            "sizeBytes" => $"文件大小 {number} 字节",
            "exposureTimeSeconds" => $"曝光时间 {number} 秒", "fNumber" => $"光圈 f/{number}",
            "iso" => $"ISO {number}", "focalLengthMm" => $"焦距 {number} mm",
            "exposureBiasEv" => $"曝光补偿 {number} EV", "width" => $"宽度 {number} 像素",
            "height" => $"高度 {number} 像素", "durationSeconds" => $"时长 {number} 秒",
            "frameRate" => $"帧率 {number} 帧/秒", "bitrate" => $"码率 {number} 比特/秒",
            _ => $"{field}={number}"
        };
    }

    internal static Dictionary<string, double> ReadSearchProperties(string path, bool metadata = true, bool refresh = false)
    {
        try
        {
            var file = new FileInfo(path);
            var length = file.Length;
            var modified = file.LastWriteTimeUtc.Ticks;
            if (!metadata) return new() { ["sizeBytes"] = length };
            if (!refresh && propertyCache.TryGetValue(path, out var cached) && cached.Length == length && cached.Modified == modified)
                return cached.Values;
            var values = WindowsProperties.ReadSearchNumbers(path);
            foreach (var (key, value) in EmbeddedMetadata.ReadSearchNumbers(path)) values.TryAdd(key, value);
            values["sizeBytes"] = length;
            if (propertyCache.Count > 256) propertyCache.Clear();
            propertyCache[path] = (length, modified, values);
            return values;
        }
        catch (Exception error) when (IsFileError(error)) { return new(); }
    }
}
