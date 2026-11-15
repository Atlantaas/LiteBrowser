// Onglets : recherche dans les onglets ouverts (Ctrl+Maj+A, bouton liste à droite du +), « Fermer les onglets en double »,
// et le scénario de test hors écran « onglets ». La barre qui défile est dans TabStrip (Program.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using LiteCore;

/// Une ligne de la recherche d'onglets.
sealed class TabSearchItem
{
    public BrowserTab Tab;
    public string Title, Url, Place, State, Glyph;
    public Image Icon;
    public bool Current, Faded;
}

/// Liste flottante des onglets ouverts, façon Brave : champ de recherche, flèches + Entrée pour y aller, Suppr pour fermer.
sealed class TabSearchPopup : Form
{
    readonly TextBox box = new() { BorderStyle = BorderStyle.None };
    readonly Font glyphFont, titleFont, subFont, smallFont;
    readonly Theme theme;
    readonly bool quiet;
    List<TabSearchItem> items = new(), rows = new();
    int selected, first, hover = -1;
    bool hoverClose;
    public event Action<BrowserTab> Picked, CloseRequested;
    /// Pendant la fermeture d'un onglet : la fenêtre principale reprend le focus un instant, la liste ne doit pas se fermer pour autant.
    public bool Hold;
    /// Hauteur maximale (place disponible sous la barre d'onglets).
    public int MaxHeight = int.MaxValue;

    public TabSearchPopup(Font glyph, Font field, Font title, Font sub, Font small, Theme theme, bool quiet)
    {
        glyphFont = glyph; titleFont = title; subFont = sub; smallFont = small;
        this.theme = theme; this.quiet = quiet;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = theme.Bar;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        box.Font = field;
        box.BackColor = theme.Field;
        box.ForeColor = theme.Text;
        box.PlaceholderText = "Rechercher dans tes onglets";
        box.TextChanged += (_, _) => Filter(false);
        box.KeyDown += (_, e) => { if (HandleKey(e.KeyCode, box.SelectionStart >= box.TextLength && box.SelectionLength == 0)) { e.Handled = true; e.SuppressKeyPress = true; } };
        box.GotFocus += (_, _) => Invalidate();
        box.LostFocus += (_, _) => Invalidate();
        Controls.Add(box);
        Width = Sc(440);
        Deactivate += (_, _) => { if (!quiet && !Hold && !IsDisposed) Close(); };
    }

