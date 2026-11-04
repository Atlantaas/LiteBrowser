using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using LiteCore;

static class Program
{
    public static bool RestartRequested;

    [STAThread]
    static void Main()
    {
        // 1er lancement depuis le dossier Téléchargements : on s'installe et on se relance depuis l'emplacement fixe
        // Mode test automatique (CI Windows) : pas d'installation, rapport puis fermeture
        // Mode développement (LITEBROWSER_DATA) : pas d'installation, données à part
        if (BrowserForm.SmokeReport == null && !AppPaths.Dev && Installer.EnsureInstalled()) return;
        Updater.CleanupOld();

        // Journal des plantages : %LOCALAPPDATA%\LiteBrowser\crash.log (et à côté du rapport en mode test)
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => CrashLog.Write("ThreadException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { CrashLog.Write("UnobservedTask", e.Exception); e.SetObserved(); };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new BrowserForm());

        if (RestartRequested)
        {
            System.Threading.Thread.Sleep(800); // laisse le moteur web se fermer proprement
            Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true });
        }
    }
}

/// Dossier des données. LITEBROWSER_DATA permet de lancer une version de test sans toucher au navigateur installé.
static class AppPaths
{
    static readonly string devData = Environment.GetEnvironmentVariable("LITEBROWSER_DATA");
    public static bool Dev => !string.IsNullOrWhiteSpace(devData);
    public static string Data => Dev ? Path.GetFullPath(devData)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteBrowser");
}

static class CrashLog
{
    public static void Write(string kind, Exception ex)
    {
        try
        {
            var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{kind}] {ex}\n\n";
            var dir = AppPaths.Data;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), text);
            if (BrowserForm.SmokeReport != null)
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(BrowserForm.SmokeReport) ?? ".", "crash.log"), text);
        }
        catch { }
    }
}

// ======================================================================
//  Installation (emplacement fixe + raccourcis) et mises à jour automatiques
// ======================================================================

static class Installer
{
    public static string AppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteBrowser", "app");
    public static string Target => Path.Combine(AppDir, "LiteBrowser.exe");

    /// true = l'appli a été relancée depuis son emplacement d'installation, ce processus doit s'arrêter.
    public static bool EnsureInstalled()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return false;
        if (string.Equals(Path.GetFullPath(exe), Path.GetFullPath(Target), StringComparison.OrdinalIgnoreCase))
        {
            CreateShortcuts();
            return false;
        }
        try
        {
            Directory.CreateDirectory(AppDir);
            Version installed = null;
            if (File.Exists(Target) && Version.TryParse(FileVersionInfo.GetVersionInfo(Target).FileVersion, out var v))
                installed = Updater.Norm(v);

            if (installed == null || installed < Updater.Current)
            {
                if (File.Exists(Target)) File.Move(Target, Target + ".old", true); // marche même si l'ancienne tourne
                File.Copy(exe, Target, true);
            }
            CreateShortcuts();
            Process.Start(new ProcessStartInfo(Target) { UseShellExecute = true, WorkingDirectory = AppDir });
            return true;
        }
        catch { return false; } // en cas de souci on tourne simplement depuis l'emplacement actuel
    }

    static void CreateShortcuts()
    {
        foreach (var dir in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
        {
            try
            {
                var lnk = Path.Combine(Environment.GetFolderPath(dir), "LiteBrowser.lnk");
                if (!File.Exists(lnk)) CreateShortcut(lnk, Target);
            }
            catch { }
        }
    }

    static void CreateShortcut(string lnkPath, string target)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        object shell = Activator.CreateInstance(t);
        try
        {
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            var lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "LiteBrowser" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            Marshal.FinalReleaseComObject(lnk);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}

static class Updater
{
    public const string Repo = "Atlantaas/LiteBrowser";
    public static Version Norm(Version v) => VersionTools.Norm(v);
    public static Version Current => Norm(typeof(Program).Assembly.GetName().Version);

    /// Renvoie la nouvelle version et le lien de l'exe, ou null si on est à jour.
    public static async Task<(Version Version, string Url)?> CheckAsync()
    {
        using var http = Net.Client(TimeSpan.FromSeconds(20));
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        return VersionTools.NewerRelease(json, Current);
    }

    /// Télécharge la nouvelle version et la met à la place de l'actuelle (prise en compte au prochain lancement).
    public static async Task InstallAsync(string url)
    {
        var exe = Environment.ProcessPath;
        string tmp = exe + ".new", old = exe + ".old";
        using (var http = Net.Client(TimeSpan.FromMinutes(10)))
        using (var s = await http.GetStreamAsync(url))
        using (var f = File.Create(tmp))
            await s.CopyToAsync(f);
        if (new FileInfo(tmp).Length < 1_000_000) { File.Delete(tmp); throw new IOException("Téléchargement incomplet."); }
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);   // Windows autorise à renommer un exe en cours d'exécution
        File.Move(tmp, exe);
    }

    public static void CleanupOld()
    {
        var old = Environment.ProcessPath + ".old";
        Task.Run(async () =>
        {
            for (int i = 0; i < 5 && File.Exists(old); i++)
            {
                try { File.Delete(old); } catch { await Task.Delay(2000); }
            }
        });
    }
}

// ======================================================================
//  Thème, icônes, petits utilitaires de dessin
// ======================================================================

sealed class Theme
{
    public Color Strip, Bar, Hover, Text, Sub, Field, FieldBorder, Accent;

    public static readonly Theme Light = new()
    {
        Strip = C(0xDEE1E6), Bar = C(0xFFFFFF), Hover = C(0xE8EAED), Text = C(0x1F1F1F),
        Sub = C(0x5F6368), Field = C(0xF1F3F4), FieldBorder = C(0xDADCE0), Accent = C(0x1A73E8)
    };
    public static readonly Theme Dark = new()
    {
        Strip = C(0x1F2023), Bar = C(0x2D2E31), Hover = C(0x3E3F43), Text = C(0xE8EAED),
        Sub = C(0x9AA0A6), Field = C(0x1F2023), FieldBorder = C(0x45474B), Accent = C(0x8AB4F8)
    };
    /// Thème « Gentle Mates » : noir profond, blanc cassé, accent rose M8 (#D669D2).
    public static readonly Theme M8 = new()
    {
        Strip = C(0x08080A), Bar = C(0x141418), Hover = C(0x26262D), Text = C(0xF4F2EE),
        Sub = C(0x8F8D96), Field = C(0x0E0E12), FieldBorder = C(0x26262D), Accent = C(0xD669D2)
    };
    static Color C(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
}

static class Glyphs
{
    public const string Settings = "", Pin = "", History = "", Download = "", Shield = "",
        Private = "", Folder = "";
    public const string Back = "", Forward = "", Refresh = "", Stop = "",
        Home = "", More = "", Star = "", StarFill = "", Add = "",
        Close = "", Lock = "", Search = "", Globe = "", Warning = "",
        Check = "", Volume = "", Mute = "";
}

static class G
{
    public const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                          TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static GraphicsPath TopRound(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddLine(r.Right, r.Y + rad, r.Right, r.Bottom);
        p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        p.CloseFigure();
        return p;
    }

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static string IconFamily()
    {
        using var fc = new System.Drawing.Text.InstalledFontCollection();
        return fc.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }
}

// ======================================================================
//  Contrôles dessinés à la main (légers, pas d'images)
// ======================================================================

/// Bouton rond avec une icône, survol discret façon Chrome/Edge.
class IconButton : Control
{
    string glyph;
    bool hover, down;
    public Theme Theme = Theme.Light;
    public bool Highlight, Dim;
    string badge;

    public string Glyph { get => glyph; set { if (glyph != value) { glyph = value; Invalidate(); } } }
    /// Petit compteur en bas à droite (ex. nombre de pubs bloquées sur la page).
    public string Badge { get => badge; set { if (badge != value) { badge = value; Invalidate(); } } }

    public IconButton(string glyph, Font font)
    {
        this.glyph = glyph;
        Font = font;
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        if (Enabled && (hover || down))
        {
            using var b = new SolidBrush(down ? G.Mix(Theme.Hover, Theme.Text, .08f) : Theme.Hover);
            g.FillEllipse(b, 1, 1, Width - 3, Height - 3);
        }
        var col = !Enabled || Dim ? G.Mix(BackColor, Theme.Sub, .45f) : Highlight ? Theme.Accent : Theme.Text;
        TextRenderer.DrawText(g, glyph, Font, ClientRectangle, col, G.Center);
        if (!string.IsNullOrEmpty(badge))
        {
            using var f = new Font("Segoe UI", 6.5f, FontStyle.Bold);
            var sz = TextRenderer.MeasureText(badge, f, Size.Empty, TextFormatFlags.NoPadding);
            int h = sz.Height, w = Math.Max(h, sz.Width + LogicalToDeviceUnits(4));
            var r = new RectangleF(Width - w, Height - h, w, h);
            using var p = G.Round(r, h / 2f);
            using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
            var fg = Theme.Accent.GetBrightness() > .6f ? Color.FromArgb(10, 10, 12) : Color.White;
            TextRenderer.DrawText(g, badge, f, Rectangle.Round(r), fg, G.Center);
        }
    }
}

/// Bouton « pilule » coloré (ex. « Mise à jour prête · Redémarrer »).
class PillButton : Control
{
    bool hover;
    public Theme Theme = Theme.Light;
    public bool Subtle; // version discrète : fond de champ, texte normal

    public PillButton(string text, Font font)
    {
        Text = text;
        Font = font;
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    public int PreferredWidth => TextRenderer.MeasureText(Text, Font).Width + LogicalToDeviceUnits(28);
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        using var p = G.Round(r, r.Height / 2);
        var fill = Subtle ? (hover ? Theme.Hover : Theme.Field) : hover ? G.Mix(Theme.Accent, Theme.Text, .15f) : Theme.Accent;
        using (var b = new SolidBrush(fill)) g.FillPath(b, p);
        if (Subtle) { using var pen = new Pen(Theme.FieldBorder); g.DrawPath(pen, p); }
        var fg = Subtle ? Theme.Text : fill.GetBrightness() > .6f ? Color.FromArgb(32, 33, 36) : Color.White;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, G.Center);
    }
}

// ======================================================================
//  Extensions Chrome
// ======================================================================

class ExtInfo
{
    public string StoreId, RuntimeId, Name, Folder, Popup, Error;
    public bool Enabled = true;
    public Image Icon;
}

static class Extensions
{
    /// Extensions installées d'office au premier lancement.
    public static readonly (string Name, string StoreId)[] Defaults =
    {
        ("NordPass", "eiaeiblijfjekdanodkjadfinkhbfgcd"),
        ("TrendTrack", "cncdmciajmaojninbopinpijdodjgfpd"),
    };

    public static string ParseStoreId(string text) => Crx.ParseStoreId(text);
    public static Task DownloadAsync(string storeId, string folder) => Crx.DownloadAsync(storeId, folder);

    /// Lit la page popup, le nom et l'icône déclarés dans manifest.json.
    public static void ReadManifest(ExtInfo e)
    {
        try
        {
            var m = Crx.ReadManifest(e.Folder);
            e.Popup = m.Popup;
            if ((e.Name == null || e.Name.StartsWith("__MSG_")) && !string.IsNullOrWhiteSpace(m.Name) && !m.Name.StartsWith("__MSG_"))
                e.Name = m.Name;
            if (m.IconPath != null)
            {
                using var img = Image.FromFile(m.IconPath);
                e.Icon = new Bitmap(img, 20, 20);
            }
        }
        catch { }
    }
}

/// Petite fenêtre qui affiche le popup d'une extension (comme quand on clique son icône dans Chrome).
class ExtensionPopup : Form
{
    readonly WebView2 view;
    readonly Action<string> openInTab;

    public ExtensionPopup(CoreWebView2Environment env, string url, Rectangle anchor, Theme theme, Action<string> openInTab)
    {
        this.openInTab = openInTab;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = theme.FieldBorder;
        Padding = new Padding(1);
        Size = new Size(LogicalToDeviceUnits(380), LogicalToDeviceUnits(560));
        Place(anchor);

        view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = theme.Bar };
        Controls.Add(view);
        Deactivate += (_, _) => Close();

        Shown += async (_, _) =>
        {
            try { await view.EnsureCoreWebView2Async(env); } catch { Close(); return; }
            var c = view.CoreWebView2;
            c.Settings.IsStatusBarEnabled = false;
            c.NewWindowRequested += (_, e) => { e.Handled = true; this.openInTab(e.Uri); Close(); };
            c.WindowCloseRequested += (_, _) => Close();
            c.NavigationCompleted += async (_, _) => { await FitToContent(anchor); await Task.Delay(500); await FitToContent(anchor); };
            c.Navigate(url);
            view.Focus();
        };
    }

    void Place(Rectangle anchor)
    {
        var wa = Screen.FromRectangle(anchor).WorkingArea;
        int x = Math.Max(wa.Left, Math.Min(anchor.Right - Width, wa.Right - Width));
        int y = Math.Max(wa.Top, Math.Min(anchor.Bottom + LogicalToDeviceUnits(4), wa.Bottom - Height));
        Location = new Point(x, y);
    }

    /// Ajuste la fenêtre à la taille du contenu du popup (bornée comme dans Chrome).
    async Task FitToContent(Rectangle anchor)
    {
        if (IsDisposed || view.CoreWebView2 == null) return;
        try
        {
            var raw = await view.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify([document.documentElement.scrollWidth, document.documentElement.scrollHeight])");
            var size = JsonSerializer.Deserialize<int[]>(JsonSerializer.Deserialize<string>(raw));
            float k = DeviceDpi / 96f;
            int w = (int)(Math.Clamp(size[0], 260, 800) * k) + 2;
            int h = (int)(Math.Clamp(size[1], 120, 600) * k) + 2;
            if (IsDisposed) return;
            Size = new Size(w, h);
            Place(anchor);
        }
        catch { }
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // ombre portée
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys k)
    {
        if (k == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, k);
    }
}

/// Ajustement des horaires (± minutes par prière).
static class AdjustDialog
{
    public static Dictionary<string, int> Show(IWin32Window owner, Theme t, Dictionary<string, int> current)
    {
        var keys = new[] { ("Fajr", "Fajr"), ("Dhuhr", "Dhuhr"), ("Asr", "Asr"), ("Maghrib", "Maghrib"), ("Isha", "Isha") };
        using var f = new Form
        {
            Text = "Ajuster les horaires de prière", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, BackColor = t.Bar, ForeColor = t.Text,
            Font = new Font("Segoe UI", 9.5f), AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(360, 330)
        };
        f.Controls.Add(new Label { Text = "Décale chaque prière de quelques minutes\n(ex. pour coller aux horaires de ta mosquée) :", Left = 16, Top = 12, Width = 330, Height = 42 });
        var boxes = new Dictionary<string, NumericUpDown>();
        int y = 62;
        foreach (var (k, label) in keys)
        {
            f.Controls.Add(new Label { Text = label, Left = 24, Top = y + 4, Width = 120 });
            var n = new NumericUpDown { Left = 160, Top = y, Width = 80, Minimum = -120, Maximum = 120, Value = current.TryGetValue(k, out var v) ? v : 0, BackColor = t.Field, ForeColor = t.Text };
            f.Controls.Add(n);
            f.Controls.Add(new Label { Text = "min", Left = 248, Top = y + 4, Width = 40 });
            boxes[k] = n;
            y += 38;
        }
        var reset = new Button { Text = "Tout remettre à 0", Left = 16, Top = 282, Width = 130, Height = 30, FlatStyle = FlatStyle.System };
        reset.Click += (_, _) => { foreach (var b in boxes.Values) b.Value = 0; };
        var ok = new Button { Text = "Valider", DialogResult = DialogResult.OK, Left = 168, Top = 282, Width = 84, Height = 30, FlatStyle = FlatStyle.System };
        var cancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Left = 260, Top = 282, Width = 84, Height = 30, FlatStyle = FlatStyle.System };
        f.Controls.AddRange(new Control[] { reset, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(owner) != DialogResult.OK) return null;
        return boxes.ToDictionary(kv => kv.Key, kv => (int)kv.Value.Value);
    }
}

/// Mini boîte de saisie.
static class Prompt
{
    public static string Show(IWin32Window owner, string title, string text, Theme t, string okText = "Ajouter", string value = "")
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, BackColor = t.Bar, ForeColor = t.Text,
            Font = new Font("Segoe UI", 9.5f), AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(460, 150)
        };
        var lbl = new Label { Text = text, Left = 16, Top = 14, Width = 428, Height = 40 };
        var box = new TextBox { Left = 16, Top = 60, Width = 428, BackColor = t.Field, ForeColor = t.Text, Text = value ?? "" };
        var ok = new Button { Text = okText, DialogResult = DialogResult.OK, Left = 268, Top = 104, Width = 84, Height = 30, FlatStyle = FlatStyle.System };
        var cancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Left = 360, Top = 104, Width = 84, Height = 30, FlatStyle = FlatStyle.System };
        f.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}

class BrowserTab
{
    public WebView2 View;
    public string Title = "Nouvel onglet";
    public Image Icon;
    public bool Loading, IsHome = true, OpenedByScript, Audio, Muted;
    public bool Pinned, Private, AdsAllowed;
    public string LocalGlyph;   // icône des pages intégrées (ex. engrenage pour les réglages)
    public BrowserTab Opener;   // onglet d'origine d'une fenêtre ouverte par un site : on y revient à sa fermeture
    public readonly Dictionary<CoreWebView2Frame, string> PlayerFrames = new(); // lecteurs intégrés (Nur Live) et leur adresse
    /// Onglet restauré pas encore chargé : il ne démarre son moteur qu'au premier clic (démarrage rapide, peu de RAM).
    public bool Lazy;
    public string PendingUrl;
    public int Blocked;
    public string DarkScriptId, YtScriptId;
    public System.Windows.Forms.Timer SuspendTimer;
}

/// Barre d'onglets : onglets arrondis, icône du site, haut-parleur (son), bouton ✕, bouton +.
class TabStrip : Control
{
    public readonly List<BrowserTab> Tabs;
    public int Selected = -1, SpinAngle;
    public Theme Theme = Theme.Light;
    public event Action<int> TabSelect, TabClose, TabMute;
    public event Action<int, Point> TabMenu;
    public event Action NewTabRequested, PrivateTabRequested;

    readonly Font icons;
    readonly ToolTip tip = new();
    int hover = -1;
    bool hoverClose, hoverPlus, hoverAudio;

