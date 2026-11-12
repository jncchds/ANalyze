import { useEffect } from 'react'
import { Link } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { api, fileUrl } from '../api/client'
import { useCategories, useCategoryMap, useSummary, useToHostPath } from '../api/hooks'
import type { Annotation, Entry } from '../api/types'
import { bytes, date, kindLabel } from '../lib/format'
import { PlanDialog } from './PlanDialog'
import { AnnotationBadges, CategoryPill, ErrorBox, Loading, Tabs } from './ui'
import { BackupCompareView } from './BackupCompareView'
import { useUrlOverlay, type EntryPreview } from '../hooks/useUrlState'
import { HexView, MainPreview, MetaView, TextView, previewTabs } from './viewers'

interface EntryDetail {
  entry: Entry
  root?: { id: number; name: string; path: string; readOnly: boolean }
  fullPath?: string
  breadcrumbs: { id: number; name: string }[]
  copies: number
  linkTarget?: string
  ai?: { id: number; category: string; tags: string[]; confidence: number; reason?: string; applied: boolean; model: string }
  annotations: Annotation[]
  /** How this entry compares with the backup server (the parent ANalyze integration). */
  backup?: { state: string; isDir: boolean; parentSize?: number; verified: boolean; children: number; syncedChildren: number }
}

function backupText(b: NonNullable<EntryDetail['backup']>, size: number) {
  if (b.isDir)
    return b.state === 'synced'
      ? `Synchronized to the backup${b.children ? ` — ${b.syncedChildren} of ${b.children} direct items there` : ' (identical, empty)'}`
      : `Not synchronized — ${b.syncedChildren} of ${b.children} direct items on the backup`
  switch (b.state) {
    case 'synced':
      return b.verified ? 'On the backup — same content hash' : 'On the backup'
    case 'corrupt':
      return b.parentSize !== undefined && b.parentSize !== size ? `Corrupted — the backup has ${bytes(b.parentSize)}` : 'Corrupted — the backup has other content (same size)'
    case 'missing':
      return 'Missing from the backup (its folder is synchronized)'
    default:
      return 'Not on the backup'
  }
}

/**
 * Side panel with details, category, integrations, actions and the preview/text/hex/metadata viewers. It is opened through the
 * URL (`useEntryPreview`: `?file=<id>&fileTab=…`), so Back closes it. With `list` (the files of the page, in its order) it steps to
 * the previous/next file (buttons, ← / → keys) in place, keeping the viewer tab.
 */
