using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Toolbox.Platform;

internal static class CodexCleanupTests
{
    internal static async Task RunAsync(Action<bool, string> check, string sandbox)
    {
        var fixtureRoot = Path.Combine(sandbox, "codex-cleanup");
        Directory.CreateDirectory(fixtureRoot);
        var fixtureNumber = 0;
        (string Root, CodexReleaseCleaner Cleaner) Fixture(Func<CodexReleaseCleaner.RunningPackages>? processes = null, string installation = "standalone")
        {
            var root = Path.Combine(fixtureRoot, (++fixtureNumber).ToString(), ".codex", "packages", installation);
            Directory.CreateDirectory(Path.Combine(root, "releases"));
            foreach (var version in new[] { "0.9.0", "0.10.0", "0.11.0", "0.12.0" }) AddPackage(root, version);
            DiskCleanupTests.CreateJunction(Path.Combine(root, "current"), Package(root, "0.12.0"));
            File.WriteAllText(Path.Combine(root, "auto-update-version"), Name("0.12.0"));
            File.WriteAllText(Path.Combine(root, "install.lock"), "");
            File.WriteAllText(Path.Combine(root, "..", "..", "config.toml"), "preserve config and credentials");
            return (root, new(root, processes ?? (() => new([]))));
        }
        var (root, cleaner) = Fixture();
        var preview = cleaner.Scan();
        check(preview.CandidateCount == 2 && preview.Releases.Where(release => release.CanDelete).Select(release => release.Version).Order().SequenceEqual(new[] { "0.10.0", "0.9.0" }),
            "Codex cleanup keeps newest two by numeric version, not lexical order");
        check(preview.Releases.Single(release => release.Version == "0.12.0").KeepReason!.Contains("current"), "Codex current launcher target is always protected");
        check(cleaner.Scan(1).CandidateCount == 3 && Directory.GetDirectories(Path.Combine(root, "releases")).Length == 4,
            "Keep-one preview does not delete or change installed packages");
        foreach (var invalid in new[] { 0, 3, -1 })
        {
            var rejected = false; try { cleaner.Scan(invalid); } catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "Reject unsupported Codex retention count " + invalid);
        }
        var result = await cleaner.DeleteAsync(preview);
        check(result.Deleted.Count == 2 && result.Skipped.Count == 0 && !result.Cancelled, "Permanent Codex cleanup deletes only the two reviewed old fixture packages");
        check(Directory.GetDirectories(Path.Combine(root, "releases")).Select(Path.GetFileName).Order().SequenceEqual(new[] { Name("0.11.0"), Name("0.12.0") }),
            "Exactly the newest two complete Codex versions remain after cleanup");
        check(File.ReadAllText(Path.Combine(root, "..", "..", "config.toml")) == "preserve config and credentials" &&
            File.ReadAllText(Path.Combine(root, "auto-update-version")) == Name("0.12.0") && Directory.Exists(Path.Combine(root, "current", "bin")),
            "Codex config, updater marker, current junction and launcher files remain intact");

        var one = Fixture(); var oneResult = await one.Cleaner.DeleteAsync(one.Cleaner.Scan(1));
        check(oneResult.Deleted.Count == 3 && Directory.GetDirectories(Path.Combine(one.Root, "releases")).Length == 1, "Keep-one cleanup leaves one newest complete package");

        var protectedFixture = Fixture();
        Directory.Delete(Path.Combine(protectedFixture.Root, "current"));
        DiskCleanupTests.CreateJunction(Path.Combine(protectedFixture.Root, "current"), Package(protectedFixture.Root, "0.9.0"));
        File.WriteAllText(Path.Combine(protectedFixture.Root, "auto-update-version"), Name("0.10.0"));
        var protectedCleaner = new CodexReleaseCleaner(protectedFixture.Root, () => new([Path.Combine(Package(protectedFixture.Root, "0.11.0"), "bin", "codex-exec-server.exe")]));
        var protectedPreview = protectedCleaner.Scan(1);
        check(protectedPreview.CandidateCount == 0 && protectedPreview.Releases.Count == 4,
            "Older current, updater and running helper packages are retained beyond the requested count");
        var unknownRunning = new CodexReleaseCleaner(protectedFixture.Root, () => new([], "unreadable process"));
        check(unknownRunning.Scan().CandidateCount == 0 && unknownRunning.Scan().Warnings.Count > 0, "Uncertain Codex process paths disable deletion");

