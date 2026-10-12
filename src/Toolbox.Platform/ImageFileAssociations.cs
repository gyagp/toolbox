using Microsoft.Win32;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Toolbox.Core;

[assembly: InternalsVisibleTo("Toolbox.Platform.Tests")]

namespace Toolbox.Platform;

public static class ImageFileAssociations
{
    public static void Register(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("无法找到 Toolbox 可执行文件。", executable);
        WriteRegistration(Registry.CurrentUser, executable);
        SHChangeNotify(0x08000000, 0, nint.Zero, nint.Zero);
    }

    // Only advertises an Open With candidate. Never writes an extension's default
    // value or Windows' protected UserChoice. The user selects defaults in Settings.
    internal static void WriteRegistration(RegistryKey userRoot, string executable)
    {
        const string progId = "Toolbox.Image";
        var command = $"\"{executable}\" \"%1\"";
        void Set(string subkey, string name, object value, RegistryValueKind kind = RegistryValueKind.String)
        {
            using var key = userRoot.CreateSubKey(subkey);
            key.SetValue(name, value, kind);
        }
        var application = @"Software\Classes\Applications\" + Path.GetFileName(executable);
        Set(application, "FriendlyAppName", "Toolbox 图片查看器");
        Set(application + @"\shell\open\command", "", command);
        Set(@"Software\Classes\" + progId, "", "Toolbox 图片");
        Set(@"Software\Classes\" + progId + @"\DefaultIcon", "", $"\"{executable}\",0");
        Set(@"Software\Classes\" + progId + @"\shell\open\command", "", command);
        const string capabilities = @"Software\Toolbox\Capabilities";
        Set(capabilities, "ApplicationName", "Toolbox 图片查看器");
        Set(capabilities, "ApplicationDescription", "浏览图片和 GIF 动画，支持全屏与连续翻图。");
        Set(capabilities, "ApplicationIcon", $"\"{executable}\",0");
        foreach (var extension in ImageFileTypes.Extensions)
        {
            Set(application + @"\SupportedTypes", extension, "");
            Set(@"Software\Classes\" + extension + @"\OpenWithProgids", progId, Array.Empty<byte>(), RegistryValueKind.None);
            Set(capabilities + @"\FileAssociations", extension, progId);
        }
        Set(@"Software\RegisteredApplications", "Toolbox", capabilities);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}

public sealed class ExplorerImageComparer : IComparer<string>
{
    public static ExplorerImageComparer Instance { get; } = new();
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return StringComparer.OrdinalIgnoreCase.Compare(x, y);
        var order = StrCmpLogicalW(Path.GetFileName(x), Path.GetFileName(y));
        return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(x, y);
    }
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string left, string right);
}
