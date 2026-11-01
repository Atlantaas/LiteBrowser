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
    "matches": [
        {"game": "VALORANT", "ts": now - 600, "t1": "Gentle Mates GC", "s1": "M8.GC", "t2": "KRÜ Blaze", "s2": "KRÜ.B", "format": "Bo5", "tournament": "Game Changers Championship 2026", "url": "https://liquipedia.net/valorant/"},
        {"game": "CS2", "ts": now + 3 * 3600 + 900, "t1": "Team Vitality", "s1": "VIT", "t2": "Gentle Mates", "s2": "M8", "format": "Bo3", "tournament": "Une compétition avec un nom vraiment très long pour tester", "url": "#"},
        {"game": "ROCKET LEAGUE", "ts": now + 26 * 3600, "t1": "Gentle Mates", "s1": "M8", "t2": "Karmine Corp", "s2": "KC", "format": "Bo7", "tournament": "RLCS 2026", "url": "#"},
        {"game": "CALL OF DUTY", "ts": now + 4 * 86400, "t1": "Paris Gentle Mates", "s1": "PAR", "t2": "OpTic Texas", "s2": "OPTX", "format": "Bo5", "tournament": "Call of Duty League 2026", "url": "#"},
    ],
    "matchesUpdated": now * 1000, "matchesLoading": False,
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
        check(f"[{theme} {w}px] match en direct signalé", page.locator(".live").count() == 1)
        check(f"[{theme} {w}px] M8 toujours à gauche", page.locator(".m-vs .gm").first.inner_text() == "M8.GC" and page.locator(".m-vs .gm").nth(1).inner_text() == "M8")
        check(f"[{theme} {w}px] 6 horaires de prière", page.locator(".prow").count() == 6)
        check(f"[{theme} {w}px] prochaine prière affichée", page.locator(".next .name").count() == 1)
        check(f"[{theme} {w}px] pas de défilement horizontal", page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"))
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
    page.evaluate("d => window.__send(d)", dict(data, matches=[], matchesLoading=False, prayers={"city": None, "error": "Impossible de récupérer les horaires."}))
    page.wait_for_timeout(300)
    check("[vide] message « aucun match »", "Aucun match" in page.inner_text("#matches"))
    check("[vide] erreur prières lisible", "Impossible" in page.inner_text("#prayers"))
    page.screenshot(path=str(out / "home_empty.png"))
    browser.close()

print("Rendu : tout est OK" if not failures else f"{failures} échec(s)")
sys.exit(1 if failures else 0)
