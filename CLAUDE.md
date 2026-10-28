# CLAUDE.md

Guidance for Claude Code in this repository. The design is described in `AGENTS.md` (authoritative); user docs are in `README.md`.

## Commands

```bash
dotnet build                                         # all projects; warnings are errors
dotnet test tests/ANalyze.Tests                      # unit + integration tests
cd src/ANalyze.Web/ClientApp && npm run typecheck && npm run lint && npm run build
dotnet ef migrations add <Name> --project src/ANalyze.Infrastructure --startup-project src/ANalyze.Infrastructure --output-dir Data/Migrations
docker compose -f docker-compose.dev.yml up --build  # full app on :8080 with ./library mounted
```

## Mandatory workflow rules

1. **After every code change** — `dotnet build` and `npm run typecheck` (in ClientApp); fix all errors, and warnings when safe.
2. **After every committed change** — version check-bump flow:
   a. If the current `VERSION` was already pushed to origin (`git log origin/main..HEAD -- VERSION` is empty and the tag/commit exists on origin),
      bump the patch number and add a new `## v{version} — {date}` heading to `RELEASE_NOTES.md`; otherwise reuse the current version.
   b. Make sure the heading for the current version carries today's date.
   c. Add a `- feat:/fix:/refactor:` bullet for the change under that heading.
3. **After every architectural or schema change** — update `AGENTS.md` and `README.md`.
4. **Never hand-write migrations** — always `dotnet ef migrations add`.
5. **UI style** — keep the nopds/abook look (sidebar shell, cards, pills, token names in `index.css`) with ANalyze's own palette.

## Gotchas

- Subtree ranges on `Entries.RelPath` use `>= 'p/' AND < 'p0'` (index-friendly); keep it that way.
- Any new code that creates files or folders on a root must give them the parent's owner (`Native.CreateDirectoryLikeParent`, `Native.SetOwner`); the container runs as root.
- Read file content only via `RootRegistry.ResolveForRead` and entry ids.
- Enum JSON is camelCase strings (`JsonStringEnumConverter` registered for HTTP and SignalR). Query-string enums are parsed case-insensitively by hand where needed.
- Every prompt lives in `src/ANalyze.Ai/Prompts.cs`.
- New integrations: see "Integrations (plugins)" in `AGENTS.md` (also add the csproj to `docker/Dockerfile`'s restore list).