    public TabStrip(List<BrowserTab> tabs, Font icons)
    {
        Tabs = tabs;
        this.icons = icons;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    int Sc(int v) => LogicalToDeviceUnits(v);
    // Les onglets épinglés sont toujours regroupés à gauche, en version compacte (icône seule)
    int PinW => Sc(44);
    int PinnedCount() { int n = 0; while (n < Tabs.Count && Tabs[n].Pinned) n++; return n; }
    int TabWidth()
    {
        int p = PinnedCount();
        int n = Math.Max(1, Tabs.Count - p);
        int avail = Width - Sc(8) - Sc(48) - p * PinW;
        return Math.Max(Sc(56), Math.Min(Sc(240), avail / n));
    }
    Rectangle TabRect(int i)
    {
        int p = PinnedCount();
        if (i < p) return new Rectangle(Sc(8) + i * PinW, Sc(7), PinW, Height - Sc(7));
        int w = TabWidth();
        return new Rectangle(Sc(8) + p * PinW + (i - p) * w, Sc(7), w, Height - Sc(7));
    }
    bool HasClose(int i) => !Tabs[i].Pinned && (i == Selected || i == hover);
    Rectangle CloseRect(Rectangle r) { int s = Sc(20); return new Rectangle(r.Right - s - Sc(8), r.Top + (r.Height - s) / 2, s, s); }
    /// Le haut-parleur se place juste avant la croix (ou au bord si la croix est masquée).
    Rectangle AudioRect(int i, Rectangle r)
    {
        var c = CloseRect(r);
        return HasClose(i) ? new Rectangle(c.Left - c.Width - Sc(2), c.Top, c.Width, c.Height) : c;
    }
    static bool HasAudio(BrowserTab t) => t.Audio || t.Muted;
    Rectangle PlusRect()
    {
        int s = Sc(28), p = PinnedCount();
        return new Rectangle(Sc(8) + p * PinW + (Tabs.Count - p) * TabWidth() + Sc(6), Sc(7) + (Height - Sc(7) - s) / 2, s, s);
    }
    int Hit(Point p) { for (int i = 0; i < Tabs.Count; i++) if (TabRect(i).Contains(p)) return i; return -1; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Strip);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int isz = Sc(16);

        for (int i = 0; i < Tabs.Count; i++)
        {
            var r = TabRect(i);
            var t = Tabs[i];
            bool sel = i == Selected, hov = i == hover;

            if (sel || hov)
            {
                using var p = G.TopRound(new RectangleF(r.X, r.Y, r.Width, r.Height + 2), Sc(8));
                var fill = sel ? Theme.Bar : G.Mix(Theme.Strip, Theme.Bar, .5f);
                if (t.Private) fill = G.Mix(fill, Theme.Accent, .07f);
                using var b = new SolidBrush(fill);
                g.FillPath(b, p);
                if (sel)
                {
                    // trait d'accent sur l'onglet actif (signature M8)
                    using var ab = new SolidBrush(t.Private ? Theme.Sub : Theme.Accent);
                    using var ap = G.Round(new RectangleF(r.X + Sc(12), r.Y, r.Width - Sc(24), Sc(2)), Sc(1));
                    g.FillPath(ab, ap);
                }
            }
            else if (i + 1 < Tabs.Count && i + 1 != Selected && i + 1 != hover)
            {
                using var pen = new Pen(G.Mix(Theme.Strip, Theme.Sub, .35f));
                g.DrawLine(pen, r.Right, r.Top + Sc(9), r.Right, r.Bottom - Sc(9));
            }

            int x = t.Pinned ? r.X + (r.Width - isz) / 2 : r.X + Sc(12), cy = r.Top + r.Height / 2;
            var ir = new Rectangle(x, cy - isz / 2, isz, isz);
            if (t.Loading)
            {
                using var pen = new Pen(Theme.Accent, Math.Max(2, Sc(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, Rectangle.Inflate(ir, -1, -1), SpinAngle, 270);
            }
            else if (t.Private) TextRenderer.DrawText(g, Glyphs.Private, icons, ir, Theme.Text, G.Center);
            else if (t.IsHome) TextRenderer.DrawText(g, Glyphs.Home, icons, ir, Theme.Sub, G.Center);
            else if (t.LocalGlyph != null) TextRenderer.DrawText(g, t.LocalGlyph, icons, ir, sel ? Theme.Text : Theme.Sub, G.Center);
            else if (t.Icon != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(t.Icon, ir); }
            else TextRenderer.DrawText(g, Glyphs.Globe, icons, ir, Theme.Sub, G.Center);
            if (t.Lazy && !sel)
            {
                // onglet restauré pas encore chargé : petit point discret
                using var db = new SolidBrush(Theme.Sub);
                g.FillEllipse(db, ir.Right - Sc(3), ir.Bottom - Sc(3), Sc(5), Sc(5));
            }

            if (t.Pinned)
            {
                if (HasAudio(t))
                {
                    using var db = new SolidBrush(t.Muted ? Theme.Sub : Theme.Accent);
                    g.FillEllipse(db, ir.Right - Sc(2), ir.Top - Sc(3), Sc(6), Sc(6));
                }
                continue;
            }

            var cr = CloseRect(r);
            int textRight = sel || hov ? cr.Left - Sc(4) : r.Right - Sc(10);
            bool audio = HasAudio(t) && r.Width > Sc(90);
            if (audio)
            {
                var ar = AudioRect(i, r);
                if (hov && hoverAudio) { using var hb = new SolidBrush(Theme.Hover); g.FillEllipse(hb, ar); }
                TextRenderer.DrawText(g, t.Muted ? Glyphs.Mute : Glyphs.Volume, icons, ar, t.Muted ? Theme.Sub : Theme.Text, G.Center);
                textRight = ar.Left - Sc(4);
            }
            int tx = ir.Right + Sc(8);
            var textRect = new Rectangle(tx, r.Top, textRight - tx, r.Height);
            TextRenderer.DrawText(g, t.Title, Font, textRect, sel ? Theme.Text : Theme.Sub,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            if (sel || hov)
            {
                if (hov && hoverClose) { using var hb = new SolidBrush(Theme.Hover); g.FillEllipse(hb, cr); }
                TextRenderer.DrawText(g, Glyphs.Close, icons, cr, Theme.Text, G.Center);
            }
        }

        var pr = PlusRect();
        if (hoverPlus) { using var hb = new SolidBrush(G.Mix(Theme.Strip, Theme.Bar, .6f)); g.FillEllipse(hb, pr); }
        TextRenderer.DrawText(g, Glyphs.Add, icons, pr, Theme.Text, G.Center);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = Hit(e.Location);
        bool hc = h >= 0 && !Tabs[h].Pinned && CloseRect(TabRect(h)).Contains(e.Location);
        bool hp = PlusRect().Contains(e.Location);
        bool ha = h >= 0 && !Tabs[h].Pinned && HasAudio(Tabs[h]) && !hc && AudioRect(h, TabRect(h)).Contains(e.Location);
        if (h != hover || hc != hoverClose || hp != hoverPlus || ha != hoverAudio)
        {
            bool tipChange = h != hover || hp != hoverPlus || ha != hoverAudio;
            hover = h; hoverClose = hc; hoverPlus = hp; hoverAudio = ha;
            Invalidate();
            if (tipChange)
                tip.SetToolTip(this, ha ? (Tabs[h].Muted ? "Réactiver le son de cet onglet" : "Couper le son de cet onglet")
                    : h >= 0 ? (Tabs[h].Private ? "Onglet privé · " : "") + Tabs[h].Title : hp ? "Nouvel onglet (Ctrl+T) · clic droit : onglet privé" : null);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1; hoverClose = hoverPlus = hoverAudio = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int h = Hit(e.Location);
        if (e.Button == MouseButtons.Middle) { if (h >= 0) TabClose?.Invoke(h); return; }
        if (e.Button == MouseButtons.Right)
        {
            if (h >= 0) TabMenu?.Invoke(h, e.Location);
            else if (PlusRect().Contains(e.Location)) PrivateTabRequested?.Invoke();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (PlusRect().Contains(e.Location)) { NewTabRequested?.Invoke(); return; }
        if (h < 0) return;
        var r = TabRect(h);
        if (Tabs[h].Pinned) TabSelect?.Invoke(h);
        else if (CloseRect(r).Contains(e.Location)) TabClose?.Invoke(h);
        else if (HasAudio(Tabs[h]) && AudioRect(h, r).Contains(e.Location)) TabMute?.Invoke(h);
        else TabSelect?.Invoke(h);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && Hit(e.Location) < 0 && !PlusRect().Contains(e.Location))
            NewTabRequested?.Invoke();
    }
}

/// Barre de favoris sous la barre d'adresse (comme Chrome / Edge).
class FavBar : Control
{
    public List<(string Title, string Url)> Items = new();
    public Theme Theme = Theme.Light;
    public Func<string, Image> IconFor;
    public event Action<string, bool> Open;          // adresse, nouvel onglet ?
    public event Action<int, Point> ItemMenu;
    public event Action<int, Point> Overflow;        // premier favori masqué, position

    readonly Font icons;
    readonly ToolTip tip = new();
    readonly List<(Rectangle R, int Index)> slots = new();
    Rectangle moreRect = Rectangle.Empty;
    int hover = -1, firstHidden = -1;
    bool hoverMore;

    public FavBar(Font icons)
    {
        this.icons = icons;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    int Sc(int v) => LogicalToDeviceUnits(v);

    void Relayout()
    {
        slots.Clear(); moreRect = Rectangle.Empty; firstHidden = -1;
        int x = Sc(8), h = Height - Sc(6), y = Sc(3), isz = Sc(16), maxText = Sc(150);
        int limit = Width - Sc(40);
        for (int i = 0; i < Items.Count; i++)
        {
            int tw = Math.Min(maxText, TextRenderer.MeasureText(Items[i].Title, Font, Size.Empty, TextFormatFlags.NoPrefix).Width);
            int w = Sc(8) + isz + Sc(6) + tw + Sc(10);
            if (x + w > limit) { firstHidden = i; break; }
            slots.Add((new Rectangle(x, y, w, h), i));
            x += w + Sc(2);
        }
        if (firstHidden >= 0) moreRect = new Rectangle(Width - Sc(34), y, Sc(28), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Relayout();
        var g = e.Graphics;
        g.Clear(Theme.Bar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = new Pen(Theme.FieldBorder)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        if (Items.Count == 0)
        {
            TextRenderer.DrawText(g, "Ajoute tes sites préférés ici avec l'étoile ☆ de la barre d'adresse (Ctrl+D)", Font,
                new Rectangle(Sc(12), 0, Width - Sc(24), Height), Theme.Sub, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }
        int isz = Sc(16);
        foreach (var (r, i) in slots)
        {
            if (i == hover) { using var p = G.Round(r, Sc(6)); using var b = new SolidBrush(Theme.Hover); g.FillPath(b, p); }
            var ir = new Rectangle(r.X + Sc(8), r.Y + (r.Height - isz) / 2, isz, isz);
            var img = IconFor?.Invoke(Items[i].Url);
            if (img != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(img, ir); }
            else
            {
                using var b = new SolidBrush(G.Mix(Theme.Bar, Theme.Sub, .35f));
                g.FillEllipse(b, ir);
                var letter = Items[i].Title.Length > 0 ? Items[i].Title.Substring(0, 1).ToUpperInvariant() : "?";
                using var f = new Font(Font.FontFamily, 7f, FontStyle.Bold);
                TextRenderer.DrawText(g, letter, f, ir, Theme.Text, G.Center);
            }
            var tr = new Rectangle(ir.Right + Sc(6), r.Y, r.Right - Sc(10) - ir.Right - Sc(6), r.Height);
            TextRenderer.DrawText(g, Items[i].Title, Font, tr, Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        if (!moreRect.IsEmpty)
        {
            if (hoverMore) { using var p = G.Round(moreRect, Sc(6)); using var b = new SolidBrush(Theme.Hover); g.FillPath(b, p); }
            TextRenderer.DrawText(g, "»", Font, moreRect, Theme.Text, G.Center);
        }
    }

    int HitIndex(Point p) { foreach (var (r, i) in slots) if (r.Contains(p)) return i; return -1; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitIndex(e.Location);
        bool hm = moreRect.Contains(e.Location);
        if (h != hover || hm != hoverMore)
        {
            hover = h; hoverMore = hm;
            Invalidate();
            tip.SetToolTip(this, h >= 0 ? Items[h].Title + "\n" + Items[h].Url : hm ? "Autres favoris" : null);
        }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; hoverMore = false; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (moreRect.Contains(e.Location) && e.Button == MouseButtons.Left) { Overflow?.Invoke(firstHidden, new Point(moreRect.Right, moreRect.Bottom)); return; }
        int h = HitIndex(e.Location);
        if (h < 0) return;
        if (e.Button == MouseButtons.Left) Open?.Invoke(Items[h].Url, (ModifierKeys & Keys.Control) != 0);
        else if (e.Button == MouseButtons.Middle) Open?.Invoke(Items[h].Url, true);
        else if (e.Button == MouseButtons.Right) ItemMenu?.Invoke(h, e.Location);
    }
}

/// Barre d'adresse arrondie : icône (cadenas / loupe) + texte + étoile favoris.
class AddressBox : Panel
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None };
    public readonly IconButton Star, Shield;
    readonly Font badgeFont;
    string badge = Glyphs.Search;
    Theme theme = Theme.Light;

    /// Icône à gauche (loupe, cadenas…), dessinée directement dans le champ pour ne jamais mordre sur l'arrondi.
    public string BadgeGlyph { get => badge; set { if (badge != value) { badge = value; Invalidate(); } } }

    public AddressBox(Font iconFont, Font iconSmall, Font textFont)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Box.Font = textFont;
        badgeFont = iconSmall;
        Star = new IconButton(Glyphs.Star, iconFont);
        Shield = new IconButton(Glyphs.Shield, iconSmall);
        Controls.Add(Box);
        Controls.Add(Star);
        Controls.Add(Shield);
        Box.GotFocus += (_, _) => Recolor();
        Box.LostFocus += (_, _) => Recolor();
        Click += (_, _) => Box.Focus();
        Star.Resize += (_, _) => RoundRegion(Star);
    }

    // Les boutons du champ ont des coins arrondis : rien n'est dessiné hors du cadre (pas d'angle sur la bordure)
    static void RoundRegion(Control c)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, c.Width, c.Height);
        c.Region = new Region(path);
    }

    int Sc(int v) => LogicalToDeviceUnits(v);
    Rectangle BadgeRect => new(Height / 2 - Sc(4), 0, Sc(18), Height);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int h = Height, s = h - Sc(8);
        Star.SetBounds(Width - h / 2 - s / 2 - Sc(2), (h - s) / 2, s, s);
        int sw = s + Sc(8);
        int shieldRight = Star.Visible ? Star.Left - Sc(2) : Width - h / 2;
        Shield.SetBounds(shieldRight - sw, (h - s) / 2, sw, s);
        int bh = Box.PreferredHeight;
        int left = BadgeRect.Right + Sc(8);
        int right = Shield.Visible ? Shield.Left - Sc(4) : Star.Visible ? Star.Left - Sc(4) : Width - h / 2;
        Box.SetBounds(left, (h - bh) / 2, Math.Max(10, right - left), bh);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        bool f = Box.Focused;
        float inset = f ? Sc(1) : 0.5f;
        var r = new RectangleF(inset, inset, Width - 1 - inset * 2, Height - 1 - inset * 2);
        using var p = G.Round(r, r.Height / 2);
        using (var b = new SolidBrush(f ? theme.Bar : theme.Field)) g.FillPath(b, p);
        // champ actif : contour plus doux qu'un trait blanc plein (thème M8), mais bien visible
        using (var pen = new Pen(f ? G.Mix(theme.Accent, theme.Bar, .35f) : theme.FieldBorder, f ? Sc(2) : 1)) g.DrawPath(pen, p);
        TextRenderer.DrawText(g, badge, badgeFont, BadgeRect, theme.Sub, G.Center);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Box.Focus(); }

    public void ApplyTheme(Theme t, Color parentBack)
    {
        theme = t;
        BackColor = parentBack;
        Star.Theme = t;
        Shield.Theme = t;
        Recolor();
    }

    void Recolor()
    {
        var fill = Box.Focused ? theme.Bar : theme.Field;
        Box.BackColor = fill;
        Box.ForeColor = theme.Text;
        Star.BackColor = fill;
        Shield.BackColor = fill;
        Invalidate();
        Star.Invalidate();
        Shield.Invalidate();
    }
}

/// Menus aux couleurs du thème.
class MenuColors : ProfessionalColorTable
{
    readonly Theme t;
    public MenuColors(Theme t) { this.t = t; UseSystemColors = false; }
    public override Color ToolStripDropDownBackground => t.Bar;
    public override Color ImageMarginGradientBegin => t.Bar;
    public override Color ImageMarginGradientMiddle => t.Bar;
    public override Color ImageMarginGradientEnd => t.Bar;
    public override Color MenuBorder => t.FieldBorder;
    public override Color MenuItemBorder => t.Hover;
    public override Color MenuItemSelected => t.Hover;
    public override Color MenuItemSelectedGradientBegin => t.Hover;
    public override Color MenuItemSelectedGradientEnd => t.Hover;
    public override Color MenuItemPressedGradientBegin => t.Hover;
    public override Color MenuItemPressedGradientEnd => t.Hover;
    public override Color SeparatorDark => t.FieldBorder;
    public override Color SeparatorLight => t.Bar;
    public override Color CheckBackground => t.Bar;
    public override Color CheckSelectedBackground => t.Hover;
    public override Color CheckPressedBackground => t.Hover;
}

class MenuRenderer : ToolStripProfessionalRenderer
{
    readonly Theme t;
    readonly Font icons;
    public MenuRenderer(Theme t, Font icons) : base(new MenuColors(t)) { this.t = t; this.icons = icons; RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? t.Text : G.Mix(t.Bar, t.Sub, .6f);
        base.OnRenderItemText(e);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = t.Sub;
        base.OnRenderArrow(e);
    }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, Glyphs.Check, icons, e.ImageRectangle, t.Accent, G.Center);
    }
}

// ======================================================================
//  La fenêtre du navigateur
// ======================================================================

class BrowserForm : Form
{
    const string HomeHost = "litebrowser.home";
    const string HomeUrl = "https://litebrowser.home/index.html";
    const string SettingsUrl = "https://litebrowser.home/settings.html";

    // Réglages ajoutés en v4 (tous modifiables depuis la page Réglages)
    string startupMode = "session";             // session (rouvrir mes onglets) | home
    int tabSleepSec = 30;                        // un onglet inactif est gelé après ce délai (0 = jamais)
    string trackingLevel = "basic";              // basic | balanced | strict
    HashSet<string> adblockAllow = new(StringComparer.OrdinalIgnoreCase);   // sites où le bloqueur est coupé
    bool historySave = true, clearOnExit, downloadAsk, closingDone;
    string downloadDir;
    int prayerReminder;                          // rappel X min avant chaque prière (0 = non)
    string lastReminded;
    string sessionPath;

    static readonly (string Id, string Name, string Url)[] Engines =
    {
        ("google", "Google", "https://www.google.com/search?q="),
        ("ddg", "DuckDuckGo", "https://duckduckgo.com/?q="),
        ("bing", "Bing", "https://www.bing.com/search?q="),
        ("qwant", "Qwant", "https://www.qwant.com/?q="),
        ("ecosia", "Ecosia", "https://www.ecosia.org/search?q="),
    };

    static readonly (string Title, string Url)[] DefaultTiles =
    {
        ("YouTube", "https://www.youtube.com/"), ("Gmail", "https://mail.google.com/"),
        ("Wikipédia", "https://fr.wikipedia.org/"), ("Twitch", "https://www.twitch.tv/"),
        ("Reddit", "https://www.reddit.com/"), ("Amazon", "https://www.amazon.fr/"),
        ("Netflix", "https://www.netflix.com/"), ("Discord", "https://discord.com/app"),
    };

    // Mode sombre forcé : on inverse la page, puis on ré-inverse images et vidéos
    const string ForceDarkJs =
        "(function(){if(location.hostname==='litebrowser.home')return;" +
        "function a(){if(document.getElementById('lb-dark'))return;" +
        "var s=document.createElement('style');s.id='lb-dark';" +
        "s.textContent='html{filter:invert(1) hue-rotate(180deg)!important;background:#fff!important}" +
        "img,video,picture,canvas,iframe,embed,object{filter:invert(1) hue-rotate(180deg)!important}';" +
        "(document.head||document.documentElement).appendChild(s);}" +
        "if(document.documentElement)a();else document.addEventListener('DOMContentLoaded',a);})();";
    const string UnforceDarkJs = "var s=document.getElementById('lb-dark');if(s)s.remove();";

    readonly List<BrowserTab> tabs = new();
    readonly List<(string Title, string Url)> favorites = new();
    readonly List<(string Title, string Url)> history = new();
    readonly List<string> closedTabs = new();
    readonly HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> suggestSet = new(StringComparer.OrdinalIgnoreCase);
    readonly AutoCompleteStringCollection suggestions = new();
    readonly Dictionary<string, string> settings = new();
    readonly ToolTip tip = new();

    readonly TabStrip strip;
    readonly Panel toolbar = new() { Dock = DockStyle.Top };
    readonly Panel content = new() { Dock = DockStyle.Fill };
    readonly AddressBox addr;
    readonly IconButton backBtn, fwdBtn, reloadBtn, homeBtn, menuBtn, extBtn;
    readonly PillButton updatePill;
    readonly List<ExtInfo> extensions = new();
    readonly string extDir, extListPath;
    CoreWebView2Profile profile;
    bool extStarted, extBusy;
    ExtensionPopup extPopup;
    bool updateReady, updateBusy;
    readonly System.Windows.Forms.Timer spin = new() { Interval = 40 };
    readonly Font iconFont, iconSmall;
    readonly string dataDir, favPath, histPath, settingsPath, homeDir;

    CoreWebView2Environment env;
    ContextMenuStrip lastMenu;
    Theme theme = Theme.Light;
    int sel = -1, blockedCount;
    bool forceDark, adblock = true, fullscreen, selectOnClick;
    string themeName = "m8";                    // m8 (Gentle Mates) | dark | light
    bool dark => themeName != "light";

    // Matchs M8, lives, prières, notifications
    M8Service m8;
    List<Channel> channels = new();
    Dictionary<string, LiveInfo> liveStatus = new();
    readonly HashSet<string> multiIds = new();          // chaînes ouvertes dans les onglets multistream
    readonly HashSet<string> matchMutes = new();
    HashSet<string> firedAlerts = new();
    readonly Dictionary<string, bool> m8Games = new();
    bool m8Notify = true, liveNotify = true, statusBusy, pageFullscreen;
    int m8Reminder = 15;
    DateTime lastMatchTry = DateTime.MinValue;
    string m8Path, channelsPath, firedPath;
    readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 120_000 };
    readonly List<string> pendingSuggestions = new();
    bool prayerPending;
    string prayerSource = "calc", prayerMosque, prayerMosqueName, prayerOffsets = "";
    PrayerDay prayerDay;
    string prayerError, prayerCity, lastNotified;
    double prayerLat = double.NaN, prayerLon = double.NaN;
    int prayerMethod = 12;
    DateTime lastPrayerTry;
    bool prayerNotify = true, prayerAuto = true, prayerBusy;
    PillButton prayerChip;
    NotifyIcon tray;
    Action trayClick;
    readonly System.Windows.Forms.Timer minuteTimer = new() { Interval = 20_000 };
    FormWindowState prevState = FormWindowState.Maximized;
    string engineId = "google";

    int S(int v) => LogicalToDeviceUnits(v); // taille qui suit la mise à l'échelle Windows
    BrowserTab Cur => sel >= 0 && sel < tabs.Count ? tabs[sel] : null;
    CoreWebView2 Core => Cur?.View?.CoreWebView2;
    (string Id, string Name, string Url) Engine => Engines.First(e => e.Id == engineId);
    static bool IsHome(string url) => url != null && url.StartsWith(HomeUrl, StringComparison.OrdinalIgnoreCase);
    static bool IsLocalPage(string url) => url != null && (url.StartsWith("https://" + HomeHost + "/", StringComparison.OrdinalIgnoreCase) || IsNurLivePage(url));
    static bool IsNurLivePage(string url) => url != null && url.StartsWith("https://" + NurLive.Host + "/", StringComparison.OrdinalIgnoreCase);
    const string MultiUrl = "https://litebrowser.home/multi.html";

    public BrowserForm()
    {
        Text = "LiteBrowser";
        var wa = Screen.PrimaryScreen.WorkingArea;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(wa.Width * 4 / 5, wa.Height * 4 / 5);
        WindowState = FormWindowState.Maximized;
        if (ShotsDir != null) { WindowState = FormWindowState.Normal; Size = new Size(1600, 900); } // captures : fenêtre discrète
        MinimumSize = new Size(S(520), S(320));
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        dataDir = AppPaths.Data;
        Directory.CreateDirectory(dataDir);
        favPath = Path.Combine(dataDir, "favoris.txt");
        histPath = Path.Combine(dataDir, "historique.txt");
        settingsPath = Path.Combine(dataDir, "reglages.txt");
        homeDir = Path.Combine(dataDir, "accueil");
        extDir = Path.Combine(dataDir, "extensions");
        extListPath = Path.Combine(dataDir, "extensions.txt");
        sessionPath = Path.Combine(dataDir, "session.txt");

        LoadSettings();
        favorites.AddRange(ReadPairs(favPath));
        history.AddRange(ReadPairs(histPath));
        if (history.Count > 500) { history.RemoveRange(0, history.Count - 500); SaveList(histPath, history); }
        foreach (var f in favorites) AddSuggestion(f.Url);
        foreach (var h in history) AddSuggestion(h.Url);

        var fam = G.IconFamily();
        iconFont = new Font(fam, 11f);
        iconSmall = new Font(fam, 8f);

        // --- Onglets ---
        strip = new TabStrip(tabs, iconSmall) { Dock = DockStyle.Top, Height = S(42), Font = new Font("Segoe UI", 9f) };
        strip.TabSelect += SelectTab;
        strip.TabClose += CloseTab;
        strip.NewTabRequested += () => _ = NewTab();
        strip.PrivateTabRequested += () => _ = NewTab(priv: true);
        strip.TabMute += i => ToggleMute(i);
        strip.TabMenu += (i, pt) => ShowTabMenu(i, pt);

        favBar = new FavBar(iconSmall) { Dock = DockStyle.Top, Height = S(32), Font = new Font("Segoe UI", 9f) };
        favBar.IconFor = FavIcon;
        favBar.Open += (url, newTab) => { if (newTab || Core == null) _ = NewTab(url); else { Core.Navigate(url); Cur.View.Focus(); } };
        favBar.ItemMenu += (i, pt) => ShowFavMenu(i, favBar, pt);
        favBar.Overflow += (first, pt) => ShowFavOverflow(first, pt);

        // --- Barre d'outils ---
        toolbar.Height = S(46);
        backBtn = Btn(Glyphs.Back, "Page précédente (Alt+←)", () => Core?.GoBack());
        fwdBtn = Btn(Glyphs.Forward, "Page suivante (Alt+→)", () => Core?.GoForward());
        reloadBtn = Btn(Glyphs.Refresh, "Actualiser (F5)", () => { if (Cur?.Loading == true) Core?.Stop(); else Core?.Reload(); });
        homeBtn = Btn(Glyphs.Home, "Page d'accueil", () => Core?.Navigate(HomeUrl));
        menuBtn = Btn(Glyphs.More, "Menu : onglet privé, favoris, historique, réglages…", ShowMenu);
        extBtn = Btn("", "Extensions (NordPass, TrendTrack…)", ShowExtensionsMenu);

        addr = new AddressBox(iconFont, iconSmall, new Font("Segoe UI", 10.5f));
        addr.Star.Click += (_, _) => ToggleFavorite();
        tip.SetToolTip(addr.Star, "Ajouter / retirer des favoris (Ctrl+D)");
        addr.Shield.Click += (_, _) => ShowShieldMenu();
        addr.Box.AutoCompleteMode = AutoCompleteMode.Suggest;
        addr.Box.AutoCompleteSource = AutoCompleteSource.CustomSource;
        addr.Box.AutoCompleteCustomSource = suggestions;
        addr.Box.TextChanged += (_, _) => { if (!settingAddress && addr.Box.Focused) addrEdited = true; };
        addr.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                addrEdited = false;
                var url = FromAlias(addr.Box.Text) ?? ToUrl(addr.Box.Text);
                if (url != null && Core != null) { Core.Navigate(url); Cur.View.Focus(); }
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                addrEdited = false;
                UpdateUi();
                Cur?.View.Focus();
            }
        };
        addr.Box.Enter += (_, _) => { selectOnClick = true; BeginInvoke(new Action(() => addr.Box.SelectAll())); };
        addr.Box.MouseUp += (_, _) => { if (selectOnClick && addr.Box.SelectionLength == 0) addr.Box.SelectAll(); selectOnClick = false; };
        addr.Box.Leave += (_, _) =>
        {
            if (pendingSuggestions.Count > 0) { suggestions.AddRange(pendingSuggestions.ToArray()); pendingSuggestions.Clear(); }
            addrEdited = false; // en quittant la barre, on revient à l'adresse de la page
            UpdateUi();
        };

        updatePill = new PillButton("Mise à jour prête · Redémarrer", new Font("Segoe UI", 9f, FontStyle.Bold)) { Visible = false };
        tip.SetToolTip(updatePill, "Une nouvelle version a été installée. Clique pour relancer LiteBrowser.");
        updatePill.Click += (_, _) => { Program.RestartRequested = true; Close(); };

        prayerChip = new PillButton("", new Font("Segoe UI", 9f)) { Subtle = true, Visible = false };
        prayerChip.Click += (_, _) => ShowPrayerMenu();

