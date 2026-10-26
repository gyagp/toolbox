namespace Toolbox.Core;

public static class PdfPageRanges
{
    // One-based UI ranges, returned in the user's order. Reject duplicates and bad input.
    public static int[] Parse(string text, int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        if (string.IsNullOrWhiteSpace(text)) return Enumerable.Range(0, count).ToArray();
        var pages = new List<int>();
        var seen = new HashSet<int>();
        foreach (var part in text.Replace('，', ',').Split(','))
        {
            var range = part.Trim().Split('-');
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0], out var first) || first < 1 || first > count)
                throw new ArgumentException($"页码应在 1–{count} 之间，例如 1-3,5。");
            var last = first;
            if (range.Length == 2 && (!int.TryParse(range[1], out last) || last < 1 || last > count))
                throw new ArgumentException("无效的页码范围。");
            var step = first <= last ? 1 : -1;
            for (var page = first; ; page += step)
            {
                if (!seen.Add(page)) throw new ArgumentException($"页码 {page} 重复。");
                pages.Add(page - 1);
                if (page == last) break;
            }
        }
        return pages.ToArray();
    }

    public static int[][] SplitGroups(string text, int count) => string.IsNullOrWhiteSpace(text)
        ? Enumerable.Range(0, count).Select(index => new[] { index }).ToArray()
        : text.Replace('；', ';').Split(';').Select(group => string.IsNullOrWhiteSpace(group)
            ? throw new ArgumentException("拆分组不能为空，例如 1-3;4-6。") : Parse(group, count)).ToArray();
}
