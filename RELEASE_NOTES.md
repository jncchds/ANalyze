# Release Notes

## v0.1.10 — 2026-10-30

- feat: scans hold the index to themselves only while they walk the folders: file types, categories, archives, hashing, duplicates and re-verification follow in the background ("Processing"), next to integration syncs, rule apply and the AI, and step aside between batches whenever the next scan, a plan or ignored-folder pruning needs the index, then carry on; integrations re-sync right after the walk, the AI runs once types are known, and unfinished hashing resumes after a restart
- feat: backup comparison no longer waits for hashes: the parent's hashes are kept, files hashed here are checked against the backup batch by batch, and every 15 minutes the child reads the hashes the backup server computed since the last sync (new hash feed `GET /backup/hashes`)
- feat: background priority also watches the disks: while other work keeps the roots' disks more than half busy, hashing and the other background work wait
- fix: lowering thread priority no longer reports a failure when a thread ends meanwhile

## v0.1.9 — 2026-10-29

- feat: extracted archives: scans list zip, rar and 7z archives (and, when enabled, tar/tar.gz/.gz/.xz/.zst by decompressing them once) and find, on all roots, the folders that hold their files — with the same paths and sizes, even when the archive's top folder was dropped and when the folder holds other files too. Duplicates → Extracted archives shows complete and partial extractions (missing files, files cut short), checks the extracted files against the CRC32s stored in the archive without reading the archive, and creates plans to remove the archive or the extracted copy; insights and the search filter "extracted" list them
- feat: ignoring a folder (Browse or the ignore list in Settings) removes it from the index at once, so its files leave duplicates, search and insights without waiting for the next scan
- feat: priority setting: full speed, or background mode (low CPU priority, idle disk priority and one worker thread) so the server's other services come first

## v0.1.8 — 2026-10-29

- fix: scans no longer seem stuck on "categorizing" for many minutes on large libraries: the duplicate search (and "hash all files") now shows its step at once, finds files of equal size from an index instead of reading every file record in size order (one random disk read per file), and does that search once instead of twice; the database gets two new indexes on first start
- feat: Browse opens folders that are not scanned yet (or ignored), as deep as they go: their files and subfolders are read live from the disk, with a path back up and Ignore on every item
- feat: the Docker Hub page shows the README (links point to GitHub, plus a link to the repository), updated with every image built from main; the README got a clearer layout

## v0.1.7 — 2026-10-29

- fix: path mappings follow ANalyze's own mounts: when one host folder is visible under two paths (e.g. /mnt/main and its ZFS dataset /mnt/main/media mounted again at /mnt/media), an app path that leads to /mnt/main/media/… finds the file under whichever path is scanned — for Docker mappings, manual mappings that point at a host path, and subfolders
- fix: a manual mapping now covers its whole prefix: Docker's mappings never override any part of it (e.g. a Docker /downloads/movies no longer beats a manual /downloads), and for the same prefix the mapping added last wins
- fix: integration syncs wait for a running scan, plan, AI run or another sync and run one after the other (shown as "Queued"), instead of fighting over the database; settings saves take effect at once and are written to the database as soon as the running job has finished
- feat: Browse compares a folder with the disk: files and folders that are there but not scanned yet, or ignored, are listed with buttons to scan the folder or ignore/stop ignoring them; "Ignore this folder" and "Scan this folder" in the header
- feat: other filesystems can be chosen one by one: with "Descend into other filesystems" off, "Other filesystems to enter" (Settings) or "Enter this filesystem" (Browse) scans just those datasets — no need to mount a dataset separately to scan it
- feat: ignore patterns accept full paths (/mnt/main/backups)
- feat: scan a single root from the sidebar ("Scan <root>" next to "Scan all") or from the roots list in Settings

## v0.1.6 — 2026-10-29

- feat: Activity log page — what ANalyze is doing step by step: each scan phase with counts and times, folders it skipped and why, unreadable files, integration syncs (mappings used, items not found and why), plans and AI runs; follows live, filters by level, area and text. Kept in memory (last 20,000 lines); the log files keep the full history
- feat: hovering a scan or sync step (walking, detecting, hashing…) explains what it does
- feat: integration pages show the app's path, where ANalyze looked for it and the file it found; with host paths (Settings, or ANalyze's own Docker mounts) the host path is shown too, also in file details and roots
- fix: folders on another filesystem (separate disk, share, dataset, subvolume) below a root are scanned by default; when that setting is off they are marked "not scanned" in Browse and logged, instead of silently looking empty and hiding files from integrations
- fix: scans store their results at least every 2 seconds and hashes of big files as they come, and open pages refresh while a scan runs, so folders no longer look empty until the scan ends
- fix: the GitHub release step no longer fails on Docker's build record artifact

