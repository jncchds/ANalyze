<p align="center">
  <img src="desktop/build/icon.png" alt="ANalyze logo" width="96" height="96">
</p>

<h1 align="center">ANalyze</h1>

<p align="center">
  <b>See what takes space on your drives, find duplicates, and clean up safely.</b><br>
  One Docker container (or a desktop app) with a web UI — nothing on disk changes until you approve a plan.
</p>

<p align="center">
  <a href="https://hub.docker.com/r/jncchds/analyze"><img alt="Docker pulls" src="https://img.shields.io/docker/pulls/jncchds/analyze?logo=docker&logoColor=white&color=0f766e"></a>
  <a href="https://hub.docker.com/r/jncchds/analyze/tags"><img alt="Image size" src="https://img.shields.io/docker/image-size/jncchds/analyze/latest?logo=docker&logoColor=white&color=0f766e"></a>
  <a href="https://github.com/jncchds/ANalyze/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/jncchds/ANalyze?color=f5a524"></a>
  <a href="https://github.com/jncchds/ANalyze/actions/workflows/docker.yml"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/jncchds/ANalyze/docker.yml?branch=main"></a>
  <a href="LICENSE"><img alt="License: GPL v3" src="https://img.shields.io/badge/license-GPL--3.0-blue"></a>
</p>

<p align="center">
  <a href="#quick-start">Quick start</a> ·
  <a href="#desktop-app">Desktop app</a> ·
  <a href="#configuration">Configuration</a> ·
  <a href="#how-actions-stay-safe">Safety</a> ·
  <a href="#integrations">Integrations</a> ·
  <a href="#development">Development</a>
</p>

<!-- dockerhub:github-link -->

## Features

