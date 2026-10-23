using Toolbox.Core;

namespace Toolbox.Platform;

public sealed record FileRecoveryResult(Exception? Error, LockReport? Locks, int Retries)
{
    public bool Succeeded => Error is null;
}

/// <summary>Retry the same confirmed operation, only releasing verified source-file owners.</summary>
public static class FileOperationRecovery
{
    public const int MaximumRetries = 2;

    public static Task<FileRecoveryResult> ExecuteAsync(FileActionPlan plan, bool automatic, nint owner = default,
        CancellationToken cancellation = default, IProgress<string>? activity = null, IProgress<string>? progress = null) =>
        ExecuteCoreAsync(plan, automatic, cancellation,
            token => ShellFileOperation.ExecuteAsync(plan, owner, token),
            token => Task.Run(() => LockInspector.Inspect(plan.Source, token,
                new ScanProgress(count => progress?.Report($"正在查找占用，已检查 {count:N0} 个文件…"))), token),
            (process, token) => Task.Run(() => LockInspector.TerminateIfStillUsingAsync(process, plan.Source, token), token),
            activity, progress);

    internal static async Task<FileRecoveryResult> ExecuteCoreAsync(FileActionPlan plan, bool automatic, CancellationToken cancellation,
        Func<CancellationToken, Task> execute, Func<CancellationToken, Task<LockReport>> inspect,
        Func<LockingProcess, CancellationToken, Task<bool>> release,
        IProgress<string>? activity = null, IProgress<string>? progress = null)
    {
        var attempted = new HashSet<(int, long)>();
        for (var retry = 0; ; retry++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(retry == 0 ? "正在执行文件操作…" : $"正在自动重试 {retry}/{MaximumRetries}…");
            Exception failure;
            try
            {
                await execute(cancellation);
                if (retry > 0) activity?.Report("自动重试成功，原操作已完成。");
                return new(null, null, retry);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { failure = error; }
            cancellation.ThrowIfCancellationRequested();
            activity?.Report($"操作未完成：{failure.Message}");
            progress?.Report("正在查找占用…");
            LockReport report;
            try { report = await inspect(cancellation); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                activity?.Report("占用查询未完成：" + error.Message);
                return new(failure, null, retry);
            }
            cancellation.ThrowIfCancellationRequested();
            if (!automatic) return new(failure, report, retry);
            // Access denied, destination-in-use and collisions do not prove a source lock.
            if (!IsSourceSharingFailure(failure))
            {
                activity?.Report("未自动结束进程：Windows 未明确报告来源文件被占用。");
                return new(failure, report, retry);
            }
            if (retry == MaximumRetries)
            {
                activity?.Report("已达到自动重试上限，停止自动处理。");
                return new(failure, report, retry);
            }
            var hadCandidate = false;
            foreach (var process in report.Processes.DistinctBy(process => (process.Id, process.StartTime)))
            {
                cancellation.ThrowIfCancellationRequested();
                var reason = AutomaticSkipReason(process);
                if (reason is not null) { activity?.Report($"跳过 {process.Label}：{reason}"); continue; }
                if (!attempted.Add((process.Id, process.StartTime)))
                { activity?.Report($"跳过 {process.Label}：本次操作已尝试处理过该进程。"); continue; }
                // Partial moves may have created the destination. Do not kill anything
                // when retry would overwrite or merge content and therefore be rejected.
                try { plan.Revalidate(); }
                catch (Exception error)
                {
                    activity?.Report("原操作已无法安全重试，未继续结束进程：" + error.Message);
                    return new(failure, report, retry);
                }
                hadCandidate = true;
                progress?.Report("正在核对并处理占用：" + process.Label);
                try
                {
                    var terminated = await release(process, cancellation);
                    activity?.Report(terminated ? $"已自动结束 {process.Label}" : $"{process.Label} 已退出或不再占用，未结束它。");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { activity?.Report($"未能自动结束 {process.Label}：{error.Message}"); }
            }
            cancellation.ThrowIfCancellationRequested();
            if (!hadCandidate)
            {
                activity?.Report("没有可自动处理的占用进程，停止自动重试。");
                return new(failure, report, retry);
            }
            activity?.Report($"自动重试 {retry + 1}/{MaximumRetries}：{plan.Source}");
            await Task.Delay(150, cancellation); // Let kernel references drain after exit.
        }
    }

    internal static bool IsSourceSharingFailure(Exception error) =>
        (uint)error.HResult is 0x80070020 or 0x80070021 or 0x80270027;

    internal static string? AutomaticSkipReason(LockingProcess process) =>
        process.Id <= 4 || process.Id == Environment.ProcessId ? "系统进程或 Toolbox 自身。" :
        process.StartTime == 0 ? "无法核对进程身份。" :
        !string.IsNullOrEmpty(process.ServiceName) ? "不自动结束 Windows 服务。" : null;

    private sealed class ScanProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
