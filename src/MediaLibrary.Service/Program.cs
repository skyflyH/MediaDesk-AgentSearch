using System.Text;
using MediaLibrary.Service;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

if (args.Length != 2 || args[0] != "--db" || !Path.IsPathFullyQualified(args[1]))
{
    Console.Error.WriteLine("Usage: MediaLibrary.Service --db <absolute-path>");
    return 2;
}

try
{
    using var database = new LibraryDatabase(args[1]);
    database.RecoverPendingSuggestions();
    await using var archives = new ArchiveWorker(args[1], Console.Error);
    var profile = Path.GetDirectoryName(args[1])!;
    using var previews = new MediaPreviews(database, Path.Combine(profile, "previews"), Path.Combine(profile, "preview-cache-path.txt"));
    var model = Environment.GetEnvironmentVariable("APM_PHOTO_MODEL_DIR");
    if (string.IsNullOrWhiteSpace(model))
    {
        model = Path.Combine(profile, "models", "chinese-clip");
        if (!File.Exists(Path.Combine(model, "manifest.json")))
            model = Path.Combine(AppContext.BaseDirectory, "models", "chinese-clip");
    }
    await using var photos = new PhotoIndex(database, args[1], model, Console.Error);
    await new JsonLinesServer(database, Console.In, Console.Out, Console.Error, archives.Schedule, previews, photos).RunAsync();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Local service stopped ({error.GetType().Name}).");
    return 1;
}
