import { useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../../api/client'
import { BarList } from '../../components/charts'
import { EntryDrawer } from '../../components/EntryDrawer'
import { PlanDialog } from '../../components/PlanDialog'
import { Empty, ErrorBox, Loading, Modal, PageTitle, Section, Stat, Tabs } from '../../components/ui'
import { ago, bytes, count, date, pct } from '../../lib/format'

/** Views a plugin page offers; every plugin built on ItemIntegration serves all of them. */
export type ItemsTab = 'titles' | 'untracked' | 'orphans' | 'copies' | 'unmatched'

export interface ItemsConfig {
  id: string
  name: string
  /** Title view: item kinds to switch between (empty: all kinds together). */
  kinds?: { id: string; label: string; unit: string }[]
  /** Group filters understood by /groups (watchedOld, neverWatched, old, missing). */
  filters?: { id: string; label: string }[]
  /** State values to filter by (monitored, seeding, completed…). */
  states?: { id: string; label: string }[]
  tabs: { id: ItemsTab; label: string; hint: ReactNode }[]
  /** Title word: "titles", "torrents", "albums"… */
  noun: string
  /** Shows "watched" counts (media servers). */
  watch?: boolean
  /** Offers "Unmonitor" for titles (Sonarr, Radarr, Lidarr). */
  unmonitor?: boolean
}

interface Summary {
  totals: { items: number; size: number; matched: number; groups: number }
  byKind: { kind: string; items: number; size: number }[]
  byState: { state: string; items: number; size: number }[]
  roots: { entryId: number; label: string; detail?: string; relPath: string; size: number }[]
}

interface GroupRow {
  key: string
  title: string
  year?: number
  files: number
  size: number
  playedFiles: number
  lastUsedAt?: number
  addedAt?: number
  missingFiles: number
  folderId?: number
  url?: string
  details?: string
  states?: string
  kind: string
}

interface GroupFile {
  id: number
  title: string
  subTitle?: string
  kind: string
  state?: string
  detail?: string
  playCount: number
  lastUsedAt?: number
  externalPath: string
  size: number
  entryId?: number
  relPath?: string
  url?: string
  isDir: boolean
}

interface EntryRow {
  id: number
  relPath: string
  size: number
  mtime: number
  isDir?: boolean
  itemTitle?: string
}

interface EntryPage {
  total: number
  totalSize: number
  items: EntryRow[]
}

const STATE_TONE: Record<string, string> = {
  seeding: 'chip',
  downloading: 'chip',
  completed: 'pill-amber',
  unmonitored: 'pill-amber',
  error: 'rounded bg-danger/10 px-1.5 text-xs text-danger',
  failed: 'rounded bg-danger/10 px-1.5 text-xs text-danger',
}

function StatePill({ state }: { state?: string }) {
  if (!state || state === 'monitored') return null
  return <span className={STATE_TONE[state] ?? 'pill-muted'}>{state}</span>
}

/** Page for an item-based integration (Sonarr, qBittorrent, Jellyfin, Immich…), configured per plugin. */
export function ItemsPage({ config }: { config: ItemsConfig }) {
  const base = `/integrations/${config.id}`
  const [tab, setTab] = useState<ItemsTab>(config.tabs[0].id)
  const summary = useQuery({ queryKey: [config.id, 'summary'], queryFn: () => api<Summary>(`${base}/summary`) })
  const s = summary.data

  if (summary.isLoading) return <Loading />
  if (summary.error) return <ErrorBox error={summary.error} />
  if (!s?.totals.items && !s?.roots.length)
    return (
      <>
        <PageTitle>{config.name}</PageTitle>
        <Empty>
          No {config.name} data yet. Configure and sync it on the <Link to="/integrations" className="link">Integrations</Link> page.
        </Empty>
      </>
    )

  const missing = s.totals.items - s.totals.matched
  const current = config.tabs.find((t) => t.id === tab)!
  return (
    <>
      <PageTitle subtitle="Sizes come from the scanned files when matched, otherwise from the app.">{config.name}</PageTitle>
      <div className="mb-4 grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label={`Known to ${config.name}`} value={bytes(s.totals.size)} hint={`${count(s.totals.items)} files`} />
        <Stat label={config.noun[0].toUpperCase() + config.noun.slice(1)} value={count(s.totals.groups)} />
        <Stat label="Matched to files" value={count(s.totals.matched)} hint={missing ? `${count(missing)} not found on disk` : 'all found'} />
        <Stat label="Folders" value={count(s.roots.length)} hint={bytes(s.roots.reduce((a, r) => a + r.size, 0))} />
      </div>
      <div className="mb-4 grid gap-4 lg:grid-cols-2">
        {s.byState.length > 1 && (
          <Section title="By state">
            <BarList total={s.totals.size} items={s.byState.map((x) => ({ key: x.state || 'none', label: <>{x.state || '—'} <span className="text-xs muted">· {count(x.items)}</span></>, value: x.size }))} />
          </Section>
        )}
        {s.roots.length > 0 && (
          <Section title="Folders">
            <BarList items={s.roots.map((r) => ({ key: String(r.entryId), label: <span title={r.relPath}>{r.label}</span>, value: r.size }))} />
            <p className="mt-2 text-xs muted">
              {s.roots.map((r, i) => (
                <Link key={r.entryId} to={`/browse/${r.entryId}`} className="link">
                  {i > 0 ? ' · ' : ''}
                  {r.relPath || '/'}
                </Link>
              ))}
            </p>
          </Section>
        )}
      </div>

      <Tabs tabs={config.tabs.map((t) => ({ id: t.id, label: t.label }))} value={tab} onChange={setTab} />
      <p className="mb-3 text-sm muted">{current.hint}</p>
      {tab === 'titles' && <Titles config={config} />}
      {tab === 'untracked' && <Entries config={config} path="untracked?mode=files" empty="Everything in these folders is known." />}
      {tab === 'orphans' && <Entries config={config} path="untracked?mode=top" empty="Nothing in these folders is unknown." />}
      {tab === 'copies' && <Entries config={config} path="copies" empty="No copies found (only files with identical content are compared)." showItem />}
      {tab === 'unmatched' && <Unmatched config={config} />}
    </>
  )
}

function Titles({ config }: { config: ItemsConfig }) {
  const base = `/integrations/${config.id}`
  const [kind, setKind] = useState(config.kinds?.[0]?.id ?? '')
  const [filter, setFilter] = useState('')
  const [state, setState] = useState('')
  const [sort, setSort] = useState('size')
  const [q, setQ] = useState('')
  const [open, setOpen] = useState<GroupRow | null>(null)
  const top = useQuery({
    queryKey: [config.id, 'groups', kind, filter, state, sort, q],
    queryFn: () => api<{ total: number; totalSize: number; items: GroupRow[] }>(`${base}/groups`, { query: { kind, filter, state, sort, q, limit: 300 } }),
  })
  const unit = config.kinds?.find((k) => k.id === kind)?.unit ?? 'files'
  return (
    <>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        {config.kinds && config.kinds.length > 1 && (
          <div className="flex rounded-md border border-line p-0.5">
            {config.kinds.map((k) => (
              <button key={k.id} className={clsx('btn px-2.5 py-1', kind === k.id ? 'bg-accent-faint text-accent' : 'text-muted')} onClick={() => setKind(k.id)}>
                {k.label}
              </button>
            ))}
          </div>
        )}
        {config.filters && (
          <select className="input w-auto" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter">
            <option value="">All</option>
            {config.filters.map((f) => (
              <option key={f.id} value={f.id}>
                {f.label}
              </option>
            ))}
          </select>
        )}
        {config.states && (
          <select className="input w-auto" value={state} onChange={(e) => setState(e.target.value)} aria-label="State">
            <option value="">Any state</option>
            {config.states.map((f) => (
              <option key={f.id} value={f.id}>
                {f.label}
              </option>
            ))}
          </select>
        )}
        <select className="input w-auto" value={sort} onChange={(e) => setSort(e.target.value)} aria-label="Sort">
          <option value="size">Largest</option>
          <option value="files">Most files</option>
          <option value="lastUsed">{config.watch ? 'Recently watched' : 'Recently finished'}</option>
          <option value="added">Recently added</option>
          <option value="title">Title</option>
        </select>
        <input className="input max-w-xs" placeholder="Title…" value={q} onChange={(e) => setQ(e.target.value)} />
        {top.data && (
          <span className="ml-auto text-sm muted">
            {count(top.data.total)} {config.noun} · {bytes(top.data.totalSize)}
          </span>
        )}
      </div>
      {top.isLoading ? (
        <Loading />
      ) : top.error ? (
        <ErrorBox error={top.error} />
      ) : !top.data?.items.length ? (
        <Empty>Nothing matches.</Empty>
      ) : (
        <div className="card divide-y divide-line/60">
          {top.data.items.map((t) => {
            const max = top.data!.items[0].size || 1
            return (
              <button key={t.key} className="flex w-full items-center gap-3 px-3 py-2 text-left hover:bg-accent-faint" onClick={() => setOpen(t)}>
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2 text-sm">
                    <span className="truncate font-semibold">{t.title}</span>
                    {t.year ? <span className="muted">({t.year})</span> : null}
                    {t.states?.split(',').map((x) => <StatePill key={x} state={x} />)}
                    {t.details && <span className="pill-muted max-w-xs truncate">{t.details}</span>}
                    {t.missingFiles > 0 && <span className="rounded bg-danger/10 px-1.5 text-xs text-danger">{t.missingFiles} not found</span>}
                  </div>
                  <div className="text-xs muted">
                    {count(t.files)} {unit}
                    {config.watch && t.kind !== 'track' ? ` · ${t.playedFiles === 0 ? 'never watched' : t.playedFiles === t.files ? 'all watched' : `${t.playedFiles} watched`}` : ''}
                    {t.lastUsedAt ? ` · ${config.watch ? 'last watched' : 'finished'} ${ago(t.lastUsedAt)}` : ''}
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
      {open && <GroupDialog config={config} row={open} onClose={() => setOpen(null)} />}
    </>
  )
}

function GroupDialog({ config, row, onClose }: { config: ItemsConfig; row: GroupRow; onClose: () => void }) {
  const base = `/integrations/${config.id}`
  const qc = useQueryClient()
  const [preview, setPreview] = useState<number | null>(null)
  const [plan, setPlan] = useState(false)
  const q = useQuery({ queryKey: [config.id, 'group', row.key], queryFn: () => api<GroupFile[]>(`${base}/group`, { query: { key: row.key } }) })
  const unmonitor = useMutation({
    mutationFn: () => api<{ unmonitored: number }>(`${base}/unmonitor`, { method: 'POST', json: { keys: [row.key] } }),
    onSuccess: () => qc.invalidateQueries({ queryKey: [config.id] }),
  })
  const files = q.data ?? []
  const ids = files.flatMap((f) => (f.entryId ? [f.entryId] : []))
  const inUse = files.some((f) => f.state === 'seeding' || f.state === 'downloading')
  return (
    <Modal open onClose={onClose} title={`${row.title}${row.year ? ` (${row.year})` : ''}`} wide>
      <div className="mb-3 flex flex-wrap items-center gap-2 text-sm">
        <span className="font-semibold">{bytes(row.size)}</span>
        <span className="muted">· {count(row.files)} files</span>
        <span className="flex-1" />
        {row.url && (
          <a className="btn-ghost" href={row.url} target="_blank" rel="noreferrer">
            Open in {config.name} ↗
          </a>
        )}
        {row.folderId && (
          <Link className="btn-secondary" to={`/browse/${row.folderId}`} onClick={onClose}>
            Open folder
          </Link>
        )}
        {config.unmonitor && (
          <button className="btn-secondary" onClick={() => unmonitor.mutate()} disabled={unmonitor.isPending || unmonitor.isSuccess} title={`Stop ${config.name} from downloading it again`}>
            {unmonitor.isSuccess ? 'Unmonitored ✓' : 'Unmonitor'}
          </button>
        )}
        <button className="btn-primary" disabled={!ids.length} onClick={() => setPlan(true)}>
          Clean up…
        </button>
      </div>
      {unmonitor.error && <ErrorBox error={unmonitor.error} />}
      {inUse && <p className="mb-2 rounded-md bg-amber-faint px-3 py-2 text-sm text-amber">Still seeding or downloading — cleanup plans skip these files.</p>}
      {config.unmonitor && <p className="mb-2 text-xs muted">Unmonitor first, otherwise {config.name} downloads deleted files again.</p>}
      {q.isLoading ? (
        <Loading />
      ) : (
        <div className="divide-y divide-line/60 text-sm">
          {files.map((f) => (
            <div key={f.id} className="flex items-center gap-3 py-1.5">
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2 truncate">
                  {f.subTitle && <span className="muted">{f.subTitle} ·</span>}
                  <span className="truncate">{f.title}</span>
                  <StatePill state={f.state} />
                  {f.detail && <span className="pill-muted">{f.detail}</span>}
                </div>
                <div className="truncate text-xs muted" title={f.externalPath}>
                  {f.relPath ?? <span className="text-danger">not found: {f.externalPath}</span>}
                </div>
              </div>
              {config.watch && f.kind !== 'track' && <span className="text-xs muted">{f.playCount > 0 ? `✓ ${f.lastUsedAt ? ago(f.lastUsedAt) : 'watched'}` : 'unwatched'}</span>}
              <span className="w-20 text-right tabular">{bytes(f.size)}</span>
              {f.entryId && (
                <button className="btn-ghost px-2 py-0.5 text-xs" onClick={() => setPreview(f.entryId!)}>
                  Details
                </button>
              )}
            </div>
          ))}
        </div>
      )}
      <EntryDrawer id={preview} onClose={() => setPreview(null)} />
      <PlanDialog open={plan} onClose={() => setPlan(false)} entryIds={ids} count={ids.length} size={row.size} title={`Clean up: ${row.title}`} />
    </Modal>
  )
}

function Entries({ config, path, empty, showItem }: { config: ItemsConfig; path: string; empty: string; showItem?: boolean }) {
  const [preview, setPreview] = useState<number | null>(null)
  const [plan, setPlan] = useState(false)
  const q = useQuery({ queryKey: [config.id, path], queryFn: () => api<EntryPage>(`/integrations/${config.id}/${path}`, { query: { limit: 500 } }) })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  if (!q.data?.items.length) return <Empty>{empty}</Empty>
  const ids = q.data.items.map((f) => f.id)
  const shownSize = q.data.items.reduce((a, f) => a + f.size, 0)
  return (
    <>
      <div className="mb-2 flex flex-wrap items-center gap-2 text-sm">
        <span className="flex-1 muted">
          {count(q.data.total)} entries · {bytes(q.data.totalSize)}
          {q.data.total > q.data.items.length ? ` (largest ${count(q.data.items.length)} shown, ${pct(shownSize, q.data.totalSize)} of the size)` : ''}
        </span>
        <button className="btn-primary" onClick={() => setPlan(true)}>
          Clean up shown…
        </button>
      </div>
      <div className="card divide-y divide-line/60">
        {q.data.items.map((f) => (
          <button key={f.id} className="flex w-full items-center gap-3 px-3 py-1.5 text-left text-sm hover:bg-accent-faint" onClick={() => setPreview(f.id)}>
            <span className="w-5 shrink-0 text-center">{f.isDir ? '📁' : '📄'}</span>
            <span className="min-w-0 flex-1">
              <span className="block truncate">{f.relPath}</span>
              {showItem && f.itemTitle && <span className="block truncate text-xs muted">same content as {f.itemTitle}</span>}
            </span>
            <span className="text-xs muted">{date(f.mtime)}</span>
            <span className="w-20 text-right tabular">{bytes(f.size)}</span>
          </button>
        ))}
      </div>
      <EntryDrawer id={preview} onClose={() => setPreview(null)} />
      <PlanDialog open={plan} onClose={() => setPlan(false)} entryIds={ids} count={ids.length} size={shownSize} title={`${config.name}: clean up ${count(ids.length)} entries`} />
    </>
  )
}

function Unmatched({ config }: { config: ItemsConfig }) {
  const q = useQuery({
    queryKey: [config.id, 'unmatched'],
    queryFn: () => api<{ kind: string; title: string; group?: string; externalPath: string; size: number; state?: string }[]>(`/integrations/${config.id}/unmatched`),
  })
  if (q.isLoading) return <Loading />
  if (!q.data?.length) return <Empty>Every file was found on disk.</Empty>
  return (
    <div className="card divide-y divide-line/60">
      {q.data.map((f) => (
        <div key={f.externalPath} className="flex items-center gap-3 px-3 py-1.5 text-sm">
          <div className="min-w-0 flex-1">
            <div className="truncate">{f.group && f.group !== f.title ? `${f.group} · ${f.title}` : f.title}</div>
            <div className="truncate font-mono text-xs muted">{f.externalPath}</div>
          </div>
          <StatePill state={f.state} />
          <span className="w-20 text-right tabular">{bytes(f.size)}</span>
        </div>
      ))}
    </div>
  )
}