        m8Path = Path.Combine(dataDir, "m8.json");
        channelsPath = Path.Combine(dataDir, "chaines.json");
        firedPath = Path.Combine(dataDir, "alertes.json");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        m8 = new M8Service(Path.Combine(dataDir, "matches-cache.json"),
            Path.Combine(appData, "nur-live", "matches-cache.json"), Path.Combine(appData, "Nur Live", "matches-cache.json"));
        m8.Updated += () => { if (IsHandleCreated) BeginInvoke(new Action(RefreshLocalPages)); };
        LoadM8Settings();
        LoadChannels(appData);
        minuteTimer.Tick += (_, _) => MinuteTick();
        statusTimer.Tick += (_, _) => _ = RefreshStatus();
        FormClosed += (_, _) => { try { tray?.Dispose(); } catch { } };
        FormClosing += async (_, e) =>
        {
            if (closingDone) return;
            SaveSession();
            if (!clearOnExit || SmokeReport != null) return;
            // « Tout effacer en quittant » : cookies, cache et historique, avant la vraie fermeture
            e.Cancel = true;
            closingDone = true;
            Hide();
            await ClearBrowsingData();
            Close();
        };
        if (SmokeReport != null || ShotsDir != null)
            FormClosing += (_, e) => CrashLog.Write("FormClosing " + e.CloseReason, new Exception("fermeture demandée par :\n" + Environment.StackTrace));

