import { useEffect } from 'react'
import { Link } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../../api/client'
import { BarList } from '../../components/charts'
import { EntryDrawer } from '../../components/EntryDrawer'
import { PlanDialog } from '../../components/PlanDialog'
import { Empty, ErrorBox, Loading, Modal, PageTitle, Section, Stat, Tabs } from '../../components/ui'
import { ago, bytes, count, date, pct } from '../../lib/format'
import { PathText } from '../../components/PathText'
import { useLocate } from '../../hooks/useLocate'
import { useEntryPreview, useUrlEnum, useUrlOverlay, useUrlParam, useUrlParams } from '../../hooks/useUrlState'

type TitleType = 'movie' | 'show' | 'artist'

interface Summary {
  sections: { key: string; title: string; type: string; items: number; files: number; size: number; watchedSize: number; missing: number }[]
  matchedBy: { by: string; files: number }[]
}

interface TitleRow {
  key: string
  title: string
  year?: number
  section: string
  files: number
  size: number
  watchedFiles: number
  lastViewedAt?: number
  addedAt?: number
  versions: number
  missingFiles: number
  folderId?: number
  url?: string
  resolutions?: string
}

interface TitleFile {
  id: number
  title: string
  parent?: string
  season?: number
  episode?: number
  viewCount: number
  lastViewedAt?: number
  plexPath: string
  checkedPath?: string
  localPath?: string
  size: number
  resolution?: string
  entryId?: number
  relPath?: string
  url?: string
  matchedBy?: string
}

const TYPES: { id: TitleType; label: string; unit: string }[] = [
  { id: 'movie', label: '🎬 Movies', unit: 'files' },
  { id: 'show', label: '📺 Shows', unit: 'episodes' },
  { id: 'artist', label: '🎵 Music', unit: 'tracks' },
]

const FILTERS = [
  { id: '', label: 'All' },
  { id: 'watchedOld', label: 'Watched long ago' },
  { id: 'neverWatched', label: 'Never watched' },
  { id: 'multiVersion', label: 'Several versions' },
  { id: 'missing', label: 'Files not found' },
]

const titleQuery = (type: TitleType | undefined, key: string | null) => ['plex-top', 'key', type, key]

/**
 * Plex view: which movies, shows and artists take the most space, and how much of it is watched. Tab, filters and the open title
 * (`?title=<key>`, with its file panel and cleanup dialog) are in the URL, so Back steps through them.
 */
