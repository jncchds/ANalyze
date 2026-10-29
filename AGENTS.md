# ANalyze — design notes

Authoritative description of how ANalyze works. Update it with every architectural or schema change.

## Shape

```
React SPA (static files in ASP.NET wwwroot — single container)
        ↕ REST /api/v1 + SignalR /hubs/jobs
ASP.NET Core 10 minimal APIs
        ↕ EF Core 10 (schema, CRUD) + Microsoft.Data.Sqlite (bulk paths)
SQLite (WAL) in /data/analyze.db
```

| Project | Purpose |
|---|---|
| `ANalyze.Domain` | Entities, enums, rule model + `RuleEngine`, `Builtins` (seeded categories, rules, templates). No dependencies. |
| `ANalyze.Infrastructure` | `AnalyzeDbContext` + migrations, `SqliteSetup` (connection string + PRAGMAs), `SettingsStore`, `SecretProtector`, `RootRegistry` (roots, writability probe, safe paths), `Native` (statx, link, realpath, lchown), `JobBoard`/`JobLock`, `Logging/ActivityLog` (in-memory activity log) + `Format`. |
| `ANalyze.Scanner` | `Walker` (incremental tree mirror), `Detector` (type by extension/magic), `Categorizer` (rules), `DuplicateFinder` + `Hashing`, `ScanService` (pipeline), `ScanQueue`/`ScanWorker`/`ScanScheduler`, `EntryMaintenance`. |
| `ANalyze.Preview` | Thumbnails (SkiaSharp, ffmpeg for video), paged text/hex reads, archive listing (SharpCompress), metadata (MetadataExtractor, TagLib). |
| `ANalyze.Actions` | `PlanBuilder` (manual/template/dedupe plans), `PathTemplate`, `DedupeSelector`, `PlanExecutor` + `PlanQueue`, `TrashService` + janitor. |
| `ANalyze.Ai` | `AiService` (classify, suggest rules), `Prompts` (every prompt and JSON schema), `IAiClientFactory` (OpenAI-compatible via Microsoft.Extensions.AI). |
| `ANalyze.Integrations` | Plugin contract `IIntegration`, `IntegrationHost` (settings with encrypted secrets, sync, auto-sync after scans), `Annotations` writer, `PathMatcher`, `IntegrationPaths` (configured + automatic mappings), shared HTTP clients, and `Items/` — the `ItemIntegration` base class with its shared endpoints. |
| `ANalyze.Integrations.Plex` | Plex plugin: `PlexClient`, `PlexIntegration`, `PlexEndpoints`. |
| `ANalyze.Integrations.Arr` | Sonarr, Radarr, Lidarr (`ArrIntegration` base). |
| `ANalyze.Integrations.Downloads` | qBittorrent, Transmission, SABnzbd (`DownloadIntegration` base). |
| `ANalyze.Integrations.Jellyfin` | Jellyfin and Emby (`MediaServerIntegration` base). |
| `ANalyze.Integrations.Photos` | Immich and PhotoPrism. |
| `ANalyze.Integrations.Backup` | Parent–child link between two ANalyze instances: `BackupExport` (parent API), `ParentClient`, `BackupSync` (comparison), `BackupAnnotations`, `ParentIntegration`, `BackupEndpoints`. |
| `ANalyze.Integrations.Docker` | Docker: `DockerClient` (unix socket or TCP), `DockerState`, `DockerPaths` (`IPathMappingSource`), `MountMath`. |
| `ANalyze.Web` | `Program.cs`, `Endpoints/*`, `JobHub`, auth, SPA in `ClientApp/`. |
| `desktop/` (Node, not .NET) | Electron app that bundles the published server; see "Desktop app". |

## Data model

