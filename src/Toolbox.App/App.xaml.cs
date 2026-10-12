using Microsoft.UI.Xaml;

namespace Toolbox.App;

public partial class App : Application
{
    private Window? _window;
    public App() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        var testIndex = Array.IndexOf(arguments, "--smoke-test");
        var startupOnly = false;
        if (testIndex < 0) { testIndex = Array.IndexOf(arguments, "--startup-test"); startupOnly = testIndex >= 0; }
        var libraryOnly = false;
        if (testIndex < 0) { testIndex = Array.IndexOf(arguments, "--library-test"); libraryOnly = testIndex >= 0; }
        var imageOnly = false;
        if (testIndex < 0) { testIndex = Array.IndexOf(arguments, "--image-test"); imageOnly = testIndex >= 0; }
        if (testIndex < 0)
        {
            var main = new MainWindow();
            _window = main;
            main.Activate();
            await main.OpenImageArgumentsAsync(arguments.Skip(1));
            return;
        }
        var output = testIndex + 1 < arguments.Length ? Path.GetFullPath(arguments[testIndex + 1]) : Path.GetFullPath("artifacts/smoke");
        Directory.CreateDirectory(output);
        try
        {
            var main = new MainWindow(new Toolbox.Core.CaptureLibrary(Path.Combine(output, "managed-images"), Path.Combine(output, "managed-videos")));
            _window = main;
            main.Activate();
            if (startupOnly) await Task.Delay(500);
            else if (imageOnly) await main.RunImageTestAsync(output, testIndex + 2 < arguments.Length ? Path.GetFullPath(arguments[testIndex + 2]) : Path.GetFullPath("tests/fixtures/images"));
            else if (libraryOnly) await main.RunLibraryTestAsync(output);
            else await main.RunSmokeTestAsync(output);
            File.WriteAllText(Path.Combine(output, "result.txt"), startupOnly ? "PASS: packaged WinUI startup and resources.\n" : imageOnly ? "PASS: image formats, GIF playback, fullscreen, keyboard navigation, file activation, corrupt files, and handle release.\n" : libraryOnly ? "PASS: library discovery, filtering, image preview, video playback/no autoplay, rename, delete, restore, file watching, empty state.\n" : "PASS: capture regression and content library integration.\n");
            Environment.ExitCode = 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), error.ToString());
            Environment.ExitCode = 1;
        }
        finally { if (_window is not null) _window.Close(); else Exit(); }
    }
}
