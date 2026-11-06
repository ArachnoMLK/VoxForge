using System.Collections.ObjectModel;
using VoxForge.Audio;

namespace VoxForge;

/// <summary>A named page of soundboard pads. User boards are folders under %AppData%\VoxForge\Boards.</summary>
public sealed class SoundBoardPage : Observable
{
    private string name;

    public SoundBoardPage(string name, string? folder)
    {
        this.name = name;
        Folder = folder;
    }

    public string Name
    {
        get => name;
        set
        {
            name = value;
            Notify();
        }
    }

    /// <summary>Null for the built-in board, whose sounds are synthesised in code.</summary>
    public string? Folder { get; set; }
    public bool IsBuiltIn => Folder is null;
    public ObservableCollection<SoundClip> Clips { get; } = new();
}

/// <summary>Creates, renames, deletes and fills soundboards on disk.</summary>
public static class SoundBoardLibrary
{
    public static readonly string[] Extensions = { ".wav", ".mp3", ".m4a", ".wma", ".flac", ".aiff", ".aif", ".aac" };

    public static string Root { get; } = Path.Combine(AppSettings.DataFolder, "Boards");

    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    public static List<SoundBoardPage> LoadAll()
    {
        MigrateLegacySounds();
        var boards = new List<SoundBoardPage>();

        foreach (var pack in SoundPacks.All())
        {
            var builtIn = new SoundBoardPage(pack.Name, null);
            foreach (var clip in pack.Clips) builtIn.Clips.Add(clip);
            boards.Add(builtIn);
        }

        Directory.CreateDirectory(Root);
        foreach (var folder in Directory.EnumerateDirectories(Root).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
        {
            var board = new SoundBoardPage(Path.GetFileName(folder), folder);
            foreach (var file in SoundFiles(folder)) board.Clips.Add(SoundClip.FromFile(file));
            boards.Add(board);
        }
        return boards;
    }

    public static SoundBoardPage Create(string name)
    {
        string folder = UniqueFolder(Clean(name));
        Directory.CreateDirectory(folder);
        return new SoundBoardPage(Path.GetFileName(folder), folder);
    }

    public static void Rename(SoundBoardPage board, string newName)
    {
        if (board.IsBuiltIn) return;
        string cleaned = Clean(newName);
        if (string.Equals(cleaned, board.Name, StringComparison.CurrentCulture)) return;
        string target = UniqueFolder(cleaned);
        Directory.Move(board.Folder!, target);

        // Clips keep absolute paths, so rebuild them from the moved folder.
        board.Folder = target;
        board.Name = Path.GetFileName(target);
        board.Clips.Clear();
        foreach (var file in SoundFiles(target)) board.Clips.Add(SoundClip.FromFile(file));
    }

    public static void Delete(SoundBoardPage board)
    {
        if (board.IsBuiltIn || board.Folder is null) return;
        if (Directory.Exists(board.Folder)) Directory.Delete(board.Folder, recursive: true);
    }

    /// <summary>Copies a sound file into the board and returns its pad. Throws if the file cannot be decoded.</summary>
    public static SoundClip AddFile(SoundBoardPage board, string source, int sampleRate)
    {
        var clip = ImportFile(board, source, sampleRate);
        board.Clips.Add(clip);
        return clip;
    }

    /// <summary>
    /// Copies and decodes a sound into the board folder without touching <see cref="SoundBoardPage.Clips"/>,
    /// so it can run off the UI thread; the caller adds the returned pad.
    /// </summary>
    public static SoundClip ImportFile(SoundBoardPage board, string source, int sampleRate)
    {
        if (board.IsBuiltIn) throw new InvalidOperationException("Built-in board is read-only.");
        string target = UniqueFile(Path.Combine(board.Folder!, Path.GetFileName(source)));
        File.Copy(source, target);
        try
        {
            var clip = SoundClip.FromFile(target);
            clip.GetSamples(sampleRate); // validates the file before it gets a pad
            return clip;
        }
        catch
        {
            try { File.Delete(target); } catch { /* nothing to clean */ }
            throw;
        }
    }

    public static void RemoveClip(SoundBoardPage board, SoundClip clip)
    {
        if (board.IsBuiltIn || clip.FilePath is null) return;
        board.Clips.Remove(clip);
        try { File.Delete(clip.FilePath); } catch { /* pad is gone anyway */ }
    }

    public static bool IsSoundFile(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static IEnumerable<string> SoundFiles(string folder) =>
        Directory.EnumerateFiles(folder).Where(IsSoundFile).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>v0.1 kept user sounds in %AppData%\VoxForge\Sounds; move them into a "Mes sons" board.</summary>
    private static void MigrateLegacySounds()
    {
        string legacy = Path.Combine(AppSettings.DataFolder, "Sounds");
        if (!Directory.Exists(legacy)) return;
        try
        {
            Directory.CreateDirectory(Root);
            string target = Path.Combine(Root, "Mes sons");
            if (!Directory.Exists(target)) Directory.Move(legacy, target);
        }
        catch
        {
            // Leave the old folder in place; nothing is lost.
        }
    }

    private static string Clean(string name)
    {
        string cleaned = new string(name.Trim().Where(c => !Invalid.Contains(c)).ToArray()).Trim('.', ' ');
        return string.IsNullOrEmpty(cleaned) ? "Soundboard" : cleaned;
    }

    private static string UniqueFolder(string name)
    {
        string path = Path.Combine(Root, name);
        for (int i = 2; Directory.Exists(path); i++) path = Path.Combine(Root, $"{name} ({i})");
        return path;
    }

    private static string UniqueFile(string path)
    {
        if (!File.Exists(path)) return path;
        string folder = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
