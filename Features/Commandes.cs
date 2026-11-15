// Pause prière (couper le son de tout ce qui joue, puis le remettre exactement comme avant)
// et palette de commandes (F2, ou « > » au début de la barre d'adresse : on tape ce qu'on veut faire).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteCore;

partial class BrowserForm
{
    // ================== Pause prière ==================

    /// Onglets dont la pause prière a coupé le son (null = pas de pause en cours). Seulement le son : rien n'est mis
    /// en pause ni rechargé, donc un lecteur Twitch de Nur Live continue de tourner et ne se fige pas.
    List<BrowserTab> prayerMuted;
    DateTime prayerPauseAt;
    System.Windows.Forms.Timer prayerPauseTimer;

    bool PrayerPaused => prayerMuted != null;

    void StartPrayerPause()
    {
        if (PrayerPaused) { PrayerPauseMessage(); return; }
        prayerMuted = new List<BrowserTab>();
        foreach (var t in tabs.ToList()) PrayerMute(t);
        prayerPauseAt = DateTime.Now;
        // Au bout de 20 min, on propose de remettre le son (jamais tout seul : ça couperait une prière plus longue)
        prayerPauseTimer?.Dispose();
        prayerPauseTimer = new System.Windows.Forms.Timer { Interval = 20 * 60 * 1000 };
        prayerPauseTimer.Tick += (_, _) => { prayerPauseTimer?.Stop(); if (PrayerPaused) PrayerPauseMessage(); };
        prayerPauseTimer.Start();
        PrayerPauseMessage();
        UpdatePrayerChip();
        RefreshHomeTabs();
    }

    /// Coupe le son d'un onglet qui joue (aussi un onglet qui se met à jouer pendant la pause). Déjà muet : on n'y touche pas.
    void PrayerMute(BrowserTab t)
    {
        var c = t?.View?.CoreWebView2;
        if (!PrayerPaused || c == null || prayerMuted.Contains(t) || c.IsMuted || !(t.Audio || c.IsDocumentPlayingAudio)) return;
        try { c.IsMuted = true; t.Muted = true; prayerMuted.Add(t); } catch { }
        strip.Invalidate();
    }

    void ResumePrayerPause()
    {
        if (!PrayerPaused) return;
        var list = prayerMuted;
        prayerMuted = null;
        prayerPauseTimer?.Dispose(); prayerPauseTimer = null;
        int back = 0;
        foreach (var t in list)
        {
            var c = t.View?.CoreWebView2;
            if (c == null || !c.IsMuted) continue; // fermé, libéré, ou son déjà remis à la main
            try { c.IsMuted = false; t.Muted = false; back++; } catch { }
        }
        strip.Invalidate();
        UpdatePrayerChip();
        RefreshHomeTabs();
        ToolMessage("Son remis", back == 0 ? "Aucun onglet n'avait besoin de son." : back == 1 ? "1 onglet a retrouvé son son." : $"{back} onglets ont retrouvé leur son.");
    }

    void PrayerPauseMessage()
    {
        int n = prayerMuted?.Count ?? 0;
        var since = (int)(DateTime.Now - prayerPauseAt).TotalMinutes;
        string text = n == 0 ? "Rien ne jouait. Ce qui se lance maintenant reste muet." : n == 1 ? "1 onglet est muet. Ce qui se lance maintenant le reste aussi." : $"{n} onglets sont muets. Ce qui se lance maintenant le reste aussi.";
        if (since >= 1) text = $"Depuis {since} min. " + text;
        ToolMessage("Pause prière", text, null, "Reprendre le son", ResumePrayerPause);
    }

    // ================== Palette de commandes ==================

    /// F2 : la barre d'adresse s'ouvre avec « > » ; on tape « capt », « épingl », « priere »… et Entrée.
    void OpenPalette()
    {
        if (IsDisposed) return;
        addr.Box.Focus();
        BeginInvoke(new Action(() =>
        {
            addr.Box.Text = ">"; // TextChanged : affiche toutes les actions
            addr.Box.SelectionStart = addr.Box.Text.Length;
            addr.Box.SelectionLength = 0;
        }));
    }

