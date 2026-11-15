// Outils de page : capture d’écran (zone visible / page entière), impression, vidéo en image dans l’image,
// rechercher dans la page (barre M8 qui pousse la page, jamais par-dessus).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using LiteCore;

partial class BrowserForm
{
    FindBar findBar;
    ToolToast toolToast;
    CoreWebView2 findCore;          // page sur laquelle porte la recherche en cours
    bool findJs;                    // moteur trop ancien pour l’API Find : repli par script
    bool findHiddenByFullscreen;
    int findRun;                    // numéro de la dernière recherche lancée (les réponses plus anciennes sont ignorées)
    ToolStripMenuItem toolsMenu;    // sous-menu ouvert (pour les captures du test)
    string lastShotPath;

    // ---------- Clavier et menu ----------

    /// Raccourcis des outils (appelé en tête de ProcessCmdKey). Vrai = touche utilisée.
    bool ToolsKey(Keys k)
    {
        switch (k)
        {
            case Keys.Control | Keys.F: ShowFind(); return true;
            case Keys.F3: if (findBar?.Visible == true) FindStep(1); else ShowFind(); return true;
            case Keys.Shift | Keys.F3: if (findBar?.Visible == true) FindStep(-1); else ShowFind(); return true;
            case Keys.Control | Keys.P: PrintPage(); return true;
            case Keys.Control | Keys.Shift | Keys.C: _ = CapturePage(false); return true;
            case Keys.Control | Keys.Shift | Keys.S: _ = CapturePage(true); return true;
            case Keys.Alt | Keys.P: _ = PictureInPicture(); return true;
        }
        return false;
    }

    /// Sous-menu « Outils de la page » du menu ☰.
    ToolStripMenuItem ToolsMenu()
    {
        var m = MI("Outils de la page");
        bool page = Core != null;
        var items = new[]
        {
            MI("Rechercher dans la page…", ShowFind, "Ctrl+F"),
            null,
            MI("Capturer la zone visible", () => _ = CapturePage(false), "Ctrl+Maj+C"),
            MI("Capturer la page entière", () => _ = CapturePage(true), "Ctrl+Maj+S"),
            MI("Imprimer…", PrintPage, "Ctrl+P"),
            MI("Vidéo en image dans l’image", () => _ = PictureInPicture(), "Alt+P"),
            null,
            MI("Ouvrir le dossier des captures", () => OpenShotsFolder(null)),
        };
        foreach (var it in items)
        {
            if (it == null) { m.DropDownItems.Add(new ToolStripSeparator()); continue; }
            if (it.Text != "Ouvrir le dossier des captures") it.Enabled = page;
            m.DropDownItems.Add(it);
        }
        toolsMenu = m;
        return m;
    }

    // ---------- Petite notification dans la fenêtre ----------

    void ToolMessage(string title, string text, Image thumb = null, string button = null, Action onButton = null)
    {
        if (IsDisposed) return;
        toolToast ??= new ToolToast(new Font("Segoe UI", 9.5f, FontStyle.Bold), new Font("Segoe UI", 9f));
        toolToast.Set(theme, title, text, thumb, button, onButton);
        // Sur Nur Live et le multistream, la notification reste sur la barre du haut : jamais au-dessus d’un lecteur Twitch
        Rectangle place;
        if (KeepsPlaying(Core) && toolbar.Visible)
        {
            var tb = toolbar.RectangleToScreen(toolbar.ClientRectangle);
            place = new Rectangle(tb.Right - toolToast.Width - S(56), tb.Bottom - toolToast.Height - S(3), toolToast.Width, toolToast.Height);
        }
        else
        {
            var area = content.RectangleToScreen(content.ClientRectangle);
            place = new Rectangle(area.Right - toolToast.Width - S(16), area.Top + S(12), toolToast.Width, toolToast.Height);
        }
        toolToast.Bounds = place;
        toolToast.Popup(this);
    }

    // ---------- Capture d’écran ----------

    string ShotsFolder() => ShotsDir != null
        ? Path.Combine(ShotsDir, "Images", "LiteBrowser")   // test : jamais dans les Images de Ryan
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LiteBrowser");

    void OpenShotsFolder(string file)
    {
        if (ShotsDir != null) return; // les tests n’ouvrent jamais l’Explorateur
        try
        {
            if (file != null && File.Exists(file)) Process.Start("explorer.exe", "/select,\"" + file + "\"");
            else { Directory.CreateDirectory(ShotsFolder()); Process.Start("explorer.exe", "\"" + ShotsFolder() + "\""); }
        }
        catch { }
    }

