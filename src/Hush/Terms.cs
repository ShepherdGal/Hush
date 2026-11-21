namespace Hush;

// The terms every person agrees to before using Hush. One text, shown on first run, in About, in the installer note and in docs/TERMS.md
// (a test checks that the file and this text say the same thing). Change Version when the meaning changes so people are asked again.
internal static class Terms
{
    internal const string Version = "2026-11";
    internal const string Title = "Use Hush at your own risk";
    // The short line shown in About and in Setup.
    internal const string Notice = "Hush comes without warranty and is used at your own risk. The author isn't responsible for any loss or damage.";
    internal const string Body =
        "Hush is free software provided “as is”, without warranty of any kind, express or implied, including any warranty that it will be free of errors or fit for a particular purpose.\n\n"
        + "Cleaning, tweaking and system tools can delete files and change settings, and some of that cannot be undone. Hush shows you what it found and asks before it acts, but you decide what to remove. Keep backups of anything that matters.\n\n"
        + "You use Hush entirely at your own risk and responsibility. To the fullest extent the law allows, the author and contributors are not liable for any loss of data or settings, downtime, or any other damage or problem that results from using, or being unable to use, Hush.\n\n"
        + "Hush is an independent project. It isn't affiliated with or endorsed by Microsoft, NVIDIA, Mozilla, Google or any other company or product it mentions.\n\n"
        + "By continuing you confirm that you have read this and accept it.";
}
