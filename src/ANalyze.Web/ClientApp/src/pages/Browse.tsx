import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { useCategoryMap, useRoots } from '../api/hooks'
import type { Annotation, Entry, EntryList } from '../api/types'
import { Treemap, type TreeNode } from '../components/charts'
import { EntryDrawer } from '../components/EntryDrawer'
import { EntryTable, type SortKey } from '../components/EntryTable'
import { PlanDialog } from '../components/PlanDialog'
import { AnnotationBadges, Empty, ErrorBox, Loading, PageTitle, SavedLater, Section } from '../components/ui'
import { useScope } from '../hooks/useScope'
import { bytes, count, date, kindIcon } from '../lib/format'

interface Detail {
  entry: Entry
  root?: { id: number; name: string; path: string; readOnly: boolean }
  breadcrumbs: { id: number; name: string }[]
  annotations: Annotation[]
}

interface DiskItem {
  name: string
  relPath: string
  fullPath: string
  isDir: boolean
  isSymlink: boolean
  size: number
  mTime: number
  ignored: boolean
}

/** The folder on disk now, compared with the index. */
interface DiskListing {
  readable: boolean
  error?: string
  items: DiskItem[]
  gone: number
  truncated: boolean
}

const VIEW_KEY = 'analyze.browse.view'