        var locked = Fixture(); var lockedPreview = locked.Cleaner.Scan();
        var lockedPath = Path.Combine(Package(locked.Root, "0.9.0"), "bin", "codex.exe");
        using (var stream = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var lockedResult = await locked.Cleaner.DeleteAsync(lockedPreview);
            check(lockedResult.Skipped.Count == 1 && File.Exists(lockedPath) && File.Exists(Path.Combine(Package(locked.Root, "0.9.0"), "codex-package.json")),
                "A locked Codex package is skipped before mutation; unrelated old packages can complete");
        }
        var lockedRetry = await locked.Cleaner.DeleteAsync(locked.Cleaner.Scan());
        check(lockedRetry.Deleted.Count == 1 && !Directory.Exists(Package(locked.Root, "0.9.0")), "A fresh preview can remove an unlocked old package without killing its owner");

        var changed = Fixture(); var changedPreview = changed.Cleaner.Scan();
        File.WriteAllText(Path.Combine(Package(changed.Root, "0.9.0"), "codex-resources", "new-work.txt"), "new data");
        var changedResult = await changed.Cleaner.DeleteAsync(changedPreview);
        check(changedResult.Skipped.Any(message => message.Contains(Name("0.9.0"))) && File.ReadAllText(Path.Combine(Package(changed.Root, "0.9.0"), "codex-resources", "new-work.txt")) == "new data",
            "Changes after preview invalidate the package fingerprint and preserve new files");

