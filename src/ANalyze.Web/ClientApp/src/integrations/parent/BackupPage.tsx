import { useState } from 'react'
import { Link } from 'react-router'
import { useMutation, useQuery } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../../api/client'
import { BarList } from '../../components/charts'
import { EntryDrawer } from '../../components/EntryDrawer'
import { PlanDialog } from '../../components/PlanDialog'
import { Empty, ErrorBox, Loading, PageTitle, Section, ShareBar, Stat, Tabs } from '../../components/ui'
import { ago, bytes, count, date, pct } from '../../lib/format'

type State = 'verified' | 'matched' | 'outdated' | 'corrupt' | 'missing' | 'extra'

const STATES: { id: State; label: string; color: string; hint: string }[] = [
  { id: 'verified', label: 'Verified', color: 'var(--accent)', hint: 'Same content: both hashes are equal.' },
  { id: 'matched', label: 'Same size and date', color: 'color-mix(in srgb, var(--accent) 55%, transparent)', hint: 'Content not compared — a hash is missing on one side.' },
  { id: 'outdated', label: 'Outdated', color: 'var(--amber)', hint: 'Another version: size or date differ.' },
  { id: 'corrupt', label: 'Corrupt', color: 'var(--danger)', hint: 'Same size and date, different content: one copy is damaged.' },
  { id: 'missing', label: 'Missing', color: 'color-mix(in srgb, var(--amber) 55%, transparent)', hint: 'On the parent, not in this backup.' },
  { id: 'extra', label: 'Only here', color: 'var(--text-muted)', hint: 'In the backup folders but not on the parent (deleted there?).' },
]
const stateInfo = (s: string) => STATES.find((x) => x.id === s)

interface Counts {
  files: number
  size: number
  states: Partial<Record<State, { files: number; size: number }>>
}

interface Target {
  parentPath: string
  localPath: string
  rootId?: number
  relPath?: string
  entryId?: number
  rootName?: string
}

interface Summary {
  snapshot?: {
    syncedAt: string
    parentName: string
    url: string
    info: { version?: string; hashAllFiles: boolean; verifyAfterDays: number; roots: { path: string; files: number; size: number; hashed: number }[] }
    detected: { from: string; to: string }[]
    unmapped: number
    hashesCompared: boolean
  }
  targets?: Target[]
  byState: { state: State; files: number; size: number }[]
  corruptBySide: { side: string; files: number }[]
  unmapped: { files: number; size: number }
  local: { hashAllFiles: boolean; verifyAfterDays: number }
}

interface FileRow {
  id: number
  state: State
  side?: string
  entryId?: number
  rootId?: number
  relPath?: string
  sourcePath?: string
  size: number
  mtime: number
  localSize?: number
  localMTime?: number
}

type Tab = 'folders' | State

