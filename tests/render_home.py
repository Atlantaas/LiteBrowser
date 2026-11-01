"""Rendu de la page d'accueil dans Chrome (Playwright) avec des données réalistes.
Usage : python3 tests/render_home.py <dossier de sortie>
Vérifie aussi qu'il n'y a aucune erreur JavaScript et que la recherche envoie bien un message."""
import json, sys, time, pathlib
from playwright.sync_api import sync_playwright

root = pathlib.Path(__file__).resolve().parent.parent
out = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else '.')
out.mkdir(parents=True, exist_ok=True)

now = int(time.time())
data = {
    "theme": "m8", "engine": "google",
    "engines": [{"id": "google", "name": "Google"}, {"id": "ddg", "name": "DuckDuckGo"}],
    "tiles": [{"title": t, "url": u} for t, u in [
        ("YouTube", "https://www.youtube.com/"), ("Gmail", "https://mail.google.com/"), ("Wikipédia", "https://fr.wikipedia.org/"),
        ("Twitch", "https://www.twitch.tv/"), ("Reddit", "https://www.reddit.com/"), ("Amazon", "https://www.amazon.fr/"),
        ("Netflix", "https://www.netflix.com/"), ("Discord", "https://discord.com/app")]],
    "m8": {
        "loading": False, "updated": now * 1000, "source": "Liquipedia", "notify": True, "reminder": 15,
        "games": [{"id": g, "label": l, "short": sh, "on": True} for g, l, sh in [
            ("valorant", "Valorant", "VAL"), ("valorantgc", "Valorant Game Changers", "GC"), ("callofduty", "Call of Duty", "CDL"),
            ("warzone", "Warzone", "WZ"), ("rocketleague", "Rocket League", "RL"), ("fortnite", "Fortnite", "FN"), ("ageofempires", "Age of Empires", "AoE")]],
        "matches": [
            {"id": "valorantgc:1", "game": "valorantgc", "gameLabel": "Valorant Game Changers", "gameShort": "GC", "ts": now - 600, "status": "live",
             "opp": "KRÜ Blaze", "oppShort": "KRÜ.B", "tournament": "Game Changers Championship 2026", "stage": "GF", "score": "", "url": "https://liquipedia.net/valorant/", "muted": False},
            {"id": "callofduty:2", "game": "callofduty", "gameLabel": "Call of Duty", "gameShort": "CDL", "ts": now + 3 * 3600 + 900, "status": "upcoming",
             "opp": "OpTic Texas", "oppShort": "OPTX", "tournament": "Une compétition avec un nom vraiment très long pour tester l'affichage", "stage": "", "score": "", "url": "#", "muted": True},
            {"id": "rocketleague:3", "game": "rocketleague", "gameLabel": "Rocket League", "gameShort": "RL", "ts": now + 26 * 3600, "status": "upcoming",
             "opp": "Karmine Corp", "oppShort": "KC", "tournament": "RLCS 2026", "stage": "Groupe A", "score": "", "url": "#", "muted": False},
            {"id": "fortnite:4", "game": "fortnite", "gameLabel": "Fortnite", "gameShort": "FN", "ts": now + 4 * 86400, "status": "upcoming",
             "opp": "À déterminer", "oppShort": "TBD", "tournament": "FNCS", "stage": "", "score": "", "url": "#", "muted": False},
        ],
        "tournaments": [{"game": "Valorant", "name": "VCT EMEA Kickoff", "when": "Jan – Feb", "url": "#"}],
    },
    "channels": [
        {"id": "twitch:gentlemates", "platform": "twitch", "key": "gentlemates", "label": "Gentle Mates", "fav": True, "notify": True, "live": True,
         "name": "GentleMates", "avatar": "", "title": "M8 en grande finale des Game Changers !", "game": "VALORANT", "viewers": 18342, "url": "#"},
        {"id": "twitch:squeezie", "platform": "twitch", "key": "squeezie", "label": "Squeezie", "fav": False, "notify": False, "live": True,
         "name": "Squeezie", "avatar": "", "title": "Watchparty", "game": "VALORANT", "viewers": 52011, "url": "#"},
        {"id": "twitch:gotaga", "platform": "twitch", "key": "gotaga", "label": "Gotaga", "fav": False, "notify": False, "live": False,
         "name": "Gotaga", "avatar": "", "title": "", "game": "", "viewers": 0, "url": "#"},
        {"id": "kick:xqc", "platform": "kick", "key": "xqc", "label": "xqc", "fav": False, "notify": False, "live": None,
         "name": "xqc", "avatar": "", "title": "", "game": "", "viewers": 0, "url": "#"},
    ],
    "prayers": {"city": "Creutzwald, FR", "hijri": "21 Joumada al-Oula 1448", "methodName": "France – UOIF (12°)",
                "times": [{"key": "Fajr", "name": "Fajr", "time": "06:10"}, {"key": "Sunrise", "name": "Lever du soleil", "time": "07:21"},
                          {"key": "Dhuhr", "name": "Dhuhr", "time": "12:17"}, {"key": "Asr", "name": "Asr", "time": "14:47"},
                          {"key": "Maghrib", "name": "Maghrib", "time": "17:12"}, {"key": "Isha", "name": "Isha", "time": "18:23"}]},
}

