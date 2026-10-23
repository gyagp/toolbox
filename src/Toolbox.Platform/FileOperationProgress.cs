using Toolbox.Core;

namespace Toolbox.Platform;

public enum FileOperationStage { Preparing, Working, Finalizing, InspectingLocks, ReleasingLocks, Retrying, Completed, Canceling }

/// <summary>Shell work units are estimates, not bytes or file counts. Null means unknown.</summary>
public sealed record FileOperationProgress(FileOperationStage Stage, string Message, string? CurrentItem = null, double? Percentage = null)
{
    public static string ActionLabel(FileAction action) => action switch
    {
        FileAction.Move => "正在移动",
        FileAction.Recycle => "正在移入回收站",
        FileAction.PermanentDelete => "正在彻底删除",
        _ => "正在处理"
    };

    internal static FileOperationProgress FromShell(FileAction action, string? item, uint total, uint completed)
    {
        if (total == 0 || completed > total)
            return new(FileOperationStage.Working, ActionLabel(action) + " · 正在统计进度…", item);
        // 100% is reserved for success after PerformOperations, HRESULT, abort and
        // source-existence checks. A Shell work estimate alone cannot prove success.
        var percentage = Math.Min(99, Math.Floor(100.0 * completed / total));
        return new(completed == total ? FileOperationStage.Finalizing : FileOperationStage.Working,
            completed == total ? "正在完成并核对结果…" : ActionLabel(action) + "…", item, percentage);
    }
}
