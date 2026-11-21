using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MediaLibrary.Service;

internal sealed class ArchiveExecutor(LibraryDatabase database, IArchiveProperties properties, Action<string>? checkpoint = null,
    TextWriter? diagnostics = null)
{
    public void Run(string operationId)
    {
        var operation = database.ReadArchive(operationId);
        if (operation is null || operation.Phase is "complete" or "failed" or "needs_review") return;

        try
        {
            if (operation.Phase != "properties_pending")
            {
                var target = database.OpenArchiveDirectory(operation.Plan.DirectoryId);
                using var root = target.Root;
                using var directory = target.Directory;
                if (operation.Phase is "accepted" or "copying")
                    operation = Prepare(operation);
                if (operation.Phase == "ready_to_publish")
                    operation = Publish(operation, directory);
                if (operation.Phase == "destination_published")
                    operation = Register(operation);
            }
            WriteProperties(operation);
        }
        catch (RequestError error)
        {
            Review(operationId, error.Code, error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            diagnostics?.WriteLine($"Archive file action failed ({error.GetType().Name}, 0x{error.HResult:X8}, {GetNativeError(error)}).");
            var current = database.ReadArchive(operationId)!;
            var denied = error is UnauthorizedAccessException || GetNativeError(error) == 5 ||
                error.HResult == unchecked((int)0x80070005);
            if (denied && current is { Phase: "copying", TempIdentity: null })
            {
                Review(operationId, "DESTINATION_UNWRITABLE",
                    $"无法在目标目录创建副本：{Path.GetDirectoryName(current.Plan.Target)}。请换一个可写目录后重试。");
            }
            else
                Review(operationId, "COPY_FAILED", "操作中断或文件无法访问，请核对记录中的路径。");
        }
    }

    private static int? GetNativeError(Exception error) =>
        (error.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode;

    private ArchiveRecord Prepare(ArchiveRecord operation)
    {
        var plan = operation.Plan;
        LibraryDatabase.CheckDestination(plan.Target);
        if (RawPhoto.Supports(Path.GetExtension(plan.Target))) LibraryDatabase.CheckDestination(RawSidecar.PathFor(plan.Target));
        using var source = OpenVerifiedSource(plan);
        var hash = Hash(source);

        // The stage name is persisted before the exclusive create. Without a recorded
        // identity, recovery leaves a file at that name for manual review.
        var temp = operation.TempPath ?? Path.Combine(Path.GetDirectoryName(plan.Target)!, $".apm-{operation.Id}.tmp");
        operation = Save(operation with { Phase = "copying", TempPath = temp });
        var stageExists = WindowsPaths.Exists(temp);
        if (stageExists && operation.TempIdentity is null)
            throw new RequestError("NEEDS_REVIEW", "临时路径被占用，无法确认文件归属。");
        using var staged = stageExists ? WindowsPaths.OpenStage(temp) : WindowsPaths.CreateStage(temp);
        var identity = MediaFile.Read(staged);
        if (!WindowsPaths.SamePath(WindowsPaths.FinalPath(staged), temp) ||
            stageExists && operation.TempIdentity != identity)
            throw new RequestError("NEEDS_REVIEW", "归档临时文件的身份发生变化。");
        if (!stageExists)
            operation = Save(operation with { TempIdentity = identity });
        Copy(source, staged);
        if (FileStamp.Read(staged).Length != plan.SourceStamp.Length || Hash(staged) != hash ||
            FileStamp.Read(source) != plan.SourceStamp)
            throw new RequestError("NEEDS_REVIEW", "副本的长度或校验值不匹配。");
        checkpoint?.Invoke("stage_flushed");
        return Save(operation with { Phase = "ready_to_publish", Hash = hash });
    }

    private ArchiveRecord Publish(ArchiveRecord operation, SafeFileHandle directory)
    {
        var plan = operation.Plan;
        if (RawPhoto.Supports(Path.GetExtension(plan.Target))) LibraryDatabase.CheckDestination(RawSidecar.PathFor(plan.Target));
        if (WindowsPaths.Exists(plan.Target))
        {
            using var existing = WindowsPaths.OpenProtectedRead(plan.Target);
            var actualPath = WindowsPaths.FinalPath(existing);
            // A legacy move may already have renamed the original before recording its destination.
            var expected = operation.TempIdentity ?? (!WindowsPaths.Exists(plan.Source) ? plan.SourceStamp.Identity : null);
            if (expected is null || !WindowsPaths.SamePath(actualPath, plan.Target) ||
                MediaFile.Read(existing) != expected || Hash(existing) != operation.Hash)
                throw new RequestError("DESTINATION_EXISTS", $"保存目录中已存在同名文件，复制已中断：{plan.Target}。已有文件未被覆盖。");
            return Save(operation with { Phase = "destination_published", Actual = actualPath,
                Published = FileStamp.Read(existing) });
        }

        if (operation.TempIdentity is null)
            operation = Prepare(operation);
        using var source = OpenVerifiedSource(plan);
        if (Hash(source) != operation.Hash)
            throw new RequestError("SOURCE_UNAVAILABLE", "原件内容在发布前发生变化。");
        using var publishing = WindowsPaths.OpenSource(operation.TempPath!);
        if (MediaFile.Read(publishing) != operation.TempIdentity || Hash(publishing) != operation.Hash)
            throw new RequestError("NEEDS_REVIEW", "待发布文件身份或内容发生变化。");
        try { WindowsPaths.Rename(publishing, directory, plan.Target); }
        catch (IOException) when (WindowsPaths.Exists(plan.Target))
        {
            throw new RequestError("DESTINATION_EXISTS", $"保存目录中已出现同名目标，复制已中断：{plan.Target}。已有文件未被覆盖。");
        }
        checkpoint?.Invoke("renamed_before_log");
        var actual = WindowsPaths.FinalPath(publishing);
        if (!WindowsPaths.SamePath(actual, plan.Target))
            throw new RequestError("NEEDS_REVIEW", "实际归档路径与计划不一致。");
        return Save(operation with { Phase = "destination_published", Actual = actual, Published = FileStamp.Read(publishing) });
    }

    private ArchiveRecord Register(ArchiveRecord operation)
    {
        var plan = operation.Plan;
        using var published = WindowsPaths.OpenProtectedRead(operation.Actual!);
        if (FileStamp.Read(published) != operation.Published || Hash(published) != operation.Hash ||
            !WindowsPaths.SamePath(WindowsPaths.FinalPath(published), plan.Target))
            throw new RequestError("NEEDS_REVIEW", "已发布文件发生变化，原件已保留。");

        var next = operation with { Phase = "properties_pending",
            Remark = RawPhoto.Supports(Path.GetExtension(operation.Actual!)) ? "write_failed" : InitialProperty(operation.Plan.Draft.Remark.Intent),
            Authors = InitialProperty(operation.Plan.Draft.Authors.Intent) };
        database.RegisterArchive(next);
        checkpoint?.Invoke(next.Phase);
        return next;
    }

    private void WriteProperties(ArchiveRecord operation)
    {
        var path = operation.Actual!;
        var draft = operation.Plan.Draft;
        if (operation.Remark == "write_failed")
        {
            operation = WriteField(operation, () => RawPhoto.Supports(Path.GetExtension(path)) && properties is WindowsProperties windows
                    ? windows.WriteRawRemark(path, draft.Remark.Value, operation.Plan.Source) : properties.WriteRemark(path, draft.Remark.Value),
                (record, state) => record with { Remark = state },
                () => WindowsProperties.VerifyRemark(properties, path, draft.Remark.Value));
        }
        if (operation.Authors == "write_failed")
        {
            operation = WriteField(operation, () => properties.WriteAuthors(path, draft.Authors.Value),
                (record, state) => record with { Authors = state },
                () => WindowsProperties.VerifyAuthors(properties, path, draft.Authors.Value));
        }
        using var read = WindowsPaths.OpenProtectedRead(path);
        VerifyPublishedIdentity(operation, read);
        var hash = Hash(read);
        var failed = operation.Remark == "write_failed" || operation.Authors == "write_failed";
        database.UpdateArchiveProperties(operation with
        {
            Phase = failed ? "properties_pending" : "complete",
            Hash = hash,
            ErrorCode = failed ? "PROPERTY_WRITE_FAILED" : null,
            Message = failed ? "文件已归档，可重试写回失败的属性。" : null
        }, hash);
    }

    private ArchiveRecord WriteField(ArchiveRecord operation, Func<string> write,
        Func<ArchiveRecord, string, ArchiveRecord> setStatus, Func<bool> readBack)
    {
        var path = operation.Actual!;
        using var guard = WindowsPaths.ProtectProperties(path);
        VerifyPublishedIdentity(operation, guard);
        var state = write();
        using var current = WindowsPaths.OpenProtectedRead(path);
        var updated = FileStamp.Read(current);
        if (!WindowsPaths.SamePath(WindowsPaths.FinalPath(current), path))
            throw new RequestError("NEEDS_REVIEW", "属性写回期间文件路径发生变化。");
        // Some property handlers atomically rewrite the container, producing a new file ID.
        // Adopt it only immediately after a successful commit and guarded read-back.
        if (updated.Identity != operation.Published?.Identity &&
            (state != "written" || !readBack()))
            throw new RequestError("NEEDS_REVIEW", "无法确认属性处理程序更新后的文件身份。");

        var next = setStatus(operation, state) with { Published = updated };
        database.UpdateArchiveProperties(next, null);
        return next;
    }

    private static string InitialProperty(string intent) => intent == "unchanged" ? "unchanged" : "write_failed";

    private static void VerifyPublishedIdentity(ArchiveRecord operation, SafeFileHandle handle)
    {
        if (MediaFile.Read(handle) != operation.Published?.Identity ||
            !WindowsPaths.SamePath(WindowsPaths.FinalPath(handle), operation.Actual!))
            throw new RequestError("NEEDS_REVIEW", "归档文件已被替换，无法写回属性。");
    }

    private ArchiveRecord Save(ArchiveRecord operation)
    {
        database.SaveArchive(operation);
        checkpoint?.Invoke(operation.Phase);
        return operation;
    }

    private void Review(string id, string code, string message)
    {
        var current = database.ReadArchive(id)!;
        // A copy cannot be published before the identity of its temporary file is recorded.
        var phase = current is { Phase: "accepted" or "copying", TempIdentity: null, Actual: null, Published: null }
            ? "failed" : "needs_review";
        database.SaveArchive(current with { Phase = phase, ErrorCode = code, Message = message });
    }

    private static SafeFileHandle OpenVerifiedSource(ArchiveSnapshot plan)
    {
        var handle = WindowsPaths.OpenProtectedRead(plan.Source);
        try
        {
            if (FileStamp.Read(handle) == plan.SourceStamp && WindowsPaths.SamePath(WindowsPaths.FinalPath(handle), plan.Source))
                return handle;
            throw new RequestError("SOURCE_UNAVAILABLE", "原件在确认后发生变化。");
        }
        catch { handle.Dispose(); throw; }
    }

    internal static string Hash(SafeFileHandle handle)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long offset = 0;
        int count;
        while ((count = RandomAccess.Read(handle, buffer, offset)) != 0)
        {
            hash.AppendData(buffer, 0, count);
            offset += count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Copy(SafeFileHandle source, SafeFileHandle destination)
    {
        var buffer = new byte[1024 * 1024];
        long offset = 0;
        int count;
        while ((count = RandomAccess.Read(source, buffer, offset)) != 0)
        {
            RandomAccess.Write(destination, buffer.AsSpan(0, count), offset);
            offset += count;
        }
        RandomAccess.SetLength(destination, offset);
        RandomAccess.FlushToDisk(destination);
    }
}
