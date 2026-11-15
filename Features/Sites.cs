// Réglages par site : zoom retenu (badge « 125 % » dans la barre d'adresse, Zoom dans le menu ☰)
// et autorisations (caméra, micro, position…) demandées par une carte M8 au lieu de la boîte grise de Windows.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using LiteCore;

partial class BrowserForm
{
    SitePrefs sitePrefs;
    string sitePrefsPath;
    ZoomBadge zoomBadge;
    SitePermCard permCard;
    PermAsk permShown;
    System.Windows.Forms.Timer sitesSaveTimer;
    readonly List<PermAsk> permQueue = new();
    /// Site vers lequel un onglet est en train de naviguer : un changement de zoom pendant ce temps n'est pas noté pour l'ancien site.
    readonly Dictionary<WebView2, string> zoomNavTo = new();
    int permCardsShown, permAutoApplied; // pour le test hors écran

    sealed class PermAsk
    {
        public BrowserTab Tab;
        public CoreWebView2PermissionRequestedEventArgs Args;
        public CoreWebView2Deferral Deferral;
        public string Url, Host, Kind;
    }

    static string PermKindName(CoreWebView2PermissionKind k) => k switch
    {
        CoreWebView2PermissionKind.Camera => "camera",
        CoreWebView2PermissionKind.Microphone => "microphone",
        CoreWebView2PermissionKind.Geolocation => "geolocation",
        CoreWebView2PermissionKind.Notifications => "notifications",
        CoreWebView2PermissionKind.ClipboardRead => "clipboard",
        CoreWebView2PermissionKind.OtherSensors => "sensors",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "downloads",
        CoreWebView2PermissionKind.FileReadWrite => "files",
        CoreWebView2PermissionKind.LocalFonts => "fonts",
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "midi",
        CoreWebView2PermissionKind.WindowManagement => "windows",
        _ => null, // lecture automatique, inconnue : comportement habituel du moteur
    };