- **Entries** — one row per file or directory: `RootId`, `ParentId`, `RelPath` (relative to the root, `/`-separated, `""` = root dir), stat data
  (size, allocated size, mtime/atime/ctime, inode, device, link count), detection (`Ext`, `Mime`, `Kind`, `Detected`), category (`CategoryId`,
  `CategorySource` none/rule/ai/manual), rule tags (`|a|b|`), hashes (`QuickHash`, `FullHash`, `HashedAt` = when hashed or last verified, `CorruptAt` = when a re-read found other content with
  unchanged size and mtime; the walker clears all four when a file changes). Directories carry subtree aggregates
  (`TotalSize`, `TotalAlloc`, `FileCount`, `DirCount`); files have `TotalSize = Size`, `FileCount = 1`. `OtherFilesystem` marks a folder
  where another filesystem is mounted that the walk listed but did not enter (cross-filesystem scanning off); `Error` marks folders with
  an unreadable folder at or below them.
  - Subtree queries use the unique index `(RootId, RelPath)` with the range `RelPath >= 'p/' AND RelPath < 'p0'` (`'0'` follows `'/'`).
    Never use `LIKE 'p/%'` for this — it cannot use the index.
- **DirSizeHistory** — `(ScanId, EntryId)` sizes of directories up to `HistoryDepth` after each full scan → growth view and pinned deltas.
- **Categories**, **Rules** (`Condition` and `Tags` stored as JSON), **ActionTemplates**.
- **ActionPlans** / **ActionItems** — every change to disk. Items remember what they expect (size, mtime, hash) and where things went (`UndoPath`).
- **AiResults** (cache by content hash or path+size+mtime; unapplied rows = review queue), **AiRuleSuggestions**.
- **Annotations** — integration labels per entry (`Source`, `Kind`, `Label`, `State`, `Url`). Integrations replace their own rows on each sync.
- **PlexItems** — Plex media parts with the matched `EntryId` and `CheckedPath` (the Plex path after the path mappings: where ANalyze looked).
- **IntegrationItems** — files/folders known to every `ItemIntegration` plugin (`Source`, `Kind`, `ExternalId`, `Title`, `GroupKey`/`GroupTitle`,
  `ExternalPath`, `CheckedPath` (after the path mappings), `Size`, `IsDir`, `State`, `Detail`, `AddedAt`/`LastUsedAt`, `PlayCount`, `Url`, matched `EntryId` + `MatchedBy`). A plugin replaces
  its own rows on each sync.