/** Folder browser: list and treemap of a folder's children, sized and categorized, with multi-select actions. */
export default function Browse() {
  const params = useParams()
  const navigate = useNavigate()
  const roots = useRoots()
  const { rootId } = useScope()
  const categories = useCategoryMap()
  const [sort, setSort] = useState<SortKey>('size')
  const [asc, setAsc] = useState(false)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [preview, setPreview] = useState<number | null>(null)
  const [plan, setPlan] = useState(false)
  const [view, setView] = useState<'list' | 'map'>(() => (localStorage.getItem(VIEW_KEY) === 'map' ? 'map' : 'list'))

  // Without an id, open the selected root (or the first one).
  const fallbackRoot = roots.data?.find((r) => r.id === rootId) ?? roots.data?.find((r) => r.rootEntryId)
  const id = params.id ? Number(params.id) : fallbackRoot?.rootEntryId

  useEffect(() => setSelected(new Set()), [id])
  useEffect(() => {
    try {
      localStorage.setItem(VIEW_KEY, view)
    } catch {
      /* ignore */
    }
  }, [view])

  const detail = useQuery({ queryKey: ['entry', id], queryFn: () => api<Detail>(`/entries/${id}`), enabled: !!id })
  const children = useQuery({
    queryKey: ['children', id, sort, asc],
    queryFn: () => api<EntryList>(`/entries/${id}/children`, { query: { sort, asc, limit: 5000 } }),
    enabled: !!id,
  })
  const tree = useQuery({
    queryKey: ['tree', id],
    queryFn: () => api<TreeNode>(`/entries/${id}/tree`, { query: { depth: 2, maxChildren: 40 } }),
    enabled: !!id && view === 'map',
  })

  const qc = useQueryClient()
  const [savedLater, setSavedLater] = useState<string | null>(null)
  const disk = useQuery({
    queryKey: ['disk', id],
    queryFn: () => api<DiskListing>(`/entries/${id}/disk`),
    enabled: !!id && view === 'list',
  })
  const rootOf = detail.data?.root?.id
  const scanHere = useMutation({
    mutationFn: (relPath: string) => api('/scan', { method: 'POST', json: { rootId: rootOf, subPath: relPath } }),
  })
  const ignore = useMutation({
    mutationFn: (v: { relPath: string; ignore: boolean }) =>
      api<{ savedLater?: string | null }>('/ignore', { method: 'POST', json: { rootId: rootOf, relPath: v.relPath, ignore: v.ignore } }),
    onSuccess: (r) => {
      setSavedLater(r.savedLater ?? null)
      qc.invalidateQueries({ queryKey: ['disk'] })
      qc.invalidateQueries({ queryKey: ['settings'] })
    },
  })
  const enterFs = useMutation({
    mutationFn: (relPath: string) => api<{ savedLater?: string | null }>('/enter-filesystem', { method: 'POST', json: { rootId: rootOf, relPath } }),
    onSuccess: (r) => setSavedLater(r.savedLater ?? null),
  })

  const onSort = (s: SortKey) => {
    if (s === sort) setAsc(!asc)
    else {
      setSort(s)
      setAsc(s === 'name')
    }
  }
  const open = useCallback((e: Entry) => (e.isDir ? navigate(`/browse/${e.id}`) : setPreview(e.id)), [navigate])
  const openTree = useCallback((nid: number, isDir: boolean) => (isDir ? navigate(`/browse/${nid}`) : setPreview(nid)), [navigate])
  const items = useMemo(() => children.data?.items ?? [], [children.data])
  const selectedItems = items.filter((i) => selected.has(i.id))

  if (!id) {
    if (roots.isLoading) return <Loading />
    return <Empty>Nothing scanned yet. Start a scan from the sidebar.</Empty>
  }
  if (detail.error) return <ErrorBox error={detail.error} />
  const d = detail.data
  const e = d?.entry

  return (
    <>
      <PageTitle
        subtitle={
          e && (
            <>
              {bytes(e.size)} · {count(e.files)} files · {count(e.dirs)} folders · modified {date(e.mTime)}
              {d?.root?.readOnly && ' · 🔒 read-only'}
            </>
          )
        }
        actions={
          <>
            <div className="flex rounded-md border border-line p-0.5">
              <button className={`btn px-2.5 py-1 ${view === 'list' ? 'bg-accent-faint text-accent' : 'text-muted'}`} onClick={() => setView('list')}>
                ☰ List
              </button>
              <button className={`btn px-2.5 py-1 ${view === 'map' ? 'bg-accent-faint text-accent' : 'text-muted'}`} onClick={() => setView('map')}>
                ▦ Map
              </button>
            </div>
            {e && (
              <button className="btn-secondary" onClick={() => setPreview(e.id)}>
                Folder details
              </button>
            )}
            {e && (
              <button className="btn-secondary" onClick={() => scanHere.mutate(e.relPath)} disabled={scanHere.isPending} title="Scan only this folder and what is inside it">
                {scanHere.isSuccess ? 'Scan queued' : '⟳ Scan this folder'}
              </button>
            )}
            {e?.otherFilesystem && (
              <button
                className="btn-secondary"
                onClick={() => enterFs.mutate(e.relPath)}
                disabled={enterFs.isPending}
                title="Another filesystem (disk, dataset, subvolume) is mounted here. Add it to the filesystems to enter and scan it."
              >
                Enter this filesystem
              </button>
            )}
            {e && e.relPath !== '' && (
              <button
                className="btn-ghost"
                onClick={() => ignore.mutate({ relPath: e.relPath, ignore: true })}
                disabled={ignore.isPending}
                title="Add this folder's full path to the ignore list; the next scan of its parent removes it from the index"
              >
                🚫 Ignore this folder
              </button>
            )}
          </>
        }
      >
        <nav className="flex flex-wrap items-center gap-1 text-[1.15rem]">
          {d?.breadcrumbs.map((b, i) => (
            <span key={b.id} className="flex items-center gap-1">
              {i > 0 && <span className="muted">/</span>}
              {b.id === id ? <span>{b.name}</span> : <Link to={`/browse/${b.id}`} className="hover:text-accent">{b.name}</Link>}
            </span>
          ))}
          {d?.annotations && <AnnotationBadges items={d.annotations} />}
        </nav>
      </PageTitle>

      {(ignore.error || scanHere.error || enterFs.error) && <ErrorBox error={ignore.error ?? scanHere.error ?? enterFs.error} />}
      {savedLater && <SavedLater holder={savedLater} />}
      {ignore.isSuccess && ignore.variables?.ignore && ignore.variables.relPath === e?.relPath && (
        <p className="mb-3 text-sm muted">This folder is ignored now; it leaves the index the next time its parent folder is scanned.</p>
      )}

      {selected.size > 0 && (
        <div className="sticky top-0 z-10 mb-3 flex flex-wrap items-center gap-3 rounded-lg border border-accent/30 bg-surface px-4 py-2 shadow-sm">
          <span className="text-sm">
            <b>{selected.size}</b> selected · {bytes(selectedItems.reduce((s, i) => s + i.size, 0))}
          </span>
          <span className="flex-1" />
          <button className="btn-ghost" onClick={() => setSelected(new Set())}>
            Clear
          </button>
          <button className="btn-primary" onClick={() => setPlan(true)} disabled={d?.root?.readOnly}>
            Clean up…
          </button>
        </div>
      )}

      {view === 'map' ? (
        <div className="card p-2">
          {tree.isLoading ? <Loading /> : tree.data ? <Treemap data={tree.data} categories={categories} onOpen={openTree} height={560} /> : null}
        </div>
      ) : children.isLoading ? (
        <Loading />
      ) : children.error ? (
        <ErrorBox error={children.error} />
      ) : items.length === 0 ? (
        disk.data?.items.length ? null : <Empty>This folder is empty.</Empty>
      ) : (
        <EntryTable
          items={items}
          annotations={children.data?.annotations}
          total={e?.size}
          selected={selected}
          onToggle={(i) => setSelected((s) => toggle(s, i))}
          onToggleAll={(all) => setSelected(all ? new Set(items.map((i) => i.id)) : new Set())}
          onOpen={open}
          sort={sort}
          asc={asc}
          onSort={onSort}
          footer={
            children.data && children.data.total > items.length ? <span className="text-xs muted">Showing the {items.length} largest of {children.data.total} items.</span> : undefined
          }
        />
      )}

      {view === 'list' && disk.data && e && (
        <DiskDiff
          listing={disk.data}
          busy={ignore.isPending || scanHere.isPending}
          onScan={() => scanHere.mutate(e.relPath)}
          onIgnore={(i) => ignore.mutate({ relPath: i.relPath, ignore: !i.ignored })}
        />
      )}

      <EntryDrawer id={preview} onClose={() => setPreview(null)} />
      <PlanDialog
        open={plan}
        onClose={() => setPlan(false)}
        entryIds={[...selected]}
        count={selected.size}
        size={selectedItems.reduce((s, i) => s + i.size, 0)}
      />
    </>
  )
}

