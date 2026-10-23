using System.Diagnostics;
using Toolbox.Core;
using Toolbox.Platform;

internal static class AutomaticRecoveryTests
{
    internal static async Task RunAsync(Action<bool, string> check, string sandbox, Func<string, bool, Task<Process>> hold)
    {
        var root = Path.Combine(sandbox, "automatic-recovery");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(destination);
        var unrelated = Path.Combine(root, "unrelated.txt");
        File.WriteAllText(unrelated, "leave this process alone");
        var other = await hold(unrelated, false);
        foreach (var action in new[] { FileAction.Move, FileAction.Recycle, FileAction.PermanentDelete })
        {
            var file = Path.Combine(root, action + ".txt");
            File.WriteAllText(file, "original payload");
            var owner = await hold(file, false);
            var log = new List<string>();
            var result = await FileOperationRecovery.ExecuteAsync(FileActionPlan.Create(action, file, destination), true,
                activity: new Activity(log.Add));
            check(result.Succeeded && result.Retries == 1 && owner.HasExited && !File.Exists(file),
                "Automatic " + action + " releases its verified owner and retries the same action");
            check(!other.HasExited && File.ReadAllText(unrelated) == "leave this process alone",
                "Automatic " + action + " preserves the unrelated locked file and process");
            check(log.Any(line => line.Contains("已自动结束") && line.Contains(owner.Id.ToString())) && log.Any(line => line.Contains("自动重试 1/2")),
                "Automatic recovery records the exact terminated PID and retry");
            if (action == FileAction.Move)
                check(File.ReadAllText(Path.Combine(destination, Path.GetFileName(file))) == "original payload", "Automatic move preserves file bytes");
        }
        var empty = Path.Combine(root, "empty-directory");
        Directory.CreateDirectory(empty);
        var directoryOwner = await hold(empty, true);
        var directoryResult = await FileOperationRecovery.ExecuteAsync(FileActionPlan.Create(FileAction.Move, empty, destination), true);
        check(directoryResult.Succeeded && directoryOwner.HasExited && Directory.Exists(Path.Combine(destination, "empty-directory")),
            "Automatic recovery releases a terminal working directory and retries the empty-folder move");

        var ownerInfo = LockInspector.Inspect(unrelated).Processes.Single(p => p.Id == other.Id);
        var freeFile = Path.Combine(root, "free.txt");
        File.WriteAllText(freeFile, "free fixture");
        check(!await LockInspector.TerminateIfStillUsingAsync(ownerInfo, freeFile, default) && !other.HasExited,
            "Out-of-scope lock resources cannot cause automatic termination");
        check(!await LockInspector.TerminateIfStillUsingAsync(ownerInfo with { Files = [freeFile] }, freeFile, default) && !other.HasExited,
            "A fresh owner query rejects stale ownership even when the PID still exists");
        check(!await LockInspector.TerminateIfStillUsingAsync(ownerInfo with { StartTime = ownerInfo.StartTime - 1 }, unrelated, default) && !other.HasExited,
            "Automatic termination rejects a stale process creation time");
        var link = Path.Combine(root, "linked-destination");
        DiskCleanupTests.CreateJunction(link, root);
        try
        {
            check(!await LockInspector.TerminateIfStillUsingAsync(ownerInfo with { Files = [Path.Combine(link, "unrelated.txt")] }, link, default) && !other.HasExited,
                "Automatic ownership refresh never follows a replacement junction");
        }
        finally { Directory.Delete(link); }
        var disabled = await FileOperationRecovery.ExecuteAsync(FileActionPlan.Create(FileAction.Move, unrelated, destination), false);
        check(!disabled.Succeeded && disabled.Retries == 0 && !other.HasExited, "Disabling automatic recovery leaves blockers alive for manual handling");
        var canceled = false;
        try { await FileOperationRecovery.ExecuteAsync(FileActionPlan.Create(FileAction.Move, unrelated, destination), true, cancellation: new CancellationToken(true)); }
        catch (OperationCanceledException) { canceled = true; }
        check(canceled && !other.HasExited && File.Exists(unrelated), "Cancellation before recovery never ends a process or moves a file");
        await VerifyPolicyAsync(check, freeFile, destination);
    }

