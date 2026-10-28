import { useRef, type ReactNode } from 'react'
import { useVirtualizer } from '@tanstack/react-virtual'
import clsx from 'clsx'
import type { Annotations, Entry } from '../api/types'
import { useCategoryMap } from '../api/hooks'
import { date, kindIcon } from '../lib/format'
import { bytes, pct } from '../lib/format'
import { AnnotationBadges, CategoryPill, ShareBar } from './ui'

export type SortKey = 'size' | 'name' | 'mtime' | 'files'

/**
 * Virtualized list of files/folders with selection checkboxes. Rows show size share, category,
 * integration badges and dates; a click opens (folder) or previews (file).
 */
export function EntryTable({
  items,
  annotations,
  total,
  selected,
  onToggle,
  onToggleAll,
  onOpen,
  sort,
  asc,
  onSort,
  showPath,
  height = 'calc(100dvh - 22rem)',
  footer,
}: {
  items: Entry[]
  annotations?: Annotations
  total?: number
  selected?: Set<number>
  onToggle?: (id: number) => void
  onToggleAll?: (all: boolean) => void
  onOpen: (e: Entry) => void
  sort?: SortKey
  asc?: boolean
  onSort?: (s: SortKey) => void
  showPath?: boolean
  height?: string
  footer?: ReactNode
}) {
  const parentRef = useRef<HTMLDivElement>(null)
  const categories = useCategoryMap()
  const max = Math.max(1, ...items.map((i) => i.size))
  const shareOf = total ?? items.reduce((s, i) => s + i.size, 0)
  const virtual = useVirtualizer({ count: items.length, getScrollElement: () => parentRef.current, estimateSize: () => (showPath ? 52 : 44), overscan: 12 })
  const allSelected = !!selected && items.length > 0 && items.every((i) => selected.has(i.id))

  const Head = ({ k, children, className }: { k?: SortKey; children: ReactNode; className?: string }) => (
    <button
      type="button"
      disabled={!k || !onSort}
      onClick={() => k && onSort?.(k)}
      className={clsx('truncate text-left text-xs font-semibold tracking-wide text-muted uppercase enabled:hover:text-fg', className, sort === k && 'text-accent')}
    >
      {children}
      {sort === k ? (asc ? ' ↑' : ' ↓') : ''}
    </button>
  )

  return (
    <div className="card overflow-hidden">
      <div className="grid grid-cols-[1.75rem_minmax(0,1fr)_6.5rem] items-center gap-3 border-b border-line px-3 py-2 md:grid-cols-[1.75rem_minmax(0,1fr)_10rem_6.5rem_6rem]">
        {onToggleAll ? (
          <input type="checkbox" className="h-4 w-4 accent-accent" checked={allSelected} onChange={(e) => onToggleAll(e.target.checked)} aria-label="Select all" />
        ) : (
          <span />
        )}
        <Head k="name">Name</Head>
        <Head className="max-md:hidden">Category</Head>
        <Head k="size" className="text-right">Size</Head>
        <Head k="mtime" className="max-md:hidden">Modified</Head>
      </div>
      <div ref={parentRef} style={{ height, minHeight: '12rem' }} className="overflow-y-auto">
        <div style={{ height: virtual.getTotalSize(), position: 'relative' }}>
          {virtual.getVirtualItems().map((v) => {
            const e = items[v.index]
            const cat = e.categoryId ? categories.get(e.categoryId) : undefined
            return (
              <div
                key={e.id}
                data-index={v.index}
                style={{ position: 'absolute', top: 0, left: 0, right: 0, transform: `translateY(${v.start}px)`, height: v.size }}
                className={clsx(
                  'grid cursor-pointer grid-cols-[1.75rem_minmax(0,1fr)_6.5rem] items-center gap-3 border-b border-line/60 px-3 hover:bg-accent-faint md:grid-cols-[1.75rem_minmax(0,1fr)_10rem_6.5rem_6rem]',
                  selected?.has(e.id) && 'bg-accent-faint',
                )}
                onClick={() => onOpen(e)}
              >
                {onToggle ? (
                  <input
                    type="checkbox"
                    className="h-4 w-4 accent-accent"
                    checked={selected?.has(e.id) ?? false}
                    onClick={(ev) => ev.stopPropagation()}
                    onChange={() => onToggle(e.id)}
                    aria-label={`Select ${e.name}`}
                  />
                ) : (
                  <span />
                )}
                <div className="min-w-0">
                  <div className="flex min-w-0 items-center gap-2 text-sm">
                    <span className="shrink-0">{e.isSymlink ? '🔗' : kindIcon(e.kind, e.isDir)}</span>
                    <span className={clsx('truncate', e.isDir && 'font-semibold', e.brokenLink && 'text-danger line-through')} title={e.relPath}>
                      {e.name}
                    </span>
                    {e.isDir && <span className="shrink-0 text-xs muted">{e.files.toLocaleString()} files</span>}
                    {e.nLink > 1 && !e.isDir && <span className="pill-muted shrink-0" title="Hard-linked">×{e.nLink}</span>}
                    <AnnotationBadges items={annotations?.[e.id]} />
                  </div>
                  {showPath && <div className="truncate text-xs muted">{e.relPath}</div>}
                  <ShareBar value={e.size} max={max} color={cat?.color} className="mt-1 max-w-[18rem]" />
                </div>
                <div className="truncate max-md:hidden">{!e.isDir && <CategoryPill category={cat} source={e.categorySource} />}</div>
                <div className="text-right text-sm tabular">
                  <div className="font-semibold">{bytes(e.size)}</div>
                  <div className="text-xs muted">{pct(e.size, shareOf)}</div>
                </div>
                <div className="text-xs muted max-md:hidden">{date(e.mTime)}</div>
              </div>
            )
          })}
        </div>
      </div>
      {footer && <div className="border-t border-line px-3 py-2">{footer}</div>}
    </div>
  )
}
