using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// ======================================================================
//  Vue partagée : deux onglets côte à côte (comme Edge / Vivaldi)
// ======================================================================
//  - splitLeft / splitRight : les deux onglets affichés ; la moitié « active » est toujours Cur (sel),
//    donc la barre d'adresse et les boutons agissent sur elle sans rien changer ailleurs.
//  - Les deux pages gardent Visible = true et un moteur réveillé (jamais gelées ni libérées).
//  - SplitSurface remplit la zone des pages, juste derrière les deux pages : elle dessine le fond,
//    le liseré rose AUTOUR de la page active (jamais par-dessus) et le séparateur qu'on glisse.
//    Elle cache aussi un onglet Nur Live/multistream laissé « sous les autres » (comme le ferait
//    un autre onglet affiché en plein) : jamais un élément par-dessus un lecteur affiché.

partial class BrowserForm
{
    BrowserTab splitLeft, splitRight, splitActive, shownLast, shownBefore;
    float splitRatio = .5f;
    SplitSurface splitSurface;
    readonly HashSet<WebView2> splitHooked = new();

    bool SplitOn => splitLeft != null && splitRight != null;
    /// Onglet affiché dans la vue partagée (il reste visible et réveillé même s'il n'est pas l'onglet choisi).
    bool SplitShows(BrowserTab t) => SplitOn && t != null && (t == splitLeft || t == splitRight);

    /// Onglet fermé (déjà retiré de la liste) : l'autre moitié de la vue partagée, à afficher à sa place, ou null.
    BrowserTab SplitPartner(BrowserTab closed)
    {
        if (!SplitShows(closed)) return null;
        var other = closed == splitLeft ? splitRight : splitLeft;
        return tabs.Contains(other) && other.Window == null ? other : null;
    }

    // ---------- Entrer / sortir ----------

    /// Affiche « other » à côté de l'onglet actuel ; il devient la moitié active.
    /// En vue partagée, il remplace la moitié qui n'est pas active.
    void SplitWith(BrowserTab other)
    {
        var cur = Cur;
        if (other == null || cur == null || !tabs.Contains(other)) return;
        if (other == cur) { SplitToggle(); return; }
        if (SplitOn)
        {
            if (SplitShows(other)) { SelectTab(tabs.IndexOf(other)); return; }
            var keep = splitActive ?? cur;
            var drop = keep == splitLeft ? splitRight : splitLeft;
            if (keep == splitLeft) splitRight = other; else splitLeft = other;
            SplitRelease(drop);
        }
        else
        {
            splitLeft = cur;
            splitRight = other;
            splitRatio = .5f;
        }
        splitActive = other;
        SplitHook(splitLeft); SplitHook(splitRight);
        SelectTab(tabs.IndexOf(other));
        SaveSessionSoon();
    }

    /// Ctrl+Maj+S : quitte la vue partagée, ou l'ouvre avec l'onglet vu juste avant (sinon le voisin, sinon un nouvel onglet).
    async void SplitToggle()
    {
        if (SplitOn) { SplitExit(); return; }
        var cur = Cur;
        if (cur == null) return;
        BrowserTab other = shownBefore != null && shownBefore != cur && tabs.Contains(shownBefore) ? shownBefore
            : sel + 1 < tabs.Count ? tabs[sel + 1] : sel > 0 ? tabs[sel - 1] : null;
        if (other != null) { SplitWith(other); return; }
        await NewTab();
        if (Cur != null && Cur != cur && tabs.Contains(cur)) SplitWith(Cur);
    }

    void SplitExit()
    {
        if (!SplitOn) return;
        SplitDrop();
        if (sel >= 0 && sel < tabs.Count) ShowTabViews(sel);
        strip.Invalidate();
    }

    /// Fin de la vue partagée sans réafficher : les deux pages reprennent toute la place.
    void SplitDrop()
    {
        var a = splitLeft; var b = splitRight;
        splitLeft = splitRight = splitActive = null;
        SplitRelease(a); SplitRelease(b);
        if (splitSurface != null) splitSurface.Visible = false;
    }

