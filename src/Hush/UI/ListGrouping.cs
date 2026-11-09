namespace Hush;

// The "too many items" rule (0.9, user direction): a list that grows long is never shown as one flat wall. Past FlatLimit items it is split into sub-categories
// automatically, so every list in Hush stays scannable whatever is added to it later:
//   1. up to FlatLimit items: shown flat, no headings;
//   2. more: grouped by each item's own topic (single-item topics are folded into "Other" when there are several of them);
//   3. a topic that is itself longer than GroupLimit is split by the item's second key (for example how safe it is), and a piece that is still too long is
//      cut into numbered parts of equal size.
// This only computes the names; the Settings page shows them with the shared collapsible topic headings (AdjustmentGroups). New lists should use it
// instead of inventing their own grouping (ListGroupingChecks).
internal static class ListGrouping
{
    internal const int FlatLimit = 12, GroupLimit = 12;
    internal const string Other = "Other";

    // One sub-category name per item, in the same order. All names are "" when the list is short enough to stay flat.
    internal static string[] Names<T>(IReadOnlyList<T> items, Func<T, string> topic, Func<T, string>? split = null, int flatLimit = FlatLimit, int groupLimit = GroupLimit)
    {
        var names = new string[items.Count];
        if (items.Count <= flatLimit) { Array.Fill(names, ""); return names; }
        var first = items.Select(item => string.IsNullOrWhiteSpace(topic(item)) ? Other : topic(item)).ToArray();
        // Several topics with a single item each become one "Other" group instead of a heading per item.
        var singles = first.GroupBy(name => name).Where(group => group.Count() == 1 && group.Key != Other).Select(group => group.Key).ToHashSet();
        if (singles.Count >= 2) for (int i = 0; i < first.Length; i++) if (singles.Contains(first[i])) first[i] = Other;
        for (int start = 0; start < first.Length; start++)
        {
            if (names[start] != null) continue;
            var members = Enumerable.Range(0, first.Length).Where(index => first[index] == first[start]).ToList();
            foreach (var (index, label) in Subdivide(items, members, first[start], split, groupLimit)) names[index] = label;
        }
        return names;
    }

    private static IEnumerable<(int Index, string Label)> Subdivide<T>(IReadOnlyList<T> items, List<int> members, string name, Func<T, string>? split, int groupLimit)
    {
        if (members.Count <= groupLimit) { foreach (int index in members) yield return (index, name); yield break; }
        List<(string Label, List<int> Indexes)> pieces = split == null ? [(name, members)]
            : members.GroupBy(index => split(items[index])).Select(group => ($"{name} · {group.Key}", group.ToList())).ToList();
        foreach (var (label, indexes) in pieces)
        {
            if (indexes.Count <= groupLimit) { foreach (int index in indexes) yield return (index, label); continue; }
            int parts = (int)Math.Ceiling(indexes.Count / (double)groupLimit), size = (int)Math.Ceiling(indexes.Count / (double)parts);
            for (int at = 0; at < indexes.Count; at++) yield return (indexes[at], $"{label} ({at / size + 1} of {parts})");
        }
    }
}