| | Feature | What it does |
|:-:|---|---|
| 📊 | **Overview** | Totals; size by category, type, extension, age and file size; largest files; "heaviest folders" (by files directly inside, so parents don't drown out the real culprits). |
| 🗂️ | **Browse** | The folder tree as a sized list or a treemap, with multi-select and integration labels (e.g. Plex titles). Compares each folder with the disk, so files that aren't scanned yet show up too. |
| 🔎 | **Search** | By name, path, category, type, extension, tag, size, age, folder name, duplicates, hard links, sparse files… Every result set can become a cleanup plan. |
| 👯 | **Duplicates** | Same size → quick hash → full hash (xxHash128). Hard links are recognized and never counted. *Similar folders* finds folder pairs that share many identical files (copied backups, extracted archives). |
| 💡 | **Insights** | Dependency folders (node_modules, venvs…), caches & temp files, incomplete downloads, OS/NAS junk, files named like copies, huge/stale files, empty files and folders, broken symlinks, growth since the previous scan. |
| 🏷️ | **Categories & rules** | ~35 built-in rules and 19 categories out of the box; edit them or add your own with a live preview of matching files; import/export as JSON. |
| ✨ | **AI** *(optional)* | Any OpenAI-compatible API (OpenAI, Ollama, LM Studio, vLLM, OpenRouter…) classifies files no rule recognizes and proposes new rules for you to accept. |
| 👁️ | **Preview** | Images, video, audio, PDF, text/code (highlighted), Markdown (rendered), archive listings, embedded metadata (EXIF, tags), and a hex viewer for everything else. |
| 🧹 | **Actions** | Trash, move (with path templates like `Sorted/{category}/{mtime:yyyy}/{name}`), delete, or replace duplicates with hard links. Every change is a **plan** you review first; trash and moves can be undone. |
| 🔌 | **Integrations** | **Plex**, **Jellyfin**, **Emby**, **Sonarr**, **Radarr**, **Lidarr**, **qBittorrent**, **Transmission**, **SABnzbd**, **Immich**, **PhotoPrism**, plus **Docker** (works out the path mappings between containers) and **ANalyze parent** (checks a backup kept on another ANalyze instance). |
| 🛡️ | **Integrity** *(optional)* | Hash every file and re-verify old hashes to catch bit rot: files whose content changed while size and date stayed the same are flagged as *possibly corrupted*. |
| 📜 | **Activity log** | What ANalyze is doing, step by step and live: scan phases with counts and times, skipped folders and why, unreadable files, integration syncs, plans and AI runs. Hover a step name in the sidebar for a short explanation. |
| 📝 | **Release notes** | What changed in each version, right in the UI (click the version next to the logo). |

## Quick start

```yaml
services:
  analyze:
    image: jncchds/analyze:latest
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      ANalyze__Roots__0: /library
      # ANalyze__Password: "change-me"
    volumes:
      - analyze-data:/data
      - /srv/nas:/library          # read-write: actions enabled
      # - /srv/media:/media:ro     # read-only: analysis only (add ANalyze__Roots__1: /media)
volumes:
  analyze-data:
```

Open <http://localhost:8080> and press **Scan now** in the sidebar. With several roots, pick one in the scope selector to
get **Scan &lt;root&gt;** next to **Scan all**; Settings also has a scan button per root. The first scan reads every folder;
later scans only look at what changed (size, mtime, inode), so they are much faster. Schedule automatic scans in **Settings**.

> [!IMPORTANT]
> The container runs as **root** so it can read — and, when allowed, move or delete — files of every user on the share.
> Folders it creates (move targets, trash) take the owner of their parent folder. To guarantee nothing is ever changed,
> mount roots with `:ro` or set `ANalyze__ReadOnly=true`.

### Scanning

- **Live results** — results are stored while the scan runs (at least every 2 seconds), so Browse, Search and the
  integrations see folders as soon as they are walked.
- **Other filesystems** — disks, shares, ZFS datasets or btrfs subvolumes mounted *below* a root are entered unless
  *Descend into other filesystems* is turned off in **Settings → Scanning**. Then they are marked "not scanned" in Browse
  and listed in the activity log, and you choose the ones to enter under *Other filesystems to enter* (e.g.
  `/mnt/main/media`, which also covers the datasets below it) or with **Enter this filesystem** in Browse.
- **Ignoring** — skip folders with *Ignore* in Settings (names like `.snapshot`, paths from the root like
  `Media/Cache/**`, or full paths like `/mnt/main/backups`) or with **Ignore** in Browse.
- **Not scanned yet** — Browse compares each folder with the disk: what is there but not scanned yet, or ignored, is
  listed under the folder with buttons to scan the folder or (stop) ignoring an item. Folders in that list open, as deep
  as they go, read live from the disk.

## Desktop app

ANalyze also runs as a desktop app for Windows, macOS and Linux: the same server and UI in a window, for analyzing the
disks of the computer it runs on. Download it from the [GitHub releases](https://github.com/jncchds/ANalyze/releases)
— each version that reaches `main` gets a release `v<VERSION>` automatically.

| OS | Files |
|---|---|
| 🪟 Windows (x64) | `ANalyze-<version>-win-x64.exe` (installer) or `.zip` |
| 🍎 macOS (Apple Silicon / Intel) | `ANalyze-<version>-mac-arm64.dmg` / `-mac-x64.dmg`, or `.zip` |
| 🐧 Linux (x64 / arm64) | `ANalyze-<version>-linux-x86_64.AppImage` / `-arm64.AppImage`, or `.tar.gz` |

On first start, pick the folders or drives to analyze; add or remove them later in **Settings → Roots** or with
**File → Add folder…**. The app's data (index, thumbnails, logs) is in its user-data folder (**File → Open data folder**).
Video thumbnails need `ffmpeg` on the `PATH` (on macOS, Homebrew's is found too). Hard-link dedupe is only available on Linux.

> [!NOTE]
> The apps are not code-signed: Windows SmartScreen asks to confirm (*More info → Run anyway*); on macOS, if the app is
> reported as damaged, run `xattr -cr /Applications/ANalyze.app`.

The bundled server listens on `127.0.0.1` only and accepts only requests from the app's window (a random token per launch).

Build it yourself (needs .NET 10 and Node 22): `cd desktop && npm ci && npm run dist` → `desktop/dist/`.
For development: `npm run server` once (publishes the server into `desktop/server/<arch>`), then `npm start`.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `ANalyze__Roots__0`, `__1`, … | `/library` if it exists | Folders to analyze (one per mount). |
| `ANalyze__ReadOnly` | `false` | Analysis only; no actions even on writable mounts. |
| `ANalyze__Password` | — | Protects the UI and API. **Set it** if the port is reachable by others. |
| `ANalyze__ApiToken` | — | Token for scripts: `Authorization: Bearer <token>`. |
| `ANalyze__DataDir` | `/data` | Database (`analyze.db`, SQLite), thumbnails, logs, encryption keys. Keep it on a volume. |
| `ANalyze__Integrations__<id>__<field>` | — | Seeds an integration's settings until they are saved in the UI (see [Plex](#plex)). |
| `TZ` | `UTC` | Time zone for schedules and dates. |

Everything else (scan schedule, ignore patterns, duplicate search, hashing threads, hashing every file and re-verifying
hashes, trash retention, AI) is set in **Settings**.

Build without ffmpeg (no video thumbnails, smaller image): `docker build -f docker/Dockerfile --build-arg FFMPEG=false .`

## How actions stay safe

1. Selecting files and choosing **Clean up…** creates a **draft plan** — nothing changes yet.
2. The review page lists every item with its target; untick anything to keep. Conflicts (destination exists) are
   resolved by the chosen policy (rename/skip/overwrite); items on read-only roots are excluded.
3. **Apply** re-checks each item right before it runs: still there, same size and mtime. Duplicate removals also re-hash
   both copies, so a file that changed since the scan is skipped.
4. **Trash** moves items to `<root>/.analyze-trash/<plan>/…` (an instant rename on the same disk). It is emptied after
   the retention period (Settings) or from **Actions → Trash**. **Undo** restores trashed and moved items.

## Duplicates

Files are grouped by size; a quick hash (first/middle/last 64 KB) filters candidates, and only colliding files get a full
xxHash128. Hashes are cached and recomputed only for changed files. The dedupe wizard keeps the oldest, newest,
shortest-path, deepest-path, or a copy inside preferred folders, and trashes, deletes or **hard-links** the others
(hard links keep every path working while storing the data once).

## Rules and categories

Rules run top to bottom on every file. Conditions combine with *all*/*any* and can test name, path, extension, MIME type,
kind, size (`100MB`), days since modified/accessed, any parent folder name (`node_modules`) or depth, with operators
equals / contains / starts / ends / glob / regex / one of / greater / less, optionally negated. The first rule that sets
a category wins; tags from all matching rules add up. A category you set by hand (on a file or a whole folder) always
wins; AI only fills gaps.

## AI

**Settings → AI**: base URL, key and model of any OpenAI-compatible server (e.g. `http://ollama:11434/v1` with `qwen2.5`).
By default only paths, names, sizes, types and dates are sent; optionally the first 2 KB of text files.

- **Classify uncategorized** sends the largest unexplained files in batches. Answers above the confidence threshold are
  applied (and cached by content hash); the rest wait in **AI → Review**.
- **Suggest rules** summarizes uncategorized files by extension and folder and asks for reusable rules; each suggestion
  shows how many files it would match before you add it.

## Integrations

Integrations are plugins with their own settings form, sync job, API and page. They label files and folders, and the
labels appear in Browse and Search; click a label to open that integration's page at the title or folder it belongs to.
Turn one on under **Integrations**: enabling it also adds its page to the sidebar, and disabling it hides it again.

| Integration | What it adds |
|---|---|
| **Plex**, **Jellyfin**, **Emby** | Movies, shows and music labelled in Browse; titles ranked by size and watch state (*watched long ago*, *never watched*); media in library folders the server does not know. |
| **Sonarr**, **Radarr**, **Lidarr** | Managed files labelled with quality; **files the app does not track** in its root folders (old upgrades, extras, failed imports); **orphan folders**; **Unmonitor** a title before cleaning it up so it is not downloaded again. |
| **qBittorrent**, **Transmission**, **SABnzbd** | Seeding and downloading items are **protected** — cleanup plans skip them; finished downloads ranked by size and age; files in the download folders the client **no longer knows**; library files **copied instead of hard-linked** (fix them in Duplicates → Dedupe with *hard link*). |
| **Immich**, **PhotoPrism** | Library photos and videos labelled; **copies outside the library** (exports, old backups, phone dumps); files in the library folders that were never indexed. |
| **ANalyze parent** | Which files and folders are **synchronized** to your backup server (when it runs ANalyze too), and which files in synchronized folders are **corrupted** (other size or hash there) or **missing** there — per folder in Browse and on the Backup page. |
| **Docker** | Finds each app's container and turns the volume mounts into path mappings for the integrations above, so `/movies` in Radarr is matched to `/library/movies` here without typing anything. Labels folders that other containers mount. |

### Path mappings

Apps in other containers see the same files under other paths. Each integration tries, in order:

1. mappings you entered,
2. mappings from **Docker**,
3. mappings detected from sample files (name + size),
4. file name + size per file.

A mapping you entered covers everything below its prefix (Docker never overrides part of it); for the same prefix the
last one in the list wins. When ANalyze sees one host folder under two paths (`/mnt/main` mounted at `/mnt/main`, and its
dataset `/mnt/main/media` again at `/mnt/media`), a mapping that leads to the path that is not scanned is followed through
ANalyze's own mounts to the one that is — so `/downloads → /mnt/main/media` works with only `/mnt/media` as a root.

To use Docker, mount the socket read-only and enable **Integrations → Docker**:

```yaml
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
```

> [!TIP]
> ANalyze only reads the container list (`/containers/json`). For tighter control, point it at a
> [docker-socket-proxy](https://github.com/Tecnativa/docker-socket-proxy) with `CONTAINERS=1` (`tcp://docker-proxy:2375`).

The app's container is found from the integration's URL (container name, compose service or IP), a published port, or
its image name; name it explicitly under *Container per app* (`radarr=radarr-4k`) when several match.

The integration pages show each file three ways: the app's path, the path ANalyze looked at (after the mappings) and the
file it found. Items not found are summed up in the activity log with the reason (e.g. inside a folder on another filesystem).
**Settings → Host paths** (Docker only) tells ANalyze where its folders are on the host (`/library` → `/mnt/media`), so paths
are shown with their host path too; with the Docker integration on, ANalyze's own mounts fill this in automatically.

### Backups between two ANalyze instances

When your NAS is backed up to another NAS and both run ANalyze, the instance being backed up (the *child*) can check its
backup on the other one (the *parent*, the backup server):

1. On the **backup server**, set `ANalyze__ApiToken` (and `ANalyze__Password` if the port is reachable by others).
2. On **both**, turn on **Settings → Integrity & backups → Hash every file** (and, to catch bit rot, *Re-verify hashes
   after N days*). Without hashes, corrupted files are found by size only.
3. On the **child**, open **Integrations → ANalyze parent**: the backup server's URL (`http://backup-nas:8080`), its token,
   and path mappings when the backup lives under another path there (`/backup/nas1` → `/library`; leave empty to detect
   it from sample files, or press **Suggest**). **Test**, enable, **Sync now**.

How it decides:

- a **file is synchronized** when the backup has a file at the same path;
- a **folder is synchronized** when at least 80% of what is directly inside it (files and sub-folders, by count) is
  synchronized, or when it is identical to the backup's folder (so an empty folder that exists empty on the backup counts too);
- in a synchronized folder, a file is **corrupted** when the backup's copy has another size or content hash, and
  **missing** when the backup does not have it. Files outside synchronized folders are just *not synchronized*.

The **Backup** page shows how much is on the backup, corrupted and missing files, and a folder view with each folder's
share; Browse labels every folder (*Synced*, *Not synced · 40%*) and each corrupted or missing file. The comparison runs
after each scan of the child; the backup server only answers read-only requests.

### Plex

**Integrations → Plex**: server URL, `X-Plex-Token`, enable, **Sync now** (and after every scan if enabled).

- Browse shows which folders are Plex libraries and which files/folders are a movie, show, season, album or track (with
  watched state and a link to Plex Web).
- The **Plex** page ranks movies, shows and artists by size, filters *watched long ago*, *never watched*, *several
  versions* and *files not found*, lists media files inside library folders that Plex does not know, and turns any
  title into a cleanup plan.
- **Plex in another container** usually sees the same files under different paths (`/data/movies` vs `/library/Movies`).
  Leave *Path mappings* empty to let ANalyze detect them from sample files, press **Suggest from files**, or add them by
  hand. Files that still don't match are found by name and size.

Environment seeding example (works the same for every integration id: `sonarr`, `qbittorrent`, `docker`, …):

```bash
ANalyze__Integrations__plex__Enabled=true
ANalyze__Integrations__plex__url=http://192.168.1.10:32400
ANalyze__Integrations__plex__token=xxxxxxxx
ANalyze__Integrations__plex__mappings=/data/movies=/library/Movies;/data/tv=/library/TV
```

## Development

```bash
dotnet build                                   # backend (warnings are errors)
dotnet test tests/ANalyze.Tests                # unit + integration tests (temp folders, real SQLite)
cd src/ANalyze.Web/ClientApp && npm ci && npm run typecheck && npm run lint && npm run build
docker compose -f docker-compose.dev.yml up --build
```

Front-end hot reload: `npm run dev` in `ClientApp` proxies `/api` and `/hubs` to `http://127.0.0.1:5199`.
The design and its rules are described in [AGENTS.md](AGENTS.md); changes per version are in [RELEASE_NOTES.md](RELEASE_NOTES.md).

## Credits

**ANalyze by CHDS** — created and maintained by [CHDS](https://github.com/jncchds), built together with
[Claude](https://claude.com/claude-code) by Anthropic.

Licensed under the [GNU GPL v3](LICENSE).