        toolbar.Controls.AddRange(new Control[] { backBtn, fwdBtn, reloadBtn, homeBtn, addr, updatePill, prayerChip, extBtn, menuBtn });
        toolbar.Resize += (_, _) => LayoutToolbar();
        toolbar.Paint += (_, e) =>
        {
            using var p = new Pen(theme.FieldBorder);
            e.Graphics.DrawLine(p, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
        };

        Controls.Add(content);
        Controls.Add(favBar);
        Controls.Add(toolbar);
        Controls.Add(strip);
        favBar.Visible = showFavBar;
        RefreshFavBar();

        spin.Tick += (_, _) =>
        {
            strip.SpinAngle = (strip.SpinAngle + 30) % 360;
            if (!tabs.Exists(t => t.Loading)) spin.Stop();
            strip.Invalidate();
        };

        ApplyTheme();
        UpdatePlaceholder();
        UpdateUi();

        Load += async (_, _) =>
        {
            try { await InitAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Impossible de démarrer le moteur web (WebView2).\n\n" + ex.Message, "LiteBrowser",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            try
            {
                if (!RestoreSession()) await NewTab();
                minuteTimer.Start();
                statusTimer.Start();
                _ = RefreshPrayers();
                _ = RefreshMatches();
                _ = RefreshStatus();
                await Task.Delay(3000);
                if (SmokeReport != null) { await RunSmoke(); return; }
                if (ShotsDir != null) { await RunShots(); return; }
                await CheckUpdates(silent: true);
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        };
    }

    // ---------- Mises à jour ----------

    async Task CheckUpdates(bool silent)
    {
        if (updateReady)
        {
            if (!silent) { Program.RestartRequested = true; Close(); }
            return;
        }
        if (SmokeReport != null || AppPaths.Dev) return;
        if (updateBusy) return;
        updateBusy = true;
        try
        {
            var found = await Updater.CheckAsync();
            if (found == null)
            {
                if (!silent)
                    MessageBox.Show(this, $"Tu as déjà la dernière version (v{Updater.Current}).", "Mises à jour",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            await Updater.InstallAsync(found.Value.Url);
            updateReady = true;
            updatePill.Text = $"v{found.Value.Version} prête · Redémarrer";
            updatePill.Visible = true;
            LayoutToolbar();
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show(this, "Impossible de vérifier les mises à jour.\n\n" + ex.Message, "Mises à jour",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { updateBusy = false; }
    }

    IconButton Btn(string glyph, string tipText, Action a)
    {
        var b = new IconButton(glyph, iconFont);
        tip.SetToolTip(b, tipText);
        b.Click += (_, _) => a();
        return b;
    }

    void LayoutToolbar()
    {
        int h = toolbar.Height, b = S(34), y = (h - b) / 2, x = S(8);
        foreach (var btn in new[] { backBtn, fwdBtn, reloadBtn, homeBtn }) { btn.SetBounds(x, y, b, b); x += b + S(2); }
        menuBtn.SetBounds(toolbar.Width - S(8) - b, y, b, b);
        extBtn.SetBounds(menuBtn.Left - S(2) - b, y, b, b);
        int right = extBtn.Left - S(8);
        int ah = S(34), ax = x + S(6);
        // Les pastilles ne s'affichent que s'il reste assez de place pour la barre d'adresse
        foreach (var pill in new[] { prayerChip, updatePill })
        {
            bool want = pill == prayerChip ? prayerChip.Tag as string == "on" : updateReady;
            int pw = pill.PreferredWidth, ph = S(30);
            if (want && right - pw - S(8) - ax >= S(220))
            {
                pill.SetBounds(right - pw, (h - ph) / 2, pw, ph);
                pill.Visible = true;
                right = pill.Left - S(8);
            }
            else pill.Visible = false;
        }
        addr.SetBounds(ax, (h - ah) / 2, Math.Max(S(100), right - ax), ah);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        strip.Height = S(42);
        favBar.Height = S(32);
        toolbar.Height = S(46);
        LayoutToolbar();
    }

    // ---------- Démarrage ----------

    async Task InitAsync()
    {
        LoadBlocklist();

        // Page d'accueil intégrée à l'exe, recopiée à chaque démarrage
        Directory.CreateDirectory(homeDir);
        foreach (var (res, file) in new[] { ("home.html", "index.html"), ("multi.html", "multi.html"), ("settings.html", "settings.html") })
        {
            try
            {
                using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(res);
                using var dst = File.Create(Path.Combine(homeDir, file));
                src.CopyTo(dst);
            }
            catch { /* déjà présente / ouverte par une autre fenêtre */ }
        }

        // On coupe tout ce qui tourne en arrière-plan pour rien
        var opts = new CoreWebView2EnvironmentOptions(
            "--disable-background-networking --disable-component-update --disable-sync " +
            "--disable-domain-reliability --disable-breakpad --no-pings " +
            "--renderer-process-limit=3 " +
            "--disable-features=Translate,msSmartScreenProtection,msEdgeShoppingAssistant,AutofillServerCommunication");
        opts.AreBrowserExtensionsEnabled = true;

        env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDir, "WebView2"), opts);
        PrepareNurLive();
    }

    // ---------- Session : rouvrir les onglets de la dernière fois ----------

    void SaveSession()
    {
        if (SmokeReport != null || ShotsDir != null || tabs.Count == 0) return;
        try
        {
            var keep = tabs.Where(t => !t.Private).ToList();
            var list = keep.Select(t =>
            {
                var src = t.View.CoreWebView2?.Source ?? t.PendingUrl ?? "";
                bool web = src.StartsWith("http://") || src.StartsWith("https://");
                // pages intégrées (accueil, réglages…) : rouvertes comme page d'accueil
                var url = !web || IsLocalPage(src) ? "" : src;
                return new SessionTab(url, t.Title, t.Pinned);
            });
            int s = Cur != null && !Cur.Private ? keep.IndexOf(Cur) : 0;
            File.WriteAllText(sessionPath, Session.Serialize(list, Math.Max(0, s)));
        }
        catch { }
    }

    /// Recrée les onglets de la dernière session, sans les charger (sauf celui affiché). false = rien à rouvrir.
    bool RestoreSession()
    {
        if (SmokeReport != null || startupMode != "session" || !File.Exists(sessionPath)) return false;
        List<SessionTab> list; int selected;
        try { (list, selected) = Session.Parse(File.ReadAllText(sessionPath)); } catch { return false; }
        // une seule page d'accueil vide : autant ouvrir normalement
        if (list.Count == 0 || (list.Count == 1 && list[0].Url == "")) return false;
        foreach (var s in list.OrderByDescending(x => x.Pinned))
        {
            var t = CreateTab(s.Url == "", priv: false);
            t.Lazy = true;
            t.PendingUrl = s.Url;
            t.Pinned = s.Pinned;
            if (!string.IsNullOrWhiteSpace(s.Title)) t.Title = s.Title;
            if (s.Url != "") t.Icon = CachedIcon(s.Url);
            tabs.Add(t);
        }
        var want = list[selected];
        int idx = tabs.FindIndex(t => t.PendingUrl == want.Url && t.Pinned == want.Pinned);
        SelectTab(Math.Max(0, idx));
        return true;
    }

    /// Icône d'un site déjà visité, gardée sur le disque (évite de la redemander à un service externe).
    Image CachedIcon(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : null;
        var file = host == null ? null : Path.Combine(dataDir, "icones", host + ".png");
        if (file == null || !File.Exists(file)) return null;
        try { using var tmp = Image.FromFile(file); return new Bitmap(tmp); } catch { return null; }
    }

    // ---------- Onglets ----------

    BrowserTab CreateTab(bool home, bool priv)
    {
        var t = new BrowserTab { IsHome = home, Private = priv, Title = priv && home ? "Onglet privé" : "Nouvel onglet" };
        t.View = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = theme.Bar };
        content.Controls.Add(t.View);
        return t;
    }

    async Task NewTab(string url = null, CoreWebView2NewWindowRequestedEventArgs popup = null, CoreWebView2Deferral deferral = null, bool priv = false)
    {
        if (env == null) { deferral?.Complete(); return; }
        var t = CreateTab(url == null && popup == null, priv);
        t.OpenedByScript = popup != null;
        if (popup != null) t.Opener = Cur;
        int idx = sel >= 0 ? sel + 1 : tabs.Count; // s'ouvre juste à droite de l'onglet actuel
        idx = Math.Max(idx, tabs.TakeWhile(x => x.Pinned).Count()); // jamais au milieu des onglets épinglés
        tabs.Insert(idx, t);
        SelectTab(idx);
        await StartTab(t, url, popup, deferral);
    }

    /// Démarre le moteur web d'un onglet et branche tous ses évènements.
    async Task StartTab(BrowserTab t, string url, CoreWebView2NewWindowRequestedEventArgs popup = null, CoreWebView2Deferral deferral = null)
    {
        t.Lazy = false;
        t.PendingUrl = null;
        try
        {
            if (t.Private)
            {
                // Navigation privée : profil en mémoire, rien n'est gardé une fois les onglets privés fermés
                var o = env.CreateCoreWebView2ControllerOptions();
                o.ProfileName = "Prive";
                o.IsInPrivateModeEnabled = true;
                await t.View.EnsureCoreWebView2Async(env, o);
            }
            else await t.View.EnsureCoreWebView2Async(env);
        }
        catch { if (popup != null) popup.Handled = true; deferral?.Complete(); return; }
        if (!tabs.Contains(t)) { if (popup != null) popup.Handled = true; deferral?.Complete(); return; } // fermé entre-temps
        var core = t.View.CoreWebView2;
        if (ShotsDir != null) core.IsMuted = true; // les tests ne font jamais de bruit sur le PC

        var s = core.Settings;
        s.IsStatusBarEnabled = true; // affiche l'adresse des liens au survol
        s.IsPasswordAutosaveEnabled = false;   // les mots de passe restent dans NordPass, pas dans le navigateur
        s.IsGeneralAutofillEnabled = !t.Private;
        // Protection contre le pistage réglable ; « De base » laisse fonctionner les connexions intégrées (chat Twitch)
        core.Profile.PreferredTrackingPreventionLevel = TrackingLevel();
        core.Profile.PreferredColorScheme = Scheme();
        if (!string.IsNullOrEmpty(downloadDir) && Directory.Exists(downloadDir))
            try { core.Profile.DefaultDownloadFolderPath = downloadDir; } catch { }
        // Pages intégrées : accessibles en navigation, mais illisibles par les autres sites (pas de CORS)
        core.SetVirtualHostNameToFolderMapping(HomeHost, homeDir, CoreWebView2HostResourceAccessKind.DenyCors);
        if (nurLiveUi != null)
        {
            core.SetVirtualHostNameToFolderMapping(NurLive.Host, nurLiveUi, CoreWebView2HostResourceAccessKind.Allow);
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(nurBridge); } catch { }
        }

        // Bloqueur de pubs / traqueurs : la requête n'est même pas envoyée.
        // On ne filtre que les ressources des pages (scripts, images, requêtes…) : jamais les pages ni les
        // téléchargements, que l'interception pouvait interrompre (« Download interrupted »).
        foreach (var ctx in new[]
        {
            CoreWebView2WebResourceContext.Script, CoreWebView2WebResourceContext.Image,
            CoreWebView2WebResourceContext.XmlHttpRequest, CoreWebView2WebResourceContext.Fetch,
            CoreWebView2WebResourceContext.Stylesheet, CoreWebView2WebResourceContext.Font,
            CoreWebView2WebResourceContext.Ping, CoreWebView2WebResourceContext.EventSource,
        })
            core.AddWebResourceRequestedFilter("*", ctx);
        core.WebResourceRequested += (_, e) =>
        {
            if (!adblock || t.AdsAllowed) return;
            if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u) && IsBlocked(u.Host))
            {
                e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
                Counted(t);
            }
        };

        // Fenêtres ouvertes par la page (connexion Google/Discord, paiement…) : un vrai onglet relié à la page d'origine
        core.NewWindowRequested += (_, e) =>
        {
            // Une page d'extension déjà ouverte (ex. accueil de NordPass) est remise au premier plan, pas dupliquée
            if (e.Uri.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            {
                var page = e.Uri.Split('#')[0];
                int same = tabs.FindIndex(x => x.View.CoreWebView2?.Source?.Split('#')[0] == page);
                if (same >= 0) { e.Handled = true; BeginInvoke(new Action(() => SelectTab(same))); return; }
            }
            var d = e.GetDeferral();
            _ = NewTab(e.Uri, e, d, priv: t.Private); // une fenêtre ouverte depuis un onglet privé reste privée
        };
        // window.close() : comme dans Chrome, seulement pour un onglet ouvert par un script (popup de connexion…)
        core.WindowCloseRequested += (_, _) => { if (t.OpenedByScript) BeginInvoke(new Action(() => CloseTab(tabs.IndexOf(t)))); };

        // Cadres publicitaires (iframes) bloqués eux aussi
        core.FrameNavigationStarting += (_, e) =>
        {
            if (adblock && !t.AdsAllowed && Uri.TryCreate(e.Uri, UriKind.Absolute, out var fu) && IsBlocked(fu.Host)) { e.Cancel = true; Counted(t); }
        };

        // Bloqueur de pubs YouTube (le script ne s'active que sur youtube.com)
        if (YouTubeBlocking)
        {
            try { t.YtScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeAdblock.Script); } catch { }
        }

        // Téléchargements : on suit chaque fichier et on prévient clairement en cas de souci
        core.DownloadStarting += (_, e) =>
        {
            WatchDownload(e.DownloadOperation);
            smokeDownload?.TrySetResult(e.DownloadOperation);
            if (!downloadAsk || SmokeReport != null) return;
            // « Demander où enregistrer » : choix du dossier et du nom, hors de l'évènement WebView2
            var d = e.GetDeferral();
            BeginInvoke(new Action(() =>
            {
                try
                {
                    using var dlg = new SaveFileDialog
                    {
                        FileName = Path.GetFileName(e.ResultFilePath),
                        InitialDirectory = Path.GetDirectoryName(e.ResultFilePath),
                        Title = "Enregistrer le fichier",
                        OverwritePrompt = true,
                    };
                    if (dlg.ShowDialog(this) == DialogResult.OK) { e.ResultFilePath = dlg.FileName; e.Handled = true; }
                    else e.Cancel = true;
                }
                catch { }
                finally { d.Complete(); }
            }));
        };

        core.NavigationStarting += (_, e) =>
        {
            // nouvelle page : compteur du bouclier remis à zéro, exception éventuelle du site appliquée
            if (!e.IsRedirected) t.Blocked = 0;
            t.AdsAllowed = SiteList.Contains(adblockAllow, e.Uri);
            if (t == Cur) UpdateShield();
            t.Loading = true;
            if (!spin.Enabled) spin.Start();
            strip.Invalidate();
            if (t == Cur) UpdateUi();
        };
        core.NavigationCompleted += (_, e) =>
        {
            t.Loading = false;
            strip.Invalidate();
            // onglet privé ou historique désactivé : rien n'est noté
            if (e.IsSuccess && historySave && !t.Private && core.Source.StartsWith("http") && !IsLocalPage(core.Source))
                AddHistory(core.DocumentTitle, core.Source);
            if (core.Source.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase)) _ = RefreshTwitchAuth();
            if (t == Cur) UpdateUi();
        };
        core.SourceChanged += (_, _) =>
        {
            t.IsHome = IsHome(core.Source);
            t.LocalGlyph = IsSettings(core.Source) ? Glyphs.Settings : null;
            strip.Invalidate();
            if (t == Cur) UpdateUi();
        };
        core.HistoryChanged += (_, _) => { if (t == Cur) UpdateUi(); };
        core.DocumentTitleChanged += (_, _) =>
        {
            t.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? core.Source : core.DocumentTitle;
            strip.Invalidate();
            if (t == Cur) UpdateUi();
        };
        core.FaviconChanged += async (_, _) =>
        {
            var old = t.Icon; t.Icon = null; old?.Dispose();
            try
            {
                using var st = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                if (st != null)
                {
                    using var ms = new MemoryStream();
                    await st.CopyToAsync(ms);
                    if (ms.Length > 0)
                    {
                        ms.Position = 0;
                        using var img = Image.FromStream(ms);
                        t.Icon = new Bitmap(img);
                        if (!t.Private) SaveSiteIcon(core.Source, ms.ToArray());
                    }
                }
            }
            catch { }
            strip.Invalidate();
        };
        // Lecture sans clic pour le multistream seulement : sans « action utilisateur » sur la page (ouverture par
        // raccourci, alerte, reprise de session), le moteur bloque les lecteurs et les pubs restent figées. L'action est
        // déléguée aux lecteurs par allow="autoplay" ; les autres sites gardent le blocage habituel.
        core.DOMContentLoaded += async (_, _) =>
        {
            if (!IsNurLivePage(core.Source) && !core.Source.StartsWith(MultiUrl, StringComparison.OrdinalIgnoreCase)) return;
            try { await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":\"1\",\"userGesture\":true}"); }
            catch (Exception ex) { CrashLog.Write("Lecture automatique", ex); }
        };
        // Lecteurs Twitch / Kick / YouTube de la page : suivis pour détecter une pub figée (Nur Live)
        core.FrameCreated += (_, e) =>
        {
            var f = e.Frame;
            f.NavigationStarting += (_, n) =>
            {
                if (NurLive.PlayerKind(n.Uri) != "") t.PlayerFrames[f] = n.Uri; else t.PlayerFrames.Remove(f);
            };
            f.Destroyed += (_, _) => t.PlayerFrames.Remove(f);
        };
        // Plantage du moteur web : avant, tous les onglets devenaient blancs sans explication
        core.ProcessFailed += (_, e) => BeginInvoke(new Action(() => OnProcessFailed(t, e.ProcessFailedKind, e.Reason, e.ExitCode)));
        core.IsDocumentPlayingAudioChanged += (_, _) => { t.Audio = core.IsDocumentPlayingAudio; strip.Invalidate(); };
        core.IsMutedChanged += (_, _) => { t.Muted = core.IsMuted; strip.Invalidate(); };
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            pageFullscreen = core.ContainsFullScreenElement;
            SetFullscreen(core.ContainsFullScreenElement);
        };

        // Messages des pages intégrées (accueil, multistream)
        core.WebMessageReceived += (_, e) =>
        {
            if (!IsLocalPage(e.Source)) return;
            string json = e.WebMessageAsJson;
            // Hors de l'évènement WebView2 : on peut y ouvrir des fenêtres sans bloquer la page
            BeginInvoke(new Action(() => HandlePageMessage(core, t, json)));
        };

        if (forceDark) { try { await ApplyForceDark(t); } catch { } }
        if (popup != null)
        {
            try { popup.NewWindow = core; } catch { core.Navigate(popup.Uri); }
            deferral?.Complete();
        }
        else core.Navigate(string.IsNullOrEmpty(url) ? HomeUrl : url);
        if (t == Cur) UpdateUi();

        // Les extensions vivent dans le profil normal, jamais dans le profil privé
        if (!extStarted && !t.Private)
        {
            extStarted = true;
            profile = core.Profile;
            extTask = LoadExtensionsAsync();
        }
    }

    void SelectTab(int i)
    {
        if (i < 0 || i >= tabs.Count) return;
        sel = i;
        addrEdited = false; // changement d'onglet : on affiche l'adresse du nouvel onglet
        for (int k = 0; k < tabs.Count; k++)
        {
            var t = tabs[k];
            var core = t.View.CoreWebView2;
            if (k == i)
            {
                t.View.Visible = true;
                StopSuspend(t);
                if (t.Lazy && env != null) _ = StartTab(t, t.PendingUrl); // onglet restauré : chargé au premier affichage
                if (core != null)
                {
                    core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                    if (core.IsSuspended) core.Resume();
                }
            }
            else
            {
                t.View.Visible = false;
                if (core != null) core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; // RAM réduite tout de suite
                if (core != null && t.SuspendTimer == null && tabSleepSec > 0)
                {
                    // puis gel complet après le délai choisi dans les réglages
                    var timer = new System.Windows.Forms.Timer { Interval = tabSleepSec * 1000 };
                    timer.Tick += async (_, _) =>
                    {
                        StopSuspend(t);
                        try { await core.TrySuspendAsync(); } catch { /* ex : vidéo ou musique en cours */ }
                    };
                    t.SuspendTimer = timer;
                    timer.Start();
                }
            }
        }
        strip.Selected = i;
        strip.Invalidate();
        UpdateUi();
        if (pageFullscreen && Core?.ContainsFullScreenElement != true) { pageFullscreen = false; SetFullscreen(false); }
        if (Cur.IsHome) addr.Box.Focus(); else Cur.View.Focus();
    }

    static void StopSuspend(BrowserTab t)
    {
        if (t.SuspendTimer == null) return;
        t.SuspendTimer.Stop();
        t.SuspendTimer.Dispose();
        t.SuspendTimer = null;
    }

    void CloseTab(int i)
    {
        if (i < 0 || i >= tabs.Count) return;
        var t = tabs[i];
        if (SmokeReport != null || ShotsDir != null)
            CrashLog.Write($"CloseTab {i}/{tabs.Count} {t.View.CoreWebView2?.Source} script={t.OpenedByScript}", new Exception(Environment.StackTrace));
        var src = t.View.CoreWebView2?.Source ?? t.PendingUrl;
        if (!t.Private && !string.IsNullOrEmpty(src) && !IsLocalPage(src)) closedTabs.Add(src);
        StopSuspend(t);
        tabs.RemoveAt(i);
        content.Controls.Remove(t.View);
        t.View.Dispose(); // libère réellement les processus du moteur
        t.Icon?.Dispose();
        if (tabs.Count == 0) { Close(); return; }
        // fenêtre ouverte par un site (connexion, extension…) : on revient à l'onglet qui l'a ouverte
        if (i == sel && t.Opener != null && tabs.Contains(t.Opener)) { SelectTab(tabs.IndexOf(t.Opener)); return; }
        if (i < sel) sel--;
        SelectTab(Math.Min(sel, tabs.Count - 1));
    }

    void ReopenClosed()
    {
        if (closedTabs.Count == 0) return;
        var u = closedTabs[^1];
        closedTabs.RemoveAt(closedTabs.Count - 1);
        _ = NewTab(u);
    }

    bool engineRestarting;

    void OnProcessFailed(BrowserTab t, CoreWebView2ProcessFailedKind kind, CoreWebView2ProcessFailedReason reason, int exitCode)
    {
        CrashLog.Write("Moteur web", new Exception($"{kind} ({reason}, code {exitCode}) · onglet : {t.Title}"));
        if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // tout le moteur est tombé : on garde les onglets et on relance le navigateur proprement
            if (engineRestarting || SmokeReport != null || ShotsDir != null) return;
            engineRestarting = true;
            SaveSession();
            Notify("LiteBrowser redémarre", "Le moteur web de Windows s'est arrêté. Tes onglets vont être rouverts.");
            Program.RestartRequested = true;
            closingDone = true; // pas d'effacement des données pendant ce redémarrage
            Close();
        }
        else if (kind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            // un seul onglet planté : on le recharge (une fois), comme « Aïe, Snap ! » mais sans clic
            try { if (tabs.Contains(t)) t.View.CoreWebView2?.Reload(); } catch { }
        }
    }

    /// Épingle / détache un onglet : les épinglés restent groupés à gauche et sont rouverts au démarrage.
    void TogglePin(BrowserTab t)
    {
        int i = tabs.IndexOf(t);
        if (i < 0) return;
        var current = Cur;
        tabs.RemoveAt(i);
        t.Pinned = !t.Pinned;
        int pinned = tabs.TakeWhile(x => x.Pinned).Count();
        tabs.Insert(pinned, t); // fin du groupe épinglé (ou juste après lui en le détachant)
        SelectTab(tabs.IndexOf(current));
    }

    /// Premier onglet normal démarré (cookies, extensions, réglages du profil) : jamais un onglet privé.
    CoreWebView2 MainCore() => tabs.Where(t => !t.Private).Select(t => t.View.CoreWebView2).FirstOrDefault(x => x != null);

    /// Profils distincts en cours (normal et privé) : les réglages s'appliquent aux deux.
    IEnumerable<CoreWebView2Profile> Profiles() =>
        tabs.Select(t => t.View.CoreWebView2?.Profile).Where(p => p != null).GroupBy(p => p.ProfileName + p.IsInPrivateModeEnabled).Select(g => g.First());

    CoreWebView2TrackingPreventionLevel TrackingLevel() => trackingLevel switch
    {
        "strict" => CoreWebView2TrackingPreventionLevel.Strict,
        "balanced" => CoreWebView2TrackingPreventionLevel.Balanced,
        _ => CoreWebView2TrackingPreventionLevel.Basic,
    };

    bool YouTubeBlocking => adblock && !SiteList.Contains(adblockAllow, "https://www.youtube.com/");

    // ---------- Bouclier (pubs bloquées sur la page) ----------

    void Counted(BrowserTab t)
    {
        blockedCount++;
        t.Blocked++;
        if (t == Cur) UpdateShield();
    }

    void UpdateShield()
    {
        var t = Cur;
        var src = Core?.Source ?? "";
        bool web = t != null && !t.IsHome && (src.StartsWith("http://") || src.StartsWith("https://")) && !IsLocalPage(src);
        addr.Shield.Visible = web;
        if (!web) { addr.PerformLayout(); return; }
        bool on = adblock && !t.AdsAllowed;
        addr.Shield.Dim = !on;
        addr.Shield.Badge = on && t.Blocked > 0 ? (t.Blocked > 99 ? "99+" : t.Blocked.ToString()) : null;
        tip.SetToolTip(addr.Shield, !adblock ? "Bloqueur de pubs coupé"
            : !on ? $"Bloqueur coupé sur {SiteList.Host(src)}"
            : $"Bloqueur actif : {t.Blocked} élément{(t.Blocked > 1 ? "s" : "")} bloqué{(t.Blocked > 1 ? "s" : "")} sur cette page");
        addr.PerformLayout();
    }

    void ShowShieldMenu()
    {
        var t = Cur;
        var host = SiteList.Host(Core?.Source);
        if (t == null || host == null) return;
        var m = NewMenu(out var r, out var font);
        m.Items.Add(new ToolStripMenuItem(!adblock ? "Bloqueur de pubs coupé partout"
            : t.AdsAllowed ? $"Bloqueur coupé sur {host}"
            : $"{t.Blocked} pub{(t.Blocked > 1 ? "s" : "")} ou traqueur{(t.Blocked > 1 ? "s" : "")} bloqué{(t.Blocked > 1 ? "s" : "")} sur cette page") { Enabled = false });
        m.Items.Add(new ToolStripSeparator());
        if (adblock)
        {
            bool allowed = UrlTools.HostMatches(host, adblockAllow);
            m.Items.Add(MI(allowed ? $"Réactiver le bloqueur sur {host}" : $"Couper le bloqueur sur {host}", () =>
            {
                // on retire aussi un domaine parent éventuel (ex. www.twitch.tv couvert par twitch.tv)
                if (allowed) adblockAllow.RemoveWhere(x => UrlTools.HostMatches(host, new HashSet<string> { x }));
                else adblockAllow.Add(host);
                SaveSettings();
                _ = ApplyYouTubeAdblock();
                Core?.Reload();
                RefreshSettingsPages();
            }));
        }
        m.Items.Add(MI("Bloquer les pubs sur tous les sites", () => SetAdblock(!adblock), null, adblock));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem($"Total depuis le lancement : {blockedCount}") { Enabled = false });
        m.Items.Add(MI("Réglages de confidentialité…", () => OpenSettings("confidentialite")));
        StyleDropDowns(m.Items, r, font);
        m.Show(addr.Shield, new Point(addr.Shield.Width, addr.Shield.Height + S(6)), ToolStripDropDownDirection.BelowLeft);
    }

    void SetAdblock(bool on)
    {
        adblock = on;
        SaveSettings();
        _ = ApplyYouTubeAdblock();
        foreach (var t in tabs) t.AdsAllowed = SiteList.Contains(adblockAllow, t.View.CoreWebView2?.Source);
        UpdateShield();
        RefreshSettingsPages();
    }

    void SaveSiteIcon(string pageUrl, byte[] png)
    {
        try
        {
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var u) || !u.Scheme.StartsWith("http") || png.Length > 200_000) return;
            var file = Path.Combine(dataDir, "icones", u.Host + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllBytes(file, png);
        }
        catch { }
    }

    // ---------- Page Réglages ----------

    static bool IsSettings(string url) => url != null && url.StartsWith(SettingsUrl, StringComparison.OrdinalIgnoreCase);

    // Adresses lisibles des pages intégrées, comme chrome://settings : affichées dans la barre et tapables
    static readonly (string Alias, string Url)[] LocalPages =
    {
        ("litebrowser://historique", SettingsUrl + "#historique"),
        ("litebrowser://favoris", SettingsUrl + "#favoris"),
        ("litebrowser://reglages", SettingsUrl),
        ("litebrowser://accueil", HomeUrl),
        ("litebrowser://multistream", MultiUrl),
    };

    /// Adresse interne → alias lisible (null si ce n'est pas une page intégrée connue).
    static string LocalAlias(string url)
    {
        if (!IsSettings(url) && !(url ?? "").StartsWith(MultiUrl, StringComparison.OrdinalIgnoreCase)) return null;
        var hash = url.Contains('#') ? url[(url.IndexOf('#') + 1)..] : "";
        if (url.StartsWith(MultiUrl, StringComparison.OrdinalIgnoreCase)) return "litebrowser://multistream";
        return hash is "historique" or "favoris" ? "litebrowser://" + hash : "litebrowser://reglages" + (hash.Length > 0 ? "#" + hash : "");
    }

    /// Alias tapé dans la barre d'adresse → vraie adresse (null si ce n'en est pas un).
    static string FromAlias(string text)
    {
        text = (text ?? "").Trim().ToLowerInvariant().Replace("réglages", "reglages");
        if (!text.StartsWith("litebrowser://")) return null;
        var (path, hash) = text.Contains('#') ? (text[..text.IndexOf('#')], text[text.IndexOf('#')..]) : (text, "");
        var hit = LocalPages.FirstOrDefault(p => p.Alias == path.TrimEnd('/'));
        return hit.Url == null ? SettingsUrl : hit.Url + (hit.Url == SettingsUrl ? hash : "");
    }

    void OpenSettings(string section = null)
    {
        var url = SettingsUrl + (section != null ? "#" + section : "");
        int i = tabs.FindIndex(x => IsSettings(x.View.CoreWebView2?.Source));
        if (i >= 0) { SelectTab(i); tabs[i].View.CoreWebView2?.Navigate(url); }
        else _ = NewTab(url);
    }

    void RefreshSettingsPages()
    {
        foreach (var t in tabs)
        {
            var c = t.View.CoreWebView2;
            if (c != null && IsSettings(c.Source)) SendSettingsData(c);
        }
    }

    async Task ClearBrowsingData()
    {
        history.Clear();
        SaveList(histPath, history);
        suggestions.Clear();
        suggestSet.Clear();
        foreach (var f in favorites) AddSuggestion(f.Url);
        closedTabs.Clear();
        var c = MainCore();
        try { if (c != null) await c.Profile.ClearBrowsingDataAsync(); } catch { }
    }

    void SendSettingsData(CoreWebView2 core)
    {
        string dlDefault = null;
        try { dlDefault = MainCore()?.Profile.DefaultDownloadFolderPath; } catch { }
        var json = JsonSerializer.Serialize(new
        {
            type = "settings",
            theme = themeName,
            version = Updater.Current.ToString(),
            webview = env?.BrowserVersionString,
            updateReady,
            dev = AppPaths.Dev,
            engine = engineId,
            engines = Engines.Select(e => new { id = e.Id, name = e.Name }).ToArray(),
            startup = startupMode,
            favbar = showFavBar,
            forceDark,
            adblock,
            blockedCount,
            allow = adblockAllow.OrderBy(x => x).ToArray(),
            tracking = trackingLevel,
            tabSleep = tabSleepSec,
            historySave,
            historyCount = history.Count,
            clearOnExit,
            downloadAsk,
            downloadDir = downloadDir ?? dlDefault ?? "",
            downloadCustom = downloadDir != null,
            prayer = new
            {
                source = prayerSource,
                hasMosque = !string.IsNullOrEmpty(prayerMosque),
                mosqueName = prayerMosqueName ?? prayerMosque,
                city = prayerCity,
                auto = prayerAuto,
                method = prayerMethod,
                methods = Prayers.Methods.Select(m => new { id = m.Id, name = m.Name }).ToArray(),
                notify = prayerNotify,
                reminder = prayerReminder,
                offsets = PrayerAdjust.ParseOffsets(prayerOffsets),
                today = HomePrayers(),
            },
            m8 = new
            {
                notify = m8Notify,
                reminder = m8Reminder,
                liveNotify,
                games = M8.Pages.Select(p => new { id = p.Game, label = p.Label, on = !m8Games.TryGetValue(p.Game, out var on) || on }).ToArray(),
            },
            favorites = favorites.Select(f => new { title = f.Title, url = f.Url }).ToArray(),
            extensions = extensions.Select(e => new { name = e.Name, id = e.StoreId, enabled = e.Enabled, error = e.Error }).ToArray(),
            extBusy,
        });
        try { core.PostWebMessageAsJson(json); } catch { }
    }

    void SendHistory(CoreWebView2 core, string q)
    {
        q = (q ?? "").Trim();
        var items = new List<object>();
        for (int i = history.Count - 1; i >= 0 && items.Count < 300; i--)
        {
            var h = history[i];
            if (q.Length > 0 && h.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && h.Url.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            items.Add(new { title = h.Title, url = h.Url });
        }
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "history", q, total = history.Count, items })); } catch { }
    }

    /// Un réglage modifié depuis la page Réglages.
    void ApplySetting(string key, JsonElement v)
    {
        bool B() => v.ValueKind == JsonValueKind.True;
        int I(int def) => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : def;
        string S() => v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (key)
        {
            case "theme": SetTheme(S()); break;
            case "engine": SetEngine(S()); break;
            case "startup": startupMode = S() == "home" ? "home" : "session"; break;
            case "favbar": if (B() != showFavBar) ToggleFavBar(); break;
            case "forceDark": if (B() != forceDark) ToggleForceDark(); break;
            case "adblock": SetAdblock(B()); return;
            case "tracking":
                trackingLevel = S() is "balanced" or "strict" ? S() : "basic";
                foreach (var p in Profiles()) try { p.PreferredTrackingPreventionLevel = TrackingLevel(); } catch { }
                break;
            case "tabSleep": tabSleepSec = Math.Clamp(I(30), 0, 3600); break;
            case "historySave": historySave = B(); break;
            case "clearOnExit": clearOnExit = B(); break;
            case "downloadAsk": downloadAsk = B(); break;
            case "prayerNotify": prayerNotify = B(); break;
            case "prayerReminder": prayerReminder = Math.Clamp(I(0), 0, 60); break;
            case "prayerMethod":
                if (Prayers.Methods.Any(m => m.Id == I(-1))) { prayerMethod = I(12); _ = RefreshPrayers(); }
                break;
            case "prayerSource":
                prayerSource = S() == "mosque" && !string.IsNullOrEmpty(prayerMosque) ? "mosque" : "calc";
                _ = RefreshPrayers();
                break;
            case "m8Notify": m8Notify = B(); SaveM8Settings(); SyncNlFromHost(); break;
            case "m8Reminder": m8Reminder = Math.Clamp(I(15), 0, 120); SaveM8Settings(); SyncNlFromHost(); break;
            case "liveNotify": liveNotify = B(); SaveM8Settings(); SyncNlFromHost(); break;
            case "m8Game":
                if (S() is string g && M8.Pages.Any(p => p.Game == g)) { m8Games[g] = m8Games.TryGetValue(g, out var on) && !on; SaveM8Settings(); SyncNlFromHost(); }
                break;
            default: return;
        }
        SaveSettings();
        RefreshLocalPages();
        RefreshSettingsPages();
    }

    /// Un bouton de la page Réglages.
    async void SettingsAction(string name, string arg)
    {
        switch (name)
        {
            case "clearData": ClearData(); break;
            case "clearHistory":
                history.Clear();
                SaveList(histPath, history);
                suggestions.Clear(); suggestSet.Clear();
                foreach (var f in favorites) AddSuggestion(f.Url);
                break;
            case "checkUpdates": await CheckUpdates(silent: false); break;
            case "chooseDownloadDir":
                using (var dlg = new FolderBrowserDialog { Description = "Dossier des téléchargements", UseDescriptionForTitle = true, SelectedPath = downloadDir ?? "" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    downloadDir = dlg.SelectedPath;
                }
                foreach (var p in Profiles()) try { p.DefaultDownloadFolderPath = downloadDir; } catch { }
                SaveSettings();
                break;
            case "resetDownloadDir":
                downloadDir = null;
                SaveSettings();
                Notify("Téléchargements", "Le dossier par défaut de Windows sera utilisé au prochain démarrage.");
                break;
            case "openDownloads":
                try { Process.Start("explorer.exe", "\"" + (downloadDir ?? MainCore()?.Profile.DefaultDownloadFolderPath ?? "") + "\""); } catch { }
                return;
            case "prayerCity": ChangeCity(); return;
            case "prayerMosque": ChooseMosque(); return;
            case "prayerAdjust": AdjustPrayers(); break;
            case "prayerLocate": prayerAuto = true; SaveSettings(); await RefreshPrayers(relocate: true); break;
            case "openDataDir": try { Process.Start("explorer.exe", "\"" + dataDir + "\""); } catch { } return;
            case "allowRemove":
                if (arg != null && adblockAllow.Remove(arg)) { SaveSettings(); _ = ApplyYouTubeAdblock(); }
                break;
            case "addExtension": AddExtensionPrompt(); return;
            case "extToggle": if (extensions.FirstOrDefault(e => e.StoreId == arg) is ExtInfo et) await ToggleExtension(et); break;
            case "extRemove": if (extensions.FirstOrDefault(e => e.StoreId == arg) is ExtInfo er) await RemoveExtension(er); break;
            case "extReinstall": if (extensions.FirstOrDefault(e => e.StoreId == arg) is ExtInfo ei) await ReinstallExtension(ei); break;
            case "testNotify": Notify("LiteBrowser", "Les notifications Windows fonctionnent : tu seras prévenu pour les prières et les matchs."); return;
            default: return;
        }
        RefreshSettingsPages();
    }

    // ---------- Affichage ----------

    bool addrEdited, settingAddress;
    FavBar favBar;
    bool showFavBar = true;
    readonly Dictionary<string, Image> favIcons = new();
    readonly HashSet<string> favIconLoading = new();

    // ---------- Barre de favoris ----------

    void RefreshFavBar()
    {
        if (favBar == null) return;
        favBar.Items = favorites.ToList();
        favBar.Invalidate();
    }

    void ToggleFavBar()
    {
        showFavBar = !showFavBar;
        favBar.Visible = showFavBar && !fullscreen;
        SaveSettings();
    }

    /// Icône d'un favori : celle du site si on l'a déjà, sinon téléchargée une fois puis gardée sur le disque.
    Image FavIcon(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Scheme.StartsWith("http")) return null;
        var host = u.Host;
        if (favIcons.TryGetValue(host, out var img)) return img;
        if (!favIconLoading.Add(host)) return null;
        var file = Path.Combine(dataDir, "icones", host + ".png");
        if (File.Exists(file))
        {
            try { using var tmp = Image.FromFile(file); favIcons[host] = new Bitmap(tmp); return favIcons[host]; } catch { }
        }
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = Net.Client(TimeSpan.FromSeconds(10));
                var bytes = await http.GetByteArrayAsync("https://www.google.com/s2/favicons?sz=32&domain=" + Uri.EscapeDataString(host));
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                await File.WriteAllBytesAsync(file, bytes);
                using var ms = new MemoryStream(bytes);
                using var tmp = Image.FromStream(ms);
                var bmp = new Bitmap(tmp);
                BeginInvoke(new Action(() => { favIcons[host] = bmp; favBar.Invalidate(); }));
            }
            catch { }
        });
        return null;
    }

    ContextMenuStrip NewMenu(out MenuRenderer r, out Font font)
    {
        lastMenu?.Dispose();
        r = new MenuRenderer(theme, iconSmall);
        font = new Font("Segoe UI", 9.5f);
        var m = new ContextMenuStrip { Renderer = r, Font = font, ShowItemToolTips = true };
        lastMenu = m;
        return m;
    }

    void ShowFavMenu(int i, Control anchor, Point pt)
    {
        if (i < 0 || i >= favorites.Count) return;
        var f = favorites[i];
        var m = NewMenu(out _, out _);
        m.Items.Add(MI("Ouvrir", () => Core?.Navigate(f.Url)));
        m.Items.Add(MI("Ouvrir dans un nouvel onglet", () => _ = NewTab(f.Url)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("Renommer…", () =>
        {
            var name = Prompt.Show(this, "Renommer le favori", "Nouveau nom :", theme, "Renommer", f.Title);
            if (string.IsNullOrWhiteSpace(name)) return;
            int k = favorites.FindIndex(x => x.Url == f.Url);
            if (k >= 0) { favorites[k] = (Clean(name), f.Url); SaveList(favPath, favorites); RefreshFavBar(); RefreshHomeTabs(); }
        }));
        m.Items.Add(MI("Déplacer à gauche", () => MoveFav(f.Url, -1)));
        m.Items.Add(MI("Déplacer à droite", () => MoveFav(f.Url, 1)));
        m.Items.Add(MI("Supprimer", () =>
        {
            favorites.RemoveAll(x => x.Url == f.Url);
            SaveList(favPath, favorites); RefreshFavBar(); UpdateUi(); RefreshHomeTabs();
        }));
        m.Show(anchor, pt);
    }

    void MoveFav(string url, int dir)
    {
        int k = favorites.FindIndex(x => x.Url == url), j = k + dir;
        if (k < 0 || j < 0 || j >= favorites.Count) return;
        (favorites[k], favorites[j]) = (favorites[j], favorites[k]);
        SaveList(favPath, favorites); RefreshFavBar(); RefreshHomeTabs();
    }

    void ShowFavOverflow(int first, Point pt)
    {
        if (first < 0) return;
        var m = NewMenu(out _, out _);
        m.ImageScalingSize = new Size(S(16), S(16));
        for (int i = first; i < favorites.Count; i++)
        {
            var f = favorites[i];
            var it = LinkItem(f);
            it.Image = FavIcon(f.Url);
            m.Items.Add(it);
        }
        m.Show(favBar, pt, ToolStripDropDownDirection.BelowLeft);
    }

    // ---------- Son et menu des onglets ----------

    void ToggleMute(int i)
    {
        if (i < 0 || i >= tabs.Count) return;
        var c = tabs[i].View.CoreWebView2;
        if (c == null) return;
        c.IsMuted = !c.IsMuted;
        tabs[i].Muted = c.IsMuted;
        strip.Invalidate();
    }

    void ShowTabMenu(int i, Point pt)
    {
        if (i < 0 || i >= tabs.Count) return;
        var t = tabs[i];
        var src = t.View.CoreWebView2?.Source;
        var m = NewMenu(out _, out _);
        m.Items.Add(MI("Nouvel onglet", () => _ = NewTab(), "Ctrl+T"));
        m.Items.Add(MI("Nouvel onglet privé", () => _ = NewTab(priv: true), "Ctrl+Maj+N"));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("Actualiser", () => t.View.CoreWebView2?.Reload(), "F5"));
        m.Items.Add(MI("Dupliquer", () => { if (src != null) _ = NewTab(IsLocalPage(src) ? null : src, priv: t.Private); }));
        if (!t.Private) m.Items.Add(MI(t.Pinned ? "Détacher l'onglet" : "Épingler l'onglet", () => TogglePin(t)));
        if (!t.Private && src != null && src.StartsWith("http") && !IsLocalPage(src))
            m.Items.Add(MI("Rouvrir en onglet privé", () => _ = NewTab(src, priv: true)));
        m.Items.Add(MI(t.Muted ? "Réactiver le son de l'onglet" : "Couper le son de l'onglet", () => ToggleMute(tabs.IndexOf(t)), "Ctrl+M"));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("Fermer", () => CloseTab(tabs.IndexOf(t)), "Ctrl+W"));
        var others = MI("Fermer les autres onglets", () =>
        {
            foreach (var x in tabs.Where(x => x != t).ToList()) CloseTab(tabs.IndexOf(x));
        });
        others.Enabled = tabs.Count > 1;
        m.Items.Add(others);
        var right = MI("Fermer les onglets à droite", () =>
        {
            int k = tabs.IndexOf(t);
            foreach (var x in tabs.Skip(k + 1).ToList()) CloseTab(tabs.IndexOf(x));
        });
        right.Enabled = i < tabs.Count - 1;
        m.Items.Add(right);
        m.Show(strip, pt);
    }

    void SetAddress(string text)
    {
        if (addr.Box.Text == text) return;
        settingAddress = true;
        addr.Box.Text = text;
        settingAddress = false;
        if (addr.Box.Focused) addr.Box.SelectAll();
    }

    void UpdateUi()
    {
        var t = Cur;
        var c = Core;
        string src = c?.Source ?? "";
        bool home = t == null || t.IsHome || src == "" || src == "about:blank";

        // L'adresse suit toujours l'onglet affiché, sauf si tu es en train de taper quelque chose
        if (!addrEdited) SetAddress(home ? "" : LocalAlias(src) ?? src);
        bool local = IsLocalPage(src);
        addr.BadgeGlyph = home ? Glyphs.Search : local ? (IsSettings(src) ? Glyphs.Settings : Glyphs.Lock)
            : src.StartsWith("https://") ? Glyphs.Lock : Glyphs.Warning;
        tip.SetToolTip(addr, home ? "" : local ? "Page intégrée à LiteBrowser" : src.StartsWith("https://") ? "Connexion sécurisée" : "Connexion non sécurisée");

        bool fav = !home && favorites.Exists(f => f.Url == src);
        addr.Star.Visible = !home && !local;
        addr.Star.Glyph = fav ? Glyphs.StarFill : Glyphs.Star;
        addr.Star.Highlight = fav;
        addr.Star.Invalidate();

        backBtn.Enabled = c?.CanGoBack == true;
        fwdBtn.Enabled = c?.CanGoForward == true;
        reloadBtn.Glyph = t?.Loading == true ? Glyphs.Stop : Glyphs.Refresh;
        tip.SetToolTip(reloadBtn, t?.Loading == true ? "Arrêter le chargement" : "Actualiser (F5)");

        Text = (ShotsDir != null ? "TEST – ne pas fermer (2 min) – " : "") +
            (t == null ? "LiteBrowser" : t.Title + (t.Private ? " – LiteBrowser (privé)" : " – LiteBrowser"));
        UpdateShield();
    }

    void UpdatePlaceholder() => addr.Box.PlaceholderText = $"Rechercher avec {Engine.Name} ou saisir une adresse";

    void ApplyTheme()
    {
        theme = themeName == "light" ? Theme.Light : themeName == "dark" ? Theme.Dark : Theme.M8;
        BackColor = theme.Strip;
        strip.Theme = theme;
        favBar.Theme = theme;
        favBar.Invalidate();
        strip.Invalidate();
        toolbar.BackColor = theme.Bar;
        content.BackColor = theme.Bar;
        foreach (var b in new[] { backBtn, fwdBtn, reloadBtn, homeBtn, menuBtn, extBtn })
        {
            b.Theme = theme;
            b.BackColor = theme.Bar;
            b.Invalidate();
        }
        updatePill.Theme = theme;
        updatePill.BackColor = theme.Bar;
        updatePill.Invalidate();
        prayerChip.Theme = theme;
        prayerChip.BackColor = theme.Bar;
        prayerChip.Invalidate();
        addr.ApplyTheme(theme, theme.Bar);
        toolbar.Invalidate();
        SetTitleBarDark(dark);

        foreach (var t in tabs)
        {
            t.View.DefaultBackgroundColor = theme.Bar;
            var c = t.View.CoreWebView2;
            if (c != null && IsHome(c.Source)) SendHomeData(c);
        }
        ApplyScheme();
    }

    CoreWebView2PreferredColorScheme Scheme() =>
        forceDark ? CoreWebView2PreferredColorScheme.Light
        : dark ? CoreWebView2PreferredColorScheme.Dark
        : CoreWebView2PreferredColorScheme.Auto;

    void ApplyScheme()
    {
        foreach (var p in Profiles()) p.PreferredColorScheme = Scheme();
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void SetTitleBarDark(bool on)
    {
        if (!IsHandleCreated) return;
        int v = on ? 1 : 0;
        try { DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)); } catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetTitleBarDark(dark);
    }

    void SetFullscreen(bool on)
    {
        if (on == fullscreen) return;
        fullscreen = on;
        SuspendLayout();
        strip.Visible = toolbar.Visible = !on;
        favBar.Visible = !on && showFavBar;
        if (on)
        {
            prevState = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
            if (prevState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
        }
        ResumeLayout();
    }

    // ---------- Page d'accueil ----------

    object HomePrayers()
    {
        bool mosque = prayerSource == "mosque" && !string.IsNullOrEmpty(prayerMosque);
        var methodName = mosque ? "Horaires de la mosquée (Mawaqit)" : Prayers.Methods.FirstOrDefault(m => m.Id == prayerMethod).Name;
        if (PrayerAdjust.ParseOffsets(prayerOffsets).Count > 0) methodName += " · ajustés";
        if (prayerDay == null)
            return new { city = mosque ? prayerMosqueName : prayerCity, error = prayerError, methodName, jumua = (string)null, times = Array.Empty<object>(),
                notify = prayerNotify, reminder = prayerReminder };
        return new
        {
            city = prayerDay.City,
            hijri = prayerDay.Hijri,
            methodName,
            jumua = prayerDay.Jumua,
            notify = prayerNotify,
            reminder = prayerReminder,
            times = prayerDay.Times.Select(t => new { key = t.Key, name = t.Name, time = t.Time.ToString(@"hh\:mm") }).ToArray()
        };
    }

    object HomeM8()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var list = m8.Current(now).Where(m => m.Status != "finished" && (!m8Games.TryGetValue(m.Game, out var on) || on)).ToList();
        return new
        {
            loading = m8.Loading,
            updated = m8.FetchedAtMs > 0 ? m8.FetchedAtMs : (long?)null,
            source = m8.Source,
            notify = m8Notify,
            reminder = m8Reminder,
            games = M8.Pages.Select(p => new { id = p.Game, label = p.Label, @short = p.Short, on = !m8Games.TryGetValue(p.Game, out var on) || on }).ToArray(),
            matches = list.Select(m => new
            {
                id = m.Id, game = m.Game, gameLabel = M8.Info(m.Game).Label, gameShort = M8.Info(m.Game).Short,
                ts = m.Ts, status = m.Status, opp = m.Opponent, oppShort = m.OpponentShort,
                tournament = m.Tournament, stage = m.Stage, score = m.Score, url = m.Url, muted = matchMutes.Contains(m.Id), timeUnknown = m.TimeUnknown,
            }).ToArray(),
            tournaments = m8.Tournaments.Where(t => !m8Games.TryGetValue(t.Game, out var on) || on)
                .Select(t => new { game = M8.Info(t.Game).Label, name = t.Name, when = t.When, url = t.Url }).ToArray(),
        };
    }

    object ChannelData(Channel c)
    {
        liveStatus.TryGetValue(c.Id, out var st);
        return new
        {
            id = c.Id, platform = c.Platform, key = c.Key, label = c.Label, fav = c.Fav, notify = c.Notify,
            live = st?.Live, name = string.IsNullOrEmpty(st?.Name) || st.Name == c.Key ? c.Label : st.Name,
            avatar = st?.Avatar, title = st?.Title, game = st?.Game, viewers = st?.Viewers ?? 0,
            url = Channels.PageUrl(c),
        };
    }

    void SendHomeData(CoreWebView2 core)
    {
        var tiles = favorites.Select(f => new { title = f.Title, url = f.Url })
            .Concat(DefaultTiles.Where(d => !favorites.Exists(f => f.Url == d.Url)).Select(d => new { title = d.Title, url = d.Url }))
            .Take(8).ToArray();
        var json = JsonSerializer.Serialize(new
        {
            theme = themeName,
            engine = engineId,
            engines = Engines.Select(e => new { id = e.Id, name = e.Name }).ToArray(),
            tiles,
            m8 = HomeM8(),
            channels = channels.Select(ChannelData).ToArray(),
            prayers = HomePrayers(),
        });
        try { core.PostWebMessageAsJson(json); } catch { }
    }

    void RefreshHomeTabs() => RefreshLocalPages();

    /// Renvoie leurs données à toutes les pages intégrées ouvertes (accueil, multistream).
    void RefreshLocalPages()
    {
        foreach (var t in tabs)
        {
            var c = t.View.CoreWebView2;
            if (c == null) continue;
            if (IsHome(c.Source)) SendHomeData(c);
            else if (IsSettings(c.Source)) SendSettingsData(c);
            else if (c.Source.StartsWith(MultiUrl, StringComparison.OrdinalIgnoreCase)) SendMultiData(c);
            else if (IsNurLivePage(c.Source)) SendNlData(c);
        }
    }

    // ---------- Messages des pages intégrées ----------

    void HandlePageMessage(CoreWebView2 core, BrowserTab t, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool isHome = IsHome(core.Source);
            switch (Str("type"))
            {
                case "nlInit": SendNlInit(core); break;
                case "nlSave":
                    if (root.TryGetProperty("state", out var nlst)) OnNlSave(nlst.GetRawText());
                    break;
                case "nlRpc": _ = HandleNlRpc(core, root.GetProperty("id").GetInt32(), Str("method"),
                    root.TryGetProperty("args", out var nargs) ? nargs.Clone() : default); break;
                case "ready":
                    if (isHome) { SendHomeData(core); homeReady.TrySetResult(true); }
                    else if (IsSettings(core.Source)) SendSettingsData(core);
                    else { SendMultiData(core); multiReady?.TrySetResult(true); }
                    break;
                case "openSettings": OpenSettings(Str("section")); break;
                // ---- page Réglages ----
                case "set" when IsSettings(core.Source):
                    ApplySetting(Str("key"), root.TryGetProperty("value", out var sv) ? sv.Clone() : default);
                    break;
                case "action" when IsSettings(core.Source):
                    SettingsAction(Str("name"), Str("arg"));
                    break;
                case "history" when IsSettings(core.Source):
                    SendHistory(core, Str("q"));
                    break;
                case "historyDelete" when IsSettings(core.Source):
                    var hu = Str("url");
                    if (hu != null && history.RemoveAll(h => h.Url == hu) > 0) SaveList(histPath, history);
                    SendHistory(core, Str("q"));
                    break;
                case "favRemove" when IsSettings(core.Source):
                    favorites.RemoveAll(f => f.Url == Str("url"));
                    SaveList(favPath, favorites); RefreshFavBar(); UpdateUi(); RefreshLocalPages(); RefreshSettingsPages();
                    break;
                case "favRename" when IsSettings(core.Source):
                    int fk = favorites.FindIndex(f => f.Url == Str("url"));
                    if (fk >= 0 && !string.IsNullOrWhiteSpace(Str("title")))
                    {
                        favorites[fk] = (Clean(Str("title")), favorites[fk].Url);
                        SaveList(favPath, favorites); RefreshFavBar(); RefreshLocalPages(); RefreshSettingsPages();
                    }
                    break;
                case "favMove" when IsSettings(core.Source):
                    MoveFav(Str("url"), root.TryGetProperty("dir", out var fd) && fd.TryGetInt32(out var fdi) ? Math.Sign(fdi) : 0);
                    RefreshSettingsPages();
                    break;
                case "search":
                    var u = ToUrl(Str("q"));
                    if (u != null) { core.Navigate(u); t.View.Focus(); }
                    break;
                case "engine": SetEngine(Str("id")); break;
                case "city": ShowPrayerMenu(Cursor.Position); break;
                case "refreshMatches": _ = RefreshMatches(force: true); break;
                case "watchMatch": _ = WatchMatch(Str("id")); break;
                case "muteMatch":
                    var mid = Str("id");
                    if (mid != null && !matchMutes.Remove(mid)) matchMutes.Add(mid);
                    SaveM8Settings(); SyncNlFromHost(); RefreshLocalPages();
                    break;
                case "m8Settings":
                    if (root.TryGetProperty("reminder", out var rm) && rm.TryGetInt32(out var rmi)) m8Reminder = Math.Clamp(rmi, 0, 120);
                    if (root.TryGetProperty("notify", out var nt)) m8Notify = nt.ValueKind == JsonValueKind.True;
                    if (Str("toggleGame") is string g) m8Games[g] = m8Games.TryGetValue(g, out var on) && !on;
                    SaveM8Settings(); SyncNlFromHost(); RefreshLocalPages();
                    break;
                case "watch":
                    var ids = root.TryGetProperty("ids", out var arr) && arr.ValueKind == JsonValueKind.Array
                        ? arr.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).ToList() : new List<string>();
                    OpenMulti(ids, chat: true);
                    break;
                case "addChannel":
                    var ch = Channels.Parse(Str("input"));
                    if (ch == null)
                    {
                        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "error", text = "Chaîne non reconnue : tape un nom Twitch, ou colle un lien Twitch / Kick / YouTube." })); } catch { }
                        break;
                    }
                    var existing = channels.FirstOrDefault(c => c.Id == ch.Id);
                    if (existing == null) { ch.Fav = root.TryGetProperty("fav", out var fv) && fv.ValueKind == JsonValueKind.True; channels.Add(ch); existing = ch; }
                    else if (root.TryGetProperty("fav", out var fv2) && fv2.ValueKind == JsonValueKind.True) existing.Fav = true;
                    SaveChannels(); SyncNlFromHost();
                    try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "added", id = existing.Id })); } catch { }
                    _ = RefreshStatus();
                    break;
                case "searchChannels":
                    var sq = Str("q");
                    _ = Task.Run(async () =>
                    {
                        var hits = await ChannelSearch.SearchAsync(sq);
                        BeginInvoke(() =>
                        {
                            try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "channelHits", q = sq, hits })); } catch { }
                        });
                    });
                    break;
                case "removeChannel": channels.RemoveAll(c => c.Id == Str("id")); SaveChannels(); SyncNlFromHost(); RefreshLocalPages(); break;
                case "toggleFav":
                    if (channels.FirstOrDefault(c => c.Id == Str("id")) is Channel cf) { cf.Fav = !cf.Fav; SaveChannels(); SyncNlFromHost(); RefreshLocalPages(); }
                    break;
                case "toggleNotify":
                    if (channels.FirstOrDefault(c => c.Id == Str("id")) is Channel cn) { cn.Notify = !cn.Notify; SaveChannels(); SyncNlFromHost(); RefreshLocalPages(); }
                    break;
                case "multiIds":
                    // Chaînes affichées par un onglet multistream : on suit aussi leur statut
                    if (root.TryGetProperty("ids", out var mi) && mi.ValueKind == JsonValueKind.Array)
                    {
                        bool added = false;
                        foreach (var x in mi.EnumerateArray()) if (x.GetString() is string id && multiIds.Add(id)) added = true;
                        if (added) _ = RefreshStatus();
                    }
                    break;
                case "openUrl":
                    var link = Str("url");
                    if (link != null && link.StartsWith("http")) _ = NewTab(link);
                    break;
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    // ---------- Nur Live dans le navigateur ----------

    string nurLiveUi, nurBridge, nlStatePath;
    JsonNode nlState;
    List<string> pendingNlWatch;
    object twitchAuth = new { loggedIn = false, user = "" };
    string twitchAuthKey = "";

    void PrepareNurLive()
    {
        try
        {
            settings.TryGetValue("nurlive.path", out var configured);
            var src = NurLive.FindFolder(NurLive.Candidates(configured));
            if (src == null) return;
            var dst = Path.Combine(dataDir, "nurlive");
            NurLive.Prepare(src, dst);
            using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("nurbridge.js");
            using var rd = new StreamReader(res);
            nurBridge = NurLive.Bridge(rd.ReadToEnd(),
                File.ReadAllText(Path.Combine(src, "core", "sources.js")), File.ReadAllText(Path.Combine(src, "core", "model.js")));
            nurLiveUi = dst;
            LoadNlState();
        }
        catch (Exception ex) { nurLiveUi = null; CrashLog.Write("Nur Live", ex); }
    }

    void LoadNlState()
    {
        nlStatePath = Path.Combine(dataDir, "nurlive-state.json");
        try
        {
            if (File.Exists(nlStatePath)) nlState = JsonNode.Parse(File.ReadAllText(nlStatePath));
            else
            {
                // Première fois : on reprend la configuration de l'appli Nur Live (chaînes, plateaux, volumes…)
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                foreach (var dir in new[] { "nur-live", "Nur Live" })
                {
                    var f = Path.Combine(appData, dir, "nur-live-v3.json");
                    if (File.Exists(f)) { nlState = JsonNode.Parse(File.ReadAllText(f)); break; }
                }
            }
        }
        catch { nlState = null; }
        if (nlState is not JsonObject) { nlState = new JsonObject(); SyncNlFromHost(push: false); }
        else SyncHostFromNl();
    }

    void SaveNlState()
    {
        try { File.WriteAllText(nlStatePath, nlState.ToJsonString()); } catch { }
    }

    /// L'état sauvé par Nur Live met à jour le navigateur : chaînes, alertes, matchs en sourdine.
    void OnNlSave(string json)
    {
        try { nlState = JsonNode.Parse(json); } catch { return; }
        SaveNlState();
        int before = channels.Count;
        SyncHostFromNl();
        RefreshHomeTabs();
        if (channels.Count != before) _ = RefreshStatus();
    }

    void SyncHostFromNl()
    {
        if (nlState is not JsonObject st) return;
        if (st["sources"] is JsonArray arr)
        {
            var list = new List<Channel>();
            foreach (var n in arr.OfType<JsonObject>())
            {
                var platform = (string)n["platform"] ?? "twitch";
                var key = (string)n["key"];
                if (string.IsNullOrEmpty(key) || list.Any(c => c.Platform == platform && c.Key == key)) continue;
                list.Add(new Channel
                {
                    Platform = platform, Key = key, Label = (string)n["label"] ?? key,
                    Fav = n["fav"] is JsonValue fv && fv.TryGetValue<bool>(out var fb) && fb,
                    Notify = n["notify"] is JsonValue nv && nv.TryGetValue<bool>(out var nb) && nb,
                });
            }
            if (list.Count > 0) { channels = list; SaveChannels(); }
        }
        if (st["settings"] is JsonObject s)
        {
            if (s["notifyMatches"] is JsonValue a && a.TryGetValue<bool>(out var av)) m8Notify = av;
            if (s["reminderMin"] is JsonValue r && r.TryGetValue<int>(out var rv)) m8Reminder = rv;
            if (s["notifyFavLive"] is JsonValue l && l.TryGetValue<bool>(out var lv)) liveNotify = lv;
            if (s["games"] is JsonObject g)
                foreach (var kv in g) if (kv.Value is JsonValue gv && gv.TryGetValue<bool>(out var on)) m8Games[kv.Key] = on;
        }
        if (st["matchMute"] is JsonObject mm)
        {
            matchMutes.Clear();
            foreach (var kv in mm) if (kv.Value is JsonValue v && v.TryGetValue<bool>(out var on) && on) matchMutes.Add(kv.Key);
        }
        SaveM8Settings();
    }

    /// Une modification faite depuis l'accueil (chaînes, alertes…) est reportée dans Nur Live.
    void SyncNlFromHost(bool push = true)
    {
        if (nlState is not JsonObject st) return;
        var old = (st["sources"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var arr = new JsonArray();
        foreach (var c in channels)
        {
            var prev = old.FirstOrDefault(o => (string)o["platform"] == c.Platform && (string)o["key"] == c.Key);
            var o = prev != null ? (JsonObject)JsonNode.Parse(prev.ToJsonString()) : new JsonObject { ["platform"] = c.Platform, ["key"] = c.Key };
            o["label"] = c.Label ?? c.Key;
            if (c.Fav) o["fav"] = true; else o.Remove("fav");
            if (c.Notify) o["notify"] = true; else o.Remove("notify");
            arr.Add(o);
        }
        st["sources"] = arr;
        var s = st["settings"] as JsonObject ?? new JsonObject();
        s["notifyMatches"] = m8Notify;
        s["reminderMin"] = m8Reminder;
        s["notifyFavLive"] = liveNotify;
        var g = s["games"] as JsonObject ?? new JsonObject();
        foreach (var p in M8.Pages) g[p.Game] = !m8Games.TryGetValue(p.Game, out var on) || on;
        s["games"] = g;
        st["settings"] = s;
        var mm = new JsonObject();
        foreach (var id in matchMutes) mm[id] = true;
        st["matchMute"] = mm;
        SaveNlState();
        if (!push) return;
        foreach (var t in tabs)
        {
            var c = t.View.CoreWebView2;
            if (c != null && IsNurLivePage(c.Source))
                try { c.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "nlData", state = st })); } catch { }
        }
    }

    object NlStatus() => liveStatus.ToDictionary(kv => kv.Key, kv => (object)new
    {
        live = kv.Value.Live, exists = true, name = kv.Value.Name, avatar = kv.Value.Avatar,
        title = kv.Value.Title, game = kv.Value.Game, viewers = kv.Value.Viewers,
    });

    Dictionary<string, object> NlData()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new Dictionary<string, object>
        {
            ["status"] = NlStatus(),
            ["statusAt"] = now,
            ["matches"] = m8.Current(now).Where(m => !m.TimeUnknown).ToList(),
            ["tournaments"] = M8.UndatedAsTournaments(m8.Current(now)).Concat(m8.Tournaments).ToList(),
            ["events"] = Array.Empty<object>(),
            ["matchesAt"] = m8.FetchedAtMs,
            ["matchErrors"] = m8.Errors.Select(e => { var p = e.Split(" : ", 2); return new { game = p[0], error = p.Length > 1 ? p[1] : e }; }).ToArray(),
            ["matchesBusy"] = m8.Loading,
            ["games"] = M8.Pages.Select(p => new { game = p.Game, label = p.Label, @short = p.Short }).ToArray(),
            ["auth"] = twitchAuth,
            ["pinned"] = TopMost,
            ["fullscreen"] = fullscreen,
        };
    }

    void SendNlInit(CoreWebView2 core)
    {
        var d = NlData();
        d["type"] = "nlInit";
        d["state"] = nlState;
        if (pendingNlWatch != null) { d["watch"] = new { ids = pendingNlWatch, chat = true }; pendingNlWatch = null; }
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(d)); } catch { }
        _ = RefreshTwitchAuth();
    }

    void SendNlData(CoreWebView2 core)
    {
        var d = NlData();
        d["type"] = "nlData";
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(d)); } catch { }
    }

    async Task HandleNlRpc(CoreWebView2 core, int id, string method, JsonElement args)
    {
        object result = true;
        string Arg(int i) => args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > i && args[i].ValueKind == JsonValueKind.String ? args[i].GetString() : null;
        try
        {
            switch (method)
            {
                case "refresh": await RefreshStatus(); break;
                case "matches":
                    await RefreshMatches(force: !(args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.False));
                    break;
                case "pickMatch":
                    var picked = await PickMatchIds(Arg(0));
                    result = new
                    {
                        ids = picked.Ids,
                        toast = new
                        {
                            title = picked.CoStream ? "Co-stream M8" : "Diffusion officielle",
                            body = picked.CoStream ? "En live : " + string.Join(", ", picked.Ids.Select(x => x.Split(':')[1])) : "Aucune chaîne M8 en live pour l'instant.",
                        },
                    };
                    break;
                case "search":
                    result = await ChannelSearch.SearchAsync(Arg(0), channels.Where(c => c.Platform == "twitch").Select(c => c.Key));
                    break;
                case "probe":
                    var resume = new HashSet<string>();
                    if (args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.Array)
                        foreach (var x in args[0].EnumerateArray()) if (x.GetString() is string rid) resume.Add(rid);
                    result = await ProbePlayers(core, resume);
                    break;
                case "log": NlLog(Arg(0)); break;
                case "pickEvent": result = null; break;
                case "testAlert": Notify("LiteBrowser : test d'alerte", "Les alertes Windows fonctionnent. Tu seras prévenu avant chaque match M8."); break;
                case "open": if (Arg(0) is string u && u.StartsWith("http")) _ = NewTab(u); break;
                case "pin": TopMost = !TopMost; result = TopMost; break;
                case "fullscreen":
                    bool want = args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? args[0].GetBoolean() : !fullscreen;
                    SetFullscreen(want);
                    result = fullscreen;
                    break;
                case "openLogs": try { Process.Start("explorer.exe", "\"" + dataDir + "\""); } catch { } break;
                case "twitchLogin":
                    _ = NewTab("https://www.twitch.tv/login");
                    result = twitchAuth;
                    break;
                case "twitchLogout":
                    await TwitchLogout();
                    result = twitchAuth;
                    break;
            }
        }
        catch (Exception ex) { CrashLog.Write("nlRpc " + method, ex); result = null; }
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "nlRpcResult", id, result })); } catch { }
    }

    /// État de chaque lecteur de la page ; relance douce de ceux demandés. Un lecteur muet au-delà de 1,5 s est signalé injoignable.
    async Task<List<JsonNode>> ProbePlayers(CoreWebView2 core, HashSet<string> resume)
    {
        var t = tabs.FirstOrDefault(x => x.View.CoreWebView2 == core);
        if (t == null) return new List<JsonNode>();
        var all = await Task.WhenAll(t.PlayerFrames.ToList().Select(async kv =>
        {
            var kind = NurLive.PlayerKind(kv.Value);
            var id = NurLive.ChannelOf(kind, kv.Value);
            JsonObject o = null;
            try
            {
                var run = kv.Key.ExecuteScriptAsync(NurLive.ProbeScript(resume.Contains(id)));
                if (await Task.WhenAny(run, Task.Delay(1500)) == run) o = JsonNode.Parse(await run) as JsonObject;
            }
            catch { }
            o ??= new JsonObject { ["video"] = false, ["unreachable"] = true };
            o["kind"] = kind;
            o["id"] = id;
            return (JsonNode)o;
        }));
        return all.ToList();
    }

    /// Journal de Nur Live dans le navigateur (relances, bilans) : nurlive.log dans le dossier des données.
    void NlLog(string line)
    {
        try
        {
            var f = Path.Combine(dataDir, "nurlive.log");
            if (File.Exists(f) && new FileInfo(f).Length > 500_000) File.Move(f, f + ".old", true);
            File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
        }
        catch { }
    }

    /// Compte Twitch : lu dans les cookies du navigateur (connexion faite sur twitch.tv dans un onglet).
    async Task RefreshTwitchAuth()
    {
        var c = MainCore();
        if (c == null) return;
        try
        {
            var cookies = await c.CookieManager.GetCookiesAsync("https://www.twitch.tv");
            bool logged = cookies.Any(x => x.Name == "auth-token" && !string.IsNullOrEmpty(x.Value));
            var user = logged ? Uri.UnescapeDataString(cookies.FirstOrDefault(x => x.Name == "login")?.Value ?? "") : "";
            var key = logged + ":" + user;
            if (key == twitchAuthKey) return;
            bool first = twitchAuthKey == "";
            twitchAuthKey = key;
            twitchAuth = new { loggedIn = logged, user };
            if (first) return;
            foreach (var t in tabs)
            {
                var core = t.View.CoreWebView2;
                if (core != null && IsNurLivePage(core.Source))
                    try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "nlAuth", auth = twitchAuth })); } catch { }
            }
            if (logged) Notify("Twitch connecté", $"Connecté en tant que {user} : ton compte est utilisé dans le multistream (chat, abonnements).");
        }
        catch { }
    }

    async Task TwitchLogout()
    {
        var c = MainCore();
        if (c == null) return;
        try
        {
            foreach (var cookie in await c.CookieManager.GetCookiesAsync("https://www.twitch.tv")) c.CookieManager.DeleteCookie(cookie);
            foreach (var cookie in await c.CookieManager.GetCookiesAsync("https://id.twitch.tv")) c.CookieManager.DeleteCookie(cookie);
        }
        catch { }
        await RefreshTwitchAuth();
    }

    // ---------- Réglages M8 et chaînes ----------

    void LoadM8Settings()
    {
        try
        {
            if (File.Exists(m8Path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(m8Path));
                var r = doc.RootElement;
                if (r.TryGetProperty("notify", out var n)) m8Notify = n.ValueKind != JsonValueKind.False;
                if (r.TryGetProperty("liveNotify", out var ln)) liveNotify = ln.ValueKind != JsonValueKind.False;
                if (r.TryGetProperty("reminder", out var rm) && rm.TryGetInt32(out var rmi)) m8Reminder = rmi;
                if (r.TryGetProperty("games", out var g) && g.ValueKind == JsonValueKind.Object)
                    foreach (var p in g.EnumerateObject()) m8Games[p.Name] = p.Value.ValueKind != JsonValueKind.False;
                if (r.TryGetProperty("mutes", out var mu) && mu.ValueKind == JsonValueKind.Array)
                    foreach (var x in mu.EnumerateArray()) if (x.GetString() is string id) matchMutes.Add(id);
            }
            if (File.Exists(firedPath))
                firedAlerts = new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(firedPath)) ?? new());
        }
        catch { }
    }

    void SaveM8Settings()
    {
        try
        {
            File.WriteAllText(m8Path, JsonSerializer.Serialize(new
            {
                notify = m8Notify, liveNotify, reminder = m8Reminder, games = m8Games, mutes = matchMutes.ToArray(),
            }));
        }
        catch { }
    }

    void LoadChannels(string appData)
    {
        try
        {
            if (File.Exists(channelsPath))
            {
                channels = JsonSerializer.Deserialize<List<Channel>>(File.ReadAllText(channelsPath)) ?? new();
                if (channels.Count > 0) return;
            }
            // Premier lancement : on reprend les chaînes de Nur Live si elle est installée
            foreach (var dir in new[] { "nur-live", "Nur Live" })
            {
                var imported = Channels.ReadNurLive(Path.Combine(appData, dir, "nur-live-v3.json"));
                if (imported != null && imported.Count > 0) { channels = imported; SaveChannels(); return; }
            }
        }
        catch { }
        channels = Channels.DefaultList();
        SaveChannels();
    }

    void SaveChannels()
    {
        try { File.WriteAllText(channelsPath, JsonSerializer.Serialize(channels)); } catch { }
    }

    // ---------- Lives : statut en direct + alertes ----------

    async Task RefreshStatus()
    {
        if (statusBusy) return;
        statusBusy = true;
        try
        {
            var all = channels.ToList();
            foreach (var id in multiIds)
            {
                var parts = id.Split(':', 2);
                if (parts.Length == 2 && !all.Any(c => c.Id == id)) all.Add(new Channel { Platform = parts[0], Key = parts[1], Label = parts[1] });
            }
            var next = await LiveStatus.FetchAsync(all);
            var prev = liveStatus;
            // une chaîne au statut inconnu garde l'ancien
            foreach (var kv in prev) if (next.TryGetValue(kv.Key, out var n) && n.Live == null && kv.Value.Live != null) next[kv.Key] = kv.Value;
            liveStatus = next;
            if (liveNotify && prev.Count > 0)
                foreach (var id in LiveStatus.WentLive(prev, next))
                {
                    var c = channels.FirstOrDefault(x => x.Id == id);
                    if (c == null || !c.Notify) continue;
                    var st = next[id];
                    var body = string.Join(" · ", new[] { st.Game, st.Title }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    Notify($"{(string.IsNullOrEmpty(st.Name) ? c.Label : st.Name)} est en live", body.Length > 0 ? body : "Clique pour regarder.",
                        () => OpenMulti(new List<string> { id }, chat: true));
                }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally { statusBusy = false; RefreshLocalPages(); }
    }

    void MatchAlerts(long nowMs)
    {
        if (m8 == null) return;
        var due = M8.DueAlerts(m8.Current(nowMs), m8Notify, m8Reminder, m8Games, matchMutes, firedAlerts, nowMs);
        foreach (var a in due)
        {
            firedAlerts.Add(a.Key);
            var info = M8.Info(a.Match.Game);
            var mins = (int)Math.Max(1, Math.Round((a.Match.Ts * 1000 - nowMs) / 60000.0));
            var title = a.Kind == "start" ? $"M8 vs {a.Match.OpponentShort} : c'est parti !" : $"M8 vs {a.Match.OpponentShort} dans {mins} min";
            var body = $"{info.Label} · {a.Match.Tournament}{(string.IsNullOrEmpty(a.Match.Stage) ? "" : " · " + a.Match.Stage)} — clique pour regarder";
            var id = a.Match.Id;
            Notify(title, body, () => _ = WatchMatch(id));
        }
        if (due.Count > 0)
        {
            try { File.WriteAllText(firedPath, JsonSerializer.Serialize(firedAlerts.TakeLast(300).ToList())); } catch { }
        }
    }

    /// « Regarder » un match : co-stream M8 en live, sinon une chaîne suivie qui en parle, sinon la diffusion officielle.
    async Task WatchMatch(string id)
    {
        var picked = await PickMatchIds(id);
        if (picked.Ids.Count > 0) OpenMulti(picked.Ids, chat: true);
    }

    /// Chaînes à ouvrir pour un match : co-stream M8 en live, sinon une chaîne suivie qui en parle, sinon l'officielle.
    async Task<(List<string> Ids, bool CoStream)> PickMatchIds(string id)
    {
        var m = m8?.Current(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).FirstOrDefault(x => x.Id == id);
        if (m == null) return (new List<string>(), false);
        var official = (m.Streams ?? new()).Where(x => x.Platform == "twitch").Select(x => x.Key)
            .Concat(M8.Broadcast.TryGetValue(m.Game, out var b) ? b : Array.Empty<string>()).ToList();
        M8.CoStreams.TryGetValue(m.Game, out var co);
        var wanted = (co ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant());
        var own = channels.Where(c => c.Platform == "twitch").Select(c => c.Key).ToList();
        var check = wanted.Concat(own).Distinct().Take(40).Select(k => new Channel { Platform = "twitch", Key = k, Label = k }).ToList();
        Dictionary<string, LiveInfo> st;
        try { st = await LiveStatus.FetchAsync(check); } catch { st = new(); }
        foreach (var kv in st) liveStatus[kv.Key] = kv.Value;
        var keys = LiveStatus.PickStreams(m.Game, official, co, own, st);
        bool costream = keys.Any(k => st.TryGetValue("twitch:" + k, out var i) && i.Live == true && !official.Contains(k));
        return (keys.Select(k => "twitch:" + k).ToList(), costream);
    }

    /// Ouvre (ou réutilise) l'onglet multistream avec ces chaînes.
    void OpenMulti(List<string> ids, bool chat)
    {
        ids = (ids ?? new()).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().Take(9).ToList();
        foreach (var id in ids) multiIds.Add(id);
        if (nurLiveUi != null)
        {
            // Multistream de Nur Live, chargé dans le navigateur
            int nl = tabs.FindIndex(x => IsNurLivePage(x.View.CoreWebView2?.Source));
            if (nl >= 0)
            {
                SelectTab(nl);
                if (ids.Count > 0)
                    try { tabs[nl].View.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "nlWatch", ids, chat })); } catch { }
            }
            else
            {
                pendingNlWatch = ids.Count > 0 ? ids : null;
                _ = NewTab(NurLive.Url);
            }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized;
            Activate();
            _ = RefreshStatus();
            return;
        }
        if (ids.Count == 0) { _ = NewTab(MultiUrl); return; }
        var url = MultiUrl + "#c=" + Uri.EscapeDataString(string.Join(",", ids)) + (chat ? "&chat=1" : "");
        int existing = tabs.FindIndex(x => x.View.CoreWebView2?.Source?.StartsWith(MultiUrl, StringComparison.OrdinalIgnoreCase) == true);
        if (existing >= 0)
        {
            SelectTab(existing);
            Core?.Navigate(url);
        }
        else _ = NewTab(url);
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized;
        Activate();
        _ = RefreshStatus();
    }

    void SendMultiData(CoreWebView2 core)
    {
        var known = channels.ToDictionary(c => c.Id);
        foreach (var id in multiIds)
            if (!known.ContainsKey(id) && id.Split(':', 2) is { Length: 2 } p)
                known[id] = new Channel { Platform = p[0], Key = p[1], Label = p[1] };
        var json = JsonSerializer.Serialize(new
        {
            type = "multi",
            theme = themeName,
            parent = HomeHost,
            channels = known.Values.Select(c => new
            {
                id = c.Id, platform = c.Platform, key = c.Key, label = c.Label, fav = c.Fav,
                live = liveStatus.TryGetValue(c.Id, out var st) ? st.Live : null,
                name = st?.Name, avatar = st?.Avatar, title = st?.Title, game = st?.Game, viewers = st?.Viewers ?? 0,
                player = Channels.PlayerUrl(c, HomeHost, true),
                chat = Channels.ChatUrl(c, HomeHost, themeName != "light"),
                page = Channels.PageUrl(c),
            }).ToArray(),
        });
        try { core.PostWebMessageAsJson(json); } catch { }
    }

    /// Le bloqueur YouTube suit le réglage « Bloquer les pubs », y compris dans les onglets déjà ouverts.
    async Task ApplyYouTubeAdblock()
    {
        foreach (var t in tabs.ToArray())
        {
            var c = t.View.CoreWebView2;
            if (c == null) continue;
            try
            {
                if (YouTubeBlocking && t.YtScriptId == null) t.YtScriptId = await c.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeAdblock.Script);
                else if (!YouTubeBlocking && t.YtScriptId != null) { c.RemoveScriptToExecuteOnDocumentCreated(t.YtScriptId); t.YtScriptId = null; }
            }
            catch { }
        }
    }

    // ---------- Recherche / adresses ----------

    string ToUrl(string text) => UrlTools.ToUrl(text, Engine.Url);

    void SetEngine(string id)
    {
        if (!Engines.Any(e => e.Id == id)) return;
        engineId = id;
        SaveSettings();
        UpdatePlaceholder();
        RefreshHomeTabs();
    }

    // ---------- Favoris & historique ----------

    static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    static IEnumerable<(string Title, string Url)> ReadPairs(string path)
    {
        if (!File.Exists(path)) yield break;
        foreach (var l in File.ReadLines(path))
        {
            var i = l.IndexOf('\t');
            if (i > 0) yield return (l[..i], l[(i + 1)..]);
        }
    }

    static void SaveList(string path, List<(string Title, string Url)> list)
    {
        try { File.WriteAllLines(path, list.ConvertAll(x => x.Title + "\t" + x.Url)); } catch { }
    }

    void AddSuggestion(string url)
    {
        var s = UrlTools.ShortForm(url);
        if (s == null || !suggestSet.Add(s)) return;
        // Modifier la liste pendant que la barre d'adresse est utilisée peut figer WinForms : on attend
        if (addr != null && addr.Box.Focused) pendingSuggestions.Add(s); else suggestions.Add(s);
    }

    void AddHistory(string title, string url)
    {
        if (history.Count > 0 && history[^1].Url == url) return;
        var t = Clean(string.IsNullOrWhiteSpace(title) ? url : title);
        history.Add((t, url));
        AddSuggestion(url);
        try { File.AppendAllText(histPath, t + "\t" + url + Environment.NewLine); } catch { }
        if (history.Count > 1000) { history.RemoveRange(0, history.Count - 500); SaveList(histPath, history); }
    }

    void ToggleFavorite()
    {
        var c = Core;
        if (c == null || Cur.IsHome || string.IsNullOrEmpty(c.Source)) return;
        int i = favorites.FindIndex(f => f.Url == c.Source);
        if (i >= 0) favorites.RemoveAt(i);
        else
        {
            favorites.Add((Clean(c.DocumentTitle is { Length: > 0 } t ? t : c.Source), c.Source));
            AddSuggestion(c.Source);
        }
        SaveList(favPath, favorites);
        RefreshFavBar();
        UpdateUi();
        RefreshHomeTabs();
    }

    // ---------- Test automatique de bout en bout (lancé par la CI sur un vrai Windows) ----------

    public static readonly string SmokeReport = Environment.GetEnvironmentVariable("LITEBROWSER_SMOKE");
    readonly TaskCompletionSource<bool> homeReady = new();
    TaskCompletionSource<bool> multiReady;
    TaskCompletionSource<CoreWebView2DownloadOperation> smokeDownload;
    Task extTask;

    static async Task<bool> NavigateAndWait(CoreWebView2 core, string url, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>();
        void Done(object s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += Done;
        core.Navigate(url);
        var first = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        core.NavigationCompleted -= Done;
        return first == tcs.Task && tcs.Task.Result;
    }

    static string JsString(string raw)
    {
        try { return JsonSerializer.Deserialize<string>(raw); } catch { return raw; }
    }


    void SmokeStep(Dictionary<string, object> r, string step)
    {
        r["step"] = step;
        try { File.WriteAllText(SmokeReport + ".partiel.json", JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    async Task RunSmoke()
    {
        var r = new Dictionary<string, object>();
        try
        {
            r["version"] = Updater.Current.ToString();
            r["webview2"] = env?.BrowserVersionString;
            r["homeReady"] = await Task.WhenAny(homeReady.Task, Task.Delay(30_000)) == homeReady.Task;
            CoreWebView2 HomeCore() => tabs.FirstOrDefault(x => x.View.CoreWebView2 != null && IsHome(x.View.CoreWebView2.Source))?.View.CoreWebView2;
            r["tabsAtStart"] = tabs.Select(x => x.View.CoreWebView2?.Source ?? $"(non démarré : {x.Title}, lazy={x.Lazy}, privé={x.Private}, disposé={x.View.IsDisposed}, init={x.View.CoreWebView2 != null})").ToList();
            var home = HomeCore();
            await Task.Delay(1500);
            r["homeTitle"] = JsString(await home.ExecuteScriptAsync("document.title"));
            r["homeTheme"] = JsString(await home.ExecuteScriptAsync("document.body.className"));
            r["screenshotHome"] = await SmokeShot("home");

            SmokeStep(r, "extensions");
            if (extTask != null) await Task.WhenAny(extTask, Task.Delay(150_000));
            r["extensions"] = extensions.Select(e => new { e.Name, e.RuntimeId, e.Popup, e.Error, e.Enabled }).ToList();

            SmokeStep(r, "onglet de test");
            await NewTab("about:blank");
            var core = Core;
            var popups = new List<object>();
            foreach (var e in extensions.Where(x => x.RuntimeId != null && x.Popup != null))
            {
                SmokeStep(r, "popup " + e.Name);
                bool ok = await NavigateAndWait(core, $"chrome-extension://{e.RuntimeId}/{e.Popup}", 20_000);
                await Task.Delay(1500);
                var text = ok ? JsString(await core.ExecuteScriptAsync("(document.body && document.body.innerText || '').slice(0,80)")) : null;
                popups.Add(new { e.Name, ok, text });
            }
            r["popups"] = popups;

            SmokeStep(r, "youtube");
            r["youtubeLoaded"] = await NavigateAndWait(core, "https://www.youtube.com/", 45_000);
            await Task.Delay(3000);
            r["youtubeAdblockActive"] = JsString(await core.ExecuteScriptAsync("String(!!window.__lbYtAdblock)")) == "true";
            r["screenshotYoutube"] = await SmokeShot("youtube");
            r["youtubeAdStyle"] = JsString(await core.ExecuteScriptAsync("String(!!document.getElementById('lb-yt-adblock'))")) == "true";

            // Vrai téléchargement (petit zip) : il doit aller au bout
            SmokeStep(r, "téléchargement");
            smokeDownload = new TaskCompletionSource<CoreWebView2DownloadOperation>();
            core.Navigate("https://github.com/Atlantaas/LiteBrowser/archive/refs/heads/main.zip");
            var started = await Task.WhenAny(smokeDownload.Task, Task.Delay(30_000)) == smokeDownload.Task;
            string dl = started ? "démarré" : "jamais démarré";
            if (started)
            {
                var op = smokeDownload.Task.Result;
                for (int i = 0; i < 60 && op.State == CoreWebView2DownloadState.InProgress; i++) await Task.Delay(500);
                dl = op.State == CoreWebView2DownloadState.Completed ? "terminé" : $"{op.State} ({op.InterruptReason})";
            }
            r["download"] = dl;

            SmokeStep(r, "prières");
            for (int i = 0; i < 40 && prayerDay == null; i++) await Task.Delay(1000);
            r["prayers"] = prayerDay?.Times.Count ?? 0;
            r["prayerCity"] = prayerDay?.City;
            r["prayerError"] = prayerError;
            r["prayerChip"] = prayerChip.Visible ? prayerChip.Text : null;

            home = HomeCore() ?? home;
            r["homePrayerRows"] = JsString(await home.ExecuteScriptAsync("String(document.querySelectorAll('.prow').length)"));

            // Lives : statut des chaînes suivies
            SmokeStep(r, "lives");
            for (int i = 0; i < 30 && liveStatus.Count == 0; i++) await Task.Delay(1000);
            r["lives"] = liveStatus.Select(kv => $"{kv.Key} {(kv.Value.Live == true ? "LIVE" : kv.Value.Live == false ? "off" : "?")}").ToList();
            r["homeLiveCards"] = JsString(await home.ExecuteScriptAsync("String(document.querySelectorAll('.ch').length)"));

            // Matchs M8 (7 jeux) : premier passage sur Liquipedia (31 s entre les pages)
            SmokeStep(r, "matchs");
            for (int i = 0; i < 260 && (m8.Loading || m8.FetchedAtMs == 0); i++) await Task.Delay(1000);
            r["m8Source"] = m8.Source;
            r["m8Errors"] = m8.Errors.ToList();
            r["m8Matches"] = m8.Current(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .Select(m => $"{m.Game} vs {m.OpponentShort} {DateTimeOffset.FromUnixTimeSeconds(m.Ts):u} {m.Status} – {m.Tournament}").ToList();
            r["m8Tournaments"] = m8.Tournaments.Select(t => $"{t.Game}: {t.Name} ({t.When})").ToList();
            // l'accueil était en arrière-plan (gelé pour économiser la RAM) : on y revient avant de compter
            SelectTab(Math.Max(0, tabs.FindIndex(x => x.View.CoreWebView2 != null && IsHome(x.View.CoreWebView2.Source))));
            await Task.Delay(2500);
            home = HomeCore() ?? home;
            r["homeMatchRows"] = JsString(await home.ExecuteScriptAsync("String(document.querySelectorAll('.match').length)"));
            r["screenshotHomeFinal"] = await SmokeShot("home_final");

            // Multistream : 2 chaînes + chat
            SmokeStep(r, "multistream");
            multiReady = new TaskCompletionSource<bool>();
            OpenMulti(new List<string> { "twitch:gentlemates", "twitch:valorant_emea" }, chat: true);
            r["multiReady"] = await Task.WhenAny(multiReady.Task, Task.Delay(30_000)) == multiReady.Task;
            await Task.Delay(8000);
            var mc = Core;
            r["multiTiles"] = JsString(await mc.ExecuteScriptAsync("String(document.querySelectorAll('#stage .tile').length)"));
            r["multiPlayers"] = JsString(await mc.ExecuteScriptAsync("String(document.querySelectorAll('#stage iframe').length)"));
            r["multiChat"] = JsString(await mc.ExecuteScriptAsync("String(document.querySelectorAll('#chat iframe').length)"));
            r["screenshotMulti"] = await SmokeShot("multi");
        }
        catch (Exception ex) { r["exception"] = ex.ToString(); }

        try { File.WriteAllText(SmokeReport, JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        Close();
    }

    // ---------- Captures d'écran automatiques (vérification visuelle en développement) ----------

    public static readonly string ShotsDir = Environment.GetEnvironmentVariable("LITEBROWSER_SHOTS");

    async Task RunShots()
    {
        try
        {
            Directory.CreateDirectory(ShotsDir);
            await Task.WhenAny(homeReady.Task, Task.Delay(20_000));
            await Task.Delay(2500);
            await ShotAsync("1_demarrage");
            ShotState("1_demarrage");
            // pages à capturer : nom=adresse ; « prive: » devant l'adresse = onglet privé ; « menu » = menu ☰ ouvert
            foreach (var page in (Environment.GetEnvironmentVariable("LITEBROWSER_SHOTS_PAGES") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = page.Split('=', 2);
                if (parts.Length != 2) continue;
                if (parts[1] == "menu") { ShowMenu(); await Task.Delay(800); await ShotAsync(parts[0]); lastMenu?.Close(); continue; }
                if (parts[1] == "bouclier") { ShowShieldMenu(); await Task.Delay(800); await ShotAsync(parts[0]); lastMenu?.Close(); continue; }
                if (parts[1] == "autotest") { await SelfTest(parts[0]); continue; }
                if (parts[1] == "pubfigee") { await StuckAdTest(parts[0]); continue; }
                if (parts[1] == "matchs")
                {
                    // vrai passage sur Liquipedia (31 s entre les pages), puis accueil et liste des matchs
                    await RefreshMatches(force: true);
                    for (int i = 0; i < 300 && m8.Loading; i++) await Task.Delay(1000);
                    SelectTab(Math.Max(0, tabs.FindIndex(x => IsHome(x.View.CoreWebView2?.Source))));
                    await Task.Delay(2500);
                    await ShotAsync(parts[0]);
                    File.WriteAllLines(Path.Combine(ShotsDir, parts[0] + ".txt"), m8.Current(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        .Select(m => $"{m.Game} vs {m.OpponentShort} {DateTimeOffset.FromUnixTimeSeconds(m.Ts):u} {m.Status} inconnue={m.TimeUnknown} – {m.Tournament} / {m.Stage}"));
                    continue;
                }
                bool priv = parts[1].StartsWith("prive:");
                await NewTab(priv ? parts[1][6..] : parts[1], priv: priv);
                await Task.Delay(7000);
                await ShotAsync(parts[0]);
                ShotState(parts[0]);
            }
        }
        catch (Exception ex) { CrashLog.Write("Captures", ex); }
        Close();
    }

    /// Ouvre de vrais lives dans Nur Live, simule une pub figée dans un lecteur et vérifie qu'il est relancé.
    async Task StuckAdTest(string name)
    {
        TopMost = true; // une fenêtre masquée ne rafraîchit plus son affichage et le lecteur Twitch ne démarre pas : test visible
        Text = "TEST – ne pas fermer";
        var r = new List<string>();
        void Check(string what, bool ok, string detail = "") => r.Add((ok ? "OK    " : "ÉCHEC ") + what + (detail.Length > 0 ? " · " + detail : ""));
        for (int i = 0; i < 40 && !liveStatus.Values.Any(v => v.Live == true); i++) await Task.Delay(1000);
        var ids = liveStatus.Where(kv => kv.Value.Live == true && kv.Key.StartsWith("twitch:")).Select(kv => kv.Key).Take(2).ToList();
        // aucune chaîne suivie en live : grosses chaînes presque toujours en direct
        if (ids.Count == 0)
        {
            var big = new[] { "slash_ftv", "otplol_", "kamet0", "valorant", "zerator", "riotgames" }.Select(k => new Channel { Platform = "twitch", Key = k, Label = k }).ToList();
            ids = (await LiveStatus.FetchAsync(big)).Where(kv => kv.Value.Live == true).Select(kv => kv.Key).Take(2).ToList();
        }
        Check("au moins un live Twitch en cours", ids.Count > 0, string.Join(", ", ids));
        if (ids.Count > 0)
        {
            var log = Path.Combine(dataDir, "nurlive.log");
            try { File.Delete(log); } catch { }
            OpenMulti(ids, chat: false);
            await Task.Delay(25_000);
            var c = Core;
            var probes = await ProbePlayers(c, new HashSet<string>());
            File.WriteAllText(Path.Combine(ShotsDir, name + "_sondes.json"), string.Join("\n", probes.Select(p => p.ToJsonString())));
            Check("la sonde lit les lecteurs", probes.Count >= ids.Count && probes.All(p => p["unreachable"] == null), probes.Count + " lecteur(s)");
            // Démarrage automatique : le lecteur doit avancer sans clic, et un play() avec le son ne doit pas être refusé
            Check("les lives démarrent seuls", probes.All(p => p["paused"]?.GetValue<bool>() == false && p["ready"]?.GetValue<int>() >= 2), string.Join(" | ", probes.Select(p => $"{p["id"]} pause={p["paused"]} prêt={p["ready"]}")));
            foreach (var kv in Cur.PlayerFrames.ToList())
                await kv.Key.ExecuteScriptAsync("(() => { const v = document.querySelector('video'); window.__t = 'attente'; if (!v) { window.__t = 'pas de vidéo'; return; } v.muted = false; v.play().then(() => window.__t = 'accepté', (e) => window.__t = e.name + ' : ' + e.message); })()");
            await Task.Delay(4000);
            foreach (var kv in Cur.PlayerFrames.ToList())
                r.Add("INFO  play() avec le son " + NurLive.ChannelOf("twitch", kv.Value) + " : " +
                    await kv.Key.ExecuteScriptAsync("window.__t + ' · visible=' + document.visibilityState + ' · taille=' + innerWidth + 'x' + innerHeight"));
            r.Add("INFO  action utilisateur page=" + await c.ExecuteScriptAsync("navigator.userActivation.hasBeenActive") + " lecteurs=" +
                string.Join(",", await Task.WhenAll(Cur.PlayerFrames.Keys.ToList().Select(f => f.ExecuteScriptAsync("navigator.userActivation.hasBeenActive")))));
            await ShotAsync(name + "_lives");
            var target = Cur.PlayerFrames.First(kv => ids.Contains(NurLive.ChannelOf("twitch", kv.Value)));
            var id = NurLive.ChannelOf("twitch", target.Value);
            await target.Key.ExecuteScriptAsync("(() => { const d = document.createElement('div'); d.setAttribute('data-a-target', 'video-ad-label'); document.body.append(d);" +
                "setInterval(() => document.querySelectorAll('video').forEach((v) => v.pause()), 100); })()");
            for (int i = 0; i < 60 && !(File.Exists(log) && File.ReadAllText(log).Contains($"relance {id} : publicité figée")); i++) await Task.Delay(1000);
            var text = File.Exists(log) ? File.ReadAllText(log) : "";
            Check("pub figée simulée : lecteur relancé", text.Contains($"relance {id} : publicité figée"), id);
            await Task.Delay(20_000);
            var after = (await ProbePlayers(Core, new HashSet<string>())).FirstOrDefault(p => (string)p["id"] == id);
            Check("le lecteur repart après la relance", after != null && after["paused"]?.GetValue<bool>() == false, after?.ToJsonString() ?? "lecteur absent");
            // Une vraie pub peut suivre la relance : elle doit défiler, pas rester figée
            var t0 = after?["tAll"]?.GetValue<double>() ?? 0;
            await Task.Delay(10_000);
            var later = (await ProbePlayers(Core, new HashSet<string>())).FirstOrDefault(p => (string)p["id"] == id);
            var t1 = later?["tAll"]?.GetValue<double>() ?? 0;
            Check("la vidéo avance (pub comprise)", t1 != t0, $"{t0:0.0} s → {t1:0.0} s, pub={later?["ad"]}");
            await ShotAsync(name + "_apres");
        }
        File.WriteAllLines(Path.Combine(ShotsDir, name + ".txt"), r);
    }

    /// Clique réellement dans la page Réglages et vérifie que le navigateur applique chaque réglage.
    async Task SelfTest(string name)
    {
        var r = new List<string>();
        void Check(string what, bool ok) => r.Add((ok ? "OK    " : "ÉCHEC ") + what);
        OpenSettings();
        await Task.Delay(4000);
        var c = Core;
        async Task Js(string code) { await c.ExecuteScriptAsync(code); await Task.Delay(700); }
        string Sel(string sel, string v) => $"(function(){{var e=document.querySelector('{sel}');e.value='{v}';e.dispatchEvent(new Event('change',{{bubbles:true}}));}})()";

        var before = (themeName, prayerReminder, trackingLevel, tabSleepSec, historySave, startupMode);
        await Js("document.querySelector('.th[data-v=light]').click()");
        Check("thème Clair appliqué au navigateur", themeName == "light" && theme == Theme.Light);
        await ShotAsync(name + "_theme_clair");
        await Js("document.querySelector('.th[data-v=dark]').click()");
        Check("thème Sombre appliqué", themeName == "dark");
        await Js("document.querySelector('.th[data-v=m8]').click()");
        Check("retour au thème Gentle Mates", themeName == "m8" && theme == Theme.M8);
        await Js(Sel("[data-set=prayerReminder]", "10"));
        Check("rappel 10 min avant la prière", prayerReminder == 10);
        await Js("document.querySelector('#tracking [data-v=strict]').click()");
        Check("protection contre le pistage stricte", trackingLevel == "strict" && MainCore()?.Profile.PreferredTrackingPreventionLevel == CoreWebView2TrackingPreventionLevel.Strict);
        await Js(Sel("[data-set=tabSleep]", "300"));
        Check("veille des onglets à 5 min", tabSleepSec == 300);
        await Js("(function(){var e=document.querySelector('[data-set=historySave]');e.checked=false;e.dispatchEvent(new Event('change',{bubbles:true}));})()");
        Check("historique désactivé", !historySave);
        await Js("document.querySelector('#startup [data-v=home]').click()");
        Check("démarrage sur la page d'accueil", startupMode == "home");
        var saved = File.ReadAllText(settingsPath);
        Check("réglages écrits dans reglages.txt", saved.Contains("prayer.reminder=10") && saved.Contains("tracking=strict") && saved.Contains("tabs.sleep=300"));
        adblockAllow.Add("example.com"); SaveSettings(); RefreshSettingsPages(); await Task.Delay(700);
        await Js("document.querySelector('[data-host=\"example.com\"]').click()");
        Check("site retiré des exceptions du bloqueur", !adblockAllow.Contains("example.com"));
        Check("alias litebrowser://historique", FromAlias("litebrowser://historique") == SettingsUrl + "#historique" && FromAlias("litebrowser://réglages") == SettingsUrl);

        // remise des réglages d'origine
        (themeName, prayerReminder, trackingLevel, tabSleepSec, historySave, startupMode) = before;
        SaveSettings(); ApplyTheme(); RefreshLocalPages();
        foreach (var p in Profiles()) p.PreferredTrackingPreventionLevel = TrackingLevel();
        File.WriteAllLines(Path.Combine(ShotsDir, name + ".txt"), r);
    }

    /// État des onglets à côté des captures, pour vérifier ce qui est réellement ouvert.
    void ShotState(string name)
    {
        try
        {
            var st = new
            {
                selected = sel,
                tabs = tabs.Select(t => new { t.Title, url = t.View.CoreWebView2?.Source ?? t.PendingUrl, t.Pinned, t.Private, t.Lazy, t.Blocked, t.AdsAllowed }),
                blockedCount,
            };
            File.WriteAllText(Path.Combine(ShotsDir, name + ".json"), JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// Capture sans voler le focus ni lire l'écran : la fenêtre est dessinée en mémoire, puis la page web par-dessus.
    /// Fonctionne même si une autre fenêtre (jeu, etc.) est au premier plan.

    async Task<string> SmokeShot(string name)
    {
        var file = Path.Combine(Path.GetDirectoryName(SmokeReport) ?? ".", $"smoke_{name}.png");
        await ShotAsync(name, file);
        return File.Exists(file) ? file : "échec de la capture";
    }

    async Task ShotAsync(string name, string file = null)
    {
        try
        {
            using var bmp = new Bitmap(Width, Height); // DrawToBitmap dessine toute la fenêtre, barre de titre comprise
            DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
            var c = Core;
            if (c != null && Cur.View.Visible)
            {
                using var ms = new MemoryStream();
                await c.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                ms.Position = 0;
                using var page = Image.FromStream(ms);
                var cs = content.PointToScreen(Point.Empty); var at = new Point(cs.X - Left, cs.Y - Top);
                using var g = Graphics.FromImage(bmp);
                g.DrawImage(page, new Rectangle(at, content.ClientSize));
            }
            // menus ouverts : ce sont des fenêtres à part, dessinées à leur place
            if (lastMenu is { Visible: true } m)
            {
                using var mb = new Bitmap(m.Width, m.Height);
                m.DrawToBitmap(mb, new Rectangle(Point.Empty, m.Size));
                using var g = Graphics.FromImage(bmp);
                g.DrawImage(mb, new Point(m.Left - Left, m.Top - Top));
            }
            bmp.Save(file ?? Path.Combine(ShotsDir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { CrashLog.Write("Capture " + name, ex); }
    }

    // En mode captures, la fenêtre s'ouvre sans prendre le focus (on ne dérange pas ce qui est en cours sur le PC)
    protected override bool ShowWithoutActivation => ShotsDir != null || SmokeReport != null;

    // ---------- Notifications Windows ----------

    void Notify(string title, string text, Action onClick = null)
    {
        try
        {
            if (tray == null)
            {
                tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "LiteBrowser", Visible = true };
                tray.BalloonTipClicked += (_, _) => trayClick?.Invoke();
                tray.Click += (_, _) => { if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized; Activate(); };
            }
            trayClick = onClick;
            tray.ShowBalloonTip(6000, title, text, ToolTipIcon.None);
        }
        catch { }
    }

    // ---------- Téléchargements ----------

    void WatchDownload(CoreWebView2DownloadOperation op)
    {
        op.StateChanged += (_, _) =>
        {
            if (op.State == CoreWebView2DownloadState.Completed)
            {
                var file = op.ResultFilePath;
                Notify("Téléchargement terminé", Path.GetFileName(file), () =>
                {
                    try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { }
                });
            }
            else if (op.State == CoreWebView2DownloadState.Interrupted &&
                     op.InterruptReason != CoreWebView2DownloadInterruptReason.UserCanceled &&
                     op.InterruptReason != CoreWebView2DownloadInterruptReason.UserShutdown)
            {
                Notify("Téléchargement interrompu", Path.GetFileName(op.ResultFilePath) + " – " + DownloadReason(op.InterruptReason));
            }
        };
    }

    static string DownloadReason(CoreWebView2DownloadInterruptReason r) => r switch
    {
        CoreWebView2DownloadInterruptReason.FileNoSpace => "plus de place sur le disque",
        CoreWebView2DownloadInterruptReason.FileAccessDenied => "accès refusé au dossier de destination",
        CoreWebView2DownloadInterruptReason.FileBlockedByPolicy or CoreWebView2DownloadInterruptReason.FileSecurityCheckFailed
            => "fichier bloqué par la sécurité de Windows",
        CoreWebView2DownloadInterruptReason.FileMalicious => "fichier détecté comme dangereux",
        CoreWebView2DownloadInterruptReason.ServerCrossOriginRedirect => "le site a redirigé le téléchargement vers un autre domaine",
        CoreWebView2DownloadInterruptReason.NetworkDisconnected or CoreWebView2DownloadInterruptReason.NetworkFailed
            or CoreWebView2DownloadInterruptReason.NetworkTimeout => "problème de connexion",
        CoreWebView2DownloadInterruptReason.ServerUnauthorized or CoreWebView2DownloadInterruptReason.ServerForbidden
            => "le site refuse le téléchargement (connexion requise ?)",
        CoreWebView2DownloadInterruptReason.ServerBadContent or CoreWebView2DownloadInterruptReason.ServerNoRange
            => "fichier introuvable sur le serveur",
        _ => "raison : " + r,
    };

    // ---------- Matchs Gentle Mates ----------

    async Task RefreshMatches(bool force = false)
    {
        if (m8 == null || m8.Loading) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!force && !m8.IsStale(now)) { RefreshLocalPages(); return; }
        lastMatchTry = DateTime.Now;
        await m8.RefreshAsync(force);
    }

    // ---------- Horaires de prière ----------

    async Task RefreshPrayers(bool relocate = false)
    {
        if (prayerBusy) { prayerPending = true; return; } // rejoué dès que le chargement en cours est fini
        prayerBusy = true;
        lastPrayerTry = DateTime.Now;
        try
        {
            if (prayerSource == "mosque" && !string.IsNullOrEmpty(prayerMosque))
            {
                try
                {
                    prayerDay = PrayerAdjust.Apply(await Mawaqit.FetchAsync(prayerMosque, DateTime.Today), PrayerAdjust.ParseOffsets(prayerOffsets));
                    prayerError = null;
                }
                catch (InvalidDataException ex) { prayerError = ex.Message; }
                catch { prayerError = "Impossible de lire les horaires de la mosquée (connexion ?)."; }
                return;
            }
            if (relocate || double.IsNaN(prayerLat))
            {
                if (prayerAuto)
                {
                    var loc = await Prayers.LocateByIpAsync();
                    // la ville a pu être choisie à la main pendant la recherche : on ne l'écrase pas
                    if (loc != null && prayerAuto) { prayerLat = loc.Value.Lat; prayerLon = loc.Value.Lon; prayerCity = loc.Value.City; SaveSettings(); }
                }
            }
            if (double.IsNaN(prayerLat)) { prayerError = "Choisis ta ville pour afficher les horaires."; return; }
            prayerDay = PrayerAdjust.Apply(await Prayers.FetchAsync(DateTime.Today, prayerLat, prayerLon, prayerMethod, prayerCity),
                PrayerAdjust.ParseOffsets(prayerOffsets));
            prayerError = null;
        }
        catch { prayerError = "Impossible de récupérer les horaires (connexion ?)."; }
        finally
        {
            prayerBusy = false;
            UpdatePrayerChip();
            RefreshHomeTabs();
            if (prayerPending) { prayerPending = false; _ = RefreshPrayers(); }
        }
    }

    void UpdatePrayerChip()
    {
        if (prayerDay == null || prayerDay.Times.Count == 0)
        {
            prayerChip.Tag = "off";
            LayoutToolbar();
            return;
        }
        var (name, at) = Prayers.Next(prayerDay, DateTime.Now);
        var left = at - DateTime.Now;
        // à moins d'une heure : compte à rebours visible directement dans la barre
        prayerChip.Text = left.TotalMinutes < 60
            ? $"☾  {name} {at:HH\\:mm} · {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min"
            : $"☾  {name} {at:HH\\:mm}";
        prayerChip.Subtle = left.TotalMinutes >= 15; // dernières minutes : pastille pleine, bien visible
        tip.SetToolTip(prayerChip, $"Prochaine prière : {name} à {at:HH\\:mm} (dans {(left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes:00}" : $"{Math.Max(0, (int)left.TotalMinutes)} min")})\n{prayerDay.City}");
        prayerChip.Tag = "on";
        prayerChip.Invalidate();
        LayoutToolbar();
    }

    void MinuteTick()
    {
        // Nouveau jour → nouveaux horaires ; échec précédent → nouvel essai toutes les 10 min
        if (prayerDay != null && prayerDay.Date != DateTime.Today && DateTime.Now - lastPrayerTry > TimeSpan.FromMinutes(2)) { _ = RefreshPrayers(); return; }
        if (prayerDay == null && !prayerBusy && DateTime.Now - lastPrayerTry > TimeSpan.FromMinutes(10)) { _ = RefreshPrayers(); return; }
        if (prayerDay != null)
        {
            // Notification à l'heure de la prière
            var now = DateTime.Now;
            foreach (var (key, name, time) in prayerDay.Times)
            {
                if (key == "Sunrise") continue;
                var at = DateTime.Today + time;
                var id = $"{DateTime.Today:yyyyMMdd}-{key}";
                if (prayerNotify && now >= at && now < at.AddMinutes(5) && lastNotified != id)
                {
                    lastNotified = id;
                    Notify($"C'est l'heure de {name}", $"{name} – {time:hh\\:mm} · {prayerDay.City}");
                }
                // Rappel quelques minutes avant (réglable : 5, 10, 15, 20, 30 min)
                var before = at.AddMinutes(-prayerReminder);
                if (prayerReminder > 0 && now >= before && now < before.AddMinutes(5) && now < at && lastReminded != id)
                {
                    lastReminded = id;
                    Notify($"{name} dans {Math.Max(1, (int)Math.Ceiling((at - now).TotalMinutes))} min",
                        $"{name} à {time:hh\\:mm} · {prayerDay.City}");
                }
            }
            UpdatePrayerChip();
        }
        // Matchs : rafraîchis toutes les heures (et 15 min d'attente après un échec)
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (m8 != null && m8.IsStale(nowMs) && !m8.Loading && DateTime.Now - lastMatchTry > TimeSpan.FromMinutes(15)) _ = RefreshMatches();
        MatchAlerts(nowMs);
    }

    void ShowPrayerMenu() => ShowPrayerMenu(null);

    void ShowPrayerMenu(Point? at)
    {
        var m = NewMenu(out var r, out var font);

        if (prayerDay != null)
        {
            var next = Prayers.Next(prayerDay, DateTime.Now).Name;
            m.Items.Add(new ToolStripMenuItem($"{prayerDay.City}  ·  {prayerDay.Hijri}") { Enabled = false });
            m.Items.Add(new ToolStripSeparator());
            foreach (var (key, name, time) in prayerDay.Times)
            {
                var it = new ToolStripMenuItem(name) { ShortcutKeyDisplayString = time.ToString(@"hh\:mm"), Padding = new Padding(0, S(3), 0, S(3)) };
                if (name == next) it.Font = new Font(font, FontStyle.Bold);
                if (key == "Sunrise") it.Enabled = false;
                m.Items.Add(it);
            }
            if (!string.IsNullOrEmpty(prayerDay.Jumua))
            {
                var j = new ToolStripMenuItem("Jumu'a (vendredi)") { ShortcutKeyDisplayString = prayerDay.Jumua, Enabled = false, Padding = new Padding(0, S(3), 0, S(3)) };
                m.Items.Add(j);
            }
            m.Items.Add(new ToolStripSeparator());
        }

        // Source des horaires : calcul selon la ville, ou la mosquée choisie
        bool mosque = prayerSource == "mosque" && !string.IsNullOrEmpty(prayerMosque);
        m.Items.Add(MI($"Calcul selon ma ville{(string.IsNullOrEmpty(prayerCity) ? "" : " (" + prayerCity + ")")}",
            () => { prayerSource = "calc"; SaveSettings(); _ = RefreshPrayers(); }, null, !mosque));
        if (!string.IsNullOrEmpty(prayerMosque))
            m.Items.Add(MI("Ma mosquée : " + (prayerMosqueName ?? prayerMosque), () => { prayerSource = "mosque"; SaveSettings(); _ = RefreshPrayers(); }, null, mosque));
        var here = Mawaqit.NormalizeUrl(Core?.Source ?? "");
        if (here != null && (Core?.Source ?? "").Contains("mawaqit.net", StringComparison.OrdinalIgnoreCase) && here != prayerMosque)
            m.Items.Add(MI("Utiliser la mosquée de cet onglet", () => _ = SetMosque(here)));
        m.Items.Add(MI("Choisir ma mosquée (Mawaqit)…", ChooseMosque));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("Ajuster les horaires (± minutes)…", AdjustPrayers));
        if (!mosque)
        {
            m.Items.Add(MI("Changer de ville…", ChangeCity));
            m.Items.Add(MI("Détecter ma position automatiquement", () => { prayerAuto = true; SaveSettings(); _ = RefreshPrayers(relocate: true); }));
            var meth = MI("Méthode de calcul");
            foreach (var (id, label) in Prayers.Methods)
                meth.DropDownItems.Add(MI(label, () => { prayerMethod = id; SaveSettings(); _ = RefreshPrayers(); }, null, id == prayerMethod));
            m.Items.Add(meth);
        }
        m.Items.Add(MI("Notification à l'heure de chaque prière", () => { prayerNotify = !prayerNotify; SaveSettings(); RefreshSettingsPages(); }, null, prayerNotify));
        var remind = MI("Rappel avant la prière");
        foreach (var min in new[] { 0, 5, 10, 15, 20, 30 })
            remind.DropDownItems.Add(MI(min == 0 ? "Pas de rappel" : $"{min} min avant", () => { prayerReminder = min; SaveSettings(); RefreshSettingsPages(); }, null, prayerReminder == min));
        m.Items.Add(remind);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("Tous les réglages des prières…", () => OpenSettings("prieres")));
        StyleDropDowns(m.Items, r, font);
        if (at != null) m.Show(at.Value);
        else if (prayerChip.Visible) m.Show(prayerChip, new Point(prayerChip.Width, prayerChip.Height + S(4)), ToolStripDropDownDirection.BelowLeft);
        else m.Show(menuBtn, new Point(menuBtn.Width, menuBtn.Height + S(4)), ToolStripDropDownDirection.BelowLeft);
    }

    void ChooseMosque()
    {
        var text = Prompt.Show(this, "Horaires de ta mosquée",
            "Colle le lien de ta mosquée sur Mawaqit (ex. mawaqit.net/fr/ma-mosquee).\nLaisse vide pour ouvrir Mawaqit et la chercher.", theme, "Valider", prayerMosque ?? "");
        if (text == null) return;
        if (string.IsNullOrWhiteSpace(text)) { _ = NewTab("https://mawaqit.net/fr/"); return; }
        var url = Mawaqit.NormalizeUrl(text);
        if (url == null)
        {
            MessageBox.Show(this, "Ce lien n'est pas une page de mosquée Mawaqit.\nOuvre ta mosquée sur mawaqit.net puis copie l'adresse.", "Prières",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ = SetMosque(url);
    }

    async Task SetMosque(string url)
    {
        try
        {
            var day = await Mawaqit.FetchAsync(url, DateTime.Today);
            prayerMosque = url; prayerMosqueName = day.City; prayerSource = "mosque";
            SaveSettings();
            await RefreshPrayers();
            Notify("Horaires de prière", $"Horaires de « {day.City} » utilisés.");
        }
        catch (InvalidDataException ex) { MessageBox.Show(this, ex.Message, "Prières", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        catch { MessageBox.Show(this, "Impossible de lire cette page (connexion ?).", "Prières", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    void AdjustPrayers()
    {
        var current = PrayerAdjust.ParseOffsets(prayerOffsets);
        var result = AdjustDialog.Show(this, theme, current);
        if (result == null) return;
        prayerOffsets = PrayerAdjust.Format(result);
        SaveSettings();
        _ = RefreshPrayers();
    }

    async void ChangeCity()
    {
        var text = Prompt.Show(this, "Ville pour les horaires de prière",
            "Tape le nom de ta ville (ex. Creutzwald, Metz, Paris…)", theme, "Valider");
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var geo = await Prayers.GeocodeAsync(text.Trim());
            if (geo == null)
            {
                MessageBox.Show(this, $"Ville « {text} » introuvable.", "Prières", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            prayerLat = geo.Value.Lat; prayerLon = geo.Value.Lon; prayerCity = geo.Value.City; prayerAuto = false;
            SaveSettings();
            await RefreshPrayers();
        }
        catch
        {
            MessageBox.Show(this, "Impossible de chercher la ville (connexion ?).", "Prières", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------- Extensions ----------

    List<(string StoreId, string RuntimeId)> ReadExtList()
    {
        if (!File.Exists(extListPath))
            return Extensions.Defaults.Select(d => (d.StoreId, (string)null)).ToList();
        var list = new List<(string, string)>();
        try
        {
            foreach (var l in File.ReadAllLines(extListPath))
            {
                var p = l.Split('\t');
                if (p[0].Length == 32) list.Add((p[0], p.Length > 1 && p[1].Length > 0 ? p[1] : null));
            }
        }
        catch { }
        return list;
    }

    void SaveExtList()
    {
        try { File.WriteAllLines(extListPath, extensions.Select(e => e.StoreId + "\t" + e.RuntimeId)); } catch { }
    }

    async Task LoadExtensionsAsync()
    {
        if (profile == null) return;
        extBusy = true;
        try
        {
            IReadOnlyList<CoreWebView2BrowserExtension> installed;
            try { installed = await profile.GetBrowserExtensionsAsync(); }
            catch { installed = Array.Empty<CoreWebView2BrowserExtension>(); }

            foreach (var (storeId, rid) in ReadExtList())
            {
                var e = new ExtInfo { StoreId = storeId, RuntimeId = rid, Folder = Path.Combine(extDir, storeId) };
                extensions.Add(e);
                await SetupExtension(e, installed);
            }
            SaveExtList();
        }
        finally { extBusy = false; }
    }

    /// Télécharge l'extension si besoin, la charge dans le moteur et lit son popup / icône.
    async Task SetupExtension(ExtInfo e, IReadOnlyList<CoreWebView2BrowserExtension> installed)
    {
        var defaultName = Extensions.Defaults.FirstOrDefault(d => d.StoreId == e.StoreId).Name;
        try
        {
            if (!File.Exists(Path.Combine(e.Folder, "manifest.json")))
                await Extensions.DownloadAsync(e.StoreId, e.Folder);

            var ext = installed?.FirstOrDefault(x => x.Id == e.RuntimeId);
            if (ext == null)
            {
                try { ext = await profile.AddBrowserExtensionAsync(e.Folder); }
                catch
                {
                    // déjà chargée sous un autre identifiant : on la retrouve par son nom
                    var all = await profile.GetBrowserExtensionsAsync();
                    ext = all.FirstOrDefault(x => defaultName != null && x.Name != null &&
                                                  x.Name.Contains(defaultName, StringComparison.OrdinalIgnoreCase));
                    if (ext == null) throw;
                }
            }
            e.RuntimeId = ext.Id;
            e.Enabled = ext.IsEnabled;
            e.Name = defaultName ?? ext.Name;
            e.Error = null;
        }
        catch (Exception ex)
        {
            e.Error = ex.Message;
            e.Name ??= defaultName ?? "Extension";
        }
        Extensions.ReadManifest(e);
    }

    void StyleDropDowns(ToolStripItemCollection items, ToolStripRenderer r, Font f)
    {
        foreach (var it in items.OfType<ToolStripMenuItem>().Where(x => x.HasDropDownItems))
        {
            var dd = (ToolStripDropDownMenu)it.DropDown;
            dd.Renderer = r;
            dd.Font = f;
            dd.ShowItemToolTips = true;
            dd.ImageScalingSize = new Size(S(20), S(20));
            StyleDropDowns(it.DropDownItems, r, f);
        }
    }

    void ShowExtensionsMenu()
    {
        lastMenu?.Dispose();
        var r = new MenuRenderer(theme, iconSmall);
        var font = new Font("Segoe UI", 9.5f);
        var m = new ContextMenuStrip { Renderer = r, Font = font, ShowItemToolTips = true, ImageScalingSize = new Size(S(20), S(20)) };
        lastMenu = m;

        if (extBusy) m.Items.Add(new ToolStripMenuItem("Installation des extensions en cours…") { Enabled = false });
        foreach (var e in extensions)
        {
            var ext = e;
            var label = ext.Name + (ext.Error != null ? "  (erreur, cliquer pour réessayer)" : !ext.Enabled ? "  (désactivée)" : "");
            var it = MI(label, () => OpenExtPopup(ext));
            it.Image = ext.Icon;
            it.Enabled = ext.Error != null || (ext.Enabled && ext.RuntimeId != null && ext.Popup != null);
            m.Items.Add(it);
        }
        if (extensions.Count == 0 && !extBusy) m.Items.Add(new ToolStripMenuItem("(aucune extension)") { Enabled = false });

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("Ajouter une extension du Chrome Web Store…", AddExtensionPrompt));

        var manage = MI("Gérer les extensions");
        foreach (var e in extensions)
        {
            var ext = e;
            var sub = MI(ext.Name);
            sub.Image = ext.Icon;
            var openTab = MI("Ouvrir dans un onglet", () => _ = NewTab($"chrome-extension://{ext.RuntimeId}/{ext.Popup}"));
            openTab.Enabled = ext.Enabled && ext.RuntimeId != null && ext.Popup != null;
            sub.DropDownItems.Add(openTab);
            var toggle = MI(ext.Enabled ? "Désactiver" : "Activer", () => _ = ToggleExtension(ext));
            toggle.Enabled = ext.RuntimeId != null;
            sub.DropDownItems.Add(toggle);
            sub.DropDownItems.Add(MI("Réinstaller (dernière version)", () => _ = ReinstallExtension(ext)));
            sub.DropDownItems.Add(MI("Retirer", () => _ = RemoveExtension(ext)));
            manage.DropDownItems.Add(sub);
        }
        manage.Enabled = extensions.Count > 0;
        m.Items.Add(manage);

        StyleDropDowns(m.Items, r, font);
        m.Show(extBtn, new Point(extBtn.Width, extBtn.Height + S(4)), ToolStripDropDownDirection.BelowLeft);
    }

    void OpenExtPopup(ExtInfo e)
    {
        if (e.Error != null)
        {
            var retry = MessageBox.Show(this, $"{e.Name} n'a pas pu être installée :\n{e.Error}\n\nRéessayer ?",
                "Extensions", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (retry == DialogResult.Yes) _ = ReinstallExtension(e);
            return;
        }
        if (e.RuntimeId == null || e.Popup == null || env == null) return;
        if (extPopup is { IsDisposed: false }) extPopup.Close();
        var anchor = extBtn.RectangleToScreen(extBtn.ClientRectangle);
        extPopup = new ExtensionPopup(env, $"chrome-extension://{e.RuntimeId}/{e.Popup}", anchor, theme, u => _ = NewTab(u));
        extPopup.Show(this);
    }

    async Task<CoreWebView2BrowserExtension> FindExtension(ExtInfo e)
    {
        if (profile == null || e.RuntimeId == null) return null;
        try { return (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == e.RuntimeId); }
        catch { return null; }
    }

    async Task ToggleExtension(ExtInfo e)
    {
        var ext = await FindExtension(e);
        if (ext == null) return;
        bool target = !e.Enabled;
        try { await ext.EnableAsync(target); e.Enabled = target; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Extensions", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    async Task RemoveExtension(ExtInfo e)
    {
        var ok = MessageBox.Show(this, $"Retirer {e.Name} ?", "Extensions", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ok != DialogResult.Yes) return;
        var ext = await FindExtension(e);
        try { if (ext != null) await ext.RemoveAsync(); } catch { }
        extensions.Remove(e);
        SaveExtList();
        try { Directory.Delete(e.Folder, true); } catch { }
    }

    async Task ReinstallExtension(ExtInfo e)
    {
        if (profile == null || extBusy) return;
        extBusy = true;
        try
        {
            var ext = await FindExtension(e);
            try { if (ext != null) await ext.RemoveAsync(); } catch { }
            e.RuntimeId = null;
            try { await Extensions.DownloadAsync(e.StoreId, e.Folder); }
            catch (Exception ex) { e.Error = ex.Message; }
            if (e.Error == null || File.Exists(Path.Combine(e.Folder, "manifest.json"))) await SetupExtension(e, null);
            SaveExtList();
        }
        finally { extBusy = false; }
        MessageBox.Show(this, e.Error == null ? $"{e.Name} est installée et à jour." : $"Échec : {e.Error}",
            "Extensions", MessageBoxButtons.OK, e.Error == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    void AddExtensionPrompt()
    {
        if (profile == null) return;
        if (extBusy)
        {
            MessageBox.Show(this, "Une installation d'extension est déjà en cours, réessaie dans un instant.", "Extensions",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var src = Core?.Source ?? "";
        string id = src.Contains("chromewebstore.google.com") || src.Contains("chrome.google.com/webstore")
            ? Extensions.ParseStoreId(src) : null;

        if (id != null)
        {
            var ok = MessageBox.Show(this, "Ajouter l'extension de cette page à LiteBrowser ?", "Extensions",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ok != DialogResult.Yes) return;
        }
        else
        {
            var text = Prompt.Show(this, "Ajouter une extension",
                "Colle le lien de l'extension sur le Chrome Web Store\n(ex. https://chromewebstore.google.com/detail/…)", theme);
            if (text == null) return;
            id = Extensions.ParseStoreId(text);
            if (id == null)
            {
                MessageBox.Show(this, "Ce lien n'est pas reconnu. Copie l'adresse de la page de l'extension sur le Chrome Web Store.",
                    "Extensions", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        if (extensions.Any(e => e.StoreId == id))
        {
            MessageBox.Show(this, "Cette extension est déjà installée.", "Extensions", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _ = AddExtension(id);
    }

    async Task AddExtension(string id)
    {
        var e = new ExtInfo { StoreId = id, Folder = Path.Combine(extDir, id) };
        extensions.Add(e);
        extBusy = true;
        try { await SetupExtension(e, null); SaveExtList(); }
        finally { extBusy = false; }
        MessageBox.Show(this,
            e.Error == null ? $"« {e.Name} » est installée. Clique sur le bouton Extensions (pièce de puzzle) pour l'ouvrir."
                            : $"Impossible d'installer cette extension :\n{e.Error}",
            "Extensions", MessageBoxButtons.OK, e.Error == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // ---------- Menu ☰ ----------

    ToolStripMenuItem MI(string text, Action a = null, string keys = null, bool check = false)
    {
        var it = new ToolStripMenuItem(text.Replace("&", "&&"))
        {
            ShortcutKeyDisplayString = keys,
            Checked = check,
            Padding = new Padding(0, S(3), 0, S(3))
        };
        if (a != null) it.Click += (_, _) => a();
        return it;
    }

    ToolStripMenuItem LinkItem((string Title, string Url) e)
    {
        var t = e.Title.Length > 60 ? e.Title[..60] + "…" : e.Title;
        var it = MI(t, () => Core?.Navigate(e.Url));
        it.ToolTipText = e.Url;
        return it;
    }

    void ShowMenu()
    {
        lastMenu?.Dispose();
        var r = new MenuRenderer(theme, iconSmall);
        var font = new Font("Segoe UI", 9.5f);
        var m = new ContextMenuStrip { Renderer = r, Font = font, ShowItemToolTips = true };
        lastMenu = m;

        m.Items.Add(MI("Nouvel onglet", () => _ = NewTab(), "Ctrl+T"));
        m.Items.Add(MI("Nouvel onglet privé", () => _ = NewTab(priv: true), "Ctrl+Maj+N"));
        m.Items.Add(MI(nurLiveUi != null ? "Multistream (Nur Live)" : "Multistream", () => OpenMulti(new List<string>(), chat: true), "Ctrl+Maj+M"));
        var reopen = MI("Rouvrir l'onglet fermé", ReopenClosed, "Ctrl+Maj+T");
        reopen.Enabled = closedTabs.Count > 0;
        m.Items.Add(reopen);
        m.Items.Add(new ToolStripSeparator());

        // Favoris
        var fav = MI("Favoris");
        bool canFav = Cur != null && !Cur.IsHome;
        bool isFav = canFav && favorites.Exists(f => f.Url == Core?.Source);
        var addFav = MI(isFav ? "Retirer cette page des favoris" : "Ajouter cette page aux favoris", ToggleFavorite, "Ctrl+D");
        addFav.Enabled = canFav;
        fav.DropDownItems.Add(addFav);
        fav.DropDownItems.Add(new ToolStripSeparator());
        if (favorites.Count == 0) fav.DropDownItems.Add(new ToolStripMenuItem("(aucun favori pour l'instant)") { Enabled = false });
        foreach (var f in favorites) fav.DropDownItems.Add(LinkItem(f));
        m.Items.Add(fav);

        // Historique
        var hist = MI("Historique");
        hist.DropDownItems.Add(MI("Tout l'historique…", () => OpenSettings("historique"), "Ctrl+H"));
        hist.DropDownItems.Add(new ToolStripSeparator());
        for (int i = history.Count - 1, n = 0; i >= 0 && n < 20; i--, n++) hist.DropDownItems.Add(LinkItem(history[i]));
        if (history.Count == 0) hist.DropDownItems.Add(new ToolStripMenuItem(historySave ? "(vide)" : "(historique désactivé)") { Enabled = false });
        m.Items.Add(hist);
        m.Items.Add(MI("Téléchargements", () => Core?.OpenDefaultDownloadDialog(), "Ctrl+J"));
        m.Items.Add(new ToolStripSeparator());

        // Réglages rapides (tout le reste est dans la page Réglages)
        var th = MI("Thème");
        foreach (var (id, label) in new[] { ("m8", "Gentle Mates"), ("dark", "Sombre"), ("light", "Clair") })
            th.DropDownItems.Add(MI(label, () => SetTheme(id), null, themeName == id));
        m.Items.Add(th);
        m.Items.Add(MI("Afficher la barre de favoris", ToggleFavBar, "Ctrl+Maj+B", showFavBar));
        m.Items.Add(MI($"Bloquer les pubs, y compris YouTube  ({blockedCount} bloquées)", () => SetAdblock(!adblock), null, adblock));
        m.Items.Add(MI("Plein écran", () => SetFullscreen(!fullscreen), "F11"));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("Réglages", () => OpenSettings(), "Ctrl+,"));
        m.Items.Add(MI("Effacer les données de navigation…", ClearData, "Ctrl+Maj+Suppr"));
        m.Items.Add(MI("Raccourcis clavier", () => OpenSettings("raccourcis")));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI(updateReady ? "Redémarrer pour installer la mise à jour" : "Rechercher des mises à jour",
            () => _ = CheckUpdates(silent: false)));
        m.Items.Add(new ToolStripMenuItem($"LiteBrowser v{Updater.Current}") { Enabled = false });

        foreach (var it in m.Items.OfType<ToolStripMenuItem>().Where(x => x.HasDropDownItems))
        {
            var dd = (ToolStripDropDownMenu)it.DropDown;
            dd.Renderer = r;
            dd.Font = font;
            dd.ShowItemToolTips = true;
        }

        m.Show(menuBtn, new Point(menuBtn.Width, menuBtn.Height + S(4)), ToolStripDropDownDirection.BelowLeft);
    }

    async void ToggleForceDark()
    {
        forceDark = !forceDark;
        SaveSettings();
        ApplyScheme();
        foreach (var t in tabs.ToArray())
            try { await ApplyForceDark(t); } catch { }
    }

    async Task ApplyForceDark(BrowserTab t)
    {
        var core = t.View.CoreWebView2;
        if (core == null) return;
        if (forceDark && t.DarkScriptId == null)
        {
            t.DarkScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(ForceDarkJs);
            await core.ExecuteScriptAsync(ForceDarkJs);
        }
        else if (!forceDark && t.DarkScriptId != null)
        {
            core.RemoveScriptToExecuteOnDocumentCreated(t.DarkScriptId);
            t.DarkScriptId = null;
            await core.ExecuteScriptAsync(UnforceDarkJs);
        }
    }

    async void ClearData()
    {
        var ok = MessageBox.Show(this,
            "Effacer l'historique, les cookies et le cache ?\nTu seras déconnecté des sites (tes favoris sont conservés).",
            "Effacer les données", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ok != DialogResult.Yes) return;
        await ClearBrowsingData();
        RefreshSettingsPages();
        Notify("Données effacées", "Historique, cookies et cache ont été supprimés. Tes favoris sont conservés.");
    }

    void SetTheme(string id)
    {
        if (id != "m8" && id != "dark" && id != "light") return;
        themeName = id;
        SaveSettings();
        ApplyTheme();
        RefreshLocalPages();
    }

    // ---------- Réglages ----------

    void LoadSettings()
    {
        try
        {
            if (File.Exists(settingsPath))
                foreach (var l in File.ReadAllLines(settingsPath))
                {
                    int i = l.IndexOf('=');
                    if (i > 0) settings[l[..i].Trim()] = l[(i + 1)..].Trim();
                }
        }
        catch { }

        bool systemDark = false;
        try
        {
            systemDark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int v && v == 0;
        }
        catch { }

        themeName = settings.TryGetValue("theme", out var th) && (th == "m8" || th == "dark" || th == "light") ? th : "m8";
        if (settings.TryGetValue("themeVersion", out var tv) == false) themeName = "m8"; // passage à la v3 : thème Gentle Mates par défaut
        _ = systemDark;
        if (settings.TryGetValue("prayer.lat", out var la) && double.TryParse(la, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat)) prayerLat = lat;
        if (settings.TryGetValue("prayer.lon", out var lo) && double.TryParse(lo, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon)) prayerLon = lon;
        if (settings.TryGetValue("prayer.city", out var pc)) prayerCity = pc;
        if (settings.TryGetValue("prayer.method", out var pm) && int.TryParse(pm, out var pmi) && Prayers.Methods.Any(x => x.Id == pmi)) prayerMethod = pmi;
        prayerNotify = !settings.TryGetValue("prayer.notify", out var pn) || pn == "1";
        prayerAuto = !settings.TryGetValue("prayer.auto", out var pa) || pa == "1";
        prayerSource = settings.TryGetValue("prayer.source", out var ps) && ps == "mosque" ? "mosque" : "calc";
        prayerMosque = settings.TryGetValue("prayer.mosque", out var pmq) && pmq.Length > 0 ? pmq : null;
        prayerMosqueName = settings.TryGetValue("prayer.mosqueName", out var pmn) && pmn.Length > 0 ? pmn : null;
        prayerOffsets = settings.TryGetValue("prayer.offsets", out var po) ? po : "";
        forceDark = settings.TryGetValue("forcedark", out var fd) && fd == "1";
        adblock = !settings.TryGetValue("adblock", out var ab) || ab == "1";
        showFavBar = !settings.TryGetValue("favbar", out var fb) || fb == "1";
        if (settings.TryGetValue("engine", out var en) && Engines.Any(e => e.Id == en)) engineId = en;
        startupMode = settings.TryGetValue("startup", out var su) && su == "home" ? "home" : "session";
        if (settings.TryGetValue("tabs.sleep", out var ts) && int.TryParse(ts, out var tsi)) tabSleepSec = Math.Clamp(tsi, 0, 3600);
        trackingLevel = settings.TryGetValue("tracking", out var tl) && (tl == "balanced" || tl == "strict") ? tl : "basic";
        adblockAllow = SiteList.Parse(settings.TryGetValue("adblock.allow", out var aa) ? aa : "");
        historySave = !settings.TryGetValue("history.save", out var hs) || hs == "1";
        clearOnExit = settings.TryGetValue("clearOnExit", out var ce) && ce == "1";
        downloadAsk = settings.TryGetValue("downloads.ask", out var da) && da == "1";
        downloadDir = settings.TryGetValue("downloads.dir", out var dd) && dd.Length > 0 ? dd : null;
        if (settings.TryGetValue("prayer.reminder", out var pr) && int.TryParse(pr, out var pri)) prayerReminder = Math.Clamp(pri, 0, 60);
    }

    void SaveSettings()
    {
        try
        {
            File.WriteAllLines(settingsPath, new[]
            {
                "theme=" + themeName,
                "themeVersion=3",
                "prayer.lat=" + (double.IsNaN(prayerLat) ? "" : prayerLat.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "prayer.lon=" + (double.IsNaN(prayerLon) ? "" : prayerLon.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "prayer.city=" + (prayerCity ?? ""),
                "prayer.method=" + prayerMethod,
                "prayer.notify=" + (prayerNotify ? "1" : "0"),
                "prayer.auto=" + (prayerAuto ? "1" : "0"),
                "prayer.source=" + prayerSource,
                "prayer.mosque=" + (prayerMosque ?? ""),
                "prayer.mosqueName=" + (prayerMosqueName ?? "").Replace('\n', ' '),
                "prayer.offsets=" + (prayerOffsets ?? ""),
                "forcedark=" + (forceDark ? "1" : "0"),
                "adblock=" + (adblock ? "1" : "0"),
                "engine=" + engineId,
                "favbar=" + (showFavBar ? "1" : "0"),
                "startup=" + startupMode,
                "tabs.sleep=" + tabSleepSec,
                "tracking=" + trackingLevel,
                "adblock.allow=" + SiteList.Format(adblockAllow),
                "history.save=" + (historySave ? "1" : "0"),
                "clearOnExit=" + (clearOnExit ? "1" : "0"),
                "downloads.ask=" + (downloadAsk ? "1" : "0"),
                "downloads.dir=" + (downloadDir ?? ""),
                "prayer.reminder=" + prayerReminder,
                // réglages d'autres versions (ex. chemin Nur Live) : conservés tels quels
            }.Concat(settings.Where(kv => kv.Key.StartsWith("nurlive.")).Select(kv => kv.Key + "=" + kv.Value)));
        }
        catch { }
    }

    // ---------- Bloqueur ----------

    void LoadBlocklist()
    {
        foreach (var d in new[] {
            "doubleclick.net", "googlesyndication.com", "googleadservices.com", "google-analytics.com",
            "googletagmanager.com", "adservice.google.com", "facebook.net", "scorecardresearch.com",
            "taboola.com", "outbrain.com", "criteo.com", "adnxs.com", "amazon-adsystem.com",
            "hotjar.com", "quantserve.com", "rubiconproject.com", "pubmatic.com", "openx.net", "moatads.com" })
            blocked.Add(d);

        // Optionnel : blocklist.txt à côté de l'exe (format "hosts" ou un domaine par ligne)
        var f = Path.Combine(AppContext.BaseDirectory, "blocklist.txt");
        if (!File.Exists(f)) return;
        foreach (var raw in File.ReadLines(f))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == '!') continue;
            var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var host = parts.Length > 1 ? parts[1] : parts[0];
            if (host != "localhost" && host != "0.0.0.0") blocked.Add(host);
        }
    }

    bool IsBlocked(string host) => UrlTools.HostMatches(host, blocked);

    // ---------- Raccourcis ----------

    protected override bool ProcessCmdKey(ref Message msg, Keys k)
    {
        switch (k)
        {
            case Keys.Control | Keys.T: _ = NewTab(); return true;
            case Keys.Control | Keys.Shift | Keys.N: _ = NewTab(priv: true); return true;
            case Keys.Control | Keys.H: OpenSettings("historique"); return true;
            case Keys.Control | Keys.J: Core?.OpenDefaultDownloadDialog(); return true;
            case Keys.Control | Keys.Oemcomma: OpenSettings(); return true;
            case Keys.Control | Keys.Shift | Keys.Delete: ClearData(); return true;
            case Keys.Control | Keys.Shift | Keys.T: ReopenClosed(); return true;
            case Keys.Control | Keys.W:
            case Keys.Control | Keys.F4: BeginInvoke(new Action(() => CloseTab(sel))); return true;
            case Keys.Control | Keys.L:
            case Keys.Alt | Keys.D:
            case Keys.F6: addr.Box.Focus(); addr.Box.SelectAll(); return true;
            case Keys.Control | Keys.D: ToggleFavorite(); return true;
            case Keys.Control | Keys.Shift | Keys.B: ToggleFavBar(); return true;
            case Keys.Control | Keys.Shift | Keys.M: OpenMulti(new List<string>(), chat: true); return true;
            case Keys.Control | Keys.M: ToggleMute(sel); return true;
            case Keys.F5:
            case Keys.Control | Keys.R: Core?.Reload(); return true;
            case Keys.Alt | Keys.Left: Core?.GoBack(); return true;
            case Keys.Alt | Keys.Right: Core?.GoForward(); return true;
            case Keys.Alt | Keys.Home: Core?.Navigate(HomeUrl); return true;
            case Keys.F11: SetFullscreen(!fullscreen); return true;
            case Keys.Control | Keys.Tab:
                if (tabs.Count > 1) SelectTab((sel + 1) % tabs.Count);
                return true;
            case Keys.Control | Keys.Shift | Keys.Tab:
                if (tabs.Count > 1) SelectTab((sel - 1 + tabs.Count) % tabs.Count);
                return true;
        }
        if ((k & Keys.Modifiers) == Keys.Control)
        {
            var key = k & Keys.KeyCode;
            if (key >= Keys.D1 && key <= Keys.D9)
            {
                int i = key == Keys.D9 ? tabs.Count - 1 : key - Keys.D1;
                if (i < tabs.Count) SelectTab(i);
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, k);
    }
}
