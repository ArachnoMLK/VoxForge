using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace VoxForge;

/// <summary>The link points at a web page (YouTube, SoundCloud…) and yt-dlp is not installed.</summary>
public sealed class NeedsPageToolException : Exception
{
    public NeedsPageToolException() : base("This link is a web page; yt-dlp is needed to extract its audio.") { }
}

/// <summary>yt-dlp ran but could not get the sound (often fixed by updating yt-dlp).</summary>
public sealed class PageToolFailedException(string message) : Exception(message);

/// <summary>
/// Downloads a sound from a link into a temporary file, ready to be added to a soundboard.
/// Direct links to audio files are fetched over HTTP. Links to pages (video sites) are handed
/// to yt-dlp when the user has installed it; VoxForge never downloads or installs that tool itself.
/// </summary>
public static class LinkImporter
{
    private const long MaxBytes = 150L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static readonly Dictionary<string, string> ExtensionByType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = ".mp3",
        ["audio/mp3"] = ".mp3",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/wave"] = ".wav",
        ["audio/vnd.wave"] = ".wav",
        ["audio/mp4"] = ".m4a",
        ["audio/x-m4a"] = ".m4a",
        ["audio/m4a"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["audio/aacp"] = ".aac",
        ["audio/flac"] = ".flac",
        ["audio/x-flac"] = ".flac",
        ["audio/x-ms-wma"] = ".wma",
        ["audio/aiff"] = ".aiff",
        ["audio/x-aiff"] = ".aiff",
    };

    public static bool IsLink(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Path of yt-dlp.exe if the user installed it (PATH, WinGet links or %AppData%\VoxForge\Tools), else null.</summary>
    public static string? FindPageTool()
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
        folders.Add(Path.Combine(AppSettings.DataFolder, "Tools"));
        foreach (var folder in folders)
        {
            try
            {
                string candidate = Path.Combine(folder.Trim('"'), "yt-dlp.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>
    /// Installs (or updates) yt-dlp with winget, in a visible console so the user sees and answers winget's prompts.
    /// Only called when the user clicks the install button. Returns the tool path, or null if it is still missing.
    /// </summary>
    public static async Task<string?> InstallPageToolAsync(bool update)
    {
        string winget = FindWinget() ?? throw new InvalidOperationException("winget (App Installer) est introuvable sur ce PC");
        var info = new ProcessStartInfo(winget) { UseShellExecute = false };
        foreach (var arg in new[] { update ? "upgrade" : "install", "--id", "yt-dlp.yt-dlp", "--exact", "--source", "winget" })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("winget n'a pas démarré");
        await process.WaitForExitAsync();
        return FindPageTool();
    }

    /// <summary>Opens the yt-dlp page, for a manual install.</summary>
    public static void OpenPageToolSite() =>
        Process.Start(new ProcessStartInfo("https://github.com/yt-dlp/yt-dlp#installation") { UseShellExecute = true });

    private static string? FindWinget()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(local)) return local;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(folder.Trim('"'), "winget.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>Downloads the sound behind <paramref name="url"/> into <paramref name="folder"/> and returns the file path.</summary>
    public static async Task<string> DownloadAsync(string url, string folder, IProgress<double>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        var uri = new Uri(url.Trim());

        // Known video/music sites never serve the file directly.
        if (!IsKnownPageSite(uri))
        {
            HttpResponseMessage response;
            try
            {
                response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidDataException("site injoignable (" + ex.Message + ")", ex);
            }
            using var _ = response;
            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException($"le site a répondu « {(int)response.StatusCode} {response.ReasonPhrase} »");
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            string? extension = ExtensionFor(mediaType, response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName, response.RequestMessage?.RequestUri ?? uri);
            if (extension is not null)
                return await SaveBodyAsync(response, uri, extension, folder, progress, cancel);
            if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Ce lien ne mène pas à un son ({mediaType}).");
        }

        string tool = FindPageTool() ?? throw new NeedsPageToolException();
        return await RunPageToolAsync(tool, uri.ToString(), folder, progress, cancel);
    }

    private static bool IsKnownPageSite(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();
        string[] sites = { "youtube.com", "youtu.be", "soundcloud.com", "tiktok.com", "twitch.tv", "x.com", "twitter.com", "instagram.com", "dailymotion.com", "vimeo.com", "bandcamp.com" };
        return sites.Any(s => host == s || host.EndsWith("." + s));
    }

    private static string? ExtensionFor(string? mediaType, string? fileName, Uri uri)
    {
        if (mediaType is not null && ExtensionByType.TryGetValue(mediaType, out var byType)) return byType;
        foreach (var name in new[] { fileName?.Trim('"', ' '), uri.AbsolutePath })
        {
            string extension = Path.GetExtension(name ?? "").ToLowerInvariant();
            if (extension.Length > 0 && SoundBoardLibrary.IsSoundFile("x" + extension)) return extension;
        }
        return null;
    }

    private static async Task<string> SaveBodyAsync(HttpResponseMessage response, Uri uri, string extension, string folder,
        IProgress<double>? progress, CancellationToken cancel)
    {
        long? length = response.Content.Headers.ContentLength;
        if (length > MaxBytes) throw new InvalidDataException("Fichier trop gros (plus de 150 Mo).");

        string name = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName
            ?? Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        name = Path.GetFileNameWithoutExtension(name.Trim('"', ' '));
        if (string.IsNullOrWhiteSpace(name)) name = "Son";
        string path = Path.Combine(folder, Sanitize(name) + extension);

        await using var source = await response.Content.ReadAsStreamAsync(cancel);
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                total += read;
                if (total > MaxBytes) throw new InvalidDataException("Fichier trop gros (plus de 150 Mo).");
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                if (length > 0) progress?.Report((double)total / length.Value);
            }
        }
        return path;
    }

    private static async Task<string> RunPageToolAsync(string tool, string url, string folder, IProgress<double>? progress, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Audio-only formats that Windows can decode without ffmpeg (AAC in m4a, or mp3).
        foreach (var arg in new[]
                 {
                     "-f", "bestaudio[ext=m4a]/bestaudio[ext=mp3]/bestaudio[acodec^=mp4a]",
                     "--no-playlist", "--max-filesize", "150M", "--restrict-filenames",
                     "--progress", "--newline",
                     "-o", Path.Combine(folder, "%(title).80B.%(ext)s"),
                     "--print", "after_move:filepath",
                     "--", url,
                 })
            info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info };
        var lines = new List<string>();
        var percent = new Regex(@"(\d+(?:\.\d+)?)%");
        void OnLine(string? line)
        {
            if (line is null) return;
            lock (lines) lines.Add(line);
            if (line.StartsWith("[download]") && percent.Match(line) is { Success: true } m &&
                double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p))
                progress?.Report(p / 100);
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        List<string> output;
        lock (lines) output = lines.ToList();
        string? file = output.Select(l => l.Trim()).LastOrDefault(File.Exists);
        if (process.ExitCode == 0 && file is not null) return file;

        string? error = output.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));
        throw new PageToolFailedException(error is null ? "yt-dlp n'a pas pu récupérer le son." : error.Replace("ERROR:", "").Trim());
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim('.', ' ');
        if (cleaned.Length > 80) cleaned = cleaned[..80];
        return string.IsNullOrEmpty(cleaned) ? "Son" : cleaned;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoxForge/0.4");
        return client;
    }
}
