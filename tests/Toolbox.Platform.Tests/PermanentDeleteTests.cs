using Toolbox.Core;
using Toolbox.Platform;

internal static class PermanentDeleteTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        const uint undo = 0x40, recycle = 0x80000;
        check((ShellFileOperation.FlagsFor(FileAction.PermanentDelete) & (undo | recycle)) == 0,
            "Permanent deletion never enables Shell undo or Recycle Bin flags");
        check((ShellFileOperation.FlagsFor(FileAction.Recycle) & (undo | recycle)) == (undo | recycle),
            "Normal recycling retains its undo and Recycle Bin flags");
        var invalidRejected = false;
        try { ShellFileOperation.FlagsFor((FileAction)99); } catch (ArgumentOutOfRangeException) { invalidRejected = true; }
        check(invalidRejected, "Unknown Shell action is rejected");

        var root = Path.Combine(Path.GetTempPath(), "Toolbox-permanent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var keep = Path.Combine(root, "keep.txt");
            File.WriteAllText(keep, "unselected file");
            var file = Path.Combine(root, "待删除 中文.txt");
            File.WriteAllText(file, "isolated fixture");
            var plan = FileActionPlan.Create(FileAction.PermanentDelete, file);
            using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                IOException? failure = null;
                try { await ShellFileOperation.ExecuteAsync(plan); } catch (IOException error) { failure = error; }
                check(failure is not null && ((uint)failure.HResult is 0x80070020 or 0x80070021 or 0x80270027) &&
                    File.ReadAllText(file) == "isolated fixture", "Locked permanent deletion exposes a sharing error and preserves the source");
            }
            await ShellFileOperation.ExecuteAsync(plan);
            check(!File.Exists(file) && File.ReadAllText(keep) == "unselected file", "Permanent-delete retry removes only the selected fixture after its lock is released");

            var folder = Path.Combine(root, "nested-delete");
            Directory.CreateDirectory(Path.Combine(folder, "child"));
            File.WriteAllText(Path.Combine(folder, "child", "fixture.txt"), "nested fixture");
            await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.PermanentDelete, folder));
            check(!Directory.Exists(folder) && File.Exists(keep), "Permanent folder deletion removes its subtree and preserves siblings");

            var target = Path.Combine(root, "outside-target");
            Directory.CreateDirectory(target);
            var targetFile = Path.Combine(target, "must-survive.txt");
            File.WriteAllText(targetFile, "junction target");
            var junction = Path.Combine(root, "junction");
            DiskCleanupTests.CreateJunction(junction, target);
            await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.PermanentDelete, junction));
            check(!Directory.Exists(junction) && File.ReadAllText(targetFile) == "junction target", "Deleting a junction removes the link, not its target");
            Directory.CreateDirectory(folder);
            DiskCleanupTests.CreateJunction(Path.Combine(folder, "nested-junction"), target);
            await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.PermanentDelete, folder));
            check(!Directory.Exists(folder) && File.ReadAllText(targetFile) == "junction target", "Recursive Shell deletion does not traverse a nested junction target");

            var empty = Path.Combine(root, "empty-folder");
            Directory.CreateDirectory(empty);
            await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.PermanentDelete, empty));
            check(!Directory.Exists(empty), "Empty directories support permanent deletion");
        }
        finally
        {
            // Delete only this generated fixture root, never a user-selected path.
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new Exception("Unexpected permanent-delete fixture path");
            Directory.Delete(root, recursive: true);
        }
    }
}
