using System.Text.RegularExpressions;

namespace Hush;

internal static class ReleaseNotes
{
    // What is new in the installed version: at most about five one-line bullets, plain words, no technical detail. The long notes stay on the release page.
    internal const string Current = "• A new cleaning animation\n• The percentage is always centered, and it says Done when finished\n• The finished gauge glows all around\n• Pro mode lights up the logo and the Pro button\n• The new icon shows right after an update";

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
