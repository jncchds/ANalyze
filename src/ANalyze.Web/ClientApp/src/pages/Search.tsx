import { useMemo, useState } from 'react'
import { useSearchParams } from 'react-router'
import { useInfiniteQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import { useCategories, useRoots } from '../api/hooks'
import type { Entry, EntryList, FileKind, SearchFilter } from '../api/types'
import { EntryDrawer } from '../components/EntryDrawer'
import { EntryTable, type SortKey } from '../components/EntryTable'
import { PlanDialog } from '../components/PlanDialog'
import { Empty, ErrorBox, Loading, PageTitle } from '../components/ui'
import { useScope } from '../hooks/useScope'
import { bytes, count, kindLabel, parseSize } from '../lib/format'

const KINDS: FileKind[] = ['video', 'audio', 'image', 'document', 'pdf', 'ebook', 'text', 'code', 'archive', 'diskImage', 'executable', 'font', 'database', 'model3D', 'other', 'unknown']
const PAGE = 300

const BOOL_KEYS = ['uncategorized', 'duplicates', 'dirs', 'emptyDirs', 'brokenLinks', 'sparse', 'hardlinked', 'corrupt', 'asc'] as const
const NUM_KEYS = ['rootId', 'categoryId', 'minSize', 'maxSize', 'olderThanDays', 'newerThanDays', 'notAccessedDays'] as const
const STR_KEYS = ['q', 'path', 'kind', 'ext', 'tag', 'segment', 'sort'] as const

/** The search filter lives in the URL so insights, the sidebar and bookmarks can link to any result set. */
function readFilter(p: URLSearchParams): SearchFilter {
  const f: Record<string, unknown> = {}
  for (const k of STR_KEYS) if (p.get(k)) f[k] = p.get(k)
  for (const k of NUM_KEYS) if (p.get(k)) f[k] = Number(p.get(k))
  for (const k of BOOL_KEYS) if (p.get(k)) f[k] = p.get(k) === 'true'
  return f as SearchFilter
}

export default function Search() {
  const [params, setParams] = useSearchParams()
  const filter = useMemo(() => readFilter(params), [params])
  const categories = useCategories()
  const roots = useRoots()
  const { rootId: scopeRoot } = useScope()
  const effective: SearchFilter = { ...filter, rootId: filter.rootId ?? scopeRoot }
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [preview, setPreview] = useState<number | null>(null)
  const [plan, setPlan] = useState<'selection' | 'all' | null>(null)
  const [q, setQ] = useState(filter.q ?? '')

  const set = (patch: Partial<Record<keyof SearchFilter, string | number | boolean | undefined>>) => {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(patch)) {
      if (v === undefined || v === '' || v === false) next.delete(k)
      else next.set(k, String(v))
    }
    setParams(next, { replace: true })
    setSelected(new Set())
  }

  const results = useInfiniteQuery({
    queryKey: ['search', effective],
    queryFn: ({ pageParam }) => api<EntryList>('/search', { query: { ...(effective as Record<string, string | number | boolean | undefined>), offset: pageParam, limit: PAGE } }),
    initialPageParam: 0,
    getNextPageParam: (last, pages) => (pages.length * PAGE < last.total ? pages.length * PAGE : undefined),
  })
  const items = useMemo(() => results.data?.pages.flatMap((p) => p.items) ?? [], [results.data])
  const annotations = useMemo(() => Object.assign({}, ...(results.data?.pages.map((p) => p.annotations) ?? [])), [results.data])
  const first = results.data?.pages[0]
  const selectedItems = items.filter((i) => selected.has(i.id))
  const hasFilter = Object.keys(filter).some((k) => k !== 'sort' && k !== 'asc')

  const onSort = (s: SortKey) => set(s === (filter.sort ?? 'size') ? { asc: !filter.asc } : { sort: s, asc: s === 'name' })

  return (
    <>
      <PageTitle
        subtitle={first ? `${count(first.total)} ${filter.dirs || filter.emptyDirs ? 'folders' : 'files'} · ${bytes(first.totalSize)}` : undefined}
        actions={
          first && first.total > 0 ? (
            <button className="btn-primary" onClick={() => setPlan('all')}>
              Clean up all {count(first.total)}…
            </button>
          ) : undefined
        }
      >
        Search
      </PageTitle>

      <div className="card mb-4 space-y-3 p-3">
        <form
          className="flex gap-2"
          onSubmit={(e) => {
            e.preventDefault()
            set({ q: q.trim() || undefined })
          }}
        >
          <input className="input" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name contains… (use * as wildcard, include / to match the path)" />
          <button className="btn-primary">Search</button>
        </form>
        <div className="grid grid-cols-2 gap-2 md:grid-cols-4 lg:grid-cols-6">
          {(roots.data?.length ?? 0) > 1 && (
            <select className="input" value={filter.rootId ?? ''} onChange={(e) => set({ rootId: e.target.value || undefined })} aria-label="Root">
              <option value="">{scopeRoot ? 'Selected root' : 'All roots'}</option>
              {roots.data?.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.name}
                </option>
              ))}
            </select>
          )}
          <select
            className="input"
            value={filter.uncategorized ? 'none' : filter.categoryId ?? ''}
            onChange={(e) => set(e.target.value === 'none' ? { uncategorized: true, categoryId: undefined } : { categoryId: e.target.value || undefined, uncategorized: undefined })}
            aria-label="Category"
          >
            <option value="">Any category</option>
            <option value="none">Uncategorized</option>
            {categories.data?.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
          <select className="input" value={filter.kind ?? ''} onChange={(e) => set({ kind: e.target.value || undefined })} aria-label="Type">
            <option value="">Any type</option>
            {KINDS.map((k) => (
              <option key={k} value={k}>
                {kindLabel(k)}
              </option>
            ))}
          </select>
          <input className="input" placeholder="Extensions: mkv,mp4" defaultValue={filter.ext} onBlur={(e) => set({ ext: e.target.value.trim() || undefined })} aria-label="Extensions" />
          <input className="input" placeholder="Min size: 100MB" defaultValue={filter.minSize ? bytes(filter.minSize) : ''} onBlur={(e) => set({ minSize: parseSize(e.target.value) })} aria-label="Minimum size" />
          <input className="input" placeholder="Older than (days)" defaultValue={filter.olderThanDays} onBlur={(e) => set({ olderThanDays: e.target.value || undefined })} aria-label="Older than days" />
          <input className="input" placeholder="Tag" defaultValue={filter.tag} onBlur={(e) => set({ tag: e.target.value.trim() || undefined })} aria-label="Tag" />
          <input className="input" placeholder="Inside folder named…" defaultValue={filter.segment} onBlur={(e) => set({ segment: e.target.value.trim() || undefined })} aria-label="Folder name" />
          <input className="input" placeholder="Under path: Photos/2021" defaultValue={filter.path} onBlur={(e) => set({ path: e.target.value.trim() || undefined })} aria-label="Path" />
        </div>
        <div className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
          {(
            [
              ['dirs', 'Folders instead of files'],
              ['duplicates', 'Has duplicates'],
              ['hardlinked', 'Hard-linked'],
              ['corrupt', 'Possibly corrupted'],
              ['sparse', 'Sparse'],
              ['brokenLinks', 'Broken links'],
              ['emptyDirs', 'Empty folders'],
            ] as const
          ).map(([k, label]) => (
            <label key={k} className="flex items-center gap-1.5">
              <input type="checkbox" className="accent-accent" checked={!!filter[k]} onChange={(e) => set({ [k]: e.target.checked || undefined })} />
              {label}
            </label>
          ))}
          {hasFilter && (
            <button
              className="link ml-auto text-sm"
              onClick={() => {
                setQ('')
                setParams(new URLSearchParams(), { replace: true })
              }}
            >
              Clear filters
            </button>
          )}
        </div>
      </div>

      {selected.size > 0 && (
        <div className="sticky top-0 z-10 mb-3 flex flex-wrap items-center gap-3 rounded-lg border border-accent/30 bg-surface px-4 py-2 shadow-sm">
          <span className="text-sm">
            <b>{selected.size}</b> selected · {bytes(selectedItems.reduce((s, i) => s + i.size, 0))}
          </span>
          <span className="flex-1" />
          <button className="btn-ghost" onClick={() => setSelected(new Set())}>
            Clear
          </button>
          <button className="btn-primary" onClick={() => setPlan('selection')}>
            Clean up selection…
          </button>
        </div>
      )}

      {results.isLoading ? (
        <Loading />
      ) : results.error ? (
        <ErrorBox error={results.error} />
      ) : items.length === 0 ? (
        <Empty>No matches.</Empty>
      ) : (
        <EntryTable
          items={items}
          annotations={annotations}
          total={first?.totalSize}
          selected={selected}
          onToggle={(id) => setSelected((s) => toggle(s, id))}
          onToggleAll={(all) => setSelected(all ? new Set(items.map((i) => i.id)) : new Set())}
          onOpen={(e: Entry) => setPreview(e.id)}
          sort={(filter.sort as SortKey) ?? 'size'}
          asc={filter.asc}
          onSort={onSort}
          showPath
          footer={
            results.hasNextPage ? (
              <button className="btn-secondary" onClick={() => results.fetchNextPage()} disabled={results.isFetchingNextPage}>
                Load more ({count(items.length)} of {count(first?.total)})
              </button>
            ) : undefined
          }
        />
      )}

      <EntryDrawer id={preview} onClose={() => setPreview(null)} />
      <PlanDialog
        open={plan !== null}
        onClose={() => setPlan(null)}
        entryIds={plan === 'selection' ? [...selected] : undefined}
        filter={plan === 'all' ? effective : undefined}
        count={plan === 'selection' ? selected.size : first?.total}
        size={plan === 'selection' ? selectedItems.reduce((s, i) => s + i.size, 0) : first?.totalSize}
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
