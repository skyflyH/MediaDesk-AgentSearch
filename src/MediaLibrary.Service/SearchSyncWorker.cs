using System.Text.Json;

namespace MediaLibrary.Service;

internal sealed class SearchSyncWorker : IAsyncDisposable
{
    private readonly string databasePath;
    private readonly TextWriter diagnostics;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Dictionary<string, RootState> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task worker;
    private readonly bool enableJournal;
    internal long FilesRefreshed { get; private set; }
    internal long DirectoriesScanned { get; private set; }
    internal int JournalRoots => states.Values.Count(state => state.Journal is not null);
    private sealed class RootState : IDisposable
    {
        internal readonly object Lock = new();
        internal readonly Dictionary<string, uint> Pending = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Renames = new(StringComparer.OrdinalIgnoreCase);
        internal FileSystemWatcher? Watcher;
        internal WindowsSearchJournal? Journal;
        internal bool Overflow;
        internal bool Initialized;
        internal bool Recursive;
        internal bool RestartWatcher;
        internal DateTime RetryAfter;
        public void Dispose() { Watcher?.Dispose(); Journal?.Dispose(); }
    }

    internal SearchSyncWorker(string path, TextWriter diagnostics, bool enableJournal = true)
    {
        databasePath = path;
        this.diagnostics = diagnostics;
        this.enableJournal = enableJournal;
        worker = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await SynchronizeAsync(null, null); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception error) { diagnostics.WriteLine($"Search synchronization failed ({error.GetType().Name})."); }
                try { await wake.WaitAsync(TimeSpan.FromSeconds(30), stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                // Coalesce bursts without busy polling or an unbounded event queue.
                try { await Task.Delay(350, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            }
        });
    }

    internal Task EnsureIndexedAsync(JsonElement input, Action<DirectoryIndexProgress>? progress) =>
        SynchronizeAsync(input, progress);
    internal void Schedule() => Signal();

