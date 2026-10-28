import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import { useCategoryMap, useRoots, useSummary } from '../api/hooks'
import type { FileKind } from '../api/types'
import { BarList } from '../components/charts'
import { EntryDrawer } from '../components/EntryDrawer'
import { Empty, ErrorBox, Loading, PageTitle, Section, Stat } from '../components/ui'
import { useScope } from '../hooks/useScope'
import { bytes, count, kindIcon, kindLabel } from '../lib/format'

interface OverviewData {
  totals: { files: number; dirs: number; size: number; alloc: number }
  byCategory: { categoryId?: number; files: number; size: number }[]
  byKind: { kind: FileKind; files: number; size: number }[]
  byExtension: { ext: string; files: number; size: number }[]
  largestFiles: { id: number; rootId: number; name: string; relPath: string; size: number; categoryId?: number; kind: FileKind; mtime: number }[]
  heaviestFolders: { id: number; rootId: number; name: string; relPath: string; direct: number; files: number; total: number }[]
  byAge: { label: string; files: number; size: number }[]
  bySize: { label: string; files: number; size: number }[]
}

export default function Overview() {
  const { rootId } = useScope()
  const navigate = useNavigate()
  const categories = useCategoryMap()
  const roots = useRoots()
  const summary = useSummary()
  const [preview, setPreview] = useState<number | null>(null)
  const q = useQuery({ queryKey: ['overview', rootId], queryFn: () => api<OverviewData>('/overview', { query: { rootId } }) })

  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const d = q.data!
  const root = roots.data?.find((r) => r.id === rootId)
  const neverScanned = (roots.data ?? []).every((r) => !r.lastScanAt)
  const reclaim = (summary.data?.reclaimable.duplicates.bytes ?? 0) + (summary.data?.reclaimable.categories.reduce((s, c) => s + c.bytes, 0) ?? 0)
  const search = (params: Record<string, string | number>) =>
    navigate(`/search?${new URLSearchParams(Object.entries({ ...params, ...(rootId ? { rootId } : {}) }).map(([k, v]) => [k, String(v)]))}`)

  if (neverScanned && d.totals.files === 0) {
    return (
      <>
        <PageTitle>Overview</PageTitle>
        <Empty>
          <p className="mb-2 text-lg font-semibold text-fg">Nothing scanned yet</p>
          <p>Press <b>Scan now</b> in the sidebar. The first scan reads every folder; later scans only look at what changed.</p>
          {(roots.data?.length ?? 0) === 0 && <p className="mt-2 text-danger">No roots are configured — mount a folder at /library or set ANalyze__Roots__0.</p>}
        </Empty>
      </>
    )
  }

  return (
    <>
      <PageTitle subtitle={root ? root.path : `${roots.data?.length ?? 0} root(s)`}>Overview</PageTitle>

      <div className="mb-6 grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Used by files" value={bytes(d.totals.size)} hint={d.totals.alloc !== d.totals.size ? `${bytes(d.totals.alloc)} allocated on disk` : undefined} />
        <Stat label="Files" value={count(d.totals.files)} hint={`${count(d.totals.dirs)} folders`} />
        <Stat label="Reclaimable" value={bytes(reclaim)} tone="amber" hint={<Link to="/insights" className="link">See insights →</Link>} />
        <Stat
          label="Free space"
          value={root?.diskFree !== undefined ? bytes(root.diskFree) : bytes(roots.data?.[0]?.diskFree)}
          hint={root?.diskTotal ? `of ${bytes(root.diskTotal)}` : roots.data?.[0]?.diskTotal ? `of ${bytes(roots.data[0].diskTotal)}` : undefined}
        />
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <Section title="By category">
          <BarList
            total={d.totals.size}
            items={d.byCategory.slice(0, 14).map((c) => {
              const cat = c.categoryId ? categories.get(c.categoryId) : undefined
              return {
                key: c.categoryId ?? 'none',
                label: (
                  <>
                    {cat?.name ?? 'Uncategorized'} <span className="text-xs muted">· {count(c.files)} files</span>
                  </>
                ),
                value: c.size,
                color: cat?.color ?? 'var(--text-muted)',
                onClick: () => search(c.categoryId ? { categoryId: c.categoryId } : { uncategorized: 'true' }),
              }
            })}
          />
        </Section>

        <Section title="Largest files">
          <ul className="divide-y divide-line/60">
            {d.largestFiles.slice(0, 12).map((f) => (
              <li key={f.id}>
                <button className="flex w-full items-center gap-2 rounded px-1.5 py-1.5 text-left text-sm hover:bg-accent-faint" onClick={() => setPreview(f.id)}>
                  <span>{kindIcon(f.kind)}</span>
                  <span className="min-w-0 flex-1">
                    <span className="block truncate">{f.name}</span>
                    <span className="block truncate text-xs muted">{f.relPath}</span>
                  </span>
                  {f.categoryId && <span className="h-2 w-2 shrink-0 rounded-full" style={{ background: categories.get(f.categoryId)?.color }} />}
                  <span className="shrink-0 font-semibold tabular">{bytes(f.size)}</span>
                </button>
              </li>
            ))}
          </ul>
        </Section>

        <Section title="Heaviest folders" actions={<span className="text-xs muted">by files directly inside</span>}>
          <BarList
            items={d.heaviestFolders.slice(0, 12).map((f) => ({
              key: f.id,
              label: (
                <span title={f.relPath}>
                  {f.relPath || '/'} <span className="text-xs muted">· {count(f.files)} files</span>
                </span>
              ),
              value: f.direct,
              onClick: () => navigate(`/browse/${f.id}`),
            }))}
          />
        </Section>

        <Section title="By type">
          <BarList
            total={d.totals.size}
            items={d.byKind.map((k) => ({
              key: k.kind,
              label: (
                <>
                  {kindIcon(k.kind)} {kindLabel(k.kind)} <span className="text-xs muted">· {count(k.files)} files</span>
                </>
              ),
              value: k.size,
              onClick: () => search({ kind: k.kind }),
            }))}
          />
        </Section>

        <Section title="Top extensions">
          <BarList
            total={d.totals.size}
            items={d.byExtension.slice(0, 15).map((x) => ({
              key: x.ext || '(none)',
              label: (
                <>
                  <span className="font-mono">.{x.ext || '(none)'}</span> <span className="text-xs muted">· {count(x.files)} files</span>
                </>
              ),
              value: x.size,
              onClick: x.ext ? () => search({ ext: x.ext }) : undefined,
            }))}
          />
        </Section>

        <Section title="Age (last modified)">
          <BarList
            total={d.totals.size}
            items={d.byAge.map((a) => ({
              key: a.label,
              label: (
                <>
                  {a.label} <span className="text-xs muted">· {count(a.files)} files</span>
                </>
              ),
              value: a.size,
            }))}
          />
          <h3 className="mt-5 mb-2 text-sm font-semibold tracking-wide text-muted uppercase">File size</h3>
          <BarList
            total={d.totals.size}
            items={d.bySize.map((a) => ({
              key: a.label,
              label: (
                <>
                  {a.label} <span className="text-xs muted">· {count(a.files)} files</span>
                </>
              ),
              value: a.size,
            }))}
          />
        </Section>
      </div>
      <EntryDrawer id={preview} onClose={() => setPreview(null)} />
    </>
  )
}
