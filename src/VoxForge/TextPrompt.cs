using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VoxForge;

/// <summary>Small themed dialog that asks for one line of text.</summary>
public static class TextPrompt
{
    public static string? Ask(Window owner, string title, string message, string initial = "", string? hint = null, string okText = "OK")
    {
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 96, Style = (Style)owner.FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 96, Margin = new Thickness(0, 0, 10, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(input);
        if (hint is not null)
            panel.Children.Add(new TextBlock { Text = hint, Style = (Style)owner.FindResource("Hint"), Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(buttons);

        var dialog = DialogShell.Create(owner, title, panel, hint is null ? 380 : 460);
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
