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
| `ANalyze.Infrastructure` | `AnalyzeDbContext` + migrations, `SqliteSetup` (connection string + PRAGMAs), `SettingsStore`, `SecretProtector`, `RootRegistry` (roots, writability probe, safe paths), `Native` (statx, link, realpath, lchown), `JobBoard`/`JobLock`. |
| `ANalyze.Scanner` | `Walker` (incremental tree mirror), `Detector` (type by extension/magic), `Categorizer` (rules), `DuplicateFinder` + `Hashing`, `ScanService` (pipeline), `ScanQueue`/`ScanWorker`/`ScanScheduler`, `EntryMaintenance`. |
| `ANalyze.Preview` | Thumbnails (SkiaSharp, ffmpeg for video), paged text/hex reads, archive listing (SharpCompress), metadata (MetadataExtractor, TagLib). |
| `ANalyze.Actions` | `PlanBuilder` (manual/template/dedupe plans), `PathTemplate`, `DedupeSelector`, `PlanExecutor` + `PlanQueue`, `TrashService` + janitor. |
| `ANalyze.Ai` | `AiService` (classify, suggest rules), `Prompts` (every prompt and JSON schema), `IAiClientFactory` (OpenAI-compatible via Microsoft.Extensions.AI). |
| `ANalyze.Integrations` | Plugin contract `IIntegration`, `IntegrationHost` (settings with encrypted secrets, sync, auto-sync after scans), `Annotations` writer. |
| `ANalyze.Integrations.Plex` | Plex plugin: `PlexClient`, `PathMatcher`, `PlexIntegration`, `PlexEndpoints`. |
| `ANalyze.Web` | `Program.cs`, `Endpoints/*`, `JobHub`, auth, SPA in `ClientApp/`. |

## Data model

- **Entries** — one row per file or directory: `RootId`, `ParentId`, `RelPath` (relative to the root, `/`-separated, `""` = root dir), stat data
  (size, allocated size, mtime/atime/ctime, inode, device, link count), detection (`Ext`, `Mime`, `Kind`, `Detected`), category (`CategoryId`,
  `CategorySource` none/rule/ai/manual), rule tags (`|a|b|`), hashes (`QuickHash`, `FullHash`). Directories carry subtree aggregates
  (`TotalSize`, `TotalAlloc`, `FileCount`, `DirCount`); files have `TotalSize = Size`, `FileCount = 1`.
  - Subtree queries use the unique index `(RootId, RelPath)` with the range `RelPath >= 'p/' AND RelPath < 'p0'` (`'0'` follows `'/'`).
    Never use `LIKE 'p/%'` for this — it cannot use the index.
- **DirSizeHistory** — `(ScanId, EntryId)` sizes of directories up to `HistoryDepth` after each full scan → growth view and pinned deltas.
- **Categories**, **Rules** (`Condition` and `Tags` stored as JSON), **ActionTemplates**.
- **ActionPlans** / **ActionItems** — every change to disk. Items remember what they expect (size, mtime, hash) and where things went (`UndoPath`).
- **AiResults** (cache by content hash or path+size+mtime; unapplied rows = review queue), **AiRuleSuggestions**.
- **Annotations** — integration labels per entry (`Source`, `Kind`, `Label`, `State`, `Url`). Integrations replace their own rows on each sync.
- **PlexItems** — Plex media parts with the matched `EntryId`.
- **Settings** — key/value JSON: `app` (`AppSettings`), `integration:<id>` (`IntegrationSettings`, secrets encrypted with data protection).

Times are unix seconds in Entries; `DateTimeOffset` columns are stored as binary longs (`DateTimeOffsetToBinaryConverter`, set globally) so
SQLite can sort them. Migrations are created only with `dotnet ef migrations add <Name> --project src/ANalyze.Infrastructure --startup-project src/ANalyze.Infrastructure --output-dir Data/Migrations`.

## Scan pipeline (`ScanService.RunAsync`)

One scan at a time (`ScanQueue` → `ScanWorker`, guarded by `JobLock` with plan apply and AI runs).

1. **Walk** (`Walker`): depth-first; per directory it loads existing children by `ParentId`, `lstat`s every name (`Native.LStat` → `statx`),
   inserts new rows, updates changed files (size/mtime/inode/type) and resets their detection and hashes, deletes vanished subtrees,
   and writes directory aggregates on the way back up. Symlinks are recorded, never followed; other filesystems below a root are listed
   but not entered unless enabled. Unreadable directories keep their previous contents. Writes are batched (5000 per transaction).
   Subtree scans (`ScanRequest.SubPath`) start at the deepest existing directory, create missing directory entries (`EnsureDirChain`) and
   then recompute the ancestors (`RecomputeAncestors`).
2. **Detect**: `FileTypes.FromExtension`, or magic bytes of the first 4 KB when the extension is unknown.
3. **Categorize** (`Categorizer`): rules for every file in scope; precedence manual > rule > AI; tags always from rules.
4. **Duplicates** (`DuplicateFinder`, all roots): sizes with ≥ 2 distinct identities (`dev:inode`, or the row id where inodes are unavailable)
   → quick hash (XxHash3 of 3×64 KB + size) → full hash (XxHash128) only on quick-hash collisions; files ≤ 192 KB get the full hash directly.
   Hard-link siblings share one hash computation.
5. **History** snapshot (full scans), `PRAGMA optimize`.

Progress is reported to `JobBoard` (kind `scan`), throttled, and pushed through SignalR (`progress` events). On completion `ScanService.Completed`
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

To add one: new project `src/ANalyze.Integrations.<Name>` referencing `ANalyze.Integrations`, implement `IIntegration`, register with
`services.AddIntegration<T>()` in `Program.cs`, add the project to the Dockerfile restore list and the solution, optionally add a UI page to the registry.

### Plex

`PlexClient` reads sections and media parts (movies `type=1`, episodes `type=4`, tracks `type=10`, paged). `PathMatcher` resolves Plex paths:
configured mappings → mappings detected by locating sample files by name+size and splitting at the common path suffix → per-file name+size
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
