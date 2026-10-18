using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Toolbox.Platform;

internal static class DiskCleanupTests
{
    public static void Run(Action<bool, string> check)
    {
        // All filesystem tests operate in this owned sandbox, never real cache locations.
        var root = Path.Combine(Path.GetTempPath(), "Toolbox-cleanup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "cache");
            Directory.CreateDirectory(Path.Combine(data, "nested"));
            var first = Path.Combine(data, "unsaved-work.tmp");
            var second = Path.Combine(data, "nested", "important.txt");
            File.WriteAllBytes(first, [1, 2, 3]);
            File.WriteAllBytes(second, [4, 5, 6, 7]);
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddYears(-2));
            var modified = File.GetLastWriteTimeUtc(first);
            File.SetAttributes(second, FileAttributes.ReadOnly);
            var location = new CleanupLocation("fixture", data, "No file is disposable based on its name or age.");
            var measured = DiskCleanupInspector.Measure(location);
            check(measured.Complete && measured.Bytes == 7 && measured.Files == 2, "Cleanup inspection measures nested and read-only files without treating them as deletable");
            check(File.ReadAllBytes(first).SequenceEqual(new byte[] { 1, 2, 3 }) && File.ReadAllBytes(second).SequenceEqual(new byte[] { 4, 5, 6, 7 }) &&
                File.GetLastWriteTimeUtc(first) == modified && File.GetAttributes(second).HasFlag(FileAttributes.ReadOnly),
                "Old temporary work and personal content remain intact with modification time and read-only attribute preserved");
            using (var locked = new FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var lockedResult = DiskCleanupInspector.Measure(location);
                check(lockedResult.HasData && File.Exists(first) && locked.Length == 3, "Inspection leaves locked files and their owner untouched");
            }
            var cancelled = new CancellationToken(true);
            var originalAttributes = File.GetAttributes(first);
            try
            {
                File.SetAttributes(first, originalAttributes | FileAttributes.Offline);
                var offline = DiskCleanupInspector.Measure(location);
                check(!offline.Complete && offline.Skipped == 1 && offline.Bytes == 4 && offline.Files == 1,
                    "Offline entries are skipped without opening or hydrating their contents");
            }
            finally { File.SetAttributes(first, originalAttributes); }
            var didCancel = false;
            try { DiskCleanupInspector.Measure(location, cancelled); } catch (OperationCanceledException) { didCancel = true; }
            check(didCancel && File.Exists(first) && File.Exists(second), "Cancellation exits without cleanup or partial deletion");
            var bounded = DiskCleanupInspector.Measure(location, maxEntries: 1);
            check(!bounded.Complete && bounded.Note.Contains("上限"), "Entry limit reports partial results instead of an apparently complete size");
            var timed = DiskCleanupInspector.Measure(location, timeLimit: TimeSpan.FromTicks(1));
            check(!timed.Complete && timed.Note.Contains("上限"), "Time limit reports incomplete estimates");
            var missing = DiskCleanupInspector.Measure(location with { Path = Path.Combine(root, "missing") });
            check(missing.Complete && missing.Bytes == 0 && missing.Note.Contains("未发现"), "Missing cache locations are distinguished from read failures");
            var notDirectory = DiskCleanupInspector.Measure(location with { Path = first });
            check(!notDirectory.Complete && !notDirectory.HasData, "Unexpected file at a cache directory path is rejected");

            var explorer = Path.Combine(root, "Explorer");
            Directory.CreateDirectory(Path.Combine(explorer, "subfolder"));
            File.WriteAllBytes(Path.Combine(explorer, "thumbcache_32.db"), [8, 9]);
            File.WriteAllBytes(Path.Combine(explorer, "settings.dat"), [1, 2, 3, 4, 5]);
            File.WriteAllBytes(Path.Combine(explorer, "subfolder", "thumbcache_256.db"), [1, 2, 3]);
            var thumbnails = DiskCleanupInspector.Measure(new("thumbnails", explorer, "fixture", false, "thumbcache_*.db"));
            check(thumbnails.Complete && thumbnails.Bytes == 2 && thumbnails.Files == 1, "Thumbnail estimate only includes matching top-level cache files");

            var outside = Path.Combine(root, "must-not-follow");
            Directory.CreateDirectory(Path.Combine(outside, "child"));
            File.WriteAllBytes(Path.Combine(outside, "child", "keep.txt"), new byte[1024]);
            var junction = Path.Combine(data, "junction");
            CreateJunction(junction, outside);
            var throughLink = DiskCleanupInspector.Measure(location);
            check(!throughLink.Complete && throughLink.Skipped == 1 && throughLink.Bytes == 7, "Nested junction is skipped and its target is excluded from the estimate");
            var rootLink = DiskCleanupInspector.Measure(location with { Path = junction });
            check(!rootLink.Complete && !rootLink.HasData, "Cache root that is itself a junction is rejected");
            var ancestorLink = DiskCleanupInspector.Measure(location with { Path = Path.Combine(junction, "child") });
            check(!ancestorLink.Complete && !ancestorLink.HasData, "Junction in a cache path's ancestors is rejected");
            check(File.ReadAllBytes(Path.Combine(outside, "child", "keep.txt")).Length == 1024, "Junction target is preserved");

            var denied = new DirectoryInfo(Path.Combine(root, "denied"));
            denied.Create();
            var changedAcl = denied.GetAccessControl();
            var denyRule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
            changedAcl.AddAccessRule(denyRule);
            try
            {
                denied.SetAccessControl(changedAcl);
                var deniedResult = DiskCleanupInspector.Measure(location with { Path = denied.FullName });
                check(!deniedResult.Complete && deniedResult.Skipped > 0, "Access-denied directories are marked incomplete, never reported as clean or empty");
            }
            finally { changedAcl.RemoveAccessRuleSpecific(denyRule); denied.SetAccessControl(changedAcl); }

            var oldTemp = Environment.GetEnvironmentVariable("TEMP");
            var oldTmp = Environment.GetEnvironmentVariable("TMP");
            try
            {
                Environment.SetEnvironmentVariable("TEMP", outside);
                Environment.SetEnvironmentVariable("TMP", outside);
                var known = DiskCleanupInspector.KnownLocations();
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                check(known.Any(entry => entry.Path == Path.Combine(local, "Temp")) && known.All(entry => !entry.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)),
                    "Production cache allowlist ignores TEMP/TMP overrides into personal directories");
                check(known.Count == 6 && known.All(entry => entry.Path != local && entry.Path != Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                    "Production scan uses six specific cache locations, not disks, profiles or all AppData");
            }
            finally { Environment.SetEnvironmentVariable("TEMP", oldTemp); Environment.SetEnvironmentVariable("TMP", oldTmp); }

            var start = WindowsCleanupLauncher.CreateDiskCleanupStartInfo("c:\\");
            check(start.FileName == Path.Combine(Environment.SystemDirectory, "cleanmgr.exe") && start.WorkingDirectory == Environment.SystemDirectory,
                "Windows cleanup executable uses the trusted absolute system path, not PATH or current directory");
            check(start.ArgumentList.SequenceEqual(new[] { "/d", "C" }) && start.Arguments.Length == 0,
                "Cleanup launcher only selects a disk: no automatic, preset or silent deletion switches");
            check(!start.UseShellExecute && start.Verb.Length == 0 && start.WindowStyle == ProcessWindowStyle.Normal && !start.CreateNoWindow,
                "Cleanup launcher opens visible review UI without elevation or shell command execution");
            foreach (var invalid in new[] { "C", "C:", "C:\\Windows", "C:\\ /sagerun:1", "C:\\&del", @"\\server\share\", "../", "" })
            {
                var rejected = false;
                try { WindowsCleanupLauncher.CreateDiskCleanupStartInfo(invalid); } catch (ArgumentException) { rejected = true; }
                check(rejected, "Reject unsafe or non-root cleanup argument: " + invalid);
            }
        }
        finally
        {
            // The only deletion here removes fixtures created by this test, never user caches.
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new Exception("Unexpected cleanup test sandbox");
            var readOnly = Path.Combine(root, "cache", "nested", "important.txt");
            if (File.Exists(readOnly)) File.SetAttributes(readOnly, FileAttributes.Normal);
            // Remove our junction separately before recursive fixture teardown.
            var junction = Path.Combine(root, "cache", "junction");
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateJunction(string path, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + path.Replace("'", "''") +
            "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(15_000)) { process.Kill(); throw new Exception("Fixture junction creation timed out"); }
        if (process.ExitCode != 0) throw new Exception("Fixture junction creation failed: " + process.StandardError.ReadToEnd());
    }
}
