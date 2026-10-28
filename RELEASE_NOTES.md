# Release Notes

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