    private async Task SynchronizeAsync(JsonElement? input, Action<DirectoryIndexProgress>? progress)
    {
        await gate.WaitAsync(stop.Token);
        try
        {
            await Task.Run(() =>
            {
                using var database = new LibraryDatabase(databasePath);
                string[]? requested = null;
                if (input is { } parameters)
                {
                    var (paths, recursive) = LibraryDatabase.SearchIndexRequest(parameters);
                    requested = paths;
                    database.RegisterSearchRoots(requested, recursive);
                }
                var roots = database.SearchRoots();
                foreach (var removed in states.Keys.Where(path => !roots.Any(root => WindowsPaths.SamePath(path, root.Path))).ToArray())
                { states[removed].Dispose(); states.Remove(removed); }
                foreach (var root in roots.OrderBy(root => root.Path.Length))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    // A recursive ancestor owns monitoring. Descendants share its persistent index.
                    if (roots.Any(parent => parent.Recursive && parent.Path.Length < root.Path.Length && WindowsPaths.Inside(root.Path, parent.Path)))
                    {
                        if (states.Remove(root.Path, out var redundant)) redundant.Dispose();
                        if (!root.Ready && roots.Any(parent => parent.Ready && parent.Recursive && parent.Path.Length < root.Path.Length && WindowsPaths.Inside(root.Path, parent.Path)))
                            database.SaveSearchCheckpoint(root.Path, null, null);
                        continue;
                    }
                    if (!states.TryGetValue(root.Path, out var state)) states[root.Path] = state = new();
                    if (state.RetryAfter > DateTime.UtcNow) continue;
                    try
                    {
                        SynchronizeRoot(database, root, state, requested?.Contains(root.Path, StringComparer.OrdinalIgnoreCase) == true ? progress : null);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or RequestError)
                    {
                        if (!Directory.Exists(root.Path)) state.RestartWatcher = true;
                        state.RetryAfter = DateTime.UtcNow.AddSeconds(state.Initialized ? 2 : 30);
                        diagnostics.WriteLine($"Search root temporarily unavailable ({error.GetType().Name}); retaining index.");
                        if (requested?.Contains(root.Path, StringComparer.OrdinalIgnoreCase) == true) throw;
                    }
                }
                FilesRefreshed += database.SearchFilesRefreshed;
                DirectoriesScanned += database.SearchDirectoriesScanned;
                progress?.Invoke(new("complete", 0, 0, 0));
            }, stop.Token);
        }
        finally { gate.Release(); }
    }

    private void SynchronizeRoot(LibraryDatabase database, LibraryDatabase.SearchRoot root, RootState state,
        Action<DirectoryIndexProgress>? progress)
    {
        if (!Directory.Exists(root.Path)) throw new IOException("Search root offline.");
        if (state.Watcher is not null && (state.Recursive != root.Recursive || state.RestartWatcher))
        {
            state.Dispose(); state.Watcher = null; state.Journal = null; state.Initialized = false;
            state.RestartWatcher = false;
        }
        if (state.Watcher is null)
        {
            state.Journal = enableJournal ? WindowsSearchJournal.TryOpen(root.Path) : null;
            state.Recursive = root.Recursive;
            var watcher = new FileSystemWatcher(root.Path) {
                IncludeSubdirectories = root.Recursive, InternalBufferSize = 8192,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                    NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.CreationTime | NotifyFilters.Security
            };
            watcher.Created += (_, e) => Changed(e.FullPath, 0x100);
            watcher.Changed += (_, e) => Changed(e.FullPath, 1);
            watcher.Deleted += (_, e) => Changed(e.FullPath, 0x200);
            watcher.Renamed += (_, e) => {
                lock (state.Lock)
                {
                    if (state.Renames.Count >= 4096) { state.Renames.Clear(); state.Overflow = true; }
                    else state.Renames[e.OldFullPath] = e.FullPath;
                }
                Changed(e.FullPath, 0x2000);
            };
            watcher.Error += (_, e) => {
                lock (state.Lock) {
                    state.Overflow = true;
                    state.RestartWatcher = e.GetException() is not InternalBufferOverflowException;
                }
                Signal();
            };
            state.Watcher = watcher;
            watcher.EnableRaisingEvents = true;

            void Changed(string path, uint reason)
            {
                // USN is authoritative; watcher events only wake it promptly.
                if (state.Journal is null)
                {
                    lock (state.Lock)
                    {
                        if (state.Pending.Count >= 4096) { state.Pending.Clear(); state.Overflow = true; }
                        else state.Pending[path] = state.Pending.GetValueOrDefault(path) | reason;
                    }
                }
                Signal();
            }
        }

        bool overflow;
        KeyValuePair<string,uint>[] pending;
        KeyValuePair<string,string>[] renames;
        lock (state.Lock) {
            overflow = state.Overflow; state.Overflow = false;
            pending = state.Pending.ToArray(); state.Pending.Clear();
            renames = state.Renames.ToArray(); state.Renames.Clear();
        }
        var journal = state.Journal;
        if (journal is not null)
        {
            JournalCursor cursor;
            try { cursor = journal.Query(); }
            catch (IOException)
            {
                journal.Dispose(); state.Journal = null; state.Initialized = false;
                lock (state.Lock) state.Overflow = true;
                throw;
            }
            var next = root.NextUsn;
            if (!root.Ready || root.JournalId != cursor.Id || next is null || next < cursor.First || next > cursor.Next)
            {
                // Capture the checkpoint before scanning so concurrent edits are replayed afterwards.
                next = cursor.Next;
                var count = 0;
                database.ScanSearchTree(root.Path, root.Recursive, progress, ref count, stop.Token);
                database.SaveSearchCheckpoint(root.Path, cursor.Id, next);
            }
            // Fixed-size pages keep memory bounded even after a long offline period.
            try
            {
                while (next < cursor.Next)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var page = journal.Read(next.Value, cursor.Id);
                    if (page.Next <= next) throw new IOException("Journal cursor did not advance.");
                    foreach (var change in page.Changes.GroupBy(change => change.FileId))
                    {
                        var latest = change.Last();
                        ApplyJournalChange(database, root, journal, latest with { Reason = change.Aggregate(0u, (reason, item) => reason | item.Reason) });
                    }
                    next = page.Next;
                    database.SaveSearchCheckpoint(root.Path, cursor.Id, next);
                }
            }
            catch (JournalUnavailableException)
            {
                journal.Dispose(); state.Journal = null; state.Initialized = false;
                lock (state.Lock) state.Overflow = true;
                throw;
            }
        }
        else
        {
            if (!state.Initialized || !root.Ready || overflow)
            {
                var count = 0;
                database.ScanSearchTree(root.Path, root.Recursive, progress, ref count, stop.Token);
                database.SaveSearchCheckpoint(root.Path, null, null);
            }
            else
            {
                foreach (var (oldPath, newPath) in renames)
                {
                    if (!WindowsPaths.Exists(newPath)) continue;
                    var directory = Directory.Exists(newPath);
                    var identity = directory ? ReadDirectoryIdentity(newPath) : MediaFile.Read(newPath).Identity;
                    var previous = database.SearchIdentityPath(identity, directory);
                    if (previous is not null && InScope(root, newPath))
                    {
                        database.RelocateSearchPath(previous, newPath, directory);
                        if (!directory && !LibraryDatabase.IsSearchMedia(newPath)) database.MarkSearchMissing(newPath);
                    }
                    else if (!InScope(root, newPath)) database.MarkSearchMissing(oldPath);
                }
                try
                {
                    foreach (var (path, reason) in pending) ApplyPath(database, root, path, reason);
                }
                catch (IOException)
                {
                    // A busy file retries the same bounded batch; it does not force a whole-root scan.
                    lock (state.Lock)
                    {
                        foreach (var (path, reason) in pending)
                        {
                            if (state.Pending.Count >= 4096) { state.Overflow = true; break; }
                            state.Pending[path] = state.Pending.GetValueOrDefault(path) | reason;
                        }
                    }
                    Signal();
                    throw;
                }
            }
        }
        state.Initialized = true;
    }

    private void ApplyJournalChange(LibraryDatabase database, LibraryDatabase.SearchRoot root, WindowsSearchJournal journal, JournalChange change)
    {
        var old = database.SearchIdentityPath(journal.Identity(change.FileId), change.Directory);
        var parent = database.SearchIdentityPath(journal.Identity(change.ParentId), folder: true);
        if (old is null && parent is null) return; // Ignore activity outside indexed trees without opening files.
        // Names in unprivileged records may be redacted. Resolve the parent by ID when necessary.
        parent ??= journal.Resolve(change.ParentId);
        var path = parent is not null && change.Name.Length > 0 ? Path.Combine(parent, change.Name) : journal.Resolve(change.FileId);
        if (path is not null && !WindowsPaths.Exists(path) && (change.Reason & 0x200u) == 0)
            path = journal.Resolve(change.FileId) ?? path;
        if (old is not null && (change.Reason & 0x3000u) != 0)
            path = journal.Resolve(change.FileId) ?? path;
        if (path is not null && InScope(root, path) && Path.GetExtension(path).Equals(".xmp", StringComparison.OrdinalIgnoreCase))
        { ApplyPath(database, root, path, change.Reason); return; }
        if (path is not null && InScope(root, path) && WindowsPaths.Exists(path))
        {
            if (!journal.Matches(path, change.FileId, change.Directory)) path = journal.Resolve(change.FileId);
            if (path is null || !InScope(root, path)) throw new IOException("Cannot resolve indexed identity.");
            if (old is not null && !WindowsPaths.SamePath(old, path)) database.RelocateSearchPath(old, path, change.Directory);
            if (!change.Directory && !LibraryDatabase.IsSearchMedia(path)) { database.MarkSearchMissing(path); return; }
            ApplyPath(database, root, path, change.Reason);
        }
        else if (old is not null && InScope(root, old))
        {
            // A failed identity resolution alone is not evidence of deletion.
            if ((change.Reason & 0x3200u) != 0 && !WindowsPaths.Exists(old)) database.MarkSearchMissing(old);
            else if ((change.Reason & 0x200u) == 0) throw new IOException("Cannot resolve changed indexed file.");
        }
    }

    private void ApplyPath(LibraryDatabase database, LibraryDatabase.SearchRoot root, string path, uint reason)
    {
        if (!InScope(root, path)) return;
        if (Path.GetExtension(path).Equals(".xmp", StringComparison.OrdinalIgnoreCase))
        {
            // Deletion also invalidates the RAW companion.
            foreach (var companion in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".*"))
                if (RawPhoto.Supports(Path.GetExtension(companion))) database.RefreshSearchFile(companion, force: true);
            return;
        }
        if (!WindowsPaths.Exists(path)) { database.MarkSearchMissing(path); return; }
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) return;
        if ((attributes & FileAttributes.Directory) != 0)
        {
            // New/moved-in subtrees require discovery; existing renamed trees already retain their rows.
            var previous = database.SearchIdentityPath(ReadDirectoryIdentity(path), folder: true);
            if (previous is not null && !WindowsPaths.SamePath(previous, path))
                database.RelocateSearchPath(previous, path, directory: true);
            if (previous is null && root.Recursive)
            {
                var count = 0;
                database.ScanSearchTree(path, recursive: true, null, ref count, stop.Token);
            }
            else database.SaveSearchFolder(path);
        }
        else if (!LibraryDatabase.IsSearchMedia(path)) return;
        else database.RefreshSearchFile(path, force: (reason & ~0x80003000u) != 0);
    }

    private static string ReadDirectoryIdentity(string path)
    { using var handle = WindowsPaths.OpenDirectory(path); return MediaFile.Read(handle, directory: true).Identity; }
    private static bool InScope(LibraryDatabase.SearchRoot root, string path) => WindowsPaths.Inside(path, root.Path) &&
        (root.Recursive || WindowsPaths.SamePath(Path.GetDirectoryName(path) ?? path, root.Path));
    private void Signal() { try { wake.Release(); } catch (SemaphoreFullException) { } }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await worker;
        await gate.WaitAsync();
        foreach (var state in states.Values) state.Dispose();
        gate.Release();
        stop.Dispose(); gate.Dispose(); wake.Dispose();
    }
}
