using System.IO;
using System.Windows;

namespace Hush;

// One Windows tweak on the shared row (ServiceTemplate). The switch shows the tweak itself: ON = the tweak is (or will be) applied, OFF = Windows as it is.
// A tweak that is already on starts ON; switching it OFF stages "switch it back".
internal sealed class TweakChoice(TweakState state, bool canChange) : OffSwitchChoice
{
    internal TweakState State { get; } = state;
    public string Id => State.Tweak.Id;
    internal string TopicName => State.Tweak.Topic;
    public override string Title => State.Tweak.Title;
    public override bool WasOff => State.On;
    public override bool CanEdit => State.Available;
    public override string HelpText => State.Tweak.Help + (State.Tweak.Explorer ? " File Explorer may need a restart (or a sign-out) before you see it." : "")
        + " Hush saves the original setting first; switching it off again puts it back.";
    protected override string CurrentStatus => !State.Available ? "Not available on this version of Windows"
        : (State.On ? (State.ChangedByHush ? "On (set by Hush)" : "On") : "Off") + (State.Tweak.NeedsAdministrator && !canChange ? " · needs administrator mode" : "");
    public override string Status => IsStaged ? (WasOff ? "Will be switched back after you review · " : "Will be applied after you review · ") + CurrentStatus : CurrentStatus;
    public override string LevelLabel => State.Tweak.NeedsAdministrator ? "All users" : "This account";
    public override bool Amber => State.Tweak.NeedsAdministrator;
}

// Settings > Windows tweaks (owner, November 19, 2026; Pro only). All real work is in WindowsTweaks; changes go through the one Review changes button.
public partial class MainWindow
{
    private WindowsTweaks? windowsTweaks;
    internal static Func<string, string, WindowsTweaks> WindowsTweaksFactory = WindowsTweaks.Create;
    private WindowsTweaks Tweaks => windowsTweaks ??= WindowsTweaksFactory(Path.Combine(toolsDataFolder, "Windows settings"), Path.Combine(toolsDataFolder, "Logs", "tweaks.jsonl"));
    private List<TweakChoice> tweakChoices = [];
    private bool tweaksBusy;
    internal IReadOnlyList<TweakChoice> TweakChoices => tweakChoices;

    private void Tweaks_Expanded(object sender, RoutedEventArgs e) { if (ReferenceEquals(e.OriginalSource, sender) && tweakChoices.Count == 0) RefreshTweaks(); }
    private void RefreshTweaks_Click(object sender, RoutedEventArgs e) => RefreshTweaks();

    // Reading is a few dozen registry values: done at once when the card opens.
    internal void RefreshTweaks()
    {
        try
        {
            foreach (var old in tweakChoices) old.PropertyChanged -= TweakChoiceChanged;
            var tweaks = Tweaks;
            tweakChoices = tweaks.Scan().Select(state => new TweakChoice(state, tweaks.CanChange(state.Tweak)))
                .OrderBy(choice => Array.IndexOf(WindowsTweaks.TopicOrder, choice.State.Tweak.Topic)).ToList();
            foreach (var choice in tweakChoices) choice.PropertyChanged += TweakChoiceChanged;
            var names = ListGrouping.Names(tweakChoices, choice => choice.State.Tweak.Topic, flatLimit: 0, foldSingles: false);
            for (int i = 0; i < tweakChoices.Count; i++) tweakChoices[i].Topic = names[i];
            var view = new System.Windows.Data.ListCollectionView(tweakChoices);
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(TweakChoice.Topic)));
            TweaksList.ItemsSource = view;
            int on = tweakChoices.Count(choice => choice.State.On);
            int available = tweakChoices.Count(choice => choice.State.Available);
            TweaksSummary.Text = $"{on} of {available} tweaks on" + (tweakChoices.Count > available ? $" · {tweakChoices.Count - available} not available on this Windows" : "");
            UpdateReviewButton();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { TweaksSummary.Text = "The tweaks can't be read right now"; }
    }

    private void TweakChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateReviewButton();
}