    static bool IsWebUrl(string u) => u != null && (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// Faut-il la carte M8 ? Jamais pour les pages intégrées de LiteBrowser (accueil, réglages, Nur Live et ses lecteurs).
    static bool PermCardWanted(string top, string requester) =>
        IsWebUrl(requester) && !IsLocalPage(top) && !IsLocalPage(requester) && SiteList.Host(requester) != null;

    void SitesInit()
    {
        if (sitePrefs != null) return;
        sitePrefsPath = Path.Combine(dataDir, "sites.json");
        try { sitePrefs = SitePrefs.Parse(File.Exists(sitePrefsPath) ? File.ReadAllText(sitePrefsPath) : null); }
        catch { sitePrefs = new SitePrefs(); }
        zoomBadge = new ZoomBadge(new Font("Segoe UI", 8.5f, FontStyle.Bold), () => addr.Box.BackColor) { Visible = false };
        zoomBadge.Click += (_, _) => ZoomStep(Cur, 0);
        addr.Extra = zoomBadge;
        LocationChanged += (_, _) => PlacePermCard();
        Resize += (_, _) => PlacePermCard();
    }

    void SaveSitePrefsSoon()
    {
        // Ctrl+molette envoie beaucoup de changements : un seul enregistrement, une demi-seconde après le dernier
        if (sitesSaveTimer == null)
        {
            sitesSaveTimer = new System.Windows.Forms.Timer { Interval = 500 };
            sitesSaveTimer.Tick += (_, _) => { sitesSaveTimer.Stop(); SaveSitePrefs(); };
        }
        sitesSaveTimer.Stop();
        sitesSaveTimer.Start();
    }

    void SaveSitePrefs()
    {
        sitesSaveTimer?.Stop();
        try { File.WriteAllText(sitePrefsPath, sitePrefs.Serialize()); } catch { }
        RefreshSitesPages();
    }

    /// Branché par StartTab sur chaque moteur d'onglet.
    void HookSites(BrowserTab t, CoreWebView2 core)
    {
        SitesInit();
        var view = t.View;
        // Ctrl+molette (zoom fait par le moteur). Un zoom posé par le code ne déclenche pas cet évènement : ZoomStep le note lui-même.
        view.ZoomFactorChanged += (_, _) => OnSiteZoomChanged(t, view);
        core.NavigationStarting += (_, e) =>
        {
            zoomNavTo[view] = SiteList.Host(e.Uri) ?? "";
            if (!e.IsRedirected) DropPermAsks(t); // la page s'en va : sa demande n'a plus lieu d'être
        };
        core.SourceChanged += (_, _) => { zoomNavTo.Remove(view); ApplySiteZoom(t, view); };
        core.NavigationCompleted += (_, _) => zoomNavTo.Remove(view);
        core.PermissionRequested += (_, e) => OnPermissionRequested(t, core, e);
        view.Disposed += (_, _) => zoomNavTo.Remove(view);
        ApplySiteZoom(t, view);
    }

    // ================== Zoom retenu par site ==================

    void ApplySiteZoom(BrowserTab t, WebView2 view)
    {
        var src = view.CoreWebView2?.Source;
        double want = IsWebUrl(src) && !IsLocalPage(src) ? sitePrefs.ZoomFor(src) : 1;
        try { if (Math.Abs(view.ZoomFactor - want) > 0.001) view.ZoomFactor = want; } catch { }
        if (t == Cur) UpdateSiteUi();
    }

    void OnSiteZoomChanged(BrowserTab t, WebView2 view)
    {
        var src = view.CoreWebView2?.Source;
        var host = SiteList.Host(src);
        bool moving = zoomNavTo.TryGetValue(view, out var to) && to != host;
        // pages intégrées et onglets privés : zoom possible, mais jamais retenu
        if (!moving && !t.Private && IsWebUrl(src) && !IsLocalPage(src) && sitePrefs.SetZoom(src, view.ZoomFactor))
        {
            SaveSitePrefsSoon();
            // les autres onglets du même site suivent, comme dans Chrome
            foreach (var o in tabs.Concat(detached.Select(w => w.Tab)))
                if (o != t && o.View.CoreWebView2 != null && SiteList.Host(o.View.CoreWebView2.Source) == host) ApplySiteZoom(o, o.View);
        }
        if (t == Cur) UpdateSiteUi();
    }

    /// Zoom + (1), − (-1) ou retour à 100 % (0) sur l'onglet affiché.
    void ZoomStep(BrowserTab t, int dir)
    {
        if (t?.View?.CoreWebView2 == null) return;
        double z = dir == 0 ? 1 : SitePrefs.NextZoom(t.View.ZoomFactor, dir);
        try { t.View.ZoomFactor = z; } catch { }
        OnSiteZoomChanged(t, t.View);
        UpdateSiteUi();
    }

    /// Ctrl + / − / 0 (aussi ceux du pavé numérique). Ctrl+molette passe par le moteur et ZoomFactorChanged.
    bool SiteZoomKey(Keys k)
    {
        switch (k)
        {
            case Keys.Control | Keys.Oemplus:
            case Keys.Control | Keys.Shift | Keys.Oemplus:
            case Keys.Control | Keys.Add: ZoomStep(Cur, 1); return true;
            case Keys.Control | Keys.OemMinus:
            case Keys.Control | Keys.Subtract: ZoomStep(Cur, -1); return true;
            case Keys.Control | Keys.D0:
            case Keys.Control | Keys.NumPad0: ZoomStep(Cur, 0); return true;
        }
        return false;
    }

    /// Appelé par UpdateUi : badge du zoom et carte d'autorisation de l'onglet affiché.
    void UpdateSiteUi()
    {
        SitesInit();
        var t = Cur;
        double z = 1;
        try { if (t?.View?.CoreWebView2 != null) z = t.View.ZoomFactor; } catch { }
        bool show = t?.View?.CoreWebView2 != null && !SitePrefs.IsDefaultZoom(z);
        var label = SitePrefs.ZoomLabel(z);
        zoomBadge.Theme = theme;
        bool relayout = show != zoomBadge.Visible || (show && zoomBadge.Text != label);
        if (zoomBadge.Text != label)
        {
            zoomBadge.Text = label;
            zoomBadge.Width = zoomBadge.PreferredWidth;
            tip.SetToolTip(zoomBadge, $"Zoom : {label} · clique pour revenir à 100 %");
        }
        zoomBadge.Visible = show;
        if (relayout) addr.PerformLayout();
        zoomBadge.Invalidate();
        ShowNextPerm();
    }

    /// Ligne « Zoom  −  125 %  +  Réinitialiser » du menu ☰ : les boutons ne ferment pas le menu.
    ToolStripItem ZoomMenuRow()
    {
        SitesInit();
        var font = new Font("Segoe UI", 9.5f);
        var panel = new Panel { BackColor = theme.Bar, Size = new Size(S(300), S(34)) };
        var title = new Label { Text = "Zoom", AutoSize = false, ForeColor = theme.Text, BackColor = theme.Bar, Font = font, TextAlign = ContentAlignment.MiddleLeft };
        var minus = new IconButton("", iconSmall) { Theme = theme, BackColor = theme.Bar };
        var plus = new IconButton("", iconSmall) { Theme = theme, BackColor = theme.Bar };
        var value = new Label { AutoSize = false, ForeColor = theme.Text, BackColor = theme.Bar, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
        var reset = new PillButton("Réinitialiser", new Font("Segoe UI", 8.5f)) { Subtle = true, Theme = theme, BackColor = theme.Bar };
        panel.Controls.AddRange(new Control[] { title, minus, value, plus, reset });
        tip.SetToolTip(minus, "Zoom arrière (Ctrl+−)");
        tip.SetToolTip(plus, "Zoom avant (Ctrl++)");
        tip.SetToolTip(reset, "Revenir à 100 % (Ctrl+0)");
        bool on = Core != null;
        void Sync()
        {
            double z = 1;
            try { if (Core != null) z = Cur.View.ZoomFactor; } catch { }
            value.Text = SitePrefs.ZoomLabel(z);
            minus.Enabled = on && z > SitePrefs.ZoomSteps[0] + 0.005;
            plus.Enabled = on && z < SitePrefs.ZoomSteps[^1] - 0.005;
            reset.Enabled = on && !SitePrefs.IsDefaultZoom(z);
            reset.Invalidate();
        }
        int b = S(28), y = (panel.Height - b) / 2;
        title.SetBounds(S(30), 0, S(80), panel.Height);
        minus.SetBounds(S(112), y, b, b);
        value.SetBounds(minus.Right, 0, S(58), panel.Height);
        plus.SetBounds(value.Right, y, b, b);
        int rw = reset.PreferredWidth;
        panel.Width = Math.Max(panel.Width, plus.Right + S(14) + rw + S(12));
        reset.SetBounds(panel.Width - rw - S(12), (panel.Height - S(26)) / 2, rw, S(26));
        minus.Click += (_, _) => { ZoomStep(Cur, -1); Sync(); };
        plus.Click += (_, _) => { ZoomStep(Cur, 1); Sync(); };
        reset.Click += (_, _) => { if (reset.Enabled) { ZoomStep(Cur, 0); Sync(); } };
        Sync();
        return new ToolStripControlHost(panel) { AutoSize = false, Size = panel.Size, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = theme.Bar };
    }

    // ================== Autorisations des sites ==================

    void OnPermissionRequested(BrowserTab t, CoreWebView2 core, CoreWebView2PermissionRequestedEventArgs e)
    {
        var kind = PermKindName(e.PermissionKind);
        if (kind == null || !PermCardWanted(core.Source, e.Uri)) return; // comportement habituel
        try { e.SavesInProfile = false; } catch { } // c'est LiteBrowser qui retient (et qui oublie depuis les Réglages)
        var saved = sitePrefs.PermFor(e.Uri, kind);
        if (saved != null)
        {
            e.State = saved.Value ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            permAutoApplied++;
            return;
        }
        permQueue.Add(new PermAsk { Tab = t, Args = e, Deferral = e.GetDeferral(), Url = e.Uri, Host = SiteList.Host(e.Uri), Kind = kind });
        ShowNextPerm();
    }

    void DecidePerm(PermAsk a, bool allow, bool remember)
    {
        if (!permQueue.Remove(a)) return;
        try { a.Args.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny; } catch { }
        try { a.Deferral.Complete(); } catch { }
        if (permShown == a) permShown = null;
        if (remember && !a.Tab.Private)
        {
            if (sitePrefs.SetPerm(a.Url, a.Kind, allow)) SaveSitePrefs();
            // même demande en attente ailleurs (autre onglet du site) : même réponse
            foreach (var o in permQueue.Where(o => o.Host == a.Host && o.Kind == a.Kind && !o.Tab.Private).ToList()) DecidePerm(o, allow, false);
        }
        ShowNextPerm();
    }

    void DropPermAsks(BrowserTab t)
    {
        foreach (var a in permQueue.Where(x => x.Tab == t).ToList()) DecidePerm(a, false, false);
    }

    /// Affiche la carte de la première demande de l'onglet visible (onglet affiché ou fenêtre détachée).
    void ShowNextPerm()
    {
        if (permQueue.Count == 0 && permCard is not { Visible: true }) return;
        // onglets fermés entre-temps : demande refusée, sans rien retenir
        foreach (var gone in permQueue.Where(a => !tabs.Contains(a.Tab) && a.Tab.Window == null).ToList()) DecidePerm(gone, false, false);
        var next = permQueue.FirstOrDefault(a => a.Tab.Window != null || (a.Tab == Cur && WindowState != FormWindowState.Minimized));
        if (next == null) { permShown = null; permCard?.Hide(); return; }
        if (permCard == null)
        {
            permCard = new SitePermCard(iconFont);
            permCard.Chosen += (allow, remember) => { if (permShown != null) DecidePerm(permShown, allow, remember); };
        }
        Form owner = (Form)next.Tab.Window ?? this;
        if (permShown != next)
        {
            permShown = next;
            permCard.Set(next.Host, next.Kind, next.Tab.Private, theme);
            if (permCard.Visible) permCard.Hide();
        }
        PlacePermCard();
        if (!permCard.Visible)
        {
            if (permCard.Owner != owner) permCard.Owner = owner;
            permCard.Opacity = 0;
            permCard.Show(owner);
            Fade.Start(permCard, 140);
            permCardsShown++;
        }
    }

    void PlacePermCard()
    {
        if (permCard == null || permShown == null) return;
        var w = permShown.Tab.Window;
        // sous la barre d'adresse, alignée sur son bord gauche (fenêtre détachée : sous sa barre)
        var p = w != null ? w.PointToScreen(new Point(S(110), S(50))) : addr.PointToScreen(new Point(S(4), addr.Height + S(8)));
        permCard.Location = p;
    }

    // ================== Page Réglages : « Autorisations des sites » ==================

    void SitesMessage(CoreWebView2 core, JsonElement root)
    {
        SitesInit();
        string Str(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (Str("op"))
        {
            case "permRemove":
                if (sitePrefs.RemovePerm(Str("host"), Str("kind"))) SaveSitePrefs();
                return;
            case "zoomRemove":
                var host = Str("host");
                if (host != null && sitePrefs.Zoom.Remove(host))
                {
                    SaveSitePrefs();
                    foreach (var o in tabs.Concat(detached.Select(x => x.Tab)))
                        if (o.View.CoreWebView2 != null && SiteList.Host(o.View.CoreWebView2.Source) == host) ApplySiteZoom(o, o.View);
                }
                return;
            default: SendSitesData(core); return;
        }
    }

    void SendSitesData(CoreWebView2 core)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "sitesData",
            perms = sitePrefs.Perms.Select(s => new
            {
                host = s.Key,
                items = s.Value.Select(k => new { kind = k.Key, label = SitePrefs.PermShort(k.Key), allow = k.Value }).ToArray(),
            }).ToArray(),
            zooms = sitePrefs.Zoom.Select(z => new { host = z.Key, label = SitePrefs.ZoomLabel(z.Value) }).ToArray(),
        });
        try { core.PostWebMessageAsJson(json); } catch { }
    }

