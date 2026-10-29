# Release Notes

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
