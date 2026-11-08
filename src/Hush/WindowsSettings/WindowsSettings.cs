using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace Hush;

// Advanced = "Somewhat recommended" (you may notice a difference; shown in the More impact card); otherwise "Recommended" (little or no effect).
// Topic only groups the rows on the Settings page. Choices that O&O ShutUp10++ itself would rate "not recommended" are deliberately not offered.
internal sealed record WindowsAdjustment(string Id, string Title, string Description, string Key, string ValueName,
    int Value, bool Advanced = false, bool Machine = false, bool EnterpriseOnly = false, string Topic = "");

internal sealed record PolicyValue(bool Exists, int Number = 0, bool IsDword = true)
{
    internal static readonly PolicyValue Missing = new(false);
    internal bool Is(int value) => Exists && IsDword && Number == value;
}

internal sealed record WindowsEnvironment(int Build, string Edition, bool Administrator, bool Managed, string Owner)
{
    // Windows Home ignores several of these policies. Hush no longer blocks it: the choices stay available and the page says so.
    internal bool Home => Edition is "Core" or "CoreN" or "CoreSingleLanguage" or "CoreCountrySpecific";
    internal bool Enterprise => Edition is "Enterprise" or "EnterpriseN" or "EnterpriseS" or "EnterpriseSN"
        or "Education" or "EducationN" or "IoTEnterprise" or "IoTEnterpriseS";
}

internal interface IWindowsPolicyStore
{
    WindowsEnvironment Environment { get; }
    PolicyValue Read(WindowsAdjustment setting, bool machineOverride = false);
    void Write(WindowsAdjustment setting, PolicyValue value);
}

// CanEdit is false only for a real blocker (recovery pending, or a value Hush could not restore). Everything else is a note in Status.
// NeedsAdministrator marks device-wide choices while Hush is not running as administrator: the confirmation then restarts Hush with Windows' own permission prompt.
// TakesEffect is false for a choice that only works on Enterprise/Education while this PC runs another edition (the bulk "recommended" actions skip those).
internal sealed record AdjustmentState(WindowsAdjustment Definition, PolicyValue Current, bool Enabled, bool CanEdit, string Status, bool NeedsAdministrator = false, bool TakesEffect = true);
internal sealed record WindowsSettingsState(IReadOnlyList<AdjustmentState> Settings, string Summary, bool CanUndo, string UndoHint);
internal sealed record AdjustmentChange(string Id, PolicyValue Expected, PolicyValue Target);
internal sealed record AdjustmentPlan(IReadOnlyList<AdjustmentChange> Changes, bool Undo);

