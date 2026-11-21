using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaLibrary.Service;

public sealed record PreviewFile(string Path, long Size, string Mime, string Volume, string FileId,
    string CreatedNs, string WrittenNs);
public sealed record ProxyStatus(string State, string? Message = null);

internal sealed class MediaPreviews : IDisposable
{
    private readonly LibraryDatabase database;
    private readonly string? settingsPath;
    private string directory;

    public MediaPreviews(LibraryDatabase database, string directory, string? settingsPath = null)
    {
        this.database = database;
        this.directory = Path.GetFullPath(directory);
        this.settingsPath = settingsPath;
        try
        {
            if (settingsPath is not null && File.Exists(settingsPath))
            {
                var saved = File.ReadAllText(settingsPath).Trim();
                if (Path.IsPathFullyQualified(saved)) this.directory = Path.GetFullPath(saved);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    public string CacheDirectory => directory;

    public string SetCacheDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new RequestError("INVALID_ARGUMENT", "请选择有效的绝对路径。");
        lock (cacheLock)
        {
            if (jobs.Values.Any(job => !job.Work.IsCompleted))
                throw new RequestError("PREVIEW_BUSY", "正在生成视频预览，请完成后再修改缓存路径。");
            string? temporary = null;
            try
            {
                var next = Path.GetFullPath(path);
                if ((Directory.CreateDirectory(next).Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException();
                var probe = Path.Combine(next, $".apm-write-{Guid.NewGuid():N}.tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                if (settingsPath is not null)
                {
                    temporary = settingsPath + $".{Guid.NewGuid():N}.tmp";
                    File.WriteAllText(temporary, next);
                    File.Move(temporary, settingsPath, true);
                }
                directory = next;
                foreach (var job in jobs.Values) job.Cancel.Dispose();
                jobs.Clear();
                return directory;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new RequestError("CACHE_PATH_UNAVAILABLE", "无法使用或保存该缓存路径，请选择有写入权限的文件夹。");
            }
            finally
            {
                if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }
    }

    private const long CacheBudget = 2L * 1024 * 1024 * 1024;
    private readonly string? ffmpeg = FindFfmpeg();
    private readonly SemaphoreSlim encoder = new(1, 1);
    private readonly ConcurrentDictionary<string, PreviewJob> jobs = new();
    private readonly ConcurrentDictionary<string, byte> activeStages = new();
    private readonly object cacheLock = new();
    private static readonly Regex CacheFile = new(
        @"^[a-f0-9-]{36}_[A-F0-9]{32}\.(webm|jpg|thumb\.jpg)(\.[a-f0-9]{32}\.tmp)?$", RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp",
        [".gif"] = "image/gif", [".bmp"] = "image/bmp", [".mp4"] = "video/mp4", [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime", [".webm"] = "video/webm", [".mkv"] = "video/x-matroska", [".avi"] = "video/x-msvideo"
    };

    public bool FfmpegAvailable => ffmpeg is not null;

    public PreviewFile Resolve(string id, string kind)
    {
        var asset = database.Locate(id);
        _ = database.RevealPath(id);
        if (kind == "proxy" && asset.Kind == "video")
        {
            var file = CachePath(id, asset, ".webm");
            if (File.Exists(file)) return Describe(file, "video/webm");
            throw new RequestError("PREVIEW_UNAVAILABLE", "预览代理尚未生成。");
        }
        if (kind != (asset.Kind == "video" ? "video" : "image") && kind != "thumbnail")
            throw new RequestError("INVALID_ARGUMENT", "不支持此预览资源类型。");
        if (kind != "thumbnail" && MimeTypes.TryGetValue(asset.Extension, out var mime))
            return Describe(asset.Path, mime, asset.Identity);
        if (asset.Kind == "photo" || kind == "thumbnail") return RenderImage(id, asset, kind == "thumbnail");
        throw new RequestError("PREVIEW_UNAVAILABLE", "当前容器需要预览代理。");
    }

    public ProxyStatus RequestProxy(string id)
    {
        var asset = database.Locate(id);
        _ = database.RevealPath(id);
        if (asset.Kind != "video") throw new RequestError("INVALID_ARGUMENT", "预览代理仅适用于视频。");
        if (ffmpeg is null) return new("unavailable", "未找到 FFmpeg，仍可定位原视频。");
        var path = CachePath(id, asset, ".webm");
        if (File.Exists(path)) return new("complete");
        if (jobs.TryGetValue(id, out var current) && !current.Work.IsCompleted)
            return current.Status;
        current?.Cancel.Dispose();
        var job = new PreviewJob();
        jobs[id] = job;
        job.Work = Task.Run(() => Encode(job, asset, path));
        return job.Status;
    }

    public ProxyStatus ProxyStatus(string id)
    {
        if (jobs.TryGetValue(id, out var job)) return job.Status;
        return new("idle");
    }

    public void CancelProxy(string id)
    {
        if (jobs.TryGetValue(id, out var job) && !job.Work.IsCompleted)
        {
            job.Cancel.Cancel();
            job.Work.GetAwaiter().GetResult();
        }
    }

    public int ClearCache()
    {
        lock (cacheLock)
        {
            if (!Directory.Exists(directory)) return 0;
            CheckCacheDirectory();
            return CacheFiles().Count(DeleteCacheFile);
        }
    }

    private async Task Encode(PreviewJob job, LocatedAsset asset, string path)
    {
        var stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var cancellation = job.Cancel.Token;
        var entered = false;
        try
        {
            await encoder.WaitAsync(cancellation);
            entered = true;
            CheckCacheDirectory();
            using var source = WindowsPaths.OpenProtectedRead(asset.Path);
            var stamp = FileStamp.Read(source);
            if (stamp.Identity != asset.Identity || StampKey(asset, stamp) != Path.GetFileNameWithoutExtension(path).Split('_', 2)[1])
                throw new IOException();
            job.Status = new("running");
            activeStages.TryAdd(stage, 0);
            var options = new ProcessStartInfo(ffmpeg!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-i", asset.Path,
                "-map", "0:v:0", "-map", "0:a:0?", "-vf", "scale=w='min(1280,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease",
                "-c:v", "libvpx", "-pix_fmt", "yuv420p", "-deadline", "realtime", "-cpu-used", "5",
                "-c:a", "libopus", "-f", "webm", stage })
                options.ArgumentList.Add(argument);
            using var process = Process.Start(options) ?? throw new IOException();
            void Stop()
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            using var registration = cancellation.Register(Stop);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            // Enforce the cache limit even while an encoder writes its staging file.
            var exited = process.WaitForExitAsync();
            while (!exited.IsCompleted)
            {
                await Task.WhenAny(exited, Task.Delay(500, cancellation));
                if (cancellation.IsCancellationRequested || File.Exists(stage) && new FileInfo(stage).Length > CacheBudget)
                {
                    Stop();
                    break;
                }
            }
            await exited;
            await Task.WhenAll(stderr, stdout);
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(stage) || new FileInfo(stage).Length == 0 || FileStamp.Read(source) != stamp)
                throw new IOException();
            PublishCache(stage, path);
            job.Status = new("complete");
        }
        catch (OperationCanceledException) { job.Status = new("cancelled", "已取消预览代理。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or RequestError)
        {
            job.Status = new("failed", "无法生成 WebM 预览，仍可定位原视频。");
        }
        finally
        {
            CleanStage(stage);
            if (entered) encoder.Release();
        }
    }

    private PreviewFile RenderImage(string id, LocatedAsset asset, bool thumbnail)
    {
        var isRaw = RawPhoto.Supports(asset.Extension);
        var target = CachePath(id, asset, thumbnail ? ".thumb.jpg" : ".jpg");
        if (File.Exists(target)) return Describe(target, "image/jpeg");
        var stage = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            CheckCacheDirectory();
            using var file = File.Open(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (MediaFile.Read(file.SafeFileHandle) != asset.Identity) throw new IOException();
            var maximum = thumbnail ? 640 : 2048;
            BitmapSource frame = asset.Kind == "video" ? WindowsThumbnail.Read(asset.Path, maximum) :
                isRaw ? RawPhoto.Decode(asset.Path, maximum) :
                BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
            var scale = Math.Min(1, (double)maximum / Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource image = scale == 1 ? frame : new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            var writer = new JpegBitmapEncoder { QualityLevel = 88 };
            writer.Frames.Add(BitmapFrame.Create(image));
            activeStages.TryAdd(stage, 0);
            using (var output = File.Open(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) writer.Save(output);
            PublishCache(stage, target);
            return Describe(target, "image/jpeg");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            NotSupportedException or InvalidOperationException or ArgumentException or FormatException or
            System.Runtime.InteropServices.COMException)
        {
            throw new RequestError("PREVIEW_UNAVAILABLE", asset.Kind == "video" ? "系统未能生成视频缩略图。" :
                isRaw ? "无法预览此 RAW 照片，请检查文件或相机型号。" :
                "系统未能解码此照片，请检查 Windows 图像组件。");
        }
        finally { CleanStage(stage); }
    }

    private void PublishCache(string stage, string path)
    {
        lock (cacheLock)
        {
            if (File.Exists(path)) return;
            var size = new FileInfo(stage).Length;
            var cache = CacheFiles().OrderBy(file => file.LastWriteTimeUtc).ToArray();
            var total = size + cache.Sum(file => file.Length);
            foreach (var file in cache)
            {
                if (total <= CacheBudget) break;
                var length = file.Length;
                if (DeleteCacheFile(file)) total -= length;
            }
            if (total > CacheBudget) throw new IOException("Preview cache is full.");
            File.Move(stage, path);
        }
    }

    private IEnumerable<FileInfo> CacheFiles() => new DirectoryInfo(directory).EnumerateFiles()
        .Where(file => CacheFile.IsMatch(file.Name) && !activeStages.ContainsKey(file.FullName) &&
            (file.Attributes & FileAttributes.ReparsePoint) == 0);

    private void CheckCacheDirectory()
    {
        if ((Directory.CreateDirectory(directory).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new RequestError("PREVIEW_UNAVAILABLE", "预览缓存目录不可用。");
    }

    private static bool DeleteCacheFile(FileInfo file)
    {
        try { file.Delete(); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private void CleanStage(string stage)
    {
        if (!activeStages.TryRemove(stage, out _)) return;
        try { File.Delete(stage); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static PreviewFile Describe(string path, string mime, MediaFile? expected = null)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var stamp = FileStamp.Read(file);
        if (expected is not null && stamp.Identity != expected)
            throw new RequestError("SOURCE_UNAVAILABLE", "原件已被替换。");
        var name = WindowsPaths.FinalPath(file);
        if (!WindowsPaths.SamePath(name, path) && expected is null)
            throw new RequestError("PREVIEW_UNAVAILABLE", "预览缓存路径已变化。");
        var parts = stamp.Identity.Identity.Split(':', 2);
        return new(name, stamp.Length, mime, parts[0], parts[1], ToUnixNanoseconds(stamp.Identity.CreatedTicks),
            ToUnixNanoseconds(stamp.LastWriteTicks));
    }

    private static string ToUnixNanoseconds(long ticks) =>
        (((System.Numerics.BigInteger)ticks - DateTime.UnixEpoch.Ticks) * 100)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    private string CachePath(string id, LocatedAsset asset, string suffix) =>
        Path.Combine(directory, $"{id}_{StampKey(asset, FileStamp.Read(asset.Path))}{suffix}");

    private static string StampKey(LocatedAsset asset, FileStamp stamp) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{asset.Identity.Identity}:{asset.Identity.CreatedTicks}:{stamp.Length}:{stamp.LastWriteTicks}")))[..32];

    private static string? FindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("APM_FFMPEG");
        if (configured is not null && Path.IsPathFullyQualified(configured) && File.Exists(configured)) return configured;
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var folder = entry.Trim('"');
            if (!Path.IsPathFullyQualified(folder)) continue;
            var executable = Path.Combine(folder, "ffmpeg.exe");
            if (File.Exists(executable)) return executable;
        }
        return null;
    }

    public void Dispose()
    {
        var active = jobs.Values.Where(job => !job.Work.IsCompleted).ToArray();
        foreach (var job in active) job.Cancel.Cancel();
        Task.WhenAll(active.Select(job => job.Work)).GetAwaiter().GetResult();
        foreach (var job in jobs.Values) job.Cancel.Dispose();
        encoder.Dispose();
    }

    private sealed class PreviewJob
    {
        public readonly CancellationTokenSource Cancel = new();
        public volatile ProxyStatus Status = new("queued");
        public Task Work = Task.CompletedTask;
    }
}