    protected override bool ShowWithoutActivation => quiet;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;                    // WS_EX_TOOLWINDOW : pas dans Alt+Tab
            if (quiet) cp.ExStyle |= 0x08000000;   // tests hors écran : ne prend jamais le focus
            cp.ClassStyle |= 0x20000;              // ombre
            return cp;
        }
    }

    int Sc(int v) => LogicalToDeviceUnits(v);
    int Pad => Sc(10);
    int FieldH => Sc(38);
    int HeaderH => Sc(26);
    int RowH => Sc(46);
    int FooterH => Sc(30);
    int ListTop => Pad + FieldH + Sc(6) + HeaderH;
    int VisibleRows => Math.Max(1, Math.Min(Math.Max(1, rows.Count), Math.Min(9, (MaxHeight - ListTop - FooterH - Sc(6)) / RowH)));

    public string Query { get => box.Text; set => box.Text = value ?? ""; }
    public IReadOnlyList<TabSearchItem> Rows => rows;
    public int SelectedIndex => selected;
    public TabSearchItem SelectedItem => selected >= 0 && selected < rows.Count ? rows[selected] : null;
    public void FocusBox() { box.Focus(); box.SelectionStart = box.TextLength; }

    public void SetItems(List<TabSearchItem> list, bool keep)
    {
        items = list;
        Filter(keep);
    }

    void Filter(bool keep)
    {
        var was = SelectedItem?.Tab;
        int wasIndex = selected;
        var q = box.Text;
        rows = items.Where(x => TabTools.Matches(q, x.Title, x.Url)).ToList();
        if (keep)
        {
            int k = rows.FindIndex(x => x.Tab == was);
            selected = k >= 0 ? k : Math.Min(wasIndex, rows.Count - 1); // onglet fermé : on passe au suivant
        }
        else selected = q.Trim().Length == 0 ? Math.Max(0, rows.FindIndex(x => x.Current)) : 0;
        selected = Math.Clamp(selected, 0, Math.Max(0, rows.Count - 1));
        if (!keep) first = 0;
        Relayout();
        Reveal();
        Invalidate();
    }

    void Relayout()
    {
        int h = ListTop + VisibleRows * RowH + FooterH + Sc(4);
        Height = h;
        using (var path = G.Round(new RectangleF(0, 0, Width, h), Sc(12))) Region = new Region(path);
        int bh = box.PreferredHeight;
        int fx = Pad + Sc(36);
        box.SetBounds(fx, Pad + (FieldH - bh) / 2, Width - Pad - Sc(14) - fx, bh);
    }

    void Reveal()
    {
        int n = VisibleRows;
        first = Math.Clamp(first, 0, Math.Max(0, rows.Count - n));
        if (selected < first) first = selected;
        else if (selected >= first + n) first = selected - n + 1;
    }

    /// Touches de la liste (aussi utilisée par les tests). atEnd : curseur au bout du texte (Suppr ferme alors l'onglet choisi).
    public bool HandleKey(Keys k, bool atEnd = true)
    {
        switch (k)
        {
            case Keys.Down: MoveSel(1); return true;
            case Keys.Up: MoveSel(-1); return true;
            case Keys.PageDown: MoveSel(VisibleRows); return true;
            case Keys.PageUp: MoveSel(-VisibleRows); return true;
            case Keys.Enter: if (SelectedItem?.Tab is { } t) BeginInvoke(new Action(() => Picked?.Invoke(t))); return true;
            case Keys.Escape: BeginInvoke(new Action(Close)); return true;
            case Keys.Delete when atEnd: if (SelectedItem != null) CloseRequested?.Invoke(SelectedItem.Tab); return true;
        }
        return false;
    }

    void MoveSel(int d)
    {
        if (rows.Count == 0) return;
        selected = Math.Abs(d) == 1 ? (selected + d + rows.Count) % rows.Count : Math.Clamp(selected + d, 0, rows.Count - 1);
        Reveal();
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.A)) { Close(); return true; } // même raccourci : referme la liste
        return base.ProcessCmdKey(ref msg, keyData);
    }

    Rectangle RowRect(int visibleIndex) => new(Pad, ListTop + visibleIndex * RowH, Width - Pad * 2 - Sc(4), RowH);
    Rectangle CloseRect(Rectangle r) { int s = Sc(26); return new Rectangle(r.Right - s - Sc(8), r.Top + (r.Height - s) / 2, s, s); }
    int RowAt(Point p)
    {
        if (p.Y < ListTop || p.X < Pad || p.X > Width - Pad) return -1;
        int i = (p.Y - ListTop) / RowH;
        return i < VisibleRows && first + i < rows.Count ? first + i : -1;
    }

    const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(theme.Bar);
        using (var path = G.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Sc(12)))
        using (var pen = new Pen(theme.FieldBorder)) g.DrawPath(pen, path);

        // champ de recherche
        var fr = new RectangleF(Pad, Pad, Width - Pad * 2, FieldH);
        bool f = box.Focused;
        using (var path = G.Round(fr, FieldH / 2f))
        {
            using (var b = new SolidBrush(theme.Field)) g.FillPath(b, path);
            using var pen = new Pen(f ? G.Mix(theme.Accent, theme.Bar, .35f) : theme.FieldBorder, f ? Sc(2) : 1);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Glyphs.Search, glyphFont, new Rectangle(Pad + Sc(10), Pad, Sc(20), FieldH), theme.Sub, G.Center);

        // en-tête : nombre d'onglets, combien dorment
        int hy = Pad + FieldH + Sc(6);
        int asleep = items.Count(x => x.Faded);
        string left = box.Text.Trim().Length == 0 ? "Onglets ouverts" : rows.Count == 0 ? "Aucun résultat" : $"{rows.Count} onglet{(rows.Count > 1 ? "s" : "")} sur {items.Count}";
        string right = $"{items.Count} ouvert{(items.Count > 1 ? "s" : "")}" + (asleep > 0 ? $" · {asleep} au repos" : "");
        TextRenderer.DrawText(g, left, smallFont, new Rectangle(Pad + Sc(6), hy, Width / 2, HeaderH), theme.Sub, Line);
        TextRenderer.DrawText(g, right, smallFont, new Rectangle(Width / 2, hy, Width / 2 - Pad - Sc(6), HeaderH), theme.Sub, Line | TextFormatFlags.Right);

        if (rows.Count == 0)
        {
            TextRenderer.DrawText(g, $"Aucun onglet ne correspond à « {box.Text.Trim()} »", subFont, RowRect(0), theme.Sub, Line | TextFormatFlags.HorizontalCenter);
        }
        int isz = Sc(16);
        for (int v = 0; v < VisibleRows && first + v < rows.Count; v++)
        {
            int i = first + v;
            var row = rows[i];
            var r = RowRect(v);
            bool on = i == selected, hov = i == hover;
            if (on || hov)
            {
                using var b = new SolidBrush(on ? G.Mix(theme.Accent, theme.Bar, .84f) : theme.Hover);
                using var path = G.Round(r, Sc(8));
                g.FillPath(b, path);
                if (on)
                {
                    using var ab = new SolidBrush(theme.Accent);
                    using var bar = G.Round(new RectangleF(r.X + Sc(2), r.Y + RowH / 4f, Sc(3), RowH / 2f), Sc(1));
                    g.FillPath(ab, bar);
                }
            }
            var ir = new Rectangle(r.X + Sc(14), r.Y + (r.Height - isz) / 2, isz, isz);
            if (row.Icon != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(row.Icon, ir); }
            else TextRenderer.DrawText(g, row.Glyph ?? Glyphs.Globe, glyphFont, ir, theme.Sub, G.Center);
            if (row.Current)
            {
                // onglet affiché : petit point rose sous l'icône
                using var db = new SolidBrush(theme.Accent);
                g.FillEllipse(db, ir.Left + isz / 2 - Sc(2), ir.Bottom + Sc(3), Sc(4), Sc(4));
            }
            int x = ir.Right + Sc(12);
            int tr = (on || hov) ? CloseRect(r).Left - Sc(6) : r.Right - Sc(12);
            int lh = titleFont.Height, sh = subFont.Height, top = r.Y + (RowH - lh - sh - Sc(2)) / 2;
            TextRenderer.DrawText(g, row.Title, titleFont, new Rectangle(x, top, tr - x, lh), row.Faded && !on ? G.Mix(theme.Text, theme.Sub, .55f) : theme.Text, Line);
            var place = row.Place ?? "";
            int pw = Math.Min(TextRenderer.MeasureText(g, place, subFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width, (tr - x) * 3 / 5);
            TextRenderer.DrawText(g, place, subFont, new Rectangle(x, top + lh + Sc(2), pw, sh), theme.Sub, Line);
            if (!string.IsNullOrEmpty(row.State) && tr - (x + pw) > Sc(40))
                TextRenderer.DrawText(g, "  ·  " + row.State, subFont, new Rectangle(x + pw, top + lh + Sc(2), tr - x - pw, sh),
                    row.Current ? theme.Accent : G.Mix(theme.Sub, theme.Text, .3f), Line);
            if (on || hov)
            {
                var cr = CloseRect(r);
                if (hov && hoverClose) { using var hb = new SolidBrush(G.Mix(theme.Hover, theme.Text, .08f)); g.FillEllipse(hb, cr); }
                TextRenderer.DrawText(g, Glyphs.Close, glyphFont, cr, theme.Text, G.Center);
            }
        }

        // barre de défilement discrète quand tout ne tient pas
        if (rows.Count > VisibleRows)
        {
            int track = VisibleRows * RowH, th = Math.Max(Sc(24), track * VisibleRows / rows.Count);
            int ty = ListTop + (track - th) * first / Math.Max(1, rows.Count - VisibleRows);
            using var sb = new SolidBrush(G.Mix(theme.FieldBorder, theme.Sub, .3f));
            using var path = G.Round(new RectangleF(Width - Pad - Sc(1), ty, Sc(3), th), Sc(1));
            g.FillPath(sb, path);
        }

        // pied : les touches
        int fy = Height - FooterH - Sc(4);
        using (var pen = new Pen(theme.FieldBorder)) g.DrawLine(pen, Pad, fy, Width - Pad, fy);
        TextRenderer.DrawText(g, "↑ ↓ choisir  ·  Entrée : y aller  ·  Suppr : fermer l'onglet  ·  Échap", smallFont,
            new Rectangle(Pad + Sc(6), fy, Width - Pad * 2 - Sc(12), FooterH), theme.Sub, Line);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = RowAt(e.Location);
        bool hc = i >= 0 && CloseRect(RowRect(i - first)).Contains(e.Location);
        if (i != hover || hc != hoverClose) { hover = i; hoverClose = hc; Invalidate(); }
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; hoverClose = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = RowAt(e.Location);
        if (i < 0) { box.Focus(); return; }
        var t = rows[i].Tab;
        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && CloseRect(RowRect(i - first)).Contains(e.Location)))
        {
            selected = i;
            CloseRequested?.Invoke(t);
        }
        else if (e.Button == MouseButtons.Left) Picked?.Invoke(t);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        first = Math.Clamp(first - Math.Sign(e.Delta) * 3, 0, Math.Max(0, rows.Count - VisibleRows));
        Invalidate();
    }
}

