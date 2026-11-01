// Tests de LiteBrowser.  dotnet run --project tests            (hors ligne)
//                        dotnet run --project tests -- --online (+ vrais services en ligne)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LiteCore;

static class Program
{
    static int failures, passed;
    static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");

    static void Check(string name, bool ok, string detail = null)
    {
        if (ok) passed++; else failures++;
        Console.WriteLine((ok ? "  OK   " : "  FAIL ") + name + (ok || detail == null ? "" : "  → " + detail));
    }

    static void Section(string title) => Console.WriteLine("\n== " + title);

    static async Task<int> Main(string[] args)
    {
        bool online = args.Contains("--online");

        Urls();
        Updates();
        Extensions();
        MatchParsing();
        PrayerParsing();
        YouTube();
        if (online) await Online();

        Console.WriteLine($"\n{passed} réussis, {failures} échoués");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    static void Urls()
    {
        Section("Barre d'adresse");
        const string g = "https://www.google.com/search?q=";
        (string In, string Out)[] cases =
        {
            ("youtube.com", "https://youtube.com"),
            ("www.youtube.com/watch?v=abc", "https://www.youtube.com/watch?v=abc"),
            ("https://example.org/a b", "https://example.org/a b"),
            ("localhost:3000", "http://localhost:3000"),
            ("192.168.1.1", "http://192.168.1.1"),
            ("météo paris", g + "m%C3%A9t%C3%A9o%20paris"),
            ("gentle mates", g + "gentle%20mates"),
            ("chatgpt", g + "chatgpt"),
            ("1.5", g + "1.5"),
            ("fr.wikipedia.org", "https://fr.wikipedia.org"),
            ("  amazon.fr  ", "https://amazon.fr"),
            ("", null),
        };
        foreach (var (i, o) in cases)
        {
            var r = UrlTools.ToUrl(i, g);
            Check($"« {i} » → {o ?? "rien"}", r == o, r);
        }
        Check("forme courte", UrlTools.ShortForm("https://www.youtube.com/") == "youtube.com");

        var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "doubleclick.net" };
        Check("bloque un sous-domaine", UrlTools.HostMatches("ad.g.doubleclick.net", list));
        Check("ne bloque pas un faux ami", !UrlTools.HostMatches("notdoubleclick.net", list));
        Check("ne bloque pas youtube", !UrlTools.HostMatches("www.youtube.com", list));
    }

    // ------------------------------------------------------------------
    static void Updates()
    {
        Section("Mises à jour");
        string Rel(string tag) => "{\"tag_name\":\"" + tag + "\",\"assets\":[{\"name\":\"LiteBrowser.exe\",\"browser_download_url\":\"https://x/LiteBrowser.exe\"}]}";
        var cur = new Version(2, 2, 0, 0);
        Check("v3.0.0 > 2.2.0 : mise à jour proposée", VersionTools.NewerRelease(Rel("v3.0.0"), cur)?.Version == new Version(3, 0, 0));
        Check("même version : rien", VersionTools.NewerRelease(Rel("v2.2.0"), cur) == null);
        Check("version plus ancienne : rien", VersionTools.NewerRelease(Rel("v2.1.0"), cur) == null);
        Check("tag invalide « v2 » : ignoré", VersionTools.NewerRelease(Rel("v2"), cur) == null);
        Check("pas d'exe dans la release : rien",
            VersionTools.NewerRelease("{\"tag_name\":\"v9.0.0\",\"assets\":[]}", cur) == null);
    }

    // ------------------------------------------------------------------
    static byte[] MakeCrx(Dictionary<string, string> files, int version = 3)
    {
        var zipMs = new MemoryStream();
        using (var zip = new ZipArchive(zipMs, ZipArchiveMode.Create, true))
            foreach (var (name, content) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
        var header = new byte[40];
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("Cr24"));
        ms.Write(BitConverter.GetBytes(version));
        if (version == 3) { ms.Write(BitConverter.GetBytes(header.Length)); ms.Write(header); }
        else { ms.Write(BitConverter.GetBytes(10)); ms.Write(BitConverter.GetBytes(30)); ms.Write(header); }
        ms.Write(zipMs.ToArray());
        return ms.ToArray();
    }

