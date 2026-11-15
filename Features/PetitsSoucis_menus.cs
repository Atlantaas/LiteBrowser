// Petits soucis des menus et des fenêtres flottantes (chantier « menus ») :
// - un menu ouvert pendant que la page a le clavier ne recevait jamais Échap, les flèches ni Entrée (la page est dans un
//   autre processus) : le clavier passe au menu le temps qu'il est ouvert, puis revient à la page ;
// - re-cliquer le bouton d'un menu ouvert (bouclier, extensions, prière, » des favoris, ⋯) le refermait puis le rouvrait aussitôt ;
// - le bouton reste allumé tant que son menu est ouvert (comme Chrome) ;
// - Lens restait figé à l'ancienne place quand la fenêtre bougeait, changeait de taille ou d'onglet ;
// - les propositions de la barre d'adresse restaient affichées après un changement d'onglet ;
// - la carte d'aperçu d'onglet s'ouvrait par-dessus un menu, la recherche d'onglets ou Lens ;
// - la carte d'aperçu était bornée à l'écran de la fenêtre, pas à celui de l'onglet (plusieurs écrans) ;
// - les boîtes de saisie coupaient leur texte d'explication à la 2e ligne ;
// - « (1 bloquées) » dans le menu ☰.
// Test hors écran : LITEBROWSER_SHOTS_PAGES=pc-menus=pc-menus (aucun vrai menu n'est ouvert : Windows le ramènerait sur l'écran).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using LiteCore;

/// Contrôle invisible qui garde le clavier dans LiteBrowser pendant qu'un menu est ouvert.
sealed class MenuKeySink : Control
{
    public MenuKeySink()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = false;
        Size = Size.Empty;
        AccessibleRole = AccessibleRole.None;
    }
}

static class PetitsSoucis
{
    /// Boîte de saisie : l'explication garde toutes ses lignes, la case et les boutons se placent dessous.
    public static void FitPrompt(Form f, Label lbl, Control box, Control ok, Control cancel)
    {
        float k = Math.Max(1f, lbl.Top / 14f); // l'échelle déjà appliquée (125 %, 150 %…)
        int need = NeededHeight(lbl);
        if (lbl.Height != need) lbl.Height = need;
        int boxTop = Math.Max((int)(60 * k), lbl.Bottom + (int)(6 * k));
        if (box.Top != boxTop) box.Top = boxTop;
        int btnTop = Math.Max((int)(104 * k), box.Bottom + (int)(20 * k));
        if (ok.Top != btnTop) ok.Top = btnTop;
        if (cancel.Top != btnTop) cancel.Top = btnTop;
        int h = Math.Max((int)(150 * k), ok.Bottom + (int)(16 * k));
        if (f.ClientSize.Height != h) f.ClientSize = new Size(f.ClientSize.Width, h);
    }