    /// Une page sort de la vue partagée : elle redevient pleine page (cachée ou non selon ShowTabViews).
    void SplitRelease(BrowserTab t)
    {
        if (t?.View == null) return;
        if (t.View.IsDisposed) { splitHooked.Remove(t.View); return; } // onglet fermé
        if (t.Window == null) t.View.Dock = DockStyle.Fill;
        SplitUnhook(t);
    }

    void SplitSwap()
    {
        if (!SplitOn) return;
        (splitLeft, splitRight) = (splitRight, splitLeft);
        splitRatio = 1 - splitRatio;
        SplitLayout();
    }

    // ---------- Branchements (appelés depuis Program.cs) ----------

    /// Début de ShowTabViews(i) : la vue partagée suit l'onglet choisi. Un autre onglet remplace la moitié active ;
    /// un onglet fermé, sorti dans sa fenêtre ou libéré met fin à la vue partagée proprement.
    void SplitPrepare(int i)
    {
        var t = i >= 0 && i < tabs.Count ? tabs[i] : null;
        if (t != null && t != shownLast) { shownBefore = shownLast; shownLast = t; }
        if (!SplitOn) return;
        bool Broken(BrowserTab x) => !tabs.Contains(x) || x.Window != null || x.View == null || x.View.IsDisposed;
        if (Broken(splitLeft) || Broken(splitRight) || splitLeft == splitRight) { SplitDrop(); return; }
        if (t == null) return;
        if (!SplitShows(t))
        {
            // un autre onglet choisi dans la barre : il prend la place de la moitié active
            var drop = splitActive != null && SplitShows(splitActive) ? splitActive : splitRight;
            if (drop == splitLeft) splitLeft = t; else splitRight = t;
            SplitRelease(drop);
            SplitHook(t);
        }
        splitActive = t;
    }

    /// Fin de ShowTabViews : place les deux pages, le séparateur et le liseré.
    void SplitLayout()
    {
        if (!SplitOn) { if (splitSurface != null) splitSurface.Visible = false; return; }
        if (splitSurface == null)
        {
            splitSurface = new SplitSurface(this) { Dock = DockStyle.Fill, Visible = false };
            content.Controls.Add(splitSurface);
            content.Resize += (_, _) => { if (SplitOn) SplitLayout(); };
            strip.PaintExtra += SplitPaintTabs;
        }
        var (lr, rr, gap) = SplitRects();
        int b = SplitBorder;
        var lv = splitLeft.View; var rv = splitRight.View;
        content.SuspendLayout();
        splitSurface.Visible = true;
        lv.Dock = DockStyle.None; rv.Dock = DockStyle.None;
        // page en plein écran (vidéo…) : elle prend toute la place, l'autre reste là, dessous (jamais cachée)
        var full = SplitFullscreen();
        if (full != null)
        {
            var other = full == lv ? rv : lv;
            other.Bounds = Rectangle.Inflate(other == lv ? lr : rr, -b, -b);
            full.Bounds = content.ClientRectangle;
            splitSurface.BringToFront(); other.BringToFront(); full.BringToFront();
        }
        else
        {
            lv.Bounds = Rectangle.Inflate(lr, -b, -b);
            rv.Bounds = Rectangle.Inflate(rr, -b, -b);
            // ordre : surface derrière les deux pages, toutes au-dessus des onglets laissés sous les autres
            splitSurface.BringToFront(); lv.BringToFront(); rv.BringToFront();
        }
        lv.Visible = rv.Visible = true;
        content.ResumeLayout();
        splitSurface.Invalidate();
        strip.Invalidate();
    }

    WebView2 SplitFullscreen()
    {
        foreach (var t in new[] { splitLeft, splitRight })
            try { if (t.View.CoreWebView2?.ContainsFullScreenElement == true) return t.View; } catch { }
        return null;
    }

    int SplitGap => S(16);
    int SplitBorder => Math.Max(2, S(2));
    int SplitMin => S(220);

    /// Rectangles des deux moitiés (liseré compris) et du séparateur, dans la zone des pages.
    (Rectangle Left, Rectangle Right, Rectangle Gap) SplitRects()
    {
        var c = content.ClientRectangle;
        int g = SplitGap, usable = Math.Max(0, c.Width - g);
        int lw = (int)Math.Round(usable * splitRatio);
        if (usable >= 2 * SplitMin) lw = Math.Clamp(lw, SplitMin, usable - SplitMin);
        else lw = usable / 2;
        return (new Rectangle(c.X, c.Y, lw, c.Height),
                new Rectangle(c.X + lw + g, c.Y, usable - lw, c.Height),
                new Rectangle(c.X + lw, c.Y, g, c.Height));
    }

