// Gentle Mates : matchs (Liquipedia, pages d'équipe), alertes, lives Twitch/Kick, chaînes favorites.
// Logique reprise de Nur Live 3 (core/matches.js, core/status.js, core/sources.js) et testée à l'identique.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LiteCore
{
    public class StreamRef
    {
        [JsonPropertyName("platform")] public string Platform { get; set; }
        [JsonPropertyName("key")] public string Key { get; set; }
        public StreamRef() { }
        public StreamRef(string platform, string key) { Platform = platform; Key = key; }
    }

    public class M8Match
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("game")] public string Game { get; set; }
        [JsonPropertyName("ts")] public long Ts { get; set; }
        [JsonPropertyName("opponent")] public string Opponent { get; set; }
        [JsonPropertyName("opponentShort")] public string OpponentShort { get; set; }
        [JsonPropertyName("tournament")] public string Tournament { get; set; }
        [JsonPropertyName("stage")] public string Stage { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; }   // upcoming | live | finished
        [JsonPropertyName("score")] public string Score { get; set; }
        [JsonPropertyName("url")] public string Url { get; set; }
        [JsonPropertyName("streams")] public List<StreamRef> Streams { get; set; } = new();
        /// Liquipedia a publié l'affiche mais pas l'heure : Ts ne vaut que pour la date, jamais d'alerte.
        [JsonPropertyName("timeUnknown")] public bool TimeUnknown { get; set; }
    }

    public class M8Tournament
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("when")] public string When { get; set; }
        [JsonPropertyName("url")] public string Url { get; set; }
        [JsonPropertyName("game")] public string Game { get; set; }
    }

    public record M8Page(string Game, string Wiki, string Page, string Label, string Short);
    public record M8Alert(string Key, string Kind, M8Match Match); // Kind : reminder | start

    public static class M8
    {
        public static readonly M8Page[] Pages =
        {
            new("valorant", "valorant", "Gentle_Mates", "Valorant", "VAL"),
            new("valorantgc", "valorant", "Gentle_Mates_GC", "Valorant Game Changers", "GC"),
            new("callofduty", "callofduty", "Paris_Gentle_Mates", "Call of Duty", "CDL"),
            new("warzone", "callofduty", "Gentle_Mates/Warzone", "Warzone", "WZ"),
            new("rocketleague", "rocketleague", "Gentle_Mates", "Rocket League", "RL"),
            new("fortnite", "fortnite", "Gentle_Mates", "Fortnite", "FN"),
            new("ageofempires", "ageofempires", "Gentle_Mates", "Age of Empires", "AoE"),
        };

        /// Chaînes officielles de diffusion (si Liquipedia n'indique aucun stream).
        public static readonly Dictionary<string, string[]> Broadcast = new()
        {
            ["valorant"] = new[] { "valorant_emea" }, ["valorantgc"] = new[] { "valorant" },
            ["callofduty"] = new[] { "callofduty" }, ["warzone"] = new[] { "callofduty" },
            ["rocketleague"] = new[] { "rocketleague" }, ["fortnite"] = new[] { "fortnite" },
            ["ageofempires"] = new[] { "ageofempires" },
        };

        /// Co-diffusions M8 par jeu (« Regarder » ouvre celles qui sont en live).
        public static readonly Dictionary<string, string> CoStreams = new()
        {
            ["valorant"] = "squeezie, gentlemates", ["valorantgc"] = "squeezie, gentlemates",
            ["callofduty"] = "gotaga, brawks", ["warzone"] = "gotaga", ["rocketleague"] = "gentlemates",
            ["fortnite"] = "nikof", ["ageofempires"] = "gentlemates",
        };

        public const int MinGapMs = 31_000;
        const long LiveWindowS = 4 * 3600;
        static readonly Regex SelfRe = new(@"gentle\s*mates|^m8(\.|\b)|^paris gentle mates$", RegexOptions.IgnoreCase);

        public static M8Page Info(string game) =>
            Pages.FirstOrDefault(p => p.Game == game) ?? new M8Page(game, game, "Gentle_Mates", game, game.Length > 3 ? game[..3].ToUpperInvariant() : game);

        public static string ApiUrl(M8Page p) =>
            $"https://liquipedia.net/{p.Wiki}/api.php?action=parse&page={Uri.EscapeDataString(p.Page).Replace("%2F", "/")}&format=json&prop=text&redirects=1";

        // ---------- utilitaires HTML (identiques à Nur Live) ----------

        public static string Decode(string s) => WebUtility.HtmlDecode(s ?? "").Replace(' ', ' ');

        static string StripTags(string s) =>
            Regex.Replace(Decode(Regex.Replace(s ?? "", "<[^>]+>", " ")), @"\s+", " ").Trim();

        /// lastIndexOf façon JavaScript (l'occurrence peut commencer à « from »).
        static int LastIndexJs(string s, string v, int from)
        {
            int start = Math.Min(s.Length - 1, from + v.Length - 1);
            return start < 0 ? -1 : s.LastIndexOf(v, start, StringComparison.Ordinal);
        }

        static int IndexJs(string s, string v, int from) =>
            from >= s.Length ? -1 : s.IndexOf(v, Math.Max(0, from), StringComparison.Ordinal);

        record Team(string Name, string Short, bool Self);

        static List<Team> TeamsIn(string fragment)
        {
            var outp = new List<Team>();
            foreach (Match m in Regex.Matches(fragment, "<div class=\"block-team[^\"]*\">([\\s\\S]*?)</div>"))
            {
                var blk = m.Groups[1].Value;
                bool selfLink = Regex.IsMatch(blk, "mw-selflink|selflink");
                var title = Regex.Match(blk, "<a [^>]*title=\"([^\"]+)\"");
                var nameSpan = Regex.Match(blk, "<span class=\"name\"[^>]*>([\\s\\S]*?)</span>");
                var shortName = StripTags(nameSpan.Success ? nameSpan.Groups[1].Value : "");
                var name = Decode(title.Success ? title.Groups[1].Value : shortName);
                if (string.IsNullOrEmpty(name)) continue;
                outp.Add(new Team(name, string.IsNullOrEmpty(shortName) ? name : shortName, selfLink || SelfRe.IsMatch(name)));
            }
            return outp;
        }

        static List<(string Href, string Text)> AnchorsWithText(string fragment)
        {
            var outp = new List<(string, string)>();
            foreach (Match m in Regex.Matches(fragment, "<a [^>]*href=\"([^\"]+)\"[^>]*>([\\s\\S]*?)</a>"))
            {
                var text = StripTags(m.Groups[2].Value);
                if (text.Length > 0) outp.Add((Decode(m.Groups[1].Value), text));
            }
            return outp;
        }

        public static List<StreamRef> StreamsIn(string fragment)
        {
            var outp = new List<StreamRef>();
            foreach (Match m in Regex.Matches(fragment, "Special:Stream/(twitch|youtube|kick)/([^\"&#?]+)", RegexOptions.IgnoreCase))
            {
                var platform = m.Groups[1].Value.ToLowerInvariant();
                string key;
                try { key = Uri.UnescapeDataString(Decode(m.Groups[2].Value)).ToLowerInvariant(); }
                catch { key = Decode(m.Groups[2].Value).ToLowerInvariant(); }
                key = key.Trim().Replace(' ', '_');
                if (!outp.Any(s => s.Platform == platform && s.Key == key)) outp.Add(new StreamRef(platform, key));
            }
            foreach (Match m in Regex.Matches(fragment, "twitch\\.tv/([a-z0-9_]{3,25})", RegexOptions.IgnoreCase))
            {
                var key = m.Groups[1].Value.ToLowerInvariant();
                if (key != "embed" && !outp.Any(s => s.Key == key)) outp.Add(new StreamRef("twitch", key));
            }
            return outp;
        }

        static (string Kind, string Text) Enclosing(string html, int idx)
        {
            int mi = LastIndexJs(html, "class=\"match-info ", idx);
            int miAlt = LastIndexJs(html, "class=\"match-info\"", idx);
            int start = Math.Max(mi, miAlt);
            if (start >= 0 && idx - start < 600)
            {
                int end = IndexJs(html, "class=\"match-info ", idx);
                int end2 = IndexJs(html, "class=\"match-info\"", idx);
                if (end < 0 || (end2 >= 0 && end2 < end)) end = end2;
                if (end < 0 || end - idx > 9000) end = idx + 9000;
                end = Math.Min(end, html.Length);
                return ("card", html[start..end]);
            }
            int tr = LastIndexJs(html, "<tr", idx);
            int trEnd = IndexJs(html, "</tr>", idx);
            if (tr >= 0 && trEnd > idx && trEnd - tr < 20000) return ("row", html[tr..trEnd]);
            int a = Math.Max(0, idx - 2500), b = Math.Min(html.Length, idx + 2500);
            return ("legacy", html[a..b]);
        }

        static List<Team> LegacyTeams(string fragment)
        {
            var outp = new List<Team>();
            foreach (var cls in new[] { "team-left", "team-right" })
            {
                var m = Regex.Match(fragment, $"class=\"{cls}\"[\\s\\S]*?title=\"([^\"]+)\"");
                if (m.Success)
                {
                    var name = Decode(m.Groups[1].Value);
                    outp.Add(new Team(name, name, SelfRe.IsMatch(name)));
                }
            }
            return outp;
        }

        /// Matchs trouvés sur une page d'équipe Liquipedia.
        public static List<M8Match> ParseTeamPage(string html, string game, string wiki, string page, long nowMs)
        {
            html ??= "";
            long now = nowMs / 1000;
            wiki ??= "valorant";
            game ??= wiki;
            var seen = new HashSet<string>();
            var outp = new List<M8Match>();
            foreach (Match m in Regex.Matches(html, "<span class=\"timer-object[^\"]*\"[^>]*data-timestamp=\"(\\d+)\""))
            {
                long ts = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (ts == 0 || ts < now - LiveWindowS || ts > now + 60 * 86400) continue;
                var (kind, text) = Enclosing(html, m.Index);
                var teams = TeamsIn(text);
                if (teams.Count == 0 && kind == "legacy") teams = LegacyTeams(text);
                var others = teams.Where(t => !t.Self).ToList();
                if (kind == "card" && teams.Count > 0 && !teams.Any(t => t.Self) && others.Count > 1) continue; // match d'une autre équipe
                Team opponent = others.FirstOrDefault();
                if (opponent == null && kind == "row")
                {
                    var sv = Regex.Match(text, "data-sort-value=\"([^\"]+)\"[^>]*>\\s*<div class=\"block-team");
                    if (sv.Success) opponent = new Team(Decode(sv.Groups[1].Value), Decode(sv.Groups[1].Value), false);
                }

                string tournament = "", stage = "";
                var tn = Regex.Match(text, "class=\"match-info-tournament-name\"[^>]*>([\\s\\S]*?)</span>\\s*</span>");
                if (tn.Success) tournament = StripTags(tn.Groups[1].Value);
                var sg = Regex.Match(text, "class=\"match-info-stage\"[^>]*>([\\s\\S]*?)</span>");
                if (sg.Success) stage = StripTags(sg.Groups[1].Value);
                if (tournament.Length == 0)
                {
                    var teamNames = new HashSet<string>(teams.SelectMany(t => new[] { t.Name, t.Short }));
                    var cand = AnchorsWithText(text).FirstOrDefault(a =>
                        !Regex.IsMatch(a.Text, "Tier", RegexOptions.IgnoreCase) && !teamNames.Contains(a.Text) &&
                        !Regex.IsMatch(a.Text, "match details|^vs\\.?$", RegexOptions.IgnoreCase) && !a.Href.Contains("Match:"));
                    if (cand.Text != null) tournament = cand.Text;
                    var tt = Regex.Match(text, "class=\"tournament-text\"[^>]*>([\\s\\S]*?)</(?:span|div)>");
                    if (tournament.Length == 0 && tt.Success) tournament = StripTags(tt.Groups[1].Value);
                }

                string score = "";
                bool finished = Regex.IsMatch(text, "data-label-type=\"result-(?:win|loss|draw)\"");
                var sc = Regex.Match(text, "<td[^>]*>\\s*(?:<span[^>]*>)?(\\d+)(?:</span>)?\\s*&#160;&#58;&#160;\\s*(?:<span[^>]*>)?(\\d+)");
                if (sc.Success) { score = $"{sc.Groups[1].Value} : {sc.Groups[2].Value}"; finished = true; }
                var cardScores = Regex.Matches(text, "class=\"match-info-opponent-score[^\"]*\"[^>]*>([^<]*)<")
                    .Select(x => StripTags(x.Groups[1].Value)).ToList();
                if (cardScores.Count == 2 && cardScores.All(x => Regex.IsMatch(x, "^\\d+$")))
                {
                    score = string.Join(" : ", cardScores);
                    if (Regex.IsMatch(text, "match-info-opponent-score--winner|winner")) finished = true;
                }
                string status = finished ? "finished" : ts > now ? "upcoming" : "live";
                var linkM = Regex.Match(text, "href=\"(/[a-z]+/Match:[^\"]+)\"");
                string link = linkM.Success ? linkM.Groups[1].Value : null;
                string opp = opponent?.Name ?? "À déterminer";
                string id = link != null
                    ? $"{game}:{Regex.Replace(Decode(link), "^.*Match:", "")}"
                    : $"{game}:{ts}:{Regex.Replace(opp.ToLowerInvariant(), "[^a-z0-9]+", "")}";
                if (!seen.Add(id)) continue;
                outp.Add(new M8Match
                {
                    Id = id, Game = game, Ts = ts, Opponent = opp, OpponentShort = opponent?.Short ?? "TBD",
                    Tournament = tournament, Stage = stage, Status = status, Score = score,
                    Url = link != null ? "https://liquipedia.net" + Decode(link) : $"https://liquipedia.net/{wiki}/{page ?? "Gentle_Mates"}",
                    Streams = StreamsIn(text),
                });
            }
            return outp.OrderBy(x => x.Ts).ToList();
        }

        /// Encart « Upcoming Tournaments ».
        // ---------- Tournois hors page d'équipe (Coupe de France, ligues nationales…) ----------
        // Liquipedia n'affiche sur la page de l'équipe que les matchs des grosses compétitions.
        // On demande donc à l'API la liste des pages qui citent l'équipe, on garde les tournois de
        // l'année modifiés récemment, puis on lit ces pages pour en extraire les matchs de M8.

        public static string BacklinksUrl(M8Page p) =>
            $"https://liquipedia.net/{p.Wiki}/api.php?action=query&format=json&generator=backlinks&gbltitle={Uri.EscapeDataString(p.Page).Replace("%2F", "/")}" +
            "&gblnamespace=0&gbllimit=500&prop=revisions&rvprop=timestamp";

        public static string ParseUrl(string wiki, string title) =>
            $"https://liquipedia.net/{wiki}/api.php?action=parse&page={Uri.EscapeDataString(title.Replace(' ', '_')).Replace("%2F", "/")}&format=json&prop=text&redirects=1";

        static readonly Regex NotTournament = new(
            @"Transfers|Rumou?rs|News|Statistics|Player|Portal|Earnings|Ranking|Roster|Broadcast|Participants$|/Results|Head2head|Main Page|Liquipedia:",
            RegexOptions.IgnoreCase);

        /// Tournois de l'année (ou de la suivante) modifiés ces derniers jours, les plus récents d'abord.
        public static List<string> TournamentCandidates(string backlinksJson, DateTime nowUtc, int days = 3)
        {
            var outp = new List<(string Title, DateTime At)>();
            using var doc = JsonDocument.Parse(backlinksJson ?? "{}");
            if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages)) return new List<string>();
            var years = new[] { nowUtc.Year, nowUtc.Year + 1, nowUtc.Month == 1 ? nowUtc.Year - 1 : nowUtc.Year }
                .Select(y => y.ToString(CultureInfo.InvariantCulture)).ToArray();
            foreach (var pg in pages.EnumerateObject())
            {
                var title = pg.Value.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                // Certaines ligues nomment leurs pages par saison, sans année : « Call of Duty League/Season 8/… »
                bool dated = years.Any(y => Regex.IsMatch(title, $@"(^|[^0-9]){y}([^0-9]|$)")) || Regex.IsMatch(title, @"(^|/)Season \d+(/|$)");
                if (title.Length == 0 || NotTournament.IsMatch(title) || !dated) continue;
                DateTime at = DateTime.MinValue;
                if (pg.Value.TryGetProperty("revisions", out var revs) && revs.ValueKind == JsonValueKind.Array && revs.GetArrayLength() > 0 &&
                    revs[0].TryGetProperty("timestamp", out var ts))
                    DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at);
                if (nowUtc - at > TimeSpan.FromDays(days)) continue;
                outp.Add((title, at));
            }
            return outp.OrderByDescending(x => x.At).Select(x => x.Title).ToList();
        }

        /// Pages à lire : d'abord les tournois connus, puis une page par jeu à tour de rôle, pour qu'un jeu
        /// aux nombreuses compétitions (RLCS, VCT) ne prive pas les autres de lecture.
        public static List<(M8Page Page, string Title, bool Known)> FairOrder(IEnumerable<(M8Page Page, string Title, bool Known)> queue)
        {
            var list = queue.ToList();
            return list.Where(q => q.Known)
                .Concat(list.Where(q => !q.Known).GroupBy(q => q.Page.Game).SelectMany(g => g.Select((q, i) => (q, i))).OrderBy(x => x.i).Select(x => x.q))
                .ToList();
        }

        /// Phase en français : « Day 1 » → « Jour 1 », « Semifinals » → « Demi-finales »…
        public static string StageFr(string stage)
        {
            var s = (stage ?? "").Trim();
            var d = Regex.Match(s, @"^(Day|Week|Round|Match ?day)\s*(\d+)$", RegexOptions.IgnoreCase);
            if (d.Success)
                return (d.Groups[1].Value.ToLowerInvariant() switch { "week" => "Semaine ", "round" => "Ronde ", _ => "Jour " }) + d.Groups[2].Value;
            return s.ToLowerInvariant() switch
            {
                "grand final" or "grand finals" or "final" or "finals" => "Finale",
                "semifinals" or "semifinal" or "semi-finals" => "Demi-finales",
                "quarterfinals" or "quarterfinal" or "quarter-finals" => "Quarts de finale",
                "upper bracket" => "Tableau haut", "lower bracket" => "Tableau bas",
                "group stage" => "Phase de groupes", "playoffs" => "Phase finale",
                _ => s,
            };
        }

        /// « NEO/Coupe de France Slash/2026 » → « Coupe de France Slash 2026 ».
        public static string TournamentName(string title)
        {
            var parts = (title ?? "").Replace('_', ' ').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && !Regex.IsMatch(parts[0], @"\d{4}")) parts = parts.Skip(1).ToArray();
            parts = parts.Where(p => !Regex.IsMatch(p, @"^Season \d+$")).ToArray();
            return string.Join(" ", parts);
        }

        /// Matchs de M8 sur une page de tournoi (listes de matchs et tableaux).
        public static List<M8Match> ParseTournamentPage(string html, string game, string wiki, string title, long nowMs)
        {
            html ??= "";
            long now = nowMs / 1000;
            var outp = new List<M8Match>();
            var seen = new HashSet<string>();
            string tournament = TournamentName(title);
            var starts = Regex.Matches(html, "<div class=\"brkts-(?:matchlist-)?match [^\"]*brkts-match-popup-wrapper").Select(m => m.Index).ToList();
            for (int i = 0; i < starts.Count; i++)
            {
                int a = starts[i], b = i + 1 < starts.Count ? starts[i + 1] : Math.Min(html.Length, a + 20000);
                var blk = html[a..b];
                var tsM = Regex.Match(blk, "class=\"timer-object[^\"]*\"[^>]*data-timestamp=\"(\\d+)\"");
                if (!tsM.Success)
                {
                    if (DateOnlyMatch(html, blk, a, game, wiki, title, tournament, now) is M8Match undated && seen.Add(undated.Id)) outp.Add(undated);
                    continue;
                }
                long ts = long.Parse(tsM.Groups[1].Value, CultureInfo.InvariantCulture);
                if (ts == 0 || ts < now - LiveWindowS || ts > now + 60 * 86400) continue;
                var names = Regex.Matches(blk, "<div class=\"team-name\">([\\s\\S]*?)</div>").Select(x => StripTags(x.Groups[1].Value)).Take(2).ToList();
                var shorts = Regex.Matches(blk, "<div class=\"team-shortname\">([\\s\\S]*?)</div>").Select(x => StripTags(x.Groups[1].Value)).Take(2).ToList();
                if (names.Count < 2) continue;
                int self = SelfRe.IsMatch(names[0]) ? 0 : SelfRe.IsMatch(names[1]) ? 1 : -1;
                if (self < 0) continue; // match d'autres équipes
                int other = 1 - self;
                string opp = names[other], oppShort = shorts.Count == 2 ? shorts[other] : opp;
                if (opp == "TBD" || opp.Length == 0) { opp = "À déterminer"; oppShort = "TBD"; }

                // scores : cellules de la liste de matchs, sinon cases du tableau
                var sc = Regex.Matches(blk, "class=\"brkts-matchlist-cell brkts-matchlist-score[^\"]*\"[^>]*>\\s*<div class=\"brkts-matchlist-cell-content\">([\\s\\S]*?)</div>")
                    .Select(x => StripTags(x.Groups[1].Value)).ToList();
                if (sc.Count < 2)
                    sc = Regex.Matches(blk, "class=\"brkts-opponent-score-inner\">([\\s\\S]*?)</div>").Select(x => StripTags(x.Groups[1].Value)).ToList();
                string score = "";
                bool finished = false;
                if (sc.Count >= 2 && Regex.IsMatch(sc[0], "^\\d+$") && Regex.IsMatch(sc[1], "^\\d+$"))
                {
                    score = self == 0 ? $"{sc[0]} : {sc[1]}" : $"{sc[1]} : {sc[0]}";
                    finished = Regex.IsMatch(blk, "brkts-matchlist-slot-winner|brkts-opponent-win|font-weight:bold");
                }

                // phase : titre de la liste (« Day 1 ») ou du tour (« Semifinals ») le plus proche
                string stage = "";
                int lt = LastIndexJs(html, "general-collapsible-default-title\">", a);
                int rh = LastIndexJs(html, "class=\"brkts-round-header", a);
                if (lt >= 0 && lt > rh)
                    stage = StripTags(html.Substring(lt + 35, Math.Min(200, html.Length - lt - 35)).Split('<')[0]);
                else if (rh >= 0)
                {
                    var rm = Regex.Match(html.Substring(rh, Math.Min(600, html.Length - rh)), ">([^<>]{2,40})<");
                    if (rm.Success) stage = Decode(rm.Groups[1].Value).Trim();
                }

                stage = StageFr(stage);
                var linkM = Regex.Match(blk, "href=\"(/[a-z]+/Match:[^\"]+)\"");
                string id = linkM.Success
                    ? $"{game}:{Regex.Replace(Decode(linkM.Groups[1].Value), "^.*Match:", "")}"
                    : $"{game}:{ts}:{Regex.Replace(opp.ToLowerInvariant(), "[^a-z0-9]+", "")}";
                if (!seen.Add(id)) continue;
                outp.Add(new M8Match
                {
                    Id = id, Game = game, Ts = ts, Opponent = opp, OpponentShort = oppShort, Tournament = tournament, Stage = stage,
                    Status = finished ? "finished" : ts > now ? "upcoming" : "live", Score = score,
                    Url = $"https://liquipedia.net/{wiki}/{title.Replace(' ', '_')}", Streams = StreamsIn(blk),
                });
            }
            return outp.OrderBy(m => m.Ts).ToList();
        }

        /// Affiche publiée sans heure (groupes annoncés avant le planning) : M8 et son adversaire, à la date du tournoi.
        static M8Match DateOnlyMatch(string html, string blk, int at, string game, string wiki, string title, string tournament, long now)
        {
            var names = Regex.Matches(blk, "<div class=\"team-name\">([\\s\\S]*?)</div>").Select(x => StripTags(x.Groups[1].Value)).Take(2).ToList();
            if (names.Count < 2) return null;
            int self = SelfRe.IsMatch(names[0]) ? 0 : SelfRe.IsMatch(names[1]) ? 1 : -1;
            if (self < 0 || names[1 - self] == "TBD" || names[1 - self].Length == 0) return null;
            if (Regex.IsMatch(blk, "brkts-matchlist-slot-winner|brkts-opponent-win")) return null; // déjà joué
            var start = Regex.Match(html, @"Start Date:\s*</div>\s*<div[^>]*>\s*(\d{4})-(\d{2})-(\d{2})");
            var end = Regex.Match(html, @"End Date:\s*</div>\s*<div[^>]*>\s*(\d{4})-(\d{2})-(\d{2})");
            if (!start.Success) return null;
            static long Noon(Match d) => new DateTimeOffset(int.Parse(d.Groups[1].Value), int.Parse(d.Groups[2].Value), int.Parse(d.Groups[3].Value), 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            long from = Noon(start), to = end.Success ? Noon(end) : from;
            if (now > to + 12 * 3600 || from > now + 60 * 86400) return null;
            // pendant le tournoi, l'affiche reste visible à la date du jour (midi UTC = bonne date en France)
            long today = now - now % 86400 + 43200;
            var shorts = Regex.Matches(blk, "<div class=\"team-shortname\">([\\s\\S]*?)</div>").Select(x => StripTags(x.Groups[1].Value)).Take(2).ToList();
            var opp = names[1 - self];
            int gh = html.LastIndexOf("id=\"Group_", at, StringComparison.Ordinal);
            var group = gh >= 0 ? Regex.Match(html.Substring(gh, 20), "Group_([A-Za-z0-9]+)") : Match.Empty;
            string stage = group.Success ? "Groupe " + group.Groups[1].Value : "";
            string oppKey = Regex.Replace(opp.ToLowerInvariant(), "[^a-z0-9]+", "");
            // Heure annoncée mais pas encore sur Liquipedia : utilisée jusqu'à ce que le vrai horaire apparaisse sur la page
            bool hinted = TimeHints.TryGetValue(title + "|" + oppKey, out var hint) && hint > now - LiveWindowS;
            return new M8Match
            {
                Id = $"{game}:{Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "")}:{oppKey}",
                Game = game, Ts = hinted ? hint : Math.Max(from, today), Opponent = opp, OpponentShort = shorts.Count == 2 ? shorts[1 - self] : opp,
                Tournament = tournament, Stage = hinted ? (stage.Length > 0 ? stage + " · " : "") + "heure à confirmer" : stage,
                Status = hinted && hint <= now ? "live" : "upcoming", Score = "",
                Url = $"https://liquipedia.net/{wiki}/{title.Replace(' ', '_')}", TimeUnknown = !hinted,
            };
        }

        /// Heures connues avant Liquipedia (page|adversaire → instant UTC). 6 nov. 2026, 20 h à Paris : indiquée par Ryan, à confirmer.
        static readonly Dictionary<string, long> TimeHints = new()
        {
            ["Call of Duty League/Season 8/Monster Launch Invitational|cloud9newyork"] =
                new DateTimeOffset(2026, 11, 6, 19, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        };

        static readonly string[] DaysFr = { "dim.", "lun.", "mar.", "mer.", "jeu.", "ven.", "sam." };
        static readonly string[] MonthsFr = { "janv.", "févr.", "mars", "avr.", "mai", "juin", "juil.", "août", "sept.", "oct.", "nov.", "déc." };

        /// « ven. 6 nov. » (le navigateur tourne sans données de langue, d'où ces noms écrits à la main).
        public static string DateFr(long ts)
        {
            var d = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
            return $"{DaysFr[(int)d.DayOfWeek]} {d.Day} {MonthsFr[d.Month - 1]}";
        }

        /// Pour Nur Live, dont le panneau suppose une vraie heure : les affiches sans heure passent en « tournoi à venir ».
        public static List<M8Tournament> UndatedAsTournaments(IEnumerable<M8Match> list) =>
            (list ?? Enumerable.Empty<M8Match>()).Where(m => m.TimeUnknown).Select(m => new M8Tournament
            {
                Game = m.Game, Url = m.Url, When = DateFr(m.Ts) + " · horaire à venir",
                Name = $"{m.Tournament} · M8 vs {m.OpponentShort}" + (string.IsNullOrEmpty(m.Stage) ? "" : $" ({m.Stage})"),
            }).ToList();

        public static List<M8Tournament> ParseUpcomingTournaments(string html, string game, string wiki)
        {
            var outp = new List<M8Tournament>();
            foreach (Match m in Regex.Matches(html ?? "",
                "tournaments-list-item(?:&#95;&#95;|__)name\"[^>]*>\\s*<a href=\"([^\"]+)\"[^>]*>([\\s\\S]*?)</a>([\\s\\S]{0,2500}?)tournaments-list-item(?:&#95;&#95;|__)date\"[^>]*>([^<]*)<"))
                outp.Add(new M8Tournament
                {
                    Name = StripTags(m.Groups[2].Value), When = StripTags(m.Groups[4].Value),
                    Url = "https://liquipedia.net" + Decode(m.Groups[1].Value), Game = game ?? wiki,
                });
            return outp;
        }

        /// Recalcule le statut à l'instant t (cache ancien).
        public static List<M8Match> RefreshStatus(IEnumerable<M8Match> list, long nowMs)
        {
            long now = nowMs / 1000;
            return (list ?? Enumerable.Empty<M8Match>())
                .Where(m => m.Ts > now - LiveWindowS || m.Status == "live")
                .Select(m => Clone(m, m.TimeUnknown ? "upcoming" : m.Status == "finished" ? "finished" : m.Ts > now ? "upcoming" : m.Ts > now - LiveWindowS ? "live" : "finished"))
                .Where(m => m.Ts > now - LiveWindowS).ToList();
        }

        static M8Match Clone(M8Match m, string status) => new()
        {
            Id = m.Id, Game = m.Game, Ts = m.Ts, Opponent = m.Opponent, OpponentShort = m.OpponentShort,
            Tournament = m.Tournament, Stage = m.Stage, Status = status, Score = m.Score, Url = m.Url, Streams = m.Streams, TimeUnknown = m.TimeUnknown,
        };

        /// Alertes à envoyer maintenant (rappel avant le match, puis coup d'envoi).
        public static List<M8Alert> DueAlerts(IEnumerable<M8Match> list, bool notify, int reminderMin,
            IDictionary<string, bool> games, ISet<string> mutes, ISet<string> fired, long nowMs)
        {
            long now = nowMs / 1000;
            var outp = new List<M8Alert>();
            if (!notify) return outp;
            foreach (var m in list ?? Enumerable.Empty<M8Match>())
            {
                if ((games != null && games.TryGetValue(m.Game, out var on) && !on) || (mutes != null && mutes.Contains(m.Id))) continue;
                if (m.Status == "finished" || m.TimeUnknown) continue; // pas d'heure connue : pas d'alerte à une heure fausse
                string kStart = $"{m.Game}@{m.Ts}#start", kRem = $"{m.Game}@{m.Ts}#rem{reminderMin}";
                if (now >= m.Ts && now < m.Ts + 1200 && !fired.Contains(kStart)) outp.Add(new M8Alert(kStart, "start", m));
                else if (reminderMin > 0 && now >= m.Ts - reminderMin * 60 && now < m.Ts && !fired.Contains(kRem)) outp.Add(new M8Alert(kRem, "reminder", m));
            }
            return outp;
        }

        /// Chaînes officielles d'un match : streams Liquipedia, sinon diffuseur officiel, + gentlemates.
        public static List<StreamRef> WatchSources(M8Match match)
        {
            var list = new List<StreamRef>(match.Streams ?? new());
            if (list.Count == 0 && Broadcast.TryGetValue(match.Game ?? "", out var b)) list.AddRange(b.Select(k => new StreamRef("twitch", k)));
            list.Add(new StreamRef("twitch", "gentlemates"));
            var seen = new HashSet<string>();
            return list.Where(s => seen.Add(s.Platform + ":" + s.Key)).Take(4).ToList();
        }

        /// Lit le cache de matchs de Nur Live (évite de solliciter Liquipedia deux fois).
        public static (List<M8Match> Matches, List<M8Tournament> Tournaments, long FetchedAt)? ReadNurLiveCache(string path)
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("matches", out var ms) || ms.ValueKind != JsonValueKind.Array) return null;
            var matches = JsonSerializer.Deserialize<List<M8Match>>(ms.GetRawText()) ?? new();
            var tournaments = root.TryGetProperty("tournaments", out var t) && t.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<M8Tournament>>(t.GetRawText()) ?? new() : new();
            long fetched = root.TryGetProperty("fetchedAt", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt64() : 0;
            return (matches, tournaments, fetched);
        }
    }

    /// Récupère les matchs : cache Nur Live s'il est récent, sinon Liquipedia (≥ 31 s entre deux pages).
    public class M8Service
    {
        readonly string cachePath;
        readonly string[] nurLiveCaches;
        public List<M8Match> Matches { get; private set; } = new();
        public List<M8Tournament> Tournaments { get; private set; } = new();
        public long FetchedAtMs { get; private set; }
        public string Source { get; private set; } = "";
        public bool Loading { get; private set; }
        public List<string> Errors { get; } = new();
        public event Action Updated;
        public Func<string, Task<HttpResponseMessage>> Fetch; // remplaçable dans les tests
        public int GapMs = M8.MinGapMs;
        public int QueryGapMs = 2500;          // requêtes simples (hors « parse ») : 1 toutes les 2 s max
        public int MaxTournamentParses = 6;    // pages de tournoi lues par actualisation
        long lastParseAt, lastQueryAt;
        bool rateLimited;
        // Matchs trouvés sur les pages de tournoi (Coupe de France, ligues…) : clé « jeu|titre »
        readonly Dictionary<string, List<M8Match>> tourMatches = new();
        readonly Dictionary<string, long> tourChecked = new();
        string TourPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cachePath)) ?? ".", Path.GetFileNameWithoutExtension(cachePath) + "-tournois.json");

        /// Matchs venant des pages de tournoi.
        public List<M8Match> Extra => tourMatches.Values.SelectMany(x => x).ToList();

        public M8Service(string cachePath, params string[] nurLiveCaches)
        {
            this.cachePath = cachePath;
            this.nurLiveCaches = nurLiveCaches;
            LoadTour();
            try
            {
                var c = M8.ReadNurLiveCache(cachePath); // même format que Nur Live
                if (c != null) { Matches = c.Value.Matches; Tournaments = c.Value.Tournaments; FetchedAtMs = c.Value.FetchedAt; Source = "cache"; }
            }
            catch { }
        }

        public bool IsStale(long nowMs) => nowMs - FetchedAtMs > 60 * 60 * 1000;

        /// Matchs à jour à l'instant t.
        public List<M8Match> Current(long nowMs) => M8.RefreshStatus(Merge(Matches, Extra), nowMs).OrderBy(m => m.Ts).ToList();

        /// Fusion page d'équipe + pages de tournoi, sans doublon (même jeu, même heure, même adversaire).
        public static List<M8Match> Merge(IEnumerable<M8Match> team, IEnumerable<M8Match> extra)
        {
            static string Key(M8Match m) => m.Game + "|" + m.Ts + "|" + System.Text.RegularExpressions.Regex.Replace((m.OpponentShort ?? m.Opponent ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
            var outp = new List<M8Match>();
            var ids = new HashSet<string>();
            var keys = new HashSet<string>();
            foreach (var m in (team ?? Enumerable.Empty<M8Match>()).Concat(extra ?? Enumerable.Empty<M8Match>()))
            {
                if (m == null || !ids.Add(m.Id ?? "") || !keys.Add(Key(m))) continue;
                outp.Add(m);
            }
            return outp;
        }

        public async Task RefreshAsync(bool force = false)
        {
            if (Loading) return;
            Loading = true;
            Updated?.Invoke();
            try
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                rateLimited = false;
                bool fromNurLive = false;
                // 1) Nur Live a-t-il des données récentes ?
                foreach (var p in nurLiveCaches ?? Array.Empty<string>())
                {
                    try
                    {
                        var c = M8.ReadNurLiveCache(p);
                        if (c != null && c.Value.FetchedAt > FetchedAtMs && (force ? nowMs - c.Value.FetchedAt < 10 * 60_000 : nowMs - c.Value.FetchedAt < 60 * 60_000))
                        {
                            Matches = c.Value.Matches; Tournaments = c.Value.Tournaments; FetchedAtMs = c.Value.FetchedAt; Source = "Nur Live";
                            Save();
                            fromNurLive = true;
                            break;
                        }
                    }
                    catch { }
                }
                // 2) Sinon Liquipedia
                if (!fromNurLive) await FetchAllAsync();
                // 3) Tournois absents de la page d'équipe (Coupe de France…)
                if (!rateLimited)
                {
                    Updated?.Invoke();
                    await RefreshTournamentsAsync();
                }
            }
            finally { Loading = false; Updated?.Invoke(); }
        }

        async Task FetchAllAsync()
        {
            using var http = Net.Client(TimeSpan.FromSeconds(30));
            var fetch = Fetch ?? (u => http.GetAsync(u));
            var matches = new List<M8Match>();
            var tournaments = new List<M8Tournament>();
            var failed = new HashSet<string>();
            Errors.Clear();
            for (int i = 0; i < M8.Pages.Length; i++)
            {
                var p = M8.Pages[i];
                long wait = lastParseAt + GapMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (GapMs > 0 && wait > 0) await Task.Delay((int)wait);
                lastParseAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                try
                {
                    var res = await fetch(M8.ApiUrl(p));
                    if ((int)res.StatusCode == 429)
                    {
                        foreach (var q in M8.Pages.Skip(i)) { failed.Add(q.Game); Errors.Add(q.Game + " : limite Liquipedia (429)"); }
                        rateLimited = true;
                        break; // on réessaiera plus tard
                    }
                    res.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("error", out var err)) throw new Exception(err.ToString());
                    var html = doc.RootElement.GetProperty("parse").GetProperty("text").GetProperty("*").GetString() ?? "";
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    matches.AddRange(M8.ParseTeamPage(html, p.Game, p.Wiki, p.Page, now));
                    tournaments.AddRange(M8.ParseUpcomingTournaments(html, p.Game, p.Wiki));
                }
                catch (Exception e) { failed.Add(p.Game); Errors.Add(p.Game + " : " + e.Message); }
            }
            if (failed.Count == M8.Pages.Length) return; // tout a échoué : on garde l'ancien cache
            // Un jeu en erreur garde ses anciens matchs
            Matches = matches.Concat(Matches.Where(m => failed.Contains(m.Game))).OrderBy(m => m.Ts).ToList();
            Tournaments = tournaments.Concat(Tournaments.Where(t => failed.Contains(t.Game))).ToList();
            FetchedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Source = "Liquipedia";
            Save();
        }

        async Task WaitGap(bool parse)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long wait = parse ? lastParseAt + GapMs - now : lastQueryAt + QueryGapMs - now;
            if (wait > 0 && (parse ? GapMs : QueryGapMs) > 0) await Task.Delay((int)wait);
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lastQueryAt = now;
            if (parse) lastParseAt = now;
        }

        async Task RefreshTournamentsAsync()
        {
            using var http = Net.Client(TimeSpan.FromSeconds(30));
            var fetch = Fetch ?? (u => http.GetAsync(u));
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var queue = new List<(M8Page Page, string Title, bool Known)>();
            var queuedTitles = new HashSet<string>();
            foreach (var p in M8.Pages)
            {
                try
                {
                    await WaitGap(parse: false);
                    var res = await fetch(M8.BacklinksUrl(p));
                    if ((int)res.StatusCode == 429) { rateLimited = true; break; }
                    if (!res.IsSuccessStatusCode) continue;
                    foreach (var title in M8.TournamentCandidates(await res.Content.ReadAsStringAsync(), DateTime.UtcNow))
                    {
                        if (!queuedTitles.Add(p.Wiki + "|" + title)) continue; // même wiki (Valorant / GC)
                        var key = p.Game + "|" + title;
                        bool known = tourMatches.TryGetValue(key, out var l) && l.Any(m => m.Ts * 1000 > nowMs - 4 * 3600_000L);
                        long last = tourChecked.TryGetValue(key, out var t) ? t : 0;
                        if (nowMs - last > (known ? 30 * 60_000L : 24 * 3600_000L)) queue.Add((p, title, known));
                    }
                }
                catch { }
            }
            int done = 0;
            foreach (var q in M8.FairOrder(queue))
            {
                if (rateLimited || done >= MaxTournamentParses) break;
                var key = q.Page.Game + "|" + q.Title;
                try
                {
                    await WaitGap(parse: true);
                    done++;
                    var res = await fetch(M8.ParseUrl(q.Page.Wiki, q.Title));
                    if ((int)res.StatusCode == 429) { rateLimited = true; break; }
                    res.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                    var html = doc.RootElement.GetProperty("parse").GetProperty("text").GetProperty("*").GetString() ?? "";
                    var found = M8.ParseTournamentPage(html, q.Page.Game, q.Page.Wiki, q.Title, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (found.Count > 0) tourMatches[key] = found; else tourMatches.Remove(key);
                    tourChecked[key] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (found.Count > 0) Updated?.Invoke();
                }
                catch { tourChecked[key] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
            }
            // ménage : tournois terminés, vérifications de plus de 3 jours
            foreach (var k in tourMatches.Where(kv => !kv.Value.Any(m => m.Ts * 1000 > nowMs - 4 * 3600_000L)).Select(kv => kv.Key).ToList()) tourMatches.Remove(k);
            foreach (var k in tourChecked.Where(kv => nowMs - kv.Value > 3 * 86400_000L).Select(kv => kv.Key).ToList()) tourChecked.Remove(k);
            SaveTour();
        }

        void LoadTour()
        {
            try
            {
                if (!File.Exists(TourPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(TourPath));
                if (doc.RootElement.TryGetProperty("matches", out var ms))
                    foreach (var kv in ms.EnumerateObject())
                        tourMatches[kv.Name] = JsonSerializer.Deserialize<List<M8Match>>(kv.Value.GetRawText()) ?? new();
                if (doc.RootElement.TryGetProperty("checked", out var ck))
                    foreach (var kv in ck.EnumerateObject())
                        if (kv.Value.TryGetInt64(out var v)) tourChecked[kv.Name] = v;
            }
            catch { }
        }

        void SaveTour()
        {
            try { File.WriteAllText(TourPath, JsonSerializer.Serialize(new { matches = tourMatches, @checked = tourChecked })); } catch { }
        }

        void Save()
        {
            try
            {
                File.WriteAllText(cachePath, JsonSerializer.Serialize(new { matches = Matches, tournaments = Tournaments, fetchedAt = FetchedAtMs }));
            }
            catch { }
        }
    }

    // ==================================================================
    //  Chaînes (Twitch / Kick / YouTube) et statut en live
    // ==================================================================
    public class Channel
    {
        [JsonPropertyName("platform")] public string Platform { get; set; } = "twitch";
        [JsonPropertyName("key")] public string Key { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; }
        [JsonPropertyName("fav")] public bool Fav { get; set; }
        [JsonPropertyName("notify")] public bool Notify { get; set; }
        [JsonIgnore] public string Id => Platform + ":" + Key;
    }

    public class LiveInfo
    {
        public bool? Live { get; set; }
        public string Name { get; set; }
        public string Avatar { get; set; }
        public string Title { get; set; }
        public string Game { get; set; }
        public int Viewers { get; set; }
    }

    public static class Channels
    {
        public static readonly (string Key, string Label)[] Defaults =
        {
            ("gentlemates", "Gentle Mates"), ("gotaga", "Gotaga"), ("squeezie", "Squeezie"),
            ("brawks", "Brawks"), ("kamet0", "Kameto"), ("zerator", "ZeratoR"),
        };

        static readonly Regex TwitchLoginRe = new("^[a-z0-9_]{3,25}$");

        public static string TwitchLogin(string raw)
        {
            var s = (raw ?? "").Trim().ToLowerInvariant();
            var m = Regex.Match(s, @"twitch\.tv/(?:popout/)?([a-z0-9_]{3,25})");
            if (m.Success) s = m.Groups[1].Value;
            s = s.TrimStart('@');
            return TwitchLoginRe.IsMatch(s) && s != "embed" && s != "directory" && s != "videos" ? s : null;
        }

        public static string KickSlug(string raw)
        {
            var s = (raw ?? "").Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"^https?://", "");
            s = Regex.Replace(s, @"^(www\.)?kick\.com/", "");
            s = s.Split('/', '?', '#')[0];
            return Regex.IsMatch(s, "^[a-z0-9_-]{2,40}$") ? s : null;
        }

        /// Comprend « gotaga », « twitch.tv/gotaga », « kick.com/x », « kick:x », un lien YouTube…
        public static Channel Parse(string input)
        {
            var s = (input ?? "").Trim();
            if (s.Length == 0) return null;
            var pref = Regex.Match(s, @"^(twitch|kick|youtube|yt)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
            var lower = s.ToLowerInvariant();
            if (pref.Success && pref.Groups[1].Value.ToLowerInvariant() == "kick" || lower.Contains("kick.com"))
            {
                var slug = KickSlug(pref.Success ? pref.Groups[2].Value : s);
                return slug == null ? null : new Channel { Platform = "kick", Key = slug, Label = slug };
            }
            if (pref.Success && pref.Groups[1].Value.ToLowerInvariant() is "youtube" or "yt" || lower.Contains("youtube.com") || lower.Contains("youtu.be"))
            {
                var v = Regex.Match(s, @"(?:v=|youtu\.be/|/live/|/embed/)([\w-]{11})");
                if (v.Success) return new Channel { Platform = "youtube", Key = "v_" + v.Groups[1].Value, Label = "YouTube" };
                var c = Regex.Match(s, @"(UC[\w-]{20,})");
                if (c.Success) return new Channel { Platform = "youtube", Key = "c_" + c.Groups[1].Value, Label = "YouTube" };
                return null;
            }
            var login = TwitchLogin(pref.Success ? pref.Groups[2].Value : s);
            return login == null ? null : new Channel { Platform = "twitch", Key = login, Label = login };
        }

        public static List<Channel> DefaultList() =>
            Defaults.Select(d => new Channel { Platform = "twitch", Key = d.Key, Label = d.Label, Fav = d.Key == "gentlemates", Notify = d.Key == "gentlemates" }).ToList();

        /// Reprend les chaînes de Nur Live (nur-live-v3.json → sources).
        public static List<Channel> ReadNurLive(string path)
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("sources", out var src) || src.ValueKind != JsonValueKind.Array) return null;
            var list = new List<Channel>();
            foreach (var e in src.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var platform = e.TryGetProperty("platform", out var p) ? p.GetString() : "twitch";
                var key = e.TryGetProperty("key", out var k) ? k.GetString() : null;
                if (string.IsNullOrWhiteSpace(key) || platform is not ("twitch" or "kick" or "youtube")) continue;
                list.Add(new Channel
                {
                    Platform = platform, Key = key,
                    Label = e.TryGetProperty("label", out var l) ? l.GetString() ?? key : key,
                    Fav = e.TryGetProperty("fav", out var f) && f.ValueKind == JsonValueKind.True,
                    Notify = e.TryGetProperty("notify", out var n) && n.ValueKind == JsonValueKind.True,
                });
            }
            return list.GroupBy(c => c.Id).Select(g => g.First()).ToList();
        }

        /// Adresse du lecteur intégré (parent = domaine de la page qui l'affiche).
        public static string PlayerUrl(Channel c, string parent, bool muted) => c.Platform switch
        {
            "kick" => $"https://player.kick.com/{Uri.EscapeDataString(c.Key)}?muted={(muted ? "true" : "false")}&autoplay=true",
            "youtube" when c.Key.StartsWith("v_") => $"https://www.youtube.com/embed/{c.Key[2..]}?autoplay=1&mute={(muted ? 1 : 0)}",
            "youtube" => $"https://www.youtube.com/embed/live_stream?channel={c.Key[2..]}&autoplay=1&mute={(muted ? 1 : 0)}",
            _ => $"https://player.twitch.tv/?channel={c.Key}&parent={parent}&muted={(muted ? "true" : "false")}&autoplay=true",
        };

        public static string ChatUrl(Channel c, string parent, bool dark) => c.Platform switch
        {
            "kick" => $"https://kick.com/popout/{Uri.EscapeDataString(c.Key)}/chat",
            "youtube" when c.Key.StartsWith("v_") => $"https://www.youtube.com/live_chat?v={c.Key[2..]}&embed_domain={parent}",
            "youtube" => null,
            _ => $"https://www.twitch.tv/embed/{c.Key}/chat?parent={parent}" + (dark ? "&darkpopout" : ""),
        };

        public static string PageUrl(Channel c) => c.Platform switch
        {
            "kick" => $"https://kick.com/{c.Key}",
            "youtube" when c.Key.StartsWith("v_") => $"https://www.youtube.com/watch?v={c.Key[2..]}",
            "youtube" => $"https://www.youtube.com/channel/{c.Key[2..]}/live",
            _ => $"https://www.twitch.tv/{c.Key}",
        };
    }

    /// Suggestion de chaîne (même forme que la recherche de Nur Live).
    public class SearchHit
    {
        [JsonPropertyName("platform")] public string Platform { get; set; } = "twitch";
        [JsonPropertyName("key")] public string Key { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("avatar")] public string Avatar { get; set; }
        [JsonPropertyName("live")] public bool Live { get; set; }
        [JsonPropertyName("viewers")] public int Viewers { get; set; }
        [JsonPropertyName("game")] public string Game { get; set; }
        [JsonPropertyName("followers")] public int Followers { get; set; }
    }

    /// Recherche de chaînes Twitch : le nom tapé + une liste de streameurs connus (FR, M8, esport),
    /// vérifiés et complétés via DecAPI (avatar, live, viewers, abonnés). Les noms inexistants sont écartés.
    public static class ChannelSearch
    {
        public static readonly string[] Known =
        {
            "gentlemates", "gotaga", "squeezie", "brawks", "kamet0", "zerator", "mistermv", "domingo", "ponce",
            "antoinedaniel", "inoxtag", "michou", "locklear", "joueur_du_grenier", "sardoche", "rivenzi", "aminematue",
            "kaatsup", "maghla", "baghera", "gom4rt", "etoiles", "deujna", "solary", "jltomy", "tonton", "chowh1",
            "kennystream", "lebouseuh", "ultia", "byilhann", "doigby", "fukano", "skyyart", "pfut", "mynthos",
            "wankilstudio", "laink", "terracid", "jeelpatate", "horty", "billy", "cinna", "maxildan", "rebeudeter",
            "lestream", "karminecorp", "vitality", "valorant", "valorant_emea", "valorant_fr", "callofduty",
            "rocketleague", "fortnite", "ageofempires", "riotgames", "otplol_", "zackrawrr", "ibai", "kaicenat",
            "xqc", "tarik", "shroud", "tenz", "nadeshot", "scump", "aceu", "tfue", "bugha", "mongraal", "lirik",
        };

        /// Candidats triés : nom exact tapé, puis noms qui commencent par la saisie, puis qui la contiennent.
        public static List<string> Candidates(string query, IEnumerable<string> extra = null, int max = 5)
        {
            var q = Channels.TwitchLogin(query) ?? Regex.Replace((query ?? "").Trim().ToLowerInvariant(), "[^a-z0-9_]", "");
            if (q.Length < 2) return new List<string>();
            var pool = Known.Concat(extra ?? Enumerable.Empty<string>()).Select(x => (x ?? "").ToLowerInvariant())
                .Where(x => x.Length > 0).Distinct().ToList();
            var outp = new List<string>();
            if (Regex.IsMatch(q, "^[a-z0-9_]{3,25}$")) outp.Add(q);
            outp.AddRange(pool.Where(x => x != q && x.StartsWith(q)).OrderBy(x => x.Length));
            outp.AddRange(pool.Where(x => x != q && !x.StartsWith(q) && x.Contains(q)).OrderBy(x => x.Length));
            return outp.Distinct().Take(max).ToList();
        }

        static readonly Dictionary<string, (DateTime At, SearchHit Hit)> Cache = new();
        const string DecApi = "https://decapi.me/twitch/";

        static async Task<string> Get(HttpClient http, string path)
        {
            try { return (await http.GetStringAsync(DecApi + path)).Trim(); } catch { return null; }
        }

        static async Task<SearchHit> One(HttpClient http, string login)
        {
            lock (Cache)
                if (Cache.TryGetValue(login, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(5)) return c.Hit;
            var avatar = await Get(http, "avatar/" + login);
            SearchHit hit = null;
            if (avatar != null && avatar.StartsWith("https://"))
            {
                hit = new SearchHit { Key = login, Name = login, Avatar = avatar };
                var live = LiveStatus.ParseUptime(await Get(http, "uptime/" + login)) == true;
                hit.Live = live;
                if (live)
                {
                    int.TryParse(await Get(http, "viewercount/" + login), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
                    hit.Viewers = v;
                    hit.Game = await Get(http, "game/" + login) ?? "";
                }
                else
                {
                    int.TryParse(await Get(http, "followcount/" + login), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f);
                    hit.Followers = f;
                }
            }
            else if (avatar == null) return null; // réseau : on ne met pas en cache
            lock (Cache) Cache[login] = (DateTime.UtcNow, hit);
            return hit;
        }

        /// Lit la réponse de la recherche Twitch (même requête que l'appli Nur Live) : en live d'abord, puis les plus suivis.
        public static List<SearchHit> ParseSearch(string json)
        {
            var hits = new List<SearchHit>();
            using var doc = JsonDocument.Parse(json);
            var edges = doc.RootElement.GetProperty("data").GetProperty("searchUsers").GetProperty("edges");
            foreach (var edge in edges.EnumerateArray())
            {
                if (!edge.TryGetProperty("node", out var n) || n.ValueKind != JsonValueKind.Object) continue;
                var login = n.GetProperty("login").GetString();
                if (string.IsNullOrEmpty(login)) continue;
                var hit = new SearchHit { Key = login, Name = n.TryGetProperty("displayName", out var d) && d.GetString() is string dn && dn.Length > 0 ? dn : login };
                if (n.TryGetProperty("profileImageURL", out var a)) hit.Avatar = a.GetString() ?? "";
                if (n.TryGetProperty("followers", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty("totalCount", out var c) && c.ValueKind == JsonValueKind.Number) hit.Followers = c.GetInt32();
                if (n.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.Object)
                {
                    hit.Live = true;
                    if (s.TryGetProperty("viewersCount", out var v) && v.ValueKind == JsonValueKind.Number) hit.Viewers = v.GetInt32();
                    if (s.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.Object && g.TryGetProperty("displayName", out var gn)) hit.Game = gn.GetString() ?? "";
                }
                hits.Add(hit);
            }
            return hits.OrderByDescending(h => h.Live).ThenByDescending(h => h.Followers).ToList();
        }

        /// Vraie recherche de chaînes sur Twitch. Liste vide si Twitch ne répond pas.
        static async Task<List<SearchHit>> TwitchSearch(string q)
        {
            try
            {
                using var http = Net.Client(TimeSpan.FromSeconds(5));
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql");
                req.Headers.Add("Client-Id", "kimne78kx3ncx6brgo4mv6wki5h1ko"); // identifiant public du site Twitch, comme dans core/status.js de Nur Live
                req.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    query = "query NurSearch($q:String!){ searchUsers(userQuery:$q, first:8){ edges { node { login displayName profileImageURL(width:50) followers { totalCount } stream { viewersCount game { displayName } } } } } }",
                    variables = new { q },
                }), Encoding.UTF8, "application/json");
                using var res = await http.SendAsync(req);
                return res.IsSuccessStatusCode ? ParseSearch(await res.Content.ReadAsStringAsync()) : new List<SearchHit>();
            }
            catch { return new List<SearchHit>(); }
        }

        public static async Task<List<SearchHit>> SearchAsync(string query, IEnumerable<string> extra = null)
        {
            var typed = (Channels.TwitchLogin(query) ?? query ?? "").Trim();
            if (typed.Length >= 2)
            {
                var found = await TwitchSearch(typed);
                if (found.Count > 0) return found;
            }
            // Twitch injoignable : les streameurs connus, vérifiés un par un
            var cands = Candidates(query, extra);
            if (cands.Count == 0) return new List<SearchHit>();
            using var http = Net.Client(TimeSpan.FromSeconds(8));
            var res = await Task.WhenAll(cands.Select(c => One(http, c)));
            // en live d'abord, puis l'ordre de pertinence
            return res.Select((h, i) => (h, i)).Where(x => x.h != null)
                .OrderByDescending(x => x.h.Live).ThenBy(x => x.i).Select(x => x.h).ToList();
        }
    }

    public static class LiveStatus
    {
        // Statut Twitch via DecAPI (service public : https://decapi.me), sans identifiant ni clé.
        const string DecApi = "https://decapi.me/twitch/";

        /// Réponse de /uptime : « x is offline » → false ; une durée → true ; autre chose (erreur) → null.
        public static bool? ParseUptime(string text)
        {
            var t = (text ?? "").Trim();
            if (t.Length == 0) return null;
            if (t.EndsWith(" is offline", StringComparison.OrdinalIgnoreCase)) return false;
            if (Regex.IsMatch(t, @"^\d+\s+(second|minute|hour|day)", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(t, "not found|does not exist|no user", RegexOptions.IgnoreCase)) return false;
            return null;
        }

        static readonly Dictionary<string, string> AvatarCache = new();

        static async Task<string> Get(HttpClient http, string path)
        {
            try { return (await http.GetStringAsync(DecApi + path)).Trim(); } catch { return null; }
        }

        static async Task<LiveInfo> TwitchOne(HttpClient http, string login)
        {
            var info = new LiveInfo { Live = ParseUptime(await Get(http, "uptime/" + login)), Name = login };
            if (!AvatarCache.TryGetValue(login, out var av))
            {
                av = await Get(http, "avatar/" + login);
                if (av != null && av.StartsWith("https://")) AvatarCache[login] = av; else av = "";
            }
            info.Avatar = av;
            if (info.Live == true)
            {
                info.Title = await Get(http, "title/" + login) ?? "";
                info.Game = await Get(http, "game/" + login) ?? "";
                int.TryParse(await Get(http, "viewercount/" + login), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
                info.Viewers = v;
            }
            return info;
        }

        public static async Task<Dictionary<string, LiveInfo>> FetchAsync(IEnumerable<Channel> channels)
        {
            var outp = new Dictionary<string, LiveInfo>();
            using var http = Net.Client(TimeSpan.FromSeconds(12));
            var list = channels.GroupBy(c => c.Id).Select(g => g.First()).ToList();
            // 4 chaînes à la fois pour rester léger
            foreach (var batch in list.Where(c => c.Platform == "twitch").Chunk(4))
            {
                var res = await Task.WhenAll(batch.Select(c => TwitchOne(http, c.Key)));
                for (int i = 0; i < batch.Length; i++) outp[batch[i].Id] = res[i];
            }
            foreach (var c in list.Where(c => c.Platform == "kick"))
            {
                try
                {
                    var j = await http.GetStringAsync("https://kick.com/api/v2/channels/" + Uri.EscapeDataString(c.Key));
                    using var doc = JsonDocument.Parse(j);
                    var r = doc.RootElement;
                    var ls = r.TryGetProperty("livestream", out var l) ? l : default;
                    bool live = ls.ValueKind == JsonValueKind.Object;
                    outp[c.Id] = new LiveInfo
                    {
                        Live = live,
                        Name = r.TryGetProperty("user", out var u) && u.TryGetProperty("username", out var un) ? un.GetString() : c.Key,
                        Avatar = r.TryGetProperty("user", out var u2) && u2.TryGetProperty("profile_pic", out var pp) ? pp.GetString() : "",
                        Title = live && ls.TryGetProperty("session_title", out var t) ? t.GetString() : "",
                        Viewers = live && ls.TryGetProperty("viewer_count", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0,
                    };
                }
                catch { outp[c.Id] = new LiveInfo { Live = null, Name = c.Key }; }
            }
            foreach (var c in list.Where(c => c.Platform == "youtube")) outp[c.Id] = new LiveInfo { Live = null, Name = c.Label };
            return outp;
        }

        /// Chaînes passées hors ligne → en live entre deux relevés.
        public static List<string> WentLive(IDictionary<string, LiveInfo> prev, IDictionary<string, LiveInfo> next) =>
            next.Where(kv => kv.Value.Live == true && prev != null && prev.TryGetValue(kv.Key, out var p) && p.Live == false)
                .Select(kv => kv.Key).ToList();

        /// « Regarder » : co-diffusions M8 en live, sinon une chaîne suivie qui parle de M8 sur ce jeu, sinon l'officielle.
        public static List<string> PickStreams(string game, IEnumerable<string> official, string coStreams,
            IEnumerable<string> own, IDictionary<string, LiveInfo> st)
        {
            var wanted = (coStreams ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 0).ToList();
            bool Live(string k) => st != null && st.TryGetValue("twitch:" + k, out var i) && i.Live == true;
            var label = M8.Info(game).Label.Split(' ')[0].ToLowerInvariant();
            var picks = wanted.Where(Live).ToList();
            if (picks.Count == 0)
                picks = (own ?? Enumerable.Empty<string>()).Where(k =>
                    st != null && st.TryGetValue("twitch:" + k, out var i) && i.Live == true &&
                    Regex.IsMatch(i.Title ?? "", @"\bm8\b|gentle ?mates", RegexOptions.IgnoreCase) &&
                    (i.Game ?? "").ToLowerInvariant().Contains(label)).ToList();
            var off = (official ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrEmpty(x)).ToList();
            var chosen = picks.Count > 0 ? picks.Take(2).ToList() : off.Take(1).ToList();
            return chosen.Count > 0 ? chosen : wanted.Take(1).ToList();
        }
    }

    // ==================================================================
    //  Nur Live : interface chargée depuis le dossier de l'appli, branchée sur le navigateur
    // ==================================================================
    public static class NurLive
    {
        public const string Host = "nurlive.home";
        public const string Url = "https://nurlive.home/index.html";
        const string ParentsLine = "const PARENTS = [\"127.0.0.1\", \"localhost\"];";

        /// Dossiers où chercher Nur Live (réglage, Bureau, Documents, OneDrive, dossier perso).
        public static IEnumerable<string> Candidates(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var bases = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(home, "Desktop"), Path.Combine(home, "OneDrive", "Desktop"), Path.Combine(home, "OneDrive", "Bureau"),
                home,
            };
            foreach (var b in bases.Where(x => !string.IsNullOrEmpty(x)).Distinct())
                foreach (var name in new[] { "nur-live", "Nur Live", "nurlive" })
                    yield return Path.Combine(b, name);
        }

        public static bool IsNurLive(string dir) =>
            !string.IsNullOrEmpty(dir) &&
            File.Exists(Path.Combine(dir, "ui", "index.html")) && File.Exists(Path.Combine(dir, "ui", "js", "app.js")) &&
            File.Exists(Path.Combine(dir, "core", "model.js")) && File.Exists(Path.Combine(dir, "core", "sources.js"));

        public static string FindFolder(IEnumerable<string> candidates) => candidates.FirstOrDefault(IsNurLive);

        /// Les lecteurs Twitch n'acceptent que l'adresse de la page qui les affiche : on autorise la nôtre.
        public static string PatchApp(string appJs)
        {
            if (appJs.Contains(ParentsLine)) return appJs.Replace(ParentsLine, "const PARENTS = [location.hostname];");
            return Regex.Replace(appJs, @"const PARENTS = \[[^\]]*\];", "const PARENTS = [location.hostname];");
        }

        /// Copie l'interface de Nur Live dans le dossier du navigateur (rafraîchie à chaque démarrage).
        public static void Prepare(string src, string dst)
        {
            var ui = Path.Combine(src, "ui");
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            foreach (var file in Directory.GetFiles(ui, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(ui, file);
                if (rel.StartsWith("node_modules")) continue;
                var target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
            var app = Path.Combine(dst, "js", "app.js");
            File.WriteAllText(app, PatchApp(File.ReadAllText(app)));
        }

        /// Recopie seulement les fichiers changés (sans tout effacer : la page ouverte continue d'être servie).
        /// Renvoie le nombre de fichiers mis à jour.
        public static int Sync(string src, string dst)
        {
            var ui = Path.Combine(src, "ui");
            int n = 0;
            foreach (var file in Directory.GetFiles(ui, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(ui, file);
                if (rel.StartsWith("node_modules")) continue;
                var target = Path.Combine(dst, rel);
                bool isApp = rel.Replace('\\', '/') == "js/app.js";
                var fresh = isApp ? PatchApp(File.ReadAllText(file)) : null;
                if (File.Exists(target))
                {
                    if (isApp ? File.ReadAllText(target) == fresh
                              : new FileInfo(target).Length == new FileInfo(file).Length && File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(file))) continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (isApp) File.WriteAllText(target, fresh); else File.Copy(file, target, true);
                n++;
            }
            return n;
        }

        // ---------- Surveillance des lecteurs (même sonde que l'appli Nur Live, voir main.js) ----------

        /// twitch / kick / youtube si l'adresse est celle d'un lecteur intégré, sinon "".
        public static string PlayerKind(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
            var h = u.Host.ToLowerInvariant();
            if (h == "player.twitch.tv") return "twitch";
            if (h == "player.kick.com" && !Regex.IsMatch(u.AbsolutePath, @"/chatroom/?$", RegexOptions.IgnoreCase)) return "kick";
            if ((h.EndsWith("youtube.com") || h.EndsWith("youtube-nocookie.com")) && u.AbsolutePath.StartsWith("/embed/")) return "youtube";
            return "";
        }

        /// Identifiant de chaîne d'un lecteur (twitch:nom), "" si inconnu.
        public static string ChannelOf(string kind, string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
            if (kind == "twitch")
            {
                var m = Regex.Match(u.Query, @"[?&]channel=([^&]*)");
                return m.Success ? "twitch:" + Uri.UnescapeDataString(m.Groups[1].Value).ToLowerInvariant() : "";
            }
            if (kind == "kick")
            {
                var first = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return first != null ? "kick:" + first.ToLowerInvariant() : "";
            }
            return "";
        }

        /// Script exécuté dans le lecteur : état de la vidéo, pub en cours, relance douce si demandée.
        public static string ProbeScript(bool resume, bool mute = false) =>
            ProbeTemplate.Replace("__RESUME__", resume ? "true" : "false").Replace("__MUTE__", mute ? "true" : "false");

        /// Mouchard des lecteurs de Nur Live (diagnostic des pubs figées) : qui met la vidéo en pause, ce que le moteur refuse,
        /// si quelque chose recouvre le lecteur (IntersectionObserver v2). Lu et vidé par la sonde, écrit dans nurlive.log.
        public const string SpyScript = """
            (() => {
              if (location.hostname !== "player.twitch.tv" || !(location.ancestorOrigins[0] || "").includes("nurlive.home")) return;
              const ev = window.__spy = [];
              const push = (s) => { if (ev.length < 40) ev.push(Math.round(performance.now() / 100) / 10 + "s " + s); };
              const stack = () => (new Error().stack || "").split("\n").slice(3, 5).map((l) => l.trim().slice(0, 60)).join(" < ");
              const pause = HTMLMediaElement.prototype.pause;
              HTMLMediaElement.prototype.pause = function () { push("pause() demandé t=" + this.currentTime.toFixed(1) + " par " + stack()); return pause.apply(this, arguments); };
              const play = HTMLMediaElement.prototype.play;
              HTMLMediaElement.prototype.play = function () { const r = play.apply(this, arguments); const m = this.muted; if (r && r.catch) r.catch((e) => push("play() refusé muet=" + m + " : " + e.name)); return r; };
              for (const k of ["warn", "error", "info", "log"]) {
                const c = console[k];
                console[k] = function (...x) { const s = x.map(String).join(" "); if (/autoplay|visibility|requirement|adblock|ad block/i.test(s)) push("console : " + s.replace(/\s+/g, " ").slice(0, 170)); return c.apply(this, x); };
              }
              for (const n of ["pause", "emptied", "stalled", "error"])
                document.addEventListener(n, (e) => { const v = e.target; if (v.tagName === "VIDEO") push(n + " t=" + v.currentTime.toFixed(1) + " w=" + v.videoWidth + " page=" + document.visibilityState + " focus=" + document.hasFocus() + (v.error ? " " + v.error.message : "")); }, true);
              let seen = null, shown = null;
              setInterval(() => {
                const v = document.querySelector("video");
                if (!v || v === seen || !window.IntersectionObserver) return;
                seen = v;
                try {
                  new IntersectionObserver((list) => {
                    const e = list[list.length - 1];
                    if (e.isVisible !== shown) { shown = e.isVisible; push("lecteur " + (e.isVisible ? "visible" : "RECOUVERT") + " (" + Math.round(e.intersectionRatio * 100) + " % à l'écran)"); }
                  }, { threshold: [0, 1], trackVisibility: true, delay: 200 }).observe(v);
                } catch (_) { }
              }, 1000);
            })();
            """;

        const string ProbeTemplate = """
            (() => {
              try {
                const RESUME = __RESUME__;
                const MUTE = __MUTE__; // ce live ne doit pas s'entendre (ex. son remis par l'écran « contenu mature »)
                const vids = Array.from(document.querySelectorAll("video"));
                const v = vids.slice().sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];
                const gate = document.querySelector('[data-a-target="content-classification-gate-overlay-start-watching-button"],[data-a-target="player-overlay-mature-accept"],[data-a-target="player-overlay-content-gate-button"]');
                let clicked = false;
                if (gate) { gate.click(); clicked = true; }
                const txt = (document.body && document.body.innerText || "").slice(0, 4000);
                let errCode = 0;
                const m = /(?:Erreur|Error)\s*#?\s*\(?(\d{4})/i.exec(txt);
                if (m) errCode = Number(m[1]);
                // Jamais le simple mot « Publicité » : c'est aussi le label des streams sponsorisés et il peut être dans un titre
                const ad = !!document.querySelector('[data-a-target="video-ad-label"],[data-a-target="video-ad-countdown"],.video-ad-label') ||
                  /Commercial break|after this ad|après cette publicit/i.test(txt);
                const vis = document.visibilityState;
                const brief = txt.replace(/\s+/g, " ").slice(0, 90);
                let resumed = 0;
                if (RESUME) for (const x of (ad ? vids : (v ? [v] : []))) {
                  if (x.paused && !x.ended && x.readyState >= 2) {
                    const p = x.play();
                    if (p && p.catch) p.catch(() => { x.muted = true; const q = x.play(); if (q && q.catch) q.catch(() => {}); });
                    resumed++;
                  }
                }
                const tAll = vids.reduce((n, x) => n + (x.currentTime || 0), 0);
                const vinfo = vids.map((x) => [x.videoWidth, x.videoHeight, Math.round((x.currentTime || 0) * 10) / 10, x.paused ? 1 : 0, x.readyState, (x.error && x.error.code) || 0]);
                if (MUTE) for (const x of vids) x.muted = true;
                const spy = window.__spy ? window.__spy.splice(0) : [];
                if (!v) return { video: false, clicked, errCode, ad, tAll, vinfo, vis, brief, spy };
                return { spy, vis, brief, vinfo, video: true, muted: !!v.muted, t: v.currentTime || 0, tAll, paused: !!v.paused, ended: !!v.ended, ready: v.readyState || 0, err: v.error ? v.error.code : 0, w: v.videoWidth || 0, clicked, errCode, ad, resumed };
              } catch (e) { return { video: false, fail: String(e && e.message || e) }; }
            })()
            """;

        /// Pont « window.nur » avec les modules de Nur Live (sources, modèle) insérés tels quels.
        public static string Bridge(string template, string sourcesJs, string modelJs) =>
            template.Replace("/*__SOURCES__*/", sourcesJs).Replace("/*__MODEL__*/", modelJs);
    }
}
