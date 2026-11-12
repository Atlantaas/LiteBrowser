# LiteBrowser

Navigateur Windows ultra léger, basé sur le moteur WebView2 déjà présent dans Windows 10/11.

## Installation
Télécharge `LiteBrowser.exe` dans la [dernière version](https://github.com/Atlantaas/LiteBrowser/releases/latest) et lance-le.
Il s'installe tout seul dans `%LOCALAPPDATA%\LiteBrowser\app`, crée un raccourci sur le Bureau et dans le menu Démarrer,
puis **se met à jour automatiquement** à chaque nouvelle version publiée ici.

## Ce qui le rend léger
- Pas de moteur embarqué : il utilise celui de Windows, partagé avec le système.
- Bloqueur de pubs et de traqueurs intégré (les requêtes ne partent même pas).
- Onglets en arrière-plan : RAM réduite tout de suite, puis gelés après 30 s.
- Services de fond coupés : sync, traduction, télémétrie, shopping Edge…

## Nouveautés v4
- **Page Réglages** complète (`litebrowser://reglages`, Ctrl+,) : démarrage, moteur, thème, confidentialité, onglets,
  téléchargements, prières, matchs, favoris, historique (Ctrl+H, avec recherche), extensions, raccourcis, à propos
- **Reprise de session** : les onglets de la dernière fois reviennent, chargés seulement au clic (démarrage rapide, peu de RAM)
- **Onglets épinglés** (compacts, toujours à gauche) et **navigation privée** (Ctrl+Maj+N, rien n'est gardé)
- **Bouclier** dans la barre d'adresse : nombre de pubs bloquées sur la page, bloqueur coupable site par site
- Protection contre le pistage réglable (de base / équilibrée / stricte), veille des onglets réglable, historique désactivable,
  « tout effacer en quittant », dossier de téléchargement et « demander où enregistrer »
- **Prières** : rappel X min avant, compte à rebours dans la barre la dernière heure, barre de progression sur l'accueil
- Thème Gentle Mates aux vraies couleurs de l'équipe : noir profond + **rose M8 (#D669D2)**
- Sécurité : mise à jour téléchargée uniquement depuis GitHub, pages intégrées illisibles par les sites, pas d'enregistrement
  des mots de passe dans le navigateur (ils restent dans NordPass)

## Fonctions
- **Édition Gentle Mates** : thème noir et rose M8, page d'accueil fan
- **Centre de matchs M8** (Valorant, Game Changers, Call of Duty, Warzone, Rocket League, Fortnite, Age of Empires) depuis Liquipedia,
  + les tournois absents de la page de l'équipe (Coupe de France, ligues…), alerte Windows avant le match (réglable) et au coup d'envoi, bouton **Regarder** (co-stream M8 en live, sinon diffusion officielle)
- **Lives** : tes chaînes Twitch / Kick / YouTube, qui est en live, alerte quand un favori lance son live (reprises de Nur Live si installé)
- **Multistream Nur Live** : si l'appli Nur Live est sur ton PC, son interface s'ouvre directement dans un onglet
  (Ctrl+Maj+M) avec tes chaînes, ton compte Twitch, les volumes par live et les matchs M8 — sinon un multistream intégré
  (jusqu'à 9 lives, dispositions, son principal ou mixage, chat, mode cinéma)
- **Suggestions de chaînes** pendant la frappe (accueil et multistream) : avatar, live, viewers, abonnés
- **Compte Twitch** : Nur Live → Twitch → « Se connecter » (connexion sur twitch.tv dans un onglet, reprise automatiquement)
- **Horaires de prière** : calcul selon ta ville (Aladhan) **ou les horaires de ta mosquée** (Mawaqit), ajustement
  ± minutes par prière, Jumu'a, prochaine prière dans la barre, notification à l'heure — clic sur l'horaire pour régler
- **Barre de favoris** sous la barre d'adresse (Ctrl+Maj+B), clic droit pour renommer / déplacer / supprimer
- **Son des onglets** : icône haut-parleur sur l'onglet qui joue du son, clic pour couper (Ctrl+M), clic droit = menu de l'onglet
- Bloqueur de pubs (sites + YouTube), extensions Chrome (NordPass, TrendTrack…)
- Onglets, page d'accueil avec recherche et raccourcis vers tes sites
- Choix du moteur : Google, DuckDuckGo, Bing, Qwant, Ecosia
- Favoris (Ctrl+D), historique, suggestions dans la barre d'adresse
- Thème clair / sombre, mode sombre forcé sur les sites
- Plein écran (F11), vidéos en plein écran

## Raccourcis
Ctrl+T nouvel onglet · Ctrl+W fermer · Ctrl+Maj+T rouvrir · Ctrl+Tab onglet suivant · Ctrl+1…9 aller à un onglet ·
Ctrl+L barre d'adresse · Ctrl+D favori · Ctrl+Maj+B barre de favoris · Ctrl+M couper le son · Ctrl+Maj+M multistream · F5 actualiser · Alt+←/→ précédent/suivant · F11 plein écran ·
Ctrl+Maj+N onglet privé · Ctrl+H historique · Ctrl+J téléchargements · Ctrl+, réglages · Ctrl+Maj+Suppr effacer les données

## Tester sans toucher au navigateur installé
```
$env:LITEBROWSER_DATA = "C:\chemin\vers\donnees-de-test"   # pas d'installation, données à part
$env:LITEBROWSER_SHOTS = "C:\chemin\captures"              # facultatif : captures automatiques puis fermeture
$env:LITEBROWSER_SHOTS_PAGES = "reglages=https://litebrowser.home/settings.html;test=autotest"
```
`autotest` clique dans la page Réglages et vérifie que chaque réglage est appliqué (résultat dans `test.txt`).

## Compiler soi-même
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -o out
```
(depuis Linux/macOS, ajouter `-p:EnableWindowsTargeting=true`)

## Publier une mise à jour
1. Augmenter `<Version>` dans `LiteBrowser.csproj`
2. Pousser sur `main` : GitHub Actions teste, compile, vérifie sur Windows et publie la release `vX.Y.Z`
3. Tous les navigateurs installés la récupèrent au prochain lancement