/** Backup page of the "ANalyze parent" integration: how this instance's copy compares with the parent's files. */
export default function BackupPage() {
  const [tab, setTab] = useState<Tab>('folders')
  const q = useQuery({ queryKey: ['parent', 'summary'], queryFn: () => api<Summary>('/integrations/parent/summary') })
  const sync = useMutation({ mutationFn: () => api('/integrations/parent/sync', { method: 'POST' }) })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const s = q.data!
  const snap = s.snapshot
  if (!snap)
    return (
      <>
        <PageTitle>Backup</PageTitle>
        <Empty>
          Not compared yet. Enter the parent's URL and token on the <Link to="/integrations" className="link">Integrations</Link> page and sync.
        </Empty>
      </>
    )

  const by = (st: State) => s.byState.find((x) => x.state === st) ?? { state: st, files: 0, size: 0 }
  const parentFiles = s.byState.filter((x) => x.state !== 'extra').reduce((a, x) => a + x.files, 0)
  const parentSize = s.byState.filter((x) => x.state !== 'extra').reduce((a, x) => a + x.size, 0)
  const okSize = by('verified').size + by('matched').size
  const okFiles = by('verified').files + by('matched').files
  const attention = by('missing').files + by('outdated').files + by('corrupt').files
  const parentHashed = snap.info.roots.reduce((a, r) => a + r.hashed, 0)
  const parentTotal = snap.info.roots.reduce((a, r) => a + r.files, 0)

  return (
    <>
      <PageTitle
        subtitle={
          <>
            Parent <span className="font-mono">{snap.url}</span> · ANalyze {snap.info.version ?? '?'} · compared {ago(snap.syncedAt)}
          </>
        }
        actions={
          <button className="btn-primary" onClick={() => sync.mutate()} disabled={sync.isPending}>
            {sync.isSuccess ? 'Comparing…' : 'Compare now'}
          </button>
        }
      >
        Backup of {snap.parentName}
      </PageTitle>
      {sync.error && <ErrorBox error={sync.error} />}

      <div className="mb-4 space-y-2">
        {(!snap.info.hashAllFiles || !s.local.hashAllFiles) && (
          <Notice>
            Content can only be compared when both instances hash every file ({!snap.info.hashAllFiles && 'the parent'}
            {!snap.info.hashAllFiles && !s.local.hashAllFiles && ' and '}
            {!s.local.hashAllFiles && 'this one'} {!snap.info.hashAllFiles && !s.local.hashAllFiles ? "don't" : "doesn't"}): turn on Settings → Integrity &amp; backups →
            "Hash every file". Until then files count as backed up by size and date. Parent: {pct(parentHashed, parentTotal)} of files hashed.
          </Notice>
        )}
        {s.local.verifyAfterDays === 0 && (
          <Notice muted>
            Turn on "Re-verify hashes" in Settings (here and on the parent) to detect bit rot in copies that were fine when first compared, and to tell which copy changed.
          </Notice>
        )}
        {snap.detected.length > 0 && (
          <Notice muted>
            Paths were detected from sample files: {snap.detected.map((m) => `${m.from} → ${m.to}`).join(', ')}. Save them as path mappings on the{' '}
            <Link to="/integrations" className="link">Integrations</Link> page to make them permanent.
          </Notice>
        )}
        {s.unmapped.files > 0 && (
          <Notice>
            {count(s.unmapped.files)} files ({bytes(s.unmapped.size)}) of the parent map to no scanned folder here. Add a path mapping for them.
          </Notice>
        )}
      </div>

      <div className="mb-4 grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Backed up" value={pct(okSize, parentSize)} hint={`${bytes(okSize)} of ${bytes(parentSize)} · ${count(okFiles)} of ${count(parentFiles)} files`} tone="accent" />
        <Stat label="Verified by content" value={count(by('verified').files)} hint={bytes(by('verified').size)} />
        <Stat label="Need attention" value={count(attention)} hint={`${count(by('corrupt').files)} corrupt · ${count(by('missing').files)} missing · ${count(by('outdated').files)} outdated`} tone={attention ? 'amber' : undefined} />
        <Stat label="Only in the backup" value={count(by('extra').files)} hint={bytes(by('extra').size)} />
      </div>

      <div className="mb-4 grid gap-4 lg:grid-cols-2">
        <Section title="By state">
          <BarList
            total={parentSize + by('extra').size}
            items={STATES.filter((x) => by(x.id).files > 0).map((x) => ({
              key: x.id,
              color: x.color,
              hint: x.hint,
              label: (
                <>
                  {x.label} <span className="text-xs muted">· {count(by(x.id).files)} files</span>
                </>
              ),
              value: by(x.id).size,
              onClick: () => setTab(x.id),
            }))}
          />
        </Section>
        <Section title="Folders">
          <ul className="space-y-2 text-sm">
            {s.targets?.map((t) => (
              <li key={t.parentPath}>
                <div className="font-mono text-xs">
                  {snap.parentName}:{t.parentPath} <span className="muted">→</span> {t.localPath}
                </div>
                {t.entryId ? (
                  <Link to={`/browse/${t.entryId}`} className="link text-xs">
                    Open in Browse
                  </Link>
                ) : (
                  <span className="text-xs text-amber">Not found here — add a path mapping.</span>
                )}
              </li>
            ))}
          </ul>
          {by('corrupt').files > 0 && (
            <p className="mt-3 text-xs muted">
              Corrupt:{' '}
              {s.corruptBySide
                .map((x) => `${count(x.files)} ${x.side === 'source' ? 'damaged on the parent (this copy is good)' : x.side === 'backup' ? 'damaged here' : 'side unknown'}`)
                .join(' · ')}
            </p>
          )}
        </Section>
      </div>

      <Tabs<Tab>
        value={tab}
        onChange={setTab}
        tabs={[
          { id: 'folders', label: 'Folders' },
          ...STATES.filter((x) => by(x.id).files > 0).map((x) => ({ id: x.id as Tab, label: `${x.label} (${count(by(x.id).files)})` })),
        ]}
      />
      {tab === 'folders' ? <Folders targets={(s.targets ?? []).filter((t) => t.rootId !== undefined)} /> : <Files key={tab} state={tab} parent={snap.parentName} />}
    </>
  )
}

