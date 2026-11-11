using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Hush;

// Review order and group outlines (owner, November 11, 2026): inside a category the files from one folder stay together, the folder holding the most comes first and
// the largest file leads each folder; the category cards go largest first inside their risk level; a fully selected group (Safe, Review, Advanced) is outlined in its color.
internal static class ReviewOrderChecks
{
    private static CleanupItem File(string folder, string name, long bytes, string category = "Temp", bool selected = false, string group = "Fixture") =>
        new() { Path = folder + "\\" + name, Root = folder, Group = group, Kind = CleanupKind.Temp, Bytes = bytes, CategoryId = category, Selected = selected };

    internal static void Run(Action<bool, string> check)
    {
        var files = new List<CleanupItem>
        {
            File(@"C:\a", "a-small.tmp", 5), File(@"C:\b", "b-only.tmp", 100), File(@"C:\a", "a-big.tmp", 50), File(@"C:\c", "c2.tmp", 30), File(@"C:\c", "c1.tmp", 30),
        };
        var ordered = ReviewCategory.OrderBySizeAndLocation(files).Select(file => file.FileName).ToArray();
        check(ordered.SequenceEqual(["b-only.tmp", "c1.tmp", "c2.tmp", "a-big.tmp", "a-small.tmp"]),
            "Files are grouped by folder, the folder holding the most comes first, and the largest file leads each folder (" + string.Join(", ", ordered) + ")");
        // Several apps in one card: the app holding the most comes first, then its folders and files in the same order.
        var apps = new List<CleanupItem> { File(@"C:\x", "x1", 40, group: "App X"), File(@"C:\y", "y1", 30, group: "App Y"), File(@"C:\y", "y2", 70, group: "App Y"), File(@"C:\x", "x2", 10, group: "App X") };
        check(ReviewCategory.OrderBySizeAndLocation(apps).Select(file => file.FileName).SequenceEqual(["y2", "y1", "x1", "x2"]), "With several apps in one card, the app holding the most comes first, then folders and files largest first");
        var created = ReviewCategory.Create();
        check(ReviewCategory.AppDownloadIds.Length == 6 && ReviewCategory.AppDownloadIds.All(id => ReviewCategory.SafetyOf(id) == SafetyLevel.Advanced) && created.All(category => category.Id != "AppDownloads")
            && created.Count(category => ReviewCategory.AppDownloadIds.Contains(category.Id)) == 6 && created.Where(category => ReviewCategory.AppDownloadIds.Contains(category.Id)).All(category => category.Title.Length <= 34),
            "The long Advanced card is split into six short cards, one per app (Unity, Autodesk Fusion 360, CapCut, Codex, Zed, GitHub Desktop), all Advanced");
        check(ReviewCategory.OrderBySizeAndLocation([]).Count == 0 && ReviewCategory.OrderBySizeAndLocation([File(@"C:\x", "one.tmp", 1)]).Count == 1, "Empty and single-file categories need no ordering");
    }