    public static int NeededHeight(Label lbl) =>
        TextRenderer.MeasureText(lbl.Text, lbl.Font, new Size(lbl.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 2;

    /// « 1 bloquée », « 12 bloquées ».
    public static string Blocked(long n) => n > 1 ? $"{n} bloquées" : $"{n} bloquée";
}

partial class BrowserForm
{
    MenuKeySink menuSink;
    readonly HashSet<ToolStripDropDown> openMenus = new(); // menus ouverts (un menu jeté sans se fermer en sort aussi)
    int menusOpen { get { openMenus.RemoveWhere(x => x.IsDisposed); return openMenus.Count; } }
    WebView2 menuReturnTo;          // page qui avait le clavier avant l'ouverture du menu
    Point menuCloseAt;              // où était la souris quand un clic à côté a refermé le dernier menu

    [StructLayout(LayoutKind.Sequential)]
    struct GuiThreadInfo { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public int l, t, r, b; }
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GuiThreadInfo info);
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    /// Fenêtre qui a le clavier pour ce fil (y compris celle de la page, tenue par le processus du moteur).
    static IntPtr KeyboardWindow()
    {
        var h = GetFocus();
        if (h != IntPtr.Zero) return h;
        var gi = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(GetCurrentThreadId(), ref gi) ? gi.hwndFocus : IntPtr.Zero;
    }

    static bool OtherProcess(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        GetWindowThreadProcessId(h, out var pid);
        return pid != (uint)Environment.ProcessId;
    }

    /// La page qui a le clavier : ses touches vont au moteur web (autre processus), jamais au menu de LiteBrowser.
    WebView2 PageWithKeyboard()
    {
        var h = KeyboardWindow();
        if (!OtherProcess(h)) return null;
        foreach (var t in tabs)
        {
            var v = t.View;
            if (v != null && !v.IsDisposed && v.IsHandleCreated && v.FindForm() == this && IsChild(v.Handle, h)) return v;
        }
        return null;
    }

    /// Branché sur chaque menu de LiteBrowser (Tag = bouton qui l'ouvre, allumé tant qu'il est ouvert).
    void MenuHooks(ToolStripDropDown m)
    {
        m.Opened += (_, _) => MenuOpened(m);
        m.Closed += (_, e) => MenuClosed(m, e.CloseReason, Cursor.Position);
    }

    void MenuOpened(ToolStripDropDown m)
    {
        if (openMenus.Add(m)) m.Disposed += (_, _) => openMenus.Remove(m);
        TabHover(-1);
        if (m.Tag is IconButton b) b.Open = true;
        // La page a le clavier : Échap, flèches et Entrée iraient à la page et le menu resterait ouvert.
        // Le clavier passe au menu (contrôle invisible de la fenêtre), il reviendra à la page à la fermeture.
        var page = PageWithKeyboard();
        if (page == null) return;
        menuReturnTo = page;
        if (menuSink == null || menuSink.IsDisposed)
        {
            menuSink = new MenuKeySink();
            menuSink.GotFocus += (_, _) =>
            {
                // reçu hors d'un menu (retour d'une boîte de dialogue…) : le clavier retourne à la page
                if (menusOpen == 0) BeginInvoke(new Action(() => { if (menusOpen == 0 && menuSink.Focused) Cur?.View.Focus(); }));
            };
            toolbar.Controls.Add(menuSink);
        }
        menuSink.Focus();
    }

    void MenuClosed(ToolStripDropDown m, ToolStripDropDownCloseReason reason, Point mouse)
    {
        openMenus.Remove(m);
        if (m.Tag is IconButton b) b.Open = false;
        if (reason == ToolStripDropDownCloseReason.AppClicked) { Fade.LastOutsideClose = DateTime.Now; menuCloseAt = mouse; }
        var back = menuReturnTo;
        menuReturnTo = null;
        if (back == null || IsDisposed) return;
        // après l'action choisie : si rien d'autre n'a pris le clavier (barre d'adresse, nouvel onglet…), il revient à la page
        BeginInvoke(new Action(() =>
        {
            if (menusOpen > 0 || menuSink == null || !menuSink.Focused) return;
            // la page d'avant si elle est toujours l'onglet actif (l'action a pu changer d'onglet ou de moitié de la vue partagée)
            var target = !back.IsDisposed && back.Visible && back == Cur?.View ? back : Cur?.View;
            target?.Focus();
        }));
    }

    /// Clic sur le bouton d'un menu qui vient de le refermer (le clic à côté) : on ne le rouvre pas dans la foulée.
    bool MenuReclick(Control btn) => MenuReclick(btn, Cursor.Position);

    bool MenuReclick(Control btn, Point mouse)
    {
        if (btn == null || btn.IsDisposed || !btn.IsHandleCreated) return false;
        var r = btn.RectangleToScreen(btn.ClientRectangle);
        if ((DateTime.Now - Fade.LastOutsideClose).TotalMilliseconds > 600 || !r.Contains(menuCloseAt) || !r.Contains(mouse)) return false;
        Fade.LastOutsideClose = DateTime.MinValue; // un seul clic avalé : le suivant rouvre le menu
        return true;
    }

    /// Un menu, la recherche d'onglets ou Lens est ouvert : pas de carte d'aperçu par-dessus.
    bool FloatingBusy => menusOpen > 0 || tabSearch is { IsDisposed: false, Visible: true } || lens != null;

    /// Changement d'onglet (appelé par SelectTab).
    void MenusTabChanged()
    {
        HideOmni(); // la barre d'adresse peut garder le focus (onglet d'accueil) : ses propositions ne valent plus
        if (lens is { IsDisposed: false }) lens.Close(); // l'image figée est celle de l'ancien onglet
    }

    /// Lens recouvre la page : la fenêtre bouge, change de taille ou se réduit, il se referme (sinon il reste figé à côté).
    void LensWatch(LensOverlay l)
    {
        void Gone(object s, EventArgs e) { if (!l.IsDisposed && l.Visible) l.Close(); }
        LocationChanged += Gone;
        Resize += Gone;
        l.FormClosed += (_, _) => { LocationChanged -= Gone; Resize -= Gone; };
    }

    /// Écran de l'onglet survolé (la fenêtre peut être à cheval sur deux écrans).
    static Rectangle TabCardArea(Point at) => Screen.FromPoint(at).WorkingArea;

    // ================== Test hors écran « pc-menus » ==================

    async Task PcMenusTest(string name)
    {
        var r = new List<string>();
        void Save() { try { File.WriteAllLines(Path.Combine(ShotsDir, name + ".txt"), r); } catch { } }
        void C(string what, bool ok, string d = "") { r.Add((ok ? "OK    " : "ÉCHEC ") + what + (d.Length > 0 ? " · " + d : "")); Save(); }
        async Task<bool> Until(Func<bool> f, int ms) { for (int i = 0; i < ms / 50; i++) { if (f()) return true; await Task.Delay(50); } return f(); }
        void Bmp(Control c, string file)
        {
            try { using var b = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height)); c.DrawToBitmap(b, new Rectangle(Point.Empty, c.Size)); b.Save(Path.Combine(ShotsDir, file + ".png")); } catch { }
        }
        try
        {
            await NewTab("https://example.com/");
            for (int i = 0; i < 100 && Cur.Loading; i++) await Task.Delay(100);
            await Task.Delay(800);
            var pageTab = Cur;

            // 1. Clavier : la page l'a, un menu s'ouvre, Échap le referme, la page le récupère
            Cur.View.Focus();
            await Until(() => PageWithKeyboard() != null, 3000);
            var kw = KeyboardWindow();
            C("constat : page cliquée, ses touches partent au moteur web (autre processus), pas au menu", PageWithKeyboard() == Cur.View && OtherProcess(kw),
                $"fenêtre du clavier 0x{kw.ToInt64():X}, autre processus : {OtherProcess(kw)}");
            var fake = new ContextMenuStrip { Tag = menuBtn };
            MenuOpened(fake);
            await Task.Delay(150);
            var kw2 = KeyboardWindow();
            C("menu ouvert : le clavier passe à LiteBrowser (Échap, flèches, Entrée arrivent au menu)", menuSink is { Focused: true } && !OtherProcess(kw2) && PageWithKeyboard() == null,
                $"fenêtre du clavier 0x{kw2.ToInt64():X}, contrôle {(Control.FromHandle(kw2)?.GetType().Name ?? "?")}");
            C("bouton ⋯ allumé tant que son menu est ouvert", menuBtn.Open);
            Bmp(toolbar, name + "_1_bouton_allume");
            using (var on = new Bitmap(menuBtn.Width, menuBtn.Height))
            {
                menuBtn.DrawToBitmap(on, new Rectangle(Point.Empty, menuBtn.Size));
                MenuClosed(fake, ToolStripDropDownCloseReason.Keyboard, Point.Empty);
                using var off = new Bitmap(menuBtn.Width, menuBtn.Height);
                menuBtn.DrawToBitmap(off, new Rectangle(Point.Empty, menuBtn.Size));
                int diff = 0;
                for (int x = 0; x < on.Width; x += 2) for (int y = 0; y < on.Height; y += 2) if (on.GetPixel(x, y) != off.GetPixel(x, y)) diff++;
                C("menu refermé : le bouton s'éteint", !menuBtn.Open && diff > 10, $"{diff} points différents entre allumé et éteint");
            }
            await Until(() => PageWithKeyboard() == pageTab.View, 3000);
            C("Échap : le clavier revient à la page", PageWithKeyboard() == pageTab.View, $"fenêtre du clavier 0x{KeyboardWindow().ToInt64():X}");

            // clavier déjà dans la barre d'adresse : le menu ne le déplace pas, rien n'est rendu à la page
            addr.Box.Focus();
            await Task.Delay(150);
            var fake2 = new ContextMenuStrip();
            MenuOpened(fake2);
            bool stayed = addr.Box.Focused;
            MenuClosed(fake2, ToolStripDropDownCloseReason.Keyboard, Point.Empty);
            await Task.Delay(200);
            C("barre d'adresse : le menu laisse le clavier où il est", stayed && addr.Box.Focused);

            // action du menu qui ouvre une saisie (le clavier part ailleurs) : jamais repris de force
            Cur.View.Focus();
            await Until(() => PageWithKeyboard() != null, 3000);
            var fake3 = new ContextMenuStrip();
            MenuOpened(fake3);
            MenuClosed(fake3, ToolStripDropDownCloseReason.ItemClicked, Point.Empty);
            addr.Box.Focus(); // l'action a mis le clavier ailleurs
            await Task.Delay(250);
            C("action qui prend le clavier : il n'est pas ramené à la page", addr.Box.Focused);
            Cur.View.Focus();
            await Task.Delay(200);
            // retour d'une boîte de dialogue sur le contrôle invisible : le clavier retourne à la page
            menuSink?.Focus();
            await Until(() => PageWithKeyboard() != null, 3000);
            C("contrôle invisible qui reçoit le clavier hors menu : rendu à la page", PageWithKeyboard() == pageTab.View);

            // 2. Re-clic sur le bouton d'un menu ouvert : refermé, pas rouvert
            var shieldCenter = addr.Shield.PointToScreen(new Point(addr.Shield.Width / 2, addr.Shield.Height / 2));
            var fake4 = new ContextMenuStrip { Tag = addr.Shield };
            MenuOpened(fake4);
            MenuClosed(fake4, ToolStripDropDownCloseReason.AppClicked, shieldCenter);
            bool swallowed = MenuReclick(addr.Shield, shieldCenter);
            bool next = MenuReclick(addr.Shield, shieldCenter);
            C("bouclier re-cliqué menu ouvert : le menu se referme sans se rouvrir", swallowed, "clic avalé");
            C("le clic suivant rouvre bien le menu", !next);
            var extCenter = extBtn.PointToScreen(new Point(extBtn.Width / 2, extBtn.Height / 2));
            MenuClosed(new ContextMenuStrip(), ToolStripDropDownCloseReason.AppClicked, shieldCenter);
            C("menu refermé par un clic ailleurs : un clic sur un autre bouton ouvre son menu", !MenuReclick(extBtn, extCenter));
            MenuClosed(new ContextMenuStrip(), ToolStripDropDownCloseReason.AppClicked, extCenter);
            await Task.Delay(700);
            C("clic sur le bouton bien après : le menu s'ouvre", !MenuReclick(extBtn, extCenter));
            Fade.LastOutsideClose = DateTime.MinValue;
            Save();

            // 3. Carte d'aperçu : jamais par-dessus un menu ou la recherche d'onglets
            await NewTab("https://example.org/");
            await Task.Delay(1500);
            var fake5 = new ContextMenuStrip();
            MenuOpened(fake5);
            TabHover(0);
            await Task.Delay(800);
            C("menu ouvert : pas de carte d'aperçu au survol d'un onglet", tabCard is not { Visible: true });
            MenuClosed(fake5, ToolStripDropDownCloseReason.Keyboard, Point.Empty);
            ShowTabSearch();
            await Until(() => tabSearch is { Visible: true }, 2000);
            TabHover(0);
            await Task.Delay(800);
            C("recherche d'onglets ouverte : pas de carte d'aperçu", tabSearch is { Visible: true } && tabCard is not { Visible: true });
            tabSearch?.Close();
            await Task.Delay(300);
            TabHover(0);
            await Task.Delay(800);
            C("rien d'ouvert : la carte d'aperçu revient au survol", tabCard is { Visible: true });
            TabHover(-1);

            // 4. Carte d'aperçu sur l'écran de l'onglet
            var screens = Screen.AllScreens;
            bool allOk = true; var det = new List<string>();
            foreach (var s in screens)
            {
                var p = new Point(s.Bounds.Left + s.Bounds.Width - 60, s.Bounds.Top + 40);
                var a = TabCardArea(p);
                allOk &= a == s.WorkingArea;
                det.Add($"{s.DeviceName.Replace("\\\\.\\", "")} → {(a == s.WorkingArea ? "son écran" : "autre écran")}");
            }
            C("carte d'aperçu bornée à l'écran de l'onglet (chaque écran)", allOk, $"{screens.Length} écran(s) : " + string.Join(", ", det));
            var winWa = Screen.FromControl(this).WorkingArea;
            var other = screens.FirstOrDefault(s => s.WorkingArea.Left >= winWa.Right || s.WorkingArea.Right <= winWa.Left); // écran à côté (pas au-dessus)
            if (other != null)
            {
                var p = new Point(other.WorkingArea.Right - S(40), other.WorkingArea.Top + S(40));
                int oldX = Math.Clamp(p.X, Screen.FromControl(this).WorkingArea.Left + S(4), Math.Max(Screen.FromControl(this).WorkingArea.Left + S(4), Screen.FromControl(this).WorkingArea.Right - S(290) - S(4)));
                var wa = TabCardArea(p);
                int newX = Math.Clamp(p.X, wa.Left + S(4), Math.Max(wa.Left + S(4), wa.Right - S(290) - S(4)));
                C("onglet sur un 2e écran : la carte reste sur cet écran (avant : renvoyée sur l'écran de la fenêtre)", other.WorkingArea.Contains(new Point(newX, p.Y)) && !other.WorkingArea.Contains(new Point(oldX, p.Y)),
                    $"onglet à x={p.X}, carte avant x={oldX}, maintenant x={newX}");
            }
            Save();

            // 5. Propositions de la barre d'adresse : refermées au changement d'onglet (même vers l'accueil, barre encore active)
            SelectTab(tabs.IndexOf(pageTab));
            await Task.Delay(300);
            omniTest = true;
            ShowOmni(OmniRows("exam", "exam", Omni.Rank("exam", FavEntries, HistEntries, 6), null, withTabs: true));
            bool omniOn = omni.Visible;
            await NewTab(); // onglet d'accueil : la barre d'adresse prend le focus, sans le quitter
            await Task.Delay(600);
            C("changement d'onglet : les propositions de la barre d'adresse se referment", omniOn && !omni.Visible, $"avant {omniOn}, après {omni.Visible}");
            omniTest = false;
            HideOmni();
            Save();

            // 6. Lens : refermé quand la fenêtre bouge, change de taille ou d'onglet
            SelectTab(tabs.IndexOf(pageTab));
            await Task.Delay(500);
            async Task<bool> OpenLens() { StartLens(); return await Until(() => lens is { Visible: true }, 5000); }
            var home = Location;
            if (await OpenLens())
            {
                await Task.Delay(200);
                Location = new Point(home.X + 30, home.Y + 10);
                await Task.Delay(300);
                C("Lens ouvert puis fenêtre déplacée : Lens se referme (il restait à l'ancienne place)", lens == null);
                Location = home;
            }
            else C("Lens ouvert", false, "pas ouvert");
            var size0 = Size;
            if (await OpenLens())
            {
                await Task.Delay(200);
                Size = new Size(size0.Width - 120, size0.Height - 60);
                await Task.Delay(300);
                C("fenêtre redimensionnée : Lens se referme", lens == null);
                Size = size0;
            }
            if (await OpenLens())
            {
                await Task.Delay(200);
                SelectTab((sel + 1) % tabs.Count);
                await Task.Delay(300);
                C("autre onglet : Lens se referme (l'image figée était l'ancienne page)", lens == null);
            }
            if (await OpenLens())
            {
                await Task.Delay(300);
                C("Lens toujours ouvert quand rien ne bouge", lens is { Visible: true });
                lens?.Close();
            }
            Save();

            // 7. Boîte de saisie : toutes les lignes de l'explication visibles
            const string mosque = "Colle le lien de ta mosquée sur Mawaqit (ex. mawaqit.net/fr/ma-mosquee).\nLaisse vide pour ouvrir Mawaqit et la chercher.";
            using (var f = Prompt.Create("Horaires de ta mosquée", mosque, theme, "Valider", ""))
            {
                f.PerformLayout();
                var lbl = f.Controls.OfType<Label>().First();
                var box = f.Controls.OfType<TextBox>().First();
                var btns = f.Controls.OfType<Button>().ToList();
                int need = PetitsSoucis.NeededHeight(lbl);
                C("constat : l'explication de la mosquée demande plus que les 40 px d'avant (3e ligne coupée)", need > 40, $"{need} px");
                C("boîte de saisie : l'explication tient en entier", lbl.Height >= need, $"étiquette {lbl.Height} px pour {need} px");
                C("la case et les boutons sont dessous, rien ne se chevauche", box.Top >= lbl.Bottom && btns.All(b => b.Top >= box.Bottom && b.Bottom <= f.ClientSize.Height),
                    $"texte jusqu'à {lbl.Bottom}, case {box.Top}–{box.Bottom}, boutons {btns[0].Top}–{btns[0].Bottom}, fenêtre {f.ClientSize.Height}");
                // capture sans afficher la boîte : chaque contrôle dessiné à sa place
                try
                {
                    using var pic = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
                    using (var g = Graphics.FromImage(pic)) g.Clear(f.BackColor);
                    foreach (Control c in f.Controls) c.DrawToBitmap(pic, c.Bounds);
                    pic.Save(Path.Combine(ShotsDir, name + "_7_saisie.png"));
                }
                catch { }
            }
            using (var f = Prompt.Create("Renommer le favori", "Nouveau nom :", theme, "Renommer", "Gentle Mates"))
            {
                f.PerformLayout();
                var box = f.Controls.OfType<TextBox>().First();
                C("saisie d'une ligne : taille d'origine gardée", box.Top == 60 && f.ClientSize.Height == 150, $"case à {box.Top}, hauteur {f.ClientSize.Height}");
            }

            // 8. Texte du menu ☰
            C("menu ☰ : « 1 bloquée », « 12 bloquées »", PetitsSoucis.Blocked(0) == "0 bloquée" && PetitsSoucis.Blocked(1) == "1 bloquée" && PetitsSoucis.Blocked(12) == "12 bloquées",
                $"{PetitsSoucis.Blocked(1)} / {PetitsSoucis.Blocked(12)}");
            C("aucun menu compté ouvert à la fin", menusOpen == 0, $"{menusOpen}");
            await ShotAsync(name + "_fin");
        }
        catch (Exception ex) { C("test interrompu", false, ex.ToString()); }
        Save();
    }
}