    static void Extensions()
    {
        Section("Extensions");
        Check("ID depuis un lien du Chrome Web Store",
            Crx.ParseStoreId("https://chromewebstore.google.com/detail/nordpass/eiaeiblijfjekdanodkjadfinkhbfgcd?hl=fr") == "eiaeiblijfjekdanodkjadfinkhbfgcd");
        Check("lien sans ID : rien", Crx.ParseStoreId("https://google.com") == null);

        foreach (var ver in new[] { 3, 2 })
        {
            var folder = Path.Combine(Path.GetTempPath(), "lb-ext-test-" + ver);
            var crx = MakeCrx(new()
            {
                ["manifest.json"] = "{ // commentaire\n\"name\":\"__MSG_extName__\",\"default_locale\":\"en\",\"manifest_version\":3," +
                                    "\"action\":{\"default_popup\":\"/popup.html\"},\"icons\":{\"16\":\"i16.png\",\"32\":\"i32.png\",\"128\":\"i128.png\"},}",
                ["_locales/en/messages.json"] = "{\"extName\":{\"message\":\"My Extension\"}}",
                ["_locales/fr/messages.json"] = "{\"extName\":{\"message\":\"Mon extension\"}}",
                ["_metadata/verified_contents.json"] = "[]",
                ["i32.png"] = "x",
                ["popup.html"] = "<p>hi</p>",
            }, ver);
            Crx.Extract(crx, folder);
            var m = Crx.ReadManifest(folder);
            Check($"CRX{ver} : décompressée", File.Exists(Path.Combine(folder, "popup.html")));
            Check($"CRX{ver} : _metadata supprimé", !Directory.Exists(Path.Combine(folder, "_metadata")));
            Check($"CRX{ver} : nom traduit en français", m.Name == "Mon extension", m.Name);
            Check($"CRX{ver} : page popup trouvée", m.Popup == "popup.html", m.Popup);
            Check($"CRX{ver} : icône 32 px choisie", m.IconPath != null && m.IconPath.EndsWith("i32.png"), m.IconPath);
        }

        bool threw = false;
        try { Crx.ZipOffset(Encoding.ASCII.GetBytes("<html>404 not found</html>")); } catch (InvalidDataException) { threw = true; }
        Check("fichier invalide : erreur claire", threw);
    }

    // ------------------------------------------------------------------
    static void MatchParsing()
    {
        Section("Matchs Gentle Mates (Liquipedia)");
        var html = File.ReadAllText(Path.Combine(Fixtures, "liquipedia_valorant.html"));
        var list = Matches.Parse(html, "VALORANT", "valorant");
        Check("1 match de Gentle Mates trouvé (les autres équipes ignorées)", list.Count == 1, list.Count.ToString());
        Check("l'onglet « terminés » est ignoré", list.All(m => m.Ts < 10_000_000_000));
        if (list.Count > 0)
        {
            var m = list[0];
            Check("équipe M8 reconnue", m.Team1.Contains("Gentle Mates") || m.Team2.Contains("Gentle Mates"), m.Team1 + " / " + m.Team2);
            Check("adversaire trouvé", !string.IsNullOrWhiteSpace(m.Short1) && !string.IsNullOrWhiteSpace(m.Short2), m.Short1 + " vs " + m.Short2);
            Check("horaire trouvé", m.Ts > 1_700_000_000);
            Check("format (Bo3/Bo5…)", m.Format != null && m.Format.StartsWith("Bo"), m.Format);
            Check("tournoi trouvé", !string.IsNullOrWhiteSpace(m.Tournament), m.Tournament);
            Check("lien vers Liquipedia", m.Url != null && m.Url.StartsWith("https://liquipedia.net/valorant/"), m.Url);
            Console.WriteLine($"         {DateTimeOffset.FromUnixTimeSeconds(m.Ts).ToOffset(TimeSpan.FromHours(1)):dd/MM HH:mm}  {m.Short1} vs {m.Short2}  ({m.Format}) – {m.Tournament}");
        }
        Check("page vide : aucun plantage", Matches.Parse("", "X", "x").Count == 0);

        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var cleaned = Matches.Clean(new[]
        {
            new Match { Game = "A", Ts = 1_800_003_600, Team1 = "Gentle Mates", Team2 = "B" },
            new Match { Game = "A", Ts = 1_800_003_600, Team1 = "Gentle Mates", Team2 = "B" },   // doublon
            new Match { Game = "A", Ts = 1_799_990_000, Team1 = "Gentle Mates", Team2 = "C" },   // en cours
            new Match { Game = "A", Ts = 1_799_900_000, Team1 = "Gentle Mates", Team2 = "D" },   // fini depuis longtemps
        }, now);
        Check("doublons et vieux matchs retirés, tri par date", cleaned.Count == 2 && cleaned[0].Team2 == "C");
    }

    // ------------------------------------------------------------------
    static void PrayerParsing()
    {
        Section("Horaires de prière");
        var day = Prayers.Parse(File.ReadAllText(Path.Combine(Fixtures, "aladhan.json")), new DateTime(2026, 11, 1), "Creutzwald");
        Check("6 horaires lus", day.Times.Count == 6, day.Times.Count.ToString());
        Check("Fajr 06:10", day.Times.First(t => t.Key == "Fajr").Time == new TimeSpan(6, 10, 0));
        Check("Isha 18:23", day.Times.First(t => t.Key == "Isha").Time == new TimeSpan(18, 23, 0));
        Check("date hégirienne en français", day.Hijri == "21 Joumada al-Oula 1448", day.Hijri);

        var n1 = Prayers.Next(day, new DateTime(2026, 11, 1, 13, 0, 0));
        Check("13h00 → prochaine = Asr", n1.Name == "Asr", n1.Name);
        var n2 = Prayers.Next(day, new DateTime(2026, 11, 1, 6, 30, 0));
        Check("6h30 → Dhuhr (le lever du soleil ne compte pas)", n2.Name == "Dhuhr", n2.Name);
        var n3 = Prayers.Next(day, new DateTime(2026, 11, 1, 22, 0, 0));
        Check("22h00 → Fajr du lendemain", n3.Name == "Fajr" && n3.At.Day == 2, n3.Name + " " + n3.At);

        var withTz = "{\"data\":{\"timings\":{\"Fajr\":\"06:10 (CET)\",\"Sunrise\":\"07:21 (CET)\",\"Dhuhr\":\"12:17 (CET)\",\"Asr\":\"14:47 (CET)\",\"Maghrib\":\"17:12 (CET)\",\"Isha\":\"18:23 (CET)\"}}}";
        Check("format « 06:10 (CET) » accepté", Prayers.Parse(withTz, DateTime.Today, "x").Times.Count == 6);
        Check("adresse API correcte (point décimal)",
            Prayers.ApiUrl(new DateTime(2026, 11, 1), 49.205, 6.696, 12) ==
            "https://api.aladhan.com/v1/timings/01-11-2026?latitude=49.205&longitude=6.696&method=12");
    }

