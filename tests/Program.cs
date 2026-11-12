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
        SessionAndSites();
        Extensions();
        M8Tests();
        ChannelTests();
        SearchTests();
        OmniTests();
        NurLiveTests();
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
        string Rel(string tag) => "{\"tag_name\":\"" + tag + "\",\"assets\":[{\"name\":\"LiteBrowser.exe\",\"browser_download_url\":\"https://github.com/Atlantaas/LiteBrowser/releases/download/" + tag + "/LiteBrowser.exe\"}]}";
        var cur = new Version(2, 2, 0, 0);
        Check("v3.0.0 > 2.2.0 : mise à jour proposée", VersionTools.NewerRelease(Rel("v3.0.0"), cur)?.Version == new Version(3, 0, 0));
        Check("même version : rien", VersionTools.NewerRelease(Rel("v2.2.0"), cur) == null);
        Check("version plus ancienne : rien", VersionTools.NewerRelease(Rel("v2.1.0"), cur) == null);
        Check("tag invalide « v2 » : ignoré", VersionTools.NewerRelease(Rel("v2"), cur) == null);
        Check("pas d'exe dans la release : rien",
            VersionTools.NewerRelease("{\"tag_name\":\"v9.0.0\",\"assets\":[]}", cur) == null);
        Check("exe hébergé ailleurs que sur GitHub : refusé",
            VersionTools.NewerRelease("{\"tag_name\":\"v9.0.0\",\"assets\":[{\"name\":\"LiteBrowser.exe\",\"browser_download_url\":\"https://evil.example/LiteBrowser.exe\"}]}", cur) == null);
        Check("exe en http (non chiffré) : refusé", !VersionTools.IsTrustedDownload("http://github.com/a/b.exe"));
        Check("faux domaine github.com.evil : refusé", !VersionTools.IsTrustedDownload("https://github.com.evil.net/a.exe"));
        Check("stockage GitHub accepté", VersionTools.IsTrustedDownload("https://objects.githubusercontent.com/x/LiteBrowser.exe"));
    }

    // ------------------------------------------------------------------
    static void SessionAndSites()
    {
        Section("Session (onglets rouverts au démarrage)");
        var tabs = new[]
        {
            new SessionTab("https://www.youtube.com/watch?v=1", "Vidéo\tavec tab", true),
            new SessionTab("", "Nouvel onglet", false),
            new SessionTab("https://liquipedia.net/valorant/Gentle_Mates", "M8", false),
        };
        var (back, sel) = Session.Parse(Session.Serialize(tabs, 2));
        Check("3 onglets relus", back.Count == 3);
        Check("onglet épinglé conservé", back[0].Pinned && !back[2].Pinned);
        Check("onglet sélectionné conservé", sel == 2);
        Check("titre nettoyé (pas de tabulation)", back[0].Title == "Vidéo avec tab");
        Check("page d'accueil = adresse vide", back[1].Url == "");
        var (bad, _) = Session.Parse("selected=9\n0\tjavascript:alert(1)\tx\n0\tfile:///C:/secret.txt\tx\n0\thttps://ok.fr\tok\n");
        Check("adresses javascript: et file: ignorées", bad.Count == 1 && bad[0].Url == "https://ok.fr");
        Check("sélection hors limites ramenée", Session.Parse("selected=9\n0\thttps://a.fr\ta\n").Selected == 0);
        Check("fichier vide : aucune session", Session.Parse("").Tabs.Count == 0);

        Section("Sites sans bloqueur");
        var set = SiteList.Parse("Twitch.tv, lequipe.fr;;  pasunsite");
        Check("liste lue (minuscules, éléments invalides ignorés)", set.Count == 2 && set.Contains("twitch.tv"));
        Check("sous-domaine couvert", SiteList.Contains(set, "https://www.twitch.tv/gentlemates"));
        Check("autre site non couvert", !SiteList.Contains(set, "https://www.youtube.com/"));
        Check("hôte sans www.", SiteList.Host("https://www.LEQUIPE.fr/a") == "lequipe.fr");
        Check("page interne ignorée", SiteList.Host("chrome-extension://abc/popup.html") == null);
        Check("liste réécrite triée", SiteList.Format(set) == "lequipe.fr,twitch.tv");
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
        Check("lien du store Edge reconnu", Crx.ParseStoreId("https://microsoftedge.microsoft.com/addons/detail/coupert-automatic-coupo/pefhciejnkgdgoahgfeklebcbpmhnhhd?utm_source=x") == "pefhciejnkgdgoahgfeklebcbpmhnhhd");

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
    static void M8Tests()
    {
        Section("Matchs Gentle Mates – mêmes résultats que Nur Live (7 pages réelles du 01/11/2026)");
        const long now = 1793550720000; // 01/11/2026 16:32 UTC, heure de récupération des pages
        var all = new List<M8Match>();
        var tours = new List<M8Tournament>();
        foreach (var p in M8.Pages)
        {
            var html = File.ReadAllText(Path.Combine(Fixtures, "m8", p.Game + ".html"));
            all.AddRange(M8.ParseTeamPage(html, p.Game, p.Wiki, p.Page, now));
            tours.AddRange(M8.ParseUpcomingTournaments(html, p.Game, p.Wiki));
        }
        // Référence : sortie du parseur JavaScript de Nur Live sur les mêmes pages
        Check("1 match trouvé (comme Nur Live)", all.Count == 1, all.Count.ToString());
        if (all.Count == 1)
        {
            var m = all[0];
            Check("id identique", m.Id == "valorantgc:1793556000:krblaze", m.Id);
            Check("heure identique", m.Ts == 1793556000);
            Check("statut « à venir »", m.Status == "upcoming", m.Status);
            Check("adversaire KRÜ Blaze / KRÜ.B", m.Opponent == "KRÜ Blaze" && m.OpponentShort == "KRÜ.B", m.Opponent + " / " + m.OpponentShort);
            Check("tournoi + phase (GF)", m.Tournament == "Game Changers Championship 2026" && m.Stage == "GF", m.Tournament + " / " + m.Stage);
            Check("aucun score, aucun stream", m.Score == "" && m.Streams.Count == 0);
            Check("lien Liquipedia", m.Url == "https://liquipedia.net/valorant/Gentle_Mates_GC", m.Url);
        }
        Check("2 tournois à venir (comme Nur Live)",
            tours.Count == 2 && tours[0].Name == "VCT EMEA Kickoff" && tours[0].When == "Jan – Feb" &&
            tours[1].Name == "Game Changers Championship 2026" && tours[1].When == "Oct 22 – Nov 01",
            string.Join(" ; ", tours.Select(t => $"{t.Game}:{t.Name}:{t.When}")));
        Check("hors période (2 mois avant) : aucun match",
            M8.ParseTeamPage(File.ReadAllText(Path.Combine(Fixtures, "m8", "valorantgc.html")), "valorantgc", "valorant", "Gentle_Mates_GC", 1780000000000).Count == 0);

        Section("Matchs – cas repris des tests de Nur Live");
        var synthRow = "<table><tr class=\"table2&#95;&#95;row--body\"><td data-sort-value=\"2000000000\"><span class=\"timer-object\" data-timestamp=\"2000000000\">x</span></td><td><a href=\"/valorant/S-Tier_Tournaments\" title=\"S-Tier Tournaments\">S-Tier</a></td><td><a href=\"/valorant/VCT/2033\" title=\"VCT/2033\">VCT 2033 Masters</a></td><td></td><td>vs</td><td data-sort-value=\"Team Heretics\"><div class=\"block-team\"><span class=\"name\"><a href=\"/valorant/Team_Heretics\" title=\"Team Heretics\">TH</a></span></div></td><td><a href=\"/valorant/Special:Stream/twitch/Valorant_EMEA\">tw</a></td></tr></table>";
        var sr = M8.ParseTeamPage(synthRow, "valorant", "valorant", null, 1999990000L * 1000);
        Check("ligne de tableau : 1 match", sr.Count == 1);
        if (sr.Count == 1)
        {
            Check("à venir", sr[0].Status == "upcoming");
            Check("adversaire Team Heretics", sr[0].Opponent == "Team Heretics", sr[0].Opponent);
            Check("tournoi VCT 2033 Masters", sr[0].Tournament == "VCT 2033 Masters", sr[0].Tournament);
            Check("stream twitch/valorant_emea", sr[0].Streams.Count == 1 && sr[0].Streams[0].Key == "valorant_emea");
        }
        Check("commencé il y a 30 min → en cours", M8.ParseTeamPage(synthRow, "valorant", "valorant", null, (2000000000L + 1800) * 1000)[0].Status == "live");
        var legacy = "<table class=\"infobox_matches_content\"><tr><td class=\"team-left\"><span><a href=\"/valorant/Gentle_Mates\" title=\"Gentle Mates\">M8</a></span></td><td class=\"match-filler\">vs<span class=\"timer-object\" data-timestamp=\"2000000000\"></span></td><td class=\"team-right\"><a href=\"/valorant/Fnatic\" title=\"Fnatic\">FNC</a></td></tr><tr><td colspan=\"3\"><div class=\"tournament-text\"><a href=\"/valorant/X\" title=\"X\">Masters Test</a></div></td></tr></table>";
        Check("ancien format d'encart : 1 match", M8.ParseTeamPage(legacy, "valorant", "valorant", null, 1999990000L * 1000).Count == 1);

        Section("Alertes de match");
        var games = new Dictionary<string, bool> { ["valorant"] = true, ["valorantgc"] = true };
        var fired = new HashSet<string>();
        var due = M8.DueAlerts(sr, true, 15, games, null, fired, (2000000000L - 600) * 1000);
        Check("10 min avant : rappel", due.Select(d => d.Kind).SequenceEqual(new[] { "reminder" }));
        foreach (var d in due) fired.Add(d.Key);
        Check("pas de doublon", M8.DueAlerts(sr, true, 15, games, null, fired, (2000000000L - 300) * 1000).Count == 0);
        due = M8.DueAlerts(sr, true, 15, games, null, fired, (2000000000L + 60) * 1000);
        Check("coup d'envoi", due.Select(d => d.Kind).SequenceEqual(new[] { "start" }));
        Check("match en sourdine", M8.DueAlerts(sr, true, 15, games, new HashSet<string> { sr[0].Id }, new HashSet<string>(), (2000000000L + 60) * 1000).Count == 0);
        Check("jeu désactivé", M8.DueAlerts(sr, true, 15, new Dictionary<string, bool> { ["valorant"] = false }, null, new HashSet<string>(), (2000000000L + 60) * 1000).Count == 0);
        Check("trop tard (1 h après)", M8.DueAlerts(sr, true, 15, games, null, new HashSet<string>(), (2000000000L + 3600) * 1000).Count == 0);
        Check("alertes coupées", M8.DueAlerts(sr, false, 15, games, null, new HashSet<string>(), (2000000000L + 60) * 1000).Count == 0);

        Section("« Regarder » : chaînes choisies");
        Check("streams du match + gentlemates", M8.WatchSources(sr[0]).Select(s => s.Key).SequenceEqual(new[] { "valorant_emea", "gentlemates" }));
        Check("sinon diffuseur officiel + gentlemates",
            M8.WatchSources(new M8Match { Game = "rocketleague" }).Select(s => s.Key).SequenceEqual(new[] { "rocketleague", "gentlemates" }));
        var st = new Dictionary<string, LiveInfo>
        {
            ["twitch:squeezie"] = new() { Live = true, Title = "M8 en finale !", Game = "VALORANT" },
            ["twitch:gentlemates"] = new() { Live = false },
            ["twitch:gotaga"] = new() { Live = true, Title = "Watchparty Gentle Mates", Game = "Call of Duty" },
        };
        Check("co-stream en live choisi (squeezie)",
            LiveStatus.PickStreams("valorant", new[] { "valorant_emea" }, M8.CoStreams["valorant"], new[] { "gotaga" }, st).SequenceEqual(new[] { "squeezie" }));
        Check("aucun co-stream → une chaîne suivie qui parle de M8 sur ce jeu",
            LiveStatus.PickStreams("callofduty", new[] { "callofduty" }, "brawks", new[] { "gotaga" }, st).SequenceEqual(new[] { "gotaga" }));
        Check("personne en live → diffusion officielle",
            LiveStatus.PickStreams("rocketleague", new[] { "rocketleague" }, "gentlemates", new string[0], st).SequenceEqual(new[] { "rocketleague" }));

        Section("Cache Nur Live et récupération");
        var cache = Path.Combine(Path.GetTempPath(), "lb-nl-cache.json");
        File.WriteAllText(cache, "{\"matches\":[{\"id\":\"valorant:x\",\"game\":\"valorant\",\"ts\":2000000000,\"opponent\":\"Fnatic\",\"opponentShort\":\"FNC\",\"tournament\":\"T\",\"stage\":\"\",\"status\":\"upcoming\",\"score\":\"\",\"url\":\"u\",\"streams\":[{\"platform\":\"twitch\",\"key\":\"valorant_emea\"}]}],\"tournaments\":[],\"events\":[],\"fetchedAt\":1999999000000}");
        var c = M8.ReadNurLiveCache(cache);
        Check("cache Nur Live relu", c != null && c.Value.Matches.Count == 1 && c.Value.Matches[0].Streams[0].Key == "valorant_emea" && c.Value.FetchedAt == 1999999000000);

        var svcCache = Path.Combine(Path.GetTempPath(), "lb-m8-own.json");
        File.Delete(svcCache);
        int calls = 0;
        var svc = new M8Service(svcCache) { GapMs = 0 };
        svc.Fetch = _ => { calls++; return Task.FromResult(new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)429)); };
        svc.RefreshAsync().Wait();
        Check("429 de Liquipedia : arrêt au 1er appel, rien de cassé", calls == 1 && svc.Matches.Count == 0 && !svc.Loading, "appels : " + calls);

        int pageIdx = 0;
        svc.Fetch = u =>
        {
            if (!u.Contains("action=parse")) return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            var p = M8.Pages[pageIdx++];
            var html = File.ReadAllText(Path.Combine(Fixtures, "m8", p.Game + ".html"));
            var json = System.Text.Json.JsonSerializer.Serialize(new { parse = new Dictionary<string, object> { ["text"] = new Dictionary<string, string> { ["*"] = html } } });
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json) });
        };
        svc.RefreshAsync().Wait();
        Check("7 pages lues et cache enregistré", pageIdx == 7 && File.Exists(svcCache) && svc.Tournaments.Count == 2, $"pages {pageIdx}, tournois {svc.Tournaments.Count}");
        var again = new M8Service(svcCache);
        Check("cache relu au démarrage suivant", again.Tournaments.Count == 2 && again.FetchedAtMs > 0);

        // ---------- Tournois hors page d'équipe (Coupe de France Slash 2026, page réelle) ----------
        Section("M8 : tournois hors page d'équipe");
        Section("M8 : affiche publiée sans horaire (Monster Launch Invitational, page réelle du 04/11/2026)");
        const string cdlTitle = "Call of Duty League/Season 8/Monster Launch Invitational";
        var cdl = File.ReadAllText(Path.Combine(Fixtures, "m8", "cdl_mli_sans_horaires.html"));
        long cdlNow = new DateTimeOffset(2026, 11, 4, 20, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var mli = M8.ParseTournamentPage(cdl, "callofduty", "callofduty", cdlTitle, cdlNow);
        Check("M8 vs Cloud9 New York trouvé", mli.Count == 1 && mli[0].Opponent == "Cloud9 New York", mli.Count + " match(s)");
        long at20h = new DateTimeOffset(2026, 11, 6, 19, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Check("vendredi 20 h à Paris, marqué à confirmer", mli.Count == 1 && !mli[0].TimeUnknown && mli[0].Ts == at20h && mli[0].Stage == "Groupe D · heure à confirmer" && mli[0].Tournament == "Monster Launch Invitational");
        Check("alerte 15 min avant", M8.DueAlerts(mli, true, 15, null, new HashSet<string>(), new HashSet<string>(), (at20h - 600) * 1000).Count == 1);
        var mliDuring = M8.ParseTournamentPage(cdl, "callofduty", "callofduty", cdlTitle, new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        Check("sans heure connue : date du jour pendant le tournoi", mliDuring.Count == 1 && mliDuring[0].TimeUnknown && mliDuring[0].Stage == "Groupe D" && M8.DateFr(mliDuring[0].Ts) == "sam. 7 nov.");
        Check("sans heure connue : aucune alerte", M8.DueAlerts(mliDuring, true, 15, null, new HashSet<string>(), new HashSet<string>(), mliDuring[0].Ts * 1000).Count == 0);
        Check("sans heure connue : reste « à venir »", M8.RefreshStatus(mliDuring, (mliDuring[0].Ts + 3600) * 1000)[0].Status == "upcoming");
        Check("après le tournoi : plus affiché", M8.ParseTournamentPage(cdl, "callofduty", "callofduty", cdlTitle, new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()).Count == 0);
        var nlTours = M8.UndatedAsTournaments(mliDuring);
        Check("Nur Live : sans heure, présenté comme tournoi à venir", nlTours.Count == 1 && nlTours[0].Name.Contains("M8 vs " + mliDuring[0].OpponentShort) && nlTours[0].When.Contains("horaire à venir"), nlTours.Count == 1 ? nlTours[0].Name : "");
        Check("Nur Live : avec heure, match normal", M8.UndatedAsTournaments(mli).Count == 0);
        M8Page PgOf(string g) => M8.Pages.First(p => p.Game == g);
        var fair = M8.FairOrder(new[] {
            (PgOf("rocketleague"), "RL1", false), (PgOf("rocketleague"), "RL2", false), (PgOf("rocketleague"), "RL3", false),
            (PgOf("rocketleague"), "connu", true), (PgOf("callofduty"), "CDL", false) });
        Check("lecture équitable : connu d'abord, puis CoD avant le 2e RL", fair[0].Item2 == "connu" && fair.FindIndex(x => x.Item2 == "CDL") < fair.FindIndex(x => x.Item2 == "RL2"), string.Join(",", fair.Select(x => x.Item2)));
        var seasonJson = "{\"query\":{\"pages\":{\"1\":{\"title\":\"" + cdlTitle + "\",\"revisions\":[{\"timestamp\":\"2026-11-03T20:31:05Z\"}]},\"2\":{\"title\":\"Call of Duty League/Season 8\",\"revisions\":[{\"timestamp\":\"2026-11-03T20:31:05Z\"}]},\"3\":{\"title\":\"Some Old Page\",\"revisions\":[{\"timestamp\":\"2026-11-03T20:31:05Z\"}]}}}}";
        var seasonCands = M8.TournamentCandidates(seasonJson, new DateTime(2026, 11, 4, 20, 0, 0, DateTimeKind.Utc));
        Check("page de ligue par saison (sans année) retenue", seasonCands.Contains(cdlTitle) && !seasonCands.Contains("Some Old Page"), string.Join(", ", seasonCands));

        var cdf = File.ReadAllText(Path.Combine(Fixtures, "m8", "rl_cdf_slash_2026.html"));
        long cdfNow = DateTimeOffset.Parse("2026-11-01T19:00:00Z").ToUnixTimeMilliseconds();
        var cm = M8.ParseTournamentPage(cdf, "rocketleague", "rocketleague", "NEO/Coupe de France Slash/2026", cdfNow);
        Check("Coupe de France : 10 matchs de M8 (et aucun autre)", cm.Count == 10 && cm.All(m => m.Opponent != "Gentle Mates"), cm.Count.ToString());
        var first = cm.FirstOrDefault();
        Check("1er match : lundi 2/11 19:30 contre Karmine Corp (KC)", first != null && first.Ts == 1793644200 && first.Opponent == "Karmine Corp" && first.OpponentShort == "KC",
            first == null ? "" : $"{first.Ts} {first.Opponent}");
        Check("nom du tournoi lisible", first?.Tournament == "Coupe de France Slash 2026", first?.Tournament);
        Check("phase en français (Jour 1)", first?.Stage == "Jour 1", first?.Stage);
        Check("lien vers la page du tournoi", first?.Url == "https://liquipedia.net/rocketleague/NEO/Coupe_de_France_Slash/2026", first?.Url);
        Check("« Regarder » : stream Slash (slash_ftv) trouvé", first != null && first.Streams.Count(x => x.Platform == "twitch") == 1 && first.Streams.Any(x => x.Key == "slash_ftv"),
            first == null ? "" : string.Join(",", first.Streams.Select(x => x.Platform + ":" + x.Key)));
        Check("phases traduites", M8.StageFr("Semifinals") == "Demi-finales" && M8.StageFr("Grand Final") == "Finale" && M8.StageFr("Week 3") == "Semaine 3");

        var bl = File.ReadAllText(Path.Combine(Fixtures, "m8", "backlinks_rocketleague.json"));
        var cands = M8.TournamentCandidates(bl, new DateTime(2026, 11, 1, 19, 5, 0, DateTimeKind.Utc));
        Check("pages liées à M8 : Coupe de France en tête", cands.Count > 0 && cands[0] == "NEO/Coupe de France Slash/2026", cands.FirstOrDefault());
        Check("transferts / rumeurs / vieux tournois écartés", !cands.Any(c => c.Contains("Transfers") || c.Contains("Rumours") || c.Contains("2024")));

        var merged = M8Service.Merge(new[] { new M8Match { Id = "rocketleague:ID_1", Game = "rocketleague", Ts = 1793644200, Opponent = "Karmine Corp", OpponentShort = "KC" } }, cm);
        Check("fusion sans doublon avec la page d'équipe", merged.Count == 10, merged.Count.ToString());

        // service complet : page d'équipe vide + liens + page du tournoi
        var tCache = Path.Combine(Path.GetTempPath(), "lb-m8-tour-" + Guid.NewGuid().ToString("N") + ".json");
        var tsvc = new M8Service(tCache) { GapMs = 0, QueryGapMs = 0 };
        var urls = new List<string>();
        string Wrap(string html) => System.Text.Json.JsonSerializer.Serialize(new { parse = new Dictionary<string, object> { ["text"] = new Dictionary<string, string> { ["*"] = html } } });
        var nowIso = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var blNow = "{\"query\":{\"pages\":{\"1\":{\"title\":\"NEO/Coupe de France Slash/" + DateTime.UtcNow.Year + "\",\"revisions\":[{\"timestamp\":\"" + nowIso + "\"}]}}}}";
        // la page du tournoi, décalée pour que les matchs soient à venir
        long shift = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400 - 1793644200;
        var cdfNowHtml = System.Text.RegularExpressions.Regex.Replace(cdf, "data-timestamp=\"(\\d+)\"", m => $"data-timestamp=\"{long.Parse(m.Groups[1].Value) + shift}\"");
        tsvc.Fetch = u =>
        {
            urls.Add(u);
            string body = u.Contains("generator=backlinks") ? (u.Contains("/rocketleague/") ? blNow : "{}")
                : u.Contains("Coupe_de_France") ? Wrap(cdfNowHtml) : Wrap("<p>rien</p>");
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(body) });
        };
        tsvc.RefreshAsync().Wait();
        var cur = tsvc.Current(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Check("service : matchs de la Coupe de France affichés", cur.Count(m => m.Tournament.StartsWith("Coupe de France")) == 10, cur.Count.ToString());
        Check("service : 7 recherches de liens + 1 page de tournoi lue", urls.Count(u => u.Contains("backlinks")) == 7 && urls.Count(u => u.Contains("Coupe_de_France")) == 1,
            string.Join(" ", urls.Select(u => u.Contains("backlinks") ? "L" : u.Contains("Coupe") ? "T" : "P")));
        var tagain = new M8Service(tCache);
        Check("service : matchs du tournoi gardés au redémarrage", tagain.Current(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Count == 10);
        urls.Clear();
        tsvc.RefreshAsync().Wait();
        Check("service : tournoi déjà lu → pas relu avant 30 min", urls.Count(u => u.Contains("Coupe_de_France")) == 0);
    }

    // ------------------------------------------------------------------
    static void NurLiveTests()
    {
        Section("Lecteurs de Nur Live (surveillance des pubs figées)");
        Check("lecteur Twitch reconnu", NurLive.PlayerKind("https://player.twitch.tv/?channel=Gotaga&parent=nurlive.home") == "twitch");
        Check("chaîne Twitch lue en minuscules", NurLive.ChannelOf("twitch", "https://player.twitch.tv/?parent=a&channel=Gotaga") == "twitch:gotaga");
        Check("chat Kick ignoré, lecteur Kick reconnu", NurLive.PlayerKind("https://player.kick.com/xqc/chatroom") == "" && NurLive.ChannelOf("kick", "https://player.kick.com/XQC?autoplay=true") == "kick:xqc");
        Check("lecteur YouTube intégré reconnu", NurLive.PlayerKind("https://www.youtube.com/embed/abc") == "youtube");
        Check("page ordinaire ignorée", NurLive.PlayerKind("https://www.twitch.tv/gotaga") == "" && NurLive.PlayerKind("pas une adresse") == "");
        Check("sonde : relance demandée ou non", NurLive.ProbeScript(true).Contains("const RESUME = true") && NurLive.ProbeScript(false).Contains("const RESUME = false"));
        Check("sonde : le mot Publicité d'un stream sponsorisé n'est pas une pub", !NurLive.ProbeScript(false).Contains("Publicit[ée]") && NurLive.ProbeScript(false).Contains("video-ad-label"));
        Section("Intégration de Nur Live");
        var root = Path.Combine(Path.GetTempPath(), "lb-nurlive-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var src = Path.Combine(root, "nur-live");
        Directory.CreateDirectory(Path.Combine(src, "ui", "js"));
        Directory.CreateDirectory(Path.Combine(src, "ui", "assets"));
        Directory.CreateDirectory(Path.Combine(src, "core"));
        File.WriteAllText(Path.Combine(src, "ui", "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(src, "ui", "js", "app.js"), "import x from './util.js';\nconst PARENTS = [\"127.0.0.1\", \"localhost\"];\nconsole.log(PARENTS);");
        File.WriteAllText(Path.Combine(src, "ui", "assets", "logo.png"), "png");
        File.WriteAllText(Path.Combine(src, "core", "model.js"), "module.exports = { reduce: (s) => s };");
        File.WriteAllText(Path.Combine(src, "core", "sources.js"), "module.exports = { sourceId: (s) => s.platform + ':' + s.key };");

        Check("dossier Nur Live reconnu", NurLive.FindFolder(new[] { Path.Combine(root, "absent"), src }) == src);
        Check("dossier incomplet ignoré", NurLive.FindFolder(new[] { Path.Combine(root) }) == null);
        Check("cherché sur le Bureau, dans Documents et OneDrive", NurLive.Candidates(null).Any(c => c.EndsWith(Path.Combine("Desktop", "nur-live"))) &&
                                                                  NurLive.Candidates(null).Any(c => c.Contains("OneDrive")));
        var dst = Path.Combine(root, "copie");
        NurLive.Prepare(src, dst);
        var app = File.ReadAllText(Path.Combine(dst, "js", "app.js"));
        Check("interface copiée (avec les images)", File.Exists(Path.Combine(dst, "index.html")) && File.Exists(Path.Combine(dst, "assets", "logo.png")));
        Check("lecteurs Twitch autorisés sur la page du navigateur", app.Contains("const PARENTS = [location.hostname];") && !app.Contains("127.0.0.1"));
        Check("variante d'écriture aussi adaptée", NurLive.PatchApp("const PARENTS = ['a','b'];") == "const PARENTS = [location.hostname];");
        Check("le dossier d'origine n'est pas modifié", File.ReadAllText(Path.Combine(src, "ui", "js", "app.js")).Contains("127.0.0.1"));
        Check("synchro : rien à recopier si rien n'a changé", NurLive.Sync(src, dst) == 0);
        File.WriteAllText(Path.Combine(src, "ui", "index.html"), "<html>v2</html>");
        File.WriteAllText(Path.Combine(src, "ui", "js", "app.js"), "const PARENTS = [\"127.0.0.1\", \"localhost\"];\nconsole.log('v2');");
        int synced = NurLive.Sync(src, dst);
        Check("synchro : seuls les 2 fichiers modifiés sont recopiés", synced == 2, synced + " fichier(s)");
        Check("synchro : nouvelle version servie, lecteurs Twitch toujours autorisés", File.ReadAllText(Path.Combine(dst, "index.html")) == "<html>v2</html>" &&
              File.ReadAllText(Path.Combine(dst, "js", "app.js")).Contains("const PARENTS = [location.hostname];") && File.ReadAllText(Path.Combine(dst, "js", "app.js")).Contains("v2"));
        Check("synchro : les fichiers inchangés restent en place", File.Exists(Path.Combine(dst, "assets", "logo.png")));
        var bridge = NurLive.Bridge("A/*__SOURCES__*/B/*__MODEL__*/C", "S", "M");
        Check("pont : modules insérés", bridge == "ASBMC");
        var real = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "nurbridge.js"));
        Check("pont réel : emplacements des modules présents", real.Contains("/*__SOURCES__*/") && real.Contains("/*__MODEL__*/") && real.Contains("window.nur ="));
    }

    // ------------------------------------------------------------------
    static void ChannelTests()
    {
        Section("Chaînes (Twitch, Kick, YouTube)");
        (string In, string Id)[] cases =
        {
            ("gotaga", "twitch:gotaga"), ("https://www.twitch.tv/Squeezie", "twitch:squeezie"), ("@ZeratoR", "twitch:zerator"),
            ("twitch: kamet0", "twitch:kamet0"), ("https://kick.com/amouranth", "kick:amouranth"), ("kick:xqc", "kick:xqc"),
            ("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtube:v_dQw4w9WgXcQ"),
            ("https://www.youtube.com/channel/UCxxxxxxxxxxxxxxxxxxxxxx", "youtube:c_UCxxxxxxxxxxxxxxxxxxxxxx"),
            ("a", null), ("twitch.tv/directory", null), ("", null),
        };
        foreach (var (i, id) in cases)
        {
            var c = Channels.Parse(i);
            Check($"« {i} » → {id ?? "refusé"}", c?.Id == id, c?.Id);
        }
        Check("lecteur Twitch avec parent", Channels.PlayerUrl(new Channel { Key = "gentlemates" }, "litebrowser.home", true)
            == "https://player.twitch.tv/?channel=gentlemates&parent=litebrowser.home&muted=true&autoplay=true");
        Check("chat Twitch sombre", Channels.ChatUrl(new Channel { Key = "gentlemates" }, "litebrowser.home", true)
            == "https://www.twitch.tv/embed/gentlemates/chat?parent=litebrowser.home&darkpopout");
        Check("6 chaînes par défaut (comme Nur Live)", Channels.DefaultList().Count == 6 && Channels.DefaultList()[0].Notify);

        var nl = Path.Combine(Path.GetTempPath(), "lb-nl-state.json");
        File.WriteAllText(nl, "{\"version\":3,\"sources\":[{\"platform\":\"twitch\",\"key\":\"gentlemates\",\"label\":\"Gentle Mates\",\"fav\":true,\"notify\":true},{\"platform\":\"kick\",\"key\":\"xqc\",\"label\":\"xQc\"},{\"platform\":\"twitch\",\"key\":\"gentlemates\"},{\"platform\":\"bad\",\"key\":\"x\"}]}");
        var imported = Channels.ReadNurLive(nl);
        Check("chaînes de Nur Live reprises (doublons et inconnues retirés)", imported.Count == 2 && imported[0].Fav && imported[0].Notify && imported[1].Id == "kick:xqc");

        Section("Statut en live (DecAPI)");
        Check("« x is offline » → hors ligne", LiveStatus.ParseUptime("gentlemates is offline") == false);
        Check("« 2 hours, 3 minutes » → en live", LiveStatus.ParseUptime("2 hours, 3 minutes, 10 seconds") == true);
        Check("« User not found » → hors ligne", LiveStatus.ParseUptime("User not found: zzzz") == false);
        Check("erreur inconnue → statut inconnu", LiveStatus.ParseUptime("Rate limit exceeded") == null);
        var prev = new Dictionary<string, LiveInfo> { ["twitch:a"] = new() { Live = false }, ["twitch:b"] = new() { Live = true }, ["twitch:c"] = new() { Live = null } };
        var next = new Dictionary<string, LiveInfo> { ["twitch:a"] = new() { Live = true }, ["twitch:b"] = new() { Live = true }, ["twitch:c"] = new() { Live = true } };
        Check("alerte seulement quand on passe de hors ligne à en live", LiveStatus.WentLive(prev, next).SequenceEqual(new[] { "twitch:a" }));
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

        Section("Horaires de la mosquée (Mawaqit)");
        Check("lien complet", Mawaqit.NormalizeUrl("https://mawaqit.net/fr/grande-mosquee-de-paris") == "https://mawaqit.net/fr/grande-mosquee-de-paris");
        Check("lien sans https", Mawaqit.NormalizeUrl("mawaqit.net/en/m/Mosquee-Creutzwald-57150") == "https://mawaqit.net/fr/mosquee-creutzwald-57150");
        Check("nom court seul", Mawaqit.NormalizeUrl("grande-mosquee-de-paris") == "https://mawaqit.net/fr/grande-mosquee-de-paris");
        Check("lien hors Mawaqit refusé", Mawaqit.NormalizeUrl("https://google.com/x y") == null);
        var mq = File.ReadAllText(Path.Combine(Fixtures, "mawaqit_paris.html"));
        var nov1 = Mawaqit.Parse(mq, new DateTime(2026, 11, 1));
        Check("nom de la mosquée", nov1.City == "GRANDE MOSQUÉE DE PARIS", nov1.City);
        Check("1er novembre : Fajr 06:01, Isha 19:00", nov1.Times.First(t => t.Key == "Fajr").Time == new TimeSpan(6, 1, 0) && nov1.Times.First(t => t.Key == "Isha").Time == new TimeSpan(19, 0, 0));
        Check("6 horaires dont le lever du soleil 07:37", nov1.Times.Count == 6 && nov1.Times[1].Time == new TimeSpan(7, 37, 0));
        Check("Jumu'a 12:50", nov1.Jumua == "12:50", nov1.Jumua);
        Check("date hégirienne calculée", !string.IsNullOrEmpty(nov1.Hijri) && nov1.Hijri.Contains("1448"), nov1.Hijri);
        var feb29 = Mawaqit.Parse(mq, new DateTime(2028, 2, 29));
        Check("autre jour lu dans le calendrier annuel (29 février)", feb29.Times.First(t => t.Key == "Dhuhr").Time == new TimeSpan(13, 8, 0));
        bool bad = false;
        try { Mawaqit.Parse("<html>pas une mosquée</html>", DateTime.Today); } catch (InvalidDataException) { bad = true; }
        Check("page qui n'est pas une mosquée : erreur claire", bad);

        Section("Ajustement manuel des horaires");
        var off = PrayerAdjust.ParseOffsets("Fajr:5, Isha:-3,Dhuhr:0,Sunrise:10,bad");
        Check("décalages lus", off.Count == 3 && off["Fajr"] == 5 && off["Isha"] == -3);
        var adj = PrayerAdjust.Apply(Mawaqit.Parse(mq, new DateTime(2026, 11, 1)), off);
        Check("Fajr +5 → 06:06, Isha −3 → 18:57", adj.Times.First(t => t.Key == "Fajr").Time == new TimeSpan(6, 6, 0) && adj.Times.First(t => t.Key == "Isha").Time == new TimeSpan(18, 57, 0));
        Check("le lever du soleil ne bouge pas", adj.Times.First(t => t.Key == "Sunrise").Time == new TimeSpan(7, 37, 0));
        Check("format enregistré", PrayerAdjust.Format(off) == "Fajr:5,Isha:-3,Sunrise:10", PrayerAdjust.Format(off));

        var withTz ="{\"data\":{\"timings\":{\"Fajr\":\"06:10 (CET)\",\"Sunrise\":\"07:21 (CET)\",\"Dhuhr\":\"12:17 (CET)\",\"Asr\":\"14:47 (CET)\",\"Maghrib\":\"17:12 (CET)\",\"Isha\":\"18:23 (CET)\"}}}";
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
    static void OmniTests()
    {
        Section("Barre d'adresse façon Brave");
        var hist = new List<Omni.Entry>
        {
            new("YouTube", "https://www.youtube.com/"), new("YouTube", "https://www.youtube.com/"),
            new("Lofi - YouTube", "https://www.youtube.com/watch?v=abc"),
            new("Gentle Mates", "https://www.gentlemates.com/fr"),
            new("Accueil", "https://litebrowser.home/index.html"),
        };
        var favs = new List<Omni.Entry> { new("Twitch", "https://www.twitch.tv/") };
        var r = Omni.Rank("you", favs, hist);
        Check("« you » → l'accueil YouTube en premier", r.Count >= 2 && r[0].Url == "https://www.youtube.com/", string.Join(" | ", r.Select(x => x.Url)));
        Check("pages intégrées jamais proposées", Omni.Rank("acc", favs, hist).All(x => !x.Url.Contains("litebrowser.home")));
        Check("favori marqué « fav »", Omni.Rank("twi", favs, hist).FirstOrDefault()?.Kind == "fav");
        Check("recherche dans le titre : « gentle »", Omni.Rank("gentle", favs, hist).Any(x => x.Url.Contains("gentlemates")));
        Check("rien tapé → rien", Omni.Rank("  ", favs, hist).Count == 0);
        Check("« you » complété en « youtube.com »", Omni.InlineCompletion("you", r) == "youtube.com", Omni.InlineCompletion("you", r));
        Check("la casse tapée est gardée : « YOu » → « YOutube.com »", Omni.InlineCompletion("YOu", r) == "YOutube.com");
        Check("pas de complétion avec un espace", Omni.InlineCompletion("you tube", r) == null);
        Check("« www.yo » → « www.youtube.com »", Omni.InlineCompletion("www.yo", r) == "www.youtube.com");
        var k = Omni.Keyword(":yt lofi girl");
        Check(":yt lofi girl → YouTube", k is { } kk && kk.Name == "YouTube" && kk.Query == "lofi girl" && kk.Prefix.Contains("youtube.com/results"));
        Check(":w seul → Wikipédia, recherche vide", Omni.Keyword(":w") is { } kw && kw.Name == "Wikipédia" && kw.Query == "");
        Check(":zz → pas un raccourci", Omni.Keyword(":zz test") == null && Omni.Keyword("yt lofi") == null);
        Check("affichage sans https:// ni www.", Omni.Display("https://www.youtube.com/") == "youtube.com" && Omni.Display("https://fr.wikipedia.org/wiki/M8") == "fr.wikipedia.org/wiki/M8");
        Check("http:// reste visible (non sécurisé)", Omni.Display("http://exemple.fr/") == "http://exemple.fr/");
        Check("suggestions Google", string.Join(",", Omni.ParseSuggest("[\"gent\",[\"gentle mates\",\"gentleman\",\"Gentle Mates\"]]")) == "gentle mates,gentleman");
        Check("suggestions DuckDuckGo", string.Join(",", Omni.ParseSuggest("[{\"phrase\":\"gentle mates\"},{\"phrase\":\"gentle mates valorant\"}]")) == "gentle mates,gentle mates valorant");
        Check("réponse illisible → aucune suggestion", Omni.ParseSuggest("<html>") .Count == 0);
        Check("adresse de suggestions : Google / DuckDuckGo", Omni.SuggestUrl("google", "a b").Contains("suggestqueries.google.com") && Omni.SuggestUrl("qwant", "a").Contains("duckduckgo.com/ac"));
        Check("temps gagné : 0 → moins d'1 min", Omni.TimeSaved(0) == "moins d'1 min");
        Check("temps gagné : 2 400 éléments → 2 min", Omni.TimeSaved(2400) == "2 min", Omni.TimeSaved(2400));
        Check("temps gagné : 100 000 éléments → 1 h 23", Omni.TimeSaved(100_000) == "1 h 23", Omni.TimeSaved(100_000));

        Section("Navigateur par défaut : liens reçus");
        Check("lien https reçu", Launch.UrlFromArgs(new[] { "https://discord.com/x" }) == "https://discord.com/x");
        Check("guillemets retirés", Launch.UrlFromArgs(new[] { "\"https://a.fr/\"" }) == "https://a.fr/");
        Check("option --truc ignorée", Launch.UrlFromArgs(new[] { "--flag", "http://b.fr" }) == "http://b.fr");
        var tmp = Path.Combine(Path.GetTempPath(), "lb test é.html");
        File.WriteAllText(tmp, "<p>x</p>");
        var fu = Launch.UrlFromArgs(new[] { tmp });
        Check("fichier .html de l'Explorateur → file:///", fu != null && fu.StartsWith("file:///") && fu.Contains("lb%20test"), fu);
        File.Delete(tmp);
        Check("programme ou commande refusés", Launch.UrlFromArgs(new[] { "javascript:alert(1)", "calc.exe" }) == null && !Launch.Allowed("javascript:alert(1)"));
        Check("rien reçu → null", Launch.UrlFromArgs(Array.Empty<string>()) == null);

        Section("Import des favoris de Brave");
        var bm = "{\"roots\":{\"bookmark_bar\":{\"type\":\"folder\",\"children\":[{\"type\":\"url\",\"name\":\"Twitch\",\"url\":\"https://www.twitch.tv/\"}," +
                 "{\"type\":\"folder\",\"name\":\"Esport\",\"children\":[{\"type\":\"url\",\"name\":\"\",\"url\":\"https://www.gentlemates.com/\"}]}]}," +
                 "\"other\":{\"type\":\"folder\",\"children\":[{\"type\":\"url\",\"name\":\"Réglages\",\"url\":\"chrome://settings\"}]}},\"version\":1}";
        var got = Bookmarks.ParseChromium(bm);
        Check("2 favoris lus (dossiers compris, chrome:// ignoré)", got.Count == 2, string.Join(" | ", got));
        Check("favori sans nom → nom du site", got.Any(x => x.Title == "gentlemates.com"));
        var neu = Bookmarks.NewOnes(new List<(string, string)> { ("Twitch", "https://www.twitch.tv/") }, got.Concat(got));
        Check("doublons et favoris déjà présents ignorés", neu.Count == 1 && neu[0].Url == "https://www.gentlemates.com/");
        Check("fichier abîmé → aucun favori, pas de plantage", Bookmarks.ParseChromium("{pas du json").Count == 0);
    }

    static void SearchTests()
    {
        Section("Suggestions de chaînes");
        var a = ChannelSearch.Candidates("gota");
        Check("« gota » → gota (tel quel) puis gotaga", a.Count >= 2 && a[0] == "gota" && a.Contains("gotaga"), string.Join(",", a));
        var b = ChannelSearch.Candidates("squ");
        Check("« squ » → squeezie proposé", b.Contains("squeezie"), string.Join(",", b));
        Check("« ge » (trop court pour un pseudo) → suggestions par préfixe", ChannelSearch.Candidates("ge").Contains("gentlemates"));
        Check("1 lettre → rien", ChannelSearch.Candidates("g").Count == 0);
        Check("lien twitch.tv/Gotaga → gotaga en premier", ChannelSearch.Candidates("https://twitch.tv/Gotaga")[0] == "gotaga");
        Check("au plus 5 candidats", ChannelSearch.Candidates("a").Count == 0 && ChannelSearch.Candidates("an").Count <= 5);
        Check("tes chaînes sont aussi proposées", ChannelSearch.Candidates("zzmonpote", new[] { "zzmonpote_tv" }).Contains("zzmonpote_tv"));
        var found = ChannelSearch.ParseSearch("{\"data\":{\"searchUsers\":{\"edges\":[" +
            "{\"node\":{\"login\":\"petit\",\"displayName\":\"Petit\",\"profileImageURL\":\"https://x/a.png\",\"followers\":{\"totalCount\":10},\"stream\":null}}," +
            "{\"node\":null}," +
            "{\"node\":{\"login\":\"grand\",\"displayName\":\"\",\"followers\":{\"totalCount\":5000},\"stream\":null}}," +
            "{\"node\":{\"login\":\"enlive\",\"displayName\":\"EnLive\",\"followers\":{\"totalCount\":3},\"stream\":{\"viewersCount\":42,\"game\":{\"displayName\":\"Valorant\"}}}}" +
            "]}}}");
        Check("recherche Twitch : en live d'abord, puis les plus suivis", string.Join(",", found.Select(h => h.Key)) == "enlive,grand,petit", string.Join(",", found.Select(h => h.Key)));
        Check("recherche Twitch : live, spectateurs et jeu lus", found[0].Live && found[0].Viewers == 42 && found[0].Game == "Valorant");
        Check("recherche Twitch : nom vide remplacé par le pseudo", found[1].Name == "grand");
        var bridge = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "nurbridge.js"));
        Check("Nur Live : la recherche passe par le navigateur", bridge.Contains("rpc(\"search\""));
    }

    static async Task Online()
    {
        Section("En ligne : recherche de chaînes");
        var hits = await ChannelSearch.SearchAsync("gotag");
        Check("« gotag » → Gotaga trouvé avec avatar", hits.Any(h => h.Key == "gotaga" && h.Avatar?.StartsWith("https://") == true),
            string.Join(",", hits.Select(h => h.Key)));
        Check("pseudo inexistant écarté", !hits.Any(h => h.Key == "gotag" && h.Avatar == null));
        var g = hits.FirstOrDefault(h => h.Key == "gotaga");
        Check("Gotaga : live (viewers) ou abonnés", g != null && (g.Live ? g.Viewers > 0 : g.Followers > 1000), g == null ? "" : $"live={g.Live} v={g.Viewers} f={g.Followers}");

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

        Section("En ligne : lives Twitch (DecAPI)");
        try
        {
            var st = await LiveStatus.FetchAsync(Channels.DefaultList());
            Check($"statut de {st.Count} chaînes : " + string.Join(", ", st.Select(kv => kv.Key.Split(':')[1] + (kv.Value.Live == true ? " LIVE" : kv.Value.Live == false ? " off" : " ?"))),
                st.Count == 6 && st.Values.All(v => v.Live != null));
            Check("avatars récupérés", st.Values.Count(v => !string.IsNullOrEmpty(v.Avatar)) >= 5);
        }
        catch (Exception e) { Check("DecAPI", false, e.Message); }

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
