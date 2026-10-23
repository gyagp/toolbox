using Toolbox.Core;
using Toolbox.Platform;

internal static class FileOperationCancellationTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "Toolbox-cancel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "keep.txt");
            var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(destination);
            File.WriteAllText(file, "preserve on cancellation");
            foreach (var action in new[] { FileAction.Move, FileAction.Recycle, FileAction.PermanentDelete })
            {
                var task = ShellFileOperation.ExecuteAsync(FileActionPlan.Create(action, file, destination), cancellation: new CancellationToken(true));
                try { await task; } catch (OperationCanceledException) { }
                check(task.IsCanceled && File.ReadAllText(file) == "preserve on cancellation" && !File.Exists(Path.Combine(destination, "keep.txt")),
                    "Cancel before " + action + " preserves the source and does not create a destination");
            }
            using var callbacksCancellation = new CancellationTokenSource();
            var callbacks = new ShellFileOperation.OperationProgress(callbacksCancellation.Token);
            check(callbacks.PreMoveItem(0, null, null, null) == 0, "Shell callback permits work before cancellation");
            callbacksCancellation.Cancel();
            check(callbacks.PreMoveItem(0, null, null, null) < 0 && callbacks.PreDeleteItem(0, null) < 0 &&
                callbacks.PreCopyItem(0, null, null, null) < 0 && callbacks.UpdateProgress(100, 20) < 0,
                "Cancellation aborts Shell move/delete/copy and progress callbacks");

            var folder = Path.Combine(root, "partial-delete");
            Directory.CreateDirectory(folder);
            for (var index = 0; index < 100; index++) File.WriteAllText(Path.Combine(folder, $"fixture-{index:D3}.txt"), "fixture");
            using var during = new CancellationTokenSource();
            var progress = new CallbackProgress(_ => during.Cancel());
            var running = ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.PermanentDelete, folder), cancellation: during.Token, progress: progress);
            try { await running; } catch (OperationCanceledException) { }
            var remaining = Directory.Exists(folder) ? Directory.GetFiles(folder).Length : 0;
            check(running.IsCanceled && remaining > 0,
                "Cancellation requested from a native callback stops recursive deletion and preserves remaining files");
            check(File.ReadAllText(file) == "preserve on cancellation", "Mid-operation cancellation leaves unrelated files intact");
        }
        finally
        {
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new Exception("Unexpected cancellation fixture path");
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CallbackProgress(Action<int> action) : IProgress<int>
    {
        public void Report(int value) => action(value);
    }
}