export function EntryDrawer({ preview, list }: { preview: EntryPreview; list?: number[] }) {
  const { id, close: onClose, step: onNavigate, tab, setTab } = preview
  const qc = useQueryClient()
  const categories = useCategories()
  const catMap = useCategoryMap()
  const summary = useSummary()
  const plan = useUrlOverlay(`${preview.key}Plan`)
  const hostPath = useToHostPath()
  const q = useQuery({ queryKey: ['entry', id], queryFn: () => api<EntryDetail>(`/entries/${id}`), enabled: id !== null })

  const pos = id !== null && list ? list.indexOf(id) : -1
  const prev = pos > 0 ? list![pos - 1] : undefined
  const next = pos >= 0 && pos < list!.length - 1 ? list![pos + 1] : undefined

  useEffect(() => {
    if (id === null || plan.value) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        // Also keeps a dialog the panel was opened from from closing with it.
        e.preventDefault()
        return onClose()
      }
      // Arrow keys belong to inputs and media controls while they have the focus.
      const t = e.target as HTMLElement | null
      if (e.altKey || e.ctrlKey || e.metaKey || t?.closest('input, textarea, select, video, audio, [contenteditable="true"]')) return
      if (e.key === 'ArrowLeft' && prev !== undefined) onNavigate!(prev)
      else if (e.key === 'ArrowRight' && next !== undefined) onNavigate!(next)
      else return
      e.preventDefault()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [id, plan.value, onClose, prev, next, onNavigate])

  const setCategory = useMutation({
    mutationFn: (categoryId: number | null) => api(`/entries/${id}/category`, { method: 'PUT', json: { categoryId } }),
    onSuccess: () => qc.invalidateQueries(),
  })
  const pinned = !!summary.data?.pinned.some((p) => p.id === id)
  const pin = useMutation({
    mutationFn: () => api(`/pins/${id}`, { method: pinned ? 'DELETE' : 'POST' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['summary'] }),
  })
  const rescan = useMutation({ mutationFn: () => api(`/entries/${id}/rescan`, { method: 'POST' }) })
  const clearCorrupt = useMutation({
    mutationFn: () => api(`/files/${id}/corrupt`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['entry', id] }),
  })

  if (id === null) return null
  const d = q.data
  const e = d?.entry
  const cat = e?.categoryId ? catMap.get(e.categoryId) : undefined
  // Files the backup has get a side-by-side comparison with its copy.
  const onBackup = !!d?.backup && !d.backup.isDir && (d.backup.state === 'synced' || d.backup.state === 'corrupt')
  // A damaged file the backup has a copy of can be replaced with it (a restore plan).
  const restorable = !!e && !!d?.backup && !d.backup.isDir && d.backup.parentSize !== undefined && (d.backup.state === 'corrupt' || !!e.corruptAt)
  const tabs = e && !e.isDir ? [...previewTabs(e), ...(onBackup ? [{ id: 'backup', label: 'Compare with backup' }] : [])] : []
  const shown = tabs.some((t) => t.id === tab) ? tab : 'preview'

  return (
    <>
      <div className="fixed inset-0 z-40 bg-black/30" onClick={onClose} />
      <aside
        className={clsx('fixed inset-y-0 right-0 z-50 flex flex-col border-l border-line bg-surface shadow-2xl', shown === 'backup' ? 'w-[min(110rem,100vw)]' : 'w-[min(52rem,100vw)]')}
        aria-label="Details"
      >
        <div className="flex items-start gap-3 border-b border-line px-5 py-3">
          <div className="min-w-0 flex-1">
            {d && (
              <nav className="mb-0.5 flex flex-wrap items-center gap-1 text-xs muted">
                {d.breadcrumbs.map((b, i) => (
                  <span key={b.id} className="flex items-center gap-1">
                    {i > 0 && <span>/</span>}
                    <Link to={`/browse/${b.id}`} className="hover:text-accent">
                      {b.name}
                    </Link>
                  </span>
                ))}
              </nav>
            )}
            <h2 className="truncate text-lg font-semibold" title={e?.name}>
              {e?.name ?? '…'}
            </h2>
          </div>
          {pos >= 0 && (
            <div className="flex shrink-0 items-center gap-1">
              <button className="btn-ghost px-2 py-1" disabled={prev === undefined} onClick={() => onNavigate!(prev!)} title="Previous file (←)" aria-label="Previous file">
                ‹
              </button>
              <span className="text-xs tabular muted">
                {pos + 1} / {list!.length}
              </span>
              <button className="btn-ghost px-2 py-1" disabled={next === undefined} onClick={() => onNavigate!(next!)} title="Next file (→)" aria-label="Next file">
                ›
              </button>
            </div>
          )}
          <button className="btn-ghost px-2 py-1" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>
        <div className="flex-1 overflow-y-auto px-5 py-4">
          {q.isLoading && <Loading />}
          {q.error && <ErrorBox error={q.error} />}
          {d && e && (
            <div className="space-y-5">
              <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm sm:grid-cols-4">
                <Info label="Size" value={bytes(e.size)} hint={e.alloc !== e.size ? `${bytes(e.alloc)} on disk` : undefined} />
                {e.isDir ? <Info label="Contents" value={`${e.files.toLocaleString()} files`} hint={`${e.dirs.toLocaleString()} folders`} /> : <Info label="Type" value={kindLabel(e.kind)} hint={e.mime} />}
                <Info label="Modified" value={date(e.mTime)} />
                <Info label="Accessed" value={date(e.aTime)} />
              </dl>

              <div className="space-y-2 text-sm">
                <div className="break-all font-mono text-xs muted">{d.fullPath}</div>
                {hostPath(d.fullPath) && <div className="break-all font-mono text-xs muted">On the host: {hostPath(d.fullPath)}</div>}
                {d.linkTarget && <div className="text-xs">🔗 → {d.linkTarget}{e.brokenLink && <span className="text-danger"> (broken)</span>}</div>}
                <div className="flex flex-wrap items-center gap-2">
                  {!e.isDir && <CategoryPill category={cat} source={e.categorySource} />}
                  {e.tags.map((t) => (
                    <Link key={t} to={`/search?tag=${encodeURIComponent(t)}`} className="chip">
                      #{t}
                    </Link>
                  ))}
                  {e.nLink > 1 && <span className="pill-muted">{e.nLink} hard links</span>}
                  {d.copies > 0 && (
                    <Link to={`/duplicates?q=${encodeURIComponent(e.name)}`} className="pill-amber">
                      {d.copies} identical {d.copies === 1 ? 'copy' : 'copies'}
                    </Link>
                  )}
                </div>
                <AnnotationBadges items={d.annotations} />
                {e.corruptAt && (
                  <div className="rounded-md bg-danger/10 px-3 py-2 text-xs text-danger">
                    Possibly corrupted: on {date(e.corruptAt)} the content no longer matched its hash although size and date were unchanged (bit rot, or a program that
                    restores timestamps). Compare it with a backup.{' '}
                    <button className="link" onClick={() => clearCorrupt.mutate()} disabled={clearCorrupt.isPending}>
                      The new content is fine
                    </button>
                  </div>
                )}
                {d.backup && (
                  <div className={clsx('text-xs', d.backup.state === 'synced' ? 'text-accent' : d.backup.state === 'corrupt' ? 'text-danger' : 'text-amber')}>
                    🛟 {backupText(d.backup, e.size)}
                    {restorable && (
                      <>
                        {' '}
                        <button className="link" disabled={d.root?.readOnly} onClick={() => plan.open('restore')} title={d.root?.readOnly ? 'Root is read-only' : undefined}>
                          Replace with the backup's copy…
                        </button>
                      </>
                    )}
                  </div>
                )}
                {!e.isDir && e.hashedAt && !e.corruptAt && <div className="text-xs muted">Content hash checked {date(e.hashedAt)}</div>}
                {d.ai && !d.ai.applied && (
                  <p className="text-xs muted">
                    ✨ AI suggests <b>{d.ai.category}</b> ({Math.round(d.ai.confidence * 100)}%): {d.ai.reason}
                  </p>
                )}
              </div>

              <div className="flex flex-wrap items-center gap-2">
                <label className="flex items-center gap-2 text-sm">
                  <span className="muted">{e.isDir ? 'Set category for all files inside' : 'Category'}</span>
                  <select
                    className="input w-auto py-1"
                    value={e.categorySource === 'manual' ? e.categoryId ?? '' : ''}
                    onChange={(ev) => setCategory.mutate(ev.target.value ? Number(ev.target.value) : null)}
                  >
                    <option value="">Automatic (rules/AI)</option>
                    {categories.data?.map((c) => (
                      <option key={c.id} value={c.id}>
                        {c.name}
                      </option>
                    ))}
                  </select>
                </label>
                <span className="flex-1" />
                {e.isDir ? (
                  <>
                    <Link className="btn-secondary" to={`/browse/${e.id}`}>
                      Open folder
                    </Link>
                    <button className="btn-ghost" onClick={() => pin.mutate()}>
                      {pinned ? 'Unpin' : '📌 Pin'}
                    </button>
                  </>
                ) : (
                  <a className="btn-secondary" href={fileUrl(e.id, 'content', { download: true })}>
                    Download
                  </a>
                )}
                <button className="btn-ghost" onClick={() => rescan.mutate()} disabled={rescan.isPending} title="Rescan this folder">
                  ↻ Rescan
                </button>
                <button className="btn-primary" disabled={d.root?.readOnly} onClick={() => plan.open(1)} title={d.root?.readOnly ? 'Root is read-only' : undefined}>
                  Clean up…
                </button>
              </div>

              {!e.isDir && (
                <div>
                  <Tabs tabs={tabs.map((t) => ({ id: t.id, label: t.label }))} value={shown} onChange={setTab} />
                  {shown === 'preview' && <MainPreview key={e.id} entry={e} />}
                  {shown === 'text' && <TextView key={e.id} entry={e} />}
                  {shown === 'hex' && <HexView key={e.id} entry={e} />}
                  {shown === 'meta' && <MetaView entry={e} />}
                  {shown === 'backup' && <BackupCompareView key={e.id} entry={e} />}
                </div>
              )}
            </div>
          )}
        </div>
      </aside>
      {e && (
        <PlanDialog
          key={plan.value === 'restore' ? 'restore' : 'plan'}
          open={plan.value !== null}
          onClose={plan.close}
          entryIds={[e.id]}
          count={1}
          size={e.size}
          defaultOp={plan.value === 'restore' ? 'restore' : undefined}
          title={plan.value === 'restore' ? "Replace with the backup's copy" : undefined}
        />
      )}
    </>
  )
}

function Info({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div>
      <dt className="text-xs muted">{label}</dt>
      <dd className="font-semibold tabular">{value}</dd>
      {hint && <dd className="truncate text-xs muted" title={hint}>{hint}</dd>}
    </div>
  )
}
