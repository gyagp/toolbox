using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toolbox.Core;
using Toolbox.Platform;
using Toolbox.App.Services;

namespace Toolbox.App.Views;

public sealed partial class FileToolsView
{
    internal async Task VerifyPermanentDeleteUiAsync(string fixture, Func<FrameworkElement, string, Task> render)
    {
        var plan = FileActionPlan.Create(FileAction.PermanentDelete, fixture);
        SourcePath.Text = fixture;
        if (AutoReleaseCheck.IsChecked != true) throw new Exception("Automatic lock recovery must be enabled by default.");
        await RunAsync(async () =>
        {
            if (PermanentDeleteButton.IsEnabled || DeleteButton.IsEnabled || MoveButton.IsEnabled || AutoReleaseCheck.IsEnabled)
                throw new Exception("File actions must be disabled during confirmation.");
            var (dialog, confirmation) = CreatePermanentDeleteDialog(plan);
            if (dialog.IsPrimaryButtonEnabled || dialog.DefaultButton != ContentDialogButton.Close)
                throw new Exception("Permanent deletion must start disabled and default to cancel.");
            var showing = dialog.ShowAsync();
            try
            {
                await Task.Delay(200);
                await render(dialog, "permanent-confirmation.png");
                confirmation.Text = "incorrect-name";
                await Task.Delay(50);
                if (dialog.IsPrimaryButtonEnabled) throw new Exception("An incorrect name enabled permanent deletion.");
                confirmation.Text = Path.GetFileName(fixture);
                await Task.Delay(50);
                if (!dialog.IsPrimaryButtonEnabled) throw new Exception("The exact name did not enable confirmation.");
                confirmation.Text = "";
                await Task.Delay(50);
                if (dialog.IsPrimaryButtonEnabled) throw new Exception("Clearing the name left permanent deletion enabled.");
            }
            finally { dialog.Hide(); }
            if (await showing == ContentDialogResult.Primary) throw new Exception("Dismissal confirmed deletion.");
        });
        // RunAsync catches UI failures for ordinary users; propagate any diagnostic error.
        if (OperationStatus.IsOpen && OperationStatus.Severity == InfoBarSeverity.Error)
            throw new Exception(OperationStatus.Message);
        if (!File.Exists(fixture) || !PermanentDeleteButton.IsEnabled || _pending is not null)
            throw new Exception("Canceling must preserve the file and leave no pending deletion.");
        using (var held = new FileStream(fixture, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Only this fixture is used; bypass its dialog to exercise failure diagnosis.
            await RunAsync(() => ExecuteAsync(plan, permanentDeleteConfirmed: true));
            if (_pending?.Action != FileAction.PermanentDelete || !RetryButton.IsEnabled ||
                !Equals(RetryButton.Content, "重试彻底删除…") || OperationStatus.Severity != InfoBarSeverity.Error ||
                !File.Exists(fixture) || ProcessList.SelectedItems.Count != 0 || TerminateButton.IsEnabled)
                throw new Exception("Permanent-delete failure must diagnose locks, preserve intent and leave processes unselected.");
        }
        await Task.Delay(100);
        if (!ActivityText.Text.Contains("跳过") || !ActivityText.Text.Contains(Environment.ProcessId.ToString()))
            throw new Exception("Automatic recovery must explain why Toolbox's own fixture handle is not terminated.");
        await render(this, "permanent-locks.png");
        // A real retry must enter the typed confirmation path again; cancel it before
        // any file operation. This also checks cancellation dismisses the modal.
        var retry = RunAsync(() => ExecuteAsync(plan));
        await Task.Delay(150);
        if (_dialog is null || _dialog.IsPrimaryButtonEnabled || !IsBusy)
            throw new Exception("Retry must require a fresh, initially disabled permanent-delete confirmation.");
        RequestCancel();
        await retry;
        if (!File.Exists(fixture) || _pending is not null)
            throw new Exception("Canceling retry confirmation must preserve the file and clear the pending action.");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = RunCancellationFixtureAsync(started);
        await started.Task;
        if (!CancelButton.IsEnabled || CancelButton.Visibility != Visibility.Visible)
            throw new Exception("Active file operations must expose an enabled cancel button.");
        var rendered = new List<FileOperationProgress>();
        var buffered = new LatestUiProgress<FileOperationProgress>(DispatcherQueue, rendered.Add);
        for (var index = 0; index < 1000; index++) buffered.Report(new(FileOperationStage.Working, index.ToString(), fixture, index % 99));
        var latest = new FileOperationProgress(FileOperationStage.Working, "last update", fixture, 42);
        buffered.Report(latest);
        await Task.Delay(100);
        if (rendered.Count != 1 || rendered[0] != latest)
            throw new Exception("A burst of native progress must retain the latest snapshot without flooding the UI queue.");
        RenderProgress(new(FileOperationStage.Working, "正在移动…", fixture, 42));
        if (OperationProgressBar.IsIndeterminate || OperationProgressBar.Value != 42 || !ProgressPercent.Text.Contains("42%") || CurrentItemText.Text != fixture)
            throw new Exception("Known progress must show its percentage and current path.");
        await Task.Delay(250); // Let the native progress indicator's visual transition settle.
        await render(this, "file-operation-cancel.png");
        RenderProgress(new(FileOperationStage.Working, "正在统计进度…", fixture));
        if (!OperationProgressBar.IsIndeterminate || ProgressPercent.Text.Contains('%'))
            throw new Exception("Unknown totals must never show a stale percentage.");
        await Task.Delay(200);
        await render(this, "file-progress-unknown.png");
        foreach (var stage in new[] { FileOperationStage.InspectingLocks, FileOperationStage.ReleasingLocks, FileOperationStage.Retrying })
        {
            RenderProgress(new(FileOperationStage.Working, "fixture", fixture, 80));
            RenderProgress(new(stage, "fixture", fixture));
            if (!OperationProgressBar.IsIndeterminate || OperationProgressBar.Value != 0 || ProgressPercent.Text.Contains('%'))
                throw new Exception("Recovery phases must discard the prior percentage.");
        }
        RenderProgress(new(FileOperationStage.Finalizing, "正在完成…", fixture, 100));
        if (OperationProgressBar.Value != 99) throw new Exception("An unverified result must not display 100%.");
        RenderProgress(new(FileOperationStage.Completed, "已完成", fixture, 100));
        if (OperationProgressBar.Value != 100 || OperationProgressBar.IsIndeterminate) throw new Exception("Verified completion must display 100%.");
        RenderProgress(new(FileOperationStage.Working, "正在移动…", fixture, 42));
        Cancel_Click(this, new RoutedEventArgs());
        if (!OperationProgressBar.IsIndeterminate || ProgressPercent.Text != "取消中") throw new Exception("Cancel must clear transfer progress immediately.");
        await running;
        if (IsBusy || _pending is not null || RetryButton.IsEnabled || !PermanentDeleteButton.IsEnabled ||
            !OperationStatus.Title.Contains("已取消") || !File.Exists(fixture) || _elapsedTimer.IsEnabled)
            throw new Exception("Cancellation must clear pending actions, release the UI and preserve its fixture.");
    }

    internal Task RunCancellationFixtureAsync(TaskCompletionSource started)
    {
        return RunAsync(async () =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, _action!.Token);
        });
    }
}
