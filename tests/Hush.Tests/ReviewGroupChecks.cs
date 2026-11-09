using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hush;

// The Review page's three color groups (0.9): Safe (green), Review (amber), Advanced (red); clicking one opens its categories, Back returns.
internal static class ReviewGroupChecks
{
    internal static void RunUi(CleanupEngine engine, string fixture, Action<bool, string> check)
    {
        bool previous = MainWindow.ReviewShowsAllByDefault;
        MainWindow.ReviewShowsAllByDefault = false;
        var window = new MainWindow(engine, preferencesPath: Path.Combine(fixture, "review-group-preferences.json")) { ShowActivated = false, ShowInTaskbar = false };
        MainWindow.ReviewShowsAllByDefault = previous;
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            window.LoadReviewItems(UiLayoutChecks.PreviewItemsForGroups()); window.ShowReviewPage(true); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            UiLayoutChecks.Layout(root, new Size(920, 820));
            var groupsView = (FrameworkElement)window.FindName("ReviewGroupsView");
            var listView = (FrameworkElement)window.FindName("CategoryListView");
            var groups = window.ReviewGroups.Where(group => group.HasItems).ToList();
            check(groupsView.Visibility == Visibility.Visible && listView.Visibility == Visibility.Collapsed && window.OpenGroup == null
                && groups.Select(group => group.Title).SequenceEqual(["Safe", "Review", "Advanced"]), "Review opens on three color groups, Safe then Review then Advanced");
            check(groups.Sum(group => group.Count) == window.ReviewItems.Count && groups.Sum(group => group.SelectedCount) == window.ReviewItems.Count(item => item.Selected)
                && groups.All(group => group.SelectedCount <= group.Count) && groups.All(group => group.TotalLabel.Length > 0), "Each group counts its items and its selected items");
            var rows = Descendants<Button>(groupsView).Where(button => button.Tag is ReviewGroupSummary).ToList();
            check(rows.Count == 3 && Descendants<TextBlock>(groupsView).Any(text => text.Text == groups[0].TotalLabel), "Each group is one row with its size and counts");
            string sizes = string.Join(",", rows.Select(button => button.ActualWidth.ToString("0") + "x" + button.ActualHeight.ToString("0") + " vis=" + button.IsVisible + " op=" + button.Opacity));
            check(rows.All(button => button.ActualHeight > 60 && button.ActualWidth > 400 && button.IsVisible), "Each group row is laid out and visible (" + sizes + "; view " + groupsView.ActualWidth.ToString("0") + "x" + groupsView.ActualHeight.ToString("0") + ")");
            var accents = groups.Select(group => ((SolidColorBrush)group.Accent).Color).Distinct().Count();
            check(accents == 3, "The three groups use the three traffic-light colors");
            Save(window, root, "review-groups");

            // Open Review (amber): only its categories are listed, and Select all stays inside the group.
            var amber = groups[1];
            rows.Single(button => button.Tag == amber).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UiLayoutChecks.Wait(360); UiLayoutChecks.Layout(root, new Size(920, 820));
            var sections = Descendants<Expander>(root).Where(section => section.DataContext is ReviewCategory && section.IsVisible).Select(section => (ReviewCategory)section.DataContext).ToList();
            check(window.OpenGroup == SafetyLevel.Review && listView.Visibility == Visibility.Visible && groupsView.Visibility == Visibility.Collapsed
                && sections.Count > 0 && sections.All(category => category.Safety == SafetyLevel.Review), "Clicking a group lists only its categories");
            Save(window, root, "review-group-open");
            var selectAll = (CheckBox)window.FindName("SelectAllBox");
            int safeBefore = window.Categories.Where(category => category.Safety == SafetyLevel.Safe).Sum(category => category.SelectedCount);
            selectAll.IsChecked = true; selectAll.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            check(window.Categories.Where(category => category.Safety == SafetyLevel.Review && category.HasFiles).All(category => category.Selected == true)
                && window.Categories.Where(category => category.Safety == SafetyLevel.Advanced).All(category => category.SelectedCount == 0)
                && window.Categories.Where(category => category.Safety == SafetyLevel.Safe).Sum(category => category.SelectedCount) == safeBefore, "Select all in a group selects that group only");

            // Back returns to the groups (still on Review); Back again leaves Review.
            var back = (Button)window.FindName("BackButton");
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UiLayoutChecks.Wait(360); UiLayoutChecks.Layout(root, new Size(920, 820));
            check(window.OpenGroup == null && groupsView.Visibility == Visibility.Visible && ((FrameworkElement)window.FindName("ReviewPage")).Visibility == Visibility.Visible
                && window.ReviewGroups.Single(group => group.Level == SafetyLevel.Review).SelectedCount == amber.Count, "Back from a group returns to the three groups, which show the new selection");
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            check(((FrameworkElement)window.FindName("ReviewPage")).Visibility == Visibility.Collapsed, "Back from the groups leaves Review");

            // Advanced group: opened like the others; a group that empties after cleaning falls back to the overview.
            window.OpenReviewGroup(SafetyLevel.Advanced); window.ShowReviewPage(true);
            check(window.OpenGroup == SafetyLevel.Advanced, "A group can be opened directly");
            window.LoadReviewItems(UiLayoutChecks.PreviewItemsForGroups().Where(item => item.Safety != SafetyLevel.Advanced).ToArray());
            check(window.OpenGroup == null && window.ReviewGroups.All(group => group.Level != SafetyLevel.Advanced || !group.HasItems), "A group with nothing left disappears and the page returns to the overview");
            window.LoadReviewItems([]);
            check(groupsView.Visibility == Visibility.Collapsed && listView.Visibility == Visibility.Collapsed, "With nothing found neither the groups nor the list show");

            // ---- Settings sections: two cards side by side, one column when narrow (0.9) ----
            window.ShowReviewPage(false); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            ((Button)window.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); UiLayoutChecks.PumpUntil(() => !window.IsPageTransitioning);
            var left = (FrameworkElement)window.FindName("SettingsLeftColumn"); var right = (FrameworkElement)window.FindName("SettingsRightColumn");
            UiLayoutChecks.Layout(root, new Size(920, 820)); UiLayoutChecks.Wait(60); UiLayoutChecks.Layout(root, new Size(920, 820));
            double Left(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)).Left;
            double Top(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)).Top;
            check(window.SettingsInTwoColumns && Left(right) > Left(left) + 300 && Math.Abs(Top(right) - Top(left)) < 2, "Settings sections sit in two columns side by side in a normal window");
            window.ApplySettingsColumns(600); UiLayoutChecks.Layout(root, new Size(920, 820));
            check(!window.SettingsInTwoColumns && Math.Abs(Left(right) - Left(left)) < 2 && Top(right) > Top(left) + 100, "In a narrow window the sections stack into one column");
            window.ApplySettingsColumns(900);
        }
        finally { window.Close(); }
    }

    private static void Save(MainWindow window, FrameworkElement root, string name)
    {
        if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is not string directory) return;
        Directory.CreateDirectory(directory);
        foreach (bool light in new[] { false, true })
        {
            if (window.IsLightTheme != light) window.ToggleTheme();
            UiLayoutChecks.Wait(360); UiLayoutChecks.Layout(root, new Size(920, 820));
            UiLayoutChecks.Save(root, new Size(920, 820), Path.Combine(directory, $"{name}-{(light ? "light" : "dark")}.png"));
        }
        if (window.IsLightTheme) window.ToggleTheme();
        UiLayoutChecks.Wait(360);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
