using MediaLibrary.WebView;
using Xunit;

namespace MediaLibrary.Service.Tests;

public sealed class NodeRuntimeTests : IDisposable
{
    private readonly TestDirectory directory = new();
    private string Runtime(string folder) {
        var path = Path.Combine(directory.FullName, folder, "node.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test fixture, never executed"); return path;
    }

    [Fact]
    public void InstalledRuntimeIsUsedWithoutStartingTheProblematicBundledBinary() {
        var bundled = Runtime("app"); var installed = Runtime("system"); var probes = new List<string>();
        var selected = NodeRuntime.Resolve(Path.GetDirectoryName(bundled)!, null, Path.GetDirectoryName(installed), path => { probes.Add(path); return true; });
        Assert.Equal(installed, selected); Assert.Equal([installed], probes);
    }
    [Fact]
    public void UnusableInstalledRuntimeFallsBackToTheBundledRuntime() {
        var bundled = Runtime("app"); var installed = Runtime("old-version"); var probes = new List<string>();
        var selected = NodeRuntime.Resolve(Path.GetDirectoryName(bundled)!, null, Path.GetDirectoryName(installed), path => { probes.Add(path); return path == bundled; });
        Assert.Equal(bundled, selected); Assert.Equal([installed, bundled], probes);
    }
    [Fact]
    public void BundleWorksWithoutAnySystemNodeInstallation() {
        var bundled = Runtime("app");
        Assert.Equal(bundled, NodeRuntime.Resolve(Path.GetDirectoryName(bundled)!, null, null, _ => true));
    }
    [Fact]
    public void DuplicatePathEntriesAndBundleOnPathAreNotRepeatedlyProbed() {
        var bundled = Runtime("app"); var installed = Runtime("system"); var probes = new List<string>();
        var search = string.Join(Path.PathSeparator, Path.GetDirectoryName(bundled), Path.GetDirectoryName(installed), Path.GetDirectoryName(installed));
        Assert.Throws<InvalidOperationException>(() => NodeRuntime.Resolve(Path.GetDirectoryName(bundled)!, null, search, path => { probes.Add(path); return false; }));
        Assert.Equal([installed, bundled], probes);
    }
    [Fact]
    public void ExplicitRuntimeIsValidatedAndDoesNotSilentlyFallBackWhenInvalid() {
        var bundled = Runtime("app"); var configured = Runtime("configured");
        Assert.Equal(configured, NodeRuntime.Resolve(Path.GetDirectoryName(bundled)!, configured, null, _ => true));
        Assert.Throws<InvalidOperationException>(() => NodeRuntime.Resolve(Path.GetDirectoryName(bundled)!, configured, null, _ => false));
        Assert.Throws<InvalidOperationException>(() => NodeRuntime.Resolve(Path.GetDirectoryName(bundled)!, "relative/node.exe", null, _ => true));
    }
    public void Dispose() => directory.Dispose();
}
