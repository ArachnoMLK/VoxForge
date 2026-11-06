using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VoxForge;

/// <summary>Small themed dialog that asks for one line of text.</summary>
public static class TextPrompt
{
    public static string? Ask(Window owner, string title, string message, string initial = "")
    {
        var input = new TextBox
        {
            Text = initial,
            Margin = new Thickness(0, 10, 0, 16),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 14,
            Background = (Brush)owner.FindResource("Panel2Brush"),
            Foreground = (Brush)owner.FindResource("TextBrush"),
            CaretBrush = (Brush)owner.FindResource("TextBrush"),
            BorderBrush = (Brush)owner.FindResource("AccentBrush"),
        };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Style = (Style)owner.FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 10, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(input);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Owner = owner,
            Content = panel,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)owner.FindResource("PanelBrush"),
            Foreground = (Brush)owner.FindResource("TextBrush"),
            FontFamily = owner.FontFamily,
            FontSize = 13,
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
            Keyboard.Focus(input);
        };

        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }
}
