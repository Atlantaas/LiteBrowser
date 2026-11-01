// Teste le script anti-pub YouTube dans un faux navigateur (Node.js).
// Usage : node youtube_test.js <fichier du script>
const fs = require('fs');
const vm = require('vm');

const script = fs.readFileSync(process.argv[2], 'utf8');
let failures = 0;
const check = (name, ok) => { console.log((ok ? '  OK   ' : '  FAIL ') + name); if (!ok) failures++; };

function makeBrowser(hostname) {
  const styles = [];
  const player = { classList: { contains: c => c === 'ad-showing' }, querySelector: () => video };
  const video = { duration: 15, currentTime: 0, muted: false };
  let skipClicked = false;
  const document = {
    documentElement: { appendChild: el => styles.push(el) },
    head: { appendChild: el => styles.push(el) },
    getElementById: id => styles.find(s => s.id === id) || null,
    createElement: () => ({}),
    addEventListener: () => {},
    querySelector: sel => sel.includes('html5-video-player') ? player
                       : sel.includes('skip') ? { click: () => { skipClicked = true; } } : null,
  };
  const intervals = [];
  const ctx = {
    // pas de JSON partagé : chaque faux navigateur garde le sien (comme un vrai onglet)
    location: { hostname }, document, Response,
    setInterval: (fn) => intervals.push(fn),
    fetch: async () => new Response(JSON.stringify({ adPlacements: [1], playerAds: [2], videoDetails: { title: 'ok' } }),
                                     { status: 200, headers: { 'content-type': 'application/json' } }),
  };
  ctx.window = ctx;
  vm.createContext(ctx);
  vm.runInContext(script, ctx);
  const J = vm.runInContext('JSON', ctx); // le JSON propre à ce faux onglet
  return { ctx, J, styles, video, intervals, skipped: () => skipClicked };
}

(async () => {
  const b = makeBrowser('www.youtube.com');

  const parsed = b.J.parse('{"adPlacements":[1],"playerAds":[2],"adSlots":[3],"videoDetails":{"title":"x"}}');
  check('JSON.parse retire adPlacements/playerAds/adSlots', !parsed.adPlacements && !parsed.playerAds && !parsed.adSlots);
  check('JSON.parse garde le reste (videoDetails)', parsed.videoDetails && parsed.videoDetails.title === 'x');

  b.ctx.ytInitialPlayerResponse = { adPlacements: [1], streamingData: { a: 1 } };
  check('ytInitialPlayerResponse nettoyé', !b.ctx.ytInitialPlayerResponse.adPlacements && b.ctx.ytInitialPlayerResponse.streamingData.a === 1);

  const nested = b.ctx.__lbPrune({ playerResponse: { playerAds: [1], ok: true } });
  check('réponse imbriquée (playerResponse) nettoyée', !nested.playerResponse.playerAds && nested.playerResponse.ok);

  const res = await b.ctx.fetch('https://www.youtube.com/youtubei/v1/player?key=x');
  const body = await res.json();
  check('fetch /youtubei/v1/player nettoyé', !body.adPlacements && !body.playerAds && body.videoDetails.title === 'ok');

  const other = await (await b.ctx.fetch('https://www.youtube.com/other')).json();
  check('fetch autres adresses intact', Array.isArray(other.adPlacements));

  check('style anti-encarts ajouté', b.styles.some(s => s.id === 'lb-yt-adblock' && s.textContent.includes('ytd-ad-slot-renderer')));

  b.intervals.forEach(fn => fn());
  check('pub en cours : passée à la fin + coupée', b.video.currentTime === 15 && b.video.muted);
  check('bouton "Ignorer" cliqué', b.skipped());

  const other2 = makeBrowser('www.google.com');
  const p2 = other2.J.parse('{"adPlacements":[1]}');
  check('aucun effet hors de YouTube', Array.isArray(p2.adPlacements) && other2.styles.length === 0);

  console.log(failures ? `${failures} échec(s)` : 'Script YouTube : tout est OK');
  process.exit(failures ? 1 : 0);
})();
