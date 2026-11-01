// Logique sans interface graphique : testable sur n'importe quel système (voir tests/).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LiteCore
{
    // ==================================================================
    //  Adresses et recherche
    // ==================================================================
    public static class UrlTools
    {
        public static bool LooksLikeUrl(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0 || s.Contains(' ')) return false;
            if (s.Contains("://") || s.StartsWith("about:") || s.StartsWith("chrome-extension:")) return true;
            var host = s.Split('/', '?', '#')[0].Split(':')[0];
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (host.Count(ch => ch == '.') == 3 && IPAddress.TryParse(host, out _)) return true;
            var parts = host.Split('.');
            if (parts.Length < 2 || parts.Any(p => p.Length == 0)) return false;
            var tld = parts[^1];
            return tld.Length >= 2 && tld.All(char.IsLetter) && Uri.CheckHostName(host) == UriHostNameType.Dns;
        }

        /// Transforme ce que tape l'utilisateur en adresse : site direct ou recherche.
        public static string ToUrl(string text, string searchPrefix)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return null;
            if (text.Contains("://") || text.StartsWith("about:")) return text;
            if (LooksLikeUrl(text))
            {
                var host = text.Split('/', '?', '#')[0].Split(':')[0];
                bool local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out _);
                return (local ? "http://" : "https://") + text;
            }
            return searchPrefix + Uri.EscapeDataString(text);
        }

        /// Forme courte pour l'autocomplétion : youtube.com/watch?v=…
        public static string ShortForm(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var s = url;
            int i = s.IndexOf("://", StringComparison.Ordinal);
            if (i >= 0) s = s[(i + 3)..];
            if (s.StartsWith("www.")) s = s[4..];
            if (s.EndsWith("/")) s = s[..^1];
            return s.Length == 0 || s.Length > 120 ? null : s;
        }

        /// Vrai si l'hôte (ou un de ses parents) est dans la liste : ads.example.com ⊂ example.com
        public static bool HostMatches(string host, ISet<string> list)
        {
            while (!string.IsNullOrEmpty(host))
            {
                if (list.Contains(host)) return true;
                int i = host.IndexOf('.');
                if (i < 0) return false;
                host = host[(i + 1)..];
            }
            return false;
        }
    }

    // ==================================================================
    //  Mises à jour
    // ==================================================================
    public static class VersionTools
    {
        public static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

        /// "v2.3.0" → 2.3.0 ; null si le tag n'est pas une version.
        public static Version ParseTag(string tag)
        {
            var t = (tag ?? "").Trim().TrimStart('v', 'V');
            return Version.TryParse(t, out var v) ? Norm(v) : null;
        }

        /// Lit la réponse JSON de GitHub "releases/latest" : version + lien du .exe si plus récente.
        public static (Version Version, string Url)? NewerRelease(string json, Version current)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var v = ParseTag(root.GetProperty("tag_name").GetString());
            if (v == null || v <= Norm(current)) return null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return (v, a.GetProperty("browser_download_url").GetString());
            }
            return null;
        }
    }

    // ==================================================================
    //  Extensions Chrome (.crx)
    // ==================================================================
    public static class Crx
    {
        static readonly JsonDocumentOptions JsonOpts = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        public static string ParseStoreId(string text)
        {
            var m = Regex.Match(text ?? "", "(?<![a-p])[a-p]{32}(?![a-p])");
            return m.Success ? m.Value : null;
        }

        public static string DownloadUrl(string storeId) =>
            "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=131.0" +
            "&acceptformat=crx2,crx3&x=id%3D" + storeId + "%26uc";

        /// Position du zip à l'intérieur d'un fichier .crx (formats 2 et 3).
        public static int ZipOffset(byte[] crx)
        {
            if (crx == null || crx.Length < 16 || crx[0] != 'C' || crx[1] != 'r' || crx[2] != '2' || crx[3] != '4')
                throw new InvalidDataException("Ce n'est pas une extension Chrome valide.");
            int version = BitConverter.ToInt32(crx, 4);
            int offset = version == 3
                ? 12 + BitConverter.ToInt32(crx, 8)
                : 16 + BitConverter.ToInt32(crx, 8) + BitConverter.ToInt32(crx, 12);
            if (offset <= 0 || offset >= crx.Length) throw new InvalidDataException("Extension corrompue.");
            return offset;
        }

        /// Décompresse l'extension dans folder (et retire _metadata, refusé par le moteur).
        public static void Extract(byte[] crx, string folder)
        {
            int start = ZipOffset(crx);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            using (var zip = new ZipArchive(new MemoryStream(crx, start, crx.Length - start)))
                zip.ExtractToDirectory(folder, true);
            var meta = Path.Combine(folder, "_metadata");
            if (Directory.Exists(meta)) Directory.Delete(meta, true);
        }

        public static async Task DownloadAsync(string storeId, string folder)
        {
            using var http = Net.Client(TimeSpan.FromMinutes(3), chromeUa: true);
            var bytes = await http.GetByteArrayAsync(DownloadUrl(storeId));
            Extract(bytes, folder);
        }

        public static string ResolveMessage(string folder, string value, string defaultLocale)
        {
            if (value == null || !value.StartsWith("__MSG_") || !value.EndsWith("__")) return value;
            var key = value[6..^2];
            foreach (var loc in new[] { "fr", defaultLocale, "en" }.Where(x => x != null).Distinct())
            {
                var f = Path.Combine(folder, "_locales", loc, "messages.json");
                if (!File.Exists(f)) continue;
                try
                {
                    using var d = JsonDocument.Parse(File.ReadAllText(f), JsonOpts);
                    foreach (var p in d.RootElement.EnumerateObject())
                        if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetProperty("message", out var msg))
                            return msg.GetString();
                }
                catch { }
            }
            return value;
        }

        public record ManifestInfo(string Name, string Popup, string IconPath);

        public static ManifestInfo ReadManifest(string folder)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")), JsonOpts);
            var root = doc.RootElement;
            var raw = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var loc = root.TryGetProperty("default_locale", out var dl) ? dl.GetString() : null;
            var name = ResolveMessage(folder, raw, loc);

            string popup = null;
            foreach (var key in new[] { "action", "browser_action", "page_action" })
                if (root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Object &&
                    a.TryGetProperty("default_popup", out var p) && p.ValueKind == JsonValueKind.String)
                { popup = p.GetString().TrimStart('/'); break; }

            string icon = null;
            if (root.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Object)
            {
                var best = icons.EnumerateObject()
                    .Select(x => (Size: int.TryParse(x.Name, out var s) ? s : 0, Path: x.Value.GetString()))
                    .OrderBy(x => Math.Abs(x.Size - 32)).FirstOrDefault();
                if (best.Path != null)
                {
                    var file = Path.Combine(folder, best.Path.TrimStart('/'));
                    if (File.Exists(file)) icon = file;
                }
            }
            return new ManifestInfo(name, popup, icon);
        }
    }

    // ==================================================================
    //  Réseau
    // ==================================================================
    public static class Net
    {
        public const string Ua = "LiteBrowser/3 (+https://github.com/Atlantaas/LiteBrowser)";
        const string ChromeUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        public static HttpClient Client(TimeSpan timeout, bool chromeUa = false)
        {
            var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
            { Timeout = timeout };
            h.DefaultRequestHeaders.UserAgent.ParseAdd(chromeUa ? ChromeUa : Ua);
            h.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
            return h;
        }
    }

    // ==================================================================
    //  Horaires de prière (API Aladhan)
    // ==================================================================
    public class PrayerDay
    {
        public DateTime Date;
        public string City;
        public string Hijri;
        public List<(string Key, string Name, TimeSpan Time)> Times = new();
    }

    public static class Prayers
    {
        public static readonly (int Id, string Name)[] Methods =
        {
            (12, "France – UOIF (12°)"),
            (3, "Ligue islamique mondiale"),
            (4, "Umm al-Qura (La Mecque)"),
            (5, "Égypte"),
            (2, "ISNA (Amérique du Nord)"),
        };

        static readonly (string Key, string Name)[] Names =
        {
            ("Fajr", "Fajr"), ("Sunrise", "Lever du soleil"), ("Dhuhr", "Dhuhr"),
            ("Asr", "Asr"), ("Maghrib", "Maghrib"), ("Isha", "Isha"),
        };

        static readonly string[] HijriMonths =
        {
            "Mouharram", "Safar", "Rabi al-Awwal", "Rabi ath-Thani", "Joumada al-Oula", "Joumada ath-Thania",
            "Rajab", "Chaabane", "Ramadan", "Chawwal", "Dhou al-Qi'da", "Dhou al-Hijja",
        };

        public static string ApiUrl(DateTime date, double lat, double lon, int method) =>
            string.Format(CultureInfo.InvariantCulture,
                "https://api.aladhan.com/v1/timings/{0:dd-MM-yyyy}?latitude={1:0.####}&longitude={2:0.####}&method={3}",
                date, lat, lon, method);

        public static PrayerDay Parse(string json, DateTime date, string city)
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            var t = data.GetProperty("timings");
            var day = new PrayerDay { Date = date.Date, City = city };
            foreach (var (key, name) in Names)
            {
                // Les heures peuvent être "06:10" ou "06:10 (CET)"
                var raw = t.GetProperty(key).GetString() ?? "";
                var m = Regex.Match(raw, "(\\d{1,2}):(\\d{2})");
                if (m.Success)
                    day.Times.Add((key, name, new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0)));
            }
            try
            {
                var h = data.GetProperty("date").GetProperty("hijri");
                int month = h.GetProperty("month").GetProperty("number").GetInt32();
                day.Hijri = $"{int.Parse(h.GetProperty("day").GetString())} {HijriMonths[month - 1]} {h.GetProperty("year").GetString()}";
            }
            catch { }
            return day;
        }

        /// Prochaine prière (hors lever du soleil). Après Isha : Fajr du lendemain.
        public static (string Name, DateTime At) Next(PrayerDay day, DateTime now)
        {
            var five = day.Times.Where(x => x.Key != "Sunrise").ToList();
            foreach (var p in five)
            {
                var at = day.Date + p.Time;
                if (at > now) return (p.Name, at);
            }
            var fajr = five.FirstOrDefault();
            return (fajr.Name, day.Date.AddDays(1) + fajr.Time);
        }

        /// Localisation approximative par l'adresse IP (aucune autorisation demandée).
        public static async Task<(double Lat, double Lon, string City)?> LocateByIpAsync()
        {
            using var http = Net.Client(TimeSpan.FromSeconds(10));
            var json = await http.GetStringAsync("https://ipwho.is/?fields=success,city,latitude,longitude");
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return null;
            return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), r.GetProperty("city").GetString());
        }

        /// Trouve une ville par son nom (Open-Meteo, gratuit, sans clé).
        public static async Task<(double Lat, double Lon, string City)?> GeocodeAsync(string name)
        {
            using var http = Net.Client(TimeSpan.FromSeconds(10));
            var json = await http.GetStringAsync(
                "https://geocoding-api.open-meteo.com/v1/search?count=1&language=fr&name=" + Uri.EscapeDataString(name));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return null;
            var r = res[0];
            var label = r.GetProperty("name").GetString();
            if (r.TryGetProperty("country_code", out var cc)) label += ", " + cc.GetString();
            return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), label);
        }

        public static async Task<PrayerDay> FetchAsync(DateTime date, double lat, double lon, int method, string city)
        {
            using var http = Net.Client(TimeSpan.FromSeconds(15));
            var json = await http.GetStringAsync(ApiUrl(date, lat, lon, method));
            return Parse(json, date, city);
        }
    }

    // ==================================================================
    //  Bloqueur de pubs YouTube (script injecté dans youtube.com)
    // ==================================================================
    public static class YouTubeAdblock
    {
        /// 1) retire les pubs des données du lecteur avant qu'il ne les lise,
        /// 2) passe automatiquement une pub qui arriverait quand même,
        /// 3) masque les encarts publicitaires de la page.
        public const string Script = @"(function () {
  if (!/(^|\.)youtube\.com$/.test(location.hostname) || window.__lbYtAdblock) return;
  window.__lbYtAdblock = true;
  var AD_KEYS = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams'];
  function prune(o) {
    if (!o || typeof o !== 'object') return o;
    try {
      for (var i = 0; i < AD_KEYS.length; i++) if (AD_KEYS[i] in o) delete o[AD_KEYS[i]];
      if (o.playerResponse) prune(o.playerResponse);
      if (o.response) prune(o.response);
      if (Array.isArray(o)) for (var j = 0; j < o.length; j++) prune(o[j]);
    } catch (e) {}
    return o;
  }
  window.__lbPrune = prune;

  var parse = JSON.parse;
  JSON.parse = function () { return prune(parse.apply(this, arguments)); };

  ['ytInitialPlayerResponse', 'ytInitialData'].forEach(function (name) {
    var value;
    try {
      Object.defineProperty(window, name, {
        configurable: true,
        get: function () { return value; },
        set: function (v) { value = prune(v); }
      });
    } catch (e) {}
  });

  var origFetch = window.fetch;
  if (origFetch) window.fetch = function (input, init) {
    var url = typeof input === 'string' ? input : (input && input.url) || '';
    var p = origFetch.apply(this, arguments);
    if (!/\/youtubei\/v1\/(player|next)/.test(url)) return p;
    return p.then(function (res) {
      return res.clone().json().then(function (j) {
        var body = JSON.stringify(prune(j));
        return new Response(body, { status: res.status, statusText: res.statusText, headers: res.headers });
      }).catch(function () { return res; });
    });
  };

  var css = 'ytd-ad-slot-renderer,ytd-in-feed-ad-layout-renderer,ytd-banner-promo-renderer,ytd-promoted-sparkles-web-renderer,' +
            'ytd-display-ad-renderer,ytd-statement-banner-renderer,ytd-companion-slot-renderer,ytd-player-legacy-desktop-watch-ads-renderer,' +
            '#masthead-ad,#player-ads,.ytp-ad-overlay-container,.ytp-ad-message-container,ytd-merch-shelf-renderer,' +
            'ytd-rich-item-renderer:has(ytd-ad-slot-renderer),ytd-reel-video-renderer:has(.ytd-ad-slot-renderer){display:none!important}';
  function addStyle() {
    if (document.getElementById('lb-yt-adblock')) return;
    var s = document.createElement('style'); s.id = 'lb-yt-adblock'; s.textContent = css;
    (document.head || document.documentElement).appendChild(s);
  }
  if (document.documentElement) addStyle(); else document.addEventListener('DOMContentLoaded', addStyle);

  function skip() {
    var player = document.querySelector('.html5-video-player');
    if (!player || !player.classList.contains('ad-showing')) return;
    var v = player.querySelector('video');
    if (v && isFinite(v.duration) && v.duration > 0) { v.muted = true; v.currentTime = v.duration; }
    var b = document.querySelector('.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern');
    if (b) b.click();
  }
  setInterval(skip, 300);
})();";
    }
}
