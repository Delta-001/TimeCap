# Architecture & détails techniques — TimeCap (ScreenClipTool)

Application Windows en zone de notification qui capture l'écran **en continu** dans un
buffer circulaire de segments mp4, et permet de sauvegarder un clip **a posteriori**
d'une durée variable (raccourci clavier → durée), sans réencodage.

- **Vidéo** : ffmpeg `ddagrab` (Desktop Duplication API) → chaîne d'encodeurs essayés
  dans l'ordre par un test d'encodage réel : `av1_nvenc` → `hevc_nvenc` → `h264_nvenc`
  (pipeline 100 % GPU, frames D3D11 directes) → `hevc_amf`/`h264_amf` (AMD) →
  `av1/hevc/h264_qsv` (Intel) → `libx264` (logiciel, fonctionne partout) — les
  familles non-NVENC passent par `hwdownload` + conversion NV12. Chaque ffmpeg
  candidat (config → dossier de l'app → PATH → installation gérée) est validé :
  un vieux build inutilisable dans le PATH est ignoré au profit du suivant.
- **Audio** : loopback WASAPI du bureau (NAudio) + micro optionnel sur piste séparée,
  streamés en PCM vers ffmpeg via named pipes, muxés au niveau des segments.
- **Export** : concat demuxer en `-c copy` → quasi instantané, granularité = 1 segment (2 s).
- **Multi-écrans** : une session de capture (process ffmpeg + audio) par écran
  sélectionné, buffers séparés (`buffer/screenN`), export groupé dans un dossier
  `Clip_<date>` contenant `Screen1.mp4`, `Screen2.mp4`… (une seule → fichier plat).
- **UI** : lecteur intégré (MediaElement, repli automatique vers le lecteur système
  si le codec manque), partage par copie presse-papiers (FileDrop) et glisser-déposer.

## Auto-tests

```powershell
TimeCap.exe --selftest resultat.json
```

Capture ~9 s (vidéo + audio), exporte un clip de 5 s, vérifie taille/durée/flux via
ffprobe et écrit un rapport JSON. Validé sur la machine cible : AV1 NVENC + AAC, ✔.

`--uitest [fichier]` ouvre les deux fenêtres (thème/templates) sans capturer, écrit
`ok` puis quitte — utile pour valider l'UI en CI.

## Configuration (`config.json`)

Portable si un `config.json` existe à côté de l'exe, sinon `%APPDATA%\ScreenClipTool\config.json`
(créé au premier lancement). Sauvegarde immédiate depuis la fenêtre de réglages, avec
réenregistrement dynamique des hotkeys (unregister/register), sans redémarrage.

| Clé | Rôle |
|---|---|
| `fps`, `cq` | Cadence de capture et qualité NVENC (CQ ~27 ≈ 8-10 Mbps en 1440p60 en jeu) |
| `segment_length_s` | Taille des segments = granularité des clips (keyframe forcée à chaque frontière) |
| `max_buffer_minutes` | Fenêtre max du buffer : les segments plus vieux sont supprimés (borne le disque) |
| `output_idx` | Index du moniteur capturé (ddagrab) |
| `buffer_dir` | Dossier des segments (défaut : `%TEMP%\ScreenClipTool\buffer`) |
| `ffmpeg_path` | Chemin explicite de ffmpeg.exe (défaut : dossier de l'app → PATH → installation gérée) |
| `duration_seconds` | Nombre de secondes, ou `"full"` = tout le buffer |
| `resolution` | Informatif : ddagrab capture toujours la résolution native de l'écran |

Les hotkeys sont triés par durée croissante (`"full"` en dernier) au chargement et
à l'affichage.

## Structure

```
/Capture   → wrapper process ffmpeg, restart auto, cycle de vie des segments, job object anti-orphelin
/Audio     → capture WASAPI loopback + micro (NAudio) → named pipes, keepalive silence
/Input     → hotkeys globaux RegisterHotKey, registre dynamique depuis la config
/Export    → sélection des N derniers segments + concat -c copy
/UI        → fenêtre principale (statut, buffer, clips), tray icon, notifications,
             fenêtre de réglages (capture de combos), thème sombre partagé (Theme.xaml)
/Config    → chargement/sauvegarde JSON (snake_case, durée polymorphe int|"full")
```

## Comportements notables

- **Arrêt propre** : `q` envoyé sur stdin de ffmpeg → le segment en cours est finalisé
  avant la sortie (pas de perte des dernières secondes). Un *job object* Windows tue
  ffmpeg si l'app meurt brutalement (pas de process orphelin).
- **Crash de ffmpeg** : redémarrage auto (max 5/min), numérotation des segments
  poursuivie ; un éventuel segment tronqué par un crash est détecté (ffprobe) et purgé
  au démarrage suivant pour ne pas casser les exports.
- **Silence** : un flux de silence WASAPI est joué en permanence, sinon le loopback ne
  délivre aucune donnée quand rien ne joue et le muxage attendrait l'audio.
- **Changement de fps/cq/audio** : le buffer est vidé et ffmpeg relancé (les segments
  doivent partager les mêmes paramètres de codec pour rester concaténables en `-c copy`).
- **Export** : le segment en cours d'écriture est exclu → un clip couvre les N derniers
  segments *terminés* (perte max : `segment_length_s` secondes en bout de clip).

## Limites connues / écarts assumés

- **Touche sans modificateur** : `RegisterHotKey` **consomme** la frappe : le jeu ne la
  reçoit pas tant que l'outil tourne. L'avertissement affiché dans les réglages reflète
  ce comportement réel. Alternative si le pass-through devient nécessaire : hook clavier
  bas niveau (`WH_KEYBOARD_LL`) non bloquant.
- `resolution` est informatif : ddagrab capture l'écran à sa résolution native (pas de
  scaling GPU dans ce pipeline).
- Léger décalage A/V possible (~100 ms) au démarrage d'une session : les timestamps
  audio démarrent à la connexion du pipe. Négligeable en pratique, à resynchroniser via
  `-use_wallclock_as_timestamps` si besoin.

## Installation automatique de ffmpeg (plug-and-play)

Au premier lancement, si aucun ffmpeg n'est trouvé (ni à côté de l'exe, ni dans le
PATH), l'application télécharge le build « essentials » de gyan.dev (~30 Mo, inclut
ddagrab + NVENC) — avec bascule automatique vers les builds officiels BtbN sur GitHub
(~160 Mo, URL stable) si gyan.dev est inaccessible — puis extrait
`ffmpeg.exe` / `ffprobe.exe` vers `%LOCALAPPDATA%\TimeCap\ffmpeg`, vérifie que le
binaire démarre, et lance la capture.
La progression s'affiche dans la barre de statut de la fenêtre principale. Déposer
manuellement `ffmpeg.exe` à côté de `TimeCap.exe` (ou dans le PATH) reste prioritaire
sur l'installation gérée.