    /// Capture la page (zone visible ou page entière) : presse-papiers + fichier dans Images\LiteBrowser. Renvoie le fichier.
    async Task<string> CapturePage(bool full)
    {
        var core = Core;
        var tab = Cur;
        if (core == null || tab == null) return null;
        string note = null;
        if (full && KeepsPlaying(core))
        {
            // la capture « page entière » agrandit la page un instant : les lecteurs Twitch s’arrêteraient
            full = false;
            note = "Nur Live tient dans l’écran : la zone visible a été capturée.";
        }
        try
        {
            string args;
            bool cut = false;
            if (full)
            {
                var m = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}"));
                var size = m?["cssContentSize"] ?? m?["contentSize"];
                double w = size?["width"]?.GetValue<double>() ?? 0, h = size?["height"]?.GetValue<double>() ?? 0;
                double dpr = 1;
                try { dpr = double.Parse(await core.ExecuteScriptAsync("window.devicePixelRatio"), System.Globalization.CultureInfo.InvariantCulture); } catch { }
                var clip = PageTools.FullPageClip(w, h, dpr);
                cut = clip.Cut;
                args = JsonSerializer.Serialize(new { format = "png", captureBeyondViewport = true, clip = new { x = 0, y = 0, width = clip.Width, height = clip.Height, scale = clip.Scale } });
            }
            else args = "{\"format\":\"png\"}";
            var res = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args));
            var bytes = Convert.FromBase64String(res?["data"]?.GetValue<string>() ?? "");
            if (bytes.Length == 0) throw new Exception("image vide");

            var dir = ShotsFolder();
            Directory.CreateDirectory(dir);
            var name = PageTools.UniqueName(PageTools.ShotFileName(tab.Title, core.Source, DateTime.Now, full), f => File.Exists(Path.Combine(dir, f)));
            var file = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(file, bytes);
            lastShotPath = file;

            Image thumb;
            bool copied = false;
            using (var ms = new MemoryStream(bytes))
            using (var img = Image.FromStream(ms))
            {
                if (ShotsDir == null) // les tests ne touchent pas au presse-papiers du PC
                {
                    try
                    {
                        var data = new DataObject();
                        data.SetImage(img);
                        data.SetData("PNG", false, new MemoryStream(bytes)); // les applis qui préfèrent le PNG (Discord, navigateurs…)
                        Clipboard.SetDataObject(data, true);
                        copied = true;
                    }
                    catch { }
                }
                thumb = ToolToast.Thumbnail(img, S(104), S(64));
            }
            var where = "Images\\LiteBrowser";
            var text = (copied ? "Copiée dans le presse-papiers et enregistrée dans " : ShotsDir != null ? "Enregistrée (test) dans " : "Enregistrée dans ") + where + "."
                       + (cut ? " Page très longue : le bas a été coupé." : "") + (note != null ? " " + note : "");
            ToolMessage(full ? "Page entière capturée" : "Capture enregistrée", text, thumb, "Ouvrir le dossier", () => OpenShotsFolder(file));
            return file;
        }
        catch (Exception ex)
        {
            ToolMessage("Capture impossible", "La page n’a pas pu être capturée : " + ex.Message);
            return null;
        }
    }

    // ---------- Impression ----------

    void PrintPage()
    {
        var c = Core;
        if (c == null) return;
        try { c.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); }
        catch (Exception ex) { ToolMessage("Impression impossible", ex.Message); }
    }

    // ---------- Image dans l’image ----------

    // Plus grande vidéo en lecture (sinon la plus grande prête) ; une 2e demande referme l’image dans l’image.
    const string PipJs = @"(async (dry) => {
  if (document.pictureInPictureElement) { if (dry) return 'active'; await document.exitPictureInPicture(); return 'closed'; }
  const vids = [...document.querySelectorAll('video')];
  if (!vids.length) return document.querySelector('iframe') ? 'noneframes' : 'none';
  if (!document.pictureInPictureEnabled) return 'disabled';
  const area = v => { const r = v.getBoundingClientRect(); return Math.max(0, r.width) * Math.max(0, r.height); };
  const playing = vids.filter(v => !v.paused && !v.ended && v.readyState > 1);
  const pool = (playing.length ? playing : vids.filter(v => v.readyState > 0)).sort((a, b) => area(b) - area(a));
  const v = pool[0];
  if (!v) return 'notready';
  if (v.disablePictureInPicture) return 'refused';
  if (dry) return 'ok:' + (playing.length ? 'playing' : 'paused') + ':' + Math.round(area(v)) + ':' + (v.id || '');
  try { await v.requestPictureInPicture(); return playing.length ? 'ok' : 'okpaused'; }
  catch (e) { return 'error:' + e.name + ':' + e.message; }
})";

    /// Détache la vidéo (dry = on dit seulement laquelle, sans ouvrir de fenêtre). Renvoie le code du script.
    async Task<string> PictureInPicture(bool dry = false)
    {
        var c = Core;
        if (c == null) return null;
        string code;
        try
        {
            // geste utilisateur simulé : sans lui, la page refuse requestPictureInPicture()
            var args = JsonSerializer.Serialize(new { expression = PipJs + "(" + (dry ? "true" : "false") + ")", userGesture = true, awaitPromise = true, returnByValue = true });
            var res = JsonNode.Parse(await c.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args));
            code = res?["result"]?["value"]?.GetValue<string>() ?? ("error:Exception:" + (res?["exceptionDetails"]?["text"]?.GetValue<string>() ?? ""));
        }
        catch (Exception ex) { code = "error:Exception:" + ex.Message; }
        var msg = PageTools.PipMessage(code);
        if (msg != null) ToolMessage(msg.Value.Title, msg.Value.Text);
        return code;
    }

    // ---------- Rechercher dans la page ----------

    void EnsureFindBar()
    {
        if (findBar != null) return;
        findBar = new FindBar(iconSmall, new Font("Segoe UI", 10f)) { Dock = DockStyle.Top, Height = S(40), Visible = false };
        findBar.ApplyTheme(theme);
        Controls.Add(findBar);
        Controls.SetChildIndex(findBar, Controls.GetChildIndex(content) + 1); // juste au-dessus de la page : elle la pousse vers le bas
        findBar.Box.TextChanged += (_, _) => _ = FindStart();
        findBar.Box.KeyDown += (_, e) =>
        {
            if (FindKey(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; }
        };
        findBar.Prev.Click += (_, _) => FindStep(-1);
        findBar.Next.Click += (_, _) => FindStep(1);
        findBar.CloseBtn.Click += (_, _) => CloseFind();
        tip.SetToolTip(findBar.Prev, "Résultat précédent (Maj+Entrée)");
        tip.SetToolTip(findBar.Next, "Résultat suivant (Entrée)");
        tip.SetToolTip(findBar.CloseBtn, "Fermer (Échap)");
        toolbar.BackColorChanged += (_, _) => findBar.ApplyTheme(theme); // changement de thème
        toolbar.VisibleChanged += (_, _) =>
        {
            // plein écran : la barre s’efface avec les autres, puis revient
            if (!toolbar.Visible && findBar.Visible) { findHiddenByFullscreen = true; findBar.Visible = false; }
            else if (toolbar.Visible && findHiddenByFullscreen) { findHiddenByFullscreen = false; findBar.Visible = true; }
        };
    }

    /// Touches dans la case de recherche.
    bool FindKey(Keys k)
    {
        switch (k)
        {
            case Keys.Enter: FindStep(1); return true;
            case Keys.Shift | Keys.Enter: FindStep(-1); return true;
            case Keys.Escape: CloseFind(); return true;
        }
        return false;
    }

    async void ShowFind()
    {
        var core = Core;
        if (core == null) return;
        EnsureFindBar();
        findBar.ApplyTheme(theme);
        findHiddenByFullscreen = false;
        if (!findBar.Visible) findBar.Visible = true;
        BindFind(core);
        findBar.Box.Focus();
        findBar.Box.SelectAll();
        // texte sélectionné dans la page : on le cherche directement (comme Chrome)
        try
        {
            var s = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("String(getSelection ? getSelection() : '').trim()")) ?? "";
            if (s.Length > 0 && s.Length <= 120 && !s.Contains('\n') && core == findCore && findBar.Visible && s != findBar.Box.Text)
            {
                findBar.Box.Text = s;
                findBar.Box.SelectAll();
                return;
            }
        }
        catch { }
        if (findBar.Box.Text.Length > 0) _ = FindStart();
    }

    void BindFind(CoreWebView2 core)
    {
        if (findCore == core) return;
        UnbindFind(stop: true);
        findCore = core;
        findJs = false;
        try
        {
            core.Find.MatchCountChanged += OnFindChanged;
            core.Find.ActiveMatchIndexChanged += OnFindChanged;
        }
        catch { findJs = true; } // moteur sans API Find
    }

    void UnbindFind(bool stop)
    {
        var old = findCore;
        findCore = null;
        if (old == null) return;
        try
        {
            if (!findJs)
            {
                old.Find.MatchCountChanged -= OnFindChanged;
                old.Find.ActiveMatchIndexChanged -= OnFindChanged;
                if (stop) old.Find.Stop();
            }
            else if (stop) _ = old.ExecuteScriptAsync("window.__lbFind = null; getSelection().removeAllRanges()");
        }
        catch { }
    }

    void OnFindChanged(object sender, object e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(UpdateFindCounter));
    }

    void UpdateFindCounter()
    {
        if (findBar == null || findCore == null || findJs) return;
        try { findBar.SetCounter(PageTools.FindCounter(findCore.Find.ActiveMatchIndex, (int)findCore.Find.MatchCount, findBar.Box.Text)); }
        catch { }
    }

    async Task FindStart()
    {
        if (findBar == null || !findBar.Visible) return;
        var core = Core;
        if (core == null) return;
        BindFind(core);
        var term = findBar.Box.Text;
        int run = ++findRun;
        if (term.Length == 0)
        {
            try { if (findJs) await core.ExecuteScriptAsync("window.__lbFind = null; getSelection().removeAllRanges()"); else core.Find.Stop(); } catch { }
            findBar.SetCounter("");
            return;
        }
        if (!findJs)
        {
            try
            {
                var o = (env ?? core.Environment).CreateFindOptions();
                o.FindTerm = term;
                o.SuppressDefaultFindDialog = true;  // notre barre M8 à la place de celle du moteur (qui recouvrirait la page)
                o.ShouldHighlightAllMatches = true;
                o.IsCaseSensitive = false;
                await core.Find.StartAsync(o);
                if (run == findRun) UpdateFindCounter();
                return;
            }
            catch { findJs = true; }
        }
        await FindJs(core, term, 1, reset: true, run);
    }

    void FindStep(int dir)
    {
        if (findBar == null || !findBar.Visible) { ShowFind(); return; }
        var core = Core;
        if (core == null || findBar.Box.Text.Length == 0) return;
        if (core != findCore) { _ = FindStart(); return; }
        if (findJs) { _ = FindJs(core, findBar.Box.Text, dir, reset: false, ++findRun); return; }
        try
        {
            if (core.Find.MatchCount == 0) { _ = FindStart(); return; } // page changée depuis : on relance
            if (dir > 0) core.Find.FindNext(); else core.Find.FindPrevious();
        }
        catch { }
    }

    // Repli pour un moteur WebView2 trop ancien : comptage du texte + window.find
    const string FindJsCode = @"(function(t, dir, reset){
  let s = window.__lbFind;
  if (!s || reset || s.t !== t) {
    s = window.__lbFind = { t: t, i: 0, n: 0 };
    const low = t.toLowerCase(), w = document.createTreeWalker(document.body || document.documentElement, NodeFilter.SHOW_TEXT);
    for (let n; (n = w.nextNode());) {
      const p = n.parentElement; if (p && /^(SCRIPT|STYLE|NOSCRIPT|TEMPLATE)$/.test(p.tagName)) continue;
      const v = n.nodeValue.toLowerCase();
      for (let k = v.indexOf(low); k >= 0; k = v.indexOf(low, k + low.length)) s.n++;
    }
    getSelection().removeAllRanges();
  }
  if (!s.n) return JSON.stringify({ i: 0, n: 0 });
  if (window.find(t, false, dir < 0, true)) s.i = ((s.i - 1 + (dir < 0 ? -1 : 1)) % s.n + s.n) % s.n + 1;
  return JSON.stringify({ i: s.i, n: s.n });
})";

    async Task FindJs(CoreWebView2 core, string term, int dir, bool reset, int run)
    {
        try
        {
            var raw = await core.ExecuteScriptAsync(FindJsCode + "(" + JsonSerializer.Serialize(term) + "," + dir + "," + (reset ? "true" : "false") + ")");
            if (run != findRun || findBar == null) return;
            var n = JsonNode.Parse(JsonSerializer.Deserialize<string>(raw) ?? "{}");
            findBar.SetCounter(PageTools.FindCounter(n?["i"]?.GetValue<int>() ?? 0, n?["n"]?.GetValue<int>() ?? 0, term));
        }
        catch { }
    }

    void CloseFind()
    {
        if (findBar == null) return;
        UnbindFind(stop: true);
        findHiddenByFullscreen = false;
        findBar.Visible = false;
        findBar.SetCounter("");
        Cur?.View.Focus();
    }

    /// Changement d’onglet (appelé par SelectTab) : la recherche suit l’onglet affiché.
    void ToolsTabChanged()
    {
        toolToast?.Hide();
        if (findBar == null || !findBar.Visible) return;
        if (Core == null) { CloseFind(); return; }
        if (Core != findCore) _ = FindStart();
    }

    // ---------- Test réel, hors écran (LITEBROWSER_SHOTS_PAGES=outils=outils) ----------

    async Task ToolsTest(string name)
    {
        var r = new List<string>();
        void Check(string what, bool ok, string d = "") { r.Add((ok ? "OK    " : "ÉCHEC ") + what + (string.IsNullOrEmpty(d) ? "" : " · " + d)); Save(); }
        void Save() => File.WriteAllLines(Path.Combine(ShotsDir, name + ".txt"), r);
        try
        {
            // Page de test : 12 fois « Gentle Mates », 3 000 px de haut, une grande vidéo qui joue (dessinée en direct) et une petite en pause
            const string page = @"<!doctype html><html><head><meta charset='utf-8'><title>Test outils – Gentle Mates</title>
<style>body{margin:0;font:18px Segoe UI;background:#0e0e12;color:#f4f2ee}section{height:240px;padding:20px 40px;border-bottom:1px solid #26262d}
h1{color:#d669d2;margin:0 0 10px}video{display:block;background:#000}</style></head><body>
<section><h1>Outils de page</h1><video id=big width=640 height=200 muted autoplay playsinline></video></section>
<script>for(let i=1;i<=12;i++)document.write('<section>Section '+i+' : les Gentle Mates jouent ce soir. <video id=v'+i+' width=120 height=60 muted></video></section>');</script>
<script>const c=document.createElement('canvas');c.width=640;c.height=200;const g=c.getContext('2d');let f=0;
(function d(){g.fillStyle='#141418';g.fillRect(0,0,640,200);g.fillStyle='#d669d2';g.fillRect((f*4)%640,60,80,80);g.fillStyle='#fff';g.font='28px Segoe UI';g.fillText('M8 '+f,20,40);f++;requestAnimationFrame(d)})();
const v=document.getElementById('big');v.srcObject=c.captureStream(30);v.play();
const s=document.createElement('canvas').captureStream(5);document.getElementById('v1').srcObject=s;</script></body></html>";
            await NewTab();
            await Task.Delay(1200);
            var tab = Cur;
            var core = Core;
            var loaded = new TaskCompletionSource<bool>();
            void Done(object s, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult(e.IsSuccess);
            core.NavigationCompleted += Done;
            core.NavigateToString(page);
            await Task.WhenAny(loaded.Task, Task.Delay(8000));
            core.NavigationCompleted -= Done;
            await Task.Delay(1500);
            tab.Title = "Test outils – Gentle Mates";

            // --- Rechercher dans la page ---
            int viewTop0 = content.Top, viewH0 = tab.View.Height;
            var msg = new Message();
            bool handled = ProcessCmdKey(ref msg, Keys.Control | Keys.F);
            await Task.Delay(300);
            Check("Ctrl+F ouvre la barre de recherche", handled && findBar?.Visible == true);
            Check("la barre pousse la page au lieu de la recouvrir",
                findBar != null && findBar.Bottom <= content.Top && content.Top > viewTop0 && tab.View.Height < viewH0 && !findBar.Bounds.IntersectsWith(content.Bounds),
                $"barre {findBar?.Bounds}, page {content.Bounds}, hauteur de la page {viewH0} → {tab.View.Height}");
            Check("la barre est sous la barre d’outils", findBar != null && findBar.Top >= toolbar.Bottom && (!favBar.Visible || findBar.Top >= favBar.Bottom), $"barre d’outils {toolbar.Bounds}, favoris {favBar.Bounds}");
            findBar.Box.Text = "gentle mates";
            for (int i = 0; i < 30 && findBar.Counter != "1 / 12"; i++) await Task.Delay(100);
            Check("compteur « 1 / 12 » (12 fois Gentle Mates sur la page)", findBar.Counter == "1 / 12", $"« {findBar.Counter} »{(findJs ? " (repli par script)" : " (API Find de WebView2)")}");
            await ToolsShot(name + "_recherche");
            FindKey(Keys.Enter);
            for (int i = 0; i < 20 && findBar.Counter != "2 / 12"; i++) await Task.Delay(100);
            Check("Entrée : résultat suivant", findBar.Counter == "2 / 12", findBar.Counter);
            FindKey(Keys.Shift | Keys.Enter); await Task.Delay(300);
            FindKey(Keys.Shift | Keys.Enter);
            for (int i = 0; i < 20 && findBar.Counter != "12 / 12"; i++) await Task.Delay(100);
            Check("Maj+Entrée deux fois : revient au dernier (on boucle)", findBar.Counter == "12 / 12", findBar.Counter);
            ToolsKey(Keys.F3);
            for (int i = 0; i < 20 && findBar.Counter != "1 / 12"; i++) await Task.Delay(100);
            Check("F3 : suivant (revient au premier)", findBar.Counter == "1 / 12", findBar.Counter);
            findBar.Box.Text = "zzz introuvable";
            for (int i = 0; i < 30 && findBar.Counter != "Aucun résultat"; i++) await Task.Delay(100);
            Check("texte absent : « Aucun résultat »", findBar.Counter == "Aucun résultat", findBar.Counter);
            await ToolsShot(name + "_aucun");
            findBar.Box.Text = "Section 7";
            for (int i = 0; i < 30 && findBar.Counter != "1 / 1"; i++) await Task.Delay(100);
            var scrolled = await core.ExecuteScriptAsync("Math.round(scrollY)");
            Check("le résultat est amené à l’écran", findBar.Counter == "1 / 1" && int.TryParse(scrolled, out var sy) && sy > 600, $"{findBar.Counter}, défilement {scrolled} px");
            SetTheme("light"); await Task.Delay(400);
            await ToolsShot(name + "_recherche_clair");
            SetTheme("m8"); await Task.Delay(300);
            Check("la barre suit le thème", findBar.BackColor == theme.Bar, findBar.BackColor.ToString());
            FindKey(Keys.Escape);
            await Task.Delay(400);
            Check("Échap ferme la barre et rend la place à la page", !findBar.Visible && content.Top == viewTop0 && tab.View.Height == viewH0, $"page {content.Bounds}");
            var hl = await core.ExecuteScriptAsync("1");
            Check("la page répond toujours", hl == "1");
            await core.ExecuteScriptAsync("scrollTo(0,0)");
            await Task.Delay(300);

            // --- Captures ---
            var vis = await CapturePage(false);
            Check("capture de la zone visible enregistrée", vis != null && File.Exists(vis), vis ?? "aucun fichier");
            if (vis != null)
            {
                using var img = Image.FromFile(vis);
                var want = tab.View.ClientSize;
                Check("capture visible = taille de la page à l’écran", Math.Abs(img.Width - want.Width) <= 2 && Math.Abs(img.Height - want.Height) <= 2, $"{img.Width}×{img.Height} pour {want.Width}×{want.Height}");
                Check("nom du fichier : titre + date", Path.GetFileName(vis).StartsWith("Test outils – Gentle Mates – " + DateTime.Now.ToString("yyyy-MM-dd")), Path.GetFileName(vis));
                Check("dossier Images\\LiteBrowser (celui du test)", Path.GetDirectoryName(vis) == ShotsFolder(), Path.GetDirectoryName(vis));
            }
            Check("notification avec bouton « Ouvrir le dossier »", toolToast?.Visible == true && toolToast.ButtonText == "Ouvrir le dossier" && toolToast.HasAction,
                $"« {toolToast?.Title} » · {toolToast?.Body} · bouton « {toolToast?.ButtonText} »");
            Check("la notification ne prend pas le focus", Form.ActiveForm != toolToast);
            await ToolsShot(name + "_capture");
            var full = await CapturePage(true);
            Check("capture de la page entière enregistrée", full != null && File.Exists(full), full ?? "aucun fichier");
            if (full != null)
            {
                using var img = Image.FromFile(full);
                Check("page entière : toute la hauteur (≈ 3 400 px CSS)", img.Height > tab.View.ClientSize.Height * 2 && img.Height >= 3000, $"{img.Width}×{img.Height}");
                Check("nom du fichier « (page entière) »", Path.GetFileName(full).Contains("(page entière)"), Path.GetFileName(full));
                try { File.Copy(full, Path.Combine(ShotsDir, name + "_page_entiere.png"), true); } catch { }
            }
            var again = await CapturePage(false);
            Check("nouvelle capture : nouveau fichier, l’ancienne est gardée", again != null && again != vis && File.Exists(vis) && File.Exists(again), Path.GetFileName(again ?? ""));

            // --- Image dans l’image (sans ouvrir la vraie fenêtre : elle s’afficherait sur l’écran) ---
            var pip = await PictureInPicture(dry: true);
            Check("vidéo choisie : la plus grande en lecture", pip != null && pip.StartsWith("ok:playing:") && pip.EndsWith(":big"), pip);
            Check("aucun message quand tout va bien", PageTools.PipMessage(pip) == null);
            await core.ExecuteScriptAsync("document.getElementById('big').disablePictureInPicture = true");
            pip = await PictureInPicture(dry: true);
            Check("site qui refuse : message clair", pip == "refused" && toolToast?.Title == "Ce site refuse l’image dans l’image", $"{pip} · « {toolToast?.Title} »");
            await core.ExecuteScriptAsync("document.querySelectorAll('video').forEach(v => v.remove())");
            pip = await PictureInPicture(dry: true);
            Check("page sans vidéo : message clair", pip == "none" && toolToast?.Title == "Aucune vidéo sur cette page", $"{pip} · « {toolToast?.Title} » · {toolToast?.Body}");
            await ToolsShot(name + "_pip_sans_video");
            await core.ExecuteScriptAsync("document.body.appendChild(document.createElement('iframe'))");
            pip = await PictureInPicture(dry: true);
            Check("vidéo dans un cadre d’un autre site : expliqué", pip == "noneframes", $"{pip} · {toolToast?.Body}");

            // --- Menu ☰ > Outils de la page ---
            // (jamais ShowMenu ici : Windows ramène un menu hors écran sur l’écran visible)
            var tm = ToolsMenu();
            var labels = tm.DropDownItems.OfType<ToolStripMenuItem>().Select(x => x.Text + (x.ShortcutKeyDisplayString != null ? " [" + x.ShortcutKeyDisplayString + "]" : "")).ToList();
            Check("menu ☰ > « Outils de la page » : les 6 outils", tm.Text == "Outils de la page" && labels.Count == 6 && labels.Contains("Vidéo en image dans l’image [Alt+P]") && labels.Contains("Capturer la page entière [Ctrl+Maj+S]"), string.Join(" | ", labels));
            try
            {
                // rendu du sous-menu en mémoire, sans l’afficher
                var dd = (ToolStripDropDownMenu)tm.DropDown;
                dd.Renderer = new MenuRenderer(theme, iconSmall);
                dd.Font = new Font("Segoe UI", 9.5f);
                dd.PerformLayout();
                using var mb = new Bitmap(Math.Max(1, dd.Width), Math.Max(1, dd.Height));
                dd.DrawToBitmap(mb, new Rectangle(Point.Empty, dd.Size));
                mb.Save(Path.Combine(ShotsDir, name + "_menu.png"));
            }
            catch (Exception ex) { Check("rendu du sous-menu", false, ex.Message); }
            tm.Dispose();
            toolsMenu = null;

            // --- Imprimer : l’aperçu du moteur s’ouvre dans la page (dans la fenêtre hors écran) ---
            Exception perr = null;
            try { Core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); } catch (Exception ex) { perr = ex; }
            await Task.Delay(2500);
            Check("Ctrl+P : aperçu d’impression demandé sans erreur", perr == null, perr?.Message ?? "");
            await ToolsShot(name + "_imprimer");
            var alive = core.ExecuteScriptAsync("1");
            Check("la page répond pendant l’aperçu", await Task.WhenAny(alive, Task.Delay(3000)) == alive);

            // --- Raccourcis dans les réglages ---
            OpenSettings("raccourcis");
            await Task.Delay(2500);
            string keys = "";
            for (int i = 0; i < 20 && !keys.Contains("Capturer"); i++) { await Task.Delay(500); try { keys = await Core.ExecuteScriptAsync("(document.getElementById('keys')||{}).textContent||''"); } catch { } }
            Check("réglages > raccourcis : les nouveaux outils sont listés", keys.Contains("Capturer la zone visible") && keys.Contains("Ctrl+Maj+C") && keys.Contains("Imprimer") && keys.Contains("Alt+P"),
                keys.Length > 300 ? keys[..300] : keys);
            await ToolsShot(name + "_raccourcis");
            CloseTab(tabs.IndexOf(Cur));
            CloseTab(tabs.IndexOf(tab)); // ferme aussi l’aperçu d’impression
            await Task.Delay(500);
            Check("onglet de test fermé proprement", !tabs.Contains(tab));
        }
        catch (Exception ex) { Check("test interrompu", false, ex.ToString()); }
        Save();
    }

    /// Capture de la fenêtre + la notification et le sous-menu (fenêtres à part).
    async Task ToolsShot(string name)
    {
        await ShotAsync(name);
        try
        {
            var file = Path.Combine(ShotsDir, name + ".png");
            Bitmap bmp;
            using (var src = Image.FromFile(file)) bmp = new Bitmap(src);
            using (bmp)
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    void Draw(Control w)
                    {
                        if (w == null || !w.Visible || w.Width <= 0) return;
                        using var b = new Bitmap(w.Width, w.Height);
                        w.DrawToBitmap(b, new Rectangle(Point.Empty, w.Size));
                        g.DrawImage(b, new Point(w.Left - Left, w.Top - Top));
                    }
                    Draw(toolToast);
                    if (toolsMenu?.DropDown is { Visible: true } dd) Draw(dd);
                }
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch (Exception ex) { CrashLog.Write("Capture " + name, ex); }
    }
}

/// Barre « Rechercher dans la page » : posée entre la barre d’outils et la page (elle pousse la page, ne la recouvre jamais).
sealed class FindBar : Panel
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None, TabStop = true };
    public readonly IconButton Prev, Next, CloseBtn;
    public string Counter { get; private set; } = "";
    Theme theme = Theme.M8;
    readonly Font glyphFont, counterFont, hintFont;
    Rectangle field;
    const string Hint = "Entrée : suivant  ·  Maj+Entrée : précédent  ·  Échap : fermer";

    public FindBar(Font glyph, Font text)
    {
        glyphFont = glyph;
        counterFont = new Font("Segoe UI", 9f);
        hintFont = new Font("Segoe UI", 8.5f);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Box.Font = text;
        Prev = new IconButton("", glyph);
        Next = new IconButton("", glyph);
        CloseBtn = new IconButton(Glyphs.Close, glyph);
        Controls.AddRange(new Control[] { Box, Prev, Next, CloseBtn });
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Box.AccessibleName = "Rechercher dans la page";
    }

    int Sc(int v) => LogicalToDeviceUnits(v);

    public void SetCounter(string c)
    {
        c ??= "";
        if (c == Counter) return;
        Counter = c;
        Invalidate();
    }

    public void ApplyTheme(Theme t)
    {
        theme = t;
        BackColor = t.Bar;
        Box.BackColor = t.Field;
        Box.ForeColor = t.Text;
        foreach (var b in new[] { Prev, Next, CloseBtn }) { b.Theme = t; b.BackColor = t.Bar; b.Invalidate(); }
        Invalidate();
    }

    int CounterW => Math.Max(TextRenderer.MeasureText("888 / 888", counterFont).Width, TextRenderer.MeasureText("Aucun résultat", counterFont).Width) + Sc(4);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int h = Height - 1, b = Sc(28), gap = Sc(2), right = Width - Sc(12);
        CloseBtn.Bounds = new Rectangle(right - b, (h - b) / 2, b, b);
        Next.Bounds = new Rectangle(CloseBtn.Left - Sc(6) - b, (h - b) / 2, b, b);
        Prev.Bounds = new Rectangle(Next.Left - gap - b, (h - b) / 2, b, b);
        int fw = Math.Min(Sc(330), Math.Max(Sc(200), Prev.Left - Sc(20)));
        field = new Rectangle(Prev.Left - Sc(8) - fw, (h - Sc(30)) / 2, fw, Sc(30));
        int bh = Box.PreferredHeight;
        Box.Bounds = new Rectangle(field.X + Sc(32), field.Y + (field.Height - bh) / 2, Math.Max(Sc(40), field.Width - Sc(32) - CounterW - Sc(12)), bh);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(theme.Bar);
        using (var p = new Pen(theme.FieldBorder)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        // aide à gauche, seulement s’il reste de la place
        var hs = TextRenderer.MeasureText(Hint, hintFont);
        if (hs.Width + Sc(40) < field.Left)
            TextRenderer.DrawText(g, Hint, hintFont, new Rectangle(Sc(16), 0, field.Left - Sc(32), Height - 1), theme.Sub,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        bool none = Counter == "Aucun résultat";
        using (var path = G.Round(new RectangleF(field.X + .5f, field.Y + .5f, field.Width - 1, field.Height - 1), Sc(8)))
        {
            using (var b = new SolidBrush(theme.Field)) g.FillPath(b, path);
            using var pen = new Pen(Box.Focused || none ? theme.Accent : theme.FieldBorder, Box.Focused ? Sc(2) / 1.5f : 1);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Glyphs.Search, glyphFont, new Rectangle(field.X + Sc(4), field.Y, Sc(26), field.Height), theme.Sub, G.Center);
        if (Counter.Length > 0)
            TextRenderer.DrawText(g, Counter, counterFont, new Rectangle(field.Right - CounterW - Sc(10), field.Y, CounterW, field.Height), none ? theme.Accent : theme.Sub,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (field.Contains(e.Location)) Box.Focus(); // clic n’importe où dans la case
    }
}

/// Petite notification de LiteBrowser (capture enregistrée, message d’erreur) : ne prend jamais le focus, s’efface seule.
sealed class ToolToast : Form
{
    readonly Font titleFont, textFont;
    readonly PillButton button;
    readonly System.Windows.Forms.Timer hideTimer = new() { Interval = 6000 };
    Theme theme = Theme.M8;
    Image thumb;
    Action onButton;
    public string Title { get; private set; } = "";
    public string Body { get; private set; } = "";
    public string ButtonText { get; private set; }
    public bool HasAction => onButton != null;
    const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl;

    public ToolToast(Font title, Font text)
    {
        titleFont = title; textFont = text;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        button = new PillButton("", new Font("Segoe UI", 9f, FontStyle.Bold)) { Visible = false };
        button.Click += (_, _) => { var a = onButton; Hide(); a?.Invoke(); };
        Controls.Add(button);
        hideTimer.Tick += (_, _) =>
        {
            // pas d’effacement sous la souris : on attend qu’elle s’en aille
            if (Bounds.Contains(Cursor.Position)) return;
            Hide();
        };
        VisibleChanged += (_, _) => { if (!Visible) hideTimer.Stop(); };
        Click += (_, _) => Hide();
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

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE → MA_NOACTIVATE : un clic ne vole pas le focus
        base.WndProc(ref m);
    }

    int Sc(int v) => LogicalToDeviceUnits(v);

    /// Vignette du haut de l’image, aux proportions de la case.
    public static Image Thumbnail(Image src, int w, int h)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float k = Math.Max((float)w / src.Width, (float)h / src.Height);
        float sw = w / k, sh = h / k;
        g.DrawImage(src, new RectangleF(0, 0, w, h), new RectangleF((src.Width - sw) / 2, 0, sw, sh), GraphicsUnit.Pixel);
        return bmp;
    }

    public void Set(Theme t, string title, string text, Image th, string btn, Action act)
    {
        theme = t;
        Title = title ?? ""; Body = text ?? "";
        thumb?.Dispose();
        thumb = th;
        onButton = act;
        int w = Sc(380), pad = Sc(12);
        int tx = pad + (thumb != null ? thumb.Width + Sc(12) : Sc(10));
        int inner = w - tx - pad;
        int h1 = titleFont.Height;
        int h2 = Math.Min(TextRenderer.MeasureText(Body, textFont, new Size(inner, 1000), Wrap).Height, textFont.Height * 3);
        int bh = 0;
        button.Visible = btn != null;
        ButtonText = btn;
        if (btn != null)
        {
            button.Text = btn;
            button.Theme = t;
            button.BackColor = t.Bar;
            bh = Sc(26);
        }
        int h = Math.Max(thumb != null ? thumb.Height + pad * 2 : 0, pad + h1 + Sc(2) + h2 + (bh > 0 ? Sc(8) + bh : 0) + pad);
        Size = new Size(w, h);
        if (btn != null) button.Bounds = new Rectangle(tx, h - pad - bh, button.PreferredWidth, bh);
        using (var path = G.Round(new RectangleF(0, 0, w, h), Sc(12))) Region = new Region(path);
        BackColor = t.Bar;
        Invalidate();
    }

    public void Popup(IWin32Window owner)
    {
        hideTimer.Stop();
        if (!Visible)
        {
            Opacity = 0;
            if (Owner == null && owner is Form f) Owner = f;
            Show();
            Fade.Start(this, 160);
        }
        else Invalidate();
        hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(theme.Bar);
        using (var path = G.Round(new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), Sc(12)))
        using (var pen = new Pen(theme.FieldBorder)) g.DrawPath(pen, path);
        int pad = Sc(12), tx;
        if (thumb != null)
        {
            var r = new Rectangle(pad, (Height - thumb.Height) / 2, thumb.Width, thumb.Height);
            using (var clip = G.Round(r, Sc(6)))
            {
                g.SetClip(clip);
                g.DrawImage(thumb, r);
                g.ResetClip();
                using var pen = new Pen(theme.FieldBorder);
                g.DrawPath(pen, clip);
            }
            tx = r.Right + Sc(12);
        }
        else
        {
            // liseré rose à gauche
            using var b = new SolidBrush(theme.Accent);
            using var bar = G.Round(new RectangleF(pad, pad + Sc(2), Sc(3), Height - pad * 2 - Sc(4)), Sc(1));
            g.FillPath(b, bar);
            tx = pad + Sc(10);
        }
        int inner = Width - tx - pad, y = pad;
        TextRenderer.DrawText(g, Title, titleFont, new Rectangle(tx, y, inner, titleFont.Height), theme.Text,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        y += titleFont.Height + Sc(2);
        int h2 = Math.Min(TextRenderer.MeasureText(Body, textFont, new Size(inner, 1000), Wrap).Height, textFont.Height * 3);
        TextRenderer.DrawText(g, Body, textFont, new Rectangle(tx, y, inner, h2), theme.Sub, Wrap);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { hideTimer.Dispose(); thumb?.Dispose(); }
        base.Dispose(disposing);
    }
}
