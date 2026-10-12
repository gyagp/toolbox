using Toolbox.Core;

internal static class LibraryTests
{
    internal static void Run(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Toolbox-library-tests-" + Guid.NewGuid().ToString("N"));
        var images = Path.Combine(folder, "images");
        var videos = Path.Combine(folder, "videos");
        Directory.CreateDirectory(images); Directory.CreateDirectory(videos);
        try
        {
            var image = Path.Combine(images, "Memo.png");
            var video = Path.Combine(videos, "Demo.mp4");
            File.WriteAllBytes(image, [1, 2, 3, 4]); File.WriteAllBytes(video, [5, 6, 7, 8, 9, 10]);
            File.WriteAllText(Path.Combine(videos, "unfinished.partial.mp4"), "unfinished");
            File.WriteAllText(Path.Combine(images, "notes.txt"), "unrelated");
            var library = new CaptureLibrary(images, videos, images);
            var snapshot = library.Scan();
            check(snapshot.Errors.Count == 0 && snapshot.Entries.Count == 2, "Library discovers existing captures once and excludes partial/unrelated files");
            check(CaptureLibrary.Query(snapshot.Entries, LibraryFilter.Images, "memo", LibrarySort.Newest).Count == 1, "Image filter and case-insensitive name search");
            check(CaptureLibrary.Query(snapshot.Entries, LibraryFilter.Videos, "", LibrarySort.Newest).Single().FullPath == video, "Video filter");
            check(CaptureLibrary.Query(snapshot.Entries, LibraryFilter.All, "missing", LibrarySort.Newest).Count == 0, "Empty search results");
            check(CaptureLibrary.Query(snapshot.Entries, LibraryFilter.All, "", LibrarySort.Largest).First().FullPath == video, "Sort by file size");
            var dated = snapshot.Entries.Select((e, i) => e with { CreatedUtc = new DateTime(2026, 1, i + 1, 0, 0, 0, DateTimeKind.Utc) }).ToArray();
            check(CaptureLibrary.Query(dated, LibraryFilter.All, "", LibrarySort.Newest).First().CreatedUtc.Day == 2, "Sort newest first");

            var renamed = library.Rename(image, "项目说明");
            check(!File.Exists(image) && File.ReadAllBytes(renamed).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Rename preserves content and extension");
            var occupied = Path.Combine(images, "occupied.png"); File.WriteAllBytes(occupied, [99]);
            var rejected = false;
            try { library.Rename(renamed, "occupied"); } catch (IOException) { rejected = true; }
            check(rejected && File.ReadAllBytes(occupied)[0] == 99 && File.Exists(renamed), "Rename never overwrites another file");
            foreach (var name in new[] { "../outside", "CON", "LPT1.report", "trailing.", " space", "bad:name" })
            {
                rejected = false;
                try { library.Rename(renamed, name); } catch (ArgumentException) { rejected = true; }
                check(rejected && File.Exists(renamed), $"Reject invalid Windows name: {name}");
            }
            rejected = false;
            try { library.Rename(video, "unfinished.partial"); } catch (ArgumentException) { rejected = true; }
            check(rejected && File.Exists(video), "Renaming cannot turn a finished video into an ignored partial file");
            var outside = Path.Combine(folder, "outside.png"); File.WriteAllBytes(outside, [42]);
            rejected = false;
            try { library.MoveToDeleted(outside); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && File.Exists(outside), "File operations cannot leave the managed roots");

            var deleted = library.MoveToDeleted(renamed);
            var restarted = new CaptureLibrary(images, videos);
            var afterDelete = restarted.Scan();
            check(!File.Exists(renamed) && File.Exists(deleted), "Delete moves the file without destroying content");
            check(CaptureLibrary.Query(afterDelete.Entries, LibraryFilter.Deleted, "项目", LibrarySort.Newest).Single().FullPath == deleted, "Recently deleted persists across library restart");
            check(CaptureLibrary.Query(afterDelete.Entries, LibraryFilter.All, "项目", LibrarySort.Newest).Count == 0, "Deleted items excluded from normal views");
            File.WriteAllBytes(renamed, [88]);
            rejected = false;
            try { restarted.Restore(deleted); } catch (IOException) { rejected = true; }
            check(rejected && File.ReadAllBytes(renamed)[0] == 88 && File.Exists(deleted), "Restore refuses a same-name collision without losing either file");
            File.Delete(renamed);
            var restored = restarted.Restore(deleted);
            check(restored == renamed && File.ReadAllBytes(restored).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Restore returns exact original content and name");
            check(CaptureLibrary.Query(restarted.Scan().Entries, LibraryFilter.Deleted, "", LibrarySort.Newest).Count == 0, "Restored items leave recently deleted");
            rejected = false;
            try { restarted.PermanentlyDelete(restored); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && File.Exists(restored), "Permanent deletion is restricted to recently deleted items");
            var disposable = restarted.MoveToDeleted(occupied);
            restarted.PermanentlyDelete(disposable);
            check(!File.Exists(disposable) && CaptureLibrary.Query(restarted.Scan().Entries, LibraryFilter.Deleted, "", LibrarySort.Newest).Count == 0, "Permanent deletion removes only the selected archived file");
            File.Delete(video);
            check(restarted.Scan().Entries.All(e => e.FullPath != video), "External deletion reflected by fresh scan");
        }
        finally
        {
            // Only the unique fixture directory created by this test is removed.
            if (Path.GetDirectoryName(folder) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new Exception("Unexpected fixture path");
            Directory.Delete(folder, recursive: true);
        }
    }
}