export default function PlexPage() {
  const qc = useQueryClient()
  const [, setParams] = useUrlParams()
  const [type, setType] = useUrlEnum<TitleType>('type', TYPES.map((t) => t.id), 'movie')
  const [filter, setFilter] = useUrlParam('filter')
  const [section, setSection] = useUrlParam('section')
  const [sort, setSort] = useUrlParam('sort', 'size')
  const [q, setQ] = useUrlParam('q')
  const title = useUrlOverlay('title', ['titleFile', 'titleFileTab', 'titleFilePlan', 'titlePlan'])
  const [tab, setTab] = useUrlEnum('tab', ['titles', 'notInPlex', 'unmatched'] as const, 'titles')
  const summary = useQuery({ queryKey: ['plex-summary'], queryFn: () => api<Summary>('/integrations/plex/summary') })
  const top = useQuery({
    queryKey: ['plex-top', type, filter, section, sort, q],
    queryFn: () => api<{ total: number; totalSize: number; items: TitleRow[] }>('/integrations/plex/top', { query: { type, filter, section, sort, q, limit: 300 } }),
    enabled: tab === 'titles',
  })
  // Opened from a label in the browser: show that movie, show or artist (the label's `?entry=` becomes `?type=…&title=…`).
  const located = useLocate<{ type?: TitleType; key?: string }>('plex')
  const loc = located.target?.key && located.target.type ? located.target : undefined
  useEffect(() => {
    if (loc) setParams({ entry: null, tab: null, section: null, type: loc.type === 'movie' ? null : loc.type, title: loc.key }, { replace: true })
  }, [loc, setParams])
  const openRow = useQuery({
    queryKey: titleQuery(type, title.value),
    queryFn: () => api<{ items: TitleRow[] }>('/integrations/plex/top', { query: { type, key: title.value } }),
    enabled: !!title.value,
  })
  const openTitle = title.value ? openRow.data?.items[0] : undefined
  const open = (t: TitleRow) => {
    qc.setQueryData(titleQuery(type, t.key), { items: [t] })
    title.open(t.key)
  }
  const s = summary.data
  const total = s?.sections.reduce((a, x) => a + x.size, 0) ?? 0
  const watched = s?.sections.reduce((a, x) => a + x.watchedSize, 0) ?? 0
  const matched = s?.matchedBy.filter((m) => m.by !== 'none').reduce((a, m) => a + m.files, 0) ?? 0
  const unmatched = s?.matchedBy.find((m) => m.by === 'none')?.files ?? 0

  if (summary.isLoading) return <Loading />
  if (summary.error) return <ErrorBox error={summary.error} />
  if (!s?.sections.length)
    return (
      <>
        <PageTitle>Plex</PageTitle>
        <Empty>
          No Plex data yet. Configure and sync Plex on the <Link to="/integrations" className="link">Integrations</Link> page.
        </Empty>
      </>
    )

  return (
    <>
      <PageTitle subtitle="Sizes come from the scanned files when matched, otherwise from Plex.">Plex</PageTitle>
      <div className="mb-4 grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="In Plex libraries" value={bytes(total)} />
        <Stat label="Watched at least once" value={bytes(watched)} hint={pct(watched, total)} />
        <Stat label="Matched to files" value={count(matched)} hint={unmatched ? `${count(unmatched)} not found on disk` : 'all found'} />
        <Stat label="Libraries" value={s.sections.length} />
      </div>
      <Section title="Libraries" className="mb-4">
        <BarList
          total={total}
          items={s.sections.map((x) => ({
            key: x.key,
            label: (
              <>
                {x.title} <span className="text-xs muted">· {count(x.items)} titles · {pct(x.watchedSize, x.size)} watched</span>
              </>
            ),
            value: x.size,
            onClick: () => setParams({ tab: null, section: x.key, type: x.type === 'show' || x.type === 'artist' ? x.type : null }),
          }))}
        />
      </Section>

      <Tabs
        tabs={[
          { id: 'titles', label: 'Titles by size' },
          { id: 'notInPlex', label: 'Media Plex does not know' },
          { id: 'unmatched', label: `Not found on disk (${count(unmatched)})` },
        ]}
        value={tab}
        onChange={(t) => setTab(t)}
      />

      {tab === 'titles' && (
        <>
          <div className="mb-3 flex flex-wrap items-center gap-2">
            <div className="flex rounded-md border border-line p-0.5">
              {TYPES.map((t) => (
                <button
                  key={t.id}
                  className={clsx('btn px-2.5 py-1', type === t.id ? 'bg-accent-faint text-accent' : 'text-muted')}
                  onClick={() => setType(t.id, { also: { section: null } })}
                >
                  {t.label}
                </button>
              ))}
            </div>
            <select className="input w-auto" value={filter} onChange={(e) => setFilter(e.target.value)}>
              {FILTERS.map((f) => (
                <option key={f.id} value={f.id}>
                  {f.label}
                </option>
              ))}
            </select>
            <select className="input w-auto" value={section} onChange={(e) => setSection(e.target.value)}>
              <option value="">All libraries</option>
              {s.sections.map((x) => (
                <option key={x.key} value={x.key}>
                  {x.title}
                </option>
              ))}
            </select>
            <select className="input w-auto" value={sort} onChange={(e) => setSort(e.target.value)}>
              <option value="size">Largest</option>
              <option value="files">Most files</option>
              <option value="lastViewed">Recently watched</option>
              <option value="added">Recently added</option>
              <option value="title">Title</option>
            </select>
            <input className="input max-w-xs" placeholder="Title…" value={q} onChange={(e) => setQ(e.target.value, { replace: true })} />
            {top.data && (
              <span className="ml-auto text-sm muted">
                {count(top.data.total)} titles · {bytes(top.data.totalSize)}
              </span>
            )}
          </div>
          {top.isLoading ? (
            <Loading />
          ) : top.error ? (
            <ErrorBox error={top.error} />
          ) : !top.data?.items.length ? (
            <Empty>No titles match.</Empty>
          ) : (
            <div className="card divide-y divide-line/60">
              {top.data.items.map((t) => {
                const max = top.data!.items[0].size
                const unit = TYPES.find((x) => x.id === type)!.unit
                return (
                  <button key={t.key} className="flex w-full items-center gap-3 px-3 py-2 text-left hover:bg-accent-faint" onClick={() => open(t)}>
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2 text-sm">
                        <span className="truncate font-semibold">{t.title}</span>
                        {t.year && <span className="muted">({t.year})</span>}
                        {t.versions > 1 && <span className="pill-amber">{t.versions} versions</span>}
                        {t.resolutions && <span className="pill-muted">{t.resolutions}</span>}
                        {t.missingFiles > 0 && <span className="rounded bg-danger/10 px-1.5 text-xs text-danger">{t.missingFiles} not found</span>}
                      </div>
                      <div className="text-xs muted">
                        {t.section} · {count(t.files)} {unit} ·{' '}
                        {type === 'artist' ? '' : t.watchedFiles === 0 ? 'never watched' : t.watchedFiles === t.files ? 'all watched' : `${t.watchedFiles} watched`}
                        {t.lastViewedAt ? ` · last ${ago(t.lastViewedAt)}` : ''}
                        {t.addedAt ? ` · added ${date(t.addedAt)}` : ''}
                      </div>
                      <div className="mt-1 h-1.5 max-w-md overflow-hidden rounded-full bg-surface-2">
                        <div className="h-full rounded-full bg-accent" style={{ width: `${Math.max(1, (t.size / max) * 100)}%` }} />
                      </div>
                    </div>
                    <span className="shrink-0 font-semibold tabular">{bytes(t.size)}</span>
                  </button>
                )
              })}
            </div>
          )}
        </>
      )}
      {tab === 'notInPlex' && <NotInPlex />}
      {tab === 'unmatched' && <Unmatched />}
      {openTitle && <TitleDialog type={type} row={openTitle} onClose={title.close} />}
    </>
  )
}

