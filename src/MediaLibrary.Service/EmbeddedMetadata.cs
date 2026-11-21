using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MediaLibrary.Service;

internal static class EmbeddedMetadata
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private const string Description = "XMP-dc:Description";
    private const string Creator = "XMP-dc:Creator";
    public static bool Available => FindTool() is not null;

    internal static Dictionary<string, double> ReadSearchNumbers(string path)
    {
        var values = new Dictionary<string, double>();
        var tool = FindTool();
        if (tool is null) return values;
        var tags = new Dictionary<string, string> {
            ["ExposureTime"] = "exposureTimeSeconds", ["FNumber"] = "fNumber", ["ISO"] = "iso",
            ["FocalLength"] = "focalLengthMm", ["ExposureCompensation"] = "exposureBiasEv",
            ["ImageWidth"] = "width", ["ImageHeight"] = "height", ["Duration"] = "durationSeconds",
            ["VideoFrameRate"] = "frameRate", ["AvgBitrate"] = "bitrate"
        };
        try
        {
            using var json = JsonDocument.Parse(Run(tool, ["-j", "-n", .. tags.Keys.Select(tag => "-" + tag), path], 5000));
            foreach (var property in json.RootElement[0].EnumerateObject())
                if (tags.TryGetValue(property.Name, out var field) &&
                    double.TryParse(property.Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                    values[field] = number;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
            TimeoutException or JsonException or InvalidOperationException or IndexOutOfRangeException) { }
        return values;
    }

    internal static DateTime? ReadVideoCaptureTime(string path)
    {
        var tool = FindTool();
        if (tool is null) return null;
        try
        {
            using var result = JsonDocument.Parse(Run(tool,
                ["-j", "-G1", "-ExifIFD:DateTimeOriginal", "-ExifIFD:OffsetTimeOriginal", "-Keys:CreationDate", path], 5000));
            var item = result.RootElement[0];
            if (item.TryGetProperty("Keys:CreationDate", out var creation) && ParseCaptureTime(creation.GetString()) is { } capture)
                return capture;
            if (!item.TryGetProperty("ExifIFD:DateTimeOriginal", out var original)) return null;
            var offset = item.TryGetProperty("ExifIFD:OffsetTimeOriginal", out var zone) ? zone.GetString() : null;
            return ParseCaptureTime(original.GetString() + offset);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
            TimeoutException or JsonException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTime? ParseCaptureTime(string? value) =>
        DateTimeOffset.TryParseExact(value, ["yyyy:MM:dd HH:mm:ssK", "yyyy:MM:dd HH:mm:ss.FFFFFFFK"],
            CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var date)
            ? date.UtcDateTime : null;

    public static bool Supports(string path)
    {
        var extension = Path.GetExtension(path);
        return RawPhoto.Supports(extension) || extension.ToLowerInvariant() is
            ".jpg" or ".jpeg" or ".png" or ".mov" or ".mp4" or ".m4v";
    }

    public static bool TryWrite(string path, bool authors, string[] values, out string? failure, string? scratchRoot = null)
    {
        var tool = FindTool();
        if (tool is null)
        {
            failure = "未找到 ExifTool，无法写入此文件的元数据";
            return false;
        }

        DirectoryInfo? scratch = null;
        var runningTool = false;
        try
        {
            scratch = scratchRoot is null ? Directory.CreateTempSubdirectory("apm-metadata-") :
                Directory.CreateDirectory(Path.Combine(scratchRoot, $"apm-metadata-{Guid.NewGuid():N}"));
            var canonical = authors ? Creator : Description;
            var arguments = new List<string> { "-overwrite_original_in_place" };
            if (values.Length == 0)
                arguments.Add($"-{canonical}=");
            for (var index = 0; index < values.Length; index++)
                AddValue(canonical, index.ToString(), values[index]);

            var native = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" when !authors => new[] { "ExifIFD:UserComment", "XMP-exif:UserComment" },
                ".png" => [authors ? "PNG:Author" : "PNG:Comment"],
                ".mov" or ".mp4" or ".m4v" => authors ? ["Keys:Author"] : ["Keys:Comment", "ItemList:Comment"],
                _ => []
            };
            if (native.Contains("ExifIFD:UserComment"))
            {
                var comment = string.Join("; ", values);
                // Older ExifTool versions truncate supplementary characters in XPComment.
                AddValue("IFD0:XPComment", "xp-comment", comment.Any(char.IsSurrogate) ? "" : comment);
            }
            for (var index = 0; index < native.Length; index++)
                AddValue(native[index], $"native-{index}", string.Join("; ", values));

            arguments.Add(path);
            runningTool = true;
            Run(tool, arguments, 120_000);
            if (Matches(tool, path, authors, values))
            {
                failure = null;
                return true;
            }
            failure = "文件读回的元数据与草稿不一致";

            void AddValue(string tag, string name, string value)
            {
                if (value.Length == 0)
                {
                    arguments.Add($"-{tag}=");
                    return;
                }
                var input = Path.Combine(scratch.FullName, name + ".txt");
                File.WriteAllText(input, value, Utf8);
                arguments.Add($"-{tag}<={input}");
            }
        }
        catch (TimeoutException) { failure = "文件元数据写入或读回超时"; }
        catch (Win32Exception) { failure = "无法启动 ExifTool"; }
        catch (UnauthorizedAccessException)
        {
            failure = runningTool ? "无法访问 ExifTool 写入或读回的文件" : "无法创建 ExifTool 所需的临时输入文件";
        }
        catch (IOException error) { failure = error.Message; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or IndexOutOfRangeException)
        {
            failure = "无法读回文件中的元数据";
        }
        finally
        {
            try { scratch?.Delete(recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    public static bool Matches(string path, bool authors, string[] values)
    {
        var tool = FindTool();
        if (tool is null) return false;
        try { return Matches(tool, path, authors, values); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            Win32Exception or TimeoutException or JsonException or InvalidOperationException) { return false; }
    }

    private static bool Matches(string tool, string path, bool authors, string[] values)
    {
        var tag = authors ? Creator : Description;
        using var document = JsonDocument.Parse(Run(tool, ["-j", "-G1", $"-{tag}", path], 15_000));
        var item = document.RootElement[0];
        var saved = item.TryGetProperty(tag, out var property) ? ReadValues(property) : [];
        return saved.SequenceEqual(values.Select(NormalizeLineEndings));
    }

    private static string[] ReadValues(JsonElement element) => element.ValueKind == JsonValueKind.Array
        ? element.EnumerateArray().Select(ReadText).ToArray() : [ReadText(element)];

    private static string ReadText(JsonElement element) =>
        NormalizeLineEndings(element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText());

    private static string NormalizeLineEndings(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string? FindTool()
    {
        var configured = Environment.GetEnvironmentVariable("APM_EXIFTOOL");
        if (configured is not null && Path.IsPathFullyQualified(configured) && File.Exists(configured))
            return configured;
        var bundled = Path.Combine(AppContext.BaseDirectory, "exiftool.exe");
        if (File.Exists(bundled)) return bundled;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var folder = entry.Trim('"');
            if (!Path.IsPathFullyQualified(folder)) continue;
            var candidate = Path.Combine(folder, "exiftool.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "LRTimelapse 5", "exiftool.exe");
        return File.Exists(installed) ? installed : null;
    }

    private static string Run(string tool, List<string> arguments, int timeout)
    {
        var options = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
        };
        foreach (var argument in new[] { "-config", "", "-charset", "filename=UTF8", "-@", "-" })
            options.ArgumentList.Add(argument);
        options.Environment["LC_ALL"] = "C";
        using var process = Process.Start(options) ?? throw new IOException("无法启动 ExifTool");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(string.Join("\n", arguments) + "\n");
        process.StandardInput.Close();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException();
        }
        if (process.ExitCode != 0)
        {
            var detail = errors.GetAwaiter().GetResult();
            throw new IOException(detail.Contains("Error opening", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("Error creating", StringComparison.OrdinalIgnoreCase)
                ? "ExifTool 无法打开原件或创建临时写入文件" : "ExifTool 拒绝写入此文件的元数据");
        }
        return output.GetAwaiter().GetResult();
    }
}