MOCK = """
window.__posted = [];
window.__listeners = [];
window.chrome = { webview: {
  postMessage: m => window.__posted.push(m),
  addEventListener: (t, fn) => window.__listeners.push(fn)
}};
window.__send = d => window.__listeners.forEach(fn => fn({ data: d }));
"""

failures = 0
def check(name, ok):
    global failures
    print(('  OK   ' if ok else '  FAIL ') + name)
    if not ok: failures += 1

with sync_playwright() as p:
    browser = p.chromium.launch(executable_path='/usr/bin/google-chrome', args=['--no-sandbox'])
    for theme, w, h in [("m8", 1366, 860), ("light", 1366, 860), ("dark", 1366, 860), ("m8", 430, 1500)]:
        page = browser.new_page(viewport={"width": w, "height": h}, device_scale_factor=1)
        errors = []
        page.on("pageerror", lambda e: errors.append(str(e)))
        page.add_init_script(MOCK)
        page.goto((root / "home.html").as_uri())
        page.wait_for_timeout(200)
        check(f"[{theme} {w}px] la page demande ses données (ready)", page.evaluate("window.__posted.some(m => m.type === 'ready')"))
        d = dict(data, theme=theme)
        page.evaluate("d => window.__send(d)", d)
        page.wait_for_timeout(400)
        check(f"[{theme} {w}px] 4 matchs affichés", page.locator(".match").count() == 4)
        check(f"[{theme} {w}px] match en direct signalé", page.locator(".match .live").count() == 1)
        check(f"[{theme} {w}px] adversaire court affiché", page.locator(".m-vs .opp").first.inner_text() == "KRÜ.B")
        check(f"[{theme} {w}px] cloche coupée sur le match en sourdine", page.locator("[data-mute='callofduty:2']").inner_text() == "🔕")
        check(f"[{theme} {w}px] 4 chaînes : lives d'abord, favoris en tête", page.locator(".ch").count() == 4 and "GentleMates" in page.locator(".ch").first.inner_text() and "Squeezie" in page.locator(".ch").nth(1).inner_text())
        page.click("[data-watch='valorantgc:1']")
        check(f"[{theme} {w}px] « Regarder » envoie le match", page.evaluate("window.__posted.some(m => m.type === 'watchMatch' && m.id === 'valorantgc:1')")
              and page.url.endswith("home.html"))
        page.click(".ch >> nth=0")
        check(f"[{theme} {w}px] clic sur une chaîne → multistream", page.evaluate("window.__posted.some(m => m.type === 'watch' && m.ids[0] === 'twitch:gentlemates')"))
        page.click("#lvall")
        check(f"[{theme} {w}px] « Tout regarder » envoie les 2 lives", page.evaluate("window.__posted.some(m => m.type === 'watch' && m.ids.length === 2)"))
        check(f"[{theme} {w}px] 6 horaires de prière", page.locator(".prow").count() == 6)
        check(f"[{theme} {w}px] prochaine prière affichée", page.locator(".next .name").count() == 1)
        check(f"[{theme} {w}px] pas de défilement horizontal", page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"))
        # suggestions de chaînes pendant la frappe
        page.click("#lvin")
        page.keyboard.type("gota")
        page.wait_for_timeout(450)
        check(f"[{theme} {w}px] la saisie demande des suggestions", page.evaluate("window.__posted.some(m => m.type === 'searchChannels' && m.q === 'gota')"))
        page.evaluate("""__send({ type: 'channelHits', q: 'gota', hits: [
          { platform: 'twitch', key: 'gotagatv', name: 'GotagaTV', avatar: '', live: true, viewers: 18300, game: 'Call of Duty', followers: 0 },
          { platform: 'twitch', key: 'gotagalegends', name: 'gotagalegends', avatar: '', live: false, viewers: 0, game: '', followers: 1520 },
          { platform: 'twitch', key: 'squeezie', name: 'Squeezie', avatar: '', live: true, viewers: 5, game: '', followers: 0 }]})""")
        check(f"[{theme} {w}px] 2 suggestions (chaîne déjà suivie masquée)", page.locator(".lv-s").count() == 2)
        check(f"[{theme} {w}px] suggestion en live : viewers + jeu", "18,3 k" in page.locator(".lv-s").first.inner_text() and "Call of Duty" in page.locator(".lv-s").first.inner_text())
        check(f"[{theme} {w}px] suggestion hors live : abonnés", "1,5 k abonnés" in page.locator(".lv-s").nth(1).inner_text())
        page.screenshot(path=str(out / f"home_sugg_{theme}_{w}.png"))
        page.keyboard.press("ArrowDown")
        page.keyboard.press("Enter")
        check(f"[{theme} {w}px] ↓ + Entrée ajoute la suggestion", page.evaluate("window.__posted.some(m => m.type === 'addChannel' && m.input === 'gotagatv')") and page.locator("#lvsugg").is_hidden())
        check(f"[{theme} {w}px] aucune erreur JavaScript", not errors)
        page.fill("#q", "gentle mates valorant")
        page.press("#q", "Enter")
        check(f"[{theme} {w}px] la recherche est envoyée", page.evaluate("window.__posted.some(m => m.type === 'search' && m.q === 'gentle mates valorant')"))
        page.screenshot(path=str(out / f"home_{theme}_{w}.png"), full_page=(w < 600))
        page.close()

    # États vides / chargement
    page = browser.new_page(viewport={"width": 1366, "height": 860})
    page.add_init_script(MOCK)
    page.goto((root / "home.html").as_uri())
    page.evaluate("d => window.__send(d)", dict(data, m8=dict(data["m8"], matches=[], tournaments=[]), channels=[], prayers={"city": None, "error": "Impossible de récupérer les horaires."}))
    page.wait_for_timeout(300)
    check("[vide] message « aucun match »", "Aucun match" in page.inner_text("#matches"))
    check("[vide] erreur prières lisible", "Impossible" in page.inner_text("#prayers"))
    check("[vide] invitation à ajouter des chaînes", "Ajoute tes chaînes" in page.inner_text("#chs"))
    page.screenshot(path=str(out / "home_empty.png"))
    page.close()

    # ---------- Multistream ----------
    page = browser.new_page(viewport={"width": 1366, "height": 768})
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)))
    page.route("**/*", lambda r: r.abort() if ("twitch.tv" in r.request.url or "kick.com" in r.request.url) else r.continue_())
    page.add_init_script(MOCK)
    page.goto((root / "multi.html").as_uri() + "#c=twitch:gentlemates,twitch:squeezie,kick:xqc&chat=1")
    page.wait_for_timeout(300)
    check("[multi] 3 lives à partir de l'adresse", page.locator("#stage .tile").count() == 3)
    check("[multi] lecteurs créés (iframes)", page.locator("#stage iframe").count() == 3)
    check("[multi] chat ouvert sur le 1er live", page.locator("#chat iframe").count() == 1 and "gentlemates" in page.locator("#chat iframe").get_attribute("src"))
    check("[multi] 3 onglets de chat (2 Twitch + Kick)", page.locator(".ct").count() == 3)
    page.evaluate("d => window.__send(d)", {"type": "multi", "theme": "m8", "parent": "litebrowser.home", "channels": [
        {"id": "twitch:gentlemates", "platform": "twitch", "key": "gentlemates", "label": "Gentle Mates", "live": True, "name": "GentleMates", "viewers": 18342,
         "player": "https://player.twitch.tv/?channel=gentlemates&parent=litebrowser.home&muted=true&autoplay=true",
         "chat": "https://www.twitch.tv/embed/gentlemates/chat?parent=litebrowser.home&darkpopout", "page": "https://www.twitch.tv/gentlemates"},
        {"id": "twitch:zerator", "platform": "twitch", "key": "zerator", "label": "ZeratoR", "live": False}]})
    page.wait_for_timeout(200)
    check("[multi] nom et live affichés", "GentleMates" in page.inner_text("#chips") and page.locator("#chips .dot.live").count() == 1)
    page.keyboard.press("2")
    check("[multi] touche 2 → squeezie en grand", page.locator(".chip.on").inner_text().startswith("2."))
    first_frame = page.locator("#stage iframe").first.get_attribute("src")
    page.keyboard.press("g")
    check("[multi] G change la disposition sans recharger les lecteurs", page.locator("#stage iframe").first.get_attribute("src") == first_frame)
    page.click("#chips .chip >> nth=2 >> .x")
    check("[multi] retirer un live", page.locator("#stage .tile").count() == 2 and "kick" not in page.url)
    page.evaluate("d => window.__send(d)", {"type": "added", "id": "twitch:zerator"})
    page.wait_for_timeout(100)
    check("[multi] ajouter un live", page.locator("#stage .tile").count() == 3 and "zerator" in page.url)
    check("[multi] le navigateur est prévenu des chaînes affichées", page.evaluate("window.__posted.filter(m => m.type === 'multiIds').pop().ids.length === 3"))
    page.keyboard.press("t")
    check("[multi] T → mode cinéma", "cinema" in page.evaluate("document.body.className"))
    page.keyboard.press("Escape")
    page.keyboard.press("c")
    check("[multi] C → chat masqué", "chat" not in page.evaluate("document.body.className").split())
    page.keyboard.press("c")
    check("[multi] pas de défilement horizontal", page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"))
    check("[multi] aucune erreur JavaScript", not errors)
    page.screenshot(path=str(out / "multi.png"))
    page.goto((root / "multi.html").as_uri())
    page.wait_for_timeout(200)
    check("[multi] vide : message d'aide", "Aucun live" in page.inner_text("#stage"))
    browser.close()

print("Rendu : tout est OK" if not failures else f"{failures} échec(s)")
sys.exit(1 if failures else 0)