        var updater = Fixture(); var updaterPreview = updater.Cleaner.Scan();
        using (var updateLock = new FileStream(Path.Combine(updater.Root, "install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var stopped = false; try { await updater.Cleaner.DeleteAsync(updaterPreview); } catch (IOException) { stopped = true; }
            check(stopped && Directory.GetDirectories(Path.Combine(updater.Root, "releases")).Length == 4, "An updater holding install.lock prevents all cleanup");
        }
        var staleReference = Fixture(); var stalePreview = staleReference.Cleaner.Scan();
        Directory.Delete(Path.Combine(staleReference.Root, "current"));
        DiskCleanupTests.CreateJunction(Path.Combine(staleReference.Root, "current"), Package(staleReference.Root, "0.9.0"));
        var staleResult = await staleReference.Cleaner.DeleteAsync(stalePreview);
        check(staleResult.Skipped.Any(message => message.Contains("current")) && Directory.Exists(Package(staleReference.Root, "0.9.0")),
            "A newly changed launcher target is re-protected immediately before cleanup");

        var canceled = Fixture();
        using (var stop = new CancellationTokenSource())
        {
            var cancelPreview = canceled.Cleaner.Scan(); stop.Cancel();
            var cancelResult = await canceled.Cleaner.DeleteAsync(cancelPreview, stop.Token);
            check(cancelResult.Cancelled && cancelResult.Deleted.Count == 0 && Directory.GetDirectories(Path.Combine(canceled.Root, "releases")).Length == 4,
                "Cancellation before Codex cleanup preserves every package");
        }
        var escaped = Fixture(); var escapePreview = escaped.Cleaner.Scan();
        var rejectedRoot = false;
        try { await escaped.Cleaner.DeleteAsync(escapePreview with { Root = fixtureRoot }); } catch (InvalidOperationException) { rejectedRoot = true; }
        check(rejectedRoot, "A forged preview cannot change the Codex installation root");
        var outside = Path.Combine(fixtureRoot, "personal-documents"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "preserve");
        var fakeRelease = escapePreview.Releases.First(release => release.CanDelete) with { Path = outside };
        var rejectedPath = false;
        try { await escaped.Cleaner.DeleteAsync(escapePreview with { Releases = [fakeRelease] }); } catch (InvalidOperationException) { rejectedPath = true; }
        check(rejectedPath && File.ReadAllText(Path.Combine(outside, "keep.txt")) == "preserve", "Forged out-of-scope package paths cannot delete personal data");

        var linked = Fixture();
        var nestedLink = Path.Combine(Package(linked.Root, "0.9.0"), "codex-resources", "linked");
        DiskCleanupTests.CreateJunction(nestedLink, outside);
        check(!linked.Cleaner.Scan().Releases.Single(release => release.Version == "" && release.Name == Name("0.9.0")).CanDelete,
            "Nested junctions disqualify an entire package, preserving their targets");
        await linked.Cleaner.DeleteAsync(linked.Cleaner.Scan());
        check(File.ReadAllText(Path.Combine(outside, "keep.txt")) == "preserve", "Cleanup never traverses a junction to unrelated files");

        var malformed = Fixture();
        File.WriteAllText(Path.Combine(Package(malformed.Root, "0.9.0"), "codex-package.json"), "{}");
        Directory.CreateDirectory(Path.Combine(malformed.Root, "releases", "0.13.0-beta.1-x86_64-pc-windows-msvc"));
        File.WriteAllText(Path.Combine(Package(malformed.Root, "0.10.0"), "user-document.txt"), "important");
        var malformedPreview = malformed.Cleaner.Scan();
        check(malformedPreview.CandidateCount == 0 && malformedPreview.Warnings.Count == 3, "Malformed metadata, prereleases and extra top-level content are preserved");
        var missingMetadata = Fixture(); File.Delete(Path.Combine(missingMetadata.Root, "auto-update-version"));
        var missingRejected = false; try { missingMetadata.Cleaner.Scan(); } catch (IOException) { missingRejected = true; }
        check(missingRejected, "Missing updater metadata fails closed instead of guessing the current version");

        var arm = Fixture();
        foreach (var version in new[] { "0.20.0", "0.21.0", "0.22.0" }) AddPackage(arm.Root, version, "aarch64-pc-windows-msvc");
        var armPreview = arm.Cleaner.Scan();
        check(armPreview.Releases.Where(release => release.Target == "aarch64-pc-windows-msvc" && !release.CanDelete).Count() == 2 &&
            armPreview.Releases.Where(release => release.Target == "x86_64-pc-windows-msvc" && !release.CanDelete).Count() == 2,
            "Retention keeps one or two newest packages separately for each architecture");

        IReadOnlyList<string> runningNow = [];
        var startedLater = Fixture(() => new(runningNow)); var startedPreview = startedLater.Cleaner.Scan();
        runningNow = [Path.Combine(Package(startedLater.Root, "0.9.0"), "bin", "codex.exe")];
        var startedResult = await startedLater.Cleaner.DeleteAsync(startedPreview);
        check(startedResult.Skipped.Count == 1 && Directory.Exists(Package(startedLater.Root, "0.9.0")),
            "A version launched after preview is protected by the fresh process check");

        var newlyInstalled = Fixture(); var installationPreview = newlyInstalled.Cleaner.Scan();
        AddPackage(newlyInstalled.Root, "0.13.0");
        await newlyInstalled.Cleaner.DeleteAsync(installationPreview);
        check(Directory.Exists(Package(newlyInstalled.Root, "0.11.0")) && Directory.Exists(Package(newlyInstalled.Root, "0.13.0")),
            "Cleanup never expands a reviewed plan to newly eligible packages");

        var partialCancel = Fixture(); var partialPreview = partialCancel.Cleaner.Scan();
        using (var stop = new CancellationTokenSource())
        {
            var partial = await partialCancel.Cleaner.DeleteAsync(partialPreview, stop.Token,
                new InlineProgress(update => { if (update.Completed == 1) stop.Cancel(); }));
            check(partial.Cancelled && partial.Deleted.Count == 1 && Directory.GetDirectories(Path.Combine(partialCancel.Root, "releases")).Length == 3,
                "Cancellation between packages stops subsequent deletion and reports already completed work");
        }

        var pinned = Fixture(); var pinnedPreview = pinned.Cleaner.Scan();
        var currentPinned = false; var markerPinned = false;
        var pinnedResult = await pinned.Cleaner.DeleteAsync(pinnedPreview, progress: new InlineProgress(update =>
        {
            if (update.Completed != 0) return;
            try { Directory.Move(Path.Combine(pinned.Root, "current"), Path.Combine(pinned.Root, "current-renamed")); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { currentPinned = true; }
            try { File.WriteAllText(Path.Combine(pinned.Root, "auto-update-version"), "changed"); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { markerPinned = true; }
        }));
        check(currentPinned && markerPinned && pinnedResult.Deleted.Count == 2,
            $"Launcher-link renaming and updater-marker writes are blocked during cleanup (link={currentPinned}, marker={markerPinned}, deleted={pinnedResult.Deleted.Count}, skipped={string.Join(";", pinnedResult.Skipped)})");

        var readOnly = Fixture(); var readonlyPreview = readOnly.Cleaner.Scan();
        var readonlyFile = Path.Combine(Package(readOnly.Root, "0.9.0"), "bin", "codex.exe");
        File.SetAttributes(readonlyFile, FileAttributes.ReadOnly);
        try
        {
            var readonlyResult = await readOnly.Cleaner.DeleteAsync(readonlyPreview);
            check(readonlyResult.Skipped.Count == 1 && File.Exists(Path.Combine(Package(readOnly.Root, "0.9.0"), "codex-package.json")) && File.Exists(readonlyFile),
                "A read-only binary aborts package deletion without clearing attributes or deleting other files");
        }
        finally { File.SetAttributes(readonlyFile, FileAttributes.Normal); }

        var liveImage = Fixture();
        var liveExe = Path.Combine(Package(liveImage.Root, "0.9.0"), "bin", "codex.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), liveExe, true);
        var livePreview = liveImage.Cleaner.Scan();
        var start = new ProcessStartInfo(liveExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("set /p toolbox_wait=");
        using (var child = Process.Start(start)!)
        {
            try
            {
                await Task.Delay(150);
                var liveResult = await liveImage.Cleaner.DeleteAsync(livePreview);
                check(!child.HasExited && liveResult.Skipped.Count == 1 && File.Exists(liveExe) && File.Exists(Path.Combine(Package(liveImage.Root, "0.9.0"), "codex-package.json")),
                    "Even if process discovery misses a running image, native delete preparation leaves its entire package intact");
            }
            finally
            {
                await child.StandardInput.WriteLineAsync("done"); child.StandardInput.Close();
                if (!child.WaitForExit(3000)) child.Kill();
            }
        }

        var combinedStandalone = Fixture(); var daemon = Fixture(installation: "app-server-daemon");
        var combined = new CodexInstallationsCleaner([combinedStandalone.Cleaner, daemon.Cleaner]);
        var combinedPreview = combined.Scan();
        check(combinedPreview.Installations.Count == 2 && combinedPreview.CandidateCount == 4 &&
            combinedPreview.Installations.All(report => report.CandidateCount == 2),
            "Both installations retain their own newest two versions, including duplicate version names");
        var combinedProgress = new List<CodexCleanupProgress>();
        var combinedResult = await combined.DeleteAsync(combinedPreview, progress: new InlineProgress(combinedProgress.Add));
        check(combinedResult.Deleted.Count == 4 && combinedResult.Deleted.Count(name => name.StartsWith("app-server-daemon / ")) == 2 &&
            Directory.GetDirectories(Path.Combine(daemon.Root, "releases")).Length == 2 &&
            Directory.GetDirectories(Path.Combine(combinedStandalone.Root, "releases")).Length == 2,
            "Combined cleanup deletes only reviewed old packages and distinguishes their installation in results");
        check(combinedProgress.All(update => update.Total == 4) && combinedProgress.Select(update => update.Completed).SequenceEqual(combinedProgress.Select(update => update.Completed).Order()) &&
            combinedProgress.Last().Completed == 4 && combinedProgress.Any(update => update.Name.Contains("app-server-daemon")),
            "Progress spans both installations monotonically with their source labels");
        check(File.Exists(Path.Combine(daemon.Root, "current", "bin", "codex.exe")) &&
            File.ReadAllText(Path.Combine(daemon.Root, "auto-update-version")) == Name("0.12.0") &&
            File.ReadAllText(Path.Combine(daemon.Root, "..", "..", "config.toml")) == "preserve config and credentials",
            "Daemon cleanup preserves its current executable, updater marker and user configuration");

        var oneStandalone = Fixture(); var oneDaemon = Fixture(installation: "app-server-daemon");
        var combinedOne = new CodexInstallationsCleaner([oneStandalone.Cleaner, oneDaemon.Cleaner]);
        var combinedOneResult = await combinedOne.DeleteAsync(combinedOne.Scan(1));
        check(combinedOneResult.Deleted.Count == 6 && Directory.GetDirectories(Path.Combine(oneDaemon.Root, "releases")).Length == 1 &&
            Directory.GetDirectories(Path.Combine(oneStandalone.Root, "releases")).Length == 1,
            "Keep-one applies independently to both installation roots");

        var protectedStandalone = Fixture(); var protectedDaemon = Fixture(installation: "app-server-daemon");
        Directory.Delete(Path.Combine(protectedDaemon.Root, "current"));
        DiskCleanupTests.CreateJunction(Path.Combine(protectedDaemon.Root, "current"), Package(protectedDaemon.Root, "0.9.0"));
        File.WriteAllText(Path.Combine(protectedDaemon.Root, "auto-update-version"), Name("0.10.0"));
        var daemonRunning = new CodexReleaseCleaner(protectedDaemon.Root, () => new([Path.Combine(Package(protectedDaemon.Root, "0.11.0"), "bin", "codex.exe")]));
        var independentlyProtected = new CodexInstallationsCleaner([protectedStandalone.Cleaner, daemonRunning]).Scan(1);
        check(independentlyProtected.Installations[1].CandidateCount == 0 && independentlyProtected.Installations[0].CandidateCount == 3,
            "Daemon current, updater and running versions are protected without affecting the other installation");

        var healthy = Fixture(); var brokenDaemon = Fixture(installation: "app-server-daemon");
        File.Delete(Path.Combine(brokenDaemon.Root, "auto-update-version"));
        var partialInstallations = new CodexInstallationsCleaner([healthy.Cleaner, brokenDaemon.Cleaner]);
        var partialInstallationsPreview = partialInstallations.Scan();
        check(partialInstallationsPreview.CandidateCount == 2 && partialInstallationsPreview.Installations[1].Releases.Count == 0 &&
            partialInstallationsPreview.Warnings.Any(warning => warning.Contains("app-server-daemon")),
            "Malformed daemon metadata contributes no deletion candidates and a source-specific warning");
        var healthyResult = await partialInstallations.DeleteAsync(partialInstallationsPreview);
        check(healthyResult.Deleted.Count == 2 && Directory.GetDirectories(Path.Combine(brokenDaemon.Root, "releases")).Length == 4,
            "An unverifiable daemon is untouched while a separately verified installation can be cleaned");

        var busyStandalone = Fixture(); var busyDaemon = Fixture(installation: "app-server-daemon");
        var combinedBusy = new CodexInstallationsCleaner([busyStandalone.Cleaner, busyDaemon.Cleaner]);
        var combinedBusyPreview = combinedBusy.Scan();
        using (var daemonLock = new FileStream(Path.Combine(busyDaemon.Root, "install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var busyResult = await combinedBusy.DeleteAsync(combinedBusyPreview);
            check(busyResult.Deleted.Count == 2 && busyResult.Skipped.Count == 2 && busyResult.Skipped.All(message => message.StartsWith("app-server-daemon / ")) &&
                Directory.GetDirectories(Path.Combine(busyDaemon.Root, "releases")).Length == 4,
                "Daemon installer lock stops all deletion in its root and reports skipped packages");
        }

        var cancelStandalone = Fixture(); var cancelDaemon = Fixture(installation: "app-server-daemon");
        var combinedCancel = new CodexInstallationsCleaner([cancelStandalone.Cleaner, cancelDaemon.Cleaner]);
        using (var stop = new CancellationTokenSource())
        {
            var canceledCombined = await combinedCancel.DeleteAsync(combinedCancel.Scan(), stop.Token,
                new InlineProgress(update => { if (update.Completed == 2) stop.Cancel(); }));
            check(canceledCombined.Cancelled && canceledCombined.Deleted.Count == 2 && Directory.GetDirectories(Path.Combine(cancelDaemon.Root, "releases")).Length == 4,
                "Cancellation at the installation boundary prevents the second root from being touched");
        }

        var safeStandalone = Fixture(); var safeDaemon = Fixture(installation: "app-server-daemon");
        var safeCombined = new CodexInstallationsCleaner([safeStandalone.Cleaner, safeDaemon.Cleaner]);
        var safePreview = safeCombined.Scan();
        async Task<bool> RejectCombined(CodexInstallationsReport forged)
        {
            try { await safeCombined.DeleteAsync(forged); return false; }
            catch (InvalidOperationException) { return true; }
        }
        check(await RejectCombined(safePreview with { Installations = [safePreview.Installations[0], safePreview.Installations[1] with { Root = outside }] }),
            "Combined cleanup rejects an unknown installation root before deleting anything");
        var daemonCandidate = safePreview.Installations[1].Releases.First(release => release.CanDelete);
        check(await RejectCombined(safePreview with { Installations = [safePreview.Installations[0], safePreview.Installations[1] with { Releases = [daemonCandidate with { Path = outside }] }] }),
            "A bad path in the second installation is rejected before deleting the first installation");
        check(await RejectCombined(safePreview with { Installations = [safePreview.Installations[0], safePreview.Installations[0]] }) &&
            await RejectCombined(safePreview with { KeepNewest = 1 }),
            "Combined cleanup rejects duplicate roots and inconsistent retention policies");
        check(Directory.GetDirectories(Path.Combine(safeStandalone.Root, "releases")).Length == 4 &&
            Directory.GetDirectories(Path.Combine(safeDaemon.Root, "releases")).Length == 4 && File.ReadAllText(Path.Combine(outside, "keep.txt")) == "preserve",
            "All forged combined plans leave both installations and unrelated user files intact");

        // All links are owned by this test. Remove them without following targets before its parent's teardown.
        RemoveFixtureLinks(sandbox);
    }

    private static string Name(string version, string target = "x86_64-pc-windows-msvc") => version + "-" + target;
    internal static void RemoveFixtureLinks(string sandbox)
    {
        var root = Path.Combine(sandbox, "codex-cleanup");
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var folder))
            foreach (var child in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // This test creates only directory junctions. Remove the link itself,
                    // including a dangling one, before the owning fixture's recursive teardown.
                    if (!RemoveDirectory(child.FullName)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                else if ((child.Attributes & FileAttributes.Directory) != 0) pending.Push(child.FullName);
            }
    }
    [DllImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool RemoveDirectory(string path);
    private sealed class InlineProgress(Action<CodexCleanupProgress> action) : IProgress<CodexCleanupProgress>
    { public void Report(CodexCleanupProgress value) => action(value); }
    private static string Package(string root, string version) => Path.Combine(root, "releases", Name(version));
    private static void AddPackage(string root, string version, string target = "x86_64-pc-windows-msvc")
    {
        var path = Path.Combine(root, "releases", Name(version, target));
        Directory.CreateDirectory(Path.Combine(path, "bin")); Directory.CreateDirectory(Path.Combine(path, "codex-path")); Directory.CreateDirectory(Path.Combine(path, "codex-resources"));
        File.WriteAllBytes(Path.Combine(path, "bin", "codex.exe"), [77, 90, 1, 2, 3]);
        File.WriteAllBytes(Path.Combine(path, "codex-path", "rg.exe"), [77, 90, 4, 5, 6]);
        File.WriteAllText(Path.Combine(path, "codex-package.json"), JsonSerializer.Serialize(new
        { layoutVersion = 1, version, target, variant = "codex", entrypoint = "bin/codex.exe", resourcesDir = "codex-resources", pathDir = "codex-path" }));
    }
}
