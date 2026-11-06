using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace VoxForge;

/// <summary>Borderless, rounded dialog window in the app's theme.</summary>
public static class DialogShell
{
    public static Window Create(Window owner, string title, UIElement body, double width = 400)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        };
        var stack = new StackPanel();
        stack.Children.Add(titleText);
        stack.Children.Add(body);

        var card = new Border
        {
            Background = (Brush)owner.FindResource("PanelBrush"),
            BorderBrush = (Brush)owner.FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(22, 18, 22, 20),
            Margin = new Thickness(18),
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
        };

        var dialog = new Window
        {
            Title = title,
            Owner = owner,
            Content = card,
            Width = width + 36,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Foreground = (Brush)owner.FindResource("TextBrush"),
            FontFamily = owner.FontFamily,
            FontSize = 13,
            Opacity = 0,
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Border or TextBlock) dialog.DragMove();
        };
        dialog.Loaded += (_, _) =>
        {
            var scale = new ScaleTransform(0.96, 0.96);
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = scale;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var time = TimeSpan.FromMilliseconds(160);
            dialog.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, time));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, time) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, time) { EasingFunction = ease });
        };
        return dialog;
    }

    /// <summary>
    /// Themed message box. <paramref name="buttons"/> are listed left to right; the last one is the main
    /// (default) action. Returns the index of the clicked button, or -1 when the dialog is closed with Escape.
    /// </summary>
    public static int Show(Window owner, string title, string message, params string[] buttons)
    {
        if (buttons.Length == 0) buttons = new[] { "OK" };
        Window? dialog = null;
        int choice = -1;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            bool main = i == buttons.Length - 1;
            var button = new Button
            {
                Content = buttons[i],
                MinWidth = 96,
                Margin = new Thickness(0, 0, main ? 0 : 10, 0),
                IsDefault = main,
                IsCancel = i == 0 && buttons.Length > 1,
            };
            if (main) button.Style = (Style)owner.FindResource("PrimaryButton");
            button.Click += (_, _) =>
            {
                choice = index;
                dialog!.DialogResult = true;
            };
            row.Children.Add(button);
        }

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
        panel.Children.Add(row);
        dialog = Create(owner, title, panel, 440);
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.DialogResult = false;
        };
        dialog.ShowDialog();
        return choice;
    }
}

/// <summary>Animated pixel scrolling for a ScrollViewer: the mouse wheel glides instead of jumping.</summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private sealed class State
    {
        public double Target;
        public bool Running;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        viewer.PreviewMouseWheel -= OnWheel;
        if ((bool)e.NewValue) viewer.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (InnerScroller(e.OriginalSource as DependencyObject, viewer)) return; // a nested list or drop-down scrolls itself
        e.Handled = true;

        var state = States.GetOrCreateValue(viewer);
        double from = state.Running ? state.Target : viewer.VerticalOffset;
        state.Target = Math.Clamp(from - e.Delta * 0.85, 0, viewer.ScrollableHeight);
        if (state.Running) return;

        state.Running = true;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            double current = viewer.VerticalOffset;
            double gap = state.Target - current;
            if (Math.Abs(gap) < 0.6 || !viewer.IsVisible)
            {
                viewer.ScrollToVerticalOffset(state.Target);
                state.Running = false;
                CompositionTarget.Rendering -= tick;
                return;
            }
            viewer.ScrollToVerticalOffset(current + gap * 0.22);
        };
        CompositionTarget.Rendering += tick;
    }

    private static bool InnerScroller(DependencyObject? source, ScrollViewer outer)
    {
        for (var node = source; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, outer)) return false;
            if (node is ScrollViewer inner && inner.ScrollableHeight > 0) return true;
        }
        return true; // not inside this viewer at all (a popup): leave it alone
    }
}

/// <summary>Native touches for the custom-chrome main window (our own window only).</summary>
public static class WindowLook
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    /// <summary>Dark frame and Windows 11 rounded corners; silently ignored on older Windows.</summary>
    public static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        int dark = 1, round = DwmwcpRound;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // No DWM: keep the default frame.
        }
    }

    /// <summary>Fade and slide a page in.</summary>
    public static void Reveal(FrameworkElement page)
    {
        if (page.RenderTransform is not TranslateTransform shift)
            page.RenderTransform = shift = new TranslateTransform();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var time = TimeSpan.FromMilliseconds(240);
        page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, time) { EasingFunction = ease });
    }
}
