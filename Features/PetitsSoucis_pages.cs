// Petits soucis du quotidien : pages et barre d'adresse, réglés pour se comporter comme Chrome / Brave.
// Menu « Coller et aller », Ctrl+Entrée, historique au clic droit sur précédent / suivant,
// clic droit qui ne déclenche plus les boutons, cadenas sur un certificat invalide, liens Ctrl+clic / clic du milieu
// en arrière-plan, plein écran qu'on sait quitter, panneau des téléchargements, adresse lisible (accents),
// pages d'erreur aux couleurs de LiteBrowser, collage d'un texte sur plusieurs lignes.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using LiteCore;

partial class BrowserForm
{
    readonly ConditionalWeakTable<BrowserTab, string> pageTrouble = new(); // « cert » : certificat invalide ; « failed » : page pas chargée
    // Sites dont le certificat a été refusé pendant cette session. Après « Continuer vers le site », le moteur garde la décision
    // et ne signale plus rien : sans cette liste, le cadenas « Connexion sécurisée » revenait sur le site.
    static readonly HashSet<string> certHosts = new(StringComparer.OrdinalIgnoreCase);
    (BrowserTab Tab, DateTime At) lastBgClick;                           // dernier Ctrl+clic / clic du milieu sur un lien
    BrowserTab lastBgOpener, lastBgTab;                                  // pour ranger les onglets ouverts en arrière-plan à la suite
    bool f11Kept;                                                        // F11 était déjà actif quand la vidéo est passée en plein écran
    FormWindowState shownState = FormWindowState.Maximized;              // état de la fenêtre avant d'être réduite
    FullscreenHint fsHint;

    // Points d'entrée des tests (jamais le vrai presse-papiers, ni une vraie boîte de dialogue, ni le vrai plein écran)
    Func<string> clipGet = () => { try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { return ""; } };
    Action<string> clipSet = s => { try { if (!string.IsNullOrEmpty(s)) Clipboard.SetText(s); } catch { } };
    Func<string, string> askSavePathTest;
    Action<bool> fullscreenTest;
    List<string> notifyTest;

    /// Appelé une fois, à la fin du constructeur.
    void PagesInit()
    {
        var m = new ContextMenuStrip { ShowItemToolTips = true };
        Fade.In(m); // comme le menu ⋯ : refermé par un clic dans la page
        m.Opening += (_, _) => FillAddrMenu(m);
        addr.Box.ContextMenuStrip = m;
        backBtn.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) _ = ShowNavHistory(backBtn, -1); };
        fwdBtn.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) _ = ShowNavHistory(fwdBtn, 1); };
        tip.SetToolTip(backBtn, "Page précédente (Alt+←) · clic droit : pages précédentes");
        tip.SetToolTip(fwdBtn, "Page suivante (Alt+→) · clic droit : pages suivantes");
        // Ctrl+V / Maj+Inser d'un texte sur plusieurs lignes : le champ d'une ligne ne gardait que la première
        addr.Box.KeyDown += (_, e) =>
        {
            if (!((e.Control && !e.Alt && e.KeyCode == Keys.V) || (e.Shift && !e.Control && e.KeyCode == Keys.Insert))) return;
            var c = (clipGet() ?? "").Trim();
            if (c.IndexOfAny(new[] { '\r', '\n' }) < 0) return;
            e.SuppressKeyPress = true; e.Handled = true;
            addrEdited = true;
            addr.Box.SelectedText = PasteText(c);
        };
        shownState = WindowState;
        Resize += (_, _) => { if (WindowState != FormWindowState.Minimized && !fullscreen) shownState = WindowState; };
    }

    /// Appelé par StartTab pour chaque onglet, avant sa première page.
    async Task HookPages(BrowserTab t, CoreWebView2 core)
    {
        ulong navId = 0; // seule la dernière navigation compte (une ancienne qui finit en retard ne touche plus au cadenas)
        core.NavigationStarting += (_, e) => navId = e.NavigationId; // rien d'effacé ici : la navigation peut encore être annulée
        // nouvelle page affichée : avertissement repris si son site a déjà montré un certificat invalide (« Continuer vers le site »)
        core.SourceChanged += (_, _) => { MarkSite(t, core.Source); if (t == Cur) PagesUi(); };
        core.NavigationCompleted += (_, e) =>
        {
            if (e.NavigationId != navId) return;
            if (!e.IsSuccess && IsCertError(e.WebErrorStatus))
            {
                lock (certHosts) certHosts.Add(HostOf(core.Source));
                pageTrouble.AddOrUpdate(t, "cert");
                _ = BrandErrorPage(core, e.WebErrorStatus, true);
            }
            else if (!e.IsSuccess && e.HttpStatusCode == 0 && e.WebErrorStatus is not (CoreWebView2WebErrorStatus.OperationCanceled
                or CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.Unknown))
            {
                pageTrouble.AddOrUpdate(t, "failed");
                _ = BrandErrorPage(core, e.WebErrorStatus, false);
            }
            else if (e.IsSuccess) MarkSite(t, core.Source); // page rechargée avec succès à la même adresse : plus « inaccessible »
            if (t == Cur) PagesUi();
        };
        try
        {
            // Levé pour TOUTES les ressources (image, pub, traqueur d'un autre site) : seule une ressource du site de la page
            // affichée la marque. Une ressource d'un autre site au certificat invalide ne touche plus au cadenas.
            core.ServerCertificateErrorDetected += (_, e) =>
            {
                var h = HostOf(e.RequestUri);
                if (h.Length == 0) return;
                lock (certHosts) certHosts.Add(h);
                if (h == HostOf(core.Source)) pageTrouble.AddOrUpdate(t, "cert");
            };
        }
        catch { }
        core.WebMessageReceived += (_, e) =>
        {
            try { if (e.WebMessageAsJson.StartsWith("{\"lbBackgroundLink\"")) lastBgClick = (t, DateTime.Now); } catch { }
        };
        try { await core.AddScriptToExecuteOnDocumentCreatedAsync(BackgroundLinkJs); } catch { }
    }

    // ================== Barre d'adresse ==================

    /// Appelé à la fin de UpdateUi : cadenas d'un certificat invalide, bulle de l'étoile.
    void PagesUi()
    {
        var c = Core;
        string src = c?.Source ?? "";
        bool home = Cur == null || Cur.IsHome || src == "" || src == "about:blank";
        if (!home && Cur != null && pageTrouble.TryGetValue(Cur, out var trouble) && !IsLocalPage(src))
        {
            // avant : cadenas et « Connexion sécurisée » même sur la page d'erreur du certificat ou une page pas chargée
            addr.BadgeGlyph = trouble == "cert" ? Glyphs.Warning : Glyphs.Globe;
            tip.SetToolTip(addr, trouble == "cert" ? "Certificat du site invalide : connexion non sécurisée" : "Page inaccessible : rien n'a été chargé depuis ce site");
        }
        bool fav = !home && favorites.Exists(f => f.Url == src);
        tip.SetToolTip(addr.Star, fav ? "Retirer des favoris (Ctrl+D)" : "Ajouter aux favoris (Ctrl+D)");
    }

    // ================== Pages d'erreur ==================

    /// Pages d'erreur du moteur (Edge) aux couleurs de LiteBrowser : plus de logo ni de nom « Microsoft Edge »,
    /// texte en français au tutoiement, bouton rose M8. La page reste celle du moteur (même adresse ; Réessayer / Continuer marchent).
    async Task BrandErrorPage(CoreWebView2 core, CoreWebView2WebErrorStatus status, bool cert)
    {
        var host = HostOf(core.Source);
        string title, text;
        if (cert) { title = "Ta connexion n’est pas privée"; text = "Le certificat de « " + host + " » n’est pas valable : quelqu’un pourrait lire ce que tu envoies (mots de passe, messages, cartes bancaires). Ne continue que si tu sais ce que tu fais."; }
        else (title, text) = status switch
        {
            CoreWebView2WebErrorStatus.Disconnected => ("Pas de connexion à internet", "LiteBrowser n’arrive pas à joindre internet. Vérifie ton Wi-Fi ou ton câble réseau, puis réessaie."),
            CoreWebView2WebErrorStatus.HostNameNotResolved => ("Site introuvable", "L’adresse « " + host + " » ne mène à aucun site. Vérifie qu’elle est bien écrite, ou ta connexion."),
            CoreWebView2WebErrorStatus.Timeout => ("Le site ne répond pas", "« " + host + " » met trop de temps à répondre. Il est peut-être surchargé : réessaie dans un instant."),
            CoreWebView2WebErrorStatus.RedirectFailed => ("Cette page tourne en rond", "« " + host + " » renvoie d’une page à l’autre sans jamais s’arrêter. Réessaie plus tard."),
            CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse => ("Réponse du site illisible", "« " + host + " » a renvoyé une réponse que LiteBrowser ne comprend pas."),
            CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired or CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired
                => ("Identification demandée", "Ce site ou ton réseau demande une identification que LiteBrowser n’a pas pu fournir."),
            _ => ("Impossible d’ouvrir cette page", "« " + host + " » a refusé ou coupé la connexion. Vérifie ta connexion, puis réessaie."),
        };
        static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var data = JsonSerializer.Serialize(new { title, text, cert, bg = Hex(theme.Strip), card = Hex(theme.Bar), fg = Hex(theme.Text), sub = Hex(theme.Sub), accent = Hex(theme.Accent), line = Hex(theme.FieldBorder) });
        try { await core.ExecuteScriptAsync("(" + BrandErrorJs + ")(" + data + ")"); } catch { }
    }

    // Styles posés par le CSSOM (el.style), jamais par une balise <style> : la page d'erreur du moteur a sa propre CSP.
    // Les boutons du moteur (Réessayer, Avancé, Continuer vers le site) sont repris tels quels : ils gardent leur action.
    const string BrandErrorJs = @"function(d){try{
