using MediaLibrary.WebView;
using Xunit;

namespace MediaLibrary.Service.Tests;

public sealed class ExplorerLauncherTests
{
    [Theory]
    [InlineData(@"C:\照片库\2026 年,原片\a.jpg", true, @"C:\照片库\2026 年,原片", "/select,\"C:\\照片库\\2026 年,原片\\a.jpg\"")]
    [InlineData(@"C:\照片库\目录", true, @"C:\照片库", "/select,\"C:\\照片库\\目录\"")]
    [InlineData(@"C:\照片库\目录", false, @"C:\照片库\目录", "\"C:\\照片库\\目录\"")]
    [InlineData(@"C:\", true, @"C:\", "\"C:\\\"")]
    [InlineData(@"\\server\share\照片\a.jpg", true, @"\\server\share\照片", "/select,\"\\\\server\\share\\照片\\a.jpg\"")]
    public void DirectoryWindowAndArgumentsMatchTheOperation(string path, bool select, string folder, string arguments)
    {
        var request = ExplorerLauncher.CreateRequest(path, select);
        Assert.Equal(folder, request.Directory);
        Assert.Equal(arguments, request.Arguments);
        Assert.Equal(select && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is not null
            ? Path.GetFullPath(path) : null, request.SelectedPath);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("C:\\bad\"path")]
    [InlineData("C:\\bad\0path")]
    public void RejectsInvalidLaunchPaths(string path) => Assert.Throws<InvalidOperationException>(() => ExplorerLauncher.CreateRequest(path, true));

    [Fact]
    public void MatchesOnlyTheRequestedFilesystemDirectory()
    {
        Assert.True(ExplorerLauncher.SameDirectory(@"C:\Photos\Album\", "c:/photos/album"));
        Assert.True(ExplorerLauncher.SameDirectory(@"\\SERVER\share\Photos", @"\\server\SHARE\photos\"));
        Assert.False(ExplorerLauncher.SameDirectory(@"C:\Photos\Album-old", @"C:\Photos\Album"));
        Assert.False(ExplorerLauncher.SameDirectory(@"C:\Photos\Album\Nested", @"C:\Photos\Album"));
        Assert.False(ExplorerLauncher.SameDirectory("This PC", @"C:\Photos"));
    }
}
