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
        if (BrowserForm.SmokeReport == null && Installer.EnsureInstalled()) return;
        Updater.CleanupOld();

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
    /// Thème « Gentle Mates » : noir profond, blanc cassé, accents blancs.
    public static readonly Theme M8 = new()
    {
        Strip = C(0x08080A), Bar = C(0x141418), Hover = C(0x26262D), Text = C(0xF4F2EE),
        Sub = C(0x8F8D96), Field = C(0x0E0E12), FieldBorder = C(0x26262D), Accent = C(0xFFFFFF)
    };
    static Color C(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
}

static class Glyphs
{
    public const string Back = "", Forward = "", Refresh = "", Stop = "",
        Home = "", More = "", Star = "", StarFill = "", Add = "",
        Close = "", Lock = "", Search = "", Globe = "", Warning = "",
        Check = "";
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
    public bool Highlight;

    public string Glyph { get => glyph; set { if (glyph != value) { glyph = value; Invalidate(); } } }

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
        var col = !Enabled ? G.Mix(BackColor, Theme.Sub, .45f) : Highlight ? Theme.Accent : Theme.Text;
        TextRenderer.DrawText(g, glyph, Font, ClientRectangle, col, G.Center);
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

/// Mini boîte de saisie.
static class Prompt
{
    public static string Show(IWin32Window owner, string title, string text, Theme t, string okText = "Ajouter")
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, BackColor = t.Bar, ForeColor = t.Text,
            Font = new Font("Segoe UI", 9.5f), AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(460, 150)
        };
        var lbl = new Label { Text = text, Left = 16, Top = 14, Width = 428, Height = 40 };
        var box = new TextBox { Left = 16, Top = 60, Width = 428, BackColor = t.Field, ForeColor = t.Text };
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
    public bool Loading, IsHome = true;
    public string DarkScriptId;
    public System.Windows.Forms.Timer SuspendTimer;
}

/// Barre d'onglets : onglets arrondis, icône du site, bouton ✕, bouton +.
class TabStrip : Control
{
    public readonly List<BrowserTab> Tabs;
    public int Selected = -1, SpinAngle;
    public Theme Theme = Theme.Light;
    public event Action<int> TabSelect, TabClose;
    public event Action NewTabRequested;

    readonly Font icons;
    readonly ToolTip tip = new();
    int hover = -1;
    bool hoverClose, hoverPlus;

    public TabStrip(List<BrowserTab> tabs, Font icons)
    {
        Tabs = tabs;
        this.icons = icons;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    int Sc(int v) => LogicalToDeviceUnits(v);
    int TabWidth()
    {
        int n = Math.Max(1, Tabs.Count);
        int avail = Width - Sc(8) - Sc(48);
        return Math.Max(Sc(56), Math.Min(Sc(240), avail / n));
    }
    Rectangle TabRect(int i) { int w = TabWidth(); return new Rectangle(Sc(8) + i * w, Sc(7), w, Height - Sc(7)); }
    Rectangle CloseRect(Rectangle r) { int s = Sc(20); return new Rectangle(r.Right - s - Sc(8), r.Top + (r.Height - s) / 2, s, s); }
    Rectangle PlusRect() { int s = Sc(28); return new Rectangle(Sc(8) + Tabs.Count * TabWidth() + Sc(6), Sc(7) + (Height - Sc(7) - s) / 2, s, s); }
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
                using var b = new SolidBrush(sel ? Theme.Bar : G.Mix(Theme.Strip, Theme.Bar, .5f));
                g.FillPath(b, p);
            }
            else if (i + 1 < Tabs.Count && i + 1 != Selected && i + 1 != hover)
            {
                using var pen = new Pen(G.Mix(Theme.Strip, Theme.Sub, .35f));
                g.DrawLine(pen, r.Right, r.Top + Sc(9), r.Right, r.Bottom - Sc(9));
            }