- **BackupFiles** — written by the `parent` integration (the parent is this instance's backup server): one row per local file and folder inside
  the backed-up folders (`State`: files synced/corrupt/missing/unsynced, folders synced/unsynced; `IsDir`, `EntryId`, `LocalRootId`/`LocalRelPath`,
  `Size`, `ParentSize` when the parent has the path, `Verified` = equal content hashes, folders' `Children`/`SyncedChildren`). Replaced on each sync;
  `Settings["backup:parent"]` keeps the last `BackupSnapshot` (parent info, targets, mappings used, detected mappings).
- **ArchiveListings** (key = the archive's `EntryId`; `Size`/`MTime` it was listed at, `Format`, `Streamed`, `Error`, `Files`, `FileBytes`),
  **ArchiveMembers** (`ArchiveId`, normalized `Path`, `Name`, `Size`, `Crc` = CRC32 or null) and **ArchiveExtractions** (`ArchiveId`, `State`
  complete/partial, `RootId` + `BaseRelPath` + `Strip` = archive path `p` is expected at `BaseRelPath/p` with `Strip` removed from its start,
  `FolderId`/`FolderRelPath` = deepest folder holding the matched files, counts `Matched`/`Missing`/`Smaller`/`Differs`, bytes, `Fingerprint`
  of the matched files' ids/sizes/mtimes, CRC check results `VerifiedAt`/`VerifiedOk`/`VerifiedBad`/`Unverified`/`BadPaths`). No foreign keys:
  stale rows are dropped by the archives phase, and readers join `Entries`.
- **Settings** — key/value JSON: `app` (`AppSettings`), `integration:<id>` (`IntegrationSettings`, secrets encrypted with data protection).

Times are unix seconds in Entries; `DateTimeOffset` columns are stored as binary longs (`DateTimeOffsetToBinaryConverter`, set globally) so
SQLite can sort them. Migrations are created only with `dotnet ef migrations add <Name> --project src/ANalyze.Infrastructure --startup-project src/ANalyze.Infrastructure --output-dir Data/Migrations`.

## Scan pipeline (`ScanService.RunAsync`)

One scan at a time (`ScanQueue` → `ScanWorker`, guarded by `JobLock`). `JobLock` serializes every background writer: scans, plan
apply, AI runs, rule apply and integration syncs (a sync waiting for it reports `queued`). Settings saves (`SettingsStore`,
`IntegrationHost.SaveAsync`, ignore / enter-filesystem endpoints) apply in memory at once and persist through `JobLock.WriteAsync`:
written immediately when the lock is free, else kept per key (latest wins) and written when the running job releases it; the
endpoints return `savedLater` (the holder) so the UI can say so.

1. **Walk** (`Walker`): depth-first; per directory it loads existing children by `ParentId`, `lstat`s every name (`Native.LStat` → `statx`),
   inserts new rows, updates changed files (size/mtime/inode/type) and resets their detection and hashes, deletes vanished subtrees,
   and writes directory aggregates on the way back up. New files get `Kind`/`Mime` from the extension at insert (detection refines them).
   Directories with no known size (first scan) get running totals at every commit, from the walker's stack of open folders; rescans keep the
   previous complete size until the folder is done. Symlinks are recorded, never followed; other filesystems below a root are entered
   unless `CrossFilesystems` is off (default on); when off they are listed, flagged `OtherFilesystem` and logged (entries below such a
   folder left from an earlier scan are deleted). Unreadable directories keep their previous contents and are logged. Writes are batched:
   a commit every 5000 writes or 2 seconds, whichever comes first (the transaction also spans the disk I/O, so this bounds how long
   other writers wait and how stale readers' view is — WAL readers never block). A cancelled or failed scan ends with `RollupDirectories` (every folder of the
   scanned roots recomputed from its children, deepest first).
   Subtree scans (`ScanRequest.SubPath`) start at the deepest existing directory, create missing directory entries (`EnsureDirChain`) and
   then recompute the ancestors (`RecomputeAncestors`).
2. **Detect**: `FileTypes.FromExtension`, or magic bytes of the first 4 KB when the extension is unknown. Until a file is detected, the API reports its type
   from the extension (`EntryDto.From`).
3. **Categorize** (`Categorizer`): rules for every file in scope; precedence manual > rule > AI; tags always from rules.
3b. **Archives** (`ArchiveIndexer`, with `FindExtractedArchives`): drops listings of archives that are gone or changed (with their members and
   extractions), then lists archives in scope without a current listing — `zip`/`zipx`/`rar`/`7z` by extension from their headers (CRC32 per
   file included), and with `ListStreamedArchives` also `tar`/`gz`/`tgz`/`bz2`/`xz`/`zst`… up to `StreamedArchiveMaxGb` by decompressing them
   once (`ReaderFactory`; CRC32 and size computed per file). Later volumes of multi-part rars, folders, links, `__MACOSX/`, `.DS_Store`,
   `Thumbs.db` and `..` paths are skipped; more than 100,000 files is an error.
3c. **Extractions** (`ExtractionMatcher`, all roots; after full scans, or when the walk or the archives phase changed something): per archive up to
   5 anchors (largest files, unique sizes first) are looked up by size + name (the covering size index); each hit gives a `(root, base, strip)`
   candidate (the longest tail of the archive path the found path ends with). Up to 8 candidates by votes are compared member by member through
   the unique `(RootId, RelPath)` index (NFC and NFD name forms both tried); extra files there do not matter. Kept: complete, or ≥ 2 matched
   files, or ≥ 1 MB matched. Rows keep their id and CRC check results for the same place while the fingerprint is unchanged.
4. **Hash all** (`ContentHasher.HashMissingAsync`, only with `AppSettings.HashAllFiles`): full + quick hash for every non-empty file without one
   (hard links read once). Needed for backup verification. Every file may therefore have a `FullHash`: code that looks for duplicates must require
   another file with the same hash and a different identity (`SearchFilter.Duplicates`, `PlanBuilder.CreateDedupeAsync`), never just `FullHash != null`.
5. **Duplicates** (`DuplicateFinder`, all roots): sizes with ≥ 2 distinct identities (`dev:inode`, or the row id where inodes are unavailable)
   → quick hash (XxHash3 of 3×64 KB + size) → full hash (XxHash128) only on quick-hash collisions; files ≤ 192 KB get the full hash directly.
   Hard-link siblings share one hash computation. The candidate sizes go into a temp table (`DupSizes`) once; the size search reads only the
   covering index `(IsDir, Size, IsSymlink, Inode, Dev)`, and the "not hashed yet" counts here and in step 4 read only the partial index
   `IX_Entries_Unhashed` (`Size` where `IsDir = 0 AND IsSymlink = 0 AND FullHash IS NULL`, named with `INDEXED BY`). Never add a scan-wide
   query that reads `Entries` rows in size order: on a disk-backed database that is one random read per file (tens of minutes for millions).
   Hashing and duplicate phases are reported before their first query.
6. **Verify** (full scans with `VerifyAfterDays > 0`, `ContentHasher.VerifyAsync`): re-reads files with `HashedAt` older than that, oldest first, up to
   `VerifyMaxGb`; a different hash with unchanged size and mtime sets `CorruptAt` (insight "Possibly corrupted files", search `corrupt=true`;
   `DELETE /files/{id}/corrupt` accepts the new content). Files whose size/mtime differ from the index are skipped (the next walk resets them).
7. **History** snapshot (full scans), `PRAGMA optimize`.

Hash batches (`ContentHasher`, `DuplicateFinder`) are also stored once they hold 2 GB / 8 GB of files, so big files' hashes are saved as
they come. Phase names (`walking`, `detecting`, `categorizing`, `archives`, `extractions`, `hashing`, `duplicates`, `verifying`, `finishing`, and the
light jobs' `checking` and `pruning`) are explained in the UI by
`ClientApp/src/lib/phases.ts` — keep it in sync when adding a phase.

Two light jobs share the scan queue (one writer at a time, same progress and cancel; no `ScanRun` row, no `Completed` event):
`ScanRequest.VerifyExtractions` (`ExtractionVerifier`: reads each matched file and compares its CRC32 with the archive's — the archive itself
is never read; files whose size/mtime differ from the index count as unverified) and `ScanRequest.PruneIgnored` (`IgnorePruner`: walks the
`(RootId, RelPath)` index and deletes the outermost entries the ignore list covers, with `EntryMaintenance.DeleteEntry`). `POST /ignore`
and settings saves that add ignore patterns queue the prune, so ignored items leave every result list at once.

Priority (`AppSettings.Priority`, `PriorityApplier`): `background` sets every thread of the process to nice 10 and the idle I/O class
(`Native.SetBackgroundPriority`, Linux via `/proc/self/task`; new threads inherit it; elsewhere the process priority class), and
`AppSettings.WorkerThreads()` returns 1 instead of `HashThreads`; `full` restores normal priority (needs root or CAP_SYS_NICE on Linux,
otherwise a restart).

Progress is reported to `JobBoard` (kind `scan`), throttled, and pushed through SignalR (`progress` events). The UI refetches what is on
screen when a job changes phase and every 10 s while a scan runs; server caches (`Caches`) are dropped on phase changes too.

## Activity log

`ActivityLog` (Infrastructure) is an in-memory ring buffer of the last 20,000 lines (`LogLine`: id, time, level, source, message, detail) —
not stored in the database; the Serilog files in `<DataDir>/logs` keep the history. `ActivityLogSink` (Web, a Serilog sink) copies every
`ANalyze.*` log line plus warnings/errors from anything into it; the source is a `Source` log property (a logging scope, e.g.
`IntegrationHost` sets `integration:<id>` around a sync) or else the namespace (`scan`, `integrations`, `actions`, `ai`, `preview`, `system`).
`LogPusher` sends new lines to the UI as SignalR `logs` events (batched twice a second); `GET /api/v1/logs?before=&level=&source=&q=&limit=`
pages back, newest first; `GET /logs/sources`. Page: `pages/Logs.tsx`.

Verbosity: phases, per-top-level-folder results, a progress line about every 30 s, skipped/unreadable folders, and per-file problems up to
50 per phase (`ScanContext.Problem`; the rest are counted) — never one line per file. Scanner classes log through `ScanContext.Log`. On completion `ScanService.Completed`
fires: AI auto-classification and integration auto-sync subscribe to it.

## Actions

`PlanBuilder` creates drafts: manual selections (folders cover their contents — nested picks are dropped), filters (`/plans/from-filter`, same
`SearchFilter` as search and insights), templates (`PathTemplate` tokens `{name} {stem} {ext} {category} {kind} {relDir} {parent} {mtime:fmt} {size}`;
values are sanitized and `..` is rejected), and dedupe (`DedupeSelector`). Items on read-only roots are excluded at creation and re-checked at apply.

`PlanExecutor` (via `PlanQueue`/`PlanWorker`) verifies each item (exists, same type, size and mtime; duplicates are re-hashed together with the
kept copy), then:
- **Trash**: rename into `<root>/.analyze-trash/<planId>/<relPath>`; the scanner ignores that folder at depth 1.
- **Delete**: remove file or folder.
- **Move**: create parents (owner copied from the parent folder), `File.Move` (copy+delete across filesystems, owner restored), directory copy fallback.
- **Hardlink**: `link(keep, tmp)` then atomic rename over the duplicate (same device only).

The index is updated directly (`EntryMaintenance.DeleteEntry`, `UpdateIdentity`) and folders that received files are rescanned as subtrees.
Undo moves items back, prunes folders left empty, and drops the moved copies from the index.

## Safety rules

- File content is read only through `RootRegistry.ResolveForRead` (realpath must stay inside the root) and only by entry id — never from a path in a request.
- `/files/{id}/content` serves HTML/SVG/JS as `text/plain` unless downloading.
- The container runs as root; `Native.SetOwner` gives created folders/copies the owner of their source/parent. Keep that behavior for any new write path.
- AI receives metadata only unless `AiPrivacy.TextSnippet` is chosen.

## Integrations (plugins)

`IIntegration` = id, name, description, icon, `Fields` (form schema: text/url/password/bool/number/mappings/select), `TestAsync`, `SyncAsync`,
`MapEndpoints` (mounted at `/api/v1/integrations/{id}`). `IntegrationHost` stores settings (`integration:<id>`), encrypts `password` fields,
seeds settings from `ANalyze:Integrations:<id>:*` configuration until the first save, runs syncs (job kind `integration:<id>`) and re-syncs enabled
integrations after each scan. Plugins write labels with `Annotations.Replace(conn, id, items)`.

Front end: `ClientApp/src/integrations/registry.tsx` maps an id to a lazy page; the settings form is generic (`pages/Integrations.tsx`).

Labels are clickable: a badge links to `/integrations/{source}?entry={entryId}` (never to the external app; those links stay inside the pages).
Extracted archives (`ArchiveEndpoints`): `GET /archives/extractions?state=complete|unverified|verified|partial|bad&rootId=&q=&sort=` (page +
counts), `GET /archives/extractions/{id}?problems=` (every archive file as matched/missing/smaller/differs/corrupt, via
`ExtractionMatcher.Compare`), `GET /archives/{entryId}/extractions`, `POST /archives/extractions/verify {ids | state, rootId}` (queues the
check) and `POST /archives/extractions/plan {ids, remove: archive|extracted, op}` (archives only for complete extractions; "extracted" = only
the matched files, never the rest of the folder). `SearchFilter.Extracted` (any/complete/verified/partial) needs `ArchiveExtractions` passed
to `Apply`; insights `extracted-verified`, `extracted`, `extracted-partial`. UI: Duplicates → Extracted archives (`components/ExtractedArchives.tsx`).

Browse also calls `GET /entries/{id}/disk` (the folder listed on disk now via `ResolveForRead`: names not in the index with lstat data and whether
they are ignored, plus the count of indexed children gone from disk; `?sub=a/b` lists a folder further down that is not in the index,
opened in place in Browse as `?disk=a/b`) and `POST /ignore {rootId, relPath, ignore}` / `POST /enter-filesystem`
(adds the full path to `IgnorePatterns` / `EnterFilesystems`; the latter also queues a subtree scan). A page reads the entry with `hooks/useLocate.ts`, which calls the plugin's `GET /integrations/{id}/locate?entryId=` and opens what it returns:
`ItemIntegration` and Plex return the title the entry belongs to (`key`, Plex also `type`) when the entry or folder holds exactly one, and the page
opens it through `groups?key=` / `top?key=`; the backup page gets `{rootId, path}` and opens its folder view there. `Db.EntryScope` + `Db.InScope`
give the "this entry or anything inside it" condition. A plugin without `locate` just opens its page.

To add one: new project `src/ANalyze.Integrations.<Name>` referencing `ANalyze.Integrations`, implement `IIntegration`, register with
`services.AddIntegration<T>()` in `Program.cs`, add the project to the Dockerfile restore list and the solution, optionally add a UI page to the registry.

Syncs record for every item where ANalyze looked (`PathMatcher.LocalPath`) and log a summary of the items not found with the reason
(`PathMatcher.WhyNotFound`: outside every root, or inside an `OtherFilesystem` folder). The item endpoints return `checkedPath` and the found
entry's full `localPath`.

Host paths (display only): `AppSettings.HostPaths` (ANalyze path → host path) followed by every `IHostPathSource` (the Docker plugin's
`DockerHostPaths`: ANalyze's own bind mounts), served at `GET /api/v1/host-paths`; the UI maps paths with `useToHostPath` / `<PathText>`.
The desktop app does not use them (it sees host paths).

`IIntegration.DockerHints` (image/name words) and `UrlField` let the Docker plugin find an app's container. `IntegrationPaths.ForAsync` returns the
configured `mappings` followed by automatic ones from every `IPathMappingSource` (`IntegrationPaths.Merge`: an automatic mapping at or below a
configured prefix is dropped; `MapPath` takes the longest prefix, the later mapping for equal prefixes); failures there are logged and ignored.
Then `PathAliases.Resolve` uses ANalyze's mounts (`IntegrationPaths.MountsAsync`: `AppSettings.HostPaths` + `IHostPathSource`, including mounts
at the same path) to move targets outside every root to the same host folder's path inside a root, and adds fallback mappings for ANalyze paths
or host paths outside every root that lead into one (never under an existing mapping's prefix). `GET /integrations` returns them as `auto` so the settings form shows them. Enabling an integration that has a UI page also shows
it in the sidebar (there is no separate visibility setting).

### ItemIntegration (shared base)

Plugins that list files from another app derive from `Items/ItemIntegration` and implement `FetchAsync` → `Snapshot(Items, Folders)`. The base
matches each `ExternalItem` with `PathMatcher` (files by path+size, folders by path or name+total size, `IsDir = null` tries both), stores
`IntegrationItems`, writes annotations (item label/badge/url, optional parent/grandparent folder labels, `root` annotations for `ExternalFolder.IsRoot`)
and maps the shared endpoints under `/integrations/{id}`: `summary`, `groups` (titles by size; filters `watchedOld`, `neverWatched`, `old`, `missing`;
`kind`, `state`, `q`, `sort`), `group?key=`, `unmatched`, `untracked?mode=files|top` (entries in root folders no item covers; `top` lists direct children
that are neither items nor containers of items/roots), `copies` (files outside the items with the same `FullHash`, different inode) and
`suggest-mappings`. `MapExtra` adds plugin-specific routes (`*arr`: `POST unmonitor {keys}` via the app's editor endpoint).

Badges `seeding` / `downloading` mark files a download client is using: `PlanBuilder` excludes trash/move/delete items that are, contain or sit inside
such an entry (hard links are allowed — the content stays).

- **Sonarr/Radarr/Lidarr** — `X-Api-Key`, `/api/v3` (`/api/v1` Lidarr). Items: episode/movie/track files (quality, monitored); folders: root folders
  (roots) and series/movie/artist folders.
- **qBittorrent** (cookie login, Referer header), **Transmission** (RPC with the 409 session-id handshake, basic auth), **SABnzbd** (`api?mode=`):
  items are torrents/downloads (`IsDir = null`), states seeding/downloading/completed/error/failed; roots are the default, incomplete and category folders.
- **Jellyfin/Emby** — `X-Emby-Token`; `/Users/{id}/Items` (Movie, Episode, Audio with MediaSources and the chosen user's UserData); library
  locations are roots. Emby uses the `/emby` prefix.
- **Immich** (`x-api-key`, `POST /api/search/metadata`, external library import paths as roots when the key is an admin's) and **PhotoPrism**
  (Bearer token, `/api/v1/photos?merged=true`, files relative to the configured originals folder).

### ANalyze parent (backups between instances)

A parent–child link: the **parent** is the ANalyze on the backup server, the **child** is the instance whose files are backed up there; the child
enables the `parent` integration (URL, the parent's `ANalyze__ApiToken`, mappings parent path → local path). The parent needs nothing but a token;
it always serves `GET /api/v1/backup/info` (version, hash algorithm, `HashAllFiles`, roots with file/hash counts) and
`GET /api/v1/backup/files?rootId=&after=&limit=` (files and folders of one root in `RelPath` order, keyset-paged on the unique index; short JSON
names `p s m h c d`). Both are behind the normal auth policy.

Rules (`BackupSync`, child side):
- a **file is synchronized** when the parent has a file at the mapped path;
- a **folder is synchronized** when at least 80% (`BackupStates.FolderThreshold`) of its direct files and sub-folders, by count, are synchronized,
  or when it is identical to the parent's folder (same entries, every file same size and hash; this is what makes an empty folder synchronized);
- inside a synchronized folder (the file's direct parent), a file is **corrupt** when the parent's copy differs in size or — when both hashes are
  known — hash, and **missing** when the parent does not have it; elsewhere a file the parent lacks is **unsynced**.

Implementation: the parent's tree goes into a temp table at the local paths it maps to (`ParentStage`, indexed by path and parent path). The
backed-up folders are the parent roots that map to a local folder, else each mapping that starts inside a root (the backup may live in a sub-folder
on the server). If fewer than a fifth of a root's first 200 files are found at the mapped path, a mapping is detected with `PathMatcher.Detect`
(name + size). Local entries in those folders (outermost only, no symlinks) are loaded with their parent counterpart and processed deepest first,
so each folder sees its children's results; then `BackupFiles` is replaced in one transaction. `BackupAnnotations` labels every folder (Synced /
Not synced · N%, `backup` kind), the backed-up folders (`backup-root`) and corrupt/missing files (at most 50k); other files are not labelled one by
one. `GET /entries/{id}` returns the entry's `backup` row. Plugin routes: `summary`, `folders?rootId&path` (the folder, its sub-folders, its direct
files by state), `files?state&rootId&path&direct&q&sort` (with the parent path, mapped back), `suggest-mappings`. UI: `integrations/parent/BackupPage.tsx`.

### Docker

`DockerClient` talks to `unix:///var/run/docker.sock` (or `tcp://…`, e.g. a socket proxy) and only reads `/version` and `/containers/json`.
`DockerState` caches a snapshot for a minute and finds ANalyze's own container from the hostname (= short container id) or the configured name;
outside a container its mounts are `/ → /`. For another app's container (override `id=container`, else the URL host matching a container name,
compose service or IP, else a unique published port, else `DockerHints` in the image/name), `MountMath.Mappings` pairs mounts that overlap on the host:
other `/movies` ← host `/mnt/media/movies`, ours `/library` ← host `/mnt/media` ⇒ `/movies → /library/movies` (and the reverse case when our mount
is inside theirs). A folder we see through several mounts gives one candidate each, our most specific mount first; `MappingsFor` keeps the first
per prefix that reaches a scanned root. Sync labels local folders that other containers mount (`mount` annotations).
`GET /integrations/docker/containers` feeds the Docker page.

### Plex

`PlexClient` reads sections and media parts (movies `type=1`, episodes `type=4`, tracks `type=10`, paged). `PathMatcher` resolves Plex paths:
configured and Docker mappings → mappings detected by locating sample files by name+size and splitting at the common path suffix → per-file name+size
(+ parent folder name). Sync replaces `PlexItems` and the `plex` annotations: files (movie/episode/track + watched state + Plex Web link),
their parent/grandparent folders (movie, season/show, album/artist) and the library folders.

## Front end

React 19 + Vite + Tailwind 4 + TanStack Query, same shell and tokens as nopds (sidebar, cards, pills) with ANalyze's teal/cyan palette and amber
for reclaimable space (`src/index.css`). `useJobs` holds the single SignalR connection and invalidates queries when a job finishes; `useScope`
holds the selected root. Search filters live in the URL so insights, sidebar entries and bookmarks link to result sets. Charts: labeled bar lists
(identity never by color alone) and an ECharts treemap. Category colors: the first eight built-ins use the validated categorical palette.

Release notes: `RELEASE_NOTES.md` is embedded into `ANalyze.Web` (`EmbeddedResource`, also copied in `docker/Dockerfile`), parsed by
`Infrastructure/ReleaseNotes.cs` (`## vX.Y.Z — date` headings, `- kind: text` bullets) and served at `GET /api/v1/release-notes`. The
`/release-notes` page renders it with credits (CHDS, Claude); the sidebar version pill links there and shows a dot until this browser has
opened the notes for the running version (`hooks/useWhatsNew.ts`, localStorage). Branding is "ANalyze by CHDS" (page title, sidebar, login).

## Desktop app (`desktop/`)

Electron shell around the unchanged server. `scripts/publish-server.mjs` builds the SPA into `wwwroot` (before publishing, like the Dockerfile —
`MapStaticAssets` only serves files known when the static-asset manifest is computed) and publishes `ANalyze.Web` self-contained into
`desktop/server/<arch>`; electron-builder copies that to `resources/server` (`scripts/dist.mjs` does both for one OS and several archs).

`main.js` starts the server on `127.0.0.1` with a port kept in `<userData>/desktop.json` (a stable origin keeps the UI's localStorage), sets
`ANalyze__DataDir=<userData>/data`, `ANalyze__Roots__N` from `desktop.json`, and a random `ANalyze__Password` + `ANalyze__ApiToken` per launch;
`session.webRequest` adds `Authorization: Bearer <token>` to the window's requests to that origin (including the SignalR WebSocket), so nothing else
on the machine can use the API. `preload.js` exposes `window.analyzeDesktop` (`addRoot`, `removeRoot`, `openDataFolder`; front end:
`src/lib/desktop.ts`): the Settings page shows Add/Remove for roots and hides Sign out. Changing roots rewrites `desktop.json` and restarts the
server (asks first when a job is running). Links to other origins open in the default browser.

Platform notes: paths stay `/`-separated in `Entries.RelPath` on every OS; `RootRegistry.IsUnder` uses the platform separator (case-insensitive on
Windows). Hard links remain Linux-only; elsewhere `Native` falls back to managed APIs (no inode/device, so duplicate identity uses row ids).

CI (`.github/workflows/docker.yml`, job `desktop`): Linux x64+arm64 (AppImage, tar.gz), macOS x64+arm64 (dmg, zip, ad-hoc signed), Windows x64
(NSIS installer, zip) on every non-PR build, uploaded as artifacts named `ANalyze-*` (the release job downloads only those: the run also
holds Docker's build record, which is not a zip). Builds of `main` publish them as the GitHub release `v<VERSION>` (job `release`):
created with its tag when a new version first reaches main (notes = that version's `RELEASE_NOTES.md` section), files replaced by later builds of the same version.
Builds of `main` also set the Docker Hub description (job `docker`): `.github/scripts/dockerhub-readme.mjs` turns `README.md` into it (relative
links and images → GitHub URLs, `#section` links → the README on GitHub, GitHub alerts → bold labels, a repository link at
`<!-- dockerhub:github-link -->`, cut at 25,000 bytes) and `peter-evans/dockerhub-description` uploads it; `DOCKERHUB_TOKEN` needs the
"Read, Write, Delete" scope. Keep README links relative (or absolute) and its images in the repository so both sites show them.