    /// Le séparateur a été glissé jusqu'à x (dans la zone des pages).
    void SplitDragTo(int x)
    {
        int usable = Math.Max(1, content.ClientSize.Width - SplitGap);
        float r = (x - SplitGap / 2f) / usable;
        float min = usable >= 2 * SplitMin ? (float)SplitMin / usable : .5f;
        splitRatio = Math.Clamp(r, min, 1 - min);
        SplitLayout();
    }

    // ---------- Moitié active : un clic dans une page la rend active ----------

    void SplitHook(BrowserTab t)
    {
        if (t?.View == null || !splitHooked.Add(t.View)) return;
        t.View.GotFocus += SplitViewFocused;
        if (t.View.CoreWebView2 != null) t.View.CoreWebView2.ContainsFullScreenElementChanged += SplitFullscreenChanged;
        else t.View.CoreWebView2InitializationCompleted += SplitCoreReady;
    }

    void SplitUnhook(BrowserTab t)
    {
        if (t?.View == null || !splitHooked.Remove(t.View)) return;
        t.View.GotFocus -= SplitViewFocused;
        t.View.CoreWebView2InitializationCompleted -= SplitCoreReady;
        try { if (t.View.CoreWebView2 != null) t.View.CoreWebView2.ContainsFullScreenElementChanged -= SplitFullscreenChanged; } catch { }
    }

    void SplitCoreReady(object s, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (s is WebView2 v && v.CoreWebView2 != null && splitHooked.Contains(v))
            v.CoreWebView2.ContainsFullScreenElementChanged += SplitFullscreenChanged;
    }

    void SplitFullscreenChanged(object s, object e) => BeginInvoke(new Action(() => { if (SplitOn) SplitLayout(); }));

    void SplitViewFocused(object s, EventArgs e)
    {
        if (!SplitOn) return;
        // Le focus arrive du moteur en différé : un focus en retard (Focus() d'avant, page qui reprend le focus)
        // ne doit jamais annuler le nouvel onglet qui se prépare dessous. Seul un vrai clic l'annule.
        if (revealing != null && Control.MouseButtons == MouseButtons.None) return;
        var t = splitLeft.View == s ? splitLeft : splitRight.View == s ? splitRight : null;
        SplitActivate(t);
    }

    /// La moitié t devient active : barre d'adresse, boutons et raccourcis agissent sur elle.
    void SplitActivate(BrowserTab t)
    {
        if (t == null || !SplitShows(t) || t == Cur || !tabs.Contains(t)) return;
        sel = tabs.IndexOf(t);
        if (revealing != null)
        {
            // un nouvel onglet se préparait dessous : il n'apparaîtra plus, il redevient un onglet caché normal
            revealing = null;
            revealTimer.Stop();
            ShowTabViews(sel);
        }
        splitActive = t;
        if (t != shownLast) { shownBefore = shownLast; shownLast = t; }
        addrEdited = false;
        HideOmni();
        strip.Selected = sel;
        strip.Invalidate();
        splitSurface?.Invalidate();
        UpdateUi();
        toolbar.Invalidate();
    }

    // ---------- Barre d'onglets : les deux onglets de la vue partagée sont reliés ----------

    void SplitPaintTabs(Graphics g)
    {
        if (!SplitOn) return;
        int li = tabs.IndexOf(splitLeft), ri = tabs.IndexOf(splitRight);
        if (li < 0 || ri < 0) return;
        var la = strip.TabBounds(li); var ra = strip.TabBounds(ri);
        int h = Math.Max(2, S(2)), y = la.Bottom - S(5);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var soft = G.Mix(theme.Strip, theme.Accent, .55f);
        void Bar(Rectangle r, bool active)
        {
            using var b = new SolidBrush(active ? theme.Accent : soft);
            using var p = G.Round(new RectangleF(r.X + S(10), y, Math.Max(S(6), r.Width - S(20)), h), h / 2f);
            g.FillPath(b, p);
        }
        Bar(la, splitLeft == Cur);
        Bar(ra, splitRight == Cur);
        if (Math.Abs(li - ri) == 1)
        {
            // onglets voisins : un trait fin les relie, comme un groupe
            var (a, z) = li < ri ? (la, ra) : (ra, la);
            using var pen = new Pen(soft, Math.Max(1, S(1)));
            g.DrawLine(pen, a.Right - S(10), y + h / 2f, z.Left + S(10), y + h / 2f);
        }
    }