    static bool IsPaletteText(string text) => (text ?? "").TrimStart().StartsWith(">");

    List<OmniRow> PaletteRows(string typed)
    {
        var q = typed.TrimStart()[1..];
        var rows = new List<OmniRow>();
        foreach (var c in Palette.Find(q))
        {
            if (c.Id == "prayerResume" && !PrayerPaused) continue;
            if (c.Id == "prayerPause" && PrayerPaused) continue;
            rows.Add(new OmniRow { Kind = "cmd", Title = c.Title, Sub = c.Keys.Length > 0 ? c.Keys : "Action", Fill = typed, Cmd = c.Id });
        }
        if (rows.Count == 0) rows.Add(new OmniRow { Kind = "cmd", Title = "Aucune action ne correspond", Sub = "Essaie « capture », « onglet », « prière »…", Fill = typed });
        return rows;
    }

    /// Action choisie dans la palette : la barre d'adresse reprend l'adresse de la page, puis l'action est faite.
    void RunPaletteRow(OmniRow row)
    {
        addrEdited = false;
        HideOmni();
        UpdateUi();
        if (row.Cmd == null) { addr.Box.Focus(); return; }
        if (Cur != null && !Cur.IsHome) Cur.View.Focus();
        var id = row.Cmd;
        BeginInvoke(new Action(() => { if (!IsDisposed) RunCommand(id); }));
    }

    void SendShortcut(Keys k)
    {
        var m = Message.Create(Handle, 0x0100, (IntPtr)(int)(k & Keys.KeyCode), IntPtr.Zero);
        ProcessCmdKey(ref m, k);
    }

    void RunCommand(string id)
    {
        var t = Cur;
        switch (id)
        {
            case "prayerPause": StartPrayerPause(); break;
            case "prayerResume": ResumePrayerPause(); break;
            case "prayers": ShowPrayerMenu(); break;
            case "newTab": _ = NewTab(); break;
            case "privateTab": _ = NewTab(priv: true); break;
            case "reopen": ReopenClosed(); break;
            case "closeTab": if (sel >= 0) CloseTab(sel); break;
            case "duplicate": if (t != null) DuplicateTab(t); break;
            case "pin": if (t != null && !t.Private) TogglePin(t); break;
            case "detach": if (sel >= 0) DetachTab(sel); break;
            case "closeDupes": CloseDuplicateTabs(); break;
            case "tabSearch": ShowTabSearch(); break;
            case "mute": ToggleMute(sel); break;
            case "reload": t?.View.CoreWebView2?.Reload(); break;
            case "find": SendShortcut(Keys.Control | Keys.F); break;
            case "print": SendShortcut(Keys.Control | Keys.P); break;
            case "shot": SendShortcut(Keys.Control | Keys.Shift | Keys.X); break;
            case "shotFull": SendShortcut(Keys.Control | Keys.Shift | Keys.S); break;
            case "pip": SendShortcut(Keys.Alt | Keys.P); break;
            case "lens": StartLens(); break;
            case "split": SplitToggle(); break;
            case "fullscreen": ToggleF11(); break;
            case "zoomIn": ZoomStep(t, 1); break;
            case "zoomOut": ZoomStep(t, -1); break;
            case "zoomReset": ZoomStep(t, 0); break;
            case "favorite": ToggleFavorite(); break;
            case "favBar": ToggleFavBar(); break;
            case "history": OpenSettings("historique"); break;
            case "downloads": t?.View.CoreWebView2?.OpenDefaultDownloadDialog(); break;
            case "settings": OpenSettings(); break;
            case "clearData": ClearData(); break;
            case "adblock": SetAdblock(!adblock); break;
            case "multi": OpenMulti(new List<string>(), chat: true); break;
            case "free":
                int n = tabs.ToList().Count(x => DiscardTab(x));
                ToolMessage("Mémoire libérée", n == 0 ? "Aucun onglet inactif à libérer (épinglés, son, saisie en cours et Nur Live sont gardés)." : n == 1 ? "1 onglet libéré : il se recharge quand tu y reviens." : $"{n} onglets libérés : ils se rechargent quand tu y reviens.");
                break;
        }
    }

