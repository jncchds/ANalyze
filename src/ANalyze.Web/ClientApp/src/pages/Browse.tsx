import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import { useCategoryMap, useRoots } from '../api/hooks'
import type { Annotation, Entry, EntryList } from '../api/types'
import { Treemap, type TreeNode } from '../components/charts'
import { EntryDrawer } from '../components/EntryDrawer'
import { EntryTable, type SortKey } from '../components/EntryTable'
import { PlanDialog } from '../components/PlanDialog'
import { AnnotationBadges, Empty, ErrorBox, Loading, PageTitle } from '../components/ui'
import { useScope } from '../hooks/useScope'
import { bytes, count, date } from '../lib/format'

interface Detail {
  entry: Entry
  root?: { id: number; name: string; path: string; readOnly: boolean }
  breadcrumbs: { id: number; name: string }[]
  annotations: Annotation[]
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
        <Empty>This folder is empty.</Empty>
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
