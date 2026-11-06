using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace VoxForge;

/// <summary>VoxForge's icon in the notification area (the arrow at the bottom right of the taskbar).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly System.Drawing.Icon image;
    private readonly IntPtr imageHandle;
    private readonly Forms.ToolStripMenuItem engineItem, effectsItem, muteItem;

    public TrayIcon(ImageSource logo, Action open, Action toggleEngine, Action toggleEffects, Action toggleMute, Action quit)
    {
        (image, imageHandle) = ToIcon(logo, 32);

        engineItem = new Forms.ToolStripMenuItem("Démarrer", null, (_, _) => toggleEngine());
        effectsItem = new Forms.ToolStripMenuItem("Effets activés", null, (_, _) => toggleEffects());
        muteItem = new Forms.ToolStripMenuItem("Micro coupé", null, (_, _) => toggleMute());

        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Ouvrir VoxForge", null, (_, _) => open());
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(engineItem);
        menu.Items.Add(effectsItem);
        menu.Items.Add(muteItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quitter", null, (_, _) => quit()));

        icon = new Forms.NotifyIcon
        {
            Icon = image,
            Text = "VoxForge",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
        };
    }

    public void Update(bool running, bool effectsOn, bool muted)
    {
        engineItem.Text = running ? "Arrêter la voix" : "Démarrer la voix";
        effectsItem.Checked = effectsOn;
        muteItem.Checked = muted;
        string state = !running ? "à l'arrêt" : muted ? "micro coupé" : effectsOn ? "en direct" : "en direct, sans effet";
        icon.Text = $"VoxForge · {state}";
    }

    public void Notify(string title, string message) =>
        icon.ShowBalloonTip(3000, title, message, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        image.Dispose();
        DestroyIcon(imageHandle);
    }

    /// <summary>Renders the vector logo to a GDI icon.</summary>
    private static (System.Drawing.Icon, IntPtr) ToIcon(ImageSource source, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawImage(source, new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        using var gdi = new System.Drawing.Bitmap(stream);
        IntPtr handle = gdi.GetHicon();
        return (System.Drawing.Icon.FromHandle(handle), handle);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
