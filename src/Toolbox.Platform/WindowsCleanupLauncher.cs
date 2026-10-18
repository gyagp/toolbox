using System.Diagnostics;

namespace Toolbox.Platform;

/// <summary>Only interactive Windows review surfaces. Never use unattended cleanup switches.</summary>
public static class WindowsCleanupLauncher
{
    public static void OpenDiskCleanup(string driveRoot)
    {
        var start = CreateDiskCleanupStartInfo(driveRoot);
        if (!File.Exists(start.FileName)) throw new FileNotFoundException("此 Windows 未提供磁盘清理。请使用“Windows 存储设置”。");
        using var process = Process.Start(start);
    }

    public static void OpenStorageSettings()
    {
        using var process = Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });
    }

    internal static ProcessStartInfo CreateDiskCleanupStartInfo(string driveRoot)
    {
        driveRoot = ValidateDriveRoot(driveRoot);
        var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.SystemDirectory,
            // This is an explicitly requested, interactive UI. Never hide it or request elevation.
            WindowStyle = ProcessWindowStyle.Normal
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add(driveRoot[..1]);
        return start;
    }

    internal static string ValidateDriveRoot(string root)
    {
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\')
            throw new ArgumentException("请选择本地磁盘根目录。", nameof(root));
        return root.ToUpperInvariant();
    }
}
