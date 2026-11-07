using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hush;

internal static class ButtonVisualChecks
{
    internal static void Run(MainWindow themeOwner)
    {
        bool originalTheme = themeOwner.IsLightTheme;
        var surface = new Grid(); surface.SetResourceReference(Panel.BackgroundProperty, "WindowBrush");
        var grid = new Grid { Margin = new Thickness(24) }; surface.Children.Add(grid);
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(90) });
        for (int col = 0; col < 3; col++) grid.ColumnDefinitions.Add(new());
        grid.RowDefinitions.Add(new() { Height = new GridLength(28) });
        for (int row = 0; row < 4; row++) grid.RowDefinitions.Add(new() { Height = new GridLength(64) });
        var buttons = new Button[4, 3];
        void Add(UIElement element, int row, int col) { Grid.SetRow(element, row); Grid.SetColumn(element, col); grid.Children.Add(element); }
        TextBlock Label(string text) => new() { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (text, col) in new[] { ("Normal action", 1), ("Clean", 2), ("Navigation", 3) })
        { var label = Label(text); label.HorizontalAlignment = HorizontalAlignment.Center; Add(label, 0, col); }
        string[] states = ["Idle", "Hover", "Pressed", "Disabled"];
        for (int row = 0; row < 4; row++)
        {
            Add(Label(states[row]), row + 1, 0);
            for (int col = 0; col < 3; col++)
            {
                var button = new Button { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (col == 0) button.Content = "Check for updates";
                if (col == 1) { button.Content = "Clean selected"; button.Style = (Style)Application.Current.Resources["DangerButton"]; }
                if (col == 2)
                {
                    button.Style = (Style)Application.Current.Resources["TextButton"];
                    var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    var arrow = new System.Windows.Shapes.Path { Data = Geometry.Parse("M15,7 L0,7 M7,0 L0,7 L7,14"), Style = (Style)Application.Current.Resources["NavigationIcon"] };
                    arrow.SetBinding(Shape.StrokeProperty, new Binding { Path = new PropertyPath("(0)", TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self });
                    content.Children.Add(arrow); content.Children.Add(new TextBlock { Text = "Back", VerticalAlignment = VerticalAlignment.Center });
                    button.Content = content;
                }
                buttons[row, col] = button; Add(button, row + 1, col + 1);
                if (row == 3) button.IsEnabled = false;
            }
        }
        var host = new Window { Title = "Hush button verification", Width = 770, Height = 340,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, Content = surface };
        void Hover(Button button, bool enter) => button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
            { RoutedEvent = enter ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });
        SolidColorBrush Fill(Button button) => (SolidColorBrush)((Border)button.Template.FindName("Surface", button)).Background;
        byte[] Pixels()
        {
            surface.UpdateLayout();
            var action = buttons[1, 0];
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                drawing.DrawRectangle(new VisualBrush(action), null, new Rect(0, 0, action.ActualWidth, action.ActualHeight));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(action.ActualWidth), (int)Math.Ceiling(action.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
        }
        void RenderFor(int milliseconds)
        {
            var sampler = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            sampler.Tick += (_, _) => Pixels(); sampler.Start();
            try { Wait(milliseconds); } finally { sampler.Stop(); }
        }
        try
        {
            host.Show();
            foreach (bool light in new[] { false, true })
            {
                if (themeOwner.IsLightTheme != light) themeOwner.ToggleTheme();
                RenderFor(340);
                foreach (var button in buttons) { Hover(button, false); Motion.Ensure(button); }
                Keyboard.ClearFocus();
                var action = buttons[1, 0];
                var baseFill = ((SolidColorBrush)Application.Current.Resources["RaisedBrush"]).Color;
                WaitUntil(() => Fill(action).Color == baseFill);
                surface.UpdateLayout();
                var bounds = action.RenderSize;
                var previousPixels = Pixels();
                var colors = new HashSet<string> { baseFill.ToString() };
                int changedFrames = 0;
                var sampler = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                sampler.Tick += (_, _) =>
                {
                    var pixels = Pixels();
                    if (!previousPixels.SequenceEqual(pixels)) changedFrames++;
                    previousPixels = pixels; colors.Add(Fill(action).Color.ToString());
                };
                sampler.Start(); Hover(action, true);
                try { Wait(420); } finally { sampler.Stop(); }
                Require(colors.Count >= 3 && changedFrames >= 2 && Fill(action).Color.ToString() != baseFill.ToString(),
                    $"Button hover traverses distinct intermediate rendered pixels before settling (light={light}, colors={colors.Count}, frames={changedFrames})");
                Hover(action, false); WaitUntil(() => Fill(action).Color == baseFill);
                Require(action.RenderSize == bounds && action.RenderTransform.Value.IsIdentity,
                    "Hover settles to the original surface without changing geometry");
                for (int col = 0; col < 3; col++)
                {
                    Hover(buttons[1, col], true); Hover(buttons[2, col], true);
                    Motion.PreviewInteraction(buttons[2, col], true, true);
                }
                RenderFor(420);
                Require(Fill(buttons[2, 0]).Color != Fill(buttons[1, 0]).Color,
                    $"Press uses a restrained distinct surface response (hover={Fill(buttons[1, 0]).Color}, press={Fill(buttons[2, 0]).Color}, light={light})");
                var navigation = buttons[1, 2]; var navContent = (StackPanel)navigation.Content;
                var navArrow = (Shape)navContent.Children[0]; var navLabel = (TextBlock)navContent.Children[1];
                WaitUntil(() => navLabel.Foreground.ToString() == Application.Current.Resources["TextBrush"].ToString());
                Require(navLabel.Foreground.ToString() == Application.Current.Resources["TextBrush"].ToString()
                    && navArrow.Stroke.ToString() == navLabel.Foreground.ToString(), "Navigation text and vector arrow share the same eased hover color");
                foreach (var button in buttons)
                {
                    Require(button.Height == 44 && button.FontSize == 16 && button.RenderTransform.Value.IsIdentity,
                        "Shared actions retain their type size, height and no-scaling geometry in every state");
                }
                Require(Fill(navigation).Color.A == 0 && ((Border)navigation.Template.FindName("GlowUnderlay", navigation)).Opacity == 0,
                    "Transparent navigation gains neither a surface wash nor underlighting");
                var clean = buttons[1, 1]; var halo = (DropShadowEffect)((Border)clean.Template.FindName("GlowHalo", clean)).Effect;
                Wait(420);
                double hoverGlow = halo.Opacity;
                Require(Fill(clean).Color.A <= 16 && halo.ShadowDepth == 0 && halo.BlurRadius == 18
                    && clean.BorderThickness.Left == 1, "Clean preserves its thin outline and outer-only light without washing the interior, and hover widens that light");
                Hover(clean, false); Wait(420);
                Require(halo.BlurRadius == 12 && halo.Opacity < hoverGlow - 0.05, "Clean's own-color glow visibly lifts on hover and settles back when the pointer leaves");
                Hover(clean, true); Wait(420);
                Require(buttons[3, 0].Opacity < 0.5 && Fill(buttons[3, 0]).Color == ((SolidColorBrush)Application.Current.Resources["RaisedBrush"]).Color,
                    "Disabled actions settle quietly without active fill feedback");
                Motion.SetGlow(buttons[0, 0], 0);
                Require(((Border)buttons[0, 0].Template.FindName("GlowUnderlay", buttons[0, 0])).Opacity == 0,
                    "Explicit zero glow also removes the source underlay on opaque buttons");
                Motion.SetGlow(buttons[0, 0], 0.08);
                if (Environment.GetEnvironmentVariable("HUSH_PREVIEW_DIRECTORY") is string directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(System.IO.Path.Combine(directory, $"buttons-{(light ? "light" : "dark")}.png")); encoder.Save(stream);
                }
            }
            Console.WriteLine("PASS: Both themes' rendered button hover, press, disable, retargeting, neutral surfaces, matched navigation ink and isolated Clean glow");
        }
        finally
        {
            host.Close();
            if (themeOwner.IsLightTheme != originalTheme) themeOwner.ToggleTheme();
        }
    }

    private static void WaitUntil(Func<bool> complete)
    {
        var frame = new DispatcherFrame(); var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (complete() || clock.ElapsedMilliseconds >= 2000) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Require(complete(), "Button transition did not settle within two seconds");
    }

    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false; timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
