using System.Text.RegularExpressions;

namespace Hush;

internal static class ReleaseNotes
{
    // What is new in the installed version: at most about five one-line bullets, plain words, no technical detail. The long notes stay on the release page.
    internal const string Current = "• A Simple | Pro switch in the header\n• A Boost chip shows RAM, CPU and what to clean\n• The cleaning animation smashes only when it reaches 100%\n• Support Hush on Ko-fi from the About window";

    // The short list for a release found online: its "In brief" section (the release notes template has one), or else the first bullet lines.
    // At most five lines, each cut to one line's worth of text, with markdown marks removed.
    internal static string InBrief(string notes)
    {
        var lines = (notes ?? "").Replace("\r", "").Split('\n');
        var section = new List<string>();
        int start = Array.FindIndex(lines, line => Regex.IsMatch(line, @"^\s{0,3}#{1,6}\s*in brief\b", RegexOptions.IgnoreCase));
        IEnumerable<string> source = start >= 0 ? lines.Skip(start + 1).TakeWhile(line => !Regex.IsMatch(line, @"^\s{0,3}#{1,6}\s")) : lines;
        foreach (string raw in source)
        {
            string line = raw.Trim();
            if (!(line.StartsWith('-') || line.StartsWith('*') || line.StartsWith('•'))) continue;
            line = Regex.Replace(line.TrimStart('-', '*', '•', ' '), @"\[([^\]]+)\]\([^)]+\)", "$1").Replace("**", "").Replace("`", "").Trim();
            if (line.Length == 0) continue;
            if (line.Length > 70) line = line[..67].TrimEnd() + "...";
            section.Add("• " + line);
            if (section.Count == 5) break;
        }
        return section.Count == 0 ? "The release page lists what changed." : string.Join("\n", section);
    }
}