    internal static void RunUi(CleanupEngine engine, Action<bool, string> check)
    {
        var window = new MainWindow(engine) { ShowActivated = false, ShowInTaskbar = false, ReviewShowsAllCategories = false };
        try
        {
            window.Show();
            var items = new List<CleanupItem>
            {
                File(@"C:\t", "temp.tmp", 10, "Temp", true), File(@"C:\th", "thumb.db", 500, "Thumbnails", true),        // Safe: Thumbnails is larger, so it comes first
                File(@"C:\p", "p.pf", 100, "Prefetch"), File(@"C:\d", "shader.bin", 900, "DirectX"), File(@"C:\d", "shader2.bin", 50, "DirectX"),   // Review: DirectX first
                File(@"C:\r", "recycle", 700, "RecycleBin"),
            };
            window.LoadReviewItems(items);
            window.ShowReviewPage(true); UiLayoutChecks.Wait(350);
            var shown = CollectionViewSource.GetDefaultView(window.Categories).Cast<ReviewCategory>().Select(category => category.Id).ToList();
            check(shown.IndexOf("Thumbnails") < shown.IndexOf("Temp") && shown.IndexOf("DirectX") < shown.IndexOf("Prefetch")
                && shown.IndexOf("Temp") < shown.IndexOf("DirectX") && shown.IndexOf("Prefetch") < shown.IndexOf("RecycleBin"),
                "Categories stay Safe, then Review, then Advanced, and inside each level the one holding the most comes first (" + string.Join(", ", shown) + ")");
            var directX = window.Categories.First(category => category.Id == "DirectX");
            check(directX.Files.Select(file => file.FileName).SequenceEqual(["shader.bin", "shader2.bin"]), "The files inside a category are listed largest first");

            // The three group rows: a fully selected group is outlined in its own color; a group that is not selected has no outline.
            var root = (FrameworkElement)window.Content;
            UiLayoutChecks.Layout(root, new Size(920, 820)); UiLayoutChecks.Wait(500);
            var outlines = Descendants<Border>((DependencyObject)window.FindName("ReviewGroupsList")).Where(border => border.Name == "GroupOutline").ToList();
            check(outlines.Count == 3, "The overview shows three group rows, each with an outline layer");
            Color Ink(Border border) => ((SolidColorBrush)border.BorderBrush).Color;
            Color Accent(SafetyLevel level) => (Color)ColorConverter.ConvertFromString(SafetyPalette.Text(level, false));
            check(window.ReviewGroups.Count == 3 && Ink(outlines[0]) == Accent(SafetyLevel.Safe) && Ink(outlines[1]).A == 0 && Ink(outlines[2]).A == 0,
                "Only the fully selected Safe group is outlined, in the Safe green");
            foreach (var category in window.Categories) category.Selected = true;
            UiLayoutChecks.Wait(500);
            check(Ink(outlines[0]) == Accent(SafetyLevel.Safe) && Ink(outlines[1]) == Accent(SafetyLevel.Review) && Ink(outlines[2]) == Accent(SafetyLevel.Advanced),
                "With everything selected, Safe is outlined green, Review amber and Advanced red");
            ThemeMotionChecks.Capture(root, "review-groups-selected-dark");
            window.ToggleTheme(); UiLayoutChecks.Wait(600);
            Color AccentLight(SafetyLevel level) => (Color)ColorConverter.ConvertFromString(SafetyPalette.Text(level, true));
            check(Ink(outlines[0]) == AccentLight(SafetyLevel.Safe) && Ink(outlines[1]) == AccentLight(SafetyLevel.Review) && Ink(outlines[2]) == AccentLight(SafetyLevel.Advanced),
                "The outlines follow the Light theme's colors too");
            ThemeMotionChecks.Capture(root, "review-groups-selected-light");
            window.ToggleTheme(); UiLayoutChecks.Wait(500);
            foreach (var category in window.Categories) category.Selected = false;
            UiLayoutChecks.Wait(500);
            check(outlines.All(border => Ink(border).A == 0), "Clearing the selection removes every outline");
        }
        finally { window.Close(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    // Sub-headings, the wheel inside a card's list and the chevron steps, in the real window.
    internal static void RunListUi(CleanupEngine engine, Action<bool, string> check)
    {
        var window = new MainWindow(engine) { ShowActivated = false, ShowInTaskbar = false, ReviewShowsAllCategories = true };
        try
        {
            window.Show();
            var items = new List<CleanupItem>();
            for (int index = 0; index < 80; index++) items.Add(File(@"C:\dev\folder" + index % 5, "lib" + index + ".nupkg", 1000 + index, "DevCache", group: "Dev tools"));
            items.Add(File(@"C:\u", "user.tmp", 50, "Temp", group: "User temp")); items.Add(File(@"C:\u", "user2.tmp", 40, "Temp", group: "User temp")); items.Add(File(@"C:\w", "win.tmp", 30, "Temp", group: "Windows temp"));
            for (int index = 0; index < 40; index++) items.Add(File(@"C:\c" + index, "cache" + index, 500 + index, "Cache", group: "Cache " + index % 3));
            window.LoadReviewItems(items);
            var temp = window.Categories.First(category => category.Id == "Temp"); var dev = window.Categories.First(category => category.Id == "DevCache");
            check(temp.Rows.Count == 5 && temp.Rows[0] is ReviewSubHeading { Title: "User temp" } && temp.Rows[3] is ReviewSubHeading { Title: "Windows temp" } && dev.Rows.Count == 80 && !dev.Rows.OfType<ReviewSubHeading>().Any(),
                "A card holding several apps shows a sub-heading before each app's files (largest app first); a card with one app has none");
            foreach (var category in window.Categories) category.Expanded = category.HasFiles;
            window.ShowReviewPage(true); UiLayoutChecks.Wait(500); UiLayoutChecks.Layout((FrameworkElement)window.Content, new Size(920, 820)); UiLayoutChecks.Wait(500);
            var outer = (ScrollViewer)window.FindName("ReviewScroll");
            var list = Descendants<ListBox>((DependencyObject)window.FindName("CategoriesList")).First(box => box.Items.Count == 80);
            var inner = Descendants<ScrollViewer>(list).First();
            check(outer.ScrollableHeight > 0 && inner.ScrollableHeight > 0 && inner.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                $"The test list scrolls inside a page that scrolls (page {outer.ScrollableHeight:0} DIP, list {inner.ScrollableHeight:0} DIP)");

            outer.ScrollToTop(); UiLayoutChecks.Wait(300); ThemeMotionChecks.Capture((FrameworkElement)window.Content, "review-subheadings-dark");
            // The wheel inside the list scrolls the list, not the page behind it.
            list.BringIntoView(); UiLayoutChecks.Wait(300);
            double pageBefore = outer.VerticalOffset;
            var row = Descendants<ListBoxItem>(list).First();
            for (int notch = 0; notch < 3; notch++) { Wheel(row, -120); UiLayoutChecks.Wait(120); }
            UiLayoutChecks.PumpUntil(() => !SmoothScroll.IsGliding(inner));
            check(inner.VerticalOffset > 20 && Math.Abs(outer.VerticalOffset - pageBefore) < 0.5,
                $"The wheel over a category's list scrolls that list ({inner.VerticalOffset:0} DIP) and leaves the page where it was ({pageBefore:0} to {outer.VerticalOffset:0})");

            // The chevrons: each click moves the list by exactly one item; up returns it.
            inner.ScrollToTop(); UiLayoutChecks.Wait(150);
            var down = Descendants<System.Windows.Controls.Primitives.RepeatButton>(inner).First(button => ScrollSteps.GetDirection(button) == 1);
            var up = Descendants<System.Windows.Controls.Primitives.RepeatButton>(inner).First(button => ScrollSteps.GetDirection(button) == -1);
            double firstRow = Descendants<ListBoxItem>(list).First().ActualHeight;
            down.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); UiLayoutChecks.PumpUntil(() => !SmoothScroll.IsGliding(inner));
            double afterOne = inner.VerticalOffset;
            down.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); UiLayoutChecks.PumpUntil(() => !SmoothScroll.IsGliding(inner));
            double afterTwo = inner.VerticalOffset;
            check(Math.Abs(afterOne - firstRow) < 2 && afterTwo > afterOne + 20 && afterTwo < afterOne + 2 * firstRow + 40,
                $"Each click of the down chevron moves the list one item (first item {firstRow:0} DIP: {afterOne:0}, then {afterTwo:0})");
            up.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); UiLayoutChecks.PumpUntil(() => !SmoothScroll.IsGliding(inner));
            check(Math.Abs(inner.VerticalOffset - afterOne) < 2, $"The up chevron moves it back by one item ({inner.VerticalOffset:0})");
            up.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); UiLayoutChecks.PumpUntil(() => !SmoothScroll.IsGliding(inner));
            up.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); UiLayoutChecks.Wait(200);
            check(inner.VerticalOffset < 1, "At the top the up chevron stays at the top");
            ThemeMotionChecks.Capture((FrameworkElement)window.Content, "review-list-chevrons-dark");
        }
        finally { window.Close(); }
    }

    private static bool Wheel(UIElement target, int delta)
    {
        var args = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = target };
        target.RaiseEvent(args);
        return args.Handled;
    }
}