function Notice({ children, muted }: { children: React.ReactNode; muted?: boolean }) {
  return <p className={clsx('rounded-md px-3 py-2 text-sm', muted ? 'bg-surface-2 muted' : 'bg-amber-faint text-amber')}>{children}</p>
}

/** Summary of one folder's counts: "3 missing · 1 corrupt", or ✓. */
function CountText({ c }: { c: Counts }) {
  const parts = (['missing', 'corrupt', 'outdated', 'extra'] as State[]).filter((st) => c.states[st]?.files).map((st) => `${count(c.states[st]!.files)} ${st === 'extra' ? 'only here' : st}`)
  if (parts.length === 0) return <span className="text-xs text-accent">✓ backed up{c.states.verified?.files === c.files ? ' · verified' : ''}</span>
  return <span className={clsx('text-xs', c.states.corrupt?.files ? 'text-danger' : 'text-amber')}>{parts.join(' · ')}</span>
}

function Folders({ targets }: { targets: Target[] }) {
  const [target, setTarget] = useState(0)
  const t = targets[target]
  const [path, setPath] = useState<string | null>(null)
  const current = path ?? t?.relPath ?? ''
  const q = useQuery({
    queryKey: ['parent', 'folders', t?.rootId, current],
    queryFn: () => api<{ path: string; entryId?: number; folders: { name: string; path: string; entryId?: number; counts: Counts }[]; here?: Counts }>('/integrations/parent/folders', { query: { rootId: t!.rootId, path: current } }),
    enabled: !!t,
  })
  if (!t) return <Empty>No backup folder was found here.</Empty>
  const base = t.relPath ?? ''
  const crumbs = current.length > base.length ? current.slice(base.length).split('/').filter(Boolean) : []
  const max = Math.max(1, ...(q.data?.folders.map((f) => f.counts.size) ?? []))
  return (
    <>
      <div className="mb-3 flex flex-wrap items-center gap-1 text-sm">
        {targets.length > 1 && (
          <select
            className="input mr-2 w-auto"
            value={target}
            onChange={(e) => {
              setTarget(Number(e.target.value))
              setPath(null)
            }}
            aria-label="Backup folder"
          >
            {targets.map((x, i) => (
              <option key={x.parentPath} value={i}>
                {x.parentPath}
              </option>
            ))}
          </select>
        )}
        <button className="link" onClick={() => setPath(base)}>
          {t.rootName}/{base}
        </button>
        {crumbs.map((c, i) => (
          <span key={i} className="flex items-center gap-1">
            <span className="muted">/</span>
            <button className="link" onClick={() => setPath([base, ...crumbs.slice(0, i + 1)].filter(Boolean).join('/'))}>
              {c}
            </button>
          </span>
        ))}
        <span className="flex-1" />
        {q.data?.entryId && (
          <Link className="btn-ghost px-2 py-1 text-xs" to={`/browse/${q.data.entryId}`}>
            Open in Browse
          </Link>
        )}
      </div>
      {q.isLoading ? (
        <Loading />
      ) : q.error ? (
        <ErrorBox error={q.error} />
      ) : !q.data?.folders.length && !q.data?.here ? (
        <Empty>Nothing here.</Empty>
      ) : (
        <div className="card divide-y divide-line/60">
          {q.data.folders.map((f) => (
            <button key={f.path} className="flex w-full items-center gap-3 px-3 py-2 text-left hover:bg-accent-faint" onClick={() => setPath(f.path)}>
              <span className="w-5 shrink-0 text-center">📁</span>
              <span className="min-w-0 flex-1">
                <span className="flex flex-wrap items-baseline gap-2">
                  <span className="truncate text-sm font-medium">{f.name}</span>
                  <CountText c={f.counts} />
                  {!f.entryId && <span className="text-xs text-amber">folder missing here</span>}
                </span>
                <ShareBar value={f.counts.size} max={max} className="mt-1 max-w-md" />
              </span>
              <span className="text-xs muted">{count(f.counts.files)} files</span>
              <span className="w-20 text-right text-sm font-semibold tabular">{bytes(f.counts.size)}</span>
            </button>
          ))}
          {q.data.here && (
            <div className="flex items-center gap-3 px-3 py-2 text-sm">
              <span className="w-5 shrink-0 text-center">📄</span>
              <span className="min-w-0 flex-1">
                <span className="mr-2 muted">Files directly in this folder</span>
                <CountText c={q.data.here} />
              </span>
              <span className="text-xs muted">{count(q.data.here.files)} files</span>
              <span className="w-20 text-right font-semibold tabular">{bytes(q.data.here.size)}</span>
            </div>
          )}
        </div>
      )}
    </>
  )
}