    private static async Task VerifyPolicyAsync(Action<bool, string> check, string file, string destination)
    {
        var plan = FileActionPlan.Create(FileAction.PermanentDelete, file);
        var process = new LockingProcess(500_000, 123456, "isolated policy fixture", "", [file]);
        static IOException Failure(uint code) => new("Isolated policy failure", unchecked((int)code));
        var executions = 0; var scans = 0; var releases = 0;
        Task Execute(CancellationToken _) { executions++; return Task.FromException(Failure(0x80070020)); }
        Task<LockReport> Inspect(CancellationToken _) { scans++; return Task.FromResult(new LockReport([process with { Id = process.Id + scans }], 1, [])); }
        Task<bool> Release(LockingProcess _, CancellationToken __) { releases++; return Task.FromResult(true); }
        var bounded = await FileOperationRecovery.ExecuteCoreAsync(plan, true, default, Execute, Inspect, Release);
        check(!bounded.Succeeded && bounded.Retries == 2 && executions == 3 && releases == 2, "Recovery stops after two rounds even when new owners keep appearing");
        check(File.ReadAllText(file) == "free fixture", "Policy simulations never delete their source");
        foreach (var code in new uint[] { 0x80070005, 0x80270028, 0x800700B7 })
        {
            releases = 0;
            var result = await FileOperationRecovery.ExecuteCoreAsync(plan, true, default, _ => Task.FromException(Failure(code)), Inspect, Release);
            check(!result.Succeeded && result.Retries == 0 && releases == 0, $"Non-source-lock error 0x{code:X8} never terminates an owner");
        }
        foreach (var protectedOwner in new[] { process with { Id = Environment.ProcessId }, process with { Id = 4 },
            process with { StartTime = 0 }, process with { ServiceName = "test-service" } })
        {
            releases = 0;
            var result = await FileOperationRecovery.ExecuteCoreAsync(plan, true, default, Execute,
                _ => Task.FromResult(new LockReport([protectedOwner], 1, [])), Release);
            check(!result.Succeeded && releases == 0, "Automatic recovery skips protected owner: " + protectedOwner.Id + "/" + protectedOwner.StartTime + "/" + protectedOwner.ServiceName);
        }
        releases = 0;
        using var cancellation = new CancellationTokenSource();
        var wasCanceled = false;
        try
        {
            await FileOperationRecovery.ExecuteCoreAsync(plan, true, cancellation.Token, Execute, _ =>
            { cancellation.Cancel(); return Task.FromResult(new LockReport([process], 1, [])); }, Release);
        }
        catch (OperationCanceledException) { wasCanceled = true; }
        check(wasCanceled && releases == 0, "Cancellation after inspection prevents process termination and retry");
        releases = 0;
        var move = FileActionPlan.Create(FileAction.Move, file, destination);
        var collision = await FileOperationRecovery.ExecuteCoreAsync(move, true, default, Execute, _ =>
        {
            File.WriteAllText(move.Destination!, "existing destination");
            return Task.FromResult(new LockReport([process], 1, []));
        }, Release);
        check(!collision.Succeeded && collision.Retries == 0 && releases == 0 && File.ReadAllText(move.Destination!) == "existing destination",
            "A destination created by a partial move blocks retry before terminating any process");
        releases = 0; executions = 0;
        var sameOwner = await FileOperationRecovery.ExecuteCoreAsync(plan, true, default, Execute,
            _ => Task.FromResult(new LockReport([process], 1, [])), (_, _) =>
            { releases++; return Task.FromException<bool>(new InvalidOperationException("protected fixture")); });
        check(!sameOwner.Succeeded && releases == 1 && executions == 2, "A failed termination is recorded and the same process is not repeatedly killed");
    }

    private sealed class Activity(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
