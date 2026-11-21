using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace MediaLibrary.WebView;

internal static class Program
{
    internal static Icon LoadIcon()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("MediaDesk.Icon")
            ?? throw new InvalidOperationException("MediaDesk icon resource is missing.");
        return new Icon(stream);
    }

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "Local\\MediaLibrary.WebView2", out var first);
        if (!first) { NativeMethods.PostMessage((nint)NativeMethods.HWND_BROADCAST, NativeMethods.ShowMessage, 0, 0); return; }
        try {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Application.Run(new Host());
        } catch (WebView2RuntimeNotFoundException) {
            MessageBox.Show("请先安装 Microsoft Edge WebView2 Runtime，然后重新打开 MediaDesk。", "缺少运行环境", MessageBoxButtons.OK, MessageBoxIcon.Information);
        } catch (Exception error) {
            MessageBox.Show(error.Message, "MediaDesk 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class Host : Form
{
    private readonly string root = AppContext.BaseDirectory;
    private readonly string profile = Environment.GetEnvironmentVariable("APM_WEBVIEW_PROFILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ai-photos-manager");
    private readonly string sync;
    private readonly Dictionary<int, Window> windows = [];
    private readonly Dictionary<string, int> hotkeys = [];
    private readonly Dictionary<int, string> hotkeyNames = [];
    private int nextHotkey = 10;
    private readonly Process bridge;
    private NotifyIcon? tray;
    private bool exiting;
    private string StartupCommand => $"\"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")}\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{Path.Combine(root, "Start-MediaDesk.ps1")}\"";
    private CoreWebView2Environment? environment;
    private readonly System.Windows.Forms.Timer diagnostics = new() { Interval = 250 };
    public Host()
    {
        ShowInTaskbar = false; Opacity = 0; FormBorderStyle = FormBorderStyle.FixedToolWindow; Size = new Size(1, 1);
        Directory.CreateDirectory(profile);
        sync = Path.Combine(profile, "webview-ipc", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(sync);
        bridge = new Process { StartInfo = new ProcessStartInfo(NodePath()) {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) } };
        bridge.StartInfo.ArgumentList.Add(Path.Combine(root, "main", "index.cjs"));
        bridge.StartInfo.Environment["APM_WEBVIEW_ROOT"] = root.TrimEnd(Path.DirectorySeparatorChar);
        bridge.StartInfo.Environment["APM_WEBVIEW_PROFILE"] = profile;
        bridge.StartInfo.Environment["APM_WEBVIEW_SYNC"] = sync;
        bridge.OutputDataReceived += (_, args) => {
            if (args.Data is not { } line || !line.StartsWith('{')) return;
            try { var message = JsonDocument.Parse(line).RootElement.Clone(); BeginInvoke(() => HandleNative(message)); }
            catch { }
        };
        bridge.ErrorDataReceived += (_, args) => { if (args.Data is { } line) Log(line); };
        bridge.EnableRaisingEvents = true;
        bridge.Exited += (_, _) => { if (!exiting && IsHandleCreated) BeginInvoke(() => { MessageBox.Show("应用后台已退出，请重新启动。", "MediaDesk"); Exit(); }); };
        if (Environment.GetEnvironmentVariable("APM_WEBVIEW_DIAGNOSTICS") == "1") {
            diagnostics.Tick += async (_, _) => {
                var file = Path.Combine(profile, "diagnostic-command.json");
                if (!File.Exists(file)) return;
                diagnostics.Stop();
                try {
                    var command = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone(); File.Delete(file);
                    var result = await Invoke(command.GetProperty("method").GetString()!, command.GetProperty("args"));
                    File.WriteAllText(Path.Combine(profile, "diagnostic-result.json"), JsonSerializer.Serialize(new { result }));
                } catch (Exception error) { File.WriteAllText(Path.Combine(profile, "diagnostic-result.json"), JsonSerializer.Serialize(new { error = error.ToString() })); }
                finally { diagnostics.Start(); }
            };
            diagnostics.Start();
        }
        Shown += (_, _) => { Hide(); bridge.Start(); bridge.BeginOutputReadLine(); bridge.BeginErrorReadLine(); };
    }
    private string NodePath()
    {
        var path = NodeRuntime.Resolve(root, Environment.GetEnvironmentVariable("APM_WEBVIEW_NODE"),
            Environment.GetEnvironmentVariable("PATH"), candidate => NodeRuntime.CanRun(candidate, Log));
        Log("Using Node runtime: " + path);
        return path;
    }
    private void Log(string text) { try { File.AppendAllText(Path.Combine(profile, "webview-host.log"), DateTime.Now + " " + text + Environment.NewLine); } catch { } }
    internal void Send(object value) { try { bridge.StandardInput.WriteLine(JsonSerializer.Serialize(value)); bridge.StandardInput.Flush(); } catch { } }
    private async void HandleNative(JsonElement message)
    {
        var method = message.GetProperty("native").GetString()!;
        if (Environment.GetEnvironmentVariable("APM_WEBVIEW_DIAGNOSTICS") == "1") Log("native " + method);
        var args = message.GetProperty("args");
        object? result = null; string? error = null;
        try { result = await Invoke(method, args); }
        catch (Exception failure) { error = failure.Message; Log(method + ": " + failure); }
        var response = new { result, error };
        if (message.TryGetProperty("syncId", out var syncId)) {
            var id = syncId.GetString()!;
            if (!Guid.TryParse(id, out _)) return;
            var path = Path.Combine(sync, id + ".json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(response), new UTF8Encoding(false)); File.Move(path + ".tmp", path);
        } else Send(new { reply = message.GetProperty("id").GetString(), result, error });
    }
    private Window Get(JsonElement args) => windows[args[0].GetInt32()];
    private async Task<object?> Invoke(string method, JsonElement args)
    {
        switch (method) {
            case "createWindow": {
                var id = args[0].GetInt32(); var options = args[1];
                var popup = options.TryGetProperty("frame", out var frame) && !frame.GetBoolean();
                var window = new Window(this, id, popup) { Text = options.GetProperty("title").GetString(),
                    ClientSize = new Size(options.GetProperty("width").GetInt32(), options.GetProperty("height").GetInt32()) };
                if (!popup) window.MinimumSize = new Size(360, 440);
                if (options.TryGetProperty("x", out var x)) { window.StartPosition = FormStartPosition.Manual; window.Location = new Point(x.GetInt32(), options.GetProperty("y").GetInt32()); }
                else window.StartPosition = FormStartPosition.CenterScreen;
                windows[id] = window;
                window.CreateControl();
                if (!options.TryGetProperty("show", out var show) || show.GetBoolean()) window.Show();
                return null;
            }
            case "load": {
                var window = Get(args); var url = args[1].GetString()!;
                Log("Initializing WebView2 environment");
                environment ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(profile, "WebView2"),
                    new CoreWebView2EnvironmentOptions(Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS")));
                Log("Creating WebView2 controller");
                await window.View.EnsureCoreWebView2Async(environment);
                Log($"WebView2 controller ready: core={window.View.CoreWebView2 is not null}, visible={window.Visible}, handle={window.View.IsHandleCreated}");
                window.View.CoreWebView2!.ProcessFailed += (_, e) => Log("WebView2 process failed: " + e.ProcessFailedKind);
                window.Origin = new Uri(url).GetLeftPart(UriPartial.Authority);
                window.AllowedPage = url;
                await window.View.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    $"window.__mediaDeskCaption = {window.Caption.ToString().ToLowerInvariant()};\n" +
                    File.ReadAllText(Path.Combine(root, "preload", "index.js")));
                Log("WebView2 preload registered");
                window.View.CoreWebView2.Settings.AreDevToolsEnabled = true;
                window.View.CoreWebView2.Settings.IsStatusBarEnabled = false;
                window.View.CoreWebView2.NavigationCompleted += (_, e) => Log("WebView2 navigation: " + e.IsSuccess + " " + e.WebErrorStatus);
                window.View.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
                window.View.CoreWebView2.NavigationStarting += (_, e) => { if (e.Uri != window.AllowedPage) e.Cancel = true; };
                window.View.CoreWebView2.WebMessageReceived += (_, e) => window.Message(e);
                window.View.CoreWebView2.Navigate(url);
                return null;
            }
            case "event": {
                var window = Get(args);
                window.Post(new { channel = args[1].GetString(), args = args[2] }); return null;
            }
            case "reply": Get(args).Post(args[1]); return null;
            case "script": return JsonSerializer.Deserialize<JsonElement>(await Get(args).View.ExecuteScriptAsync(args[1].GetString()));
            case "inspect": return windows.Values.Select(w => new { w.Id, w.Visible, w.IsHandleCreated, core = w.View.CoreWebView2 is not null, w.AllowedPage,
                w.Caption, state = w.WindowState.ToString(), bounds = new { w.Left, w.Top, w.Width, w.Height }, client = new { w.ClientSize.Width, w.ClientSize.Height }, w.FullScreen }).ToArray();
            case "capture" when Environment.GetEnvironmentVariable("APM_WEBVIEW_DIAGNOSTICS") == "1": {
                var path = Path.Combine(profile, "diagnostic-preview.png");
                using var stream = File.Create(path);
                await Get(args).View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                return path;
            }
            case "testHotkey" when Environment.GetEnvironmentVariable("APM_WEBVIEW_DIAGNOSTICS") == "1":
                NativeMethods.PostMessage(Handle, 0x0312, hotkeys[args[0].GetString()!], 0); return null;
            case "show": { var w = Get(args); w.Show(); w.Activate(); w.View.Focus(); return null; }
            case "hide": Get(args).Hide(); return null;
            case "focus": Get(args).Activate(); Get(args).View.Focus(); return null;
            case "destroy": { var w = Get(args); windows.Remove(w.Id); w.Dispose(); return null; }
            case "minimized": return Get(args).WindowState == FormWindowState.Minimized;
            case "restore": Get(args).WindowState = FormWindowState.Normal; return null;
            case "bounds": {
                var bounds = args[1]; var w = Get(args);
                w.Location = new Point(bounds.GetProperty("x").GetInt32(), bounds.GetProperty("y").GetInt32());
                w.ClientSize = new Size(bounds.GetProperty("width").GetInt32(), bounds.GetProperty("height").GetInt32()); return null;
            }
            case "theme": {
                var dark = args[1].GetString() == "dark" ? 1 : 0; NativeMethods.DwmSetWindowAttribute(Get(args).Handle, 20, ref dark, 4); return null;
            }
            case "fullscreen": {
                var w = Get(args); var full = args[1].GetBoolean();
                w.SetFullScreen(full); return null;
            }
            case "reload": Get(args).View.CoreWebView2.Reload(); return null;
            case "devtools": Get(args).View.CoreWebView2.OpenDevToolsWindow(); return null;
            case "pastePlain": {
                var text = JsonSerializer.Serialize(Clipboard.ContainsText() ? Clipboard.GetText() : "");
                await Get(args).View.ExecuteScriptAsync($"document.execCommand('insertText',false,{text})"); return null;
            }
            case "registerHotkey": return Register(args[0].GetString()!);
            case "unregisterHotkey": { var key = args[0].GetString()!; if (hotkeys.Remove(key, out var id)) { NativeMethods.UnregisterHotKey(Handle, id); hotkeyNames.Remove(id); } return null; }
            case "getLoginStartup": {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                var command = key?.GetValue("ai-photos-manager") as string;
                using var approved = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
                var state = approved?.GetValue("ai-photos-manager") as byte[];
                var enabled = command == StartupCommand && (state is null || state.Length == 0 || state[0] != 3);
                return new { openAtLogin = enabled, executableWillLaunchAtLogin = enabled };
            }
            case "setLoginStartup": {
                var enabled = args[0].GetBoolean();
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (enabled) key.SetValue("ai-photos-manager", StartupCommand); else key.DeleteValue("ai-photos-manager", false);
                using var approved = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
                approved.DeleteValue("ai-photos-manager", false); return null;
            }
            case "encrypt": return Convert.ToBase64String([.. Encoding.ASCII.GetBytes("v10"), .. ProtectedData.Protect(Encoding.UTF8.GetBytes(args[0].GetString()!), null, DataProtectionScope.CurrentUser)]);
            case "decrypt": {
                var data = Convert.FromBase64String(args[0].GetString()!);
                // Electron safeStorage uses the DPAPI payload after its v10 header on Windows.
                if (data.Length > 3 && Encoding.ASCII.GetString(data, 0, 3) == "v10") data = data[3..];
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));
            }
            case "dialog": return ShowDialog(Get(args), args[1]);
            case "openPath": {
                try {
                    var path = args[0].GetString()!;
                    if (Directory.Exists(path)) await ExplorerLauncher.OpenAsync(path, false, Log);
                    else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return "";
                } catch (Exception e) { Log("Opening path failed: " + e); return e.Message; }
            }
            case "reveal": {
                await ExplorerLauncher.OpenAsync(args[0].GetString()!, true, Log); return null;
            }
            case "tray": {
                tray = new NotifyIcon { Icon = Program.LoadIcon(), Text = "MediaDesk", Visible = true };
                var menu = new ContextMenuStrip();
                foreach (var entry in new[] { ("打开", 0), ("快捷搜索", 1), ("退出", 2) }) { var index = entry.Item2; menu.Items.Add(entry.Item1, null, (_, _) => Send(new { tray = index })); }
                tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Send(new { tray = 0 }); return null;
            }
            case "destroyTray": tray?.Dispose(); return null;
            case "cursor": return new { x = Cursor.Position.X, y = Cursor.Position.Y };
            case "display": {
                var point = args[0]; var screen = Screen.FromPoint(new Point(point.GetProperty("x").GetInt32(), point.GetProperty("y").GetInt32()));
                var bounds = screen.WorkingArea; return new { workArea = new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height } };
            }
            case "exit": Exit(); return null;
            default: throw new InvalidOperationException("Unsupported native operation: " + method);
        }
    }
    private object ShowDialog(Window owner, JsonElement options)
    {
        var properties = options.GetProperty("properties").EnumerateArray().Select(p => p.GetString()).ToArray();
        var title = options.TryGetProperty("title", out var t) ? t.GetString() : "选择文件";
        owner.Picking = true;
        try {
            if (properties.Contains("openDirectory")) {
                using var dialog = new FolderBrowserDialog { Description = title ?? "选择目录", UseDescriptionForTitle = true,
                    Multiselect = properties.Contains("multiSelections"), ShowNewFolderButton = true };
                if (options.TryGetProperty("defaultPath", out var initial)) dialog.InitialDirectory = initial.GetString() ?? "";
                var ok = dialog.ShowDialog(owner) == DialogResult.OK;
                return new { canceled = !ok, filePaths = ok ? dialog.SelectedPaths : [] };
            }
            using var files = new OpenFileDialog { Title = title ?? "选择文件", Multiselect = properties.Contains("multiSelections"), CheckFileExists = true };
            var accepted = files.ShowDialog(owner) == DialogResult.OK;
            return new { canceled = !accepted, filePaths = accepted ? files.FileNames : [] };
        } finally { owner.Picking = false; }
    }
    private bool Register(string accelerator)
    {
        if (hotkeys.ContainsKey(accelerator)) return true;
        var parts = accelerator.Split('+'); uint modifiers = 0x4000;
        foreach (var part in parts[..^1]) modifiers |= part switch { "Control" => 2u, "Alt" => 1u, "Shift" => 4u, "Super" => 8u, _ => 0 };
        var token = parts[^1];
        var key = token == "Space" ? Keys.Space : token.Length == 1 && char.IsDigit(token[0])
            ? Keys.D0 + (token[0] - '0') : Enum.Parse<Keys>(token, true);
        var id = ++nextHotkey;
        if (!NativeMethods.RegisterHotKey(Handle, id, modifiers, (uint)key)) return false;
        hotkeys[accelerator] = id; hotkeyNames[id] = accelerator; return true;
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && hotkeyNames.TryGetValue(message.WParam.ToInt32(), out var key)) Send(new { hotkey = key });
        if (message.Msg == NativeMethods.ShowMessage) Send(new { secondInstance = true });
        base.WndProc(ref message);
    }
    private void Exit()
    {
        exiting = true;
        foreach (var id in hotkeyNames.Keys) NativeMethods.UnregisterHotKey(Handle, id);
        tray?.Dispose(); foreach (var window in windows.Values) window.Dispose();
        try { bridge.StandardInput.Close(); } catch { }
        Application.Exit();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { exiting = true; diagnostics.Dispose(); tray?.Dispose(); try { if (!bridge.HasExited) bridge.Kill(true); } catch { } bridge.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class Window : Form
{
    internal readonly WebView2 View = new() { Dock = DockStyle.Fill };
    internal readonly int Id;
    internal string Origin = "", AllowedPage = "";
    internal bool Picking;
    internal bool Caption { get; }
    internal bool FullScreen { get; private set; }
    private Rectangle restoredBounds;
    private FormWindowState restoredState;
    private readonly Host host;
    private readonly Queue<string> messages = [];
    private bool ready;
    public Window(Host host, int id, bool popup)
    {
        Icon = Program.LoadIcon();
        this.host = host; Id = id; Controls.Add(View);
        Caption = !popup;
        if (popup) { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; }
        Resize += (_, _) => CaptionState();
        Activated += (_, _) => host.Send(new { window = Id, focus = true });
        Deactivate += (_, _) => { if (!Picking) host.Send(new { window = Id, focus = false }); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; host.Send(new { window = Id, close = true }); } };
        View.NavigationCompleted += (_, _) => { ready = true; while (messages.TryDequeue(out var message)) View.CoreWebView2.PostWebMessageAsJson(message); CaptionState(); };
    }
    private void CaptionState()
    {
        if (ready && Caption) Post(new { captionState = true, maximized = WindowState == FormWindowState.Maximized, fullscreen = FullScreen });
    }
    internal void SetFullScreen(bool full)
    {
        if (FullScreen == full) return;
        if (full) {
            restoredBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            restoredState = WindowState;
            FullScreen = true; WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None; Bounds = Screen.FromControl(this).Bounds;
        } else {
            FullScreen = false; FormBorderStyle = Caption ? FormBorderStyle.Sizable : FormBorderStyle.None;
            Bounds = restoredBounds; WindowState = restoredState;
        }
        CaptionState();
    }
    protected override void WndProc(ref Message message)
    {
        // Keep the standard resizable window styles, system menu and Aero Snap,
        // but let the web content occupy the native caption area as well.
        if (Caption && message.Msg == 0x83) { message.Result = 0; return; }
        if (Caption && !FullScreen && message.Msg == 0x24) {
            base.WndProc(ref message);
            var info = Marshal.PtrToStructure<NativeMethods.MinMaxInfo>(message.LParam);
            var screen = Screen.FromHandle(Handle);
            info.MaxPosition = new Point(screen.WorkingArea.Left - screen.Bounds.Left, screen.WorkingArea.Top - screen.Bounds.Top);
            info.MaxSize = screen.WorkingArea.Size;
            Marshal.StructureToPtr(info, message.LParam, false);
            return;
        }
        base.WndProc(ref message);
    }
    internal void Post(object message)
    {
        var json = JsonSerializer.Serialize(message);
        if (!ready) messages.Enqueue(json); else View.CoreWebView2.PostWebMessageAsJson(json);
    }
    internal void Message(CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (args.Source != AllowedPage) return;
        try {
            var message = JsonDocument.Parse(args.WebMessageAsJson).RootElement;
            if (message.TryGetProperty("nativeDrop", out _)) {
                var additional = args.GetType().GetProperty("AdditionalObjects")?.GetValue(args) as System.Collections.IEnumerable;
                if (additional is null) return;
                var paths = new List<string>();
                foreach (var file in additional) if (file.GetType().GetProperty("Path")?.GetValue(file) is string path) paths.Add(path);
                if (paths.Count > 0) host.Send(new { window = Id, drop = paths });
            } else if (message.TryGetProperty("nativeCaption", out var action) && Caption) {
                switch (action.GetString()) {
                    case "minimize": WindowState = FormWindowState.Minimized; break;
                    case "maximize" when !FullScreen:
                        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
                    case "close": Close(); break;
                }
            } else if (message.TryGetProperty("nativeResize", out var edge) && Caption && !FullScreen && WindowState == FormWindowState.Normal) {
                if (edge.TryGetInt32(out var hit) && hit is >= 10 and <= 17) {
                    NativeMethods.ReleaseCapture(); NativeMethods.PostMessage(Handle, 0xA1, hit, NativeMethods.CursorPosition());
                }
            } else if (message.TryGetProperty("nativeDrag", out _) && !FullScreen) {
                NativeMethods.ReleaseCapture(); NativeMethods.PostMessage(Handle, 0xA1, 2, NativeMethods.CursorPosition());
            } else if (message.TryGetProperty("channel", out var channel) && channel.ValueKind == JsonValueKind.String) {
                var fields = new Dictionary<string, object> { ["window"] = Id, ["channel"] = channel.GetString()!, ["args"] = message.GetProperty("args").Clone() };
                if (message.TryGetProperty("id", out var id) && id.TryGetInt32(out var requestId)) fields["id"] = requestId;
                host.Send(fields);
            } else if (message.TryGetProperty("input", out var input)) {
                host.Send(new { window = Id, input = input.Clone() });
            }
        } catch { }
    }
}

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo { internal Point Reserved; internal Size MaxSize; internal Point MaxPosition; internal Size MinTrackSize; internal Size MaxTrackSize; }
    internal static nint CursorPosition() { var p = Cursor.Position; return (nint)((p.Y << 16) | (p.X & 0xffff)); }
    internal const int HWND_BROADCAST = 0xffff;
    internal static readonly int ShowMessage = RegisterWindowMessage("MediaLibrary.WebView2.Show");
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint window, int message, nint w, nint l);
    [DllImport("user32.dll")] internal static extern nint SendMessage(nint window, int message, nint w, nint l);
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
