using Toolbox.Core;

var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}
var rng = new Random(2817);
var data = new byte[180 * 1400 * 4];
rng.NextBytes(data);
var document = new PixelFrame(180, 1400, data);
var stitcher = new ScrollStitcher();
Check(stitcher.Add(document.Crop(0, 0, 180, 400)).Status == StitchStatus.Added, "First frame");
Check(stitcher.Add(document.Crop(0, 0, 180, 400)).Status == StitchStatus.Unchanged, "Duplicate frame");
foreach (var y in new[] { 117, 294, 501, 740, 1000 })
    Check(stitcher.Add(document.Crop(0, y, 180, 400)).Status == StitchStatus.Added, $"Offset {y}");
Check(stitcher.Build().Pixels.SequenceEqual(data), "Pixel-exact reconstruction across multiple scrolls");
Check(stitcher.Add(document.Crop(0, 850, 180, 400)).Status == StitchStatus.NoMatch, "Reject upward scroll");
var unrelated = new byte[180 * 400 * 4];
rng.NextBytes(unrelated);
Check(stitcher.Add(new(180, 400, unrelated)).Status == StitchStatus.NoMatch, "Reject unrelated frame");
Check(stitcher.Height == 1400, "Rejected frame must not mutate result");
var blank = new ScrollStitcher();
Check(blank.Add(new(100, 200, new byte[100 * 200 * 4])).Status == StitchStatus.Added, "Blank first frame");
Check(blank.Add(new(100, 200, new byte[100 * 200 * 4])).Status == StitchStatus.Unchanged, "Blank duplicate");
var tooTall = new ScrollStitcher();
Check(tooTall.Add(new(1, ScrollStitcher.MaxHeight + 1, new byte[(ScrollStitcher.MaxHeight + 1) * 4])).Status == StitchStatus.LimitReached, "Height guard");
var badSize = false;
try { _ = new PixelFrame(10, 10, new byte[3]); } catch (ArgumentException) { badSize = true; }
Check(badSize, "Invalid image buffer rejected");
var desktop = PixelRect.Union([new(-1920, -200, 1920, 1080), new(0, 0, 2560, 1440)]);
Check(desktop == new PixelRect(-1920, -200, 4480, 1640), "Virtual desktop with negative monitor coordinates");
Check(CaptureGeometry.FromDrag(new(98.5, 85.5), new(10.25, 4.25), 100, 100) == new PixelRect(10, 4, 89, 82), "Reverse drag and fractional DPI rounding");
Check(CaptureGeometry.FromDrag(new(-15, -2), new(120, 130), 100, 100) == new PixelRect(0, 0, 100, 100), "Clamp selection at desktop edges");
Check(new PixelRect(-20, -10, 40, 30).Intersect(new(0, 0, 100, 100)) == new PixelRect(0, 0, 20, 20), "Clip partially offscreen window outline");
Check(!new PixelRect(0, 0, 10, 10).Contains(new(10, 5)), "Shared edge belongs to adjacent window");
var opaque = new PixelFrame(40, 40, Enumerable.Repeat((byte)255, 40 * 40 * 4).ToArray());
var polygon = CaptureGeometry.CutPolygon(opaque, [new(5, 5), new(35, 5), new(5, 35)]);
Check(polygon is not null && polygon.Value.Bounds == new PixelRect(5, 5, 30, 30), "Freeform output bounds");
Check(polygon!.Value.Frame.Pixels[3] == 255, "Freeform interior preserves pixels");
Check(polygon.Value.Frame.Pixels[(29 * 30 + 29) * 4 + 3] == 0, "Freeform exterior is transparent");
var concave = CaptureGeometry.CutPolygon(opaque, [new(0, 0), new(30, 0), new(30, 10), new(10, 10), new(10, 30), new(0, 30)]);
Check(concave is not null && concave.Value.Frame.Pixels[(20 * 30 + 20) * 4 + 3] == 0, "Concave lasso cutout");
Check(CaptureGeometry.CutPolygon(opaque, [new(0, 0), new(10, 10), new(20, 20)]) is null, "Degenerate lasso rejected");
Check(opaque.IsSimilarTo(opaque), "Stable frame detection");
Check(!opaque.IsSimilarTo(new(40, 40, new byte[40 * 40 * 4])), "Animation / changed frame detection");
LibraryTests.Run(Check);
FileActionTests.Run(Check);
ImageLaunchTests.Run(Check);
Console.WriteLine($"PASS: {assertions} assertions (capture geometry, scrolling, content library, file-operation validation).");
