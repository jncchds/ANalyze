import { useEffect, useRef, type ReactNode } from 'react'
import * as echarts from 'echarts/core'
import { TreemapChart } from 'echarts/charts'
import { TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import type { Category } from '../api/types'
import { useIsDark } from '../hooks/useTheme'
import { bytes, pct } from '../lib/format'

echarts.use([TreemapChart, TooltipComponent, CanvasRenderer])

export interface TreeNode {
  id: number
  name: string
  size: number
  isDir: boolean
  categoryId?: number
  files: number
  children?: TreeNode[]
  other?: boolean
}

/** Folder sizes as nested rectangles; click a folder to drill into it. Files take their category color. */
export function Treemap({ data, categories, onOpen, height = 420 }: {
  data: TreeNode
  categories: Map<number, Category>
  onOpen: (id: number, isDir: boolean) => void
  height?: number
}) {
  const ref = useRef<HTMLDivElement>(null)
  const dark = useIsDark()
  const total = data.size

  useEffect(() => {
    if (!ref.current) return
    const chart = echarts.init(ref.current, undefined, { renderer: 'canvas' })
    const surface = dark ? '#111e21' : '#ffffff'
    const dirColor = dark ? '#1f3a3f' : '#cfe3e1'
    const dirColor2 = dark ? '#284a50' : '#b5d6d2'
    const otherColor = dark ? '#2a3436' : '#e3e8e8'
    const text = dark ? '#e2eeee' : '#0f1f22'

    const toNode = (n: TreeNode, depth: number): object => ({
      name: n.name,
      value: n.size,
      id: String(n.id),
      raw: n,
      itemStyle: {
        color: n.other ? otherColor : n.isDir ? (depth % 2 ? dirColor2 : dirColor) : (n.categoryId ? categories.get(n.categoryId)?.color : undefined) ?? otherColor,
        borderColor: surface,
      },
      children: n.children?.map((c) => toNode(c, depth + 1)),
    })

    chart.setOption({
      tooltip: {
        backgroundColor: surface,
        borderColor: dark ? '#23383d' : '#d3dfdf',
        textStyle: { color: text, fontSize: 12 },
        formatter: (p: { data: { raw: TreeNode } }) => {
          const n = p.data.raw
          const cat = n.categoryId ? categories.get(n.categoryId)?.name : undefined
          return `<b>${escapeHtml(n.name)}</b><br/>${bytes(n.size)} · ${pct(n.size, total)}${n.isDir ? ` · ${n.files.toLocaleString()} files` : cat ? ` · ${escapeHtml(cat)}` : ''}`
        },
      },
      series: [
        {
          type: 'treemap',
          data: (data.children ?? []).map((c) => toNode(c, 0)),
          roam: false,
          nodeClick: false,
          breadcrumb: { show: false },
          width: '100%',
          height: '100%',
          top: 0,
          left: 0,
          squareRatio: 0.6,
          visibleMin: 300,
          label: { show: true, formatter: (p: { name: string; value: number }) => `${p.name}\n${bytes(p.value)}`, color: text, fontSize: 11, overflow: 'truncate' },
          upperLabel: { show: true, height: 18, color: text, fontSize: 11, formatter: (p: { name: string; value: number }) => `${p.name}  ${bytes(p.value)}` },
          itemStyle: { borderColor: surface, borderWidth: 2, gapWidth: 2, borderRadius: 4 },
          levels: [
            { itemStyle: { borderWidth: 3, gapWidth: 2 } },
            { itemStyle: { borderWidth: 2, gapWidth: 1 }, upperLabel: { show: true } },
            { itemStyle: { borderWidth: 1, gapWidth: 1 } },
          ],
        },
      ],
    })
    chart.on('click', (p) => {
      const n = (p.data as { raw?: TreeNode } | undefined)?.raw
      if (n && !n.other) onOpen(n.id, n.isDir)
    })
    const ro = new ResizeObserver(() => chart.resize())
    ro.observe(ref.current)
    return () => {
      ro.disconnect()
      chart.dispose()
    }
  }, [data, categories, dark, onOpen, total])

  return <div ref={ref} style={{ height }} className="w-full" role="img" aria-label="Folder size treemap" />
}

function escapeHtml(s: string) {
  return s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!)
}

export interface BarItem {
  key: string | number
  label: ReactNode
  value: number
  color?: string
  hint?: string
  onClick?: () => void
}

/** Labeled horizontal bars: label + value as text (never color alone), thin bar for magnitude. */
export function BarList({ items, total, format = bytes, empty }: { items: BarItem[]; total?: number; format?: (n: number) => string; empty?: ReactNode }) {
  const max = Math.max(1, ...items.map((i) => i.value))
  if (items.length === 0) return <div className="py-6 text-center text-sm muted">{empty ?? 'No data yet.'}</div>
  return (
    <ul className="space-y-1.5">
      {items.map((i) => (
        <li key={i.key}>
          <button
            type="button"
            onClick={i.onClick}
            disabled={!i.onClick}
            title={i.hint}
            className="group w-full rounded-md px-1.5 py-1 text-left enabled:hover:bg-accent-faint"
          >
            <div className="flex items-baseline gap-2 text-sm">
              {i.color && <span className="h-2.5 w-2.5 shrink-0 self-center rounded-sm" style={{ background: i.color }} />}
              <span className="min-w-0 flex-1 truncate">{i.label}</span>
              <span className="shrink-0 font-semibold tabular">{format(i.value)}</span>
              {total ? <span className="w-12 shrink-0 text-right text-xs muted tabular">{pct(i.value, total)}</span> : null}
            </div>
            <div className="mt-1 h-1.5 overflow-hidden rounded-full bg-surface-2">
              <div className="h-full rounded-r-[4px]" style={{ width: `${Math.max(0.8, (i.value / max) * 100)}%`, background: i.color ?? 'var(--accent)' }} />
            </div>
          </button>
        </li>
      ))}
    </ul>
  )
}
