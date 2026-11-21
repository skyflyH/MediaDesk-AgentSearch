using System.Threading.Channels;

namespace MediaLibrary.Service;

internal sealed class ArchiveWorker : IAsyncDisposable
{
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task worker;

    public ArchiveWorker(string path, TextWriter diagnostics)
    {
        worker = Task.Run(async () =>
        {
            using var database = new LibraryDatabase(path);
            var executor = new ArchiveExecutor(database, new WindowsProperties(database.ProfileDirectory), diagnostics: diagnostics);
            foreach (var id in database.PendingArchives()) Run(id, recovering: true);
            await foreach (var id in queue.Reader.ReadAllAsync()) Run(id, recovering: false);

            void Run(string id, bool recovering)
            {
                try
                {
                    if (!recovering && database.ReadArchive(id)?.ErrorCode == "PROPERTY_WRITE_FAILED") return;
                    executor.Run(id);
                }
                catch (Exception error) { diagnostics.WriteLine($"Archive worker error ({error.GetType().Name})."); }
            }
        });
    }

    public void Schedule(string id) => queue.Writer.TryWrite(id);

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        await worker;
    }
}