## v0.1.5 — 2026-10-28

- feat: desktop app for Windows, macOS and Linux (Electron, server bundled): pick folders to analyze with the system folder picker, add or remove them in Settings; built by the same CI pipeline as the Docker image and published as the GitHub release for each version (from VERSION, no tag needed)
- fix: root and path checks work with Windows paths, and ffmpeg is found on Windows

## v0.1.4 — 2026-10-28

- feat: "ANalyze parent" integration links two ANalyze instances when one NAS is backed up to another: shows which files and folders are synchronized to the backup server (a folder counts at 80% of its direct content, or when identical) and which files in synchronized folders are corrupted (other size or hash there) or missing there — per folder in Browse and on a Backup page
- feat: optional hashing of every file and periodic re-verification that flags files whose content changed while size and date stayed the same (possible corruption) — new insight, search filter and a notice in the file details
- feat: integration labels in Browse, Search and the file details are clickable: they open the integration's page at that title (Plex, Jellyfin, *arr, download clients, photos) or folder (backup)
- fix: with every file hashed, "Has duplicates" search and dedupe plans still only consider files that have another copy

## v0.1.3 — 2026-10-28

- fix: folders no longer show 0 bytes during a first scan (running totals) or after a scan that stopped or failed (sizes are recomputed)
- fix: a folder on another filesystem that is not scanned drops entries left from an earlier scan instead of showing them under a 0-byte folder
- fix: previews work as soon as a file is found: the file type comes from the extension right away instead of only after the whole scan

## v0.1.2 — 2026-10-28

- fix: the Docker image build includes RELEASE_NOTES.md (the 0.1.1 image failed to build)

## v0.1.1 — 2026-10-28

- feat: release notes page in the UI (linked from the version pill, which marks versions you haven't read yet)
- feat: "ANalyze by CHDS" title and credits to CHDS and Claude
- feat: Sonarr, Radarr and Lidarr integrations — managed files labelled, files and folders they don't track, unmonitor before cleanup
- feat: qBittorrent, Transmission and SABnzbd integrations — seeding/downloading files are protected from cleanup plans, finished downloads, orphans in download folders, copies that should be hard links
- feat: Jellyfin and Emby integrations — titles by size and watch state, media the server doesn't know
- feat: Immich and PhotoPrism integrations — copies of library photos elsewhere on disk, files never indexed
- feat: Docker integration — finds each app's container and turns its volume mounts into path mappings automatically (also used by Plex)
- feat: disabled integrations collapse on the Integrations page; enabling one adds its page to the sidebar
- fix: every build of main publishes the `latest` Docker image tag

## v0.1.0 — 2026-10-28

- feat: incremental scanner (statx metadata, inode-aware, cross-filesystem guard, ignore globs, scheduled and per-folder rescans) storing everything in SQLite
- feat: overview with size by category, type, extension, age and file size, largest files and heaviest folders; folder browser with list and treemap
- feat: search across all files by name, path, category, type, extension, tag, size, age, folder name, duplicates, hard links, sparse files and broken links
- feat: 19 built-in categories and ~35 built-in rules (media, documents, dev junk, caches, incomplete downloads, NAS/OS metadata, backups, logs, screenshots, copies…), fully editable with live preview, import and export
- feat: duplicate finder (size → quick hash → xxHash128, hard links recognized), similar-folder pairs and a dedupe wizard (keep oldest/newest/shortest/deepest/preferred folder; trash, delete or hard-link the rest)
- feat: insights for reclaimable space and growth since the previous scan
- feat: previews for images, video, audio, PDF, text/code, Markdown, archives, metadata and a hex viewer
- feat: plan → review → apply actions (trash, move with path templates, delete, hard link) with per-item verification, trash with retention, and undo
- feat: optional OpenAI-compatible AI to classify uncategorized files and suggest rules
- feat: integration plugin framework with Plex as the first plugin (labels in the browser, titles ranked by size and watch state, automatic path-mapping detection)
- feat: optional password and API token, read-only mode, Docker image running as root with owner-preserving writes
