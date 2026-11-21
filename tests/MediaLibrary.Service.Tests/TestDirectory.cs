namespace MediaLibrary.Service.Tests;

internal sealed class TestDirectory : IDisposable
{
    private const string Prefix = "media-library-test-";
    private readonly string root;
    private readonly DirectoryInfo directory;

    public TestDirectory(string? path = null)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path ?? AppContext.BaseDirectory));
        directory = Directory.CreateDirectory(Path.Combine(root, $"{Prefix}{Guid.NewGuid():N}"));
    }

    public string FullName => directory.FullName;

    public void Dispose()
    {
        directory.Refresh();
        if (directory.Exists && directory.Name.StartsWith(Prefix, StringComparison.Ordinal) &&
            directory.Parent?.FullName == root &&
            (directory.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            directory.Delete(recursive: true);
        }
    }
}