partial class BrowserForm
{
    TabSearchPopup tabSearch;
    DateTime tabSearchClosedAt;
    Font tabSearchField, tabSearchTitle, tabSearchSub, tabSearchSmall;

    /// État d'un onglet, en clair (affiché, privé, libéré, en veille, son…).
    string TabStateText(BrowserTab t)
    {
        var parts = new List<string>();
        if (t == Cur) parts.Add("Onglet affiché");
        if (t.Pinned) parts.Add("Épinglé");
        if (t.Private) parts.Add("Privé");
        var core = t.View.CoreWebView2;
        if (t.Lazy) parts.Add(t.Discarded ? "Mémoire libérée" : "Pas encore chargé");
        else if (core?.IsSuspended == true) parts.Add("En veille");
        if (t.Muted) parts.Add("Son coupé");
        else if (t.Audio) parts.Add("Joue du son");
        return string.Join(" · ", parts);
    }

    string TabUrl(BrowserTab t) => t.View.CoreWebView2?.Source ?? t.PendingUrl ?? t.LastUrl ?? "";

    List<TabSearchItem> TabSearchItems() => tabs.Select(t =>
    {
        var url = TabUrl(t);
        return new TabSearchItem
        {
            Tab = t,
            Title = string.IsNullOrWhiteSpace(t.Title) ? "Nouvel onglet" : t.Title,
            Url = IsLocalPage(url) ? "" : url,
            Place = t.IsHome || IsHome(url) || url == "" ? "Page d'accueil" : IsLocalPage(url) ? "Page de LiteBrowser" : Omni.Host(url),
            State = TabStateText(t),
            Icon = t.Private || t.IsHome || t.LocalGlyph != null ? null : t.Icon,
            Glyph = t.Private ? Glyphs.Private : t.IsHome ? Glyphs.Home : t.LocalGlyph ?? Glyphs.Globe,
            Current = t == Cur,
            Faded = t.Lazy || t.View.CoreWebView2?.IsSuspended == true,
        };
    }).ToList();