    // ------------------------------------------------------------------
    static void YouTube()
    {
        Section("Bloqueur de pubs YouTube (Node.js)");
        var node = FindNode();
        if (node == null) { Console.WriteLine("  (Node.js absent, test ignoré)"); return; }
        var scriptFile = Path.Combine(Path.GetTempPath(), "lb_yt_adblock.js");
        File.WriteAllText(scriptFile, YouTubeAdblock.Script);
        var psi = new ProcessStartInfo(node, $"\"{Path.Combine(AppContext.BaseDirectory, "youtube_test.js")}\" \"{scriptFile}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi);
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        foreach (var line in output.Split('\n').Where(l => l.Trim().Length > 0)) Console.WriteLine(line.StartsWith("  ") ? line : "         " + line);
        Check("script YouTube validé", p.ExitCode == 0);
    }

    static string FindNode()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in new[] { "node", "node.exe" })
            {
                var f = Path.Combine(dir, name);
                if (File.Exists(f)) return f;
            }
        return null;
    }

    // ------------------------------------------------------------------
    static async Task Online()
    {
        Section("En ligne : extensions réelles");
        foreach (var (name, id) in new[] { ("NordPass", "eiaeiblijfjekdanodkjadfinkhbfgcd"), ("TrendTrack", "cncdmciajmaojninbopinpijdodjgfpd") })
        {
            try
            {
                var folder = Path.Combine(Path.GetTempPath(), "lb-real-" + id);
                await Crx.DownloadAsync(id, folder);
                var m = Crx.ReadManifest(folder);
                Check($"{name} téléchargée et lisible ({m.Name})", m.Name != null && !m.Name.StartsWith("__MSG_"), m.Name);
                Check($"{name} : popup = {m.Popup}", m.Popup != null && File.Exists(Path.Combine(folder, m.Popup)));
            }
            catch (Exception e) { Check($"{name} téléchargée", false, e.Message); }
        }

        Section("En ligne : prières");
        try
        {
            var geo = await Prayers.GeocodeAsync("Creutzwald");
            Check("ville trouvée : " + geo?.City, geo != null && Math.Abs(geo.Value.Lat - 49.2) < 0.2, geo?.ToString());
            var day = await Prayers.FetchAsync(DateTime.Today, geo.Value.Lat, geo.Value.Lon, 12, geo.Value.City);
            Check("horaires du jour : " + string.Join(" ", day.Times.Select(t => $"{t.Key} {t.Time:hh\\:mm}")), day.Times.Count == 6);
        }
        catch (Exception e) { Check("prières en ligne", false, e.Message); }
        try
        {
            var ip = await Prayers.LocateByIpAsync();
            Check("localisation par IP : " + ip?.City, ip != null);
        }
        catch (Exception e) { Check("localisation par IP", false, e.Message); }

        Section("En ligne : matchs (Liquipedia, VALORANT)");
        try
        {
            using var http = Net.Client(TimeSpan.FromSeconds(30));
            var json = await http.GetStringAsync(Matches.ApiUrl("valorant"));
            var list = Matches.Parse(Matches.HtmlFromApi(json), "VALORANT", "valorant");
            Check($"page lue, {list.Count} match(s) M8 à venir", true);
            foreach (var m in list)
                Console.WriteLine($"         {DateTimeOffset.FromUnixTimeSeconds(m.Ts).ToOffset(TimeSpan.FromHours(1)):dd/MM HH:mm}  {m.Short1} vs {m.Short2}  ({m.Format}) – {m.Tournament}");
        }
        catch (Exception e) { Check("Liquipedia", false, e.Message); }

        Section("En ligne : mises à jour GitHub");
        try
        {
            using var http = Net.Client(TimeSpan.FromSeconds(15));
            var json = await http.GetStringAsync("https://api.github.com/repos/Atlantaas/LiteBrowser/releases/latest");
            var r = VersionTools.NewerRelease(json, new Version(0, 0, 1));
            Check("dernière release trouvée : v" + r?.Version, r != null && r.Value.Url.EndsWith(".exe"));
        }
        catch (Exception e) { Check("GitHub", false, e.Message); }
    }
}
