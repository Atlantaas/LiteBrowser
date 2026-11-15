// Petits soucis des pages intégrées (accueil, Réglages, multistream) : corrections côté navigateur et test hors écran « pc-integrees ».
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

partial class BrowserForm
{
    // ---------- Dossier Téléchargements de Windows ----------

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    /// Vrai dossier « Téléchargements » de Windows (même s'il a été déplacé sur un autre disque).
    static string WindowsDownloads()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
            {
                try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// Ctrl+H, « Historique », « Gérer les favoris »… alors que les Réglages sont déjà ouverts sur cette section :
    /// naviguer vers la même adresse ne fait rien (pas de « hashchange »), la page restait où elle était, recherche comprise.
    void SettingsGo(CoreWebView2 core, string url)
    {
        if (core == null) return;
        if (url.Contains('#') && string.Equals(core.Source, url, StringComparison.OrdinalIgnoreCase))
            _ = core.ExecuteScriptAsync("typeof goHash === 'function' && goHash()");
        else core.Navigate(url);
    }

    // ---------- Test hors écran ----------

    async Task IntegreesTest(string name)
    {
        var r = new List<string>();
        void Check(string what, bool ok, string d = "") { r.Add((ok ? "OK    " : "ÉCHEC ") + what + (string.IsNullOrEmpty(d) ? "" : " · " + d)); Save(); }
        void Save() => File.WriteAllLines(Path.Combine(ShotsDir, name + ".txt"), r);
        static string Q(string s) => JsonSerializer.Serialize(s);
        try
        {
            // ===================== Réglages =====================
            OpenSettings();
            CoreWebView2 c = null;
            for (int i = 0; i < 100; i++) { await Task.Delay(100); c = Core; if (c != null && IsSettings(c.Source) && await PsJs(c, "typeof S === 'object' && S !== null") == "true") break; }
            await Task.Delay(500);
            async Task Find(string q) { await PsJs(c, $"(() => {{ const i = document.getElementById('find'); i.value = {Q(q)}; i.dispatchEvent(new Event('input', {{ bubbles: true }})); }})()"); await Task.Delay(150); }
            async Task<bool> Shown(string id) => await PsJs(c, $"!document.getElementById('{id}').hidden") == "true";

            await Find("priere");
            Check("Réglages : « priere » (sans accent) trouve la section Prières", await Shown("prieres"));
            await Find("telechargement");
            Check("Réglages : « telechargement » trouve Téléchargements", await Shown("telechargements"));
            await Find("page d'accueil");
            bool a1 = await Shown("general");
            await Find("page d’accueil");
            Check("Réglages : apostrophe droite ou typographique, même résultat", a1 && await Shown("general"));

            await Find("prieres");
            var navHidden = await PsJs(c, "(() => { const a = document.querySelector('nav a[href=\"#historique\"]'); return a.offsetParent === null; })()");
            var navShown = await PsJs(c, "document.querySelector('nav a[href=\"#prieres\"]').offsetParent !== null");
            Check("Réglages : la colonne ne garde que les sections trouvées (plus de lien qui ne mène à rien)", navHidden == "true" && navShown == "true", $"Historique caché {navHidden}, Prières affiché {navShown}");

            await PsJs(c, "document.getElementById('find').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
            await Task.Delay(200);
            var esc = await PsJs(c, "document.getElementById('find').value === '' && [...document.querySelectorAll('main section')].every(s => !s.hidden)");
            Check("Réglages : Échap vide la recherche et réaffiche tout", esc == "true");

            // Ctrl+H alors que les Réglages sont déjà ouverts sur l'historique (recherche en cours qui le masque, page remontée)
            OpenSettings("historique");
            await Task.Delay(1500);
            await Find("matchs");
            await PsJs(c, "window.scrollTo({ top: 0, behavior: 'instant' }); document.activeElement && document.activeElement.blur()");
            await Task.Delay(200);
            OpenSettings("historique");
            await Task.Delay(1500);
            c = Core;
            var h = await PsJs(c, "JSON.stringify({ shown: !document.getElementById('historique').hidden, top: Math.round(document.getElementById('historique').getBoundingClientRect().top), focus: document.activeElement && document.activeElement.id, bas: Math.ceil(scrollY + innerHeight) >= document.documentElement.scrollHeight - 2 })");
            Check("Réglages : Ctrl+H sur la page déjà ouverte montre l'historique et sa recherche", h.Contains("\\\"shown\\\":true") && h.Contains("\\\"focus\\\":\\\"hq\\\"") && (TopOk(h) || h.Contains("\\\"bas\\\":true")), h);
            await Find("");

            // Effacer tout l'historique : la liste se vide sous les yeux
            for (int i = 1; i <= 3; i++) history.Add(($"Page test M8 {i}", $"https://example.com/m8-historique-{i}"));
            SaveList(histPath, history);
            RefreshSettingsPages();
            await PsJs(c, "post({ type: 'history', q: '' })");
            await Task.Delay(800);
            var listed = await PsJs(c, "document.getElementById('hist').innerText.includes('m8-historique')");
            await PsJs(c, "document.getElementById('hClear').click()");
            await Task.Delay(1000);
            var after = await PsJs(c, "document.getElementById('hist').innerText.replace(/\\s+/g, ' ').trim()");
            Check("Réglages : « Tout effacer » vide aussi la liste affichée", listed == "true" && history.Count == 0 && !after.Contains("m8-historique"), "après : " + after);

            // Renommer un favori puis Échap : rien ne doit changer
            favorites.Add(("Favori test M8", "https://example.com/fav-m8"));
            RefreshSettingsPages();
            await Task.Delay(700);
            await PsJs(c, "(() => { const row = [...document.querySelectorAll('#favs .it')].find(x => x.dataset.url === 'https://example.com/fav-m8'); row.querySelector('[data-fav=rename]').click();" +
                       " const inp = row.querySelector('.tx input'); inp.value = 'Renommé par erreur'; inp.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); })()");
            await Task.Delay(800);
            var favTitle = favorites.FirstOrDefault(f => f.Url == "https://example.com/fav-m8").Title;
            Check("Réglages : Échap pendant le renommage d'un favori annule vraiment", favTitle == "Favori test M8", "titre : " + favTitle);
            await PsJs(c, "(() => { const row = [...document.querySelectorAll('#favs .it')].find(x => x.dataset.url === 'https://example.com/fav-m8'); row.querySelector('[data-fav=rename]').click();" +
                       " const inp = row.querySelector('.tx input'); inp.value = 'Favori renommé'; inp.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()");
            await Task.Delay(800);
            favTitle = favorites.FirstOrDefault(f => f.Url == "https://example.com/fav-m8").Title;
            Check("Réglages : Entrée renomme le favori", favTitle == "Favori renommé", "titre : " + favTitle);
            favorites.RemoveAll(f => f.Url == "https://example.com/fav-m8");
            RefreshSettingsPages();

            // Dossier des téléchargements : « Par défaut » s'applique tout de suite
            var tmp = Path.Combine(dataDir, "dl-test");
            Directory.CreateDirectory(tmp);
            downloadDir = tmp;
            foreach (var p in Profiles()) try { p.DefaultDownloadFolderPath = tmp; } catch { }
            RefreshSettingsPages();
            await Task.Delay(700);
            await PsJs(c, "document.getElementById('dlReset').click()");
            await Task.Delay(900);
            string now = null; try { now = MainCore()?.Profile.DefaultDownloadFolderPath; } catch { }
            var shownDir = await PsJs(c, "document.getElementById('dlDir').textContent");
            Check("Réglages : « Par défaut » remet le dossier Téléchargements tout de suite (sans redémarrer)",
                downloadDir == null && string.Equals(now?.TrimEnd('\\'), WindowsDownloads().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && !shownDir.Contains("dl-test"), $"moteur : {now} · affiché : {shownDir}");

            // Rechercher des mises à jour : un retour visible tout de suite
            await PsJs(c, "document.getElementById('upd').click()");
            await Task.Delay(250);
            var upd = await PsJs(c, "JSON.stringify({ toast: document.getElementById('toast').classList.contains('show') ? document.getElementById('toast').textContent : '', btn: document.getElementById('upd').textContent })");
            Check("Réglages : « Rechercher des mises à jour » répond quelque chose", !upd.Contains("\\\"toast\\\":\\\"\\\"") || upd.Contains("Recherche en cours"), upd);
            await PsJs(c, "document.getElementById('toast').classList.remove('show')");

            var apo = await PsJs(c, PsApostrophes);
            Check("Réglages : apostrophes typographiques partout (’ et non ')", apo == "\"\"", apo);

            // tailles de fenêtre et thèmes
            await Shots(c, "reglages");

            // ===================== Accueil =====================
            await NewTab();
            await Task.Delay(2500);
            var hc = Core;
            for (int i = 0; i < 50 && await PsJs(hc, "!!state") != "true"; i++) await Task.Delay(100);
            var foc = await PsJs(hc, "(() => { const b = document.querySelector('.ch .ca button'); if (!b) return 'pas de chaîne'; b.focus(); tick(); const a = document.activeElement; return a && a.closest && a.closest('.ch') ? 'gardé' : 'perdu (' + (a && a.tagName) + ')'; })()");
            Check("Accueil : la mise à jour de la page ne fait plus perdre le focus clavier", foc.Contains("gardé"), foc);
            var pr = await PsJs(hc, "(() => { const n = document.querySelector('#prayers .next'); return n ? getComputedStyle(n.querySelector('.n')).display + ' ' + getComputedStyle(n).borderRadius : 'absente'; })()");
            Check("Accueil : la carte « Prochaine prière » garde sa mise en page", pr == "\"flex 14px\"" || pr.Contains("absente"), pr);
            var kb = await PsJs(hc, "(() => { const ch = document.querySelector('.ch'); if (!ch) return 'pas de chaîne'; ch.focus(); return JSON.stringify({ tab: ch.tabIndex, focus: document.activeElement === ch }); })()");
            await Task.Delay(300);
            var op = await PsJs(hc, "getComputedStyle(document.querySelector('.ch .ca')).opacity");
            var enter = await PsJs(hc, "(() => { const ch = document.querySelector('.ch'); let hit = false; ch.addEventListener('click', e => { hit = true; e.stopPropagation(); }, { once: true }); ch.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); return hit; })()");
            Check("Accueil : les chaînes se parcourent au clavier (Tab, Entrée) avec leurs boutons visibles", kb.Contains("\\\"tab\\\":0") && kb.Contains("\\\"focus\\\":true") && op == "\"1\"" && enter == "true", $"{kb} · boutons {op} · Entrée {enter}");

            await PsJs(hc, "document.getElementById('mset').click()");
            var o1 = await PsJs(hc, "document.getElementById('settings').classList.contains('open')");
            await PsJs(hc, "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
            var o2 = await PsJs(hc, "document.getElementById('settings').classList.contains('open')");
            await PsJs(hc, "document.getElementById('mset').click()");
            await PsJs(hc, "document.querySelector('.hint').dispatchEvent(new MouseEvent('mousedown', { bubbles: true })); document.querySelector('.hint').click()");
            var o3 = await PsJs(hc, "document.getElementById('settings').classList.contains('open')");
            Check("Accueil : le panneau ⚙ des matchs se ferme avec Échap ou un clic à côté", o1 == "true" && o2 == "false" && o3 == "false", $"ouvert {o1}, après Échap {o2}, après clic à côté {o3}");

            await PsJs(hc, "render({ type: 'error', text: 'Chaîne non reconnue' }); const i = document.getElementById('lvin'); i.value = 'g'; i.dispatchEvent(new Event('input', { bubbles: true }))");
            var err = await PsJs(hc, "document.getElementById('lverr').textContent");
            await PsJs(hc, "document.getElementById('lvin').value = ''");
            Check("Accueil : le message d'erreur d'ajout de chaîne s'efface dès qu'on retape", err == "\"\"", err);

            for (int i = 0; i < 70 && DateTime.Now.Second != 1; i++) await Task.Delay(1000);
            await Task.Delay(1200);
            var clock = await PsJs(hc, "document.getElementById('clock').textContent");
            var want = DateTime.Now.ToString("HH:mm");
            Check("Accueil : l'horloge change de minute à l'heure pile (plus 15 s de retard)", clock == Q(want), $"affiché {clock}, heure {want}");

            apo = await PsJs(hc, PsApostrophes);
            Check("Accueil : apostrophes typographiques partout", apo == "\"\"", apo);
            await Shots(hc, "accueil");

            // ===================== Multistream =====================
            await NewTab(MultiUrl + "#c=kick:lbtesta,kick:lbtestb,kick:lbtestc&chat=0");
            await Task.Delay(2500);
            var mc = Core;
            var k1 = await PsJs(mc, "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'é', code: 'Digit2', bubbles: true })); S.focus");
            Check("Multistream : touche 2 en AZERTY (é) met le 2e live en grand", k1 == "\"kick:lbtestb\"", k1);
            var k2 = await PsJs(mc, "document.dispatchEvent(new KeyboardEvent('keydown', { key: ']', code: 'Minus', ctrlKey: true, altKey: true, bubbles: true })); S.focus");
            Check("Multistream : ] avec AltGr (AZERTY) passe au live suivant", k2 == "\"kick:lbtestc\"", k2);
            var k3 = await PsJs(mc, "document.dispatchEvent(new KeyboardEvent('keydown', { key: '1', code: 'Numpad1', bubbles: true })); S.focus");
            Check("Multistream : pavé numérique 1 met le 1er live en grand", k3 == "\"kick:lbtesta\"", k3);
            await PsJs(mc, "location.hash = ''; S.ids = []; render()");
            await Task.Delay(300);
            apo = await PsJs(mc, PsApostrophes);
            Check("Multistream : apostrophes typographiques partout", apo == "\"\"", apo);
            await ShotAsync(name + "_multistream_vide");
        }
        catch (Exception ex) { Check("test interrompu", false, ex.ToString()); }
        Save();

        static bool TopOk(string json)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json, "top\\\\\":(-?\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out var t) && t >= -5 && t < 160;
        }

        async Task Shots(CoreWebView2 core, string page)
        {
            var size = Size;
            foreach (var (label, w, hgt) in new[] { ("etroite", 720, 900), ("1080p", 1920, 1080), ("1440p", 2560, 1440) })
            {
                Size = new Size(w, hgt);
                await Task.Delay(900);
                await ShotAsync($"{name}_{page}_{label}");
                var over = await PsJs(core, "document.documentElement.scrollWidth > innerWidth + 1 ? 'déborde : ' + document.documentElement.scrollWidth + ' > ' + innerWidth : ''");
                Check($"{page} {label} : rien ne déborde sur le côté", over == "\"\"", over);
            }
            Size = size;
            await Task.Delay(500);
            foreach (var th in new[] { "light", "dark", "m8" })
            {
                SetTheme(th);
                await Task.Delay(700);
                if (th != "m8") await ShotAsync($"{name}_{page}_theme_{th}");
            }
        }
    }

    /// PsApostrophes droites restées dans le texte visible, les infobulles et les textes d'aide (vide = tout est typographique).
    /// Les contenus venus d'ailleurs (titres de lives, pages de l'historique, noms d'équipes…) sont retirés : seuls les textes de LiteBrowser comptent.
    const string PsApostrophes = "(() => { const b = document.body.cloneNode(true);" +
        " b.querySelectorAll('script,style,#hist,#favs .tx,#sitePerms,#siteZooms,#exts,.ch .ci,.tn,.opp,#tours,.tile .name,#osug,#lvsugg,#chips,.tile .name,#pToday,#prayers .name,#favs').forEach(e => e.remove());" +
        " b.querySelectorAll('.ch,.opp').forEach(e => e.removeAttribute('title'));" +
        " const t = [b.textContent, ...[...b.querySelectorAll('[placeholder],[title]')].map(e => (e.getAttribute('placeholder') || '') + ' ' + (e.getAttribute('title') || ''))].join(' ');" +
        " return (t.match(/.{0,12}[A-Za-zÀ-ÿ]'[A-Za-zÀ-ÿ].{0,12}/g) || []).slice(0, 6).join(' | '); })()";

    static async Task<string> PsJs(CoreWebView2 c, string code)
    {
        try { return await c.ExecuteScriptAsync(code); } catch (Exception ex) { return "erreur " + ex.Message; }
    }
}