    /// Recherche dans les onglets (Ctrl+Maj+A, bouton liste de la barre d'onglets). Le même raccourci la referme.
    void ShowTabSearch()
    {
        if (tabSearch is { IsDisposed: false }) { tabSearch.Close(); return; }
        if ((DateTime.Now - tabSearchClosedAt).TotalMilliseconds < 250) return; // clic sur le bouton qui vient de la refermer
        if (tabs.Count == 0) return;
        HideOmni(); TabHover(-1);
        tabSearchField ??= new Font("Segoe UI", 10.5f);
        tabSearchTitle ??= new Font("Segoe UI", 9.75f);
        tabSearchSub ??= new Font("Segoe UI", 8.5f);
        tabSearchSmall ??= new Font("Segoe UI", 8.25f);
        var p = new TabSearchPopup(iconSmall, tabSearchField, tabSearchTitle, tabSearchSub, tabSearchSmall, theme, Offscreen);
        p.Picked += t =>
        {
            p.Close();
            int i = tabs.IndexOf(t);
            if (i >= 0) SelectTab(i);
        };
        p.CloseRequested += t =>
        {
            int i = tabs.IndexOf(t);
            if (i < 0) return;
            if (tabs.Count == 1) { p.Close(); CloseTab(i); return; }
            p.Hold = true;
            CloseTab(i);
            p.Hold = false;
            if (p.IsDisposed) return;
            p.SetItems(TabSearchItems(), keep: true);
            PlaceTabSearch(p);
            if (!Offscreen) { p.Activate(); p.FocusBox(); }
        };
        p.FormClosed += (_, _) => { if (tabSearch == p) { tabSearch = null; tabSearchClosedAt = DateTime.Now; } };
        tabSearch = p;
        PlaceTabSearch(p, first: true);
        p.SetItems(TabSearchItems(), keep: false);
        PlaceTabSearch(p);
        p.Opacity = 0;
        p.Show(this);
        Fade.Start(p, 120);
        if (!Offscreen) { p.Activate(); p.FocusBox(); }
    }

    /// Sous le bouton liste, aligné à droite, sans sortir de la fenêtre.
    void PlaceTabSearch(TabSearchPopup p, bool first = false)
    {
        var lb = strip.RectangleToScreen(strip.ListBounds);
        var area = RectangleToScreen(ClientRectangle);
        int y = strip.PointToScreen(new Point(0, strip.Height)).Y + S(4);
        p.MaxHeight = Math.Max(S(220), area.Bottom - y - S(10));
        if (first) return;
        int x = Math.Clamp(lb.Right - p.Width, area.Left + S(8), Math.Max(area.Left + S(8), area.Right - p.Width - S(8)));
        p.Location = new Point(x, y);
    }

