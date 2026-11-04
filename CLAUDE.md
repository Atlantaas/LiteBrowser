# LiteBrowser — notes pour Claude

- Navigateur Windows de Ryan : .NET 8 WinForms + WebView2, exe unique. Dépôt GitHub `Atlantaas/LiteBrowser`.
- **Un push sur `main` publie une release (CI) que TOUS les LiteBrowser installés téléchargent tout seuls.**
  Ne jamais pousser sur `main` sans l'accord explicite de Ryan ; travailler sur une branche.
- Carte : `Program.cs` (fenêtre, onglets, réglages, messages des pages), `Core.cs` / `M8.cs` (logique testée),
  `home.html` (accueil), `settings.html` (réglages, historique, favoris), `multi.html` (multistream), `nurbridge.js` (Nur Live).
- À garder absolument : thème Gentle Mates (noir + rose M8 `#D669D2`) et tout ce qui touche aux prières.
- Tests : `dotnet run --project tests/Tests.csproj -c Release` (logique) ; test réel sans toucher à l'install :
  `LITEBROWSER_DATA` + `LITEBROWSER_SHOTS` (voir README). Captures faites en mémoire : elles ne prennent pas le focus.
- Installé chez Ryan : `%LOCALAPPDATA%\LiteBrowser\app\LiteBrowser.exe`, données dans `%LOCALAPPDATA%\LiteBrowser`.
- Le PC bloque l'ouverture de ports locaux (serveur d'aperçu impossible) : vérifier les pages dans l'app.

## Historique
- 2026-11-04 — v4.0.1 : lecture sans clic limitée à Nur Live (action utilisateur via CDP), surveillance des lecteurs branchée dans le navigateur (`probe`), matchs sans horaire (`TimeUnknown`), heure indicative `TimeHints` pour M8 vs Cloud9 (20 h, à retirer une fois sur Liquipedia). À NE PAS refaire : lancer des fenêtres de test en boucle pendant que Ryan utilise le PC. Démarrage réel des lecteurs pas confirmé en test (résultats instables, fenêtre masquée ou non) : voir `nurlive.log` dans le dossier des données.
- 2026-11-03 — v4.0.0 (branche `refonte-v4`, commit d287e25, installée sur le PC de Ryan, non publiée sur GitHub ; sauvegarde v3.1.2 dans `%LOCALAPPDATA%\LiteBrowser\_backup\`) : page Réglages, session, onglets épinglés et privés, bouclier
  par site, pistage réglable, rappels de prière, thème rose M8, mode test (`LITEBROWSER_DATA`/`LITEBROWSER_SHOTS`).
