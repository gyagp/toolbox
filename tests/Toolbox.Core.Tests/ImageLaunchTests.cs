using Toolbox.Core;

internal static class ImageLaunchTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(ImageLaunchRequest.Parse([]) is null, "Ordinary launch retains the tool overview");
        var path = Path.Combine(Path.GetTempPath(), "相册 空格", "第 2 张.JPEG");
        var request = ImageLaunchRequest.Parse([path]);
        check(request is { FullScreen: false } && request.Path == path, "Open With preserves spaces, Unicode, and uppercase extensions");
        check(ImageLaunchRequest.Parse(["--view", path, "--fullscreen"]) is { FullScreen: true }, "Image launch accepts explicit fullscreen");
        check(Path.IsPathFullyQualified(ImageLaunchRequest.Parse(["picture.gif"])!.Path), "Relative image arguments resolve against the working directory");
        foreach (var args in new[] { new[] { "--fullscreen" }, new[] { "--unknown" }, new[] { "document.txt" }, new[] { path, path } })
        {
            var rejected = false;
            try { ImageLaunchRequest.Parse(args); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Reject invalid image activation: " + string.Join(" ", args));
        }
    }
}