function TitleDialog({ type, row, onClose }: { type: TitleType; row: TitleRow; onClose: () => void }) {
  const preview = useEntryPreview('titleFile')
  const plan = useUrlOverlay('titlePlan')
  const q = useQuery({ queryKey: ['plex-title', type, row.key], queryFn: () => api<TitleFile[]>('/integrations/plex/title', { query: { type: type === 'movie' ? 'movie' : type, key: row.key } }) })
  const files = q.data ?? []
  const ids = files.flatMap((f) => (f.entryId ? [f.entryId] : []))
  return (
    <Modal open onClose={onClose} title={`${row.title}${row.year ? ` (${row.year})` : ''}`} wide>
      <div className="mb-3 flex flex-wrap items-center gap-2 text-sm">
        <span className="font-semibold">{bytes(row.size)}</span>
        <span className="muted">· {count(row.files)} files</span>
        <span className="flex-1" />
        {row.url && (
          <a className="btn-ghost" href={row.url} target="_blank" rel="noreferrer">
            Open in Plex ↗
          </a>
        )}
        {row.folderId && (
          <Link className="btn-secondary" to={`/browse/${row.folderId}`}>
            Open folder
          </Link>
        )}
        <button className="btn-primary" disabled={!ids.length} onClick={() => plan.open(1)}>
          Clean up…
        </button>
      </div>
      {q.isLoading ? (
        <Loading />
      ) : (
        <div className="divide-y divide-line/60 text-sm">
          {files.map((f) => (
            <div key={f.id} className="flex items-center gap-3 py-1.5">
              <div className="min-w-0 flex-1">
                <div className="truncate">
                  {f.season !== undefined && f.episode !== undefined ? `S${String(f.season).padStart(2, '0')}E${String(f.episode).padStart(2, '0')} · ` : f.parent ? `${f.parent} · ` : ''}
                  {f.title}
                  {f.resolution && <span className="ml-2 pill-muted">{f.resolution}</span>}
                </div>
                <PathText path={f.plexPath} label="Plex:" className="muted" />
                {f.localPath ? (
                  <PathText path={f.localPath} label="ANalyze:" className="muted" />
                ) : (
                  <PathText path={f.checkedPath ?? f.plexPath} label="Not found at" className="text-danger" />
                )}
              </div>
              <span className="text-xs muted">{f.viewCount > 0 ? `✓ ${f.lastViewedAt ? ago(f.lastViewedAt) : 'watched'}` : 'unwatched'}</span>
              <span className="w-20 text-right tabular">{bytes(f.size)}</span>
              {f.entryId && (
                <button className="btn-ghost px-2 py-0.5 text-xs" onClick={() => preview.open(f.entryId!)}>
                  Details
                </button>
              )}
            </div>
          ))}
        </div>
      )}
      <EntryDrawer preview={preview} list={ids} />
      <PlanDialog open={plan.value !== null} onClose={plan.close} entryIds={ids} count={ids.length} size={row.size} title={`Clean up: ${row.title}`} />
    </Modal>
  )
}

