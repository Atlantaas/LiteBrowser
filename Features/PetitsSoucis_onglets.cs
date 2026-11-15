// ======================================================================
//  Petits soucis « onglets et fenêtres » (ce qui agace au quotidien et fait « prototype »)
// ======================================================================
//  1. Raccourcis des onglets muets quand la page a le focus : une touche tapée dans une page arrive dans le moteur
//     (autre processus), qui la rend au contrôle WebView2 (KeyDown) ; personne ne l'écoutait, donc Ctrl+T, Ctrl+W,
//     Ctrl+Tab, Ctrl+1…9, Ctrl+Maj+T, F11, Ctrl+L… ne marchaient que depuis la barre d'adresse.
//  2. Ctrl+Pg.suiv / Ctrl+Pg.préc (onglet suivant / précédent) et Ctrl+Maj+Pg.suiv / Pg.préc (déplacer l'onglet), comme Chrome.
//  3. Fermetures en rafale : après un clic sur ✕, les onglets gardent leur largeur tant que la souris reste sur la barre
//     (la croix suivante arrive sous la souris), puis reprennent leur place en glissant.
//  4. « Fermer les autres onglets » / « à droite » : les onglets épinglés restent, l'onglet choisi reste affiché,
//     et aucun onglet voisin n'est chargé pour rien pendant la fermeture.
//  5. Ctrl+Maj+T : l'onglet revient à sa place, épinglé s'il l'était (avant : à côté de l'onglet affiché).
//  6. « Dupliquer » : la copie s'ouvre juste à droite de l'onglet dupliqué ; marche aussi sur un onglet pas encore chargé.
//  7. Session : l'onglet affiché au démarrage est retrouvé par sa place (RestoreSession, Program.cs).
//  8. Fenêtre principale : taille, position, écran et « agrandie » retenus d'une fois sur l'autre, ramenée sur un écran présent.
//  9. Fenêtre réduite rouverte (lien d'une autre appli, onglet remis) : revient comme elle était (agrandie ou non).
// 10. Fenêtre détachée lâchée sur la barre d'onglets : l'onglet revient là où tu l'as lâché ; un onglet épinglé revient
//     avec les épinglés (avant : au milieu des autres, sans titre).
// 11. Fenêtre détachée : Ctrl+T, Ctrl+Maj+T et Ctrl+Maj+N ouvrent dans la fenêtre principale (avant : rien).
// 12. F11 : une petite notification dit comment sortir du plein écran (jamais sur Nur Live : rien sur un lecteur Twitch).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using LiteCore;

partial class TabStrip
{
    /// Largeur figée après une fermeture à la souris (0 = largeur normale).
    int closeFreezeW;
    public bool CloseFrozen => closeFreezeW > 0;

    void FreezeForClose(int i)
    {
        if (closeFreezeW > 0 || i < PinnedCount() || i >= Tabs.Count) return;
        closeFreezeW = Math.Max(1, (int)Math.Round(CurWidth()));
    }

    /// La souris a quitté la barre (ou la fenêtre change de taille, un onglet s'ouvre) : largeur normale, en glissant.
    public void EndCloseFreeze()
    {
        if (closeFreezeW <= 0) return;
        curW = closeFreezeW;
        closeFreezeW = 0;
        anim.Start();
    }
}

partial class BrowserForm
{
    // ---------- 1. Raccourcis depuis la page ----------

    readonly HashSet<WebView2> pageKeysHooked = new();