    // ================== Test hors écran ==================

    async Task CommandesTest(string name)
    {
        var r = new List<string>();
        void C(string what, bool ok, string d = "") => r.Add((ok ? "OK    " : "ÉCHEC ") + what + (d.Length > 0 ? " · " + d : ""));
        try
        {
            // Pause prière : un onglet qui « joue » (son simulé par le moteur muet des tests : on lit IsMuted)
            await NewTab("https://example.org/");
            for (int i = 0; i < 80 && Cur.Loading; i++) await Task.Delay(100);
            var a = Cur; var ca = a.View.CoreWebView2;
            ca.IsMuted = false; a.Audio = true; // comme un onglet qui joue un son
            await NewTab("https://example.com/");
            for (int i = 0; i < 80 && Cur.Loading; i++) await Task.Delay(100);
            var b = Cur; var cb = b.View.CoreWebView2;
            cb.IsMuted = true; b.Audio = true; // déjà muet à la main
            StartPrayerPause(); await Task.Delay(200);
            C("pause prière : l'onglet qui joue devient muet", ca.IsMuted && prayerMuted.Contains(a));
            C("… l'onglet déjà muet n'est pas dans la liste (il le restera après)", !prayerMuted.Contains(b));
            C("… la notification propose « Reprendre le son »", lastToolMessage == "Pause prière");
            await ShotAsync(name + "_pause");
            // la palette ne propose plus « Pause » mais « Reprendre »
            var rows = PaletteRows(">prière");
            C("palette pendant la pause : « Reprendre le son » proposé, pas « Pause »", rows.Any(x => x.Cmd == "prayerResume") && !rows.Any(x => x.Cmd == "prayerPause"), string.Join(", ", rows.Select(x => x.Cmd)));
            ResumePrayerPause(); await Task.Delay(200);
            C("reprendre : le son revient sur l'onglet coupé par la pause", !ca.IsMuted && !PrayerPaused);
            C("… l'onglet muet à la main reste muet", cb.IsMuted);

            // Palette : F2 ouvre la barre avec « > » et toutes les actions
            Activate(); await Task.Delay(200);
            SendShortcut(Keys.F2); await Task.Delay(500);
            C("F2 : la barre d'adresse s'ouvre avec « > »", addr.Box.Text == ">", addr.Box.Text);
            C("… et la liste montre des actions", omni.Visible && omni.Rows.Count > 0 && omni.Rows.All(x => x.Kind == "cmd"), $"{omni.Visible} {omni.Rows.Count}");
            await ShotAsync(name + "_palette");
            addr.Box.Text = ">capt"; await Task.Delay(300);
            C("« capt » : les deux captures, avec leur raccourci", omni.Rows.Count == 2 && omni.Rows[0].Sub == "Ctrl+Maj+X", string.Join(" | ", omni.Rows.Select(x => x.Title + " " + x.Sub)));
            addr.Box.Text = ">barre fav"; await Task.Delay(300);
            bool fav = showFavBar;
            RunPaletteRow(omni.Rows[0]); await Task.Delay(500);
            C("Entrée sur « barre de favoris » : l'action est faite", showFavBar != fav, $"{fav} → {showFavBar}");
            C("… la barre d'adresse reprend l'adresse de la page", !IsPaletteText(addr.Box.Text) && !omni.Visible, addr.Box.Text);
            if (showFavBar != fav) ToggleFavBar();
            addr.Box.Focus(); addr.Box.Text = ">zzzz"; await Task.Delay(300);
            C("rien trouvé : une ligne qui l'explique, sans action", omni.Rows.Count == 1 && omni.Rows[0].Cmd == null, omni.Rows.FirstOrDefault()?.Title ?? "");
            C("aucune suggestion Google pour une commande", omniSuggest.Count == 0 || !omniSuggest.Any(s => s.StartsWith(">")));
            addrEdited = false; HideOmni(); UpdateUi();
        }
        catch (Exception ex) { C("le test a planté", false, ex.Message); }
        System.IO.File.WriteAllLines(System.IO.Path.Combine(ShotsDir, name + ".txt"), r);
    }
}