function NotInPlex() {
  const preview = useEntryPreview()
  const q = useQuery({
    queryKey: ['plex-not-in'],
    queryFn: () => api<{ total: number; totalSize: number; items: { id: number; relPath: string; size: number; mtime: number }[] }>('/integrations/plex/not-in-plex', { query: { limit: 300 } }),
  })
  if (q.isLoading) return <Loading />
  if (!q.data?.items.length) return <Empty>Every video and audio file inside the Plex library folders is known to Plex.</Empty>
  return (
    <>
      <p className="mb-2 text-sm muted">
        {count(q.data.total)} files · {bytes(q.data.totalSize)} inside Plex library folders that Plex does not list — samples, extras, unsupported or misnamed files, leftovers.
      </p>
      <div className="card divide-y divide-line/60">
        {q.data.items.map((f) => (
          <button key={f.id} className="flex w-full items-center gap-3 px-3 py-1.5 text-left text-sm hover:bg-accent-faint" onClick={() => preview.open(f.id)}>
            <span className="min-w-0 flex-1 truncate">{f.relPath}</span>
            <span className="text-xs muted">{date(f.mtime)}</span>
            <span className="w-20 text-right tabular">{bytes(f.size)}</span>
          </button>
        ))}
      </div>
      <EntryDrawer preview={preview} list={q.data.items.map((f) => f.id)} />
    </>
  )
}

function Unmatched() {
  const q = useQuery({ queryKey: ['plex-unmatched'], queryFn: () => api<{ section: string; title: string; show?: string; plexPath: string; size: number; checkedPath?: string }[]>('/integrations/plex/unmatched') })
  if (q.isLoading) return <Loading />
  if (!q.data?.length) return <Empty>Every Plex file was found on disk.</Empty>
  return (
    <>
      <p className="mb-2 text-sm muted">
        Plex lists these files but ANalyze cannot find them. Usually a path mapping is missing (Integrations → Plex → Suggest), the folder is not mounted here, or the files were deleted.
      </p>
      <div className="card divide-y divide-line/60">
        {q.data.map((f) => (
          <div key={f.plexPath} className="flex items-center gap-3 px-3 py-1.5 text-sm">
            <div className="min-w-0 flex-1">
              <div className="truncate">{f.show ? `${f.show} · ${f.title}` : f.title}</div>
              <PathText path={f.plexPath} label="Plex:" className="muted" />
              <PathText path={f.checkedPath ?? f.plexPath} label="Looked for at" className="muted" />
            </div>
            <span className="text-xs muted">{f.section}</span>
            <span className="w-20 text-right tabular">{bytes(f.size)}</span>
          </div>
        ))}
      </div>
    </>
  )
}