    void HookPageKeys(WebView2 v)
    {
        if (v == null || !pageKeysHooked.Add(v)) return;
        v.Disposed += (_, _) => pageKeysHooked.Remove(v);
        v.KeyDown += (_, e) => { if (PageShortcut(v, e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; } };
    }

    /// Raccourcis de la fenêtre que la page ne garde pas pour elle : seulement onglets, plein écran et barre d'adresse
    /// (ce que Chrome et Brave ne laissent pas aux sites). Tout le reste va à la page, comme avant : Ctrl+H (Docs :
    /// rechercher et remplacer), Ctrl+, (Docs : indice), Ctrl+Maj+L (Docs : aligner à gauche), Ctrl+Maj+M / Ctrl+Maj+D
    /// (Discord : micro / casque), Ctrl+Maj+A (Slack), Ctrl+D, Ctrl+E, Ctrl+K, Ctrl+M… et ce que le moteur fait déjà
    /// lui-même (F5, Alt+←/→, zoom, Ctrl+F, Ctrl+P). AltGr (Ctrl+Alt) n'est jamais pris : é, @, € s'écrivent.
    static bool TabShortcutFromPage(Keys k)
    {
        switch (k)
        {
            case Keys.Control | Keys.T:
            case Keys.Control | Keys.Shift | Keys.T:
            case Keys.Control | Keys.Shift | Keys.N:
            case Keys.Control | Keys.W:
            case Keys.Control | Keys.F4:
            case Keys.Control | Keys.Tab:
            case Keys.Control | Keys.Shift | Keys.Tab:
            case Keys.Control | Keys.PageUp:
            case Keys.Control | Keys.PageDown:
            case Keys.Control | Keys.Shift | Keys.PageUp:
            case Keys.Control | Keys.Shift | Keys.PageDown:
            case Keys.F11:
            case Keys.F2: // palette de commandes
            case Keys.Control | Keys.L:
            case Keys.Alt | Keys.D:
            case Keys.F6:
            case Keys.Alt | Keys.Home:
                return true;
        }
        var code = k & Keys.KeyCode;
        return (k & Keys.Modifiers) == Keys.Control && code >= Keys.D1 && code <= Keys.D9;
    }

    /// Comme Chrome : les autres raccourcis de LiteBrowser vont d'abord à la page ; si elle ne s'en sert pas
    /// (pas de preventDefault), LiteBrowser les fait. Docs et Discord gardent donc les leurs, Ctrl+D marche partout ailleurs.
    /// Ctrl+F, Ctrl+P, F5, zoom, Alt+←/→ restent au moteur. Lettres lues par e.key : justes en AZERTY.
    internal const string PageKeysScript = @"(() => {
  if (!window.chrome || !chrome.webview || window.top !== window) return;
  const keys = { c: 'dhjkem,', cs: 'blamdxs', a: 'p' };
  addEventListener('keydown', e => {
    if (e.repeat || e.metaKey || !e.isTrusted) return;
    const m = (e.ctrlKey ? 'c' : '') + (e.shiftKey ? 's' : '') + (e.altKey ? 'a' : '');
    const k = (e.key || '').toLowerCase();
    if (k.length !== 1 || !(keys[m] || '').includes(k)) return;
    setTimeout(() => { if (!e.defaultPrevented) chrome.webview.postMessage('lbkey:' + m + ':' + k); }, 0);
  });
})();";

    /// Raccourci laissé par la page (message de PageKeysScript). Seulement les touches de la liste, jamais de quoi effacer des données.
    bool PageKeyMessage(BrowserTab t, string msg)
    {
        if (msg == null || !msg.StartsWith("lbkey:")) return false;
        var p = msg.Split(':');
        if (p.Length != 3 || p[2].Length != 1 || t.Window != null || t != Cur) return true;
        char c = p[2][0];
        Keys k = c == ',' ? Keys.Oemcomma : c is >= 'a' and <= 'z' ? Keys.A + (c - 'a') : Keys.None;
        if (k == Keys.None) return true;
        if (p[1].Contains('c')) k |= Keys.Control;
        if (p[1].Contains('s')) k |= Keys.Shift;
        if (p[1].Contains('a')) k |= Keys.Alt;
        if (!PageFallbackKey(k)) return true;
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || t != Cur) return;
            var m = Message.Create(Handle, 0x0100, (IntPtr)(int)(k & Keys.KeyCode), IntPtr.Zero);
            ProcessCmdKey(ref m, k);
        }));
        return true;
    }

    static bool PageFallbackKey(Keys k) => k is
        (Keys.Control | Keys.D) or (Keys.Control | Keys.H) or (Keys.Control | Keys.J) or (Keys.Control | Keys.K) or
        (Keys.Control | Keys.E) or (Keys.Control | Keys.M) or (Keys.Control | Keys.Oemcomma) or
        (Keys.Control | Keys.Shift | Keys.B) or (Keys.Control | Keys.Shift | Keys.L) or (Keys.Control | Keys.Shift | Keys.A) or
        (Keys.Control | Keys.Shift | Keys.M) or (Keys.Control | Keys.Shift | Keys.D) or (Keys.Control | Keys.Shift | Keys.X) or
        (Keys.Control | Keys.Shift | Keys.S) or (Keys.Alt | Keys.P);

    static bool MainOnlyKey(Keys k) => k is (Keys.Control | Keys.T) or (Keys.Control | Keys.Shift | Keys.T) or (Keys.Control | Keys.Shift | Keys.N);
    static bool DetachedOwnKey(Keys k) => k is (Keys.Control | Keys.W) or (Keys.Control | Keys.L) or Keys.F6;

    /// Touche tapée dans une page. Le moteur attend la réponse : on dit tout de suite « pris », l'action suit juste après.
    bool PageShortcut(WebView2 v, Keys k)
    {
        if (IsDisposed || v.IsDisposed) return false;
        if (v.FindForm() is DetachedWindow dw)
        {
            if (!DetachedOwnKey(k) && !MainOnlyKey(k)) return false;
            BeginInvoke(new Action(() => { if (dw.IsDisposed) return; if (DetachedOwnKey(k)) dw.PageKey(k); else DetachedMainKey(k); }));
            return true;
        }
        if (!TabShortcutFromPage(k)) return false;
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            var m = Message.Create(Handle, 0x0100, (IntPtr)(int)(k & Keys.KeyCode), IntPtr.Zero);
            ProcessCmdKey(ref m, k);
        }));
        return true;
    }

    /// Ctrl+T, Ctrl+Maj+T, Ctrl+Maj+N dans une fenêtre détachée : dans la fenêtre principale, qui passe devant.
    bool DetachedMainKey(Keys k)
    {
        if (!MainOnlyKey(k) || IsDisposed) return false;
        RestoreFromMinimized();
        var m = Message.Create(Handle, 0x0100, (IntPtr)(int)(k & Keys.KeyCode), IntPtr.Zero);
        ProcessCmdKey(ref m, k);
        if (!Offscreen) Activate();
        return true;
    }

    // ---------- 2. Ctrl+Pg.suiv / Pg.préc ----------

    bool TabKey(Keys k)
    {
        switch (k)
        {
            case Keys.Control | Keys.PageDown: if (tabs.Count > 1) SelectTab((sel + 1) % tabs.Count); return true;
            case Keys.Control | Keys.PageUp: if (tabs.Count > 1) SelectTab((sel - 1 + tabs.Count) % tabs.Count); return true;
            case Keys.Control | Keys.Shift | Keys.PageDown: MoveCurrentTab(1); return true;
            case Keys.Control | Keys.Shift | Keys.PageUp: MoveCurrentTab(-1); return true;
        }
        return false;
    }

    void MoveCurrentTab(int d)
    {
        int to = sel + d;
        if (sel < 0 || to < 0 || to >= tabs.Count || tabs[to].Pinned != tabs[sel].Pinned) return; // les épinglés restent entre eux
        MoveTab(sel, to);
    }

    // ---------- 4. Fermer les autres / à droite ----------

    List<BrowserTab> OtherTabsToClose(BrowserTab t) => tabs.Where(x => x != t && !x.Pinned).ToList();

    List<BrowserTab> TabsToRight(BrowserTab t)
    {
        int k = tabs.IndexOf(t);
        return k < 0 ? new List<BrowserTab>() : tabs.Skip(k + 1).Where(x => !x.Pinned).ToList();
    }

    void CloseOtherTabs(BrowserTab t)
    {
        if (!tabs.Contains(t)) return;
        SelectTab(tabs.IndexOf(t)); // d'abord : l'onglet choisi reste affiché, aucun voisin n'est réveillé en route
        foreach (var x in OtherTabsToClose(t)) { int i = tabs.IndexOf(x); if (i >= 0) CloseTab(i); }
        if (tabs.Contains(t) && Cur != t) SelectTab(tabs.IndexOf(t));
    }

    void CloseTabsToRight(BrowserTab t)
    {
        int k = tabs.IndexOf(t);
        if (k < 0) return;
        var right = TabsToRight(t);
        if (right.Contains(Cur)) SelectTab(k); // l'onglet affiché va être fermé : c'est celui choisi qui reste (un épinglé affiché reste affiché)
        foreach (var x in right) { int i = tabs.IndexOf(x); if (i >= 0) CloseTab(i); }
    }

    // ---------- 5. Ctrl+Maj+T à sa place ----------

    readonly List<(string Url, int Index, bool Pinned)> closedPlaces = new();

    void RememberClosedPlace(string url, int index, bool pinned)
    {
        closedPlaces.RemoveAll(p => !closedTabs.Contains(p.Url)); // historique effacé entre-temps : on oublie aussi
        closedPlaces.Add((url, index, pinned));
        if (closedPlaces.Count > 100) closedPlaces.RemoveAt(0);
    }

    void ReopenAtPlace(string url)
    {
        int k = closedPlaces.FindLastIndex(p => p.Url == url);
        var place = k >= 0 ? closedPlaces[k] : default;
        if (k >= 0) closedPlaces.RemoveAt(k);
        int before = tabs.Count;
        _ = NewTab(url);
        if (k < 0 || tabs.Count != before + 1 || sel < 0 || sel >= tabs.Count) return;
        var nt = tabs[sel];
        int pc = tabs.TakeWhile(x => x.Pinned).Count();
        int to = TabPlace.InGroup(place.Index, place.Pinned, pc, tabs.Count - 1);
        if (place.Pinned) nt.Pinned = true;
        MoveTab(sel, to);
        strip.Invalidate();
    }

    // ---------- 6. Dupliquer à côté ----------

    void DuplicateTab(BrowserTab t)
    {
        if (!tabs.Contains(t)) return;
        var src = t.View.CoreWebView2?.Source ?? t.PendingUrl ?? t.LastUrl; // onglet pas encore chargé : son adresse en attente
        string url = string.IsNullOrEmpty(src) || IsLocalPage(src) ? null : src;
        int before = tabs.Count;
        _ = NewTab(url, priv: t.Private);
        if (tabs.Count != before + 1 || sel < 0) return;
        if (t.Pinned)
        {
            // un onglet épinglé donne un double épinglé, juste à côté (comme Chrome)
            var dup = tabs[sel];
            tabs.RemoveAt(sel);
            dup.Pinned = true;
            tabs.Insert(tabs.IndexOf(t) + 1, dup);
            SelectTab(tabs.IndexOf(dup));
            SaveSessionSoon();
            return;
        }
        int pc = tabs.TakeWhile(x => x.Pinned).Count();
        MoveTab(sel, TabPlace.InGroup(tabs.IndexOf(t) + 1, false, pc, tabs.Count - 1));
    }

    // ---------- 8. Taille et position de la fenêtre ----------

    static string PlacementPath => Path.Combine(AppPaths.Data, "fenetre.txt");

    [StructLayout(LayoutKind.Sequential)] struct WinRect { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct WinPlacement { public int Length, Flags, ShowCmd; public Point Min, Max; public WinRect Normal; }
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WinPlacement wp);

    static bool RestoresMaximized(Form f)
    {
        var wp = new WinPlacement { Length = Marshal.SizeOf<WinPlacement>() };
        return f.IsHandleCreated && GetWindowPlacement(f.Handle, ref wp) && (wp.Flags & 2) != 0; // WPF_RESTORETOMAXIMIZED
    }

    /// Appelé par le constructeur : la fenêtre reprend sa place de la dernière fois (jamais en test).
    void RestoreWindowPlacement()
    {
        if (ShotsDir != null || SmokeReport != null || Offscreen) return;
        FormClosing += (_, _) => SaveWindowPlacement();
        try
        {
            if (File.Exists(PlacementPath)) ApplyWindowPlacement(this, File.ReadAllText(PlacementPath), Screen.AllScreens.Select(s => s.WorkingArea).ToArray(), Screen.PrimaryScreen.WorkingArea);
        }
        catch { }
    }

    /// Pose une fenêtre pas encore affichée à la place lue dans fenetre.txt (testé sur une fenêtre jamais montrée).
    static bool ApplyWindowPlacement(Form f, string text, Rectangle[] screens, Rectangle primary)
    {
        if (!WindowPlace.TryParse(text, out var saved, out bool max)) return false;
        f.StartPosition = FormStartPosition.Manual;
        f.Bounds = WindowPlace.Fit(saved, screens, primary, new Size(520, 320));
        f.WindowState = max ? FormWindowState.Maximized : FormWindowState.Normal; // agrandie sur l'écran où elle était
        return true;
    }

    void SaveWindowPlacement()
    {
        try
        {
            Rectangle normal; bool max;
            if (fullscreen) { normal = fullNormal.Width > 0 ? fullNormal : RestoreBounds; max = prevState == FormWindowState.Maximized; } // taille prise avant de retirer les bords
            else if (WindowState == FormWindowState.Normal) { normal = Bounds; max = false; }
            else if (WindowState == FormWindowState.Maximized) { normal = RestoreBounds; max = true; }
            else { normal = RestoreBounds; max = RestoresMaximized(this); }
            if (normal.Width < 100 || normal.Height < 100) return;
            File.WriteAllText(PlacementPath, WindowPlace.Format(normal, max));
        }
        catch { }
    }

    // ---------- 9. Sortir de la barre des tâches comme avant ----------

    static void RestoreFromMinimized(Form f)
    {
        if (f.IsDisposed || f.WindowState != FormWindowState.Minimized) return;
        f.WindowState = RestoresMaximized(f) ? FormWindowState.Maximized : FormWindowState.Normal;
    }

    void RestoreFromMinimized() => RestoreFromMinimized(this);

    // ---------- 10. Onglet remis là où on le lâche ----------

    int? dropAt;

    void ReattachAt(DetachedWindow w, Point screen)
    {
        dropAt = DropIndex(screen);
        ReattachWindow(w);
    }

    int DropIndex(Point screen)
    {
        var p = strip.PointToClient(screen);
        return TabPlace.DropIndex(Enumerable.Range(0, tabs.Count).Select(i => { var r = strip.TabBounds(i); return (r.Left, r.Width); }).ToList(), p.X);
    }

    int ReattachIndex(BrowserTab t)
    {
        int at = dropAt ?? Math.Min(tabs.Count, sel + 1);
        dropAt = null;
        return TabPlace.InGroup(at, t.Pinned, tabs.TakeWhile(x => x.Pinned).Count(), tabs.Count);
    }

    // ---------- 12. F11 ----------

    /// Un peu après le passage en plein écran : la fenêtre a fini de changer de taille (sinon la notification repartirait aussitôt).
    async void FullscreenHint()
    {
        await Task.Delay(300);
        if (!IsDisposed && fullscreen && !pageFullscreen) ShowFullscreenHint();
    }
    void ShowFullscreenHint() => ToolMessage("Plein écran", "Appuie sur F11 pour en sortir.");

    /// Test hors écran : le plein écran ne doit jamais agrandir la fenêtre sur ton écran. Elle prend la taille d'un écran
    /// en restant hors de vue : mêmes changements de taille et de place qu'un vrai plein écran (ceux qui ferment la notification).
    void OffscreenFullscreen()
    {
        var a = Screen.PrimaryScreen.Bounds;
        Bounds = new Rectangle(-4000 - a.Width, -4000, a.Width, a.Height);
    }

    /// Taille de la fenêtre normale avant F11 : retirer les bords la rétrécit, on la remet telle quelle en sortant.
    Rectangle fullNormal;

    // ================== Test hors écran « pc-onglets » ==================

    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    // (DwmSetWindowAttribute : Program.cs ; EnumChildWindows, GetClassName, PostMessage : Sites.cs ; SendMessage, StripClick : Tabs.cs)

    /// Fenêtre du moteur qui reçoit le clavier d'une page (processus du navigateur, pas LiteBrowser).
    static IntPtr EngineKeyWindow(WebView2 v)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(v.Handle, (h, _) =>
        {
            var sb = new System.Text.StringBuilder(64);
            GetClassName(h, sb, 64);
            GetWindowThreadProcessId(h, out uint pid);
            if (sb.ToString() == "Chrome_WidgetWin_1" && pid != Environment.ProcessId) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// Une touche tapée dans la page, comme au clavier : postée à la fenêtre du moteur. Ctrl/Maj/Alt simulés
    /// pour CE fil seulement (le vrai clavier de Ryan n'est jamais touché). Renvoie combien de fois le contrôle l'a vue.
    async Task<int> PcPageKey(WebView2 v, Keys k)
    {
        var target = EngineKeyWindow(v);
        if (target == IntPtr.Zero) return -1;
        var st = new byte[256]; GetKeyboardState(st);
        var saved = (byte[])st.Clone();
        bool ctrl = k.HasFlag(Keys.Control), shift = k.HasFlag(Keys.Shift), alt = k.HasFlag(Keys.Alt);
        if (ctrl) st[0x11] = st[0xA2] = 0x80;
        if (shift) st[0x10] = st[0xA0] = 0x80;
        if (alt) st[0x12] = st[0xA4] = 0x80;
        int seen = 0;
        void Spy(object s, KeyEventArgs e) => seen++;
        v.KeyDown += Spy;
        try
        {
            SetKeyboardState(st);
            int vk = (int)(k & Keys.KeyCode);
            uint scan = MapVirtualKey((uint)vk, 0);
            long ext = vk is 0x21 or 0x22 or 0x24 or 0x2E ? 1 << 24 : 0; // Pg.préc, Pg.suiv, Origine, Suppr : touches étendues
            long lp = 1L | ((long)scan << 16) | ext | (alt ? 1L << 29 : 0L);
            PostMessage(target, alt ? 0x0104 : 0x0100, (IntPtr)vk, (IntPtr)lp);
            await Task.Delay(150);
            PostMessage(target, alt ? 0x0105 : 0x0101, (IntPtr)vk, (IntPtr)unchecked((int)(lp | 0xC0000000)));
            await Task.Delay(350);
        }
        finally { v.KeyDown -= Spy; SetKeyboardState(saved); }
        return seen;
    }

    async Task PcOngletsTest(string name)
    {
        var r = new List<string>();
        void Save() { try { File.WriteAllLines(Path.Combine(ShotsDir, name + ".txt"), r); } catch { } }
        void C(string what, bool ok, string d = "") { r.Add((ok ? "OK    " : "ÉCHEC ") + what + (d.Length > 0 ? " · " + d : "")); Save(); }
        async Task Settle() { for (int i = 0; i < 150 && strip.Animating; i++) await Task.Delay(20); await Task.Delay(80); }
        async Task Shot(string n) { var job = ShotAsync(name + "_" + n); if (await Task.WhenAny(job, Task.Delay(8000)) != job) r.Add("(capture " + n + " abandonnée)"); }
        string Url(BrowserTab t) => t.View.CoreWebView2?.Source ?? t.PendingUrl ?? "";
        BrowserTab Lazy(string url, string title) { var t = CreateTab(false, false); t.Lazy = true; t.PendingUrl = url; t.LastUrl = url; t.Title = title; return t; }
        try
        {
            // ---------- 1. raccourcis tapés dans la page ----------
            await NewTab("https://example.com/"); await Task.Delay(2500);
            var ex = Cur;
            await NewTab("https://example.org/"); await Task.Delay(2500);
            var org = Cur;
            C("fenêtre du moteur qui reçoit le clavier de la page trouvée", EngineKeyWindow(ex.View) != IntPtr.Zero);

            int n = tabs.Count;
            int seen = await PcPageKey(org.View, Keys.Control | Keys.T); await Task.Delay(500);
            C("page qui a le focus : Ctrl+T ouvre un nouvel onglet", tabs.Count == n + 1 && Cur.IsHome, $"{n} → {tabs.Count} onglets, touche reçue {seen}×");
            C("nouvel onglet : le curseur est dans la barre d'adresse", addr.Box.Focused || ActiveControl == addr.Box || addr.ContainsFocus, $"actif : {ActiveControl?.GetType().Name ?? "aucun"}");
            CloseTab(sel); await Settle();

            SelectTab(tabs.IndexOf(org)); await Task.Delay(300);
            int si = sel;
            await PcPageKey(org.View, Keys.Control | Keys.Tab); await Task.Delay(300);
            C("page qui a le focus : Ctrl+Tab passe à l'onglet suivant", sel == (si + 1) % tabs.Count, $"{si + 1} → {sel + 1}");
            SelectTab(tabs.IndexOf(org)); await Task.Delay(300);
            await PcPageKey(org.View, Keys.Control | Keys.Shift | Keys.Tab); await Task.Delay(300);
            C("page qui a le focus : Ctrl+Maj+Tab revient à l'onglet précédent", sel == (si - 1 + tabs.Count) % tabs.Count, $"{si + 1} → {sel + 1}");
            SelectTab(tabs.IndexOf(org)); await Task.Delay(300);
            await PcPageKey(org.View, Keys.Control | Keys.D1); await Task.Delay(300);
            C("page qui a le focus : Ctrl+1 (touche & en AZERTY) va au 1er onglet", sel == 0, $"onglet {sel + 1}");
            SelectTab(tabs.IndexOf(org)); await Task.Delay(300);
            await PcPageKey(org.View, Keys.Control | Keys.PageUp); await Task.Delay(300);
            C("page qui a le focus : Ctrl+Pg.préc va à l'onglet de gauche", Cur == ex, $"« {Cur.Title} »");
            SelectTab(tabs.IndexOf(org)); await Task.Delay(300);
            addr.Box.Text = ""; // la barre d'adresse a été quittée
            await PcPageKey(org.View, Keys.Control | Keys.L); await Task.Delay(400);
            C("page qui a le focus : Ctrl+L met le curseur dans la barre d'adresse", addr.Box.Focused || ActiveControl == addr.Box, $"actif : {ActiveControl?.GetType().Name ?? "aucun"}, texte « {addr.Box.Text} »");
            org.View.Focus();
            n = tabs.Count;
            await PcPageKey(org.View, Keys.Control | Keys.W); await Task.Delay(700);
            C("page qui a le focus : Ctrl+W ferme l'onglet", tabs.Count == n - 1 && !tabs.Contains(org), $"{n} → {tabs.Count}");
            var cur = Cur; n = tabs.Count;
            await PcPageKey(cur.View.CoreWebView2 != null ? cur.View : ex.View, Keys.Control | Keys.Shift | Keys.T); await Task.Delay(900);
            org = tabs.FirstOrDefault(t => Url(t).Contains("example.org"));
            C("page qui a le focus : Ctrl+Maj+T rouvre l'onglet fermé", tabs.Count == n + 1 && org != null, $"{n} → {tabs.Count}");
            var siteKeys = new[] { Keys.Control | Keys.D, Keys.Control | Keys.K, Keys.Control | Keys.E, Keys.Control | Keys.M, Keys.Control | Keys.C, Keys.Control | Keys.Alt | Keys.E, Keys.Control | Keys.Alt | Keys.D0,
                Keys.Control | Keys.H, Keys.Control | Keys.Oemcomma, Keys.Control | Keys.Shift | Keys.L, Keys.Control | Keys.Shift | Keys.M, Keys.Control | Keys.Shift | Keys.D,
                Keys.Control | Keys.Shift | Keys.A, Keys.Control | Keys.Shift | Keys.B, Keys.Control | Keys.Shift | Keys.Delete };
            C("touches laissées à la page : Ctrl+D/K/E/M/C, AltGr, Ctrl+H et Ctrl+, (Docs), Ctrl+Maj+L (Docs), Ctrl+Maj+M/D (Discord), Ctrl+Maj+A/B/Suppr",
                !siteKeys.Any(TabShortcutFromPage), string.Join(", ", siteKeys.Where(TabShortcutFromPage)));
            C("touches prises même dans une page : F11, Ctrl+9, Ctrl+F4, Alt+D, F6", new[] { Keys.F11, Keys.Control | Keys.D9, Keys.Control | Keys.F4, Keys.Alt | Keys.D, Keys.F6 }.All(TabShortcutFromPage));
            SelectTab(tabs.IndexOf(ex)); await Task.Delay(300);
            n = tabs.Count;
            await PcPageKey(ex.View, Keys.Control | Keys.K); await Task.Delay(300);
            C("Ctrl+K tapé dans la page : rien ne bouge côté LiteBrowser", tabs.Count == n && Cur == ex);
            // la page doit vraiment recevoir les raccourcis des sites (Discord : Ctrl+Maj+M coupe le micro ; Docs : Ctrl+H remplace)
            await ex.View.CoreWebView2.ExecuteScriptAsync("window.__k=[];addEventListener('keydown',e=>{if(window.__k){__k.push((e.ctrlKey?'C':'')+(e.shiftKey?'S':'')+e.code);e.preventDefault()}},true)"); // le site s'en sert
            ex.View.Focus();
            n = tabs.Count;
            string got = "";
            foreach (var (sk2, code) in new[] { (Keys.Control | Keys.H, "CKeyH"), (Keys.Control | Keys.Shift | Keys.M, "CSKeyM"), (Keys.Control | Keys.Shift | Keys.D, "CSKeyD"), (Keys.Control | Keys.Shift | Keys.L, "CSKeyL") })
                for (int tr = 0; tr < 3 && !got.Contains(code); tr++) // touche simulée : le moteur la rate parfois, on la retape
                {
                    ex.View.Focus(); await Task.Delay(200);
                    await PcPageKey(ex.View, sk2); await Task.Delay(200);
                    got = await ex.View.CoreWebView2.ExecuteScriptAsync("(window.__k||[]).join(' ')");
                }
            C("dans une page : Ctrl+H, Ctrl+Maj+M, Ctrl+Maj+D, Ctrl+Maj+L arrivent au site", got.Contains("CKeyH") && got.Contains("CSKeyM") && got.Contains("CSKeyD") && got.Contains("CSKeyL"), "page : " + got);
            C("… et LiteBrowser n'ouvre ni l'historique, ni le multistream, ni rien d'autre", tabs.Count == n && Cur == ex, $"{n} → {tabs.Count} onglets, affiché « {Cur.Title} »");
            // touche dont la page ne se sert pas : LiteBrowser la fait (comme Chrome). Ctrl+Maj+B affiche/masque la barre de favoris.
            await ex.View.CoreWebView2.ExecuteScriptAsync("window.__k=null");
            bool favBefore = showFavBar;
            for (int tr = 0; tr < 3 && showFavBar == favBefore; tr++)
            {
                ex.View.Focus(); await Task.Delay(200);
                await PcPageKey(ex.View, Keys.Control | Keys.Shift | Keys.B); await Task.Delay(500);
            }
            C("dans une page qui ne s'en sert pas : Ctrl+Maj+B marche (barre de favoris)", showFavBar != favBefore, $"barre {favBefore} → {showFavBar}");
            if (showFavBar != favBefore) ToggleFavBar();
            Save();

            // ---------- 2. Ctrl+Pg.suiv / Ctrl+Maj+Pg.suiv ----------
            SelectTab(tabs.IndexOf(ex)); await Task.Delay(200);
            int ei = sel;
            var m0 = new Message();
            ProcessCmdKey(ref m0, Keys.Control | Keys.PageDown); await Task.Delay(200);
            C("Ctrl+Pg.suiv : onglet suivant", sel == (ei + 1) % tabs.Count, $"{ei + 1} → {sel + 1}");
            SelectTab(tabs.IndexOf(ex)); await Task.Delay(200);
            ProcessCmdKey(ref m0, Keys.Control | Keys.Shift | Keys.PageDown); await Task.Delay(200);
            C("Ctrl+Maj+Pg.suiv : l'onglet se déplace d'un cran à droite, toujours affiché", tabs.IndexOf(ex) == ei + 1 && Cur == ex, $"place {ei + 1} → {tabs.IndexOf(ex) + 1}");
            ProcessCmdKey(ref m0, Keys.Control | Keys.Shift | Keys.PageUp); await Task.Delay(200);
            C("Ctrl+Maj+Pg.préc : il revient", tabs.IndexOf(ex) == ei && Cur == ex);

            // ---------- 3. fermetures en rafale ----------
            var many = Enumerable.Range(1, 8).Select(i => Lazy("https://example.com/?rafale=" + i, "Rafale " + i)).ToList();
            tabs.AddRange(many);
            strip.Invalidate();
            int k = tabs.IndexOf(many[3]);
            SelectTab(k); await Task.Delay(600); await Settle();
            var r0 = strip.TabBounds(k);
            int w0 = strip.CurrentTabWidth;
            var x = new Point(r0.Right - S(8) - S(10), r0.Top + r0.Height / 2); // centre de la croix de l'onglet affiché
            int before = tabs.Count;
            var first = tabs[k];
            StripClick(x); await Settle();
            var r1 = strip.TabBounds(k);
            C("clic sur ✕ : l'onglet est fermé", tabs.Count == before - 1 && !tabs.Contains(first));
            C("fermetures en rafale : les onglets gardent leur largeur, la croix suivante est sous la souris", strip.CloseFrozen && r1 == r0 && Cur == tabs[k],
                $"onglet {k + 1} : {r0} → {r1}, {w0} px");
            var second = tabs[k];
            StripClick(x); await Settle();
            C("2e clic au même endroit : l'onglet suivant est fermé", tabs.Count == before - 2 && !tabs.Contains(second), $"{before} → {tabs.Count}");
            StripShot(name + "_1_rafale");
            SendMessage(strip.Handle, 0x02A3, IntPtr.Zero, IntPtr.Zero); // WM_MOUSELEAVE : la souris quitte la barre
            await Task.Delay(40);
            int wMid = strip.CurrentTabWidth;
            await Settle();
            int w2 = strip.CurrentTabWidth;
            C("souris sortie de la barre : les onglets reprennent leur largeur en glissant", !strip.CloseFrozen && w2 > w0 && wMid < w2,
                $"{w0} → {w2} px (à 40 ms : {wMid}) · sans ce correctif, la croix aurait sauté de {(w2 - w0) * (k + 1)} px au 1er clic");
            C("aucune animation au repos", !strip.Animating);
            StripShot(name + "_2_apres");

            // ---------- 4. fermer les autres / à droite : les épinglés restent ----------
            var pin = many[0];
            TogglePin(pin); await Settle();
            var keep = many[5];
            SelectTab(tabs.IndexOf(many[7])); await Task.Delay(300);
            var right = TabsToRight(keep);
            CloseTabsToRight(keep); await Settle();
            C("« Fermer les onglets à droite » : seulement ceux de droite, l'onglet choisi s'affiche", right.Count > 0 && right.All(t => !tabs.Contains(t)) && Cur == keep && tabs.Contains(pin),
                $"{right.Count} fermés, affiché « {Cur.Title} »");
            var others = OtherTabsToClose(keep);
            var sleeping = others.Where(t => t.Lazy).ToList(); // pas encore chargés : ils doivent le rester jusqu'à leur fermeture
            CloseOtherTabs(keep); await Settle();
            C("« Fermer les autres onglets » : l'onglet épinglé reste", tabs.Contains(pin) && pin.Pinned && tabs.Contains(keep) && tabs.Count == 2, string.Join(" | ", tabs.Select(t => t.Title)));
            C("l'onglet choisi reste affiché", Cur == keep, $"« {Cur.Title} »");
            C("aucun onglet voisin chargé pour rien pendant la fermeture", sleeping.Count > 0 && sleeping.All(t => t.Lazy), $"{sleeping.Count} onglets au repos fermés, {sleeping.Count(t => !t.Lazy)} réveillés en route");
            C("plus rien à fermer : éléments grisés", OtherTabsToClose(keep).Count == 0 && TabsToRight(keep).Count == 0);
            await Shot("3_autres");
            // onglet choisi épinglé, onglet affiché = un autre épinglé plus à droite : il n'est pas fermé, il reste affiché
            var pin2 = Lazy("https://example.com/?pin2=1", "Épinglé 2"); pin2.Pinned = true;
            tabs.Insert(1, pin2); strip.Invalidate();
            SelectTab(1); await Task.Delay(300);
            CloseTabsToRight(pin); await Settle();
            C("« Fermer les onglets à droite » d'un épinglé : l'épinglé affiché à droite reste affiché", Cur == pin2 && tabs.Contains(pin2) && !tabs.Contains(keep),
                $"affiché « {Cur.Title} » · {string.Join(" | ", tabs.Select(t => t.Title))}");
            CloseTab(tabs.IndexOf(pin2)); await Settle();
            closedTabs.Remove("https://example.com/?pin2=1");

            // ---------- 5. Ctrl+Maj+T à sa place ----------
            var a = Lazy("https://example.com/?place=a", "Place A"); var b = Lazy("https://example.com/?place=b", "Place B"); var c = Lazy("https://example.com/?place=c", "Place C");
            tabs.AddRange(new[] { a, b, c }); strip.Invalidate();
            SelectTab(tabs.IndexOf(c)); await Task.Delay(300);
            int ai = tabs.IndexOf(a);
            CloseTab(ai); await Settle();
            ReopenClosed(); await Task.Delay(400); await Settle();
            C("Ctrl+Maj+T : l'onglet revient à sa place", Url(tabs[ai]).Contains("place=a") && Cur == tabs[ai], $"place {ai + 1} : {Url(tabs[ai])}");
            string pinUrl = Url(pin);
            CloseTab(tabs.IndexOf(pin)); await Settle();
            ReopenClosed(); await Task.Delay(400); await Settle();
            C("Ctrl+Maj+T sur un onglet épinglé : il revient épinglé, à gauche", tabs[0].Pinned && Url(tabs[0]) == pinUrl, $"{tabs[0].Title} · épinglé {tabs[0].Pinned}");
            pin = tabs[0];

            // ---------- 6. Dupliquer à côté ----------
            var d = Lazy("https://example.com/?dup=1", "À dupliquer");
            tabs.Insert(tabs.IndexOf(b), d); strip.Invalidate();
            SelectTab(tabs.Count - 1); await Task.Delay(300);
            n = tabs.Count;
            DuplicateTab(d); await Task.Delay(400); await Settle();
            int di = tabs.IndexOf(d);
            C("« Dupliquer » sur un onglet pas encore chargé : la copie s'ouvre", tabs.Count == n + 1, $"{n} → {tabs.Count}");
            C("la copie est juste à droite de l'onglet dupliqué, et affichée", di + 1 < tabs.Count && Url(tabs[di + 1]).Contains("dup=1") && Cur == tabs[di + 1], $"onglet {di + 1} → copie {tabs.IndexOf(Cur) + 1}");
            Save();

            // ---------- 7. session : bon onglet affiché ----------
            var sessionFile = sessionPath;
            File.WriteAllText(sessionFile, "selected=2\n0\t\tNouvel onglet\n0\thttps://example.com/?session=1\tExemple\n0\t\tNouvel onglet\n");
            int baseCount = tabs.Count;
            bool restored = RestoreSession(); await Task.Delay(500);
            C("session : l'onglet affiché au démarrage est le bon (même adresse que d'autres)", restored && sel == baseCount + 2, $"voulu {baseCount + 3}, affiché {sel + 1}");
            foreach (var t in tabs.Skip(baseCount).ToList()) CloseTab(tabs.IndexOf(t));
            File.Delete(sessionFile);
            await Settle();

            // ---------- 10. onglet lâché sur la barre : à sa place ----------
            var e1 = Lazy("https://example.com/?drop=1", "Lâché 1");
            tabs.Add(e1); strip.Invalidate();
            SelectTab(tabs.Count - 1); await Task.Delay(800);
            DetachTab(tabs.IndexOf(e1), new Point(-3800, -3800)); await Task.Delay(1500);
            var w = detached.FirstOrDefault(z => z.Tab == e1);
            C("onglet sorti dans sa fenêtre", w != null && !tabs.Contains(e1));
            if (w != null)
            {
                var t1 = strip.TabBounds(1);
                var drop = strip.PointToScreen(new Point(t1.Left + t1.Width / 4, t1.Top + t1.Height / 2));
                ReattachAt(w, drop); await Task.Delay(600); await Settle();
                C("lâché sur la barre : l'onglet revient là où tu l'as lâché", tabs.IndexOf(e1) == 1 && Cur == e1, $"place {tabs.IndexOf(e1) + 1}");
            }
            DetachTab(tabs.IndexOf(pin), new Point(-3800, -3800)); await Task.Delay(1200);
            var wp = detached.FirstOrDefault(z => z.Tab == pin);
            if (wp != null)
            {
                var end = strip.PointToScreen(new Point(strip.Width - S(120), S(20)));
                ReattachAt(wp, end); await Task.Delay(600); await Settle();
                C("onglet épinglé remis : il revient avec les épinglés (pas au milieu, sans titre)", tabs[0] == pin && pin.Pinned, string.Join(" | ", tabs.Select(t => (t.Pinned ? "📌" : "") + t.Title)));
            }
            else C("onglet épinglé sorti dans sa fenêtre", false);
            StripShot(name + "_4_remis");

            // ---------- 11. fenêtre détachée : Ctrl+T, Ctrl+W ----------
            // une vieille fermeture de la même adresse, épinglée tout à gauche : elle ne doit pas servir pour celle-ci
            string e1Url = "https://example.com/?drop=1";
            closedTabs.Add(e1Url); RememberClosedPlace(e1Url, 0, true);
            DetachTab(tabs.IndexOf(e1), new Point(-3800, -3800)); await Task.Delay(2500);
            var w3 = detached.FirstOrDefault(z => z.Tab == e1);
            if (w3 != null)
            {
                n = tabs.Count;
                bool h = w3.PageKey(Keys.Control | Keys.T); await Task.Delay(500);
                C("fenêtre détachée (barre d'adresse) : Ctrl+T ouvre un onglet dans la fenêtre principale", h && tabs.Count == n + 1 && Cur.IsHome, $"{n} → {tabs.Count}");
                CloseTab(sel); await Settle();
                n = tabs.Count;
                for (int i = 0; i < 40 && e1.View.CoreWebView2 == null; i++) await Task.Delay(100);
                await Task.Delay(1500);
                int sk = await PcPageKey(e1.View, Keys.Control | Keys.T); await Task.Delay(600);
                C("fenêtre détachée (page) : Ctrl+T ouvre un onglet dans la fenêtre principale", tabs.Count == n + 1, $"{n} → {tabs.Count}, touche reçue {sk}×");
                if (tabs.Count == n + 1) { CloseTab(sel); await Settle(); }
                await PcPageKey(e1.View, Keys.Control | Keys.W); await Task.Delay(900);
                C("fenêtre détachée (page) : Ctrl+W la ferme (onglet rouvrable)", w3.IsDisposed && !detached.Contains(w3) && closedTabs.LastOrDefault()?.Contains("drop=1") == true, closedTabs.LastOrDefault() ?? "");
                n = tabs.Count;
                ReopenClosed(); await Task.Delay(500); await Settle();
                var back1 = Cur;
                C("Ctrl+Maj+T après fermeture de sa fenêtre : revient en onglet normal, à la fin (pas épinglé à gauche comme une vieille fermeture)",
                    tabs.Count == n + 1 && Url(back1).Contains("drop=1") && !back1.Pinned && tabs.IndexOf(back1) == tabs.Count - 1, $"place {tabs.IndexOf(back1) + 1}/{tabs.Count}, épinglé {back1.Pinned}");
                CloseTab(tabs.IndexOf(back1)); await Settle();
            }
            else C("onglet sorti pour le test du clavier", false);

            // ---------- 8. taille et position de la fenêtre ----------
            var p1 = new Rectangle(0, 0, 1920, 1040); var p2 = new Rectangle(1920, 0, 2560, 1400);
            var mini = new Size(520, 320);
            C("fenêtre retenue : remise telle quelle sur son écran", WindowPlace.Fit(new Rectangle(100, 80, 1200, 800), new[] { p1, p2 }, p1, mini) == new Rectangle(100, 80, 1200, 800));
            C("fenêtre sur le 2e écran : y revient", WindowPlace.Fit(new Rectangle(2100, 100, 1500, 900), new[] { p1, p2 }, p1, mini) == new Rectangle(2100, 100, 1500, 900));
            var gone = WindowPlace.Fit(new Rectangle(2100, 100, 1500, 900), new[] { p1 }, p1, mini);
            C("2e écran débranché : la fenêtre revient sur l'écran principal, entière", p1.Contains(gone), gone.ToString());
            var big = WindowPlace.Fit(new Rectangle(-50, -20, 3000, 2000), new[] { p1 }, p1, mini);
            C("plus grande que l'écran : ramenée à l'écran", big == p1, big.ToString());
            var edge = WindowPlace.Fit(new Rectangle(1700, 500, 800, 600), new[] { p1 }, p1, mini);
            C("à moitié hors de l'écran : rentrée dedans", p1.Contains(edge) && edge.Size == new Size(800, 600), edge.ToString());
            SaveWindowPlacement();
            bool parsed = WindowPlace.TryParse(File.Exists(PlacementPath) ? File.ReadAllText(PlacementPath) : "", out var back, out bool backMax);
            C("taille et position enregistrées à la fermeture", parsed && back == Bounds && !backMax, $"{(File.Exists(PlacementPath) ? File.ReadAllText(PlacementPath) : "pas de fichier")} pour {Bounds}");
            var areas = Screen.AllScreens.Select(s => s.WorkingArea).ToArray();
            var home = WindowPlace.Fit(back, areas, Screen.PrimaryScreen.WorkingArea, mini);
            C("fenêtre enregistrée hors de tout écran : rouverte sur un écran", areas.Any(s => s.Contains(home)), $"{back} → {home}");
            C("fichier abîmé : ignoré", !WindowPlace.TryParse("12;abc;;", out _, out _) && !WindowPlace.TryParse("", out _, out _));
            // au démarrage (RestoreWindowPlacement) : sur une fenêtre jamais affichée, donc jamais sur ton écran
            using (var f0 = new Form())
            {
                bool ok0 = ApplyWindowPlacement(f0, WindowPlace.Format(new Rectangle(2100, 100, 1500, 900), false), new[] { p1, p2 }, p1);
                C("démarrage : fenêtre normale remise à sa place, sur le 2e écran", ok0 && f0.Bounds == new Rectangle(2100, 100, 1500, 900) && f0.WindowState == FormWindowState.Normal && f0.StartPosition == FormStartPosition.Manual && !f0.Visible, $"{f0.Bounds} {f0.WindowState}");
                ok0 = ApplyWindowPlacement(f0, WindowPlace.Format(new Rectangle(2100, 100, 1500, 900), true), new[] { p1 }, p1);
                C("démarrage : fenêtre agrandie, 2e écran débranché : rouverte agrandie sur l'écran principal", ok0 && p1.Contains(f0.Bounds) && f0.WindowState == FormWindowState.Maximized && !f0.Visible, $"{f0.Bounds} {f0.WindowState}");
                var keepB = f0.Bounds;
                C("démarrage : fichier abîmé, la fenêtre n'est pas touchée", !ApplyWindowPlacement(f0, "12;abc;;", new[] { p1 }, p1) && f0.Bounds == keepB);
                C("démarrage : la fenêtre de test n'a jamais été créée à l'écran", !f0.IsHandleCreated);
            }

            // ---------- 9. fenêtre réduite rouverte comme avant ----------
            C("avant : « prevState » disait « agrandie » pour une fenêtre normale (elle se serait rouverte agrandie)", prevState == FormWindowState.Maximized && WindowState == FormWindowState.Normal, $"prevState {prevState}, fenêtre {WindowState}");
            using (var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -3000), Size = new Size(400, 300), ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.FixedToolWindow, Opacity = 0.01 })
            {
                f.CreateControl();
                int off = 1;
                try { DwmSetWindowAttribute(f.Handle, 3, ref off, 4); } catch { } // pas d'animation de réduction
                ShowWindow(f.Handle, 4); // SW_SHOWNOACTIVATE
                await Task.Delay(200);
                ShowWindow(f.Handle, 7); // SW_SHOWMINNOACTIVE
                await Task.Delay(300);
                bool wasMin = f.WindowState == FormWindowState.Minimized;
                RestoreFromMinimized(f);
                await Task.Delay(300);
                C("fenêtre normale réduite puis rouverte : redevient normale, à sa place", wasMin && f.WindowState == FormWindowState.Normal && f.Location == new Point(-4000, -3000), $"réduite {wasMin}, puis {f.WindowState} à {f.Location}");
                f.Close();
            }

            // ---------- 12. F11 : comment en sortir ----------
            // vrai chemin : F11 tapé dans la page → plein écran (bords retirés, taille de l'écran) → notification 300 ms après
            await NewTab("https://example.com/?f11=1"); await Task.Delay(2500);
            var f11 = Cur;
            toolToast?.Hide();
            lastToolMessage = null;
            var beforeFull = Bounds;
            await PcPageKey(f11.View, Keys.F11); await Task.Delay(900);
            var area = content.RectangleToScreen(content.ClientRectangle);
            C("F11 tapé dans la page : plein écran (barres cachées, taille de l'écran, hors de ton écran)", fullscreen && !toolbar.Visible && !strip.Visible && FormBorderStyle == FormBorderStyle.None && Size == Screen.PrimaryScreen.Bounds.Size && Right < 0,
                $"plein écran {fullscreen}, {Bounds}");
            C("plein écran F11 : « Appuie sur F11 pour en sortir » s'affiche après le changement de taille, dans la fenêtre", lastToolMessage == "Plein écran" && toolToast is { Visible: true } && area.Contains(toolToast.Bounds),
                toolToast == null ? "pas de notification" : $"{toolToast.Visible} {toolToast.Bounds} dans {area}");
            await Shot("5_f11");
            try
            {
                // la notification est une petite fenêtre à part : on la pose sur la capture, à sa place
                var file = Path.Combine(ShotsDir, name + "_5_f11.png");
                Bitmap shot; using (var img = Image.FromFile(file)) shot = new Bitmap(img);
                using (shot)
                {
                    using var tb = new Bitmap(toolToast.Width, toolToast.Height);
                    toolToast.DrawToBitmap(tb, new Rectangle(Point.Empty, toolToast.Size));
                    using (var g = Graphics.FromImage(shot)) g.DrawImage(tb, new Point(toolToast.Left - Left, toolToast.Top - Top));
                    shot.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch { }
            await PcPageKey(f11.View, Keys.F11); await Task.Delay(600);
            C("F11 à nouveau : plein écran quitté, fenêtre remise comme avant, notification partie", !fullscreen && toolbar.Visible && strip.Visible && FormBorderStyle == FormBorderStyle.Sizable && Bounds == beforeFull && toolToast is not { Visible: true },
                $"plein écran {fullscreen}, {Bounds} (avant {beforeFull})");
            // jamais sur Nur Live / le multistream : toute la fenêtre est aux lecteurs Twitch
            await NewTab(MultiUrl); await Task.Delay(2500);
            var multi = Cur;
            C("onglet multistream ouvert (lecteurs Twitch)", KeepsPlaying(multi.View.CoreWebView2), Url(multi));
            toolToast?.Hide();
            SetFullscreen(true); await Task.Delay(900);
            C("F11 sur le multistream : aucune notification par-dessus les lecteurs", fullscreen && toolToast is not { Visible: true }, toolToast == null ? "pas de notification" : $"visible {toolToast.Visible}");
            SetFullscreen(false); await Task.Delay(400);
            CloseTab(tabs.IndexOf(multi)); CloseTab(tabs.IndexOf(f11)); await Settle();
            C("la fenêtre de test n'a jamais été agrandie ni mise en plein écran sur ton écran", WindowState == FormWindowState.Normal && !fullscreen && Location.X < -3000);
        }
        catch (Exception e) { C("test interrompu", false, e.ToString()); }
        Save();
    }
}
