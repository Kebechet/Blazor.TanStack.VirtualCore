namespace Blazor.TanStackVirtual.Demo.Stories;

/// <summary>
/// One row in a demo list. <see cref="EstimatedHeight"/> is what the story feeds to
/// <c>ItemSizeEstimator</c>; it is deliberately only approximate, so the difference between the
/// estimate and the measured height is visible rather than hidden behind exact numbers.
/// </summary>
internal sealed record DemoRow(
    string Key,
    string Title,
    string Body,
    double EstimatedHeight,
    string CssClass,
    bool IsGroup)
{
    private const int _shortBodyWordCount = 6;

    /// <summary>Every row identical - the shape the framework's own Virtualize already handles well.</summary>
    internal static IReadOnlyList<DemoRow> BuildUniform(int count)
    {
        var rows = new DemoRow[count];
        for (var index = 0; index < count; index++)
        {
            rows[index] = new DemoRow(
                Key: $"u{index}",
                Title: $"Row {index}",
                Body: BuildBody(index, _shortBodyWordCount),
                EstimatedHeight: 58,
                CssClass: "row",
                IsGroup: false);
        }

        return rows;
    }

    /// <summary>
    /// Heights varying by roughly 5x. A single averaged size puts short rows too far apart and long
    /// rows on top of each other, which is precisely what per-row measurement fixes.
    /// </summary>
    internal static IReadOnlyList<DemoRow> BuildVariable(int count)
    {
        var rows = new DemoRow[count];
        for (var index = 0; index < count; index++)
        {
            // A repeating 1/8/25/3-word cycle, so the pattern is obvious while scrolling and stable
            // across runs - a random length would make the browser assertions unreproducible.
            var wordCount = (index % 4) switch
            {
                0 => 1,
                1 => 8,
                2 => 25,
                _ => 3
            };

            var isTall = wordCount >= 25;

            rows[index] = new DemoRow(
                Key: $"v{index}",
                Title: $"Row {index} - {wordCount} words",
                Body: BuildBody(index, wordCount),
                // Deliberately one flat estimate for every row despite the real spread, so the
                // measurement pass visibly has work to do.
                EstimatedHeight: 70,
                CssClass: isTall ? "row row-tall" : "row",
                IsGroup: false);
        }

        return rows;
    }

    /// <summary>
    /// Group rows that insert children in place when expanded. The group keeps its key across the
    /// toggle, which is exactly the case <c>InvalidateItemSize</c> exists for.
    /// </summary>
    internal static IReadOnlyList<DemoRow> BuildExpandable(int groupCount, IReadOnlySet<string> expandedKeys)
    {
        var rows = new List<DemoRow>(groupCount * 2);
        for (var index = 0; index < groupCount; index++)
        {
            var groupKey = $"g{index}";
            var isExpanded = expandedKeys.Contains(groupKey);

            rows.Add(new DemoRow(
                Key: groupKey,
                Title: $"{(isExpanded ? "▾" : "▸")} Group {index}",
                Body: isExpanded ? "Expanded - three children inserted below." : "Collapsed. Click to expand.",
                EstimatedHeight: 62,
                CssClass: "row row-group",
                IsGroup: true));

            if (!isExpanded)
            {
                continue;
            }

            for (var child = 0; child < 3; child++)
            {
                rows.Add(new DemoRow(
                    Key: $"{groupKey}c{child}",
                    Title: $"Child {index}.{child}",
                    Body: BuildBody(index + child, _shortBodyWordCount),
                    EstimatedHeight: 54,
                    CssClass: "row row-child",
                    IsGroup: false));
            }
        }

        return rows;
    }

    private static string BuildBody(int seed, int wordCount)
    {
        var words = new string[wordCount];
        for (var index = 0; index < wordCount; index++)
        {
            words[index] = _lexicon[(seed + index) % _lexicon.Length];
        }

        return string.Join(' ', words);
    }

    private static readonly string[] _lexicon =
    [
        "measure", "offset", "viewport", "overscan", "anchor", "cache", "estimate", "window",
        "scroll", "translate", "layout", "reflow", "paint", "observer", "threshold", "index"
    ];
}
