import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fileUrl } from '../api/client'
import { useCategories, useCategoryMap, useSummary } from '../api/hooks'
import type { Annotation, Entry } from '../api/types'
import { bytes, date, kindLabel } from '../lib/format'
import { PlanDialog } from './PlanDialog'
import { AnnotationBadges, CategoryPill, ErrorBox, Loading, Tabs } from './ui'
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
}

/** Side panel with details, category, integrations, actions and the preview/text/hex/metadata viewers. */
export function EntryDrawer({ id, onClose }: { id: number | null; onClose: () => void }) {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const categories = useCategories()
  const catMap = useCategoryMap()
  const summary = useSummary()
  const [tab, setTab] = useState('preview')
  const [plan, setPlan] = useState(false)
  const q = useQuery({ queryKey: ['entry', id], queryFn: () => api<EntryDetail>(`/entries/${id}`), enabled: id !== null })

  useEffect(() => setTab('preview'), [id])
  useEffect(() => {
    if (id === null) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [id, onClose])

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

  if (id === null) return null
  const d = q.data
  const e = d?.entry
  const cat = e?.categoryId ? catMap.get(e.categoryId) : undefined
  const tabs = e && !e.isDir ? previewTabs(e) : []

  return (
    <>
      <div className="fixed inset-0 z-40 bg-black/30" onClick={onClose} />
      <aside className="fixed inset-y-0 right-0 z-50 flex w-[min(52rem,100vw)] flex-col border-l border-line bg-surface shadow-2xl" aria-label="Details">
        <div className="flex items-start gap-3 border-b border-line px-5 py-3">
          <div className="min-w-0 flex-1">
            {d && (
              <nav className="mb-0.5 flex flex-wrap items-center gap-1 text-xs muted">
                {d.breadcrumbs.map((b, i) => (
                  <span key={b.id} className="flex items-center gap-1">
                    {i > 0 && <span>/</span>}
                    <Link to={`/browse/${b.id}`} onClick={onClose} className="hover:text-accent">
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
                {d.linkTarget && <div className="text-xs">🔗 → {d.linkTarget}{e.brokenLink && <span className="text-danger"> (broken)</span>}</div>}
                <div className="flex flex-wrap items-center gap-2">
                  {!e.isDir && <CategoryPill category={cat} source={e.categorySource} />}
                  {e.tags.map((t) => (
                    <Link key={t} to={`/search?tag=${encodeURIComponent(t)}`} onClick={onClose} className="chip">
                      #{t}
                    </Link>
                  ))}
                  {e.nLink > 1 && <span className="pill-muted">{e.nLink} hard links</span>}
                  {d.copies > 0 && (
                    <Link to={`/duplicates?q=${encodeURIComponent(e.name)}`} onClick={onClose} className="pill-amber">
                      {d.copies} identical {d.copies === 1 ? 'copy' : 'copies'}
                    </Link>
                  )}
                </div>
                <AnnotationBadges items={d.annotations} />
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
                    <button className="btn-secondary" onClick={() => { onClose(); navigate(`/browse/${e.id}`) }}>
                      Open folder
                    </button>
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
                <button className="btn-primary" disabled={d.root?.readOnly} onClick={() => setPlan(true)} title={d.root?.readOnly ? 'Root is read-only' : undefined}>
                  Clean up…
                </button>
              </div>

              {!e.isDir && (
                <div>
                  <Tabs tabs={tabs.map((t) => ({ id: t.id, label: t.label }))} value={tab} onChange={setTab} />
                  {tab === 'preview' && <MainPreview entry={e} />}
                  {tab === 'text' && <TextView entry={e} />}
                  {tab === 'hex' && <HexView entry={e} />}
                  {tab === 'meta' && <MetaView entry={e} />}
                </div>
              )}
            </div>
          )}
        </div>
      </aside>
      {e && <PlanDialog open={plan} onClose={() => setPlan(false)} entryIds={[e.id]} count={1} size={e.size} />}
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
