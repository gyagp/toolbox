namespace Toolbox.Core;

public enum FileAction { Move, Recycle }

/// <summary>Validated, immutable intent. Execution revalidates it immediately before use.</summary>
public sealed record FileActionPlan(FileAction Action, string Source, string? DestinationFolder)
{
    public string? Destination => DestinationFolder is null ? null : Path.Combine(DestinationFolder, Path.GetFileName(Source));

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择文件或文件夹。");
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException("请输入完整的绝对路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    public static FileActionPlan Create(FileAction action, string source, string? destinationFolder = null)
    {
        source = Normalize(source);
        if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException("源文件或文件夹不存在，或当前账户无法访问。", source);
        if (string.Equals(source, Path.GetPathRoot(source), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能移动或删除整个磁盘或共享根目录。");
        if (action == FileAction.Recycle) return new(action, source, null);
        var folder = Normalize(destinationFolder ?? "");
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("请选择已存在的目标文件夹。");
        var destination = Path.Combine(folder, Path.GetFileName(source));
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("文件已经位于该目标文件夹。");
        if (Directory.Exists(source) && (string.Equals(source, folder, StringComparison.OrdinalIgnoreCase) ||
            folder.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("不能把文件夹移动到自身或其子文件夹中。");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("目标位置已有同名文件或文件夹，请先处理重名项；不会自动覆盖或合并。");
        return new(action, source, folder);
    }

    public FileActionPlan Revalidate() => Create(Action, Source, DestinationFolder);
}
