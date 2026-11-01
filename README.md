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

## Fonctions
- **Édition Gentle Mates** : thème noir M8, page d'accueil fan
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
Ctrl+L barre d'adresse · Ctrl+D favori · Ctrl+Maj+B barre de favoris · Ctrl+M couper le son · Ctrl+Maj+M multistream · F5 actualiser · Alt+←/→ précédent/suivant · F11 plein écran

## Compiler soi-même
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out
```
(depuis Linux/macOS, ajouter `-p:EnableWindowsTargeting=true`)

## Publier une mise à jour
1. Augmenter `<Version>` dans `LiteBrowser.csproj`
2. Pousser sur `main` : GitHub Actions teste, compile, vérifie sur Windows et publie la release `vX.Y.Z`
3. Tous les navigateurs installés la récupèrent au prochain lancement
