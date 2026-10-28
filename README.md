# ANalyze by CHDS

See what takes space on your drives, find duplicates, and clean up safely. ANalyze runs as one Docker container: mount
the folders to analyze, open the web UI, press **Scan now**.

- **Overview** — totals, size by category, type, extension, age and file size; largest files; "heaviest folders"
  (by files directly inside, so parents don't drown out the real culprits).
- **Browse** — folder tree as a sized list or a treemap; multi-select; integration labels (e.g. Plex titles) on files and folders.
- **Search** — by name, path, category, type, extension, tag, size, age, folder name, duplicates, hard links, sparse files…
  Every result set can become a cleanup plan.
- **Duplicates** — same size → quick hash → full hash (xxHash128). Hard links are recognized and never counted.
  "Similar folders" finds folder pairs that share many identical files (copied backups, extracted archives).
- **Insights** — dependency folders (node_modules, venvs…), caches & temp files, incomplete downloads, OS/NAS junk,
  files named like copies, huge/stale files, empty files and folders, broken symlinks, growth since the previous scan.
- **Categories & rules** — ~35 built-in rules and 19 categories out of the box; edit them or add your own with a live
  preview of matching files; import/export as JSON.
- **AI** (optional, any OpenAI-compatible API: OpenAI, Ollama, LM Studio, vLLM, OpenRouter…) — classifies files no rule
  recognizes and proposes new rules for you to accept.
- **Preview** — images, video, audio, PDF, text/code (highlighted), Markdown (rendered), archive listings, embedded
  metadata (EXIF, tags), and a hex viewer for everything else.
- **Actions** — trash, move (with path templates like `Sorted/{category}/{mtime:yyyy}/{name}`), delete, or replace
  duplicates with hard links. Every change is a **plan** you review first; trash and moves can be undone.
- **Integrations** — plugins that cross-reference other apps: **Plex**, **Jellyfin**, **Emby**, **Sonarr**, **Radarr**,
  **Lidarr**, **qBittorrent**, **Transmission**, **SABnzbd**, **Immich**, **PhotoPrism**, plus **Docker**, which works out the path
  mappings between containers for all of them. **ANalyze parent** links two ANalyze instances when one NAS is backed up to another.
- **Integrity** (optional) — hash every file and re-verify old hashes to catch bit rot: files whose content changed while size
  and date stayed the same are flagged as *possibly corrupted*.
- **Release notes** — what changed in each version, right in the UI (click the version next to the logo).

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

Open <http://localhost:8080> and press **Scan now** in the sidebar. The first scan reads every folder; later scans only
look at what changed (size, mtime, inode), so they are much faster. Schedule automatic scans in **Settings**.

The container runs as **root** so it can read — and, when allowed, move or delete — files of every user on the share.
Folders it creates (move targets, trash) take the owner of their parent folder. To guarantee nothing is ever changed,
mount roots with `:ro` or set `ANalyze__ReadOnly=true`.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `ANalyze__Roots__0`, `__1`, … | `/library` if it exists | Folders to analyze (one per mount). |
| `ANalyze__ReadOnly` | `false` | Analysis only; no actions even on writable mounts. |
| `ANalyze__Password` | — | Protects the UI and API. **Set it** if the port is reachable by others. |
| `ANalyze__ApiToken` | — | Token for scripts: `Authorization: Bearer <token>`. |
| `ANalyze__DataDir` | `/data` | Database (`analyze.db`, SQLite), thumbnails, logs, encryption keys. Keep it on a volume. |
| `ANalyze__Integrations__<id>__<field>` | — | Seeds an integration's settings until they are saved in the UI (see below). |
| `TZ` | `UTC` | Time zone for schedules and dates. |

Everything else (scan schedule, ignore patterns, duplicate search, hashing threads, hashing every file and re-verifying hashes, trash retention, AI) is set in **Settings**.

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

Settings → AI: base URL, key and model of any OpenAI-compatible server (e.g. `http://ollama:11434/v1` with `qwen2.5`).
By default only paths, names, sizes, types and dates are sent; optionally the first 2 KB of text files.

- **Classify uncategorized** sends the largest unexplained files in batches. Answers above the confidence threshold are
  applied (and cached by content hash); the rest wait in **AI → Review**.
- **Suggest rules** summarizes uncategorized files by extension and folder and asks for reusable rules; each suggestion
  shows how many files it would match before you add it.

## Integrations

Integrations are plugins (`IIntegration`) with their own settings form, sync job, API and page. They label
files and folders, and the labels appear in Browse and Search. Click a label to open that integration's page at the title or folder it belongs to. Turn one on under **Integrations**: enabling it also
adds its page to the sidebar, and disabling it hides it again.

| Integration | What it adds |
|---|---|
| **Plex**, **Jellyfin**, **Emby** | Movies, shows and music labelled in Browse; titles ranked by size and watch state (*watched long ago*, *never watched*); media in library folders the server does not know. |
| **Sonarr**, **Radarr**, **Lidarr** | Managed files labelled with quality; **files the app does not track** in its root folders (old upgrades, extras, failed imports); **orphan folders**; **Unmonitor** a title before cleaning it up so it is not downloaded again. |
| **qBittorrent**, **Transmission**, **SABnzbd** | Seeding and downloading items are **protected** — cleanup plans skip them; finished downloads ranked by size and age; files in the download folders the client **no longer knows**; library files **copied instead of hard-linked** (fix them in Duplicates → Dedupe with *hard link*). |
| **Immich**, **PhotoPrism** | Library photos and videos labelled; **copies outside the library** (exports, old backups, phone dumps); files in the library folders that were never indexed. |
| **ANalyze parent** | Which files and folders are **synchronized** to your backup server (when it runs ANalyze too), and which files in synchronized folders are **corrupted** (other size or hash there) or **missing** there — per folder in Browse and on the Backup page. |
| **Docker** | Finds each app's container and turns the volume mounts into path mappings for the integrations above, so `/movies` in Radarr is matched to `/library/movies` here without typing anything. Labels folders that other containers mount. |

### Path mappings

Apps in other containers see the same files under other paths. Each integration tries, in order: mappings you
entered, mappings from **Docker**, mappings detected from sample files (name + size), then file name + size per file.
To use Docker, mount the socket read-only and enable **Integrations → Docker**:

```yaml
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
```

ANalyze only reads the container list (`/containers/json`). For tighter control, point it at a
[docker-socket-proxy](https://github.com/Tecnativa/docker-socket-proxy) with `CONTAINERS=1` (`tcp://docker-proxy:2375`).
The app's container is found from the integration's URL (container name, compose service or IP), a published port, or
its image name; name it explicitly under *Container per app* (`radarr=radarr-4k`) when several match.

### Backups between two ANalyze instances

When your NAS is backed up to another NAS and both run ANalyze, the instance being backed up (the *child*) can check its backup on the
other one (the *parent*, the backup server):

1. On the **backup server**, set `ANalyze__ApiToken` (and `ANalyze__Password` if the port is reachable by others).
2. On **both**, turn on **Settings → Integrity & backups → Hash every file** (and, to catch bit rot, *Re-verify hashes after N days*).
   Without hashes, corrupted files are found by size only.
3. On the **child**, open **Integrations → ANalyze parent**: the backup server's URL (`http://backup-nas:8080`), its token, and path mappings
   when the backup lives under another path there (`/backup/nas1` → `/library`; leave empty to detect it from sample files, or press **Suggest**).
   **Test**, enable, **Sync now**.

How it decides:

- a **file is synchronized** when the backup has a file at the same path;
- a **folder is synchronized** when at least 80% of what is directly inside it (files and sub-folders, by count) is synchronized, or when it is
  identical to the backup's folder (so an empty folder that exists empty on the backup counts too);
- in a synchronized folder, a file is **corrupted** when the backup's copy has another size or content hash, and **missing** when the backup
  does not have it. Files outside synchronized folders are just *not synchronized*.

The **Backup** page shows how much is on the backup, corrupted and missing files, and a folder view with each folder's share; Browse labels every
folder (*Synced*, *Not synced · 40%*) and each corrupted or missing file. The comparison runs after each scan of the child; the backup server only
answers read-only requests.

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

```
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

## Credits

ANalyze by CHDS — created and maintained by [CHDS](https://github.com/jncchds), built together with
[Claude](https://claude.com/claude-code) by Anthropic.

Licensed under the GNU GPL v3.
