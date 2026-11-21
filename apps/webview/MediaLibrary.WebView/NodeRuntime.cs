using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MediaLibrary.WebView;

internal static class NodeRuntime
{
    internal static string Resolve(string root, string? configured, string? searchPath, Func<string, bool> probe)
    {
        if (!string.IsNullOrWhiteSpace(configured)) {
            if (Path.IsPathFullyQualified(configured) && File.Exists(configured) && probe(configured)) return configured;
            throw new InvalidOperationException("APM_WEBVIEW_NODE 指定的 Node.js 无法运行，或版本低于 22。请检查配置路径。");
        }
        var bundled = Path.GetFullPath(Path.Combine(root, "node.exe"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bundled };
        // An installed runtime is already executable in this environment. Avoid
        // repeatedly starting a copied binary when execution from its directory fails.
        foreach (var directory in (searchPath ?? "").Split(Path.PathSeparator)) {
            string candidate;
            try {
                var path = directory.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(path)) continue;
                candidate = Path.GetFullPath(Path.Combine(path, "node.exe"));
            } catch { continue; }
            if (seen.Add(candidate) && File.Exists(candidate) && probe(candidate)) return candidate;
        }
        if (File.Exists(bundled) && probe(bundled)) return bundled;
        throw new InvalidOperationException("无法启动 Node.js 运行环境。请重新解压应用到可执行的目录，或安装 Node.js 22 及以上版本后重试。详情见 webview-host.log。");
    }

    internal static bool CanRun(string path, Action<string> log)
    {
        try {
            // CreateNoWindow only hides consoles. Windows loader error dialogs
            // require an inherited process error mode on the probe itself.
            var previous = SetErrorMode(GetErrorMode() | 0x0001u | 0x0002u | 0x8000u);
            Process? process;
            try {
                process = Process.Start(new ProcessStartInfo(path, "--version") {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                });
            } finally { SetErrorMode(previous); }
            using var probe = process;
            if (probe is null) return false;
            var output = probe.StandardOutput.ReadToEndAsync();
            var errors = probe.StandardError.ReadToEndAsync();
            if (!probe.WaitForExit(3000)) {
                try { probe.Kill(true); probe.WaitForExit(1000); } catch { }
                log($"Node probe timed out: {path}"); return false;
            }
            var version = output.GetAwaiter().GetResult().Trim();
            _ = errors.GetAwaiter().GetResult();
            var usable = probe.ExitCode == 0 && version.StartsWith('v') && Version.TryParse(version[1..], out var parsed) && parsed.Major >= 22;
            log($"Node probe: {path}; exit=0x{unchecked((uint)probe.ExitCode):X8}; version={version}; usable={usable}");
            return usable;
        } catch (Exception error) {
            log($"Node probe failed: {path}; {error.Message}"); return false;
        }
    }

    [DllImport("kernel32.dll")] private static extern uint GetErrorMode();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
}
