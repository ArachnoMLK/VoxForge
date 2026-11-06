using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace VoxForge;

/// <summary>
/// Fetches the official VB-Cable driver pack from vb-audio.com and starts its setup when the user asks.
/// The setup is only started if its Authenticode signature is valid and was issued to VB-Audio's author;
/// Windows then asks the user for permission (UAC) and the setup itself installs the driver.
/// </summary>
public static class CableInstaller
{
    public const string HomePage = "https://vb-audio.com/Cable/";

    private static readonly string[] PackUrls =
    {
        "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip",
        "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack44.zip",
        "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip",
    };

    private const string ExpectedSigner = "BUREL VINCENT";

    public enum Result { Started, Declined }

    public static async Task<Result> DownloadAndRunAsync(IProgress<double>? progress, CancellationToken cancel)
    {
        string folder = Path.Combine(Path.GetTempPath(), "VoxForge", "VBCable");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        string zip = Path.Combine(folder, "pack.zip");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("VoxForge/0.4");
            Exception? last = null;
            bool done = false;
            foreach (var url in PackUrls)
            {
                try
                {
                    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
                    response.EnsureSuccessStatusCode();
                    long? length = response.Content.Headers.ContentLength;
                    await using var source = await response.Content.ReadAsStreamAsync(cancel);
                    await using var target = File.Create(zip);
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                        total += read;
                        if (length > 0) progress?.Report((double)total / length.Value);
                    }
                    done = true;
                    break;
                }
                catch (HttpRequestException ex)
                {
                    last = ex;
                }
            }
            if (!done) throw new InvalidOperationException("Téléchargement impossible depuis vb-audio.com.", last);
        }

        string extracted = Path.Combine(folder, "pack");
        ZipFile.ExtractToDirectory(zip, extracted);
        string setupName = Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe";
        string setup = Path.Combine(extracted, setupName);
        if (!File.Exists(setup)) throw new FileNotFoundException("Installeur introuvable dans le paquet VB-Cable.", setupName);
        if (!IsTrusted(setup)) throw new InvalidOperationException("La signature de l'installeur VB-Cable n'est pas valide : il n'a pas été lancé.");

        try
        {
            // The setup must run elevated; Windows shows its own permission prompt.
            Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WorkingDirectory = extracted });
            return Result.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the user said no
        {
            return Result.Declined;
        }
    }

    public static void OpenHomePage() => Process.Start(new ProcessStartInfo(HomePage) { UseShellExecute = true });

    private static bool IsTrusted(string file)
    {
        if (!VerifyAuthenticode(file)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // reading the signer of a file Windows has just verified
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains(ExpectedSigner, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ---------- WinVerifyTrust ----------

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

    private static bool VerifyAuthenticode(string file)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = file,
        };
        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 0,
                dwProvFlags = 0x00000080, // WTD_REVOCATION_CHECK_NONE
            };
            return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }
}
