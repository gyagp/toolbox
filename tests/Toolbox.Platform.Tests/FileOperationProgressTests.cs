using System.Runtime.InteropServices;
using Toolbox.Core;
using Toolbox.Platform;

internal static class FileOperationProgressTests
{
    internal static async Task RunAsync(Action<bool, string> check, string sandbox)
    {
        FileOperationProgress Estimate(uint total, uint done) => FileOperationProgress.FromShell(FileAction.Move, "fixture", total, done);
        check(Estimate(0, 0).Percentage is null && Estimate(0, 25).Percentage is null, "Unknown Shell totals never invent a percentage");
        check(Estimate(100, 101).Percentage is null, "Invalid or changing Shell totals fall back to indeterminate progress");
        check(Estimate(100, 0).Percentage == 0 && Estimate(100, 40).Percentage == 40, "Known Shell work units produce the actual estimated ratio");
        check(Estimate(uint.MaxValue, uint.MaxValue / 2).Percentage == 49, "Large native work counters cannot overflow percentage arithmetic");
        check(Estimate(100, 100).Percentage == 99 && Estimate(100, 100).Stage == FileOperationStage.Finalizing,
            "Shell's full estimate is not reported as completed before filesystem verification");
        check(Estimate(100, 50).Percentage == 50 && Estimate(200, 50).Percentage == 25, "Updated Windows totals replace the estimate instead of fabricating monotonic progress");
        check(FileOperationProgress.FromShell(FileAction.Recycle, null, 100, 20).Message.Contains("回收站") &&
            FileOperationProgress.FromShell(FileAction.PermanentDelete, null, 100, 20).Message.Contains("彻底删除"), "Progress distinguishes recycle from permanent deletion");

        var callbackFrames = new List<FileOperationProgress>();
        var callbacks = new ShellFileOperation.OperationProgress(operationProgress: new Sink(callbackFrames.Add));
        callbacks.UpdateProgress(100, 40);
        callbacks.PostDeleteItem(0, null, 0x00270000, null); // Positive scheduling status, not a file count.
        check(callbackFrames[^1].Percentage == 40, "A Shell item notification is never counted as another completed file or byte");
        callbacks.FinishOperations(0);
        check(callbackFrames.All(frame => frame.Stage != FileOperationStage.Completed && frame.Percentage != 100), "FinishOperations callback alone never declares success");

        var root = Path.Combine(sandbox, "progress");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(destination);
        var path = Path.Combine(root, "当前 文件.txt");
        File.WriteAllText(path, "progress fixture");
        var iid = typeof(ShellFileOperation.IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out var item));
        try
        {
            callbackFrames.Clear();
            var itemCallbacks = new ShellFileOperation.OperationProgress(operationProgress: new Sink(callbackFrames.Add), source: "different fallback");
            itemCallbacks.PreMoveItem(0, item, null, null);
            check(callbackFrames[^1].CurrentItem == path, "Current path comes from the real IShellItem, including Unicode and spaces");
        }
        finally { Marshal.FinalReleaseComObject(item); }

        foreach (var action in new[] { FileAction.Move, FileAction.Recycle, FileAction.PermanentDelete })
        {
            var file = Path.Combine(root, action + ".txt");
            File.WriteAllText(file, "progress fixture");
            var frames = new List<FileOperationProgress>();
            await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(action, file, destination), operationProgress: new Sink(frames.Add));
            check(frames.Count > 1 && frames[^1].Stage == FileOperationStage.Completed && frames[^1].Percentage == 100 && !File.Exists(file),
                action + " emits 100% only after native success and source verification");
            check(frames.Take(frames.Count - 1).All(frame => frame.Percentage is null or < 100) && frames.Any(frame => frame.CurrentItem == file),
                action + " reports its path without prematurely reporting completion");
        }
        var failed = new List<FileOperationProgress>();
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var didFail = false;
            try { await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.PermanentDelete, path), operationProgress: new Sink(failed.Add)); }
            catch (IOException) { didFail = true; }
            check(didFail && File.Exists(path) && failed.All(frame => frame.Stage != FileOperationStage.Completed && frame.Percentage != 100),
                "A locked operation can never publish false completion");
        }
        var canceledFrames = new List<FileOperationProgress>();
        try { await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.PermanentDelete, path),
            cancellation: new CancellationToken(true), operationProgress: new Sink(canceledFrames.Add)); }
        catch (OperationCanceledException) { }
        check(File.Exists(path) && canceledFrames.All(frame => frame.Stage != FileOperationStage.Completed), "Cancellation never emits completed progress");

        var recoveryFrames = new List<FileOperationProgress>();
        var count = 0;
        await FileOperationRecovery.ExecuteCoreAsync(FileActionPlan.Create(FileAction.Move, path, destination), true, default,
            _ => ++count == 1 ? Task.FromException(new IOException("sharing fixture", unchecked((int)0x80070020))) : Task.CompletedTask,
            _ => Task.FromResult(new LockReport([new(500000, 123, "policy fixture", "", [path])], 1, [])),
            (_, _) => Task.FromResult(true), progress: new Sink(recoveryFrames.Add));
        check(recoveryFrames.Any(frame => frame.Stage == FileOperationStage.InspectingLocks) && recoveryFrames.Any(frame => frame.Stage == FileOperationStage.ReleasingLocks) &&
            recoveryFrames.Any(frame => frame.Stage == FileOperationStage.Retrying) && recoveryFrames.All(frame => frame.Percentage is null),
            "Lock inspection, termination and retry start with no stale transfer percentage");
    }

    private sealed class Sink(Action<FileOperationProgress> report) : IProgress<FileOperationProgress>
    {
        public void Report(FileOperationProgress value) => report(value);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid iid, out ShellFileOperation.IShellItem item);
}