function detail(f: FileRow, parent: string) {
  switch (f.state) {
    case 'corrupt':
      return f.side === 'source' ? `damaged on ${parent} — this copy is the good one` : f.side === 'backup' ? 'this backup copy is damaged' : 'same size and date, different content'
    case 'outdated':
      return `${parent}: ${bytes(f.size)}, ${date(f.mtime)} · here: ${bytes(f.localSize)}, ${date(f.localMTime)}`
    case 'missing':
      return f.relPath ? `expected at ${f.relPath}` : 'no folder here for it'
    case 'extra':
      return `not on ${parent}`
    default:
      return date(f.mtime)
  }
}

function Files({ state, parent }: { state: State; parent: string }) {
  const [q, setQ] = useState('')
  const [pages, setPages] = useState(1)
  const [preview, setPreview] = useState<number | null>(null)
  const [plan, setPlan] = useState(false)
  const r = useQuery({
    queryKey: ['parent', 'files', state, q, pages],
    queryFn: () => api<{ total: number; totalSize: number; items: FileRow[] }>('/integrations/parent/files', { query: { state, q, limit: 200 * pages } }),
  })
  const info = stateInfo(state)
  const ids = r.data?.items.flatMap((f) => (f.entryId ? [f.entryId] : [])) ?? []
  return (
    <>
      <p className="mb-3 text-sm muted">{info?.hint}</p>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <input className="input max-w-xs" placeholder="Path…" value={q} onChange={(e) => setQ(e.target.value)} />
        {r.data && (
          <span className="flex-1 text-sm muted">
            {count(r.data.total)} files · {bytes(r.data.totalSize)}
          </span>
        )}
        {state === 'extra' && ids.length > 0 && (
          <button className="btn-secondary" onClick={() => setPlan(true)} title="Files the parent no longer has">
            Clean up shown…
          </button>
        )}
      </div>
      {r.isLoading ? (
        <Loading />
      ) : r.error ? (
        <ErrorBox error={r.error} />
      ) : !r.data?.items.length ? (
        <Empty>No files.</Empty>
      ) : (
        <>
          <div className="card divide-y divide-line/60">
            {r.data.items.map((f) => (
              <button
                key={f.id}
                className="flex w-full items-center gap-3 px-3 py-1.5 text-left text-sm enabled:hover:bg-accent-faint"
                disabled={!f.entryId}
                onClick={() => f.entryId && setPreview(f.entryId)}
              >
                <span className="min-w-0 flex-1">
                  <span className="block truncate" title={f.sourcePath}>
                    {f.relPath ?? f.sourcePath}
                  </span>
                  <span className={clsx('block truncate text-xs', state === 'corrupt' ? 'text-danger' : 'muted')}>{detail(f, parent)}</span>
                </span>
                <span className="w-20 text-right tabular">{bytes(f.size)}</span>
              </button>
            ))}
          </div>
          {r.data.total > r.data.items.length && (
            <button className="btn-ghost mt-2" onClick={() => setPages(pages + 1)}>
              Show more
            </button>
          )}
        </>
      )}
      <EntryDrawer id={preview} onClose={() => setPreview(null)} />
      {state === 'extra' && (
        <PlanDialog open={plan} onClose={() => setPlan(false)} entryIds={ids} count={ids.length} size={r.data?.items.reduce((a, f) => a + f.size, 0) ?? 0} title={`Only in the backup: ${count(ids.length)} files`} />
      )}
    </>
  )
}
