<p align="center">
  <img src="assets/logo.png" alt="TimeCap — Clip the moment" width="300">
</p>

<h1 align="center">TimeCap</h1>
<p align="center"><em>Clip the moment</em> — Enregistreur de clips en arrière-plan (style Medal.tv / OBS)</p>

---

TimeCap (exécutable `ScreenClipTool.exe`) est une application Windows légère qui tourne en arrière-plan (dans votre barre des tâches). Elle enregistre votre écran **en continu** sans impacter vos performances. Dès que vous appuyez sur un raccourci clavier, elle sauvegarde instantanément un clip des dernières secondes ou minutes écoulées.

## ✨ Points forts

* **Zéro impact sur vos jeux** : L'enregistrement est géré à 100 % par votre carte graphique NVIDIA.
* **Sauvegarde instantanée** : Les clips sont générés en une fraction de seconde sans faire ramer l'ordinateur.
* **Léger et portable** : Pas besoin d'installation complexe, l'application s'exécute directement.

## 📥 Téléchargement

La dernière version prête à l'emploi est disponible dans la section **[Releases](../../releases)** du dépôt : téléchargez `ScreenClipTool.exe` (exécutable unique et autonome, aucun runtime .NET à installer), placez `ffmpeg.exe` et `ffprobe.exe` à côté (voir prérequis ci-dessous), puis lancez.

---

## 🚀 Guide de démarrage rapide

### 1. Prérequis matériel et logiciel
* **Système** : Windows 10 ou Windows 11.
* **Carte graphique** : NVIDIA GeForce (Série RTX 40xx/50xx recommandée pour le format AV1, cartes plus anciennes compatibles grâce à un basculement automatique en HEVC).
* **FFmpeg** (le moteur vidéo) : 
  1. Téléchargez la version « full » récente sur [gyan.dev](https://www.gyan.dev/ffmpeg/builds/).
  2. Extrayez le fichier téléchargé et placez **`ffmpeg.exe`** (et idéalement `ffprobe.exe`) directement dans le même dossier que l'application `ScreenClipTool.exe`.

### 2. Lancement
Double-cliquez sur `ScreenClipTool.exe`. 
* Une fenêtre sombre s'ouvre avec un indicateur **REC** (qui confirme que l'enregistrement en arrière-plan fonctionne) et la liste de vos clips récents.
* Si vous fermez cette fenêtre, l'application reste active et se réduit dans la zone de notification (à côté de l'horloge Windows). Un double-clic sur l'icône permet de la rouvrir.

---

## ⚙️ Configuration et Réglages

Vous pouvez tout configurer directement depuis l'interface graphique de l'application (bouton Réglages). Les changements sont appliqués immédiatement sans avoir besoin de redémarrer le programme.

### Les raccourcis clavier (Hotkeys)
Vous pouvez configurer plusieurs touches pour des durées différentes (la liste est triée de la durée la plus courte à la plus longue). Par exemple :
* `Alt + X` ➔ Sauvegarder les 15 dernières secondes.
* `Alt + C` ➔ Sauvegarder les 10 dernières minutes.
* `F11` ➔ Sauvegarder la totalité de la mémoire tampon disponible.

> ⚠️ **Note importante** : Si vous choisissez une touche seule sans modificateur (comme `F11` sans Alt ou Ctrl), cette touche sera "bloquée" par l'application et vos jeux ne la recevront pas tant que ScreenClipTool est ouvert. Privilégiez des combinaisons (ex: `Alt + F11`).

### Options principales du fichier `config.json`
Pour les utilisateurs avancés, un fichier `config.json` est créé automatiquement à côté de l'exécutable (ou dans votre dossier `%APPDATA%`). Voici les options principales que vous pouvez modifier :

| Paramètre | Description |
|---|---|
| `output_dir` | Le dossier où seront enregistrés vos clips finaux (ex: `"C:/Clips"`). |
| `max_buffer_minutes` | La durée maximale de l'enregistrement en continu (ex: `15`). Les vidéos plus vieilles sont automatiquement effacées pour ne pas saturer votre disque dur. |
| `fps` | La fluidité de la vidéo (`60` ou `30`). |
| `audio_enabled` | `true` (activé) ou `false` (désactivé) pour enregistrer le son de votre PC. |
| `mic_enabled` | `true` ou `false` pour inclure votre microphone sur une piste audio séparée. |
| `output_idx` | Si vous avez plusieurs écrans, `0` désigne le premier écran, `1` le second, etc. |

---

## 🛠️ Pour les développeurs (Compilation)

Si vous préférez compiler l'application vous-même, le SDK .NET 8 est requis :

```powershell
# Compiler en mode Release :
dotnet build -c Release

# Créer un exécutable unique et autonome (sans besoin d'installer .NET sur la machine cible) :
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Outils de diagnostic intégrés
Vous pouvez tester le bon fonctionnement de votre configuration vidéo/audio en ligne de commande :
* `ScreenClipTool.exe --selftest resultat.json` : Lance un enregistrement test de 9 secondes et vérifie automatiquement si le fichier généré est valide.
* `ScreenClipTool.exe --uitest` : Permet de tester et valider l'interface graphique sans démarrer le moteur d'enregistrement.

### Architecture

Le détail du pipeline (ddagrab → NVENC → segments → concat), les comportements de robustesse (restart auto, job object, keepalive audio) et les limites connues sont documentés dans [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
