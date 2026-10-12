using System.Diagnostics;
using System.Reflection;
using Toolbox.Core;
using Toolbox.Platform;

if (args.Length == 2 && args[0] == "--inspect")
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(LockInspector.Inspect(args[1]), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Length == 3 && args[0] == "--hold-directory")
{
    Environment.CurrentDirectory = args[1];
    File.WriteAllText(args[2], Environment.ProcessId.ToString());
    Thread.Sleep(Timeout.Infinite);
    return;
}

if (args.Length == 3 && args[0] == "--hold-file")
{
    using var held = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read);
    File.WriteAllText(args[2], Environment.ProcessId.ToString());
    Thread.Sleep(Timeout.Infinite);
    return;
}

var count = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    count++;
    Console.WriteLine("PASS: " + message);
}

var root = Path.Combine(Path.GetTempPath(), "Toolbox-file-tests-" + Guid.NewGuid().ToString("N"));
var source = Path.Combine(root, "source");
var target = Path.Combine(root, "destination");
Directory.CreateDirectory(source); Directory.CreateDirectory(target);
var children = new List<Process>();
async Task<Process> HoldAsync(string path, bool directory = false)
{
    var ready = path + ".ready";
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(directory ? "--hold-directory" : "--hold-file"); start.ArgumentList.Add(path); start.ArgumentList.Add(ready);
    var child = Process.Start(start)!; children.Add(child);
    for (var attempt = 0; attempt < 100 && !File.Exists(ready); attempt++) await Task.Delay(50);
    if (!File.Exists(ready)) throw new Exception("Fixture process failed to acquire file lock");
    return child;
}

try
{
    var file = Path.Combine(source, "锁定文件.txt");
    var second = Path.Combine(source, "保留进程.txt");
    File.WriteAllText(file, "original bytes"); File.WriteAllText(second, "unselected process");
    var child = await HoldAsync(file);
    var other = await HoldAsync(second);
    var report = LockInspector.Inspect(source);
    Check(report.Warnings.Count == 0, "Folder lock scan completed without errors");
    var owner = report.Processes.Single(p => p.Id == child.Id);
    Check(owner.Files.SequenceEqual(new[] { file }), "Restart Manager identifies the exact locked file and PID");
    Check(report.Processes.Any(p => p.Id == other.Id && p.Files.Contains(second)), "Folder scan finds multiple independent owners");
    Check(owner.StartTime == child.StartTime.ToUniversalTime().ToFileTimeUtc(), "Native FILETIME layout matches process creation time");
    var blocked = false;
    try { await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.Move, file, target)); }
    catch (Exception) { blocked = true; }
    Check(blocked && File.Exists(file) && !File.Exists(Path.Combine(target, Path.GetFileName(file))), "Locked move fails without losing the source");
    var staleRejected = false;
    try { await LockInspector.TerminateAsync(owner with { StartTime = owner.StartTime - 1 }); }
    catch (InvalidOperationException) { staleRejected = true; }
    Check(staleRejected && !child.HasExited, "Stale process identity cannot terminate a reused PID");
    var selfRejected = false;
    try { await LockInspector.TerminateAsync(owner with { Id = Environment.ProcessId }); }
    catch (InvalidOperationException) { selfRejected = true; }
    Check(selfRejected, "Toolbox cannot terminate itself");
    await LockInspector.TerminateAsync(owner);
    Check(child.HasExited && !other.HasExited, "Only the explicitly selected fixture process is terminated");
    Check(LockInspector.Inspect(file).Processes.Count == 0, "Selected process exit releases its file lock");
    await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.Move, file, target));
    var moved = Path.Combine(target, Path.GetFileName(file));
    Check(!File.Exists(file) && File.ReadAllText(moved) == "original bytes", "Move retry preserves file contents");
    await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.Recycle, moved));
    Check(!File.Exists(moved), "Recycle operation completes for the isolated fixture");

    var nested = Path.Combine(root, "nested", "child");
    Directory.CreateDirectory(nested); File.WriteAllText(Path.Combine(nested, "data.txt"), "nested content");
    await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.Move, Path.GetDirectoryName(nested)!, target));
    var movedFolder = Path.Combine(target, "nested");
    Check(File.ReadAllText(Path.Combine(movedFolder, "child", "data.txt")) == "nested content", "Folder move preserves nested contents");
    await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.Recycle, movedFolder));
    Check(!Directory.Exists(movedFolder), "Folder recycling completes for the isolated fixture");
    var emptyDirectory = Path.Combine(source, "空目录");
    Directory.CreateDirectory(emptyDirectory);
    var directoryHolder = await HoldAsync(emptyDirectory, directory: true);
    var directoryReport = LockInspector.Inspect(emptyDirectory);
    Check(directoryReport.FilesScanned == 0 && directoryReport.DirectoriesScanned == 1, "An empty directory is queried even when it contains no files");
    var directoryOwner = directoryReport.Processes.Single(p => p.Id == directoryHolder.Id);
    Check(directoryOwner.Files.SequenceEqual(new[] { emptyDirectory }) && directoryOwner.StartTime > 0, "Find the process whose working directory prevents moving an empty folder");
    Check(directoryReport.Processes.All(p => p.Id != Environment.ProcessId), "The temporary directory query handle is not reported as a blocker");
    var recursive = LockInspector.Inspect(source);
    Check(recursive.Processes.Any(p => p.Id == directoryHolder.Id && p.Files.Contains(emptyDirectory)), "Directory handles in nested folders are detected");
    IOException? moveFailure = null;
    try { await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.Move, emptyDirectory, target)); }
    catch (IOException error) { moveFailure = error; }
    Check(moveFailure is not null && ((uint)moveFailure.HResult is 0x80070020 or 0x80070021 or 0x80270027), "Failed directory move exposes the actual Windows sharing-violation code");
    Check(Directory.Exists(emptyDirectory) && !Directory.Exists(Path.Combine(target, "空目录")), "Failed directory move preserves source and does not create a destination");
    await LockInspector.TerminateAsync(directoryOwner);
    Check(!other.HasExited, "Releasing a directory lock leaves unselected processes running");
    await ShellFileOperation.ExecuteAsync(FileActionPlan.Create(FileAction.Move, emptyDirectory, target));
    Check(Directory.Exists(Path.Combine(target, "空目录")) && !Directory.Exists(emptyDirectory), "Moving an empty directory succeeds after its selected owner exits");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    var canceled = false;
    try { LockInspector.Inspect(source, cancelled.Token); } catch (OperationCanceledException) { canceled = true; }
    Check(canceled, "Large folder scans support cancellation");
    Console.WriteLine($"PASS: {count} Windows integration assertions.");
}
finally
{
    // Terminate only helper processes spawned by this test, never a discovered user process.
    foreach (var child in children)
    {
        if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
        child.Dispose();
    }
    if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new Exception("Unexpected fixture path");
    // Process exit is signaled before every kernel file reference necessarily drains.
    for (var attempt = 0; ; attempt++)
    {
        try { Directory.Delete(root, recursive: true); break; }
        catch (IOException) when (attempt < 20) { await Task.Delay(100); }
    }
}