    List<BrowserTab> DuplicateTabs()
    {
        var list = tabs.Select(t => (TabUrl(t), t.Pinned, t.Private)).ToList();
        return TabTools.DuplicatesToClose(list, sel).Select(i => tabs[i]).ToList();
    }

    /// Ferme les onglets ouverts plusieurs fois sur la même adresse (rouvrables avec Ctrl+Maj+T). Renvoie le nombre fermé.
    int CloseDuplicateTabs()
    {
        var dupes = DuplicateTabs();
        foreach (var t in dupes) { int i = tabs.IndexOf(t); if (i >= 0 && tabs.Count > 1) CloseTab(i); }
        return dupes.Count;
    }

    /// Élément « Fermer les onglets en double » du menu de l'onglet.
    ToolStripMenuItem DuplicatesMenuItem()
    {
        int n = DuplicateTabs().Count;
        var it = MI(n > 0 ? $"Fermer les onglets en double ({n})" : "Fermer les onglets en double", () => CloseDuplicateTabs());
        it.Enabled = n > 0;
        it.ToolTipText = n > 0 ? "Garde un seul onglet par adresse (rouvrables avec Ctrl+Maj+T)" : "Aucun onglet ouvert deux fois";
        return it;
    }

    // ================== Test hors écran « onglets » ==================

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    static IntPtr Lp(int x, int y) => (IntPtr)((y & 0xFFFF) << 16 | (x & 0xFFFF));

    void StripClick(Point p)
    {
        SendMessage(strip.Handle, 0x0201, (IntPtr)1, Lp(p.X, p.Y)); // WM_LBUTTONDOWN
        SendMessage(strip.Handle, 0x0202, IntPtr.Zero, Lp(p.X, p.Y)); // WM_LBUTTONUP
    }

    void StripWheel(int delta)
    {
        var c = strip.PointToScreen(new Point(strip.Width / 2, strip.Height / 2));
        SendMessage(strip.Handle, 0x020A, (IntPtr)(delta << 16), Lp(c.X, c.Y)); // WM_MOUSEWHEEL (coordonnées écran)
    }