function toggle(s: Set<number>, id: number) {
  const n = new Set(s)
  if (n.has(id)) n.delete(id)
  else n.add(id)
  return n
}

/** What is in this folder on disk but not in the index: new since the last scan, or ignored. */
function DiskDiff({ listing, busy, onScan, onIgnore }: { listing: DiskListing; busy: boolean; onScan: () => void; onIgnore: (i: DiskItem) => void }) {
  if (!listing.readable) return <p className="mt-3 text-sm text-warning">The folder cannot be read on disk right now: {listing.error}</p>
  const notScanned = listing.items.filter((i) => !i.ignored).length
  if (listing.items.length === 0 && listing.gone === 0) return null
  return (
    <Section
      className="mt-4"
      title="On disk, not in the index"
      actions={
        notScanned > 0 || listing.gone > 0 ? (
          <button className="btn-primary px-2 py-1 text-xs" onClick={onScan} disabled={busy}>
            ⟳ Scan this folder
          </button>
        ) : undefined
      }
    >
      {listing.gone > 0 && (
        <p className="mb-2 text-sm muted">
          {listing.gone.toLocaleString()} {listing.gone === 1 ? 'item' : 'items'} in the index {listing.gone === 1 ? 'is' : 'are'} no longer on disk.
        </p>
      )}
      <ul className="divide-y divide-line">
        {listing.items.map((i) => (
          <li key={i.name} className="flex items-center gap-2 py-1.5 text-sm">
            <span className="shrink-0">{i.isSymlink ? '🔗' : kindIcon('unknown', i.isDir)}</span>
            <span className={`min-w-0 flex-1 truncate ${i.isDir ? 'font-semibold' : ''} ${i.ignored ? 'muted line-through' : ''}`} title={i.fullPath}>
              {i.name}
            </span>
            {i.ignored ? (
              <span className="pill-muted shrink-0" title="Matches the ignore list in Settings; scans skip it">
                🚫 ignored
              </span>
            ) : (
              <span className="pill-amber shrink-0" title="On disk but not scanned yet: added after the last scan of this folder">
                ⏳ not scanned yet
              </span>
            )}
            {!i.isDir && <span className="w-20 shrink-0 text-right text-xs muted">{bytes(i.size)}</span>}
            <button className="btn-ghost shrink-0 px-2 py-0.5 text-xs" onClick={() => onIgnore(i)} disabled={busy}>
              {i.ignored ? 'Stop ignoring' : 'Ignore'}
            </button>
          </li>
        ))}
      </ul>
      {listing.truncated && <p className="mt-2 text-xs muted">Only the first {listing.items.length.toLocaleString()} are listed.</p>}
    </Section>
  )
}
