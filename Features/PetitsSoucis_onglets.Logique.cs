// Logique pure des petits soucis « onglets et fenêtres » (sans WinForms) : testée par tests/Tests.csproj.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

namespace LiteCore
{
    /// Taille et position de la fenêtre principale, retenues d'une fois sur l'autre (comme Chrome et Brave).
    public static class WindowPlace
    {
        public static string Format(Rectangle r, bool max) =>
            string.Join(";", r.X, r.Y, r.Width, r.Height, max ? 1 : 0);

        public static bool TryParse(string text, out Rectangle r, out bool max)
        {
            r = Rectangle.Empty; max = false;
            var p = (text ?? "").Trim().Split(';');
            if (p.Length != 5) return false;
            var v = new int[5];
            for (int i = 0; i < 5; i++)
                if (!int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return false;
            if (v[2] < 100 || v[3] < 100) return false;
            r = new Rectangle(v[0], v[1], v[2], v[3]);
            max = v[4] == 1;
            return true;
        }

        /// Ramène la fenêtre sur un écran qui existe encore : l'écran qui en montre le plus, taille ramenée à cet écran,
        /// entièrement visible. Écran débranché ou fenêtre presque invisible : centrée sur l'écran principal.
        public static Rectangle Fit(Rectangle saved, IReadOnlyList<Rectangle> screens, Rectangle primary, Size min)
        {
            Rectangle best = primary;
            long bestArea = 0;
            foreach (var s in screens)
            {
                var i = Rectangle.Intersect(saved, s);
                long a = (long)Math.Max(0, i.Width) * Math.Max(0, i.Height);
                if (a > bestArea) { bestArea = a; best = s; }
            }
            // la barre de titre doit pouvoir être attrapée : au moins 120 × 40 px visibles
            bool lost = bestArea < 120L * 40;
            if (lost) best = primary;
            int w = Math.Clamp(saved.Width, Math.Min(min.Width, best.Width), best.Width);
            int h = Math.Clamp(saved.Height, Math.Min(min.Height, best.Height), best.Height);
            int x = lost ? best.X + (best.Width - w) / 2 : Math.Clamp(saved.X, best.Left, best.Right - w);
            int y = lost ? best.Y + (best.Height - h) / 2 : Math.Clamp(saved.Y, best.Top, best.Bottom - h);
            return new Rectangle(x, y, w, h);
        }
    }

    /// Places dans la barre d'onglets.
    public static class TabPlace
    {
        /// Onglet lâché à l'abscisse x : il se glisse avant le premier onglet dont le milieu est à droite du point.
        public static int DropIndex(IReadOnlyList<(int Left, int Width)> tabs, int x)
        {
            for (int i = 0; i < tabs.Count; i++)
                if (x < tabs[i].Left + tabs[i].Width / 2) return i;
            return tabs.Count;
        }

        /// Les onglets épinglés restent groupés à gauche : un épinglé va dans leur groupe, un autre après eux.
        public static int InGroup(int at, bool pinned, int pinnedCount, int max) =>
            pinned ? Math.Clamp(at, 0, Math.Min(pinnedCount, max)) : Math.Clamp(at, Math.Min(pinnedCount, max), max);
    }
}
