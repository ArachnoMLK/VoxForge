using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using VoxForge.Audio;

namespace VoxForge;

public partial class MainWindow : Window
{
    private static readonly AudioDevice NoDevice = new("", "(Aucune)");
    private const string SoundFilter = "Sons|*.wav;*.mp3;*.m4a;*.wma;*.flac;*.aiff;*.aif;*.aac|Tous les fichiers|*.*";

    private readonly AudioEngine engine = new();
    private readonly AppSettings settings = AppSettings.Load();
    private readonly ObservableCollection<SoundBoardPage> boards = new();
    private readonly DispatcherTimer meterTimer;
    private readonly List<HotkeyTarget> actionTargets;
    private HotkeyManager? hotkeys;
    private TrayIcon? tray;
    private bool exiting;
    private double inputLevel;
    private double outputLevel;
    private bool loading = true;
    private readonly ListCollectionView voiceView;
    private CancellationTokenSource? download;

    /// <summary>A voice category chip; a null key shows every voice.</summary>
    public sealed record CategoryChip(string? Key, string Name, int Count);

    public MainWindow()
    {
        InitializeComponent();

        voiceView = new ListCollectionView(VoicePreset.All.ToList());
        voiceView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VoicePreset.Category)));
        PresetList.ItemsSource = voiceView;
        var chips = new List<CategoryChip> { new(null, "Toutes", VoicePreset.All.Count) };
        chips.AddRange(VoicePreset.All.GroupBy(p => p.Category).Select(g => new CategoryChip(g.Key, g.Key, g.Count())));
        CategoryList.ItemsSource = chips;
        CategoryList.SelectedIndex = 0;
        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "");
        EffectList.ItemsSource = engine.Effects.All;
        BoardList.ItemsSource = boards;
        LoadBoards();
        RefreshDevices();
        ApplySettingsToUi();
        loading = false;

        engine.Faulted += message => Dispatcher.BeginInvoke(() =>
        {
            engine.Stop();
            UpdateRunState();
            SetStatus($"Erreur audio : {message}", warn: true);
        });

        meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) => UpdateMeters(), Dispatcher);
        meterTimer.Start();

        actionTargets = new List<HotkeyTarget>
        {
            new("action:toggleEffects", "Activer / couper les effets", ToggleEffects),
            new("action:toggleMute", "Couper / rétablir le micro", ToggleMute),
            new("action:stopSounds", "Arrêter tous les sons", () => engine.Soundboard.StopAll()),
            new("action:nextVoice", "Voix suivante", () => StepVoice(+1)),
            new("action:previousVoice", "Voix précédente", () => StepVoice(-1)),
            new("action:toggleEngine", "Démarrer / arrêter VoxForge", () => OnStartStop(this, new RoutedEventArgs())),
        };
        ActionHotkeys.ItemsSource = actionTargets;
        settings.Hotkeys ??= DefaultHotkeys();
        SourceInitialized += (_, _) =>
        {
            WindowLook.Apply(this);
            hotkeys = new HotkeyManager(this);
            ApplyHotkeys();
            tray = new TrayIcon((ImageSource)FindResource("LogoImage"),
                open: ShowFromTray,
                toggleEngine: () => OnStartStop(this, new RoutedEventArgs()),
                toggleEffects: ToggleEffects,
                toggleMute: ToggleMute,
                quit: QuitApp);
            UpdateTray();
        };
        Loaded += (_, _) => Navigate(settings.Page);
    }

    // ---------- Window chrome and navigation ----------

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // A maximised custom-chrome window overhangs the screen by the resize border.
        RootFrame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void Navigate(string? page)
    {
        var target = page switch
        {
            "effects" => NavEffects,
            "sounds" => NavSounds,
            "settings" => NavSettings,
            _ => NavVoices,
        };
        if (target.IsChecked == true) OnNavigate(target, new RoutedEventArgs());
        else target.IsChecked = true;
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (PageVoices is null) return; // still loading XAML
        var (page, key, title, subtitle) = sender == NavEffects ? ((FrameworkElement)PageEffects, "effects", "Effets", "Règle chaque effet à la main, ou combine-les.")
            : sender == NavSounds ? (PageSounds, "sounds", "Soundboards", "Joue des sons et des musiques dans ta voix.")
            : sender == NavSettings ? (PageSettings, "settings", "Réglages", "Micro, sortie, câble virtuel et raccourcis.")
            : (PageVoices, "voices", "Voix", "Choisis une voix, elle s'applique tout de suite.");
        foreach (var other in new FrameworkElement[] { PageVoices, PageEffects, PageSounds, PageSettings })
            other.Visibility = other == page ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
        settings.Page = key;
        WindowLook.Reveal(page);
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (voiceView is null) return;
        string? key = (CategoryList.SelectedItem as CategoryChip)?.Key;
        voiceView.Filter = key is null ? null : o => ((VoicePreset)o).Category == key;
        if (!loading) WindowLook.Reveal(PresetList);
    }

    // ---------- Notification area ----------

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void QuitApp()
    {
        exiting = true;
        Close();
    }

    private void UpdateTray() =>
        tray?.Update(engine.IsRunning, EffectsCheck.IsChecked == true, MuteCheck.IsChecked == true);

    private void OnTrayOptionChanged(object sender, RoutedEventArgs e) => settings.CloseToTray = TrayCheck.IsChecked == true;

    // ---------- Hotkeys ----------

    private static Dictionary<string, string> DefaultHotkeys()
    {
        var ctrlAlt = ModifierKeys.Control | ModifierKeys.Alt;
        return new Dictionary<string, string>
        {
            ["action:toggleEffects"] = new Hotkey(ctrlAlt, Key.E).Serialize(),
            ["action:toggleMute"] = new Hotkey(ctrlAlt, Key.M).Serialize(),
            ["action:stopSounds"] = new Hotkey(ctrlAlt, Key.S).Serialize(),
            ["action:nextVoice"] = new Hotkey(ctrlAlt, Key.Right).Serialize(),
            ["action:previousVoice"] = new Hotkey(ctrlAlt, Key.Left).Serialize(),
        };
    }

    private Dictionary<string, string> HotkeyMap => settings.Hotkeys ??= DefaultHotkeys();

    private static string VoiceKey(VoicePreset preset) => "voice:" + preset.Name;

    private static string PadKey(SoundClip clip) =>
        "pad:" + (clip.FilePath is null ? "builtin/" + clip.Name : Path.GetRelativePath(SoundBoardLibrary.Root, clip.FilePath));

    /// <summary>Re-registers every hotkey with Windows and refreshes the labels shown in the UI.</summary>
    private void ApplyHotkeys()
    {
        if (hotkeys is null) return;
        hotkeys.UnregisterAll();

        var targets = new Dictionary<string, (Action Run, Action<string> Show)>();
        foreach (var t in actionTargets) targets[t.Id] = (t.Run, text => t.HotkeyText = text);
        foreach (var p in VoicePreset.All) targets[VoiceKey(p)] = (() => SelectVoice(p), text => p.HotkeyText = text);
        foreach (var clip in boards.SelectMany(b => b.Clips))
            targets[PadKey(clip)] = (() => PlayClip(clip), text => clip.HotkeyText = text);

        foreach (var (_, target) in targets) target.Show("");

        var refused = new List<string>();
        foreach (var (id, serialized) in HotkeyMap)
        {
            if (!targets.TryGetValue(id, out var target) || Hotkey.Parse(serialized) is not { } hotkey) continue;
            if (hotkeys.Register(hotkey, target.Run))
                target.Show(hotkey.ToString());
            else
            {
                refused.Add(hotkey.ToString());
                target.Show(hotkey + " ⚠");
            }
        }
        if (refused.Count > 0)
            SetStatus($"Raccourci(s) déjà pris par une autre app : {string.Join(", ", refused)}", warn: true);
    }

    private void EditHotkey(string targetId, string label)
    {
        hotkeys?.UnregisterAll(); // so the combination reaches the dialog instead of firing
        var (outcome, hotkey) = HotkeyCapture.Ask(this, label);
        if (outcome == HotkeyCapture.Outcome.Set)
        {
            string serialized = hotkey.Serialize();
            foreach (var other in HotkeyMap.Where(kv => kv.Value == serialized && kv.Key != targetId).Select(kv => kv.Key).ToList())
                HotkeyMap.Remove(other);
            HotkeyMap[targetId] = serialized;
        }
        else if (outcome == HotkeyCapture.Outcome.Cleared)
        {
            HotkeyMap.Remove(targetId);
        }
        ApplyHotkeys();
    }

    private void ClearHotkey(string targetId)
    {
        HotkeyMap.Remove(targetId);
        ApplyHotkeys();
    }

    private void OnEditActionHotkey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HotkeyTarget target) EditHotkey(target.Id, target.Label);
    }

    private void OnEditVoiceHotkey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VoicePreset preset) EditHotkey(VoiceKey(preset), "Voix " + preset.Name);
    }

    private void OnClearVoiceHotkey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VoicePreset preset) ClearHotkey(VoiceKey(preset));
    }

    private void OnEditPadHotkey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SoundClip clip) EditHotkey(PadKey(clip), "Son " + clip.Name);
    }

    private void OnClearPadHotkey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SoundClip clip) ClearHotkey(PadKey(clip));
    }

    /// <summary>Hotkeys of pads in a board use paths relative to the boards root; keep them when the board moves.</summary>
    private void MoveBoardHotkeys(string oldName, string? newName)
    {
        string oldPrefix = "pad:" + oldName + Path.DirectorySeparatorChar;
        foreach (var key in HotkeyMap.Keys.Where(k => k.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            string value = HotkeyMap[key];
            HotkeyMap.Remove(key);
            if (newName is not null)
                HotkeyMap["pad:" + newName + Path.DirectorySeparatorChar + key[oldPrefix.Length..]] = value;
        }
    }

    private void ToggleEffects()
    {
        EffectsCheck.IsChecked = EffectsCheck.IsChecked != true;
        SetStatus(EffectsCheck.IsChecked == true ? "Effets activés" : "Effets coupés : voix naturelle", ok: engine.IsRunning);
    }

    private void ToggleMute()
    {
        MuteCheck.IsChecked = MuteCheck.IsChecked != true;
        SetStatus(MuteCheck.IsChecked == true ? "Micro coupé" : "Micro rétabli", ok: MuteCheck.IsChecked != true, warn: MuteCheck.IsChecked == true);
    }

    private void SelectVoice(VoicePreset preset)
    {
        if (!voiceView.PassesFilter(preset)) CategoryList.SelectedIndex = 0;
        PresetList.SelectedItem = preset;
        PresetList.ScrollIntoView(preset);
        SetStatus("Voix : " + preset.Name, ok: engine.IsRunning);
    }

    private void StepVoice(int direction)
    {
        var all = VoicePreset.All;
        int index = PresetList.SelectedItem is VoicePreset current ? IndexOf(all, current) : -1;
        int next = ((index + direction) % all.Count + all.Count) % all.Count;
        SelectVoice(all[next]);
    }

    private static int IndexOf(IReadOnlyList<VoicePreset> list, VoicePreset item)
    {
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }

    private SoundBoardPage? CurrentBoard => BoardList.SelectedItem as SoundBoardPage;

    // ---------- Devices ----------

    private void RefreshDevices()
    {
        bool wasLoading = loading;
        loading = true;

        var inputs = DeviceCatalog.Inputs();
        var outputs = DeviceCatalog.Outputs();

        MicCombo.ItemsSource = inputs;
        MicCombo.SelectedItem = inputs.FirstOrDefault(d => d.Id == settings.MicId)
            ?? inputs.FirstOrDefault(d => d.Id == DeviceCatalog.DefaultInputId())
            ?? inputs.FirstOrDefault();

        var outputChoices = new List<AudioDevice> { NoDevice };
        outputChoices.AddRange(outputs);
        OutputCombo.ItemsSource = outputChoices;
        OutputCombo.SelectedItem = (settings.OutputId is null ? null : outputChoices.FirstOrDefault(d => d.Id == settings.OutputId))
            ?? outputs.FirstOrDefault(d => d.IsVirtualCableInput)
            ?? NoDevice;

        MonitorCombo.ItemsSource = outputs;
        MonitorCombo.SelectedItem = outputs.FirstOrDefault(d => d.Id == settings.MonitorId)
            ?? outputs.FirstOrDefault(d => d.Id == DeviceCatalog.DefaultOutputId())
            ?? outputs.FirstOrDefault();

        bool cablePresent = outputs.Any(d => d.IsVirtualCableInput);
        CableHint.Text = cablePresent
            ? "Câble détecté. Sortie : « CABLE Input ». Dans Discord / OBS, choisis le micro « CABLE Output »."
            : "Câble non installé : Discord, OBS et tes jeux ne peuvent pas encore entendre ta voix forgée. En attendant, active « M'écouter » pour tester.";
        CableIcon.Text = cablePresent ? "\uE73E" : "\uE7BA";
        CableIcon.Foreground = (Brush)FindResource(cablePresent ? "OkBrush" : "WarnBrush");
        CableSteps.Visibility = cablePresent ? Visibility.Collapsed : Visibility.Visible;

        loading = wasLoading;
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        RememberDevices();
        RefreshDevices();
        if (engine.IsRunning) StartEngine();
    }

    private void OnDeviceChanged(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        RememberDevices();
        if (engine.IsRunning) StartEngine();
    }

    private void RememberDevices()
    {
        settings.MicId = (MicCombo.SelectedItem as AudioDevice)?.Id ?? settings.MicId;
        if (OutputCombo.SelectedItem is AudioDevice output) settings.OutputId = output.Id;
        settings.MonitorId = (MonitorCombo.SelectedItem as AudioDevice)?.Id ?? settings.MonitorId;
        settings.MonitorEnabled = MonitorCheck.IsChecked == true;
    }

    // ---------- Engine ----------

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (engine.IsRunning)
        {
            engine.Stop();
            UpdateRunState();
        }
        else
        {
            StartEngine();
        }
    }

    private void StartEngine()
    {
        if (MicCombo.SelectedItem is not AudioDevice mic)
        {
            SetStatus("Aucun micro trouvé", warn: true);
            return;
        }

        string? outputId = (OutputCombo.SelectedItem as AudioDevice)?.Id;
        if (string.IsNullOrEmpty(outputId)) outputId = null;
        string? monitorId = MonitorCheck.IsChecked == true ? (MonitorCombo.SelectedItem as AudioDevice)?.Id : null;
        if (monitorId == outputId) monitorId = null;

        if (outputId is null && monitorId is null)
        {
            SetStatus("Choisis une sortie ou active « M'écouter »", warn: true);
            return;
        }

        try
        {
            engine.Start(mic.Id, outputId, monitorId);
            engine.Soundboard.SampleRate = engine.SampleRate;
            PrepareClipsInBackground(boards.SelectMany(b => b.Clips).ToList());
        }
        catch (Exception ex)
        {
            SetStatus($"Impossible de démarrer : {ex.Message}", warn: true);
            UpdateRunState(keepStatus: true);
            return;
        }
        UpdateRunState();
    }

    private void PrepareClipsInBackground(List<SoundClip> clips)
    {
        int rate = engine.SampleRate;
        Task.Run(() =>
        {
            foreach (var clip in clips)
            {
                // Long music is decoded when first played, so memory stays small at start.
                if (clip.FilePath is not null && new FileInfo(clip.FilePath) is { Exists: true, Length: > 3_000_000 }) continue;
                try { clip.GetSamples(rate); } catch { /* reported when played */ }
            }
        });
    }

    private void UpdateRunState(bool keepStatus = false)
    {
        StartLabel.Text = engine.IsRunning ? "Arrêter" : "Démarrer";
        StartIcon.Text = engine.IsRunning ? "\uE71A" : "\uE768";
        UpdatePulse();
        UpdateTray();
        if (keepStatus) return;
        if (engine.IsRunning)
            SetStatus($"En direct · {engine.SampleRate / 1000.0:0.#} kHz", ok: true);
        else
            SetStatus("À l'arrêt");
    }

    private void SetStatus(string text, bool ok = false, bool warn = false)
    {
        StatusText.Text = text;
        StatusText.ToolTip = text;
        StatusDot.Fill = (Brush)FindResource(ok ? "OkBrush" : warn ? "WarnBrush" : "MutedBrush");
        StatusPill.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(260)));
    }

    /// <summary>A soft ring pulses around the status dot while the voice is live.</summary>
    private void UpdatePulse()
    {
        if (engine.IsRunning)
        {
            var period = TimeSpan.FromSeconds(1.6);
            var grow = new DoubleAnimation(1, 2.6, period) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            StatusPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0, period) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            StatusPulse.BeginAnimation(OpacityProperty, null);
            StatusPulse.Opacity = 0;
        }
    }

    private void UpdateMeters()
    {
        inputLevel = Smooth(inputLevel, ToMeter(engine.TakeInputPeak()));
        outputLevel = Smooth(outputLevel, ToMeter(engine.TakeOutputPeak()));
        InputMeter.Value = inputLevel;
        OutputMeter.Value = outputLevel;

        if (SoundPads.ItemsSource is IEnumerable<SoundClip> pads)
            foreach (var clip in pads) clip.PlayProgress = engine.IsRunning ? engine.Soundboard.ProgressOf(clip) : -1;
    }

    /// <summary>Peak 0..1 to a 60 dB meter scale.</summary>
    private static double ToMeter(float peak) =>
        peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);

    /// <summary>Instant rise, gentle fall.</summary>
    private static double Smooth(double current, double target) =>
        target > current ? target : current + (target - current) * 0.15;

    // ---------- Voices and effects ----------

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || PresetList.SelectedItem is not VoicePreset preset) return;
        preset.ApplyTo(engine.Effects);
        EffectsCheck.IsChecked = true;
        settings.Preset = preset.Name;
    }

    private void OnEffectsMasterChanged(object sender, RoutedEventArgs e)
    {
        engine.Effects.Enabled = EffectsCheck.IsChecked == true;
        UpdateTray();
    }

    private void OnClearEffects(object sender, RoutedEventArgs e)
    {
        foreach (var effect in engine.Effects.All) effect.Enabled = false;
        PresetList.SelectedItem = null;
    }

    private void OnGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        engine.InputGain = (float)GainSlider.Value;
    }

    private void OnMuteChanged(object sender, RoutedEventArgs e)
    {
        engine.MicMuted = MuteCheck.IsChecked == true;
        UpdateTray();
    }

    // ---------- Soundboards ----------

    private void LoadBoards()
    {
        boards.Clear();
        try
        {
            foreach (var board in SoundBoardLibrary.LoadAll()) boards.Add(board);
        }
        catch (Exception ex)
        {
            SetStatus($"Soundboards illisibles : {ex.Message}", warn: true);
        }
        if (!boards.Any(b => !b.IsBuiltIn)) boards.Add(SoundBoardLibrary.Create("Mes sons"));
        BoardList.SelectedItem = boards.FirstOrDefault(b => b.Name == settings.Board) ?? boards.FirstOrDefault();
    }

    private void OnBoardChanged(object sender, SelectionChangedEventArgs e)
    {
        var board = CurrentBoard;
        SoundPads.ItemsSource = board?.Clips;
        bool editable = board is { IsBuiltIn: false };
        RenameBoardButton.IsEnabled = editable;
        DeleteBoardButton.IsEnabled = editable;
        AddSoundButton.IsEnabled = editable;
        AddLinkButton.IsEnabled = editable && download is null;
        RenameBoardButton.Visibility = DeleteBoardButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        BoardHint.Text = editable
            ? "Glisse-dépose des fichiers ici, ajoute des sons ou colle un lien. Clic sur un son qui joue pour l'arrêter. Clic droit : raccourci ou retirer."
            : "Soundboard fournie avec VoxForge (sons créés par l'app). Crée ta propre soundboard pour ajouter tes sons et musiques.";
        if (!loading) WindowLook.Reveal(SoundPads);
        if (board is not null) settings.Board = board.Name;
    }

    private void OnNewBoard(object sender, RoutedEventArgs e)
    {
        string? name = TextPrompt.Ask(this, "Nouvelle soundboard", "Nom de la soundboard :", "Ma soundboard");
        if (name is null) return;
        try
        {
            var board = SoundBoardLibrary.Create(name);
            boards.Add(board);
            BoardList.SelectedItem = board;
        }
        catch (Exception ex)
        {
            SetStatus($"Impossible de créer la soundboard : {ex.Message}", warn: true);
        }
    }

    private void OnImportFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choisis un dossier de sons à importer" };
        if (dialog.ShowDialog(this) != true) return;

        var files = SoundBoardLibrary.SoundFiles(dialog.FolderName).ToList();
        if (files.Count == 0)
        {
            SetStatus("Aucun son trouvé dans ce dossier", warn: true);
            return;
        }
        try
        {
            var board = SoundBoardLibrary.Create(Path.GetFileName(dialog.FolderName.TrimEnd('\\')));
            boards.Add(board);
            BoardList.SelectedItem = board;
            AddFiles(board, files);
        }
        catch (Exception ex)
        {
            SetStatus($"Import impossible : {ex.Message}", warn: true);
        }
    }

    private void OnRenameBoard(object sender, RoutedEventArgs e)
    {
        if (CurrentBoard is not { IsBuiltIn: false } board) return;
        string? name = TextPrompt.Ask(this, "Renommer", "Nouveau nom :", board.Name);
        if (name is null) return;
        try
        {
            engine.Soundboard.StopAll();
            string oldName = board.Name;
            SoundBoardLibrary.Rename(board, name);
            settings.Board = board.Name;
            MoveBoardHotkeys(oldName, board.Name);
            ApplyHotkeys();
            if (engine.IsRunning) PrepareClipsInBackground(board.Clips.ToList());
        }
        catch (Exception ex)
        {
            SetStatus($"Impossible de renommer : {ex.Message}", warn: true);
        }
    }

    private void OnDeleteBoard(object sender, RoutedEventArgs e)
    {
        if (CurrentBoard is not { IsBuiltIn: false } board) return;
        var answer = MessageBox.Show(this,
            $"Supprimer la soundboard « {board.Name} » et ses {board.Clips.Count} son(s) ?\nLes fichiers d'origine ne sont pas touchés, seulement les copies de VoxForge.",
            "Supprimer la soundboard", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            engine.Soundboard.StopAll();
            SoundBoardLibrary.Delete(board);
            boards.Remove(board);
            MoveBoardHotkeys(board.Name, null);
            ApplyHotkeys();
            BoardList.SelectedItem = boards.FirstOrDefault();
        }
        catch (Exception ex)
        {
            SetStatus($"Impossible de supprimer : {ex.Message}", warn: true);
        }
    }

    private void OnAddSound(object sender, RoutedEventArgs e)
    {
        if (CurrentBoard is not { IsBuiltIn: false } board) return;
        var dialog = new OpenFileDialog { Title = "Ajouter des sons", Filter = SoundFilter, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        AddFiles(board, dialog.FileNames);
    }

    private void OnDragOverSounds(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) && CurrentBoard is { IsBuiltIn: false };
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropSounds(object sender, DragEventArgs e)
    {
        if (CurrentBoard is not { IsBuiltIn: false } board) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var files = paths.SelectMany(p => Directory.Exists(p) ? SoundBoardLibrary.SoundFiles(p) : new[] { p })
                         .Where(SoundBoardLibrary.IsSoundFile)
                         .ToList();
        if (files.Count == 0)
        {
            SetStatus("Aucun fichier son reconnu", warn: true);
            return;
        }
        AddFiles(board, files);
    }

    private void AddFiles(SoundBoardPage board, IEnumerable<string> files)
    {
        int added = 0;
        var failed = new List<string>();
        foreach (var file in files)
        {
            try
            {
                SoundBoardLibrary.AddFile(board, file, engine.SampleRate);
                added++;
            }
            catch
            {
                failed.Add(Path.GetFileName(file));
            }
        }
        if (failed.Count > 0)
            SetStatus($"{added} son(s) ajouté(s), illisible(s) : {string.Join(", ", failed)}", warn: true);
        else
            SetStatus($"{added} son(s) ajouté(s) à « {board.Name} »", ok: engine.IsRunning);
    }

    private void OnPlaySound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SoundClip clip) PlayClip(clip);
    }

    private async void PlayClip(SoundClip clip)
    {
        if (!engine.IsRunning)
        {
            SetStatus("Démarre VoxForge pour jouer un son", warn: true);
            return;
        }
        if (clip.IsPlaying)
        {
            engine.Soundboard.Stop(clip); // a second click stops it, handy for music
            return;
        }
        try
        {
            int rate = engine.SampleRate;
            if (!clip.IsReady(rate))
            {
                SetStatus($"Chargement de « {clip.Name} »…");
                await Task.Run(() => clip.GetSamples(rate));
                SetStatus("En direct · " + clip.Name, ok: true);
            }
            if (engine.IsRunning) engine.Soundboard.Play(clip);
        }
        catch (Exception ex)
        {
            SetStatus($"Son illisible : {ex.Message}", warn: true);
        }
    }

    // ---------- Sounds from links ----------

    private async void OnAddLink(object sender, RoutedEventArgs e)
    {
        if (CurrentBoard is not { IsBuiltIn: false } board || download is not null) return;
        string clipboard = "";
        try { clipboard = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; } catch { /* clipboard busy */ }
        string? url = TextPrompt.Ask(this, "Ajouter depuis un lien", "Colle le lien du son ou de la musique :",
            LinkImporter.IsLink(clipboard) ? clipboard : "",
            hint: "Un lien direct vers un fichier son (.mp3, .wav, .m4a…) marche tout de suite. Pour YouTube, SoundCloud ou TikTok, " +
                  "VoxForge utilise l'outil gratuit yt-dlp s'il est installé. Le son est ensuite enregistré dans cette soundboard.",
            okText: "Télécharger");
        if (url is null) return;
        if (!LinkImporter.IsLink(url))
        {
            SetStatus("Ce n'est pas un lien web (http:// ou https://)", warn: true);
            return;
        }

        string temp = Path.Combine(Path.GetTempPath(), "VoxForge", "Downloads", Guid.NewGuid().ToString("N"));
        download = new CancellationTokenSource();
        AddLinkButton.IsEnabled = false;
        DownloadPanel.Visibility = Visibility.Visible;
        WindowLook.Reveal(DownloadPanel);
        DownloadText.Text = "Téléchargement de " + url;
        DownloadBar.Value = 0;
        var progress = new Progress<double>(p => DownloadBar.Value = p);
        try
        {
            string file = await LinkImporter.DownloadAsync(url, temp, progress, download.Token);
            DownloadText.Text = "Préparation de « " + Path.GetFileNameWithoutExtension(file) + " »…";
            DownloadBar.Value = 1;
            int rate = engine.SampleRate;
            var clip = await Task.Run(() => SoundBoardLibrary.ImportFile(board, file, rate));
            board.Clips.Add(clip);
            ApplyHotkeys();
            SetStatus($"« {clip.Name} » ajouté à « {board.Name} »", ok: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Téléchargement annulé");
        }
        catch (NeedsPageToolException)
        {
            SetStatus("Lien de page web : installe yt-dlp pour ce site", warn: true);
            MessageBox.Show(this,
                "Ce lien mène à une page (vidéo, musique en ligne), pas directement à un fichier son.\n\n" +
                "Pour ces sites, VoxForge s'appuie sur yt-dlp, un outil gratuit. Installe-le une fois, par exemple avec la commande :\n" +
                "    winget install yt-dlp.yt-dlp\n" +
                "puis relance VoxForge et colle à nouveau le lien.\n\nUn lien direct vers un .mp3 ou un .wav marche sans rien installer.",
                "Ajouter depuis un lien", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SetStatus("Lien impossible : " + ex.Message, warn: true);
        }
        finally
        {
            download.Dispose();
            download = null;
            DownloadPanel.Visibility = Visibility.Collapsed;
            AddLinkButton.IsEnabled = CurrentBoard is { IsBuiltIn: false };
            try { Directory.Delete(temp, recursive: true); } catch { /* temp folder */ }
        }
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e) => download?.Cancel();

    // ---------- Virtual cable ----------

    private async void OnInstallCable(object sender, RoutedEventArgs e)
    {
        InstallCableButton.IsEnabled = false;
        InstallCableLabel.Text = "Téléchargement…";
        try
        {
            var progress = new Progress<double>(p => InstallCableLabel.Text = $"Téléchargement… {p:P0}");
            var result = await CableInstaller.DownloadAndRunAsync(progress, CancellationToken.None);
            if (result == CableInstaller.Result.Started)
            {
                SetStatus("Installeur VB-Cable lancé : clique « Install Driver », puis redémarre le PC", ok: true);
                MessageBox.Show(this,
                    "L'installeur officiel de VB-Cable est ouvert.\n\n1. Clique sur « Install Driver ».\n2. Redémarre le PC quand c'est fini.\n" +
                    "3. Rouvre VoxForge : la sortie « CABLE Input » sera choisie toute seule.\n4. Dans Discord / OBS / ton jeu, prends le micro « CABLE Output ».",
                    "Câble virtuel", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                SetStatus("Installation du câble annulée", warn: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Installation impossible : " + ex.Message, warn: true);
            if (MessageBox.Show(this, ex.Message + "\n\nOuvrir la page officielle de VB-Cable pour l'installer à la main ?", "Câble virtuel",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                CableInstaller.OpenHomePage();
        }
        finally
        {
            InstallCableButton.IsEnabled = true;
            InstallCableLabel.Text = "Installer le câble virtuel";
        }
    }

    private void OnStopSounds(object sender, RoutedEventArgs e) => engine.Soundboard.StopAll();

    private void OnSoundVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        engine.Soundboard.Volume = (float)SoundVolume.Value;
    }

    private void OnRemoveSound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SoundClip clip) return;
        var board = boards.FirstOrDefault(b => b.Clips.Contains(clip));
        if (board is null || board.IsBuiltIn)
        {
            SetStatus("Les sons intégrés ne peuvent pas être retirés");
            return;
        }
        HotkeyMap.Remove(PadKey(clip));
        SoundBoardLibrary.RemoveClip(board, clip);
        ApplyHotkeys();
    }

    // ---------- Settings ----------

    private void ApplySettingsToUi()
    {
        MonitorCheck.IsChecked = settings.MonitorEnabled;
        TrayCheck.IsChecked = settings.CloseToTray;
        GainSlider.Value = settings.InputGain;
        SoundVolume.Value = settings.SoundboardVolume;
        engine.InputGain = (float)settings.InputGain;
        engine.Soundboard.Volume = (float)settings.SoundboardVolume;

        var preset = VoicePreset.All.FirstOrDefault(p => p.Name == settings.Preset);
        if (!settings.RestoreEffects(engine.Effects)) preset?.ApplyTo(engine.Effects);
        PresetList.SelectedItem = preset;
        EffectsCheck.IsChecked = engine.Effects.Enabled;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exiting && settings.CloseToTray && tray is not null)
        {
            e.Cancel = true;
            Hide();
            settings.CaptureEffects(engine.Effects);
            settings.Save();
            if (!settings.TrayHintShown)
            {
                tray.Notify("VoxForge reste actif", "Ta voix continue. Retrouve VoxForge dans la flèche en bas à droite ; clic droit sur l'icône pour quitter.");
                settings.TrayHintShown = true;
            }
            return;
        }

        meterTimer.Stop();
        tray?.Dispose();
        RememberDevices();
        settings.CaptureEffects(engine.Effects);
        settings.InputGain = GainSlider.Value;
        settings.SoundboardVolume = SoundVolume.Value;
        settings.Save();
        hotkeys?.Dispose();
        engine.Dispose();
    }
}