if(location.protocol!=='chrome-error:'&&!document.getElementById('main-frame-error')&&!document.getElementById('proceed-link'))return 'pas une page d\'erreur';
if(document.getElementById('lb-error'))return 'déjà';
const S=(el,css)=>{el.style.cssText+=';'+css;return el;};
const E=(tag,css,txt)=>{const e=S(document.createElement(tag),css);if(txt!=null)e.textContent=txt;return e;};
const font=""'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif"";
const btn=(b,primary)=>{if(!b)return b;b.removeAttribute('class');b.style.cssText='';S(b,'all:unset;box-sizing:border-box;display:inline-block;cursor:pointer;padding:10px 22px;border-radius:999px;font:600 14px '+font+';margin:6px 10px 0 0;'+(primary?'background:'+d.accent+';color:#0b0b0d;':'background:transparent;color:'+d.fg+';border:1px solid '+d.line+';'));b.onmouseenter=()=>b.style.filter='brightness(1.12)';b.onmouseleave=()=>b.style.filter='';return b;};
const code=((document.querySelector('.error-code')||{}).textContent||'').trim();
const root=E('div','box-sizing:border-box;min-height:100vh;display:flex;align-items:center;justify-content:center;padding:40px 16px;background:'+d.bg+';font-family:'+font+';color:'+d.fg+';');
root.id='lb-error';
const card=E('div','box-sizing:border-box;max-width:560px;width:100%;background:'+d.card+';border:1px solid '+d.line+';border-radius:18px;padding:34px 34px 28px;box-shadow:0 18px 50px rgba(0,0,0,.35);');
const mark=E('div','width:46px;height:46px;border-radius:50%;display:flex;align-items:center;justify-content:center;font:700 24px '+font+';margin-bottom:18px;background:'+(d.cert?'rgba(255,107,122,.14)':'rgba(214,105,210,.14)')+';color:'+(d.cert?'#ff6b7a':d.accent)+';',d.cert?'!':'?');
card.append(mark,E('h1','margin:0 0 10px;font:700 24px/1.25 '+font+';color:'+d.fg+';',d.title),E('p','margin:0 0 18px;font:400 15px/1.55 '+font+';color:'+d.sub+';',d.text));
const actions=E('div','display:flex;flex-wrap:wrap;align-items:center;');
card.append(actions);
if(d.cert){
 const back=btn(document.getElementById('primary-button'),true);if(back){back.textContent='Revenir en sécurité';actions.append(back);}
 const adv=btn(document.getElementById('details-button'),false);if(adv){actions.append(adv);}
 const det=document.getElementById('details');const link=document.getElementById('proceed-link');
 if(det){det.querySelectorAll('*').forEach(n=>{n.style.color=d.sub;n.style.fontFamily=font;});S(det,'margin-top:18px;font:14px/1.5 '+font+';color:'+d.sub+';');card.append(det);}
 if(link){S(link,'color:'+d.accent+';font:600 14px '+font+';text-decoration:underline;cursor:pointer;');}
}else{
 let re=document.getElementById('reload-button');
 if(!re){re=document.createElement('button');re.onclick=()=>location.reload();}
 btn(re,true);re.textContent='Réessayer';actions.append(re);
}
if(code)card.append(E('div','margin-top:22px;font:12px Consolas,monospace;color:'+d.sub+';opacity:.75;',code));
const keep=[...document.body.children].filter(n=>n.tagName==='SCRIPT'||n.tagName==='TEMPLATE');
document.body.replaceChildren(root,...keep);root.append(card);
S(document.body,'margin:0;background:'+d.bg+';');S(document.documentElement,'background:'+d.bg+';color-scheme:dark;');
return 'ok';}catch(e){return 'erreur '+e.message;}}";

    static string HostOf(string url) => Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) ? u.Host : "";

    /// Avertissement de la page affichée d'après son site (certificat refusé plus tôt dans la session ou non).
    void MarkSite(BrowserTab t, string url)
    {
        var h = HostOf(url);
        bool bad; lock (certHosts) bad = h.Length > 0 && certHosts.Contains(h);
        if (bad) pageTrouble.AddOrUpdate(t, "cert"); else pageTrouble.Remove(t);
    }

    static bool IsCertError(CoreWebView2WebErrorStatus s) => s is CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
        or CoreWebView2WebErrorStatus.CertificateExpired or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors
        or CoreWebView2WebErrorStatus.CertificateRevoked or CoreWebView2WebErrorStatus.CertificateIsInvalid;

    /// Adresse affichée hors édition : les accents de l'adresse (%C3%A9…) redeviennent lisibles, comme dans Chrome.
    /// Les caractères qui changeraient le sens de l'adresse ou pourraient tromper (/ ? # & espace, contrôle de sens…) restent codés.
    static string PrettyAddress(string url)
    {
        var s = Omni.Display(url);
        if (string.IsNullOrEmpty(s) || s.IndexOf('%') < 0) return s;
        int slash = s.IndexOf('/', s.IndexOf("://", StringComparison.Ordinal) is int p && p >= 0 ? p + 3 : 0);
        if (slash < 0) return s; // jamais dans le nom du site
        var strict = new UTF8Encoding(false, true);
        var path = Regex.Replace(s[slash..], "(?:%[0-9A-Fa-f]{2})+", m =>
        {
            var bytes = Enumerable.Range(0, m.Length / 3).Select(i => Convert.ToByte(m.Value.Substring(i * 3 + 1, 2), 16)).ToArray();
            string text;
            try { text = strict.GetString(bytes); } catch { return m.Value; }
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                var cat = char.GetUnicodeCategory(ch);
                bool keep = ch < 0x21 || ch == 0x7F || "%/?#&=+\\\"<>`".IndexOf(ch) >= 0 || char.IsSurrogate(ch) ||
                    cat is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control
                        or System.Globalization.UnicodeCategory.SpaceSeparator or System.Globalization.UnicodeCategory.LineSeparator
                        or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.OtherNotAssigned;
                if (keep) foreach (var b in Encoding.UTF8.GetBytes(ch.ToString())) sb.Append('%').Append(b.ToString("X2"));
                else sb.Append(ch);
            }
            return sb.ToString();
        });
        return s[..slash] + path;
    }

    /// Ctrl+Entrée : « youtube » → https://www.youtube.com/ (comme Chrome). null si ce n'est pas un simple mot.
    string CtrlEnterUrl(TextBox b)
    {
        var text = b.Text;
        // suite proposée en ligne (sélectionnée à la fin) : on garde ce que tu as vraiment tapé
        if (b.SelectionLength > 0 && b.SelectionStart + b.SelectionLength == text.Length) text = text[..b.SelectionStart];
        text = text.Trim();
        if (text.Length == 0 || text.StartsWith(":") || text.Contains("://") || text.Any(ch => char.IsWhiteSpace(ch) || ch == '.' || ch == '/')) return null;
        return "https://www." + text.ToLowerInvariant() + ".com/";
    }

    void FillAddrMenu(ContextMenuStrip m)
    {
        m.Renderer = new MenuRenderer(theme, iconSmall);
        m.Font = new Font("Segoe UI", 9.5f);
        foreach (var old in m.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        m.Items.Clear();
        var b = addr.Box;
        bool selected = b.SelectionLength > 0;
        var clip = (clipGet() ?? "").Trim();
        void Edit(Action a) { b.Focus(); addrEdited = true; a(); }
        var undo = MI("Annuler", () => Edit(b.Undo), "Ctrl+Z"); undo.Enabled = b.CanUndo; m.Items.Add(undo);
        m.Items.Add(new ToolStripSeparator());
        var cut = MI("Couper", () => { CopyAddr(); Edit(() => b.SelectedText = ""); }, "Ctrl+X"); cut.Enabled = selected; m.Items.Add(cut);
        var copy = MI("Copier", CopyAddr, "Ctrl+C"); copy.Enabled = selected; m.Items.Add(copy);
        var paste = MI("Coller", () => Edit(() => b.SelectedText = PasteText(clip)), "Ctrl+V"); paste.Enabled = clip.Length > 0; m.Items.Add(paste);
        var go = MI(PasteGoLabel(clip), () => PasteAndGo(clip)); go.Enabled = clip.Length > 0; go.Font = new Font(m.Font, FontStyle.Bold); m.Items.Add(go);
        var del = MI("Supprimer", () => Edit(() => b.SelectedText = ""), "Suppr"); del.Enabled = selected; m.Items.Add(del);
        m.Items.Add(new ToolStripSeparator());
        var all = MI("Tout sélectionner", () => { b.Focus(); b.SelectAll(); }, "Ctrl+A"); all.Enabled = b.TextLength > 0; m.Items.Add(all);
    }

    static bool ClipIsUrl(string clip) => clip.Contains("://") || (!clip.Contains(' ') && UrlTools.LooksLikeUrl(clip));

    /// Texte copié sur plusieurs lignes → une seule ligne, comme Chrome (une adresse coupée en deux est recollée).
    static string PasteText(string clip) => OneLine(clip, ClipIsUrl(OneLine(clip, true)));

    static string OneLine(string clip, bool url) => Regex.Replace(clip, @"\s*[\r\n]+\s*", url ? "" : " ").Trim();

    static string PasteGoLabel(string clip)
    {
        if (clip.Length == 0) return "Coller et aller";
        bool url = ClipIsUrl(OneLine(clip, true));
        var t = OneLine(clip, url);
        if (t.Length > 40) t = t[..40] + "…";
        return (url ? "Coller et aller sur « " : "Coller et rechercher « ") + t + " »";
    }

    void PasteAndGo(string clip)
    {
        bool url = ClipIsUrl(OneLine(clip ?? "", true));
        var target = ResolveTyped(OneLine(clip ?? "", url));
        if (target == null) return;
        addrEdited = false;
        HideOmni();
        Go(target);
    }

    /// « Copier » sur l'adresse affichée en entier : c'est l'adresse complète (https://…) qui part, comme dans Chrome.
    void CopyAddr()
    {
        var b = addr.Box;
        var src = Core?.Source;
        if (!addrEdited && b.SelectionLength == b.TextLength && Omni.IsWeb(src) && !IsLocalPage(src)) clipSet(src);
        else clipSet(b.SelectedText);
    }

    // ================== Précédent / suivant : historique au clic droit ==================

    async Task<(int Current, List<(int Id, string Url, string Title)> Entries)> NavEntries(CoreWebView2 c)
    {
        var list = new List<(int, string, string)>();
        var json = await c.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}");
        using var doc = JsonDocument.Parse(json);
        int cur = doc.RootElement.GetProperty("currentIndex").GetInt32();
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            list.Add((e.GetProperty("id").GetInt32(), e.GetProperty("url").GetString() ?? "", e.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : ""));
        return (cur, list);
    }

    /// Menu des pages précédentes (dir = -1) ou suivantes (+1), la plus proche en haut. null s'il n'y en a pas.
    async Task<ContextMenuStrip> NavHistoryMenu(int dir)
    {
        var c = Core;
        if (c == null) return null;
        (int Current, List<(int Id, string Url, string Title)> Entries) h;
        try { h = await NavEntries(c); } catch { return null; }
        var picks = new List<(int Id, string Url, string Title)>();
        for (int i = h.Current + dir; i >= 0 && i < h.Entries.Count && picks.Count < 12; i += dir) picks.Add(h.Entries[i]);
        if (picks.Count == 0 || c != Core) return null;
        var r = new MenuRenderer(theme, iconSmall);
        var m = new ContextMenuStrip { Renderer = r, Font = new Font("Segoe UI", 9.5f), ShowItemToolTips = true };
        Fade.In(m);
        foreach (var p in picks)
        {
            string title = IsHome(p.Url) ? "Page d'accueil" : IsSettings(p.Url) ? "Réglages" : string.IsNullOrWhiteSpace(p.Title) ? Omni.Display(p.Url) : p.Title;
            var it = MI(title.Length > 60 ? title[..60] + "…" : title, () => _ = c.CallDevToolsProtocolMethodAsync("Page.navigateToHistoryEntry", "{\"entryId\":" + p.Id + "}"));
            it.ToolTipText = IsLocalPage(p.Url) ? null : p.Url;
            if (Omni.IsWeb(p.Url) && !IsLocalPage(p.Url)) it.Image = CachedIcon(p.Url);
            m.Items.Add(it);
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("Tout l'historique…", () => OpenSettings("historique"), "Ctrl+H"));
        return m;
    }

    async Task ShowNavHistory(IconButton b, int dir)
    {
        var m = await NavHistoryMenu(dir);
        if (m == null || IsDisposed) return;
        lastMenu?.Dispose();
        lastMenu = m;
        m.Show(b, new Point(0, b.Height + S(4)));
    }

    // ================== Liens ouverts en arrière-plan ==================

    // Ctrl+clic ou clic du milieu sur un lien : signalé juste avant que la page demande le nouvel onglet.
    // Écouteur discret (rien de visible ni de modifié dans la page, aucune variable globale).
    const string BackgroundLinkJs =
        "(()=>{try{const w=window.chrome&&window.chrome.webview;if(!w||window.top!==window)return;" +
        "const f=e=>{if(e.shiftKey||e.altKey)return;if(!(e.button===1||(e.button===0&&(e.ctrlKey||e.metaKey))))return;" +
        "const p=e.composedPath?e.composedPath():[];const a=p.find(n=>n&&n.tagName==='A'&&n.href);if(!a)return;" +
        "w.postMessage({lbBackgroundLink:String(a.href)});};" +
        "addEventListener('click',f,true);addEventListener('auxclick',f,true);}catch(_){}})();";

    /// La fenêtre demandée vient-elle d'un Ctrl+clic / clic du milieu ? (Chrome l'ouvre alors sans quitter la page.)
    bool TakeBackgroundLink(BrowserTab t, CoreWebView2NewWindowRequestedEventArgs e)
    {
        bool bg = lastBgClick.Tab == t && (DateTime.Now - lastBgClick.At).TotalMilliseconds < 1500 && e.IsUserInitiated;
        if (bg) lastBgClick = default;
        return bg;
    }

    /// Place d'un onglet ouvert en arrière-plan : à la suite des précédents ouverts depuis la même page.
    int BackgroundIndex(int idx)
    {
        if (lastBgOpener == Cur && lastBgTab != null && tabs.IndexOf(lastBgTab) is int k && k >= sel && k >= 0) idx = k + 1;
        return Math.Max(idx, tabs.TakeWhile(x => x.Pinned).Count());
    }

    /// L'onglet vient d'être inséré à idx sans être affiché : l'onglet affiché reste le même.
    void KeepSelection(BrowserTab t, int idx)
    {
        if (idx <= sel) sel++;
        lastBgOpener = sel >= 0 && sel < tabs.Count ? tabs[sel] : null;
        lastBgTab = t;
        strip.Selected = sel;
        strip.Invalidate();
        SaveSessionSoon();
    }

    // ================== Plein écran ==================

    void FullscreenTo(bool on)
    {
        if (fullscreenTest != null) { fullscreen = on; fullscreenTest(on); return; }
        SetFullscreen(on);
    }

    /// Vidéo (ou page) en plein écran, ou sortie. Sortir de la vidéo ne fait plus quitter le plein écran F11 d'avant.
    void PageFullscreenChanged(bool on)
    {
        if (on)
        {
            if (pageFullscreen) return;
            f11Kept = fullscreen;
            pageFullscreen = true;
            FullscreenTo(true);
            ShowFsHint("Appuie sur Échap pour quitter le plein écran");
        }
        else
        {
            if (!pageFullscreen) return;
            pageFullscreen = false;
            fsHint?.Hide();
            if (!f11Kept) FullscreenTo(false);
            f11Kept = false;
        }
    }

    /// F11 (et « Plein écran » du menu). Pendant une vidéo en plein écran : on en sort, comme dans Chrome.
    void ToggleF11()
    {
        if (pageFullscreen)
        {
            f11Kept = false;
            try { _ = Core?.ExecuteScriptAsync("document.fullscreenElement && document.exitFullscreen()"); } catch { }
            return;
        }
        FullscreenTo(!fullscreen);
        if (fullscreen) ShowFsHint("Appuie sur F11 pour quitter le plein écran");
        else fsHint?.Hide();
    }

    /// Petite bulle en haut de l'écran pendant 2,5 s (jamais sur Nur Live ni le multistream : rien sur un lecteur Twitch).
    void ShowFsHint(string text)
    {
        if (IsDisposed || KeepsPlaying(Core)) { fsHint?.Hide(); return; }
        fsHint ??= new FullscreenHint();
        fsHint.Popup(this, theme, text, Bounds);
    }

    // ================== Téléchargements ==================

    /// « Demander où enregistrer » : la boîte de Windows, puis le panneau des téléchargements (progression, Ouvrir, Dossier)
    /// s'affiche comme pour les autres fichiers (avant, il était masqué : aucun retour pendant le téléchargement).
    void AskDownloadPath(CoreWebView2DownloadStartingEventArgs e)
    {
        string chosen;
        if (askSavePathTest != null) chosen = askSavePathTest(e.ResultFilePath);
        else
        {
            var name = Path.GetFileName(e.ResultFilePath);
            var ext = Path.GetExtension(name).TrimStart('.');
            using var dlg = new SaveFileDialog
            {
                FileName = name,
                InitialDirectory = Path.GetDirectoryName(e.ResultFilePath),
                Title = "Enregistrer le fichier",
                OverwritePrompt = true,
                AddExtension = ext.Length > 0,
                DefaultExt = ext,
                Filter = ext.Length > 0 ? $"Fichier .{ext}|*.{ext}|Tous les fichiers|*.*" : "Tous les fichiers|*.*",
            };
            chosen = dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
        }
        if (chosen == null) { e.Cancel = true; return; }
        e.ResultFilePath = chosen;
    }

    /// Notifications pendant le test : notées, jamais affichées sur l'écran.
    bool NotifyQuiet(string title, string text)
    {
        if (notifyTest == null) return false;
        notifyTest.Add(title + " · " + text);
        return true;
    }

    // ================== Test hors écran ==================

    [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] static extern bool PostKey(IntPtr h, int msg, IntPtr w, IntPtr l);

    async Task PagesTest(string name)
    {
        var r = new List<string>();
        void C(string what, bool ok, string d = "")
        {
            r.Add((ok ? "OK    " : "ÉCHEC ") + what + (d.Length > 0 ? " · " + d : ""));
            File.WriteAllLines(Path.Combine(ShotsDir, name + ".txt"), r);
        }
        void Info(string what) { r.Add("INFO  " + what); File.WriteAllLines(Path.Combine(ShotsDir, name + ".txt"), r); }
        async Task<bool> Until(Func<bool> f, int ms = 8000) { for (int i = 0; i < ms / 100; i++) { if (f()) return true; await Task.Delay(100); } return f(); }
        async Task<string> Js(CoreWebView2 c, string s) { var p = c.ExecuteScriptAsync(s); return await Task.WhenAny(p, Task.Delay(4000)) == p ? p.Result : "(pas de réponse)"; }
        async Task<bool> UntilJs(string js, string want, int ms) { for (int i = 0; i < ms / 200; i++) { if (await Js(Core, js) == want) return true; await Task.Delay(200); } return await Js(Core, js) == want; }
        async Task Nav(string url) { Core.Navigate(url); await Task.Delay(300); await Until(() => Cur?.Loading == false, 15000); await Task.Delay(400); }
        notifyTest = new List<string>();
        fullscreenTest = _ => { };
        var dl = Path.Combine(ShotsDir, "telechargements");
        Directory.CreateDirectory(dl);

        // Touche envoyée au vrai champ (message Windows), Ctrl tenu dans l'état clavier de ce fil seulement : sans focus, sans fenêtre
        async Task CtrlKey(Keys key)
        {
            var saved = new byte[256]; GetKeyboardState(saved);
            var st = (byte[])saved.Clone(); st[0x11] = 0x80; st[0xA2] = 0x80; SetKeyboardState(st);
            try
            {
                PostKey(addr.Box.Handle, 0x100, (IntPtr)(int)key, (IntPtr)1);
                PostKey(addr.Box.Handle, 0x101, (IntPtr)(int)key, unchecked((IntPtr)(int)0xC0000001));
                for (int i = 0; i < 6; i++) { Application.DoEvents(); await Task.Delay(30); }
            }
            finally { SetKeyboardState(saved); }
        }

        try
        {
            await NewTab("https://example.com/");
            await Until(() => Cur?.Loading == false && Core?.Source?.Contains("example.com") == true, 15000);
            await Task.Delay(800);

            // 1. Ctrl+Retour arrière (soupçon vérifié : le champ efface déjà le mot, rien à corriger)
            addrEdited = true;
            addr.Box.Text = "youtube.com/watch"; addr.Box.SelectionStart = addr.Box.TextLength;
            await CtrlKey(Keys.Back);
            var after1 = addr.Box.Text;
            C("(vérification) Ctrl+Retour arrière efface le mot d'avant, sans petit carré", after1 == "youtube.com/" && !after1.Contains('\x7f'), $"« {after1} »");

            // 2. Ctrl+Entrée
            addr.Box.Text = "example"; addr.Box.SelectionStart = addr.Box.TextLength;
            await CtrlKey(Keys.Enter);
            await Task.Delay(300); await Until(() => Cur?.Loading == false, 15000);
            C("Ctrl+Entrée : « example » ouvre www.example.com (comme Chrome)", Core.Source.StartsWith("https://www.example.com"), Core.Source);
            addrEdited = false;

            // 3. Menu de la barre d'adresse : Coller et aller, Copier l'adresse complète
            await Nav("https://example.com/");
            UpdateUi();
            var menu = addr.Box.ContextMenuStrip;
            string clip = "example.org";
            clipGet = () => clip;
            string copied = null;
            clipSet = s => copied = s;
            FillAddrMenu(menu);
            var labels = menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList();
            Info("menu de la barre d'adresse : " + string.Join(" | ", labels));
            var goItem = menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text.StartsWith("Coller et aller"));
            C("le menu du clic droit propose « Coller et aller sur « example.org » »", goItem?.Text == "Coller et aller sur « example.org »" && goItem.Enabled, goItem?.Text ?? "absent");
            goItem?.PerformClick();
            await Task.Delay(300); await Until(() => Cur?.Loading == false && Core.Source.Contains("example.org"), 15000);
            C("« Coller et aller » ouvre l'adresse copiée", Core.Source.Contains("example.org"), Core.Source);
            clip = "gentle mates\r\nvalorant";
            FillAddrMenu(menu);
            var search = menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text.StartsWith("Coller et "));
            C("texte copié qui n'est pas une adresse : « Coller et rechercher » (sur une ligne)", search?.Text == "Coller et rechercher « gentle mates valorant »", search?.Text ?? "");
            UpdateUi();
            addr.Box.SelectAll();
            FillAddrMenu(menu);
            menu.Items.OfType<ToolStripMenuItem>().First(i => i.Text == "Copier").PerformClick();
            C("« Copier » sur l'adresse affichée copie l'adresse complète (https://…)", copied == "https://example.org/", $"affichée « {addr.Box.Text} », copiée « {copied} »");
            // Ctrl+V d'une adresse coupée sur deux lignes : avant, seule la première ligne restait
            addrEdited = true;
            addr.Box.Text = ""; clip = "https://fr.wikipedia.org/wiki/\r\nGentle_Mates";
            await CtrlKey(Keys.V);
            C("Ctrl+V d'une adresse sur deux lignes : recollée en entier", addr.Box.Text == "https://fr.wikipedia.org/wiki/Gentle_Mates", $"« {addr.Box.Text} »");
            addr.Box.Text = ""; clip = "gentle mates\r\nvalorant";
            await CtrlKey(Keys.V);
            C("Ctrl+V d'un texte sur deux lignes : une seule ligne avec une espace", addr.Box.Text == "gentle mates valorant", $"« {addr.Box.Text} »");
            addrEdited = false; UpdateUi();

            // 4. Clic droit sur les boutons : plus de clic gauche déguisé
            await Nav("https://example.com/");
            await Nav("https://www.iana.org/help/example-domains");
            var src0 = Core.Source;
            InvokeOnClick(backBtn, new MouseEventArgs(MouseButtons.Right, 1, 5, 5, 0));
            await Task.Delay(1500);
            C("clic droit sur « Page précédente » ne recule plus d'une page", Core.Source == src0, Core.Source);
            InvokeOnClick(addr.Star, new MouseEventArgs(MouseButtons.Right, 1, 5, 5, 0));
            C("clic droit sur l'étoile n'ajoute pas la page aux favoris", !favorites.Exists(f => f.Url == Core.Source), favorites.Count + " favoris");

            // 5. Historique de navigation au clic droit
            var bm = await NavHistoryMenu(-1);
            var bItems = bm?.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList() ?? new();
            C("clic droit sur « Page précédente » : liste des pages précédentes, la plus proche en haut",
                bItems.Count >= 4 && bItems[0].Contains("Example Domain") && bItems[^1] == "Tout l'historique…", string.Join(" | ", bItems));
            var target = bm?.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.ToolTipText?.Contains("example.org") == true);
            target?.PerformClick();
            await Task.Delay(400); await Until(() => Cur?.Loading == false, 10000); await Task.Delay(300);
            C("choisir une page de la liste y retourne directement", Core.Source.Contains("example.org"), Core.Source);
            var fm = await NavHistoryMenu(1);
            var fItems = fm?.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList() ?? new();
            C("clic droit sur « Page suivante » : les pages suivantes", fItems.Count >= 3, string.Join(" | ", fItems));
            C("bulle de « Page précédente » : signale le clic droit", tip.GetToolTip(backBtn).Contains("clic droit"), tip.GetToolTip(backBtn));
            bm?.Dispose(); fm?.Dispose();

            // 6. Ctrl+clic et clic du milieu : onglets en arrière-plan, à la suite
            await Nav("https://example.com/");
            var page = Cur;
            int count = tabs.Count, at = sel;
            async Task ClickLink(string button, int mods)
            {
                var rect = await Js(Core, "(() => { const a = document.querySelector('a'); const r = a.getBoundingClientRect(); return [r.left + r.width / 2, r.top + r.height / 2]; })()");
                var xy = rect.Trim('[', ']').Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                string X = xy[0].ToString(System.Globalization.CultureInfo.InvariantCulture), Y = xy[1].ToString(System.Globalization.CultureInfo.InvariantCulture);
                int buttons = button == "middle" ? 4 : 1;
                await Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", $"{{\"type\":\"mousePressed\",\"x\":{X},\"y\":{Y},\"button\":\"{button}\",\"buttons\":{buttons},\"clickCount\":1,\"modifiers\":{mods}}}");
                await Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", $"{{\"type\":\"mouseReleased\",\"x\":{X},\"y\":{Y},\"button\":\"{button}\",\"buttons\":0,\"clickCount\":1,\"modifiers\":{mods}}}");
            }
            await ClickLink("left", 2);
            bool opened1 = await Until(() => tabs.Count == count + 1, 6000);
            var bg1 = opened1 ? tabs[at + 1] : null;
            C("Ctrl+clic sur un lien : nouvel onglet en arrière-plan, la page reste affichée",
                opened1 && Cur == page && bg1 != page && !bg1.View.Visible, $"{tabs.Count} onglets, affiché « {Cur?.Title} »");
            await ClickLink("middle", 0);
            bool opened2 = await Until(() => tabs.Count == count + 2, 6000);
            C("clic du milieu : aussi en arrière-plan, rangé à la suite du précédent (comme Chrome)",
                opened2 && Cur == page && tabs.IndexOf(bg1) == at + 1 && tabs[at + 2] != page && tabs.IndexOf(page) == at, string.Join(" | ", tabs.Select(x => x == page ? "[" + x.Title + "]" : x.Title)));
            await Until(() => bg1?.View.CoreWebView2?.Source?.Contains("iana") == true, 10000);
            C("l'onglet ouvert en arrière-plan charge bien sa page", bg1?.View.CoreWebView2?.Source?.Contains("iana") == true, bg1?.View.CoreWebView2?.Source ?? "");
            await ShotAsync(name + "_1_arriere_plan");
            await ClickLink("left", 0);
            await Task.Delay(400); await Until(() => Cur?.Loading == false, 10000);
            C("clic normal : le lien s'ouvre dans la page, comme avant", tabs.Count == count + 2 && Cur == page && Core.Source.Contains("iana"), Core.Source);
            // barre de favoris : clic du milieu → arrière-plan aussi
            var openEv = typeof(FavBar).GetField("Open", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(favBar) as Action<string, bool>;
            int c3 = tabs.Count;
            openEv?.Invoke("https://example.net/", true);
            await Until(() => tabs.Count == c3 + 1, 5000);
            C("barre de favoris, clic du milieu / Ctrl+clic : en arrière-plan aussi", tabs.Count == c3 + 1 && Cur == page, $"affiché « {Cur?.Title} »");
            foreach (var x in tabs.Where(x => x != page).ToList()) CloseTab(tabs.IndexOf(x));
            await Task.Delay(800);
            SelectTab(tabs.IndexOf(page));

            // 7. Certificat invalide et pages d'erreur
            await Nav("https://expired.badssl.com/");
            await Until(() => addr.BadgeGlyph == Glyphs.Warning, 15000); // le moteur réessaie seul après une coupure : on attend sa page
            C("certificat expiré : plus de cadenas « Connexion sécurisée », mais l'avertissement",
                addr.BadgeGlyph == Glyphs.Warning && tip.GetToolTip(addr).Contains("Certificat"), $"badge {(addr.BadgeGlyph == Glyphs.Lock ? "cadenas" : addr.BadgeGlyph == Glyphs.Warning ? "avertissement" : "autre")}, « {tip.GetToolTip(addr)} »");
            await ShotAsync(name + "_2_certificat");
            var certText = await Js(Core, "document.body ? document.body.innerText.slice(0, 200) : ''");
            Info("page du certificat : " + certText.Replace("\\n", " / "));
            C("page du certificat aux couleurs de LiteBrowser (plus de « Microsoft Edge »)",
                await UntilJs("!!document.getElementById('lb-error')", "true", 5000), certText);
            // « Continuer vers le site » : le moteur retient la décision, l'avertissement doit rester
            for (int attempt = 0; attempt < 3; attempt++) // réseau capricieux : le moteur peut retomber sur une page de coupure
            {
                if (attempt > 0) { await Nav("https://expired.badssl.com/"); await UntilJs("!!document.getElementById('proceed-link')", "true", 8000); }
                await Core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":\"document.getElementById('proceed-link').click()\",\"userGesture\":true}");
                if (await UntilJs("location.protocol", "\"https:\"", 15000)) break;
            }
            await Until(() => Cur?.Loading == false, 15000);
            await Task.Delay(1000);
            var proceeded = await Js(Core, "location.protocol + ' ' + document.title");
            C("« Continuer vers le site » : l'avertissement reste sur le site", addr.BadgeGlyph == Glyphs.Warning, $"{proceeded}, « {tip.GetToolTip(addr)} »");
            await Nav("https://example.com/");
            await Task.Delay(3000); // une navigation ancienne qui finirait en retard ne doit plus rien changer
            C("site normal : le cadenas revient (et y reste)", addr.BadgeGlyph == Glyphs.Lock, tip.GetToolTip(addr));
            await Nav("https://expired.badssl.com/");
            await Until(() => addr.BadgeGlyph == Glyphs.Warning, 8000);
            C("retour sur le site déjà accepté : toujours l'avertissement", addr.BadgeGlyph == Glyphs.Warning, tip.GetToolTip(addr));
            // fausse alerte signalée par le relecteur : une image d'un autre site au certificat invalide
            await Nav("https://example.com/");
            await Js(Core, "(() => { const i = document.createElement('img'); i.src = 'https://wrong.host.badssl.com/icons/icon-red.png?' + Date.now(); document.body.appendChild(i); })()");
            await Task.Delay(4000);
            bool imgSeen; lock (certHosts) imgSeen = certHosts.Contains("wrong.host.badssl.com");
            C("image d'un autre site au certificat invalide : la page saine garde son cadenas",
                imgSeen && addr.BadgeGlyph == Glyphs.Lock, $"erreur de l'image vue par le moteur : {(imgSeen ? "oui" : "non")}, « {tip.GetToolTip(addr)} »");
            await Nav("https://ce-site-n-existe-pas-du-tout.invalid/");
            await Until(() => addr.BadgeGlyph == Glyphs.Globe, 6000);
            C("site introuvable : pas de cadenas « Connexion sécurisée » sur la page d'erreur", addr.BadgeGlyph == Glyphs.Globe, tip.GetToolTip(addr));
            await ShotAsync(name + "_3_introuvable");
            var dnsText = await Js(Core, "document.body ? document.body.innerText.slice(0, 200) : ''");
            Info("page « site introuvable » : " + dnsText.Replace("\\n", " / "));
            C("page « site introuvable » aux couleurs de LiteBrowser, en français, sans « Microsoft Edge »",
                dnsText.Contains("Site introuvable") && !dnsText.Contains("Edge") && await Js(Core, "!!document.getElementById('reload-button')") == "true", dnsText);

            // 8. Adresse lisible
            await Nav("https://fr.wikipedia.org/wiki/%C3%89cole");
            UpdateUi();
            C("adresse affichée avec ses accents (fr.wikipedia.org/wiki/École)", addr.Box.Text == "fr.wikipedia.org/wiki/École", addr.Box.Text);
            await ShotAsync(name + "_4_accents");
            var tricky = PrettyAddress("https://exemple.fr/a%20b/%2F%3F/%E2%80%AE/%C3%A9t%C3%A9?q=%26");
            C("espaces, / ? & et caractères trompeurs restent codés", tricky == "exemple.fr/a%20b/%2F%3F/%E2%80%AE/été?q=%26", tricky);

            // 9. Favoris : bulle de l'étoile, pages intégrées
            await Nav("https://example.com/");
            UpdateUi();
            var tipAdd = tip.GetToolTip(addr.Star);
            ToggleFavorite();
            var tipDel = tip.GetToolTip(addr.Star);
            ToggleFavorite();
            C("la bulle de l'étoile dit ce que fera le clic", tipAdd == "Ajouter aux favoris (Ctrl+D)" && tipDel == "Retirer des favoris (Ctrl+D)", $"« {tipAdd} » puis « {tipDel} »");
            await Nav(SettingsUrl);
            int nf = favorites.Count;
            ToggleFavorite();
            C("Ctrl+D sur une page de LiteBrowser (Réglages) ne crée plus de favori invisible", favorites.Count == nf && !addr.Star.Visible, favorites.Count + " favoris");

            // 10. Plein écran
            await Nav("https://example.com/");
            var fsLog = new List<bool>();
            fullscreenTest = on => fsLog.Add(on);
            // demande de plein écran de la page (comme un clic sur le bouton d'une vidéo) ; redemandée si le moteur ne l'a pas prise
            async Task<string> AskFullscreen()
            {
                string res = "";
                for (int i = 0; i < 3 && !pageFullscreen; i++)
                {
                    res = await Core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":\"document.documentElement.requestFullscreen().then(() => 'ok', e => e.message)\",\"userGesture\":true,\"awaitPromise\":true}");
                    await Until(() => pageFullscreen, 4000);
                }
                return res;
            }
            fullscreen = false; pageFullscreen = false;
            var fsAsk = await AskFullscreen();
            C("vidéo / page en plein écran : bulle « Appuie sur Échap pour quitter le plein écran »",
                pageFullscreen && fullscreen && fsHint is { Visible: true } && fsHint.Message.Contains("Échap"), fsHint?.Message ?? "pas de bulle (demande : " + fsAsk + ")");
            if (fsHint is { Visible: true })
            {
                using var hb = new Bitmap(fsHint.Width, fsHint.Height);
                fsHint.DrawToBitmap(hb, new Rectangle(Point.Empty, fsHint.Size));
                hb.Save(Path.Combine(ShotsDir, name + "_5_bulle_plein_ecran.png"));
            }
            ToggleF11();
            await Until(() => !pageFullscreen, 4000);
            C("F11 pendant la vidéo en plein écran : on en sort", !pageFullscreen && !fullscreen, string.Join(",", fsLog));
            ToggleF11();
            C("F11 : plein écran + bulle « Appuie sur F11 pour quitter »", fullscreen && fsHint is { Visible: true } && fsHint.Message.Contains("F11"), fsHint?.Message ?? "");
            fsAsk = await AskFullscreen();
            await Js(Core, "document.exitFullscreen()");
            await Until(() => !pageFullscreen, 4000);
            C("sortir de la vidéo en plein écran garde le plein écran F11 d'avant", !pageFullscreen && fullscreen, string.Join(",", fsLog));
            ToggleF11();
            C("F11 de nouveau : fenêtre normale", !fullscreen, string.Join(",", fsLog));
            fullscreenTest = _ => { };

            // 11. Téléchargements
            try { Core.Profile.DefaultDownloadFolderPath = dl; } catch { }
            downloadAsk = true;
            askSavePathTest = p => Path.Combine(dl, "choisi-" + Path.GetFileName(p));
            CoreWebView2DownloadOperation op = null;
            void Got(object s, CoreWebView2DownloadStartingEventArgs e) => op = e.DownloadOperation;
            Core.DownloadStarting += Got;
            await Core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":\"(() => { const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob(['LiteBrowser'], {type: 'text/plain'})); a.download = 'lb-test.txt'; document.body.appendChild(a); a.click(); })()\",\"userGesture\":true}");
            await Until(() => op != null && op.State != CoreWebView2DownloadState.InProgress, 10000);
            Core.DownloadStarting -= Got;
            bool panel = false; try { panel = Core.IsDefaultDownloadDialogOpen; } catch { }
            C("« Demander où enregistrer » : le fichier va où tu l'as choisi", op?.State == CoreWebView2DownloadState.Completed && File.Exists(Path.Combine(dl, "choisi-lb-test.txt")), op?.ResultFilePath ?? "rien");
            C("… et le panneau des téléchargements s'affiche (avant : masqué, aucun retour)", panel);
            await Until(() => notifyTest.Any(n => n.StartsWith("Téléchargement terminé")), 3000);
            C("fin du téléchargement signalée", notifyTest.Any(n => n.StartsWith("Téléchargement terminé · choisi-lb-test.txt")), string.Join(" | ", notifyTest));
            try { Core.CloseDefaultDownloadDialog(); } catch { }
            downloadAsk = false; askSavePathTest = null;
            var english = Enum.GetValues<CoreWebView2DownloadInterruptReason>().Where(v => v != CoreWebView2DownloadInterruptReason.None)
                .Select(v => (v, DownloadReason(v))).Where(x => x.Item2.Contains(x.v.ToString()) || x.Item2.StartsWith("raison")).Select(x => x.v.ToString()).ToList();
            C("téléchargement interrompu : toutes les raisons en français", english.Count == 0, english.Count == 0 ? DownloadReason(CoreWebView2DownloadInterruptReason.FileTooLarge) : string.Join(", ", english));

            // 12. Fenêtre rouverte depuis la barre des tâches / un lien d'une autre appli
            // Vrai chemin d'un lien venu d'une autre appli (OpenFromOutside). La fenêtre n'est pas réduite ici : réduite, elle
            // laisserait une petite barre de titre visible sur l'écran de Ryan. On vérifie que l'état retenu est le bon.
            int beforeOutside = tabs.Count;
            var stateBefore = WindowState;
            OpenFromOutside("https://example.net/");
            await Until(() => tabs.Count == beforeOutside + 1 && Core?.Source?.Contains("example.net") == true, 10000);
            C("lien d'une autre appli : nouvel onglet, la fenêtre garde son état (normale, pas agrandie d'office)",
                tabs.Count == beforeOutside + 1 && Core.Source.Contains("example.net") && WindowState == stateBefore && shownState == stateBefore,
                $"{tabs.Count - beforeOutside} onglet ouvert, {Core.Source}, état {WindowState}, retenu pour le retour {shownState} (avant la correction : toujours Maximized)");
            Info("pas de réduction réelle de la fenêtre dans ce test (elle apparaîtrait à l'écran) : le retour utilise l'état retenu ci-dessus");
        }
        catch (Exception ex) { C("le test s'est déroulé sans erreur", false, ex.GetType().Name + " " + ex.Message + " " + ex.StackTrace?.Split('\n').FirstOrDefault()); }
        finally { notifyTest = null; fullscreenTest = null; }
    }
}

