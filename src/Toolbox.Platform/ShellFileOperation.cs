using System.Runtime.InteropServices;
using Toolbox.Core;

namespace Toolbox.Platform;

/// <summary>Use Explorer's move/recycle implementation, including cross-volume folders.</summary>
public static class ShellFileOperation
{
    public static Task ExecuteAsync(FileActionPlan plan, nint owner = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Execute(plan.Revalidate(), owner); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "Toolbox file operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Execute(FileActionPlan plan, nint owner)
    {
        IFileOperation? operation = null;
        IShellItem? source = null, destination = null;
        uint? subscription = null;
        var progress = new OperationProgress();
        try
        {
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            operation.SetOwnerWindow(owner);
            operation.Advise(progress, out var cookie);
            subscription = cookie;
            // On errors return to Toolbox for lock diagnosis. Never silently overwrite
            // collisions. A non-recyclable item requires an additional Windows warning.
            const uint silent = 0x0004, allowUndo = 0x0040, noErrorUi = 0x0400, earlyFailure = 0x00100000;
            const uint recycle = 0x00080000, wantNukeWarning = 0x4000, noConfirmation = 0x0010;
            operation.SetOperationFlags(silent | allowUndo | noErrorUi | earlyFailure |
                (plan.Action == FileAction.Recycle ? recycle | wantNukeWarning | noConfirmation : 0));
            source = Item(plan.Source);
            if (plan.Action == FileAction.Move)
            {
                destination = Item(plan.DestinationFolder!);
                operation.MoveItem(source, destination, null, nint.Zero);
            }
            else operation.DeleteItem(source, nint.Zero);
            try { operation.PerformOperations(); }
            catch (COMException error) { throw Failure(plan, progress.FailureCode ?? error.HResult); }
            if (progress.FailureCode is { } failure) throw Failure(plan, failure);
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted) throw Failure(plan, unchecked((int)0x80004004));
            if (File.Exists(plan.Source) || Directory.Exists(plan.Source))
                throw new IOException("源路径仍然存在，操作尚未全部完成。请检查源与目标内容后重试。");
        }
        finally
        {
            if (operation is not null && subscription is { } cookie)
            {
                try { operation.Unadvise(cookie); }
                catch (COMException) { } // Preserve the original operation failure.
            }
            if (destination is not null) Marshal.FinalReleaseComObject(destination);
            if (source is not null) Marshal.FinalReleaseComObject(source);
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
        }
    }

    private static IOException Failure(FileActionPlan plan, int code)
    {
        var reason = (uint)code switch
        {
            0x80070020 or 0x80070021 or 0x80270027 => "源文件或文件夹正在被其他进程使用。终端的当前工作目录也会阻止移动。",
            0x80270028 => "目标位置正在被其他进程使用。",
            0x80070005 or 0x80270021 => "没有访问或修改源路径的权限。",
            0x80270022 => "没有写入目标位置的权限。",
            0x8027003F => "文件被设为只读，无法修改。",
            0x80004004 or 0x800704C7 or 0x80270000 or 0x80270001 => "操作被取消或尚未全部完成。",
            _ => Marshal.GetExceptionForHR(code)?.Message ?? "Windows 文件操作失败。"
        };
        return new IOException($"{reason}\nWindows 错误：0x{code:X8}\n源：{plan.Source}" +
            (plan.Destination is null ? "" : $"\n目标：{plan.Destination}"), code);
    }

    private static IShellItem Item(string path)
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, nint.Zero, ref iid, out var item));
        return item;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid iid, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem { }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOperationProgress
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem? item, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem? item, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem? item, IShellItem? destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem? item, IShellItem? destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem? item, IShellItem? destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem? item, IShellItem? destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem? item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem? item, int result, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem? destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem? destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class OperationProgress : IOperationProgress
    {
        internal int? FailureCode { get; private set; }
        private int Record(int result) { if (result < 0) FailureCode ??= result; return 0; }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => Record(result);
        public int PreRenameItem(uint flags, IShellItem? item, string? name) => 0;
        public int PostRenameItem(uint flags, IShellItem? item, string? name, int result, IShellItem? created) => Record(result);
        public int PreMoveItem(uint flags, IShellItem? item, IShellItem? destination, string? name) => 0;
        public int PostMoveItem(uint flags, IShellItem? item, IShellItem? destination, string? name, int result, IShellItem? created) => Record(result);
        public int PreCopyItem(uint flags, IShellItem? item, IShellItem? destination, string? name) => 0;
        public int PostCopyItem(uint flags, IShellItem? item, IShellItem? destination, string? name, int result, IShellItem? created) => Record(result);
        public int PreDeleteItem(uint flags, IShellItem? item) => 0;
        public int PostDeleteItem(uint flags, IShellItem? item, int result, IShellItem? created) => Record(result);
        public int PreNewItem(uint flags, IShellItem? destination, string? name) => 0;
        public int PostNewItem(uint flags, IShellItem? destination, string? name, string? template, uint attributes, int result, IShellItem? created) => Record(result);
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    // Keep the native vtable order, including methods not called here.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IOperationProgress sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint properties);
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(nint items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        void RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, nint sink);
        void MoveItems(nint items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, nint sink);
        void CopyItems(nint items, IShellItem destination);
        void DeleteItem(IShellItem item, nint sink);
        void DeleteItems(nint items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, nint sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
