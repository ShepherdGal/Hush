using System.IO;
using System.Text.RegularExpressions;
using Hush;

// Locks the typography rules by scanning the XAML/C# source:
//  * Italic is reserved for the home heading "Make more room to breathe". The Hush logo (header, About, Setup) is upright, Medium 500 (user direction, 0.9).
//  * Page titles (Review files, Settings) share one style (PageTitle: 29, Medium 500, upright); popup titles share PopupTitle; Settings card titles share CardTitle.
internal static class TypographyChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string testsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string source = Path.GetFullPath(Path.Combine(testsDirectory, "..", "..", "src", "Hush"));
        var xamlFiles = Directory.EnumerateFiles(source, "*.xaml", SearchOption.AllDirectories).Where(path => !path.Contains(@"\obj\") && !path.Contains(@"\bin\")).ToList();
        var italicOffenders = new List<string>();
        int allowedItalics = 0;
        foreach (string file in xamlFiles)
            foreach (string line in File.ReadAllLines(file))
            {
                if (!(line.Contains("FontStyle=\"Italic\"") || line.Contains("Value=\"Italic\"") || line.Contains("FontStyles.Italic"))) continue; // comments may mention the word
                bool allowed = line.Contains("x:Name=\"Headline\"");
                if (allowed) allowedItalics++; else italicOffenders.Add(Path.GetFileName(file) + ": " + line.Trim()[..Math.Min(70, line.Trim().Length)]);
            }
        check(italicOffenders.Count == 0, "Italic is used only by the home heading" + (italicOffenders.Count == 0 ? "" : ": " + string.Join(" | ", italicOffenders)));
        check(allowedItalics == 1, $"The one allowed italic place exists (the home heading), and the Hush logo is upright: {allowedItalics}");
        var code = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories).Where(path => !path.Contains(@"\obj\") && !path.Contains(@"\bin\")).ToList();
        check(!code.Any(path => Regex.IsMatch(File.ReadAllText(path), @"FontStyles\.Italic|FontStyle\.Italic")), "No code sets an italic font");

        string window = File.ReadAllText(Path.Combine(source, "MainWindow.xaml"));
        string app = File.ReadAllText(Path.Combine(source, "App.xaml"));
        string setup = File.ReadAllText(Path.Combine(source, "Setup", "SetupWindow.xaml"));
        string TitleLine(string text) => window.Split('\n').Single(line => line.Contains($"Text=\"{text}\""));
        foreach (string title in new[] { "Review files", "Settings" })
        {
            string line = TitleLine(title);
            check(line.Contains("Style=\"{StaticResource PageTitle}\"") && !line.Contains("FontSize=") && !line.Contains("FontWeight=") && !line.Contains("FontStyle="),
                $"The '{title}' title uses the shared PageTitle style and overrides nothing");
        }
        var pageTitle = Regex.Match(app, "<Style x:Key=\"PageTitle\".*?</Style>", RegexOptions.Singleline).Value;
        check(pageTitle.Contains("\"FontSize\" Value=\"29\"") && pageTitle.Contains("\"FontWeight\" Value=\"Medium\"") && pageTitle.Contains("\"FontStyle\" Value=\"Normal\""),
            "PageTitle is 29, Medium (500) and upright");
        check(new[] { "Are you sure?" }.All(text => TitleLine(text).Contains("PopupTitle"))
            && window.Contains("x:Name=\"WindowsConfirmTitle\" Text=\"Apply Windows changes?\" Style=\"{StaticResource PopupTitle}\"")
            && window.Contains("x:Name=\"ConsentTitle\" Style=\"{StaticResource PopupTitle}\"") && setup.Contains("x:Name=\"Heading\" Grid.Row=\"1\" Style=\"{StaticResource PopupTitle}\""),
            "Every popup title (confirm, Windows changes, consent, Setup) uses the shared PopupTitle style");
        check(new[] { "Info", "Smart Boost", "Browser data", "Privacy &amp; telemetry", "More impact" }.All(text => TitleLine(text).Contains("CardTitle")), "Every Settings card title uses the shared CardTitle style");
    }
}