    // ---------- Menu de l'onglet ----------

    void SplitMenuItems(ContextMenuStrip m, BrowserTab t)
    {
        if (t.Window != null) return;
        if (SplitShows(t))
        {
            m.Items.Add(MI("Échanger les côtés", SplitSwap));
            m.Items.Add(MI("Quitter la vue partagée", SplitExit, "Ctrl+Maj+S"));
        }
        else if (t == Cur)
            m.Items.Add(MI("Vue partagée avec l'onglet précédent", SplitToggle, "Ctrl+Maj+S"));
        else
            m.Items.Add(MI("Afficher à côté de l'onglet actuel", () => SplitWith(t)));
    }

    // ---------- Surface : fond, liseré de la page active, séparateur ----------

    sealed class SplitSurface : Control
    {
        readonly BrowserForm f;
        readonly ToolTip tip = new();
        bool hoverGap, hoverClose, dragging;
        string tipShown;

        public SplitSurface(BrowserForm form)
        {
            f = form;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        int Sc(int v) => LogicalToDeviceUnits(v);
        Rectangle Gap => f.SplitRects().Gap;
        Rectangle CloseRect { get { var g = Gap; int s = g.Width; return new Rectangle(g.X, g.Y + Sc(6), s, s); } }

        public void SetHover(bool gap, bool close) { hoverGap = gap; hoverClose = close; Invalidate(); } // test hors écran

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var th = f.theme;
            g.Clear(th.Bar);
            if (!f.SplitOn) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var (lr, rr, gap) = f.SplitRects();
            int b = f.SplitBorder;
            // liseré autour des pages : rose discret pour la page active, trait neutre pour l'autre (dans la marge, jamais sur la page)
            foreach (var (r, t) in new[] { (lr, f.splitLeft), (rr, f.splitRight) })
            {
                bool active = t == f.Cur;
                var c = active ? G.Mix(th.Bar, th.Accent, .85f) : th.FieldBorder;
                int w = active ? b : Math.Max(1, b / 2);
                using var br = new SolidBrush(c);
                var inner = Rectangle.Inflate(r, -b, -b);
                g.FillRectangle(br, inner.X - w, inner.Y - w, inner.Width + 2 * w, w);           // haut
                g.FillRectangle(br, inner.X - w, inner.Bottom, inner.Width + 2 * w, w);          // bas
                g.FillRectangle(br, inner.X - w, inner.Y, w, inner.Height);                      // gauche
                g.FillRectangle(br, inner.Right, inner.Y, w, inner.Height);                      // droite
            }
            // séparateur : poignée fine, rose au survol et pendant le glissé
            bool hot = hoverGap || dragging;
            int gw = hot ? Math.Max(2, Sc(3)) : Math.Max(2, Sc(2));
            int gh = Math.Min(Sc(48), gap.Height / 3);
            var grip = new RectangleF(gap.X + (gap.Width - gw) / 2f, gap.Y + (gap.Height - gh) / 2f, gw, gh);
            using (var gb = new SolidBrush(hot ? th.Accent : G.Mix(th.Bar, th.Sub, .45f)))
            using (var gp = G.Round(grip, gw / 2f)) g.FillPath(gb, gp);
            if (hot)
            {
                using var lp = new Pen(Color.FromArgb(90, th.Accent), 1);
                float x = gap.X + gap.Width / 2f;
                g.DrawLine(lp, x, gap.Y + Sc(28), x, grip.Top - Sc(6));
                g.DrawLine(lp, x, grip.Bottom + Sc(6), x, gap.Bottom - Sc(6));
            }
            // bouton « quitter la vue partagée » en haut du séparateur
            var cr = CloseRect;
            if (hoverClose) { using var hb = new SolidBrush(th.Hover); g.FillEllipse(hb, cr); }
            TextRenderer.DrawText(g, Glyphs.Close, f.iconSmall, cr, hoverClose ? th.Accent : th.Sub, G.Center);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) { f.SplitDragTo(e.X); return; }
            bool inGap = Gap.Contains(e.Location), inClose = CloseRect.Contains(e.Location);
            Cursor = inClose ? Cursors.Hand : inGap ? Cursors.SizeWE : Cursors.Default;
            string want = inClose ? "Quitter la vue partagée (Ctrl+Maj+S)" : inGap ? "Glisse pour régler la largeur · double-clic : moitié-moitié" : null;
            if (want != tipShown) { tipShown = want; tip.SetToolTip(this, want); }
            if (inGap != hoverGap || inClose != hoverClose) { hoverGap = inGap && !inClose; hoverClose = inClose; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (dragging) return;
            hoverGap = hoverClose = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (CloseRect.Contains(e.Location)) { f.BeginInvoke(new Action(f.SplitExit)); return; }
            if (Gap.Contains(e.Location)) { dragging = true; Capture = true; Invalidate(); return; }
            // clic dans la marge d'une page : elle devient active
            var (lr, rr, _) = f.SplitRects();
            if (lr.Contains(e.Location)) f.SplitActivate(f.splitLeft);
            else if (rr.Contains(e.Location)) f.SplitActivate(f.splitRight);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            Capture = false;
            hoverGap = Gap.Contains(e.Location);
            Invalidate();
            f.Cur?.View.Focus();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && Gap.Contains(e.Location) && !CloseRect.Contains(e.Location))
            { f.splitRatio = .5f; f.SplitLayout(); }
        }
    }

    // ---------- Test hors écran : LITEBROWSER_SHOTS_PAGES=partage=partage ----------

    /// Capture de la fenêtre avec les DEUX pages de la vue partagée à leur place (ShotAsync ne dessine que l'onglet choisi).
    async Task SplitShotAsync(string name)
    {
        try
        {
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
            using var g = Graphics.FromImage(bmp);
            var views = SplitOn ? new[] { splitLeft.View, splitRight.View } : Cur != null ? new[] { Cur.View } : Array.Empty<WebView2>();
            foreach (var v in views)
            {
                if (v.CoreWebView2 == null || !v.Visible) continue;
                using var ms = new MemoryStream();
                await v.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                ms.Position = 0;
                using var page = Image.FromStream(ms);
                var at = v.PointToScreen(Point.Empty);
                g.DrawImage(page, new Rectangle(at.X - Left, at.Y - Top, v.Width, v.Height));
            }
            if (lastMenu is { Visible: true } m)
            {
                using var mb = new Bitmap(m.Width, m.Height);
                m.DrawToBitmap(mb, new Rectangle(Point.Empty, m.Size));
                g.DrawImage(mb, new Point(m.Left - Left, m.Top - Top));
            }
            bmp.Save(Path.Combine(ShotsDir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { CrashLog.Write("Capture " + name, ex); }
    }

    async Task SplitTest(string name)
    {
        var r = new List<string>();
        void C(string what, bool ok, string d = "") => r.Add((ok ? "OK    " : "ÉCHEC ") + what + (d.Length > 0 ? " · " + d : ""));
        void Save() { try { File.WriteAllLines(Path.Combine(ShotsDir, name + ".txt"), r); } catch { } }
        bool Awake(BrowserTab t) => t.View.Visible && t.View.CoreWebView2 != null && !t.View.CoreWebView2.IsSuspended &&
                                    t.View.CoreWebView2.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal;
        string Desc(BrowserTab t) => $"« {t?.Title} » {t?.View.Bounds} visible={t?.View.Visible} dock={t?.View.Dock}";
        bool SideBySide()
        {
            if (!SplitOn) return false;
            var a = splitLeft.View.Bounds; var b = splitRight.View.Bounds; var c = content.ClientRectangle;
            return !a.IntersectsWith(b) && a.Right < b.Left && c.Contains(a) && c.Contains(b) && a.Width > 100 && b.Width > 100 &&
                   a.Height > c.Height - 10 && b.Height > c.Height - 10;
        }
        // Nouvel onglet ouvert en vue partagée : c'est lui l'onglet choisi, juste après l'ouverture
        async Task<BrowserTab> OpenInSplit(string url, string what)
        {
            var before = tabs.ToList();
            await NewTab(url);
            var opened = tabs.Except(before).FirstOrDefault();
            C(what + " : choisi juste après l'ouverture", opened != null && Cur == opened, Desc(Cur));
            await Task.Delay(300);
            C(what + " : toujours choisi 300 ms après (focus en retard ignoré)", opened != null && Cur == opened, Desc(Cur));
            return opened ?? Cur;
        }
        try
        {
            await NewTab("https://example.com/"); await Task.Delay(3500);
            var ex = Cur;
            await NewTab("https://fr.wikipedia.org/wiki/Gentle_Mates"); await Task.Delay(4500);
            var wiki = Cur;

            // 1. Menu de l'onglet
            // (menu construit sans être montré : un menu se replace sur l'écran visible, même pour une fenêtre hors écran)
            using (var tm = new ContextMenuStrip())
            {
                SplitMenuItems(tm, ex);
                var side = tm.Items.OfType<ToolStripMenuItem>().FirstOrDefault(x => x.Text == "Afficher à côté de l'onglet actuel");
                C("menu de l'onglet : « Afficher à côté de l'onglet actuel »", side != null,
                    string.Join(" | ", tm.Items.OfType<ToolStripMenuItem>().Select(x => x.Text)));
                C("… sans raccourci affiché (Ctrl+Maj+S fait autre chose)", side != null && string.IsNullOrEmpty(side.ShortcutKeyDisplayString), side?.ShortcutKeyDisplayString ?? "");
            }
            using (var tm = new ContextMenuStrip())
            {
                SplitMenuItems(tm, wiki);
                var prev = tm.Items.OfType<ToolStripMenuItem>().FirstOrDefault(x => x.Text == "Vue partagée avec l'onglet précédent");
                C("onglet actuel : « Vue partagée avec l'onglet précédent » (Ctrl+Maj+S)", prev?.ShortcutKeyDisplayString == "Ctrl+Maj+S");
            }

            // 2. Vue partagée
            SplitWith(ex); await Task.Delay(1500);
            C("deux onglets côte à côte", SideBySide() && splitLeft == wiki && splitRight == ex, Desc(splitLeft) + " | " + Desc(splitRight));
            C("les deux pages restent visibles et réveillées", Awake(wiki) && Awake(ex));
            C("l'onglet ajouté devient la moitié active (barre d'adresse)", Cur == ex && addr.Box.Text.Contains("example.com"), addr.Box.Text);
            var gap = SplitRects().Gap;
            C("liseré et séparateur hors des pages", !gap.IntersectsWith(wiki.View.Bounds) && !gap.IntersectsWith(ex.View.Bounds) &&
                wiki.View.Left >= SplitBorder && ex.View.Right <= content.ClientSize.Width - SplitBorder, $"séparateur {gap}");
            await Task.Delay(1500);
            await SplitShotAsync(name + "_2_partage");
            using (var tm = new ContextMenuStrip())
            {
                SplitMenuItems(tm, wiki);
                C("menu d'un onglet partagé : échanger / quitter", tm.Items.OfType<ToolStripMenuItem>().Any(x => x.Text == "Quitter la vue partagée") &&
                    tm.Items.OfType<ToolStripMenuItem>().Any(x => x.Text == "Échanger les côtés"));
            }
            SplitSwap();
            C("échanger les côtés", splitLeft == ex && splitRight == wiki && SideBySide() && ex.View.Left < wiki.View.Left);
            SplitSwap();

            // 3. Clic dans l'autre moitié
            wiki.View.Focus(); await Task.Delay(500);
            bool viaFocus = Cur == wiki;
            if (!viaFocus) SplitActivate(wiki); // fenêtre de test jamais active : le focus Windows peut être refusé
            C("cliquer dans une moitié la rend active", Cur == wiki && addr.Box.Text.Contains("wikipedia"), (viaFocus ? "par le focus de la page" : "par SplitActivate (focus refusé hors écran)") + " · " + addr.Box.Text);
            Core.Navigate("https://example.org/"); await Task.Delay(3000);
            C("la barre d'adresse agit sur la moitié active", wiki.View.CoreWebView2.Source.Contains("example.org") && ex.View.CoreWebView2.Source.Contains("example.com"),
                wiki.View.CoreWebView2.Source + " / " + ex.View.CoreWebView2.Source);
            wiki.View.CoreWebView2.GoBack(); await Task.Delay(3000);

            // 4. Séparateur glissé
            int before = splitLeft.View.Width;
            splitSurface.SetHover(true, false);
            SplitDragTo(content.ClientSize.Width * 2 / 3);
            await Task.Delay(800);
            C("séparateur glissé : la largeur change", splitLeft.View.Width > before + 100 && SideBySide(), $"{before} → {splitLeft.View.Width} px");
            await SplitShotAsync(name + "_3_glisse");
            SplitDragTo(5);
            C("largeur minimale respectée", splitLeft.View.Width >= SplitMin - 2 * SplitBorder - 1, $"{splitLeft.View.Width} px");
            splitSurface.SetHover(false, true);
            splitRatio = .5f; SplitLayout(); await Task.Delay(600);
            await SplitShotAsync(name + "_4_bouton_quitter");
            splitSurface.SetHover(false, false);

            // 5. Barre d'onglets
            using (var sb = new Bitmap(strip.Width, strip.Height))
            {
                strip.DrawToBitmap(sb, new Rectangle(Point.Empty, strip.Size));
                var ta = strip.TabBounds(tabs.IndexOf(splitLeft));
                var px = sb.GetPixel(ta.X + ta.Width / 2, ta.Bottom - S(5) + 1);
                C("onglets repérés dans la barre (trait rose)", px.R > 120 && px.B > 120 && px.G < 140, px.ToString());
                sb.Save(Path.Combine(ShotsDir, name + "_5_barre.png"));
            }

            // 6. Un autre onglet choisi : il remplace la moitié active
            // focus en retard de la moitié active (comme wiki.View.Focus() plus haut) pendant l'ouverture : il ne doit rien annuler
            wiki.View.Focus();
            var third = await OpenInSplit("https://example.net/", "nouvel onglet");
            await Task.Delay(4000);
            C("nouvel onglet : toujours l'onglet choisi 4 s après", Cur == third && third.View.Visible, Desc(Cur));
            C("nouvel onglet : il remplace la moitié active", SplitShows(third) && SplitShows(ex) && !SplitShows(wiki) && SideBySide(), Desc(splitLeft) + " | " + Desc(splitRight));
            C("l'onglet remplacé redevient un onglet normal", wiki.View.Dock == DockStyle.Fill && !wiki.View.Visible, Desc(wiki));
            SelectTab(tabs.IndexOf(wiki)); await Task.Delay(1500);
            C("onglet choisi dans la barre : il remplace la moitié active", SplitShows(wiki) && SplitShows(ex) && !SplitShows(third) && Cur == wiki && Awake(wiki) && Awake(ex), Desc(splitLeft) + " | " + Desc(splitRight));
            await SplitShotAsync(name + "_6_remplace");

            // 7. Ctrl+Maj+S
            var msg = new Message();
            ProcessCmdKey(ref msg, Keys.Control | Keys.Shift | Keys.S); await Task.Delay(800);
            C("Ctrl+Maj+S quitte la vue partagée", !SplitOn && Cur.View.Dock == DockStyle.Fill && Cur.View.Visible && tabs.Count(x => x.View.Visible) == 1 && !splitSurface.Visible,
                $"{tabs.Count(x => x.View.Visible)} page(s) visible(s)");
            ProcessCmdKey(ref msg, Keys.Control | Keys.Shift | Keys.S); await Task.Delay(1200);
            C("Ctrl+Maj+S la rouvre avec l'onglet vu juste avant", SplitOn && SplitShows(wiki) && SideBySide(), Desc(splitLeft) + " | " + Desc(splitRight));

            // 8. Multistream (KeepsPlaying) : jamais caché, ni à côté ni dessous
            var multi = await OpenInSplit(MultiUrl, "multistream");
            await Task.Delay(3500);
            C("multistream : toujours l'onglet choisi 3,5 s après", Cur == multi, Desc(Cur));
            C("multistream ouvert dans la vue partagée", SplitShows(multi) && SideBySide() && multi.View.Visible, Desc(multi));
            await SplitShotAsync(name + "_7_multistream");
            SelectTab(tabs.IndexOf(third)); await Task.Delay(1500);
            C("multistream sorti de la vue partagée : jamais caché (sous les autres)", !SplitShows(multi) && multi.View.Visible && multi.View.Dock == DockStyle.Fill &&
                content.Controls.GetChildIndex(multi.View) > content.Controls.GetChildIndex(splitSurface), Desc(multi));
            SplitWith(multi); await Task.Delay(1200);

            // 8 bis. Nouvel onglet qui se prépare dessous (apparition douce) puis clic dans une moitié : il ne reste pas affiché dessous
            SplitExit(); await Task.Delay(500);
            SelectTab(tabs.IndexOf(ex)); await Task.Delay(800);
            SplitWith(wiki); await Task.Delay(1200);
            SelectTab(tabs.IndexOf(third), soft: true);
            bool wasRevealing = revealing == third;
            // focus de page arrivé en retard, sans clic : l'apparition continue
            SplitViewFocused(ex.View, EventArgs.Empty);
            SplitViewFocused(wiki.View, EventArgs.Empty);
            C("focus en retard d'une moitié (sans clic) : le nouvel onglet n'est pas annulé", wasRevealing && revealing == third && Cur == third, Desc(Cur));
            SplitActivate(ex); await Task.Delay(800);
            C("apparition douce interrompue par un clic dans une moitié : l'onglet repart caché", wasRevealing && revealing == null && !third.View.Visible &&
                Cur == ex && SplitShows(ex) && SplitShows(wiki) && SideBySide(), Desc(third));

            // 9. Fermer la moitié ACTIVE (pas le dernier onglet, titres différents) : l'autre moitié reste affichée
            SplitExit(); await Task.Delay(500);
            SelectTab(tabs.IndexOf(ex)); await Task.Delay(800);
            SplitWith(third); await Task.Delay(1500); // [.. ex, wiki, third, multi] : third active, ex à côté
            var survivor = ex;
            C("préparation : moitié active au milieu de la barre", Cur == third && SplitShows(ex) && tabs.IndexOf(third) < tabs.Count - 1 && tabs[tabs.IndexOf(third) + 1] != ex,
                string.Join(" | ", tabs.Select(x => x.Title)));
            CloseTab(sel); await Task.Delay(1500);
            C("fermer la moitié active affiche l'autre moitié (pas le voisin)", !SplitOn && !splitSurface.Visible && Cur == survivor && Cur.View.Dock == DockStyle.Fill &&
                Cur.View.Bounds.Size == content.ClientSize && survivor.View.Visible && Awake(survivor) && tabs.Count(x => x.View.Visible && !KeepsPlaying(x.View.CoreWebView2)) == 1,
                $"affiché : {Desc(Cur)} (prévu : « {survivor.Title} » {survivor.View.CoreWebView2?.Source})");
            await SplitShotAsync(name + "_8_apres_fermeture");

            // 9 bis. Fermer la moitié NON active : la moitié active reste affichée
            SplitWith(wiki); await Task.Delay(1200); // wiki active, ex à côté
            var keepActive = Cur;
            CloseTab(tabs.IndexOf(ex)); await Task.Delay(1500);
            C("fermer la moitié non active garde la moitié active", !SplitOn && Cur == keepActive && Cur == wiki && Cur.View.Dock == DockStyle.Fill &&
                Cur.View.Bounds.Size == content.ClientSize && wiki.View.Visible, Desc(Cur));

            // 10. Sortir un onglet dans sa fenêtre
            SplitWith(tabs.First(x => x != Cur)); await Task.Delay(1200);
            var gone = splitActive;
            DetachTab(tabs.IndexOf(gone), new Point(-3800, -3800)); await Task.Delay(2000);
            C("onglet sorti dans sa fenêtre : vue partagée quittée", !SplitOn && Cur.View.Dock == DockStyle.Fill && Cur.View.Visible && gone.Window != null, Desc(Cur));
            foreach (var w in detached.ToList()) ReattachWindow(w);
            await Task.Delay(1000);
            C("aucun onglet libéré ou gelé par erreur", tabs.All(x => !x.Discarded), string.Join(" | ", tabs.Select(x => x.Title)));
        }
        catch (Exception e) { C("test interrompu", false, e.ToString()); }
        Save();
    }
}
