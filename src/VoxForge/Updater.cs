using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoxForge;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string ZipUrl, string? HashUrl, string Notes);

/// <summary>
/// Automatic updates from the GitHub releases of the VoxForge repository.
/// A release ships "VoxForge-win-x64.zip" (the published app folder) and, optionally,
/// "VoxForge-win-x64.zip.sha256". The update is downloaded and checked in the background,
/// then copied over the app folder by a small hidden PowerShell step once VoxForge has exited.
/// </summary>
public static class Updater
{
    public const string Repository = "ArachnoMLK/VoxForge";
    public const string AssetName = "VoxForge-win-x64.zip";

    // Declared before Http: static fields initialise in order and the client uses it.
    public static Version Current { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);

    private static readonly HttpClient Http = CreateClient();

    private static string UpdatesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxForge", "Updates");

    /// <summary>Returns the latest release when it is newer than this build, else null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancel);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // No public release yet.
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"GitHub a répondu « {(int)response.StatusCode} {response.ReasonPhrase} »");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        var root = json.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        if (version <= Current) return null;

        string? zip = null, hash = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!IsGitHubUrl(url)) continue;
            if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) zip = url;
            else if (name.Equals(AssetName + ".sha256", StringComparison.OrdinalIgnoreCase)) hash = url;
        }
        if (zip is null) return null;
        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(version, zip, hash, notes);
    }

    /// <summary>Downloads, checks and unpacks the update. Returns the folder holding the new app files.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancel)
    {
        string folder = Path.Combine(UpdatesFolder, update.Version.ToString());
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        try
        {
            return await DownloadIntoAsync(update, folder, progress, cancel);
        }
        catch
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
            throw;
        }
    }

    private static async Task<string> DownloadIntoAsync(UpdateInfo update, string folder, IProgress<double>? progress, CancellationToken cancel)
    {
        string staging = Path.Combine(folder, "app");
        string zipPath = Path.Combine(folder, AssetName);

        using (var response = await Http.GetAsync(update.ZipUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long? length = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var target = File.Create(zipPath);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                total += read;
                if (length > 0) progress?.Report((double)total / length.Value);
            }
        }

        if (update.HashUrl is not null)
        {
            string expected = (await Http.GetStringAsync(update.HashUrl, cancel)).Trim().Split(' ', '\t', '\r', '\n')[0];
            string actual;
            await using (var stream = File.OpenRead(zipPath))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("le fichier de mise à jour est abîmé (empreinte différente)");
        }

        ZipFile.ExtractToDirectory(zipPath, staging);
        File.Delete(zipPath);

        // The archive may hold the files directly or inside one folder.
        if (!File.Exists(Path.Combine(staging, "VoxForge.exe")) && Directory.GetDirectories(staging) is [var inner] &&
            File.Exists(Path.Combine(inner, "VoxForge.exe")))
            staging = inner;
        string dll = Path.Combine(staging, "VoxForge.dll");
        if (!File.Exists(Path.Combine(staging, "VoxForge.exe")) || !File.Exists(dll))
            throw new InvalidDataException("la mise à jour ne contient pas VoxForge");
        var built = AssemblyName.GetAssemblyName(dll).Version;
        if (built is null || new Version(built.Major, built.Minor, Math.Max(built.Build, 0)) != update.Version)
            throw new InvalidDataException("la version téléchargée ne correspond pas à la release");
        return staging;
    }

    /// <summary>
    /// Starts a hidden step that waits for VoxForge to exit, copies the new files over the app folder
    /// and starts VoxForge again. The caller must then quit the app.
    /// </summary>
    public static void ScheduleInstall(string staging, bool restart)
    {
        string appFolder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exe = Path.Combine(appFolder, "VoxForge.exe");
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue");
        script.AppendLine("for ($i = 0; $i -lt 20; $i++) {");
        script.AppendLine($"  try {{ Copy-Item -Path (Join-Path {Quote(staging)} '*') -Destination {Quote(appFolder)} -Recurse -Force; break }}");
        script.AppendLine("  catch { Start-Sleep -Milliseconds 500 }");
        script.AppendLine("}");
        if (restart) script.AppendLine($"Start-Process -FilePath {Quote(exe)}");
        script.AppendLine($"Remove-Item -Path {Quote(Path.GetDirectoryName(staging)!)} -Recurse -Force -ErrorAction SilentlyContinue");

        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString())) })
            info.ArgumentList.Add(arg);
        Process.Start(info);
    }

    private static bool IsGitHubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoxForge/" + Current);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