/// Bulle « Appuie sur F11 / Échap pour quitter le plein écran » : ne prend jamais le focus, laisse passer les clics, s'efface seule.
sealed class FullscreenHint : Form
{
    readonly System.Windows.Forms.Timer life = new() { Interval = 2600 };
    System.Windows.Forms.Timer fadeOut;
    readonly Font font = new("Segoe UI", 10.5f);
    Theme theme = Theme.M8;
    public string Message { get; private set; } = "";

    public FullscreenHint()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        life.Tick += (_, _) => { life.Stop(); FadeAway(); };
        VisibleChanged += (_, _) => { if (!Visible) { life.Stop(); StopFade(); } };
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x20; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT (les clics passent)
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE → MA_NOACTIVATE
        base.WndProc(ref m);
    }

    int Sc(int v) => LogicalToDeviceUnits(v);

    public void Popup(Form owner, Theme t, string text, Rectangle area)
    {
        theme = t;
        Message = text;
        StopFade();
        var sz = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPrefix);
        int w = sz.Width + Sc(44), h = sz.Height + Sc(22);
        Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + Sc(28), w, h);
        using (var p = G.Round(new RectangleF(0, 0, w, h), h / 2f)) Region = new Region(p);
        BackColor = t.Bar;
        Invalidate();
        if (!Visible) { Opacity = 0; Show(owner); }
        Fade.Start(this, 140);
        life.Stop(); life.Start();
    }

    void StopFade() { if (fadeOut == null) return; fadeOut.Stop(); fadeOut.Dispose(); fadeOut = null; }

    void FadeAway()
    {
        StopFade();
        fadeOut = new System.Windows.Forms.Timer { Interval = 15 };
        fadeOut.Tick += (_, _) =>
        {
            if (IsDisposed) { StopFade(); return; }
            Opacity = Math.Max(0, Opacity - 0.08);
            if (Opacity <= 0) Hide();
        };
        fadeOut.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(theme.Bar);
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var p = G.Round(r, r.Height / 2))
        using (var pen = new Pen(theme.Accent, Sc(2))) g.DrawPath(pen, p);
        TextRenderer.DrawText(g, Message, font, ClientRectangle, theme.Text, G.Center | TextFormatFlags.NoPrefix);
    }
}
