# Release Notes

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