            int x = r.X + Sc(12), cy = r.Top + r.Height / 2;
            var ir = new Rectangle(x, cy - isz / 2, isz, isz);
            if (t.Loading)
            {
                using var pen = new Pen(Theme.Accent, Math.Max(2, Sc(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, Rectangle.Inflate(ir, -1, -1), SpinAngle, 270);
            }
            else if (t.IsHome) TextRenderer.DrawText(g, Glyphs.Home, icons, ir, Theme.Sub, G.Center);
            else if (t.Icon != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(t.Icon, ir); }
            else TextRenderer.DrawText(g, Glyphs.Globe, icons, ir, Theme.Sub, G.Center);

            var cr = CloseRect(r);
            int tx = ir.Right + Sc(8);
            var textRect = new Rectangle(tx, r.Top, (sel || hov ? cr.Left - Sc(4) : r.Right - Sc(10)) - tx, r.Height);
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
        bool hc = h >= 0 && CloseRect(TabRect(h)).Contains(e.Location);
        bool hp = PlusRect().Contains(e.Location);
        if (h != hover || hc != hoverClose || hp != hoverPlus)
        {
            bool tipChange = h != hover || hp != hoverPlus;
            hover = h; hoverClose = hc; hoverPlus = hp;
            Invalidate();
            if (tipChange)
                tip.SetToolTip(this, h >= 0 ? Tabs[h].Title : hp ? "Nouvel onglet (Ctrl+T)" : null);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1; hoverClose = hoverPlus = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int h = Hit(e.Location);
        if (e.Button == MouseButtons.Middle) { if (h >= 0) TabClose?.Invoke(h); return; }
        if (e.Button != MouseButtons.Left) return;
        if (PlusRect().Contains(e.Location)) { NewTabRequested?.Invoke(); return; }
        if (h < 0) return;
        if (CloseRect(TabRect(h)).Contains(e.Location)) TabClose?.Invoke(h);
        else TabSelect?.Invoke(h);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && Hit(e.Location) < 0 && !PlusRect().Contains(e.Location))
            NewTabRequested?.Invoke();
    }
}

/// Barre d'adresse arrondie : icône (cadenas / loupe) + texte + étoile favoris.
class AddressBox : Panel
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None };
    public readonly Label Badge = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    public readonly IconButton Star;
    Theme theme = Theme.Light;

    public AddressBox(Font iconFont, Font iconSmall, Font textFont)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Box.Font = textFont;
        Badge.Font = iconSmall;
        Star = new IconButton(Glyphs.Star, iconFont);
        Controls.Add(Box);
        Controls.Add(Badge);
        Controls.Add(Star);
        Box.GotFocus += (_, _) => Recolor();
        Box.LostFocus += (_, _) => Recolor();
        Badge.Click += (_, _) => Box.Focus();
        Click += (_, _) => Box.Focus();
    }

    int Sc(int v) => LogicalToDeviceUnits(v);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int h = Height, s = h - Sc(6);
        Badge.SetBounds(Sc(12), 0, Sc(18), h);
        Star.SetBounds(Width - s - Sc(3), Sc(3), s, s);
        int bh = Box.PreferredHeight;
        int left = Badge.Right + Sc(8);
        int right = Star.Visible ? Star.Left - Sc(4) : Width - Sc(16);
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
        using (var pen = new Pen(f ? theme.Accent : theme.FieldBorder, f ? Sc(2) : 1)) g.DrawPath(pen, p);
    }

    public void ApplyTheme(Theme t, Color parentBack)
    {
        theme = t;
        BackColor = parentBack;
        Star.Theme = t;
        Recolor();
    }

    void Recolor()
    {
        var fill = Box.Focused ? theme.Bar : theme.Field;
        Box.BackColor = fill;
        Box.ForeColor = theme.Text;
        Badge.BackColor = fill;
        Badge.ForeColor = theme.Sub;
        Star.BackColor = fill;
        Invalidate();
        Star.Invalidate();
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
    const int SuspendDelayMs = 30_000; // un onglet inactif est gelé après 30 s

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

    // Matchs, prières, notifications
    MatchService matches;
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
    static bool IsHome(string url) => url != null && url.StartsWith("https://" + HomeHost + "/", StringComparison.OrdinalIgnoreCase);

    public BrowserForm()
    {
        Text = "LiteBrowser";
        var wa = Screen.PrimaryScreen.WorkingArea;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(wa.Width * 4 / 5, wa.Height * 4 / 5);
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(S(520), S(320));
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteBrowser");
        Directory.CreateDirectory(dataDir);
        favPath = Path.Combine(dataDir, "favoris.txt");
        histPath = Path.Combine(dataDir, "historique.txt");
        settingsPath = Path.Combine(dataDir, "reglages.txt");
        homeDir = Path.Combine(dataDir, "accueil");
        extDir = Path.Combine(dataDir, "extensions");
        extListPath = Path.Combine(dataDir, "extensions.txt");

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

        // --- Barre d'outils ---
        toolbar.Height = S(46);
        backBtn = Btn(Glyphs.Back, "Page précédente (Alt+←)", () => Core?.GoBack());
        fwdBtn = Btn(Glyphs.Forward, "Page suivante (Alt+→)", () => Core?.GoForward());
        reloadBtn = Btn(Glyphs.Refresh, "Actualiser (F5)", () => { if (Cur?.Loading == true) Core?.Stop(); else Core?.Reload(); });
        homeBtn = Btn(Glyphs.Home, "Page d'accueil", () => Core?.Navigate(HomeUrl));
        menuBtn = Btn(Glyphs.More, "Menu : favoris, historique, moteur de recherche, thème…", ShowMenu);
        extBtn = Btn("", "Extensions (NordPass, TrendTrack…)", ShowExtensionsMenu);

        addr = new AddressBox(iconFont, iconSmall, new Font("Segoe UI", 10.5f));
        addr.Star.Click += (_, _) => ToggleFavorite();
        tip.SetToolTip(addr.Star, "Ajouter / retirer des favoris (Ctrl+D)");
        addr.Box.AutoCompleteMode = AutoCompleteMode.Suggest;
        addr.Box.AutoCompleteSource = AutoCompleteSource.CustomSource;
        addr.Box.AutoCompleteCustomSource = suggestions;
        addr.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                var url = ToUrl(addr.Box.Text);
                if (url != null && Core != null) { Core.Navigate(url); Cur.View.Focus(); }
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                Cur?.View.Focus();
            }
        };
        addr.Box.Enter += (_, _) => { selectOnClick = true; BeginInvoke(new Action(() => addr.Box.SelectAll())); };
        addr.Box.MouseUp += (_, _) => { if (selectOnClick && addr.Box.SelectionLength == 0) addr.Box.SelectAll(); selectOnClick = false; };
        addr.Box.Leave += (_, _) => UpdateUi();

        updatePill = new PillButton("Mise à jour prête · Redémarrer", new Font("Segoe UI", 9f, FontStyle.Bold)) { Visible = false };
        tip.SetToolTip(updatePill, "Une nouvelle version a été installée. Clique pour relancer LiteBrowser.");
        updatePill.Click += (_, _) => { Program.RestartRequested = true; Close(); };

        prayerChip = new PillButton("", new Font("Segoe UI", 9f)) { Subtle = true, Visible = false };
        prayerChip.Click += (_, _) => ShowPrayerMenu();

        matches = new MatchService(Path.Combine(dataDir, "matchs.json"));
        matches.Updated += () => { if (IsHandleCreated) BeginInvoke(new Action(RefreshHomeTabs)); };
        minuteTimer.Tick += (_, _) => MinuteTick();
        FormClosed += (_, _) => { try { tray?.Dispose(); } catch { } };

        toolbar.Controls.AddRange(new Control[] { backBtn, fwdBtn, reloadBtn, homeBtn, addr, updatePill, prayerChip, extBtn, menuBtn });
        toolbar.Resize += (_, _) => LayoutToolbar();
        toolbar.Paint += (_, e) =>
        {
            using var p = new Pen(theme.FieldBorder);
            e.Graphics.DrawLine(p, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
        };

        Controls.Add(content);
        Controls.Add(toolbar);
        Controls.Add(strip);

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
            await NewTab();
            minuteTimer.Start();
            _ = RefreshPrayers();
            _ = RefreshMatches();
            await Task.Delay(3000);
            if (SmokeReport != null) { await RunSmoke(); return; }
            await CheckUpdates(silent: true);
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
        if (prayerChip.Visible)
        {
            int pw = prayerChip.PreferredWidth, ph = S(30);
            prayerChip.SetBounds(right - pw, (h - ph) / 2, pw, ph);
            right = prayerChip.Left - S(8);
        }
        if (updatePill.Visible)
        {
            int pw = updatePill.PreferredWidth, ph = S(30);
            updatePill.SetBounds(right - pw, (h - ph) / 2, pw, ph);
            right = updatePill.Left - S(8);
        }
        int ah = S(34), ax = x + S(6);
        addr.SetBounds(ax, (h - ah) / 2, Math.Max(S(100), right - ax), ah);
    }

    // ---------- Démarrage ----------

    async Task InitAsync()
    {
        LoadBlocklist();

        // Page d'accueil intégrée à l'exe, recopiée à chaque démarrage
        Directory.CreateDirectory(homeDir);
        try
        {
            using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("home.html");
            using var dst = File.Create(Path.Combine(homeDir, "index.html"));
            src.CopyTo(dst);
        }
        catch { /* déjà présente / ouverte par une autre fenêtre */ }

        // On coupe tout ce qui tourne en arrière-plan pour rien
        var opts = new CoreWebView2EnvironmentOptions(
            "--disable-background-networking --disable-component-update --disable-sync " +
            "--disable-domain-reliability --disable-breakpad --no-pings " +
            "--renderer-process-limit=3 " +
            "--disable-features=Translate,msSmartScreenProtection,msEdgeShoppingAssistant,AutofillServerCommunication");
        opts.AreBrowserExtensionsEnabled = true;

        env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDir, "WebView2"), opts);
    }

    // ---------- Onglets ----------

    async Task NewTab(string url = null)
    {
        if (env == null) return;
        var t = new BrowserTab { IsHome = url == null };
        t.View = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = theme.Bar };
        content.Controls.Add(t.View);
        int idx = sel >= 0 ? sel + 1 : tabs.Count; // s'ouvre juste à droite de l'onglet actuel
        tabs.Insert(idx, t);
        SelectTab(idx);

        try { await t.View.EnsureCoreWebView2Async(env); } catch { return; }
        if (!tabs.Contains(t)) return; // fermé entre-temps
        var core = t.View.CoreWebView2;

        var s = core.Settings;
        s.IsStatusBarEnabled = true; // affiche l'adresse des liens au survol
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;
        core.Profile.PreferredColorScheme = Scheme();
        core.SetVirtualHostNameToFolderMapping(HomeHost, homeDir, CoreWebView2HostResourceAccessKind.Allow);

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
            if (!adblock) return;
            if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u) && IsBlocked(u.Host))
            {
                e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
                blockedCount++;
            }
        };

        core.NewWindowRequested += (_, e) => { e.Handled = true; _ = NewTab(e.Uri); };

        // Bloqueur de pubs YouTube (le script ne s'active que sur youtube.com)
        if (adblock)
        {
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeAdblock.Script); } catch { }
        }

        // Téléchargements : on suit chaque fichier et on prévient clairement en cas de souci
        core.DownloadStarting += (_, e) =>
        {
            WatchDownload(e.DownloadOperation);
            smokeDownload?.TrySetResult(e.DownloadOperation);
        };

        core.NavigationStarting += (_, _) =>
        {
            t.Loading = true;
            if (!spin.Enabled) spin.Start();
            strip.Invalidate();
            if (t == Cur) UpdateUi();
        };
        core.NavigationCompleted += (_, e) =>
        {
            t.Loading = false;
            strip.Invalidate();
            if (e.IsSuccess && core.Source.StartsWith("http") && !IsHome(core.Source))
                AddHistory(core.DocumentTitle, core.Source);
            if (t == Cur) UpdateUi();
        };
        core.SourceChanged += (_, _) =>
        {
            t.IsHome = IsHome(core.Source);
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
                    if (ms.Length > 0) { ms.Position = 0; using var img = Image.FromStream(ms); t.Icon = new Bitmap(img); }
                }
            }
            catch { }
            strip.Invalidate();
        };
        core.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(core.ContainsFullScreenElement);

        // Messages de la page d'accueil (recherche, choix du moteur)
        core.WebMessageReceived += (_, e) =>
        {
            if (!IsHome(e.Source)) return;
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "ready": SendHomeData(core); homeReady.TrySetResult(true); break;
                    case "search":
                        var u = ToUrl(root.GetProperty("q").GetString());
                        if (u != null) { core.Navigate(u); t.View.Focus(); }
                        break;
                    case "engine": SetEngine(root.GetProperty("id").GetString()); break;
                    case "city": ChangeCity(); break;
                    case "refreshMatches": _ = RefreshMatches(force: true); break;
                }
            }
            catch { }
        };

        if (forceDark) { try { await ApplyForceDark(t); } catch { } }
        core.Navigate(url ?? HomeUrl);
        if (t == Cur) UpdateUi();

        if (!extStarted)
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
        for (int k = 0; k < tabs.Count; k++)
        {
            var t = tabs[k];
            var core = t.View.CoreWebView2;
            if (k == i)
            {
                t.View.Visible = true;
                StopSuspend(t);
                if (core != null)
                {
                    core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                    if (core.IsSuspended) core.Resume();
                }
            }
            else
            {
                t.View.Visible = false;
                if (core != null && t.SuspendTimer == null)
                {
                    // Onglet en arrière-plan : RAM réduite tout de suite, puis gel complet
                    core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                    var timer = new System.Windows.Forms.Timer { Interval = SuspendDelayMs };
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
        var src = t.View.CoreWebView2?.Source;
        if (!string.IsNullOrEmpty(src) && !IsHome(src)) closedTabs.Add(src);
        StopSuspend(t);
        tabs.RemoveAt(i);
        content.Controls.Remove(t.View);
        t.View.Dispose(); // libère réellement les processus du moteur
        t.Icon?.Dispose();
        if (tabs.Count == 0) { Close(); return; }
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

    // ---------- Affichage ----------

    void UpdateUi()
    {
        var t = Cur;
        var c = Core;
        string src = c?.Source ?? "";
        bool home = t == null || t.IsHome || src == "" || src == "about:blank";

        if (!addr.Box.Focused) addr.Box.Text = home ? "" : src;
        addr.Badge.Text = home ? Glyphs.Search : src.StartsWith("https://") ? Glyphs.Lock : Glyphs.Warning;
        tip.SetToolTip(addr.Badge, home ? "" : src.StartsWith("https://") ? "Connexion sécurisée" : "Connexion non sécurisée");

        bool fav = !home && favorites.Exists(f => f.Url == src);
        addr.Star.Visible = !home;
        addr.Star.Glyph = fav ? Glyphs.StarFill : Glyphs.Star;
        addr.Star.Highlight = fav;
        addr.Star.Invalidate();

        backBtn.Enabled = c?.CanGoBack == true;
        fwdBtn.Enabled = c?.CanGoForward == true;
        reloadBtn.Glyph = t?.Loading == true ? Glyphs.Stop : Glyphs.Refresh;
        tip.SetToolTip(reloadBtn, t?.Loading == true ? "Arrêter le chargement" : "Actualiser (F5)");

        Text = t == null ? "LiteBrowser" : t.Title + " – LiteBrowser";
    }

    void UpdatePlaceholder() => addr.Box.PlaceholderText = $"Rechercher avec {Engine.Name} ou saisir une adresse";

    void ApplyTheme()
    {
        theme = themeName == "light" ? Theme.Light : themeName == "dark" ? Theme.Dark : Theme.M8;
        BackColor = theme.Strip;
        strip.Theme = theme;
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
        var c = tabs.Select(t => t.View.CoreWebView2).FirstOrDefault(x => x != null);
        if (c != null) c.Profile.PreferredColorScheme = Scheme();
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
        var methodName = Prayers.Methods.FirstOrDefault(m => m.Id == prayerMethod).Name;
        if (prayerDay == null)
            return new { city = prayerCity, error = prayerError, methodName, times = Array.Empty<object>() };
        return new
        {
            city = prayerDay.City,
            hijri = prayerDay.Hijri,
            methodName,
            times = prayerDay.Times.Select(t => new { key = t.Key, name = t.Name, time = t.Time.ToString(@"hh\:mm") }).ToArray()
        };
    }

    object[] HomeMatches() => (matches?.Items ?? new List<LiteCore.Match>()).Select(m => (object)new
    {
        game = m.Game, ts = m.Ts, t1 = m.Team1, s1 = m.Short1, t2 = m.Team2, s2 = m.Short2,
        format = m.Format, tournament = m.Tournament, url = m.Url
    }).ToArray();

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
            matches = HomeMatches(),
            matchesLoading = matches?.Loading ?? false,
            matchesUpdated = matches == null || matches.FetchedAt == default ? (long?)null : matches.FetchedAt.ToUnixTimeMilliseconds(),
            prayers = HomePrayers(),
        });
        try { core.PostWebMessageAsJson(json); } catch { }
    }

    void RefreshHomeTabs()
    {
        foreach (var t in tabs)
        {
            var c = t.View.CoreWebView2;
            if (c != null && IsHome(c.Source)) SendHomeData(c);
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
        if (s != null && suggestSet.Add(s)) suggestions.Add(s);
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
        UpdateUi();
        RefreshHomeTabs();
    }

    // ---------- Test automatique de bout en bout (lancé par la CI sur un vrai Windows) ----------

    public static readonly string SmokeReport = Environment.GetEnvironmentVariable("LITEBROWSER_SMOKE");
    readonly TaskCompletionSource<bool> homeReady = new();
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

    string Screenshot(string name)
    {
        try
        {
            Activate();
            var file = Path.Combine(Path.GetDirectoryName(SmokeReport) ?? ".", $"smoke_{name}.png");
            using var bmp = new Bitmap(Bounds.Width, Bounds.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(Bounds.Location, Point.Empty, Bounds.Size);
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            return file;
        }
        catch (Exception ex) { return "échec : " + ex.Message; }
    }

    async Task RunSmoke()
    {
        var r = new Dictionary<string, object>();
        try
        {
            r["version"] = Updater.Current.ToString();
            r["webview2"] = env?.BrowserVersionString;
            r["homeReady"] = await Task.WhenAny(homeReady.Task, Task.Delay(30_000)) == homeReady.Task;
            var home = tabs[0].View.CoreWebView2;
            await Task.Delay(1500);
            r["homeTitle"] = JsString(await home.ExecuteScriptAsync("document.title"));
            r["homeTheme"] = JsString(await home.ExecuteScriptAsync("document.body.className"));
            r["screenshotHome"] = Screenshot("home");

            if (extTask != null) await Task.WhenAny(extTask, Task.Delay(150_000));
            r["extensions"] = extensions.Select(e => new { e.Name, e.RuntimeId, e.Popup, e.Error, e.Enabled }).ToList();

            await NewTab("about:blank");
            var core = Core;
            var popups = new List<object>();
            foreach (var e in extensions.Where(x => x.RuntimeId != null && x.Popup != null))
            {
                bool ok = await NavigateAndWait(core, $"chrome-extension://{e.RuntimeId}/{e.Popup}", 20_000);
                await Task.Delay(1500);
                var text = ok ? JsString(await core.ExecuteScriptAsync("(document.body && document.body.innerText || '').slice(0,80)")) : null;
                popups.Add(new { e.Name, ok, text });
            }
            r["popups"] = popups;

            r["youtubeLoaded"] = await NavigateAndWait(core, "https://www.youtube.com/", 45_000);
            await Task.Delay(3000);
            r["youtubeAdblockActive"] = JsString(await core.ExecuteScriptAsync("String(!!window.__lbYtAdblock)")) == "true";
            r["screenshotYoutube"] = Screenshot("youtube");
            r["youtubeAdStyle"] = JsString(await core.ExecuteScriptAsync("String(!!document.getElementById('lb-yt-adblock'))")) == "true";

            // Vrai téléchargement (petit zip) : il doit aller au bout
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

            for (int i = 0; i < 40 && prayerDay == null; i++) await Task.Delay(1000);
            r["prayers"] = prayerDay?.Times.Count ?? 0;
            r["prayerCity"] = prayerDay?.City;
            r["prayerError"] = prayerError;
            r["prayerChip"] = prayerChip.Visible ? prayerChip.Text : null;

            for (int i = 0; i < 20 && (matches.Loading || matches.FetchedAt == default); i++) await Task.Delay(1000);
            r["matches"] = matches.Items.Select(m => $"{m.Game} {m.Short1} vs {m.Short2} {DateTimeOffset.FromUnixTimeSeconds(m.Ts):u}").ToList();
            r["homePrayerRows"] = JsString(await home.ExecuteScriptAsync("String(document.querySelectorAll('.prow').length)"));
        }
        catch (Exception ex) { r["exception"] = ex.ToString(); }

        try { File.WriteAllText(SmokeReport, JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        Close();
    }

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
        if (matches == null || matches.Loading) return;
        if (!force && !matches.IsStale) { RefreshHomeTabs(); return; }
        var t = matches.RefreshAsync();
        RefreshHomeTabs(); // affiche « Mise à jour… »
        await t;
    }

    // ---------- Horaires de prière ----------

    async Task RefreshPrayers(bool relocate = false)
    {
        if (prayerBusy) return;
        prayerBusy = true;
        lastPrayerTry = DateTime.Now;
        try
        {
            if (relocate || double.IsNaN(prayerLat))
            {
                if (prayerAuto)
                {
                    var loc = await Prayers.LocateByIpAsync();
                    if (loc != null) { prayerLat = loc.Value.Lat; prayerLon = loc.Value.Lon; prayerCity = loc.Value.City; SaveSettings(); }
                }
            }
            if (double.IsNaN(prayerLat)) { prayerError = "Choisis ta ville pour afficher les horaires."; return; }
            prayerDay = await Prayers.FetchAsync(DateTime.Today, prayerLat, prayerLon, prayerMethod, prayerCity);
            prayerError = null;
        }
        catch { prayerError = "Impossible de récupérer les horaires (connexion ?)."; }
        finally
        {
            prayerBusy = false;
            UpdatePrayerChip();
            RefreshHomeTabs();
        }
    }

    void UpdatePrayerChip()
    {
        if (prayerDay == null || prayerDay.Times.Count == 0)
        {
            prayerChip.Visible = false;
            LayoutToolbar();
            return;
        }
        var (name, at) = Prayers.Next(prayerDay, DateTime.Now);
        prayerChip.Text = $"☾  {name} {at:HH\\:mm}";
        var left = at - DateTime.Now;
        tip.SetToolTip(prayerChip, $"Prochaine prière : {name} à {at:HH\\:mm} (dans {(left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes:00}" : $"{Math.Max(0, (int)left.TotalMinutes)} min")})\n{prayerDay.City}");
        prayerChip.Visible = true;
        prayerChip.Invalidate();
        LayoutToolbar();
    }

    void MinuteTick()
    {
        // Nouveau jour → nouveaux horaires ; échec précédent → nouvel essai toutes les 10 min
        if (prayerDay != null && prayerDay.Date != DateTime.Today) { _ = RefreshPrayers(); return; }
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
            }
            UpdatePrayerChip();
        }
        if (matches != null && matches.IsStale && !matches.Loading) _ = RefreshMatches();
    }

    void ShowPrayerMenu()
    {
        lastMenu?.Dispose();
        var r = new MenuRenderer(theme, iconSmall);
        var font = new Font("Segoe UI", 9.5f);
        var m = new ContextMenuStrip { Renderer = r, Font = font, ShowItemToolTips = true };
        lastMenu = m;

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
            m.Items.Add(new ToolStripSeparator());
        }
        m.Items.Add(MI("Changer de ville…", ChangeCity));
        m.Items.Add(MI("Détecter ma position automatiquement", () => { prayerAuto = true; SaveSettings(); _ = RefreshPrayers(relocate: true); }));
        var meth = MI("Méthode de calcul");
        foreach (var (id, label) in Prayers.Methods)
            meth.DropDownItems.Add(MI(label, () => { prayerMethod = id; SaveSettings(); _ = RefreshPrayers(); }, null, id == prayerMethod));
        m.Items.Add(meth);
        m.Items.Add(MI("Notification à l'heure de chaque prière", () => { prayerNotify = !prayerNotify; SaveSettings(); }, null, prayerNotify));
        StyleDropDowns(m.Items, r, font);
        m.Show(prayerChip, new Point(prayerChip.Width, prayerChip.Height + S(4)), ToolStripDropDownDirection.BelowLeft);
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
        for (int i = history.Count - 1, n = 0; i >= 0 && n < 25; i--, n++) hist.DropDownItems.Add(LinkItem(history[i]));
        if (history.Count == 0) hist.DropDownItems.Add(new ToolStripMenuItem("(vide)") { Enabled = false });
        else
        {
            hist.DropDownItems.Add(new ToolStripSeparator());
            hist.DropDownItems.Add(MI("Effacer l'historique", () =>
            {
                history.Clear();
                SaveList(histPath, history);
            }));
        }
        m.Items.Add(hist);
        m.Items.Add(new ToolStripSeparator());

        // Réglages
        var eng = MI("Moteur de recherche");
        foreach (var e in Engines) { var id = e.Id; eng.DropDownItems.Add(MI(e.Name, () => SetEngine(id), null, id == engineId)); }
        m.Items.Add(eng);
        var th = MI("Thème");
        foreach (var (id, label) in new[] { ("m8", "Gentle Mates"), ("dark", "Sombre"), ("light", "Clair") })
            th.DropDownItems.Add(MI(label, () => { themeName = id; SaveSettings(); ApplyTheme(); }, null, themeName == id));
        m.Items.Add(th);
        m.Items.Add(MI("Forcer le mode sombre sur tous les sites", ToggleForceDark, null, forceDark));
        m.Items.Add(MI($"Bloquer les pubs, y compris YouTube  ({blockedCount} bloquées)", () => { adblock = !adblock; SaveSettings(); }, null, adblock));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("Plein écran", () => SetFullscreen(!fullscreen), "F11"));
        m.Items.Add(MI("Effacer les données de navigation…", ClearData));
        m.Items.Add(MI("Raccourcis clavier", ShowShortcuts));
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
        history.Clear();
        SaveList(histPath, history);
        suggestions.Clear();
        suggestSet.Clear();
        foreach (var f in favorites) AddSuggestion(f.Url);
        var c = tabs.Select(t => t.View.CoreWebView2).FirstOrDefault(x => x != null);
        try { if (c != null) await c.Profile.ClearBrowsingDataAsync(); } catch { }
    }

    void ShowShortcuts()
    {
        MessageBox.Show(this,
            "Ctrl+T\tNouvel onglet\n" +
            "Ctrl+W\tFermer l'onglet\n" +
            "Ctrl+Maj+T\tRouvrir l'onglet fermé\n" +
            "Ctrl+Tab\tOnglet suivant\n" +
            "Ctrl+1…9\tAller à l'onglet n°\n" +
            "Ctrl+L\tAller dans la barre d'adresse\n" +
            "Ctrl+D\tAjouter aux favoris\n" +
            "F5\tActualiser\n" +
            "Alt+← / →\tPage précédente / suivante\n" +
            "F11\tPlein écran\n" +
            "Ctrl+molette\tZoom\n" +
            "Clic molette\tFermer un onglet / ouvrir un lien dans un onglet",
            "Raccourcis clavier", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        forceDark = settings.TryGetValue("forcedark", out var fd) && fd == "1";
        adblock = !settings.TryGetValue("adblock", out var ab) || ab == "1";
        if (settings.TryGetValue("engine", out var en) && Engines.Any(e => e.Id == en)) engineId = en;
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
                "forcedark=" + (forceDark ? "1" : "0"),
                "adblock=" + (adblock ? "1" : "0"),
                "engine=" + engineId,
            });
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
            case Keys.Control | Keys.Shift | Keys.T: ReopenClosed(); return true;
            case Keys.Control | Keys.W:
            case Keys.Control | Keys.F4: CloseTab(sel); return true;
            case Keys.Control | Keys.L:
            case Keys.Alt | Keys.D:
            case Keys.F6: addr.Box.Focus(); addr.Box.SelectAll(); return true;
            case Keys.Control | Keys.D: ToggleFavorite(); return true;
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