    async Task TabShot(string name)
    {
        await ShotAsync(name);
        var p = tabSearch;
        if (p is not { IsDisposed: false, Visible: true }) return;
        var file = Path.Combine(ShotsDir, name + ".png");
        try
        {
            Bitmap shot;
            using (var img = Image.FromFile(file)) shot = new Bitmap(img);
            using (shot)
            {
                using var pb = new Bitmap(p.Width, p.Height);
                p.DrawToBitmap(pb, new Rectangle(Point.Empty, p.Size));
                using (var g = Graphics.FromImage(shot)) g.DrawImage(pb, new Point(p.Left - Left, p.Top - Top));
                shot.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch (Exception ex) { CrashLog.Write("Capture " + name, ex); }
    }

    /// Bande de la barre d'onglets seule, en grand (pour juger la lisibilité).
    void StripShot(string name)
    {
        try
        {
            using var b = new Bitmap(strip.Width, strip.Height);
            strip.DrawToBitmap(b, new Rectangle(Point.Empty, strip.Size));
            b.Save(Path.Combine(ShotsDir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch { }
    }

    async Task TabsTest(string name)
    {
        var r = new List<string>();
        void Save() { try { File.WriteAllLines(Path.Combine(ShotsDir, name + ".txt"), r); } catch { } }
        void C(string what, bool ok, string d = "") { r.Add((ok ? "OK    " : "ÉCHEC ") + what + (d.Length > 0 ? " · " + d : "")); Save(); }
        // une page qui charge mal peut ne jamais rendre sa capture : on n'attend pas plus de 8 s
        async Task Wait8(Task job, string n) { if (await Task.WhenAny(job, Task.Delay(8000)) != job) r.Add("(capture " + n + " abandonnée : la page ne répond pas)"); }
        Task ShotAsync(string n) => Wait8(this.ShotAsync(n), n);
        Task TabShotAsync(string n) => Wait8(TabShot(n), n);
        async Task Settle() { for (int i = 0; i < 100 && strip.Animating; i++) await Task.Delay(20); await Task.Delay(60); }
        try
        {
            // Deux vrais sites (dont un en double), puis 24 onglets « à charger » comme après une restauration de session
            await NewTab("https://example.com/"); await Task.Delay(2500);
            var exA = Cur;
            await NewTab("https://example.com/"); await Task.Delay(2500);
            var exB = Cur;
            string[] sites =
            {
                "https://fr.wikipedia.org/wiki/%C3%89lectricit%C3%A9|Électricité — Wikipédia",
                "https://www.youtube.com/watch?v=jfKfPfyJRdk|lofi hip hop radio 📚 beats to relax/study to",
                "https://www.twitch.tv/gentlemates|GentleMates - Twitch",
                "https://github.com/Atlantaas/LiteBrowser|Atlantaas/LiteBrowser : navigateur léger",
                "https://www.lequipe.fr/|L'Équipe, l'actualité du sport en continu",
                "https://www.leboncoin.fr/|leboncoin, site de petites annonces",
                "https://mawaqit.net/fr/grande-mosquee-de-paris|Grande Mosquée de Paris — Mawaqit",
                "https://www.amazon.fr/|Amazon.fr : livres, DVD, jeux vidéo",
                "https://www.reddit.com/r/france/|r/france",
                "https://fr.wikipedia.org/wiki/Gentle_Mates|Gentle Mates — Wikipédia",
                "https://www.deepl.com/translator|DeepL Traducteur",
                "https://www.netflix.com/browse|Netflix",
                "https://open.spotify.com/|Spotify – Lecteur Web",
                "https://www.google.com/maps|Google Maps",
                "https://www.youtube.com/watch?v=jfKfPfyJRdk|lofi hip hop radio 📚 beats to relax/study to",
                "https://www.lemonde.fr/|Le Monde.fr - Actualités et Infos en France et dans le monde",
                "https://x.com/GentleMates|Gentle Mates (@GentleMates) / X",
                "https://www.instagram.com/gentlemates/|Gentle Mates • Instagram",
                "https://store.steampowered.com/|Steam",
                "https://liquipedia.net/valorant/Gentle_Mates|Gentle Mates - Liquipedia VALORANT Wiki",
                "https://www.twitch.tv/gentlemates|GentleMates - Twitch",
                "https://chat.openai.com/|ChatGPT",
                "https://example.com/|Example Domain",
                "https://www.marmiton.org/|Marmiton : recettes de cuisine",
            };
            foreach (var s in sites)
            {
                var parts = s.Split('|');
                var t = CreateTab(false, false);
                t.Lazy = true; t.PendingUrl = parts[0]; t.LastUrl = parts[0]; t.Title = parts[1];
                tabs.Add(t);
            }
            strip.Invalidate();
            SelectTab(tabs.IndexOf(exB));
            await Settle();
            int n = tabs.Count;
            C("barre qui déborde avec beaucoup d'onglets", strip.Overflowing, $"{n} onglets, fenêtre de {Width} px");
            C("largeur lisible gardée", strip.CurrentTabWidth >= strip.MinTabWidth && strip.MinTabWidth >= S(120), $"{strip.CurrentTabWidth} px par onglet (minimum {strip.MinTabWidth})");
            C("onglet affiché visible", strip.IsFullyVisible(sel), $"onglet {sel + 1}, décalage {strip.ScrollOffset:0}");
            C("aucune animation en cours au repos (pas de minuteur permanent)", !strip.Animating);
            await ShotAsync(name + "_1_debut"); StripShot(name + "_1_barre");

            // Molette sur la barre : défile vers la droite, en douceur
            float s0 = strip.ScrollOffset;
            StripWheel(-120 * 3);
            await Task.Delay(45);
            float mid = strip.ScrollOffset;
            await Settle();
            float s1 = strip.ScrollOffset;
            C("molette sur la barre : elle défile", s1 > s0 + S(100), $"{s0:0} → {s1:0} px");
            C("défilement animé (pas de saut)", mid > s0 && mid < s1, $"à 45 ms : {mid:0}");
            C("flèche gauche apparue", !strip.LeftArrowBounds.IsEmpty);
            StripShot(name + "_2_molette");

            // Flèche de droite jusqu'au bout, puis flèche de gauche
            for (int i = 0; i < 12 && !strip.RightArrowBounds.IsEmpty; i++) { var a = strip.RightArrowBounds; StripClick(new Point(a.Left + a.Width / 2, a.Top + a.Height / 2)); await Settle(); }
            C("flèche droite : jusqu'au dernier onglet", strip.IsFullyVisible(tabs.Count - 1) && strip.RightArrowBounds.IsEmpty, $"décalage {strip.ScrollOffset:0}");
            StripShot(name + "_3_bout");
            var la = strip.LeftArrowBounds;
            float s2 = strip.ScrollOffset;
            if (!la.IsEmpty) StripClick(new Point(la.Left + la.Width / 2, la.Top + la.Height / 2));
            await Settle();
            C("flèche gauche : revient en arrière", strip.ScrollOffset < s2, $"{s2:0} → {strip.ScrollOffset:0}");

            // Clic sur un onglet visible après défilement
            int target = Enumerable.Range(0, tabs.Count).Where(i => i != sel && strip.IsFullyVisible(i) && !tabs[i].Pinned).Skip(1).FirstOrDefault(-1);
            if (target >= 0)
            {
                var tr = strip.TabBounds(target);
                StripClick(new Point(tr.Left + S(30), tr.Top + tr.Height / 2));
                await Task.Delay(300);
                C("clic sur un onglet après défilement", sel == target, $"voulu {target + 1}, affiché {sel + 1} « {Cur.Title} »");
            }
            else C("clic sur un onglet après défilement", false, "aucun onglet visible trouvé");

            // Ctrl+1 : premier onglet → la barre revient au début ; dernier → au bout
            SelectTab(0); await Settle();
            C("premier onglet choisi : barre revenue au début", strip.ScrollOffset < 1 && strip.IsFullyVisible(0), $"décalage {strip.ScrollOffset:0}");
            SelectTab(tabs.Count - 1); await Settle();
            C("dernier onglet choisi : visible", strip.IsFullyVisible(tabs.Count - 1), $"décalage {strip.ScrollOffset:0}");
            int middle = tabs.Count / 2;
            SelectTab(middle); await Settle();
            C("onglet du milieu choisi : visible", strip.IsFullyVisible(middle), $"onglet {middle + 1}, décalage {strip.ScrollOffset:0}");
            StripShot(name + "_4_milieu");

            // Carte d'aperçu toujours sous le bon onglet après défilement
            ShowTabCard(middle); await Task.Delay(300);
            var cardX = tabCard?.Left ?? 0;
            var tabX = strip.PointToScreen(strip.TabBounds(middle).Location).X;
            C("carte d'aperçu sous l'onglet après défilement", tabCard?.Visible == true && Math.Abs(cardX - tabX) < S(4), $"carte {cardX}, onglet {tabX}");
            await ShotAsync(name + "_5_carte");
            TabHover(-1);

            // Nouvel onglet avec le + : ouvert au bout, visible, le + reste à droite
            int before = tabs.Count;
            var plus = strip.PlusBounds;
            StripClick(new Point(plus.Left + plus.Width / 2, plus.Top + plus.Height / 2));
            await Task.Delay(900); await Settle();
            C("le + ouvre un onglet visible au bout", tabs.Count == before + 1 && sel == tabs.Count - 1 && strip.IsFullyVisible(sel), $"{tabs.Count} onglets, affiché {sel + 1}, décalage {strip.ScrollOffset:0}");
            C("le + reste dans la barre", strip.PlusBounds.Right <= strip.Width && strip.ListBounds.Right <= strip.Width - S(4), $"+ à {strip.PlusBounds.X}, liste à {strip.ListBounds.X}, barre {strip.Width}");
            StripShot(name + "_6_nouvel");
            CloseTab(sel); await Settle();
            Save();

            // Recherche d'onglets : bouton liste
            SelectTab(tabs.IndexOf(exB)); await Settle();
            var lb = strip.ListBounds;
            StripClick(new Point(lb.Left + lb.Width / 2, lb.Top + lb.Height / 2));
            await Task.Delay(300);
            var p = tabSearch;
            C("bouton liste : recherche d'onglets ouverte", p is { Visible: true }, p == null ? "rien" : $"{p.Rows.Count} lignes");
            if (p != null)
            {
                C("tous les onglets listés", p.Rows.Count == tabs.Count, $"{p.Rows.Count} / {tabs.Count}");
                C("l'onglet affiché est présélectionné", p.SelectedItem?.Tab == Cur, p.SelectedItem?.Title ?? "");
                var area = RectangleToScreen(ClientRectangle);
                C("liste dans la fenêtre, sous la barre d'onglets", p.Left >= area.Left && p.Right <= area.Right && p.Top >= strip.PointToScreen(new Point(0, strip.Height)).Y && p.Bottom <= area.Bottom,
                    $"{p.Bounds} dans {area}");
                C("onglets au repos signalés", p.Rows.Any(x => x.State.Contains("Pas encore chargé")) && p.Rows.Any(x => x.Faded));
                await TabShotAsync(name + "_7_liste");

                p.Query = "electricite";
                await Task.Delay(100);
                C("filtre en tapant, sans accent", p.Rows.Count == 1 && p.Rows[0].Title.StartsWith("Électricité"), string.Join(" | ", p.Rows.Select(x => x.Title)));
                p.Query = "gentle";
                await Task.Delay(100);
                C("filtre sur le titre et l'adresse", p.Rows.Count >= 6, $"{p.Rows.Count} : " + string.Join(" | ", p.Rows.Select(x => x.Title)));
                await TabShotAsync(name + "_8_filtre");
                p.HandleKey(Keys.Down);
                var want = p.SelectedItem?.Tab;
                C("flèches : la sélection descend", p.SelectedIndex == 1, $"ligne {p.SelectedIndex + 1}");
                p.HandleKey(Keys.Enter);
                await Task.Delay(400);
                C("Entrée : va sur l'onglet et referme la liste", Cur == want && tabSearch == null, $"« {Cur.Title} »");
                await Settle();
                C("onglet choisi visible dans la barre", strip.IsFullyVisible(sel));

                await Task.Delay(300);
                ShowTabSearch(); // Ctrl+Maj+A
                p = tabSearch;
                C("Ctrl+Maj+A : liste rouverte", p is { Visible: true });
                p.Query = "marmiton";
                await Task.Delay(100);
                var victim = p.SelectedItem?.Tab;
                int count = tabs.Count;
                p.HandleKey(Keys.Delete);
                await Task.Delay(400);
                C("Suppr : ferme l'onglet choisi, la liste reste ouverte", victim != null && !tabs.Contains(victim) && tabs.Count == count - 1 && tabSearch == p && p.Visible,
                    $"{count} → {tabs.Count} onglets");
                C("liste mise à jour après fermeture", p.Rows.Count == 0, $"{p.Rows.Count} lignes pour « marmiton »");
                await TabShotAsync(name + "_9_vide");
                p.Query = "";
                await Task.Delay(100);
                C("liste complète à nouveau", p.Rows.Count == tabs.Count);
                // un onglet chargé puis libéré : indiqué
                var ex = exA;
                bool freed = DiscardTab(ex, force: true);
                p.SetItems(TabSearchItems(), keep: true);
                C("onglet libéré indiqué « Mémoire libérée »", freed && p.Rows.Any(x => x.Tab == ex && x.State.Contains("Mémoire libérée")), p.Rows.FirstOrDefault(x => x.Tab == ex)?.State ?? "");
                ShowTabSearch(); // même raccourci : referme
                await Task.Delay(200);
                C("Ctrl+Maj+A une 2e fois : liste refermée", tabSearch == null);
            }
            Save();

            // Menu de l'onglet : fermer les onglets en double
            var dup = DuplicateTabs();
            int nd = dup.Count; // 3 × example.com, 2 × Twitch, 2 × YouTube (moins ceux qu'une page chargée a redirigés)
            C("doublons repérés", nd >= 2, string.Join(" | ", dup.Select(x => x.Title)));
            SelectTab(tabs.IndexOf(exB));
            // (le menu n'est pas affiché : Windows ramènerait un menu hors écran sur ton écran ; on teste l'élément que ShowTabMenu y ajoute)
            var item = DuplicatesMenuItem();
            C("menu de l'onglet : « Fermer les onglets en double »", item is { Enabled: true } && item.Text.Contains($"({nd})"), item?.Text ?? "absent");
            int cnt = tabs.Count;
            var keepB = exB;
            item?.PerformClick();
            await Task.Delay(800); await Settle();
            var urls = tabs.Select(TabUrl).Where(u => Omni.IsWeb(u)).ToList();
            C("onglets en double fermés", tabs.Count == cnt - nd && urls.Count == urls.Distinct().Count(), $"{cnt} → {tabs.Count}");
            C("l'onglet affiché est gardé", tabs.Contains(keepB) && Cur == keepB, Cur.Title);
            C("rouvrables avec Ctrl+Maj+T", closedTabs.Count >= nd);
            var again = DuplicateTabs().Count;
            C("plus aucun double : élément grisé", again == 0 && !DuplicatesMenuItem().Enabled);

            // Peu d'onglets : la barre redevient normale
            foreach (var t in tabs.Where(x => x != keepB).Skip(3).ToList()) CloseTab(tabs.IndexOf(t));
            await Task.Delay(600); await Settle();
            C("peu d'onglets : plus de défilement", !strip.Overflowing && strip.ScrollOffset == 0 && strip.LeftArrowBounds.IsEmpty && strip.RightArrowBounds.IsEmpty,
                $"{tabs.Count} onglets de {strip.CurrentTabWidth} px, décalage {strip.ScrollOffset:0}");
            C("le + suit le dernier onglet", strip.PlusBounds.Left < strip.TabBounds(tabs.Count - 1).Right + S(12), $"+ à {strip.PlusBounds.X}, fin {strip.TabBounds(tabs.Count - 1).Right}");
            C("aucune animation en cours au repos", !strip.Animating);
            await ShotAsync(name + "_11_peu"); StripShot(name + "_11_barre");
        }
        catch (Exception ex) { C("test interrompu", false, ex.ToString()); }
        Save();
    }
}