    void RefreshSitesPages()
    {
        foreach (var t in tabs.Concat(detached.Select(w => w.Tab)))
        {
            var c = t.View.CoreWebView2;
            if (c != null && IsSettings(c.Source)) SendSitesData(c);
        }
    }

    // ================== Test hors écran (LITEBROWSER_SHOTS_PAGES=sites=sites) ==================

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    /// Molette vers le haut avec Ctrl, postée à la fenêtre de rendu de la page (sans focus ni souris réelle).
    static void WheelWithCtrl(WebView2 view)
    {
        IntPtr target = IntPtr.Zero;
        EnumChildWindows(view.Handle, (h, _) =>
        {
            var sb = new System.Text.StringBuilder(64);
            GetClassName(h, sb, 64);
            if (sb.ToString() == "Chrome_RenderWidgetHostHWND") { target = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (target == IntPtr.Zero) return;
        var p = view.PointToScreen(new Point(200, 200));
        PostMessage(target, 0x020A, (IntPtr)((120 << 16) | 0x0008), (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)));
    }

    async Task SitesTest(string name)
    {
        var r = new List<string>();
        void C(string what, bool ok, string d = "")
        {
            r.Add((ok ? "OK    " : "ÉCHEC ") + what + (d.Length > 0 ? " · " + d : ""));
            File.WriteAllLines(Path.Combine(ShotsDir, name + ".txt"), r);
        }
        async Task<string> Js(CoreWebView2 c, string s) { var p = c.ExecuteScriptAsync(s); return await Task.WhenAny(p, Task.Delay(4000)) == p ? p.Result : "(pas de réponse)"; }
        async Task<bool> Until(Func<bool> f, int ms = 6000) { for (int i = 0; i < ms / 100; i++) { if (f()) return true; await Task.Delay(100); } return f(); }
        async Task<string> JsUntil(CoreWebView2 c, string s, int ms = 6000) { string v = "null"; for (int i = 0; i < ms / 200; i++) { v = await Js(c, s); if (v != "null" && v != "\"\"" && v != "(pas de réponse)") return v; await Task.Delay(200); } return v; }
        async Task CardShot(string file)
        {
            var path = Path.Combine(ShotsDir, file + ".png");
            await ShotAsync(file, path);
            if (permCard is not { Visible: true }) return;
            try
            {
                Bitmap page;
                using (var tmp = Image.FromFile(path)) page = new Bitmap(tmp);
                using (page)
                {
                    using var cb = new Bitmap(permCard.Width, permCard.Height);
                    permCard.DrawToBitmap(cb, new Rectangle(Point.Empty, permCard.Size));
                    using (var g = Graphics.FromImage(page)) g.DrawImage(cb, new Point(permCard.Left - Left, permCard.Top - Top));
                    page.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception ex) { r.Add("(capture de la carte : " + ex.Message + ")"); }
        }
        try
        {
            SitesInit();
            // ---------- 1. Zoom retenu par site ----------
            await NewTab("https://example.com/");
            await Until(() => Core?.Source?.Contains("example.com") == true, 15000);
            await Task.Delay(1500);
            var ex = Cur;
            // vraie molette avec Ctrl enfoncé, envoyée à la page : c'est le moteur qui zoome (évènement ZoomFactorChanged)
            try { await ex.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseWheel\",\"x\":300,\"y\":300,\"deltaX\":0,\"deltaY\":-120,\"modifiers\":2}"); } catch { }
            await Task.Delay(800);
            if (ex.View.ZoomFactor < 1.01) { WheelWithCtrl(ex.View); await Task.Delay(800); } // sinon : vrai message molette Windows (Ctrl) à la fenêtre de la page
            double z1 = ex.View.ZoomFactor;
            if (z1 > 1.01)
                C("Ctrl+molette : le moteur zoome, le zoom est retenu pour le site", Math.Abs(sitePrefs.ZoomFor("https://example.com/") - z1) < 0.001, $"page {SitePrefs.ZoomLabel(z1)}, retenu {SitePrefs.ZoomLabel(sitePrefs.ZoomFor("https://example.com/"))}");
            else
            {
                r.Add("NOTE  Ctrl+molette simulée sans effet sur le moteur : zoom fait par Ctrl++");
                SiteZoomKey(Keys.Control | Keys.Oemplus); await Task.Delay(300); z1 = ex.View.ZoomFactor;
            }
            C($"badge « {SitePrefs.ZoomLabel(z1)} » dans la barre d'adresse", zoomBadge.Visible && zoomBadge.Text == SitePrefs.ZoomLabel(z1) && zoomBadge.Width > 0 && zoomBadge.Right <= addr.Shield.Left + 1,
                $"visible={zoomBadge.Visible} texte={zoomBadge.Text} x={zoomBadge.Left}..{zoomBadge.Right} bouclier={addr.Shield.Left}, champ jusqu'à {addr.Box.Right}");
            C("le texte de l'adresse ne passe pas sous le badge", addr.Box.Right <= zoomBadge.Left, $"{addr.Box.Right} ≤ {zoomBadge.Left}");
            await ShotAsync(name + "_0_badge_molette");
            while (ex.View.ZoomFactor < 1.49) SiteZoomKey(Keys.Control | Keys.Oemplus); // Ctrl++ jusqu'à 150 %
            await Task.Delay(400);
            C("Ctrl++ : niveaux suivants jusqu'à 150 %, retenu", Math.Abs(ex.View.ZoomFactor - 1.5) < 0.01 && sitePrefs.ZoomFor("https://example.com/") == 1.5 && zoomBadge.Text == SitePrefs.ZoomLabel(1.5), SitePrefs.ZoomLabel(ex.View.ZoomFactor));
            await ShotAsync(name + "_1_badge");
            await Task.Delay(800);
            C("enregistré dans sites.json", File.Exists(sitePrefsPath) && File.ReadAllText(sitePrefsPath).Contains("example.com"), File.Exists(sitePrefsPath) ? File.ReadAllText(sitePrefsPath).Replace("\r", "").Replace("\n", " ") : "absent");

            await NavigateAndWait(ex.View.CoreWebView2, "https://example.org/", 15000);
            await Task.Delay(500);
            C("autre site : 100 %, badge caché", SitePrefs.IsDefaultZoom(ex.View.ZoomFactor) && !zoomBadge.Visible, SitePrefs.ZoomLabel(ex.View.ZoomFactor));
            C("le zoom d'example.com n'a pas été écrasé en partant", sitePrefs.ZoomFor("https://example.com/") == 1.5, SitePrefs.ZoomLabel(sitePrefs.ZoomFor("https://example.com/")));
            await NavigateAndWait(ex.View.CoreWebView2, "https://example.com/", 15000);
            await Task.Delay(500);
            C("retour sur example.com : 150 % réappliqué", Math.Abs(ex.View.ZoomFactor - 1.5) < 0.01 && zoomBadge.Visible, SitePrefs.ZoomLabel(ex.View.ZoomFactor));
            await NewTab("https://example.com/");
            await Until(() => Core?.Source?.Contains("example.com") == true, 15000);
            await Task.Delay(800);
            C("nouvel onglet sur le même site : 150 %", Math.Abs(Cur.View.ZoomFactor - 1.5) < 0.01, SitePrefs.ZoomLabel(Cur.View.ZoomFactor));
            var ex2 = Cur;
            await NavigateAndWait(ex2.View.CoreWebView2, "https://example.org/", 15000);
            await Task.Delay(400);
            SiteZoomKey(Keys.Control | Keys.OemMinus);
            await Task.Delay(400);
            C("Ctrl+− sur example.org : 90 %", Math.Abs(ex2.View.ZoomFactor - 0.9) < 0.01 && sitePrefs.ZoomFor("https://example.org/") == 0.9, SitePrefs.ZoomLabel(ex2.View.ZoomFactor));
            await ShotAsync(name + "_2_badge_90");
            ShowMenu();
            await Task.Delay(700);
            var row = lastMenu?.Items.OfType<ToolStripControlHost>().FirstOrDefault();
            C("menu ☰ : ligne Zoom présente", row != null, row == null ? "absente" : $"{row.Width}×{row.Height}");
            if (lastMenu is { Visible: true }) await ShotAsync(name + "_3_menu");
            if (row != null)
            {
                var panel = row.Control;
                var kids = panel.Controls.Cast<Control>().ToList();
                C("menu ☰ : rien ne se chevauche dans la ligne Zoom", kids.All(a => kids.All(b => a == b || !a.Bounds.IntersectsWith(b.Bounds))) && kids.All(k => k.Right <= panel.Width),
                    string.Join(" ", kids.Select(k => $"{k.GetType().Name}:{k.Left}-{k.Right}")) + $" / {panel.Width}");
                using (var bmp = new Bitmap(panel.Width, panel.Height)) { panel.DrawToBitmap(bmp, new Rectangle(Point.Empty, panel.Size)); bmp.Save(Path.Combine(ShotsDir, name + "_3_menu_zoom.png")); }
                var value = panel.Controls.OfType<Label>().Last();
                var plusBtn = panel.Controls.OfType<IconButton>().Last();
                C("menu ☰ : la ligne affiche le zoom de la page", value.Text == SitePrefs.ZoomLabel(0.9), value.Text);
                plusBtn.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(plusBtn, new object[] { EventArgs.Empty });
                await Task.Delay(300);
                C("menu ☰ : bouton + (le menu reste ouvert)", Math.Abs(ex2.View.ZoomFactor - 1.0) < 0.01 && value.Text == SitePrefs.ZoomLabel(1.0), $"{SitePrefs.ZoomLabel(ex2.View.ZoomFactor)}, affiché {value.Text}");
                SiteZoomKey(Keys.Control | Keys.OemMinus); await Task.Delay(200);
            }
            lastMenu?.Close();
            await Task.Delay(200);
            ZoomStep(Cur, 0); // = clic sur le badge
            await Task.Delay(400);
            C("clic sur le badge : retour à 100 %, site oublié", SitePrefs.IsDefaultZoom(ex2.View.ZoomFactor) && !sitePrefs.Zoom.ContainsKey("example.org") && !zoomBadge.Visible);
            C("pages intégrées : pas de carte d'autorisation", !PermCardWanted(SettingsUrl, SettingsUrl) && !PermCardWanted(NurLive.Url, "https://player.twitch.tv/?channel=x") && PermCardWanted("https://www.youtube.com/", "https://www.youtube.com/"));

            // ---------- 2. Autorisations ----------
            SelectTab(tabs.IndexOf(ex));
            await Task.Delay(500);
            var c = ex.View.CoreWebView2;
            int shown0 = permCardsShown;
            await Js(c, "window.__n=null;Notification.requestPermission().then(x=>window.__n=x)");
            await Until(() => permCard is { Visible: true }, 6000);
            C("notifications : la carte M8 apparaît", permCard is { Visible: true } && permShown?.Kind == "notifications" && permCardsShown == shown0 + 1,
                permShown != null ? $"{permShown.Host} {SitePrefs.PermAsk(permShown.Kind)}" : "pas de carte");
            if (permCard != null)
            {
                var cardAt = permCard.Location; var addrAt = addr.PointToScreen(new Point(0, addr.Height));
                C("la carte est juste sous la barre d'adresse", cardAt.Y >= addrAt.Y && cardAt.Y - addrAt.Y < S(20) && Math.Abs(cardAt.X - addrAt.X) < S(20), $"carte {cardAt}, barre {addrAt}");
                C("la carte ne prend pas le focus", !permCard.ContainsFocus && !permCard.Focused);
            }
            await CardShot(name + "_4_carte");
            C("« Retenir pour ce site » coché par défaut", permCard?.Remember == true);
            permCard?.Choose(true);
            var nres = await JsUntil(c, "window.__n");
            C("Autoriser : la page reçoit « granted »", nres.Contains("granted"), nres);
            C("décision retenue pour example.com", sitePrefs.PermFor("https://example.com/", "notifications") == true);
            C("la carte se referme", permCard is not { Visible: true });
            int auto0 = permAutoApplied;
            await Js(c, "window.__n=null;Notification.requestPermission().then(x=>window.__n=x)");
            nres = await JsUntil(c, "window.__n");
            C("2e demande : appliquée directement, sans carte", nres.Contains("granted") && permAutoApplied == auto0 + 1 && permCard is not { Visible: true }, $"{nres}, auto={permAutoApplied - auto0}");

            // position : bloquée et retenue (on n'autorise pas, pour ne pas réveiller la localisation de Windows)
            await Js(c, "window.__g=null;navigator.geolocation.getCurrentPosition(p=>window.__g='ok',e=>window.__g='err'+e.code,{timeout:4000})");
            await Until(() => permCard is { Visible: true }, 6000);
            C("position : carte « veut connaître ta position »", permShown?.Kind == "geolocation", permShown?.Kind ?? "pas de carte");
            await CardShot(name + "_5_carte_position");
            permCard?.Choose(false);
            var gres = await JsUntil(c, "window.__g");
            C("Bloquer : la page reçoit un refus (code 1)", gres.Contains("err1"), gres);
            C("blocage retenu pour example.com", sitePrefs.PermFor("https://example.com/", "geolocation") == false);
            auto0 = permAutoApplied;
            await Js(c, "window.__g=null;navigator.geolocation.getCurrentPosition(p=>window.__g='ok',e=>window.__g='err'+e.code,{timeout:4000})");
            gres = await JsUntil(c, "window.__g");
            C("2e demande de position : refusée directement", gres.Contains("err1") && permAutoApplied == auto0 + 1 && permCard is not { Visible: true }, gres);

            // caméra : bloquée sans retenir
            await Js(c, "window.__c=null;navigator.mediaDevices.getUserMedia({video:true}).then(s=>{s.getTracks().forEach(t=>t.stop());window.__c='ok'},e=>window.__c=e.name)");
            bool camCard = await Until(() => permCard is { Visible: true }, 5000);
            if (camCard)
            {
                C("caméra : carte « veut utiliser ta caméra »", permShown?.Kind == "camera", permShown?.Kind);
                permCard.Remember = false;
                await CardShot(name + "_6_carte_camera_decochee");
                permCard.Choose(false);
                var cres = await JsUntil(c, "window.__c");
                C("Bloquer sans retenir : refus, rien n'est noté", cres.Contains("NotAllowed") && sitePrefs.PermFor("https://example.com/", "camera") == null, cres);
            }
            else r.Add("NOTE  caméra : aucune demande du moteur (pas de caméra sur ce PC ?) · " + await JsUntil(c, "window.__c", 3000));

            // une demande en attente disparaît quand la page change
            await Js(c, "window.__m=null;navigator.mediaDevices.getUserMedia({audio:true}).then(s=>{s.getTracks().forEach(t=>t.stop());window.__m='ok'},e=>window.__m=e.name)");
            if (await Until(() => permCard is { Visible: true }, 5000))
            {
                C("micro : carte « veut utiliser ton micro »", permShown?.Kind == "microphone", permShown?.Kind);
                // changement d'onglet : la carte se cache, et revient avec l'onglet
                SelectTab(tabs.IndexOf(ex2)); await Task.Delay(300);
                C("autre onglet affiché : la carte se cache", permCard is not { Visible: true } && permQueue.Count == 1);
                SelectTab(tabs.IndexOf(ex)); await Task.Delay(300);
                C("retour sur l'onglet : la carte revient", permCard is { Visible: true } && permShown?.Kind == "microphone");
                await NavigateAndWait(c, "https://example.net/", 15000);
                await Task.Delay(300);
                C("page quittée : demande abandonnée, carte fermée", permQueue.Count == 0 && permCard is not { Visible: true } && sitePrefs.PermFor("https://example.com/", "microphone") == null);
            }
            else r.Add("NOTE  micro : aucune demande du moteur (pas de micro sur ce PC ?)");
            await NavigateAndWait(c, "https://example.com/", 15000); // reste ouvert à 150 % pour l'essai des Réglages
            await Task.Delay(400);

            // ---------- 3. Réglages : « Autorisations des sites » ----------
            sitePrefs.SetZoom("https://fr.wikipedia.org/", 1.1); SaveSitePrefs();
            OpenSettings("sites");
            await Until(() => Core != null && IsSettings(Core.Source), 10000);
            var sc = Core;
            var rows = await JsUntil(sc, "document.querySelectorAll('#sitePerms .pchip').length || null", 8000);
            C("Réglages : choix retenus listés", rows == "2", rows + " choix · " + await Js(sc, "document.getElementById('sitePerms').innerText.replace(/\\s+/g,' ')"));
            var zr = await Js(sc, "document.querySelectorAll('#siteZooms .it').length");
            C("Réglages : zooms retenus listés", zr == "2", zr + " · " + await Js(sc, "document.getElementById('siteZooms').innerText.replace(/\\s+/g,' ')"));
            await Js(sc, "document.getElementById('sites').scrollIntoView()");
            await Task.Delay(600);
            await ShotAsync(name + "_7_reglages");
            await Js(sc, "document.querySelector('#sitePerms [data-kind=\"geolocation\"]').click()");
            await Until(() => sitePrefs.PermFor("https://example.com/", "geolocation") == null, 3000);
            C("Réglages : retirer un choix", sitePrefs.PermFor("https://example.com/", "geolocation") == null && sitePrefs.PermFor("https://example.com/", "notifications") == true);
            var left = await JsUntil(sc, "document.querySelectorAll('#sitePerms .pchip').length===1 || null", 3000);
            C("la liste se met à jour", left == "true", left);
            await Js(sc, "document.querySelector('#siteZooms [data-zoom=\"example.com\"]').click()");
            await Until(() => !sitePrefs.Zoom.ContainsKey("example.com"), 3000);
            await Task.Delay(400);
            C("Réglages : revenir à 100 % pour un site (onglets ouverts compris)", !sitePrefs.Zoom.ContainsKey("example.com") && SitePrefs.IsDefaultZoom(ex.View.ZoomFactor) && ex.View.CoreWebView2.Source.Contains("example.com"),
                $"onglet {ex.View.CoreWebView2.Source} à {SitePrefs.ZoomLabel(ex.View.ZoomFactor)}");
            await Task.Delay(500);
            await ShotAsync(name + "_8_reglages_apres");
            var saved = File.ReadAllText(sitePrefsPath);
            var back = SitePrefs.Parse(saved);
            C("fichier sites.json relu : identique", back.Serialize() == sitePrefs.Serialize(), saved.Replace("\r", "").Replace("\n", " "));
        }
        catch (Exception e) { C("test interrompu", false, e.ToString()); }
    }
}

/// Badge du zoom dans la barre d'adresse (« 125 % »). Clic = retour à 100 %.
sealed class ZoomBadge : Control
{
    readonly Func<Color> back;
    bool hover;
    public Theme Theme = Theme.M8;

    public ZoomBadge(Font font, Func<Color> back)
    {
        Font = font; this.back = back;
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    public int PreferredWidth => TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width + LogicalToDeviceUnits(16);
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bg = back();
        g.Clear(bg);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var p = G.Round(r, r.Height / 2);
        using (var b = new SolidBrush(G.Mix(Theme.Accent, bg, hover ? .62f : .80f))) g.FillPath(b, p);
        using (var pen = new Pen(G.Mix(Theme.Accent, bg, hover ? .15f : .45f))) g.DrawPath(pen, p);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, hover ? Theme.Text : Theme.Accent, G.Center);
    }
}

/// Carte « youtube.com veut utiliser ton micro » sous la barre d'adresse. Ne prend jamais le focus.
sealed class SitePermCard : Form
{
    public event Action<bool, bool> Chosen; // (autoriser, retenir)
    public bool Remember = true;
    readonly Font glyphFont, hostFont, textFont, btnFont, smallFont;
    string host = "", ask = "", glyph = "";
    bool priv;
    Theme theme = Theme.M8;
    int hover; // 0 rien, 1 retenir, 2 bloquer, 3 autoriser
    Rectangle rRemember, rBlock, rAllow;

    public SitePermCard(Font icons)
    {
        glyphFont = new Font(icons.FontFamily, 13f);
        hostFont = new Font("Segoe UI Semibold", 10.5f);
        textFont = new Font("Segoe UI", 10f);
        btnFont = new Font("Segoe UI Semibold", 9.5f);
        smallFont = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            cp.ClassStyle |= 0x20000;        // CS_DROPSHADOW
            return cp;
        }
    }
    const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; } // un clic ne vole pas le focus à la page
        base.WndProc(ref m);
    }

    int Sc(int v) => LogicalToDeviceUnits(v);

    static string GlyphFor(string kind) => kind switch
    {
        "camera" => "",
        "microphone" => "",
        "geolocation" => "",
        "notifications" => "",
        "clipboard" => "",
        "downloads" => "",
        "files" => "",
        "fonts" => "",
        "windows" => "",
        _ => "",
    };

    public void Set(string host, string kind, bool priv, Theme t)
    {
        this.host = host ?? ""; ask = SitePrefs.PermAsk(kind); glyph = GlyphFor(kind); this.priv = priv; theme = t;
        Remember = !priv;
        hover = 0;
        int w = Sc(360), pad = Sc(16), textX = pad + Sc(40) + Sc(12), inner = w - textX - pad;
        int hostH = Math.Min(TextRenderer.MeasureText(this.host, hostFont, new Size(inner, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height, hostFont.Height * 2);
        int askH = TextRenderer.MeasureText(ask, textFont, new Size(inner, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        int top = pad + Math.Max(Sc(40), hostH + Sc(2) + askH);
        rRemember = new Rectangle(textX, top + Sc(12), inner, Sc(22));
        int by = rRemember.Bottom + Sc(14), bh = Sc(34);
        int aw = TextRenderer.MeasureText("Autoriser", btnFont).Width + Sc(34), bw = TextRenderer.MeasureText("Bloquer", btnFont).Width + Sc(34);
        rAllow = new Rectangle(w - pad - aw, by, aw, bh);
        rBlock = new Rectangle(rAllow.Left - Sc(8) - bw, by, bw, bh);
        int h = by + bh + pad;
        Size = new Size(w, h);
        using (var path = G.Round(new RectangleF(0, 0, w, h), Sc(12))) Region = new Region(path);
        BackColor = theme.Bar;
        Invalidate();
    }

    public void Choose(bool allow) => Chosen?.Invoke(allow, Remember && !priv);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(theme.Bar);
        using (var path = G.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Sc(12)))
        using (var pen = new Pen(theme.FieldBorder)) g.DrawPath(pen, path);
        // fine ligne rose en haut : la carte vient de LiteBrowser, pas du site
        using (var top = new SolidBrush(theme.Accent)) g.FillRectangle(top, Sc(16), 0, Width - Sc(32), Math.Max(2, Sc(2)));
        int pad = Sc(16), textX = pad + Sc(40) + Sc(12), inner = Width - textX - pad;
        var ic = new Rectangle(pad, pad, Sc(40), Sc(40));
        using (var b = new SolidBrush(G.Mix(theme.Accent, theme.Bar, .82f))) g.FillEllipse(b, ic);
        TextRenderer.DrawText(g, glyph, glyphFont, ic, theme.Accent, G.Center);
        const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl;
        int hostH = Math.Min(TextRenderer.MeasureText(host, hostFont, new Size(inner, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height, hostFont.Height * 2);
        int askH = TextRenderer.MeasureText(ask, textFont, new Size(inner, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        int y = pad + Math.Max(0, (Sc(40) - (hostH + Sc(2) + askH)) / 2);
        TextRenderer.DrawText(g, host, hostFont, new Rectangle(textX, y, inner, hostH), theme.Text, wrap);
        TextRenderer.DrawText(g, ask, textFont, new Rectangle(textX, y + hostH + Sc(2), inner, askH), theme.Sub, wrap);

        // « Retenir pour ce site » (un onglet privé ne retient jamais rien)
        if (priv)
            TextRenderer.DrawText(g, "Onglet privé : ce choix ne sera pas retenu.", smallFont, rRemember, theme.Sub, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        else
        {
            var box = new RectangleF(rRemember.X + 0.5f, rRemember.Y + (rRemember.Height - Sc(16)) / 2f, Sc(16), Sc(16));
            using (var bp = G.Round(box, Sc(4)))
            {
                if (Remember) { using var fb = new SolidBrush(theme.Accent); g.FillPath(fb, bp); }
                else { using var fb = new SolidBrush(theme.Field); g.FillPath(fb, bp); }
                using var pen = new Pen(Remember ? theme.Accent : hover == 1 ? theme.Sub : theme.FieldBorder);
                g.DrawPath(pen, bp);
            }
            if (Remember)
            {
                using var pen = new Pen(theme.Accent.GetBrightness() > .6f ? Color.FromArgb(10, 10, 12) : Color.White, Math.Max(1.6f, Sc(2) * .9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(pen, new[] { new PointF(box.X + box.Width * .24f, box.Y + box.Height * .52f), new PointF(box.X + box.Width * .43f, box.Y + box.Height * .70f), new PointF(box.X + box.Width * .76f, box.Y + box.Height * .32f) });
            }
            TextRenderer.DrawText(g, "Retenir pour ce site", smallFont, new Rectangle(rRemember.X + Sc(24), rRemember.Y, rRemember.Width - Sc(24), rRemember.Height),
                hover == 1 ? theme.Text : G.Mix(theme.Sub, theme.Text, .35f), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        // Bloquer (discret) · Autoriser (rose M8)
        using (var bp = G.Round(new RectangleF(rBlock.X + 0.5f, rBlock.Y + 0.5f, rBlock.Width - 1, rBlock.Height - 1), rBlock.Height / 2f))
        {
            using (var fb = new SolidBrush(hover == 2 ? theme.Hover : theme.Field)) g.FillPath(fb, bp);
            using var pen = new Pen(theme.FieldBorder); g.DrawPath(pen, bp);
        }
        TextRenderer.DrawText(g, "Bloquer", btnFont, rBlock, theme.Text, G.Center);
        var fill = hover == 3 ? G.Mix(theme.Accent, theme.Text, .15f) : theme.Accent;
        using (var ap = G.Round(new RectangleF(rAllow.X + 0.5f, rAllow.Y + 0.5f, rAllow.Width - 1, rAllow.Height - 1), rAllow.Height / 2f))
        using (var fb = new SolidBrush(fill)) g.FillPath(fb, ap);
        TextRenderer.DrawText(g, "Autoriser", btnFont, rAllow, fill.GetBrightness() > .6f ? Color.FromArgb(10, 10, 12) : Color.White, G.Center);
    }

    int HitAt(Point p) => rAllow.Contains(p) ? 3 : rBlock.Contains(p) ? 2 : !priv && new Rectangle(rRemember.X, rRemember.Y, Sc(24) + TextRenderer.MeasureText("Retenir pour ce site", smallFont).Width, rRemember.Height).Contains(p) ? 1 : 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitAt(e.Location);
        if (h != hover) { hover = h; Invalidate(); }
        Cursor = h > 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != 0) { hover = 0; Invalidate(); } }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        switch (HitAt(e.Location))
        {
            case 1: Remember = !Remember; Invalidate(); break;
            case 2: Choose(false); break;
            case 3: Choose(true); break;
        }
    }
}
