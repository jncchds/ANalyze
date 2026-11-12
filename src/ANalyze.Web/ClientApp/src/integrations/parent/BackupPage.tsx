import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router'
import { useMutation, useQuery } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../../api/client'
import { BarList } from '../../components/charts'
import { EntryDrawer } from '../../components/EntryDrawer'
import { PathText } from '../../components/PathText'
import { Empty, ErrorBox, Loading, PageTitle, Section, ShareBar, Stat, Tabs } from '../../components/ui'
import { useLocate } from '../../hooks/useLocate'
import { useEntryPreview, useUrlEnum, useUrlNumber, useUrlParam, useUrlParams } from '../../hooks/useUrlState'
import { ago, bytes, count, pct } from '../../lib/format'
import { ComparePanel } from './ComparePanel'

type FileState = 'synced' | 'corrupt' | 'missing' | 'unsynced'

const FILE_STATES: { id: FileState; label: string; color: string; hint: string }[] = [
  { id: 'synced', label: 'Synchronized', color: 'var(--accent)', hint: 'The backup has a file at the same path.' },
  { id: 'corrupt', label: 'Corrupted', color: 'var(--danger)', hint: 'In a synchronized folder, but the copy on the backup has another size or content hash.' },
  { id: 'missing', label: 'Missing', color: 'var(--amber)', hint: 'In a synchronized folder, but not on the backup.' },
  { id: 'unsynced', label: 'Not synchronized', color: 'var(--text-muted)', hint: 'Not on the backup, in a folder that is not synchronized.' },
]

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
    info: { version?: string; hashAllFiles: boolean; roots: { path: string; files: number }[] }
    detected: { from: string; to: string }[]
    unmapped: number
    hashesCompared: boolean
  }
  targets?: Target[]
  files: { state: FileState; files: number; size: number }[]
  verified: number
  folders: { total: number; synced: number }
  threshold: number
  local: { hashAllFiles: boolean }
}

interface FolderRow {
  entryId: number
  name: string
  path: string
  state: 'synced' | 'unsynced'
  children: number
  synced: number
  size: number
}

interface FolderView {
  path: string
  self?: Omit<FolderRow, 'name' | 'path'>
  folders: FolderRow[]
  files: { state: FileState; files: number; size: number }[]
}

interface FileRow {
  id: number
  state: FileState
  entryId: number
  relPath: string
  parentPath?: string
  size: number
  parentSize?: number
  verified: boolean
}

type Tab = 'folders' | FileState

const TABS: Tab[] = ['folders', ...FILE_STATES.map((x) => x.id)]

const inTarget = (t: Target, f: { rootId: number; path: string }) =>
  t.rootId === f.rootId && (!t.relPath || f.path === t.relPath || f.path.startsWith(t.relPath + '/'))

/**
 * Backup page of the "ANalyze parent" integration: what of this instance is synchronized to the backup server (the parent).
 * Tab, folder (`?target=&path=`), filters and the file panel are in the URL, so Back steps through them.
 */
