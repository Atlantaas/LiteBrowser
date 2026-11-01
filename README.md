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
- Onglets, page d'accueil avec recherche et raccourcis vers tes sites
- Choix du moteur : Google, DuckDuckGo, Bing, Qwant, Ecosia
- Favoris (Ctrl+D), historique, suggestions dans la barre d'adresse
- Thème clair / sombre, mode sombre forcé sur les sites
- Plein écran (F11), vidéos en plein écran

## Raccourcis
Ctrl+T nouvel onglet · Ctrl+W fermer · Ctrl+Maj+T rouvrir · Ctrl+Tab onglet suivant · Ctrl+1…9 aller à un onglet ·
Ctrl+L barre d'adresse · Ctrl+D favori · F5 actualiser · Alt+←/→ précédent/suivant · F11 plein écran

## Compiler soi-même
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out
```
(depuis Linux/macOS, ajouter `-p:EnableWindowsTargeting=true`)

## Publier une mise à jour
1. Augmenter `<Version>` dans `LiteBrowser.csproj`
2. Compiler, puis créer une release GitHub avec le tag `vX.Y.Z` et `LiteBrowser.exe` en pièce jointe
3. Tous les navigateurs installés la récupèrent au prochain lancement
