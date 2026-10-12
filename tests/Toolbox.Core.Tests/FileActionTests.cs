using Toolbox.Core;

internal static class FileActionTests
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "Toolbox-plan-tests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        var file = Path.Combine(source, "data.txt"); File.WriteAllText(file, "keep");
        void Reject(Action action, string message)
        {
            var rejected = false;
            try { action(); } catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException) { rejected = true; }
            check(rejected && File.ReadAllText(file) == "keep", message);
        }
        try
        {
            var plan = FileActionPlan.Create(FileAction.Move, file, destination);
            check(plan.Destination == Path.Combine(destination, "data.txt") && File.Exists(file), "Planning a move has no side effects");
            Reject(() => FileActionPlan.Create(FileAction.Move, file, source), "Reject moving to the same location");
            Reject(() => FileActionPlan.Create(FileAction.Move, source, source), "Reject moving a directory into itself");
            Directory.CreateDirectory(Path.Combine(source, "child"));
            Reject(() => FileActionPlan.Create(FileAction.Move, source, Path.Combine(source, "child")), "Reject moving a directory into its descendants");
            Reject(() => FileActionPlan.Create(FileAction.Recycle, Path.GetPathRoot(root)!), "Reject recycling a drive root");
            Reject(() => FileActionPlan.Create(FileAction.Move, file, Path.Combine(root, "missing")), "Reject a missing destination");
            Reject(() => FileActionPlan.Create(FileAction.Recycle, Path.Combine(root, "missing")), "Reject a missing source");
            File.WriteAllText(plan.Destination!, "existing");
            Reject(() => plan.Revalidate(), "Revalidation catches a new collision before retrying");
            check(File.ReadAllText(plan.Destination!) == "existing", "A collision never overwrites destination contents");
            check(FileActionPlan.Create(FileAction.Recycle, "\"" + file + "\"").Source == file, "Quoted absolute paths are accepted");
            Reject(() => FileActionPlan.Normalize("relative.txt"), "Reject ambiguous relative paths");
        }
        finally
        {
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new Exception("Unexpected fixture path");
            Directory.Delete(root, recursive: true);
        }
    }
}
