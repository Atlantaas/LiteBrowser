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
                .Select(m => Clone(m, m.Status == "finished" ? "finished" : m.Ts > now ? "upcoming" : m.Ts > now - LiveWindowS ? "live" : "finished"))
                .Where(m => m.Ts > now - LiveWindowS).ToList();
        }

        static M8Match Clone(M8Match m, string status) => new()
        {
            Id = m.Id, Game = m.Game, Ts = m.Ts, Opponent = m.Opponent, OpponentShort = m.OpponentShort,
            Tournament = m.Tournament, Stage = m.Stage, Status = status, Score = m.Score, Url = m.Url, Streams = m.Streams,
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
                if (m.Status == "finished") continue;
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
        long lastParseAt;

        public M8Service(string cachePath, params string[] nurLiveCaches)
        {
            this.cachePath = cachePath;
            this.nurLiveCaches = nurLiveCaches;
            try
            {
                var c = M8.ReadNurLiveCache(cachePath); // même format que Nur Live
                if (c != null) { Matches = c.Value.Matches; Tournaments = c.Value.Tournaments; FetchedAtMs = c.Value.FetchedAt; Source = "cache"; }
            }
            catch { }
        }

        public bool IsStale(long nowMs) => nowMs - FetchedAtMs > 60 * 60 * 1000;

        /// Matchs à jour à l'instant t.
        public List<M8Match> Current(long nowMs) => M8.RefreshStatus(Matches, nowMs).OrderBy(m => m.Ts).ToList();

        public async Task RefreshAsync(bool force = false)
        {
            if (Loading) return;
            Loading = true;
            Updated?.Invoke();
            try
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
                            return;
                        }
                    }
                    catch { }
                }
                // 2) Sinon Liquipedia
                await FetchAllAsync();
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
}
