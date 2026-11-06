using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VoxForge.Audio;

namespace VoxForge;

/// <summary>A key plus modifiers. Serialised as "modifiers:key" using the WPF enum names.</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public string Serialize() => $"{(int)Modifiers}:{Key}";

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var parts = text.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out int mods) || !Enum.TryParse(parts[1], out Key key)) return null;
        return new Hotkey((ModifierKeys)mods, key);
    }

    /// <summary>Keys that are safe to use alone, without stealing normal typing.</summary>
    public static bool CanStandAlone(Key key) =>
        key is >= Key.F1 and <= Key.F24
        || key is >= Key.NumPad0 and <= Key.Divide
        || key is Key.Insert or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Pause or Key.Scroll;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Maj");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => $"Pavé {(int)(key - Key.NumPad0)}",
        Key.Add => "Pavé +",
        Key.Subtract => "Pavé -",
        Key.Multiply => "Pavé *",
        Key.Divide => "Pavé /",
        Key.Decimal => "Pavé .",
        Key.Space => "Espace",
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.PageUp => "Page préc.",
        Key.PageDown => "Page suiv.",
        Key.Insert => "Inser",
        Key.Home => "Début",
        Key.End => "Fin",
        Key.Scroll => "Arrêt défil",
        _ => key.ToString(),
    };
}

/// <summary>System-wide hotkeys through RegisterHotKey: they fire even while a game or Discord has focus.</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly IntPtr handle;
    private readonly HwndSource source;
    private readonly Dictionary<int, Action> actions = new();
    private int nextId = 1;

    public HotkeyManager(Window window)
    {
        handle = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(handle);
        source.AddHook(WndProc);
    }

    /// <summary>False when Windows refuses the combination (usually: another app already owns it).</summary>
    public bool Register(Hotkey hotkey, Action action)
    {
        uint mods = ModNoRepeat;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= ModAlt;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Control)) mods |= ModControl;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= ModShift;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= ModWin;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);

        int id = nextId++;
        if (!RegisterHotKey(handle, id, mods, vk)) return false;
        actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in actions.Keys) UnregisterHotKey(handle, id);
        actions.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        source.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>Something the user can bind a hotkey to: an app action, a voice or a pad. Label is UI text.</summary>
public sealed class HotkeyTarget : Observable
{
    private string hotkeyText = "";

    public HotkeyTarget(string id, string label, Action run)
    {
        Id = id;
        Label = label;
        Run = run;
    }

    public string Id { get; }
    public string Label { get; }
    public Action Run { get; }

    public string HotkeyText
    {
        get => hotkeyText;
        set
        {
            hotkeyText = value;
            Notify();
            Notify(nameof(ButtonText));
        }
    }

    public string ButtonText => string.IsNullOrEmpty(hotkeyText) ? "Aucun" : hotkeyText;
}

/// <summary>Dialog that records one key combination.</summary>
public static class HotkeyCapture
{
    public enum Outcome { Cancelled, Cleared, Set }

    public static (Outcome Outcome, Hotkey Hotkey) Ask(Window owner, string targetLabel)
    {
        var result = (Outcome.Cancelled, default(Hotkey));

        var preview = new TextBlock
        {
            Text = "…",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)owner.FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 16),
        };
        var hint = new TextBlock
        {
            Text = "Appuie sur la combinaison voulue (ex. Ctrl + Alt + 1, ou une touche F1-F12 / pavé numérique seule).\nÉchap : annuler · Retour arrière : supprimer le raccourci.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)owner.FindResource("MutedBrush"),
            FontSize = 12,
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = $"Pour « {targetLabel} »", Foreground = (Brush)owner.FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new Border
        {
            Background = (Brush)owner.FindResource("Panel2Brush"),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 12, 0, 14),
            Child = preview,
        });
        panel.Children.Add(hint);

        var dialog = DialogShell.Create(owner, "Raccourci clavier", panel, 400);

        dialog.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;

            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                preview.Text = mods == ModifierKeys.None ? "…" : new Hotkey(mods, Key.None).ToString().Replace(" + None", " + …");
                return;
            }
            if (mods == ModifierKeys.None && key == Key.Escape)
            {
                dialog.Close();
                return;
            }
            if (mods == ModifierKeys.None && key is Key.Back or Key.Delete)
            {
                result = (Outcome.Cleared, default);
                dialog.Close();
                return;
            }
            if (mods == ModifierKeys.None && !Hotkey.CanStandAlone(key))
            {
                preview.Text = "Ajoute Ctrl, Alt ou Maj";
                return;
            }
            result = (Outcome.Set, new Hotkey(mods, key));
            dialog.Close();
        };

        dialog.ShowDialog();
        return result;
    }
}
