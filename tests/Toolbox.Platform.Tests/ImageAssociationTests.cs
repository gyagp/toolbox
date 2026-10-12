using Microsoft.Win32;
using Toolbox.Core;
using Toolbox.Platform;

internal static class ImageAssociationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var fixture = @"Software\Toolbox.Tests\Associations-" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(fixture);
            using (var extension = root.CreateSubKey(@"Software\Classes\.png")) extension.SetValue("", "Original.Image");
            const string choicePath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.png\UserChoice";
            using (var choice = root.CreateSubKey(choicePath)) choice.SetValue("ProgId", "Original.Image");
            var executable = @"D:\Apps with spaces\工具箱\Toolbox.exe";
            ImageFileAssociations.WriteRegistration(root, executable);
            using (var command = root.OpenSubKey(@"Software\Classes\Toolbox.Image\shell\open\command"))
                check((string?)command?.GetValue("") == $"\"{executable}\" \"%1\"", "Association command quotes executable and image paths");
            using (var extension = root.OpenSubKey(@"Software\Classes\.png"))
                check((string?)extension?.GetValue("") == "Original.Image", "Registration preserves the existing extension default");
            using (var choice = root.OpenSubKey(choicePath))
                check((string?)choice?.GetValue("ProgId") == "Original.Image", "Registration never changes the user's default application choice");
            using (var types = root.OpenSubKey(@"Software\Toolbox\Capabilities\FileAssociations"))
                check(ImageFileTypes.Extensions.All(type => (string?)types?.GetValue(type) == "Toolbox.Image"), "All supported image types are advertised in Windows settings");
            ImageFileAssociations.WriteRegistration(root, @"E:\Moved\Toolbox.exe");
            using (var command = root.OpenSubKey(@"Software\Classes\Applications\Toolbox.exe\shell\open\command"))
                check((string?)command?.GetValue("") == "\"E:\\Moved\\Toolbox.exe\" \"%1\"", "Registering again updates the portable app location");
            var ordered = new[] { "10.png", "2.png", "1.png" }.Order(ExplorerImageComparer.Instance);
            check(ordered.SequenceEqual(new[] { "1.png", "2.png", "10.png" }), "Series use Explorer's numeric filename order");
        }
        finally
        {
            if (!fixture.StartsWith(@"Software\Toolbox.Tests\Associations-", StringComparison.Ordinal)) throw new Exception("Unexpected registry fixture");
            Registry.CurrentUser.DeleteSubKeyTree(fixture, throwOnMissingSubKey: false);
        }
    }
}