export default function BackupPage() {
  const [, setParams] = useUrlParams()
  const [tab, setTab] = useUrlEnum<Tab>('tab', TABS, 'folders')
  const q = useQuery({ queryKey: ['parent', 'summary'], queryFn: () => api<Summary>('/integrations/parent/summary') })
  // Opened from a label in the browser: the folder view at that folder (or the file's folder); `?entry=` becomes `?target=&path=`.
  const located = useLocate<{ rootId: number; path: string }>('parent')
  const focus = located.target
  const shownTargets = useMemo(() => q.data?.targets?.filter((t) => t.entryId), [q.data])
  useEffect(() => {
    if (!focus || !shownTargets) return
    const i = shownTargets.findIndex((t) => inTarget(t, focus))
    setParams({ entry: null, tab: null, target: i > 0 ? i : null, path: i >= 0 ? focus.path : null }, { replace: true })
  }, [focus, shownTargets, setParams])
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
          Not compared yet. Enter the backup server's URL and token on the <Link to="/integrations" className="link">Integrations</Link> page and sync.
        </Empty>
      </>
    )

  const by = (st: FileState) => s.files.find((x) => x.state === st) ?? { state: st, files: 0, size: 0 }
  const allFiles = s.files.reduce((a, x) => a + x.files, 0)
  const allSize = s.files.reduce((a, x) => a + x.size, 0)
  const onBackup = by('synced').files + by('corrupt').files
  const onBackupSize = by('synced').size + by('corrupt').size
  const threshold = Math.round(s.threshold * 100)

  return (
    <>
      <PageTitle
        subtitle={
          <>
            Backup server <span className="font-mono">{snap.url}</span> · ANalyze {snap.info.version ?? '?'} · compared {ago(snap.syncedAt)}
          </>
        }
        actions={
          <button className="btn-primary" onClick={() => sync.mutate()} disabled={sync.isPending}>
            {sync.isSuccess ? 'Comparing…' : 'Compare now'}
          </button>
        }
      >
        Backup on {snap.parentName}
      </PageTitle>
      {sync.error && <ErrorBox error={sync.error} />}

      <div className="mb-4 space-y-2">
        {(!snap.info.hashAllFiles || !s.local.hashAllFiles || !snap.hashesCompared) && (
          <Notice>
            Corrupted files are found by size only until both instances hash every file (Settings → Integrity &amp; backups → "Hash every file"
            {!snap.info.hashAllFiles ? ' — off on the backup server' : ''}
            {!s.local.hashAllFiles ? ' — off here' : ''}).
          </Notice>
        )}
        {snap.detected.length > 0 && (
          <Notice muted>
            Paths were detected from sample files: {snap.detected.map((m) => `${m.from} → ${m.to}`).join(', ')}. Save them as path mappings on the{' '}
            <Link to="/integrations" className="link">Integrations</Link> page to make them permanent.
          </Notice>
        )}
        {s.targets?.some((t) => !t.entryId) && (
          <Notice>
            Some of the backup's folders match no folder here: {s.targets.filter((t) => !t.entryId).map((t) => t.parentPath).join(', ')}. Add a path mapping.
          </Notice>
        )}
      </div>

      <div className="mb-4 grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Files on the backup" value={pct(onBackupSize, allSize)} hint={`${bytes(onBackupSize)} of ${bytes(allSize)} · ${count(onBackup)} of ${count(allFiles)} files`} tone="accent" />
        <Stat label="Synchronized folders" value={`${count(s.folders.synced)} / ${count(s.folders.total)}`} hint={`≥ ${threshold}% of their direct content on the backup, or identical`} />
        <Stat label="Corrupted" value={count(by('corrupt').files)} hint={`${bytes(by('corrupt').size)} · other size or hash on the backup`} tone={by('corrupt').files ? 'amber' : undefined} />
        <Stat label="Missing" value={count(by('missing').files)} hint={`${bytes(by('missing').size)} · in synchronized folders`} tone={by('missing').files ? 'amber' : undefined} />
      </div>

      <div className="mb-4 grid gap-4 lg:grid-cols-2">
        <Section title="Files by state">
          <BarList
            total={allSize}
            items={FILE_STATES.filter((x) => by(x.id).files > 0).map((x) => ({
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
          {s.verified > 0 && <p className="mt-2 text-xs muted">{count(s.verified)} synchronized files have the same content hash on both sides.</p>}
        </Section>
        <Section title="Backed-up folders">
          <ul className="space-y-2 text-sm">
            {s.targets?.map((t) => (
              <li key={t.parentPath}>
                <div className="font-mono text-xs">
                  {t.localPath} <span className="muted">→</span> {snap.parentName}:{t.parentPath}
                </div>
                <PathText path={t.localPath} label="ANalyze checks" className="muted" />
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
        </Section>
      </div>

      <Tabs<Tab>
        value={tab}
        onChange={(t) => setTab(t, { also: { q: null, explain: null } })}
        tabs={[
          { id: 'folders', label: 'Folders' },
          ...FILE_STATES.filter((x) => by(x.id).files > 0).map((x) => ({ id: x.id as Tab, label: `${x.label} (${count(by(x.id).files)})` })),
        ]}
      />
      {tab === 'folders' ? (
        <Folders targets={shownTargets ?? []} threshold={threshold} />
      ) : (
        <Files key={tab} state={tab} />
      )}
    </>
  )
}

function Notice({ children, muted }: { children: ReactNode; muted?: boolean }) {
  return <p className={clsx('rounded-md px-3 py-2 text-sm', muted ? 'bg-surface-2 muted' : 'bg-amber-faint text-amber')}>{children}</p>
}

function FolderPill({ state, children, synced }: { state: string; children: number; synced: number }) {
  const share = children ? ` · ${pct(synced, children)}` : ''
  return state === 'synced' ? <span className="chip">Synchronized{share}</span> : <span className="pill-amber">Not synchronized{share}</span>
}

function Folders({ targets, threshold }: { targets: Target[]; threshold: number }) {
  const [params, setParams] = useUrlParams()
  const [targetParam] = useUrlNumber('target')
  const target = targetParam < targets.length ? targetParam : 0
  const path = params.get('path')
  const setPath = (p: string) => setParams({ path: p, explain: null })
  const [preview, open] = useFilePreview()
  const t = targets[target]
  const current = path ?? t?.relPath ?? ''
  const q = useQuery({
    queryKey: ['parent', 'folders', t?.rootId, current],
    queryFn: () => api<FolderView>('/integrations/parent/folders', { query: { rootId: t!.rootId, path: current } }),
    enabled: !!t,
  })
  if (!t) return <Empty>No backed-up folder was found here.</Empty>
  const base = t.relPath ?? ''
  const crumbs = current.length > base.length ? current.slice(base.length).split('/').filter(Boolean) : []
  const max = Math.max(1, ...(q.data?.folders.map((f) => f.size) ?? []))
  const self = q.data?.self
  return (
    <>
      <div className="mb-3 flex flex-wrap items-center gap-1 text-sm">
        {targets.length > 1 && (
          <select
            className="input mr-2 w-auto"
            value={target}
            onChange={(e) => setParams({ target: Number(e.target.value) || null, path: null, explain: null })}
            aria-label="Backed-up folder"
          >
            {targets.map((x, i) => (
              <option key={x.parentPath} value={i}>
                {x.localPath}
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
        {self && (
          <span className="ml-2">
            <FolderPill state={self.state} children={self.children} synced={self.synced} />
          </span>
        )}
        <span className="flex-1" />
        {self && (
          <Link className="btn-ghost px-2 py-1 text-xs" to={`/browse/${self.entryId}`}>
            Open in Browse
          </Link>
        )}
      </div>
      {self && (
        <p className="mb-3 text-xs muted">
          {count(self.synced)} of {count(self.children)} direct items are on the backup — a folder is synchronized at {threshold}% or when it is identical.
        </p>
      )}
      {q.isLoading ? (
        <Loading />
      ) : q.error ? (
        <ErrorBox error={q.error} />
      ) : !q.data?.folders.length && !q.data?.files.length ? (
        <Empty>Nothing here.</Empty>
      ) : (
        <>
          {q.data.folders.length > 0 && (
            <div className="card mb-4 divide-y divide-line/60">
              {q.data.folders.map((f) => (
                <button key={f.path} className="flex w-full items-center gap-3 px-3 py-2 text-left hover:bg-accent-faint" onClick={() => setPath(f.path)}>
                  <span className="w-5 shrink-0 text-center">📁</span>
                  <span className="min-w-0 flex-1">
                    <span className="flex flex-wrap items-baseline gap-2">
                      <span className="truncate text-sm font-medium">{f.name}</span>
                      <FolderPill state={f.state} children={f.children} synced={f.synced} />
                    </span>
                    <ShareBar value={f.size} max={max} className="mt-1 max-w-md" />
                  </span>
                  <span className="text-xs muted">{count(f.children)} items</span>
                  <span className="w-20 text-right text-sm font-semibold tabular">{bytes(f.size)}</span>
                </button>
              ))}
            </div>
          )}
          {q.data.files.length > 0 && (
            <Section title="Files directly in this folder">
              <p className="mb-2 text-xs muted">
                {FILE_STATES.filter((x) => q.data!.files.some((f) => f.state === x.id))
                  .map((x) => `${count(q.data!.files.find((f) => f.state === x.id)!.files)} ${x.label.toLowerCase()}`)
                  .join(' · ')}
              </p>
              <FileList rootId={t.rootId!} path={current} direct onOpen={open} />
            </Section>
          )}
        </>
      )}
      {preview}
    </>
  )
}

/** The file panel (in the URL) with the list it was opened from (in memory, for previous/next). */
function useFilePreview() {
  const preview = useEntryPreview()
  const [list, setList] = useState<number[]>()
  const open: OpenFile = (id, ids) => {
    setList(ids)
    preview.open(id)
  }
  return [<EntryDrawer preview={preview} list={list} />, open] as const
}

function detail(f: FileRow) {
  switch (f.state) {
    case 'corrupt':
      return f.parentSize !== undefined && f.parentSize !== f.size ? `backup has ${bytes(f.parentSize)}, here ${bytes(f.size)}` : 'same size, other content hash on the backup'
    case 'missing':
      return `not on the backup${f.parentPath ? ` (${f.parentPath})` : ''}`
    case 'unsynced':
      return 'not on the backup; its folder is not synchronized'
    default:
      return f.verified ? 'same content hash on the backup' : 'on the backup'
  }
}

const STATE_TONE: Record<FileState, string> = { synced: 'chip', corrupt: 'rounded bg-danger/10 px-1.5 text-xs text-danger', missing: 'pill-amber', unsynced: 'pill-muted' }

/** Opens a file in the drawer, with the list it was opened from (for previous/next). */
type OpenFile = (id: number, list: number[]) => void

function FileList({ state, rootId, path, direct, q, onOpen }: { state?: FileState; rootId?: number; path?: string; direct?: boolean; q?: string; onOpen: OpenFile }) {
  const [pages, setPages] = useState(1)
  // The open "What happened?" comparison, in the URL so Back closes it.
  const [explainParam, setExplainParam] = useUrlNumber('explain', -1)
  const explain = explainParam < 0 ? null : explainParam
  const setExplain = (id: number | null) => setExplainParam(id ?? -1)
  const parentName = useQuery({ queryKey: ['parent', 'summary'], queryFn: () => api<Summary>('/integrations/parent/summary') }).data?.snapshot?.parentName ?? 'the backup'
  const r = useQuery({
    queryKey: ['parent', 'files', state, rootId, path, direct, q, pages],
    queryFn: () => api<{ total: number; totalSize: number; items: FileRow[] }>('/integrations/parent/files', { query: { state, rootId, path, direct, q, limit: 200 * pages } }),
  })
  if (r.isLoading) return <Loading />
  if (r.error) return <ErrorBox error={r.error} />
  if (!r.data?.items.length) return <Empty>No files.</Empty>
  return (
    <>
      <div className="divide-y divide-line/60">
        {r.data.items.map((f) => (
          <div key={f.id}>
            <div className="flex items-center gap-2">
              <button className="flex min-w-0 flex-1 items-center gap-3 px-1 py-1.5 text-left text-sm hover:bg-accent-faint" onClick={() => onOpen(f.entryId, r.data!.items.map((x) => x.entryId))}>
                <span className="min-w-0 flex-1">
                  <span className="block truncate" title={f.parentPath}>
                    {f.relPath}
                  </span>
                  <span className={clsx('block truncate text-xs', f.state === 'corrupt' ? 'text-danger' : 'muted')}>{detail(f)}</span>
                </span>
                {!state && <span className={STATE_TONE[f.state]}>{FILE_STATES.find((x) => x.id === f.state)?.label}</span>}
                <span className="w-20 text-right tabular">{bytes(f.size)}</span>
              </button>
              {f.state === 'corrupt' && (
                <button
                  className={clsx('btn-ghost shrink-0 px-2 py-1 text-xs', explain === f.entryId && 'bg-accent-faint')}
                  onClick={() => setExplain(explain === f.entryId ? null : f.entryId)}
                  title="Read both copies and explain how they differ"
                >
                  What happened?
                </button>
              )}
            </div>
            {explain === f.entryId && (
              <div className="mx-1 mb-2 rounded-md bg-surface-2 p-3">
                <ComparePanel entryId={f.entryId} parentName={parentName} />
              </div>
            )}
          </div>
        ))}
      </div>
      {r.data.total > r.data.items.length && (
        <button className="btn-ghost mt-2" onClick={() => setPages(pages + 1)}>
          Show more ({count(r.data.total - r.data.items.length)} left)
        </button>
      )}
    </>
  )
}

function Files({ state }: { state: FileState }) {
  const [q, setQParam] = useUrlParam('q')
  const setQ = (v: string) => setQParam(v, { replace: true })
  const [preview, open] = useFilePreview()
  const navigate = useNavigate()
  // Every damaged file (corrupt here, or flagged by the integrity check) replaced with the backup's copy: a draft plan to review.
  const restore = useMutation({
    mutationFn: () => api<{ id: number }>('/plans/restore-damaged', { method: 'POST', json: {} }),
    onSuccess: (p) => navigate(`/actions/${p.id}`),
  })
  const totals = useQuery({
    queryKey: ['parent', 'files', state, q, 'totals'],
    queryFn: () => api<{ total: number; totalSize: number }>('/integrations/parent/files', { query: { state, q, limit: 1 } }),
  })
  return (
    <>
      <p className="mb-3 text-sm muted">{FILE_STATES.find((x) => x.id === state)?.hint}</p>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <input className="input max-w-xs" placeholder="Path…" value={q} onChange={(e) => setQ(e.target.value)} />
        {totals.data && (
          <span className="text-sm muted">
            {count(totals.data.total)} files · {bytes(totals.data.totalSize)}
          </span>
        )}
        {state === 'corrupt' && (
          <button
            className="btn-primary ml-auto"
            disabled={restore.isPending || !totals.data?.total}
            onClick={() => restore.mutate()}
            title="Creates a plan to review: each damaged file is replaced with the backup's copy (checked against its size and hash), the damaged one goes to the trash"
          >
            🛟 Replace all with the backup's copy…
          </button>
        )}
      </div>
      {restore.error && <ErrorBox error={restore.error} />}
      <div className="card px-3 py-1">
        <FileList state={state} q={q} onOpen={open} />
      </div>
      {preview}
    </>
  )
}