internal sealed class WindowsSettingsService
{
    // Fixed catalog only. Never accept registry paths or executable commands from UI/history.
    private static readonly WindowsAdjustment[] CoreChoices = new[]
    {
        new WindowsAdjustment("diagnostics", "Limit optional diagnostic data",
            "Limits Windows diagnostics to required data for this user. Required data still sends; other apps have their own controls.",
            @"Software\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1),
        new WindowsAdjustment("tailored", "Stop personalized offers from diagnostic data",
            "Stops Windows using diagnostic data to personalize tips and offers for this user. Suggestions can still appear.",
            @"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1),
        new WindowsAdjustment("suggestions", "Hide suggestions in Windows Settings",
            "Turns off suggested content in Windows Settings for this user. Only takes effect on Enterprise, Education or IoT Enterprise.",
            @"Software\Policies\Microsoft\Windows\CloudContent", "DisableWindowsSpotlightOnSettings", 1, EnterpriseOnly: true),
        new WindowsAdjustment("location", "Turn off location for this user",
            "Prevents apps using the Windows location feature for this user. Maps and location-based features may stop working; websites can still estimate location.",
            @"Software\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1, Advanced: true),
        new WindowsAdjustment("widgets", "Turn off Windows Widgets",
            "Disables the Widgets board and its taskbar content for every user. Does not uninstall Widgets.",
            @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, Advanced: true, Machine: true),

        // ----- Microsoft Office (this user). Office reads these policy values for Office 2016, 2019, 2021 and Microsoft 365 apps. -----
        new WindowsAdjustment("office-telemetry", "Stop Office from sending diagnostic data",
            "Sets Office's diagnostic data level to \"Neither\", so Word, Excel, Outlook and the other Office apps send no client diagnostic data. Office keeps working; Microsoft sees less when something breaks.",
            @"Software\Policies\Microsoft\office\common\clienttelemetry", "sendtelemetry", 3),
        new WindowsAdjustment("office-personal", "Stop Office from sending personal information",
            "Turns off Office's \"Send personal information\" policy, so Office does not send your name, user names or file names to Microsoft with feedback and diagnostics.",
            @"Software\Policies\Microsoft\office\16.0\common", "sendcustomerdata", 0),
        new WindowsAdjustment("office-ceip", "Opt out of the Office Customer Experience Improvement Program",
            "Turns off Office's Customer Experience Improvement Program, which reports how you use Office features. Nothing else in Office changes.",
            @"Software\Policies\Microsoft\office\16.0\common", "qmenable", 0),

        // ----- Windows privacy and telemetry (this user) -----
        new WindowsAdjustment("search-web", "Keep web suggestions out of Start search",
            "Searching in Start stays on this PC: no Bing web suggestions or web results are mixed in. Local apps, files and settings are still found.",
            @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
        new WindowsAdjustment("typing", "Stop collecting typing and inking samples",
            "Stops Windows learning from what you type to build a personal dictionary and suggestions. Spell-check and text suggestions keep working with the standard dictionary.",
            @"Software\Policies\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),

        // ----- Windows privacy and telemetry (every user; run Hush as administrator) -----
        new WindowsAdjustment("telemetry", "Limit Windows telemetry to required data",
            "Sets Windows telemetry (the Connected User Experiences and Telemetry service, DiagTrack) to the Required level for every user. Required data still goes to Microsoft; the service stays installed, and Windows Update keeps working.",
            @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1, Machine: true),
        new WindowsAdjustment("feedback", "Stop Windows feedback prompts",
            "Windows no longer asks you to rate features or give feedback.",
            @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1, Machine: true),
        new WindowsAdjustment("advertising-id", "Turn off the advertising ID",
            "Apps can no longer use a per-user advertising ID to show interest-based ads. You still see ads, just less targeted.",
            @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1, Machine: true),
        new WindowsAdjustment("windows-ceip", "Opt out of the Windows Customer Experience Improvement Program",
            "Stops Windows reporting how its features are used (the Windows CEIP).",
            @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0, Machine: true),
        new WindowsAdjustment("inventory", "Stop the application inventory collector",
            "Windows no longer scans which programs and drivers are installed to send compatibility data to Microsoft.",
            @"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisableInventory", 1, Machine: true),
        new WindowsAdjustment("activity", "Turn off activity history",
            "Windows stops recording the apps, files and sites you used in order to sync a timeline across your devices.",
            @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, Machine: true),
        new WindowsAdjustment("delivery", "Stop sharing Windows updates with other PCs",
            "Delivery Optimization downloads updates only from Microsoft, and your PC never uploads them to other PCs, saving upload bandwidth. Updates still install normally.",
            @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, Machine: true),
        new WindowsAdjustment("maps", "Stop automatic offline-maps downloads",
            "Windows no longer downloads or updates offline map data in the background. Maps you chose to download stay, but will not be refreshed.",
            @"SOFTWARE\Policies\Microsoft\Windows\Maps", "AutoDownloadAndUpdateMapData", 0, Machine: true),
        new WindowsAdjustment("error-reporting", "Turn off Windows Error Reporting",
            "Windows no longer packages and sends crash reports to Microsoft. Programs still crash and recover the same way; you just lose the \"look for a solution\" step.",
            @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, Advanced: true, Machine: true),
        new WindowsAdjustment("background-apps", "Stop Store apps running in the background",
            "Microsoft Store apps no longer refresh, sync or fetch data while you are not using them, which saves bandwidth and battery. Mail, calendar and chat apps may stop notifying you until you open them. Desktop programs are not affected.",
            @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2, Advanced: true, Machine: true)
    };

    private static string TopicOf(string id) => id.StartsWith("office-", StringComparison.Ordinal) ? "Microsoft Office" : id switch
    {
        "diagnostics" or "tailored" or "telemetry" or "feedback" or "windows-ceip" or "inventory" or "error-reporting" => "Telemetry & diagnostics",
        "suggestions" or "advertising-id" => "Suggestions & ads",
        "search-web" => "Search & Cortana",
        "typing" => "Typing & handwriting",
        "location" or "maps" => "Location & maps",
        "widgets" or "background-apps" => "Apps & background",
        "activity" => "Activity history & sync",
        "delivery" => "Windows Update & maintenance",
        _ => "Microsoft Office"
    };

    // More choices in the spirit of O&O ShutUp10++ (recommended and somewhat recommended only), each one a documented Windows or Edge policy value.
    // Device-wide ones need administrator rights. Nothing here touches the camera, microphone, location access, Defender, SmartScreen, Windows
    // Update itself, the Store, OneDrive or sign-in: those are the "not recommended" kind and are not offered.
    private static WindowsAdjustment W(string topic, string id, string title, string description, string key, string value, int data,
        bool somewhat = false, bool machine = true, bool enterprise = false)
        => new(id, title, description, key, value, data, somewhat, machine, enterprise, topic);
    private const string Win = @"SOFTWARE\Policies\Microsoft\Windows\";
    private const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
    // Edge shows "managed by your organization" once any Edge policy is set; the group heading says so once instead of every row.
    private const string EdgeTopic = "Microsoft Edge · Edge will say it is managed by your organization (normal for policies)";
    private static readonly WindowsAdjustment[] MoreChoices =
    [
        // ----- Telemetry & diagnostics -----
        W("Telemetry & diagnostics", "device-name", "Keep this PC's name out of diagnostic data", "Windows stops adding the computer name to the diagnostic data it sends.", Win + "DataCollection", "AllowDeviceNameInTelemetry", 0),
        W("Telemetry & diagnostics", "diag-logs", "Limit diagnostic log collection", "Windows collects fewer diagnostic logs for Microsoft.", Win + "DataCollection", "LimitDiagnosticLogCollection", 1),
        W("Telemetry & diagnostics", "diag-dumps", "Limit crash dump collection", "Windows collects fewer crash dumps (memory snapshots) for Microsoft.", Win + "DataCollection", "LimitDumpCollection", 1),
        W("Telemetry & diagnostics", "mrt-report", "Stop the Malicious Software Removal Tool reporting", "The monthly Microsoft cleanup tool no longer reports what it found. It still runs.", @"SOFTWARE\Policies\Microsoft\MRT", "DontReportInfectionInformation", 1),
        W("Telemetry & diagnostics", "steps-recorder", "Turn off Steps Recorder", "Turns off the Steps Recorder tool, which records screenshots of what you do.", Win + "AppCompat", "DisableUAR", 1),
        W("Telemetry & diagnostics", "app-telemetry", "Turn off application telemetry", "Windows stops tracking which programs you run for compatibility reports.", Win + "AppCompat", "AITEnable", 0),
        W("Telemetry & diagnostics", "eventviewer-links", "Stop Event Viewer sending event details online", "Event Viewer no longer offers to send event details to Microsoft's website.", @"SOFTWARE\Policies\Microsoft\EventViewer", "MicrosoftEventVwrDisableLinks", 1),
        W("Telemetry & diagnostics", "onesettings", "Stop Windows downloading settings from Microsoft's OneSettings", "Windows no longer fetches remote configuration from Microsoft. Some Microsoft-driven changes to Windows may stop arriving.", Win + "DataCollection", "DisableOneSettingsDownloads", 1, somewhat: true),

        // ----- Suggestions & ads -----
        W("Suggestions & ads", "consumer-features", "Stop automatic installs of suggested apps", "Windows no longer quietly installs suggested apps and promotional content. Only takes effect on Enterprise, Education or IoT Enterprise.", Win + "CloudContent", "DisableWindowsConsumerFeatures", 1, enterprise: true),
        W("Suggestions & ads", "cloud-content", "Stop cloud-optimized content and tips", "Windows no longer shows cloud-delivered tips and promotional content. Only takes effect on Enterprise, Education or IoT Enterprise.", Win + "CloudContent", "DisableCloudOptimizedContent", 1, enterprise: true),
        W("Suggestions & ads", "soft-landing", "Stop Windows tips and welcome experiences", "No more \"get the most out of Windows\" tips and welcome pages after updates. Only takes effect on Enterprise, Education or IoT Enterprise.", Win + "CloudContent", "DisableSoftLanding", 1, enterprise: true),
        W("Suggestions & ads", "spotlight", "Turn off Windows Spotlight features for this user", "No Spotlight pictures, tips or suggestions for this user. Only takes effect on Enterprise, Education or IoT Enterprise.", @"Software\Policies\Microsoft\Windows\CloudContent", "DisableWindowsSpotlightFeatures", 1, machine: false, enterprise: true),
        W("Suggestions & ads", "third-party-suggestions", "Stop third-party suggestions for this user", "Windows no longer shows suggestions from other companies' apps for this user. Only takes effect on Enterprise, Education or IoT Enterprise.", @"Software\Policies\Microsoft\Windows\CloudContent", "DisableThirdPartySuggestions", 1, machine: false, enterprise: true),

        // ----- Search & Cortana -----
        W("Search & Cortana", "cortana", "Turn off Cortana", "Cortana (the old voice assistant) is switched off.", Win + "Windows Search", "AllowCortana", 0),
        W("Search & Cortana", "search-web-all", "Never search the web from Windows Search", "Windows Search only looks at this PC and shows no web results.", Win + "Windows Search", "DisableWebSearch", 1),
        W("Search & Cortana", "search-connected", "Stop Windows Search using the web", "Search no longer sends what you type to Bing.", Win + "Windows Search", "ConnectedSearchUseWeb", 0),
        W("Search & Cortana", "search-location", "Keep location out of Windows Search", "Search no longer uses your location.", Win + "Windows Search", "AllowSearchToUseLocation", 0),
        W("Search & Cortana", "search-cloud", "Stop Search looking in your cloud accounts", "Search no longer includes content from your Microsoft or work account in results.", Win + "Windows Search", "AllowCloudSearch", 0, somewhat: true),

        // ----- Apps & permissions (value 2 = apps are denied) -----
        W("Apps & permissions", "apps-diagnostics", "Stop apps reading diagnostic info about other apps", "Apps can no longer see which other apps are running or their diagnostic details.", Win + "AppPrivacy", "LetAppsGetDiagnosticInfo", 2),
        W("Apps & permissions", "apps-call-history", "Stop apps reading your call history", "Apps are denied access to call history.", Win + "AppPrivacy", "LetAppsAccessCallHistory", 2),
        W("Apps & permissions", "apps-tasks", "Stop apps reading your tasks", "Apps are denied access to your to-do tasks.", Win + "AppPrivacy", "LetAppsAccessTasks", 2),
        W("Apps & permissions", "apps-sync-devices", "Stop apps talking to unpaired devices", "Apps can no longer share and sync with devices that are not paired with this PC.", Win + "AppPrivacy", "LetAppsSyncWithDevices", 2),
        W("Apps & permissions", "apps-messaging", "Stop apps reading and sending messages", "Apps are denied access to text and chat messages. Messaging apps that rely on it may stop working.", Win + "AppPrivacy", "LetAppsAccessMessaging", 2, somewhat: true),
        W("Apps & permissions", "apps-account-info", "Stop apps reading your account name and picture", "Apps are denied your account name and picture. Some apps will show a blank profile.", Win + "AppPrivacy", "LetAppsAccessAccountInfo", 2, somewhat: true),
        W("Apps & permissions", "apps-email", "Stop apps reading your email", "Apps are denied access to email stored on this PC. Mail apps may stop working.", Win + "AppPrivacy", "LetAppsAccessEmail", 2, somewhat: true),
        W("Apps & permissions", "apps-contacts", "Stop apps reading your contacts", "Apps are denied access to your contacts. Mail and calendar apps may stop working.", Win + "AppPrivacy", "LetAppsAccessContacts", 2, somewhat: true),
        W("Apps & permissions", "apps-calendar", "Stop apps reading your calendar", "Apps are denied access to your calendar. Calendar apps may stop working.", Win + "AppPrivacy", "LetAppsAccessCalendar", 2, somewhat: true),
        W("Apps & permissions", "apps-motion", "Stop apps reading motion sensors", "Apps are denied access to motion and activity sensors. Fitness and rotation features may stop working.", Win + "AppPrivacy", "LetAppsAccessMotion", 2, somewhat: true),
        W("Apps & permissions", "apps-radios", "Stop apps switching Bluetooth and Wi-Fi", "Apps cannot turn radios such as Bluetooth on or off.", Win + "AppPrivacy", "LetAppsAccessRadios", 2, somewhat: true),
        W("Apps & permissions", "apps-trusted-devices", "Stop apps using trusted devices", "Apps cannot use devices you have marked as trusted.", Win + "AppPrivacy", "LetAppsAccessTrustedDevices", 2, somewhat: true),

        // ----- Activity history & sync -----
        W("Activity history & sync", "activity-upload", "Stop sending activity history to Microsoft", "Your activity history is no longer uploaded to Microsoft.", Win + "System", "UploadUserActivities", 0),
        W("Activity history & sync", "clipboard-sync", "Stop syncing the clipboard between devices", "What you copy stays on this PC.", Win + "System", "AllowCrossDeviceClipboard", 0),
        W("Activity history & sync", "message-sync", "Stop syncing text messages to the cloud", "Messages are no longer synced to Microsoft's cloud.", Win + "Messaging", "AllowMessageSync", 0),
        W("Activity history & sync", "activity-publish", "Stop storing activity history on this PC", "Windows no longer keeps a local list of what you did. The Timeline-style history stays empty.", Win + "System", "PublishUserActivities", 0, somewhat: true),
        W("Activity history & sync", "cdp", "Turn off the Connected Devices Platform", "Stops features that link this PC to your phone and other PCs (Nearby sharing, Phone Link handoff).", Win + "System", "EnableCdp", 0, somewhat: true),
        W("Activity history & sync", "setting-sync", "Stop syncing Windows settings to your account", "Your theme and layout settings no longer sync between your PCs.", Win + "SettingSync", "DisableSettingSync", 2, somewhat: true),

        // ----- Typing & handwriting -----
        W("Typing & handwriting", "handwriting-sharing", "Stop sharing handwriting data", "Handwriting samples are no longer sent to Microsoft.", Win + "TabletPC", "PreventHandwritingDataSharing", 1),
        W("Typing & handwriting", "handwriting-errors", "Stop handwriting error reports", "Handwriting recognition errors are no longer reported to Microsoft.", Win + "HandwritingErrorReports", "PreventHandwritingErrorReports", 1),
        W("Typing & handwriting", "input-personalization", "Turn off online speech recognition and inking personalization", "Windows stops building a personal voice and typing profile. Voice typing and similar features may work less well.", @"SOFTWARE\Policies\Microsoft\InputPersonalization", "AllowInputPersonalization", 0, somewhat: true),

        // ----- AI features (this user) -----
        W("AI features", "recall", "Turn off Windows Recall snapshots", "Windows does not save snapshots of your screen for Recall. Does nothing on PCs without Recall.", @"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, machine: false),
        W("AI features", "copilot", "Turn off Windows Copilot", "Hides and disables Copilot for this user. You lose the built-in AI assistant.", @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, somewhat: true, machine: false),

        // ----- Microsoft Edge (device-wide policies) -----
        W(EdgeTopic, "edge-metrics", "Edge: stop usage and crash reporting", "Edge no longer sends usage and crash data to Microsoft.", Edge, "MetricsReportingEnabled", 0),
        W(EdgeTopic, "edge-diagnostic", "Edge: send no diagnostic data", "Edge sends no optional diagnostic data.", Edge, "DiagnosticData", 0),
        W(EdgeTopic, "edge-personalization", "Edge: stop personalizing ads and news from your browsing", "Edge no longer sends browsing history to personalize ads, search and news.", Edge, "PersonalizationReportingEnabled", 0),
        W(EdgeTopic, "edge-siteinfo", "Edge: stop sending sites you visit to Microsoft", "Edge no longer sends information about visited sites to improve Microsoft services.", Edge, "SendSiteInfoToImproveServices", 0),
        W(EdgeTopic, "edge-feedback", "Edge: hide the feedback button", "Removes the feedback smiley and survey prompts.", Edge, "UserFeedbackAllowed", 0),
        W(EdgeTopic, "edge-shopping", "Edge: turn off the shopping assistant", "No price comparison and coupon pop-ups.", Edge, "EdgeShoppingAssistantEnabled", 0),
        W(EdgeTopic, "edge-recommendations", "Edge: stop feature recommendations", "No \"try this feature\" notifications.", Edge, "ShowRecommendationsEnabled", 0),
        W(EdgeTopic, "edge-spotlight", "Edge: stop Microsoft recommendations and promotions", "No Microsoft promotions inside Edge.", Edge, "SpotlightExperiencesAndRecommendationsEnabled", 0),
        W(EdgeTopic, "edge-rewards", "Edge: hide Microsoft Rewards", "Hides the Rewards points button and prompts.", Edge, "ShowMicrosoftRewards", 0),
        W(EdgeTopic, "edge-sidebar", "Edge: turn off the sidebar", "Removes Edge's sidebar, including its Copilot and shopping panels.", Edge, "HubsSidebarEnabled", 0, somewhat: true),
        W(EdgeTopic, "edge-search-suggest", "Edge: stop search suggestions", "Edge no longer sends what you type in the address bar to the search engine for suggestions.", Edge, "SearchSuggestEnabled", 0, somewhat: true),
        W(EdgeTopic, "edge-background", "Edge: stop running in the background", "Edge closes completely when you close its windows. Notifications from web apps stop until Edge is open.", Edge, "BackgroundModeEnabled", 0, somewhat: true),
        W(EdgeTopic, "edge-prediction", "Edge: stop preloading pages", "Edge no longer predicts and preloads pages you might open. Pages may load slightly slower.", Edge, "NetworkPredictionOptions", 2, somewhat: true),

        // ----- Windows Update & maintenance -----
        W("Windows Update & maintenance", "no-auto-reboot", "Never restart automatically while someone is signed in", "Windows installs updates but waits for you to restart. Remember to restart now and then.", Win + @"WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1),
        W("Windows Update & maintenance", "build-preview", "Block Windows Insider preview builds", "Stops this PC from being switched to unfinished Insider builds by accident.", Win + "PreviewBuilds", "AllowBuildPreview", 0),
        W("Windows Update & maintenance", "device-metadata", "Stop downloading device pictures and apps from the internet", "Windows no longer fetches icons and manufacturer apps for your devices.", Win + "Device Metadata", "PreventDeviceMetadataFromNetwork", 1, somewhat: true),
    ];

    internal static readonly IReadOnlyList<WindowsAdjustment> Catalog = Array.AsReadOnly(
        CoreChoices.Concat(MoreChoices).Select(item => item.Topic.Length > 0 ? item : item with { Topic = TopicOf(item.Id) }).ToArray());

    internal const string NotConfigured = "Not configured by Hush";
    // Original is the value before Hush first touched the setting; Applied is what Hush wrote (null in older history files: the setting's "on" value).
    private sealed record SavedValue(string Id, PolicyValue Original, PolicyValue? Applied = null);
    private static PolicyValue AppliedOf(SavedValue saved, WindowsAdjustment setting) => saved.Applied ?? new PolicyValue(true, setting.Value);
    private sealed record History(int Schema, string Owner, bool Pending, List<SavedValue> Values);
    private readonly IWindowsPolicyStore store;
    private readonly string directory, historyPath;
    internal WindowsSettingsService(IWindowsPolicyStore store, string directory)
    {
        this.store = store;
        this.directory = Path.GetFullPath(directory);
        historyPath = Path.Combine(this.directory, "windows-settings.json");
    }
    internal static WindowsSettingsService Create() => new(new RegistryPolicyStore(), Path.Combine(CleanupEngine.DataDirectory, "Windows settings"));
    private static WindowsAdjustment Find(string id) => Catalog.SingleOrDefault(item => item.Id == id) ?? throw new InvalidDataException("Unknown Windows setting.");

    internal WindowsSettingsState Read()
    {
        using var gate = Lock();
        var environment = store.Environment;
        var history = Load(environment);
        var rows = Catalog.Select(setting => ReadRow(setting, environment, history)).ToArray();
        string summary = environment.Managed ? "This PC looks managed by a work or school, so its own rules may override or undo these choices."
            : environment.Home ? "Windows Home ignores several of these policies, so some choices may have no effect there."
            : "Choose changes, then review before applying. Hush explains each change first and tells you whether to sign out or restart.";
        bool canUndo = history.Values.Count > 0 && history.Values.All(saved => CanRestore(Find(saved.Id), saved));
        string undoHint = history.Values.Count == 0 ? "Undo restores the values saved before Hush's changes."
            : history.Pending ? "An interrupted operation needs review. Use Undo changes to restore tracked values."
            : !canUndo ? "Undo is blocked because a value was changed outside Hush after Hush set it, so nothing else is overwritten. Refresh after resolving it."
            : "Undo restores all changes tracked by Hush, including an originally absent policy. It does not force Windows defaults.";
        return new(rows, summary, canUndo, undoHint);
    }

    private AdjustmentState ReadRow(WindowsAdjustment setting, WindowsEnvironment environment, History history)
    {
        var current = store.Read(setting);
        var saved = history.Values.SingleOrDefault(value => value.Id == setting.Id);
        bool enabled = current.Is(setting.Value) || setting.Id == "diagnostics" && current.Is(0);
        // Only two things really block a switch: an interrupted operation that must be undone first, and a value of an unusual registry type
        // that Hush could not put back exactly. Everything else is a plain note and the person decides in the confirmation.
        string? blocked = history.Pending ? "Recovery needed · use Undo changes"
            : current.Exists && !current.IsDword ? "Unusual value type · left alone because Hush could not restore it exactly"
            : null;
        var notes = new List<string>();
        if (saved != null && current != AppliedOf(saved, setting)) notes.Add("Changed by another tool since Hush set it");
        else if (saved == null && current.Exists) notes.Add(enabled ? "Already on · set outside Hush" : "Has another value set outside Hush · saved for Undo");
        if (!setting.Machine && store.Read(setting, machineOverride: true).Exists) notes.Add("A device-wide policy also exists and takes priority");
        if (setting.EnterpriseOnly && !environment.Enterprise) notes.Add("Only takes effect on Enterprise or Education");
        string status = blocked ?? (notes.Count > 0 ? string.Join(" · ", notes) : saved == null ? NotConfigured : "Applied by Hush · can be undone");
        return new(setting, current, enabled, blocked == null, status, setting.Machine && !environment.Administrator, !(setting.EnterpriseOnly && !environment.Enterprise));
    }
    private bool CanRestore(WindowsAdjustment setting, SavedValue saved)
    {
        var current = store.Read(setting);
        return current == AppliedOf(saved, setting) || current == saved.Original;
    }

    // True when the plan changes a device-wide setting and this copy of Hush is not running as administrator.
    internal bool NeedsAdministrator(AdjustmentPlan plan) => !store.Environment.Administrator && plan.Changes.Any(change => Find(change.Id).Machine);

    internal AdjustmentPlan Prepare(IReadOnlyList<AdjustmentState> displayed, IReadOnlyDictionary<string, bool> requested)
    {
        var fresh = Read();
        var changes = new List<AdjustmentChange>();
        foreach (var request in requested)
        {
            var row = fresh.Settings.Single(item => item.Definition.Id == request.Key);
            var before = displayed.Single(item => item.Definition.Id == request.Key);
            if (request.Value == before.Enabled) continue;
            if (!row.CanEdit || row.Current != before.Current || !before.CanEdit) throw new IOException("A Windows setting changed or is unavailable. Refresh and review again.");
            changes.Add(new(request.Key, row.Current, request.Value ? new(true, row.Definition.Value) : PolicyValue.Missing));
        }
        return new(changes.AsReadOnly(), false);
    }

    internal AdjustmentPlan PrepareUndo()
    {
        using var gate = Lock();
        var environment = store.Environment;
        var history = Load(environment);
        if (history.Values.Count == 0) throw new IOException("There are no tracked changes to undo.");
        if (history.Values.Any(saved => !CanRestore(Find(saved.Id), saved)))
            throw new IOException("A tracked value was changed outside Hush after Hush set it. Nothing was restored.");
        return new(history.Values.Select(saved => new AdjustmentChange(saved.Id, store.Read(Find(saved.Id)), saved.Original)).ToArray(), true);
    }

    // Called only after the dedicated confirmation. Revalidates the entire plan before any write.
    internal void Execute(AdjustmentPlan plan)
    {
        using var gate = Lock();
        var environment = store.Environment;
        var history = Load(environment);
        if (plan.Changes.Count == 0 || plan.Changes.Count > Catalog.Count || plan.Changes.Select(change => change.Id).Distinct().Count() != plan.Changes.Count)
            throw new IOException("No valid Windows changes were confirmed.");
        if (plan.Undo && plan.Changes.Count != history.Values.Count) throw new IOException("Undo history changed. Review again.");
        if (!environment.Administrator && plan.Changes.Any(change => Find(change.Id).Machine))
            throw new IOException("Administrator rights are needed for device-wide changes. Nothing was changed.");
        foreach (var change in plan.Changes) Validate(change, plan.Undo, environment, history);
        var tracked = history.Values.ToList();
        foreach (var change in plan.Changes)
        {
            // The first value Hush ever saw stays the original for Undo; Applied follows what Hush writes now.
            int index = tracked.FindIndex(saved => saved.Id == change.Id);
            if (index < 0) tracked.Add(new(change.Id, change.Expected, change.Target));
            else tracked[index] = tracked[index] with { Applied = change.Target };
        }
        var pending = history with { Pending = true, Values = tracked };
        Save(pending); // Flush original values before the first registry mutation.
        var attempted = new List<AdjustmentChange>();
        try
        {
            foreach (var change in plan.Changes)
            {
                // A policy/permission/value may have changed while the confirmation was open.
                Validate(change, plan.Undo, store.Environment, history);
                if (change.Expected == change.Target) continue;
                attempted.Add(change); // Include a write that throws after changing the value.
                store.Write(Find(change.Id), change.Target);
                if (store.Read(Find(change.Id)) != change.Target) throw new IOException("Windows did not retain a confirmed setting.");
            }
            // A setting that is back at the value Hush first found needs no tracking any more.
            tracked.RemoveAll(saved => AppliedOf(saved, Find(saved.Id)) == saved.Original);
            Save(pending with { Pending = false, Values = tracked });
        }
        catch (Exception failure)
        {
            foreach (var change in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    // Do not overwrite an unrelated edit made during a failed operation.
                    if (store.Read(Find(change.Id)) == change.Target) store.Write(Find(change.Id), change.Expected);
                }
                catch { /* Durable originals remain for explicit recovery. */ }
            }
            throw new IOException("Windows changes could not be completed. Saved history was retained. Refresh and review Undo changes; another tool's changes will not be overwritten.", failure);
        }
    }

    private void Validate(AdjustmentChange change, bool undo, WindowsEnvironment environment, History history)
    {
        var setting = Find(change.Id);
        var saved = history.Values.SingleOrDefault(value => value.Id == change.Id);
        if (store.Read(setting) != change.Expected) throw new IOException("A Windows value changed after review. Nothing further was changed.");
        if (undo)
        {
            if (saved == null || change.Target != saved.Original || !CanRestore(setting, saved)) throw new IOException("Undo is no longer safe. Refresh and review again.");
        }
        else
        {
            var row = ReadRow(setting, environment, history);
            if (!row.CanEdit || change.Target != (row.Enabled ? PolicyValue.Missing : new PolicyValue(true, setting.Value)))
                throw new IOException("The confirmed Windows change is no longer available.");
        }
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(directory);
        if (!CleanupEngine.SafePath(directory, directory)) throw new IOException("Windows settings history location is unsafe.");
        string path = Path.Combine(directory, "operation.lock");
        if (File.Exists(path) && !CleanupEngine.SafePath(path, directory)) throw new IOException("Windows settings history is unsafe.");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private History Load(WindowsEnvironment environment)
    {
        if (!File.Exists(historyPath)) return new(1, environment.Owner, false, []);
        if (!CleanupEngine.SafePath(historyPath, directory) || new FileInfo(historyPath).Length > 32768) throw new InvalidDataException("Windows settings history is unsafe or damaged. No settings were changed.");
        var history = JsonSerializer.Deserialize<History>(File.ReadAllText(historyPath)) ?? throw new InvalidDataException("Windows settings history is damaged.");
        if (history.Schema != 1 || history.Owner != environment.Owner || history.Values == null || history.Values.Count > Catalog.Count
            || history.Values.Select(saved => saved.Id).Distinct().Count() != history.Values.Count
            || history.Values.Any(saved => !Catalog.Any(setting => setting.Id == saved.Id)
                || saved.Original is { Exists: true, IsDword: false } || saved.Applied is { Exists: true, IsDword: false }))
            throw new InvalidDataException("Windows settings history does not match this account or catalog. No settings were changed.");
        return history;
    }
    private void Save(History history)
    {
        if (File.Exists(historyPath) && !CleanupEngine.SafePath(historyPath, directory)) throw new IOException("Unsafe Windows settings history.");
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, history); file.Flush(true); }
            File.Move(temporary, historyPath, overwrite: true);
        }
        finally { if (File.Exists(temporary) && CleanupEngine.SafePath(temporary, directory)) File.Delete(temporary); }
    }
}

internal sealed class RegistryPolicyStore : IWindowsPolicyStore
{
    public WindowsEnvironment Environment
    {
        get
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var version = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            int build = int.TryParse(version?.GetValue("CurrentBuildNumber") as string, out int number) ? number : 0;
            using var identity = WindowsIdentity.GetCurrent();
            bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            bool managed = IsManaged(root);
            return new(build, version?.GetValue("EditionID") as string ?? "", admin, managed, identity.User?.Value ?? "unknown");
        }
    }
    // A PC counts as managed only when it is joined to a domain, joined to Entra ID (Azure AD), or enrolled with a real device-management service.
    // Every Windows 11 PC carries built-in "Local/Cloud/Deploy Authority" enrollment entries with state 1, and NetGetAadJoinInformation answers 1
    // ("not joined") on an ordinary PC; counting either as management marked nearly every PC as managed and locked every switch.
    private static bool IsManaged(RegistryKey machine)
    {
        int result = NetGetJoinInformation(null, out IntPtr name, out int status);
        try { if (result == 0 && status == 3) return true; }
        finally { if (name != IntPtr.Zero) NetApiBufferFree(name); }
        int joined = NetGetAadJoinInformation(null, out IntPtr info);
        try { if (joined == 0 && info != IntPtr.Zero) return true; }
        finally { if (info != IntPtr.Zero) NetFreeAadJoinInformation(info); }
        using var enrollments = machine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments");
        if (enrollments != null)
            foreach (string child in enrollments.GetSubKeyNames())
            {
                using var enrollment = enrollments.OpenSubKey(child);
                if (enrollment != null && IsRealEnrollment(enrollment.GetValue("EnrollmentState") as int?, enrollment.GetValue("ProviderID") as string, enrollment.GetValue("UPN") as string)) return true;
            }
        return false;
    }
    internal static bool IsRealEnrollment(int? state, string? provider, string? upn)
    {
        if (state != 1) return false;
        bool builtIn = string.IsNullOrWhiteSpace(provider) || provider.EndsWith("Authority", StringComparison.OrdinalIgnoreCase);
        return !builtIn || !string.IsNullOrWhiteSpace(upn);
    }
    public PolicyValue Read(WindowsAdjustment setting, bool machineOverride = false)
    {
        using var root = RegistryKey.OpenBaseKey(setting.Machine || machineOverride ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(setting.Key);
        return ReadValue(key, setting.ValueName);
    }
    public void Write(WindowsAdjustment setting, PolicyValue value)
    {
        if (!WindowsSettingsService.Catalog.Contains(setting)) throw new InvalidOperationException("Unknown Windows policy.");
        using var root = RegistryKey.OpenBaseKey(setting.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        if (value.Exists)
        { using var key = root.CreateSubKey(setting.Key, writable: true); WriteValue(key, setting.ValueName, value); }
        else
        { using var key = root.OpenSubKey(setting.Key, writable: true); if (key != null) WriteValue(key, setting.ValueName, value); }
    }
    internal static PolicyValue ReadValue(RegistryKey? key, string name)
    {
        if (key == null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return PolicyValue.Missing;
        return key.GetValueKind(name) == RegistryValueKind.DWord && key.GetValue(name) is int number ? new(true, number) : new(true, IsDword: false);
    }
    internal static void WriteValue(RegistryKey key, string name, PolicyValue value)
    {
        if (value.Exists)
        { if (!value.IsDword) throw new InvalidDataException("Only DWORD policies are supported."); key.SetValue(name, value.Number, RegistryValueKind.DWord); }
        else key.DeleteValue(name, throwOnMissingValue: false);
        key.Flush(); // Never remove a registry key/tree or any neighboring value.
    }
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetGetJoinInformation(string? server, out IntPtr name, out int status);
    [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetGetAadJoinInformation(string? tenant, out IntPtr info);
    [DllImport("netapi32.dll")] private static extern void NetFreeAadJoinInformation(IntPtr info);
}

internal sealed class AdjustmentChoice(AdjustmentState state) : INotifyPropertyChanged
{
    internal AdjustmentState Baseline { get; } = state;
    public string Id => Baseline.Definition.Id;
    public string Title => Baseline.Definition.Title;
    public string Description => Baseline.Definition.Description;
    public string Status => Baseline.Status;
    // The default "Not configured by Hush" line is noise on every row; only show a status when it tells the user something.
    public bool HasNotice => Baseline.Status != WindowsSettingsService.NotConfigured;
    public bool CanEdit => Baseline.CanEdit;
    public bool NeedsAdministrator => Baseline.NeedsAdministrator;
    public bool TakesEffect => Baseline.TakesEffect;
    // The dot and word at the right of a row (green Recommended, amber Somewhat recommended), and the text of its "?" help popup.
    public string LevelLabel => Advanced ? "Somewhat recommended" : "Recommended";
    public string HelpText => HasNotice ? Description + "\n\n" + Status : Description;
    public bool IsBlocked => !Baseline.CanEdit;
    public bool Advanced => Baseline.Definition.Advanced;
    public string Topic => Baseline.Definition.Topic;
    private bool selected = state.Enabled;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
