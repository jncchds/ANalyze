import { useEffect, useMemo, useState, type ReactNode } from 'react'
import clsx from 'clsx'
import { EntryDrawer } from './EntryDrawer'
import { badPill, okPill, type FileRow } from './ExtractedArchives'
import { FilterInput, ShareBar } from './ui'
import { useEntryPreview, useUrlParam } from '../hooks/useUrlState'
import { bytes, count, date } from '../lib/format'

/**
 * One path of a comparison between a source (an archive, or a folder) and a folder on disk: `entryId`/`diskSize`/`diskMTime` are the
 * disk side, `sourceId`/`sourceMTime` the source's file when it is indexed too, `elsewhere` a path on the other side with the same content.
 */
export interface CompareRow extends FileRow {
  sourceId?: number
  sourceMTime?: number
  elsewhere?: string
}

export type CompareState = FileRow['state']
export type CompareShow = 'problems' | 'all' | CompareState

export interface CompareFilter {
  id: CompareShow
  label: string
  title: string
}

/** The words of one kind of comparison: column headings, filters and how each row's state reads. */
export interface CompareText {
  /** Heading of the folder tree, e.g. “Folders in the archive”. */
  tree: string
  rootIcon: string
  rootLabel: string
  /** Column headings for the source side and the disk side. */
  a: string
  b: string
  filters: CompareFilter[]
  /** The states the “problems” filter shows. */
  problems: CompareState[]
  label: (f: CompareRow) => string
  /** Shown when the problems filter has nothing in the chosen folder. */
  noProblems: string
  /** Under the file list. */
  footer: (show: CompareShow, shown: number) => ReactNode
}

/** Counts per state below one folder (every level), and the bytes of the source's files there. */
interface Folder {
  name: string
  path: string
  children: Map<string, Folder>
  counts: Record<CompareState, number>
  bytes: number
  matchedBytes: number
}

/** States the tree counts as differences (the disk-only files are counted on their own, “+n”). */
const BAD: CompareState[] = ['missing', 'smaller', 'differs', 'corrupt']
const emptyCounts = (): Record<CompareState, number> => ({ matched: 0, missing: 0, smaller: 0, differs: 0, corrupt: 0, extra: 0 })
const bad = (c: Record<CompareState, number>) => BAD.reduce((n, s) => n + c[s], 0)
const parentOf = (path: string) => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')

function buildTree(files: CompareRow[]) {
  const root: Folder = { name: '', path: '', children: new Map(), counts: emptyCounts(), bytes: 0, matchedBytes: 0 }
  for (const f of files) {
    const parts = f.path.split('/').slice(0, -1)
    let node = root
    const visit = (n: Folder) => {
      n.counts[f.state]++
      if (f.state !== 'extra') n.bytes += f.size
      if (f.state === 'matched' || f.state === 'corrupt') n.matchedBytes += f.size
    }
    visit(node)
    for (let i = 0; i < parts.length; i++) {
      let next = node.children.get(parts[i])
      if (!next) {
        next = { name: parts[i], path: parts.slice(0, i + 1).join('/'), children: new Map(), counts: emptyCounts(), bytes: 0, matchedBytes: 0 }
        node.children.set(parts[i], next)
      }
      node = next
      visit(node)
    }
  }
  return root
}

const ROWS = 500

/**
 * A source compared with a folder on disk, folder by folder: the source's folder tree with per-folder counts and shares, and the chosen
 * folder's files filtered by state. Shared by the extraction page (archive ↔ extracted folder) and the duplicate folders page (folder ↔ folder).
 * The chosen folder, filter and text filter are in the URL (`dir`, `show`, `q`).
 */
export function FolderCompare({ files, text, defaultShow }: { files: CompareRow[]; text: CompareText; defaultShow: CompareShow }) {
  const preview = useEntryPreview()
  const [dir, setDir] = useUrlParam('dir')
  const [showParam, setShow] = useUrlParam('show')
  const [q, setQ] = useUrlParam('q')
  const [differencesOnly, setDifferencesOnly] = useState(false)
  const [rows, setRows] = useState(ROWS)

  const tree = useMemo(() => buildTree(files), [files])
  const show = (showParam || defaultShow) as CompareShow
  const problems = text.problems
  const inDir = useMemo(() => (dir ? files.filter((f) => f.path.startsWith(dir + '/')) : files), [files, dir])
  const dirCounts = useMemo(() => {
    const c = emptyCounts()
    for (const f of inDir) c[f.state]++
    return c
  }, [inDir])
  const shown = useMemo(() => {
    const needle = q.toLowerCase()
    const matches = (f: CompareRow) => show === 'all' || (show === 'problems' ? problems.includes(f.state) : f.state === show)
    return inDir
      .filter((f) => matches(f) && (!needle || f.path.toLowerCase().includes(needle) || f.target?.toLowerCase().includes(needle)))
      .sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0))
  }, [inDir, show, q, problems])
  useEffect(() => setRows(ROWS), [dir, show, q])

  return (
    <>
      <div className="grid gap-4 lg:grid-cols-[minmax(16rem,22rem)_1fr]">
        <section className="card min-w-0 p-3">
          <div className="mb-2 flex items-center gap-2">
            <h2 className="flex-1 text-sm font-semibold tracking-wide text-muted uppercase">{text.tree}</h2>
            <label className="flex items-center gap-1.5 text-xs muted" title="Hide folders without differences">
              <input type="checkbox" className="accent-accent" checked={differencesOnly} onChange={(e) => setDifferencesOnly(e.target.checked)} />
              differences only
            </label>
          </div>
          <div className="max-h-[70vh] overflow-auto text-sm">
            <FolderNode node={tree} depth={0} selected={dir} onSelect={(p) => setDir(p)} differencesOnly={differencesOnly} text={text} />
          </div>
        </section>

        <section className="card min-w-0 p-3">
          <div className="mb-2 flex flex-wrap items-center gap-1.5">
            {text.filters.map((s) => {
              const n = s.id === 'all' ? inDir.length : s.id === 'problems' ? problems.reduce((t, st) => t + dirCounts[st], 0) : dirCounts[s.id]
              if (n === 0 && s.id !== show && s.id !== 'all' && s.id !== 'problems') return null
              return (
                <button
                  key={s.id}
                  title={s.title}
                  onClick={() => setShow(s.id === defaultShow ? '' : s.id, { replace: true })}
                  className={clsx('rounded px-2 py-0.5 text-xs', show === s.id ? 'bg-accent text-accent-contrast' : 'bg-surface-2 text-muted hover:text-fg')}
                >
                  {s.label} <span className="tabular opacity-80">{count(n)}</span>
                </button>
              )
            })}
            <span className="flex-1" />
            <FilterInput className="input max-w-56 py-1 text-xs" placeholder="Path contains…" value={q} onChange={(v) => setQ(v, { replace: true })} />
          </div>
          {dir && (
            <div className="mb-2 flex items-center gap-2 text-xs muted">
              <span className="truncate font-mono">📁 {dir}/</span>
              <button className="link" onClick={() => setDir('')}>
                everything
              </button>
            </div>
          )}
          {!shown.length ? (
            <p className="py-8 text-center text-sm muted">{show === 'problems' ? text.noProblems.replace('{where}', dir ? ' in this folder' : '') : 'No files for this filter.'}</p>
          ) : (
            <div className="max-h-[70vh] overflow-auto">
              <table className="w-full text-sm">
                <thead className="sticky top-0 bg-surface text-left text-xs text-muted">
                  <tr>
                    <th className="py-1 pr-2 font-medium">File</th>
                    <th className="py-1 pr-2 text-right font-medium">{text.a}</th>
                    <th className="py-1 pr-2 text-right font-medium">{text.b}</th>
                    <th className="py-1 font-medium">Status</th>
                  </tr>
                </thead>
                <tbody>
                  {shown.slice(0, rows).map((f) => (
                    <FileLine key={`${f.state}:${f.path}`} f={f} dir={dir} label={text.label(f)} onOpen={preview.open} />
                  ))}
                </tbody>
              </table>
              {shown.length > rows && (
                <div className="py-2 text-center">
                  <button className="btn-secondary px-2.5 py-1 text-xs" onClick={() => setRows(rows + ROWS * 4)}>
                    Show more ({count(shown.length - rows)} left)
                  </button>
                </div>
              )}
            </div>
          )}
          {text.footer(show, shown.length)}
        </section>
      </div>
      <EntryDrawer preview={preview} list={shown.flatMap((f) => [f.sourceId, f.entryId].filter((id): id is number => id != null))} />
    </>
  )
}

function FolderNode({
  node,
  depth,
  selected,
  onSelect,
  differencesOnly,
  text,
}: {
  node: Folder
  depth: number
  selected: string
  onSelect: (path: string) => void
  differencesOnly: boolean
  text: CompareText
}) {
  const inside = selected === node.path || selected.startsWith(node.path + '/')
  const [open, setOpen] = useState(depth < 1 || inside)
  useEffect(() => {
    if (inside) setOpen(true)
  }, [inside])
  const c = node.counts
  const wrong = bad(c)
  const children = [...node.children.values()]
    .filter((n) => !differencesOnly || bad(n.counts) > 0 || n.counts.extra > 0)
    .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))
  const complete = wrong === 0 && node.matchedBytes === node.bytes
  const title = (s: CompareState) => text.filters.find((f) => f.id === s)?.label ?? s
  const wrongTitle = BAD.filter((s) => c[s])
    .map((s) => `${count(c[s])} ${title(s).toLowerCase()}`)
    .join(', ')
  return (
    <div>
      <div
        className={clsx('flex items-center gap-1.5 rounded px-1 py-0.5', selected === node.path ? 'bg-accent-faint' : 'hover:bg-accent-faint/60')}
        style={{ paddingLeft: `${depth * 0.9 + 0.25}rem` }}
      >
        <button className="w-4 shrink-0 text-xs muted" onClick={() => setOpen(!open)} disabled={!children.length} aria-label={open ? 'Collapse' : 'Expand'}>
          {children.length ? (open ? '▾' : '▸') : ''}
        </button>
        <button className="min-w-0 flex-1 text-left" onClick={() => onSelect(node.path)} title={node.path || text.rootLabel}>
          <div className="flex items-center gap-1.5">
            <span className="min-w-0 flex-1 truncate">{depth === 0 ? `${text.rootIcon} ${text.rootLabel}` : `📁 ${node.name}`}</span>
            {wrong > 0 && (
              <span className={c.smaller || c.differs || c.corrupt ? badPill : 'pill-amber'} title={wrongTitle}>
                {count(wrong)}
              </span>
            )}
            {c.extra > 0 && (
              <span className="chip" title={title('extra')}>
                +{count(c.extra)}
              </span>
            )}
            {complete && <span className="text-xs text-success">✓</span>}
          </div>
          {node.bytes > 0 && <ShareBar value={node.matchedBytes} max={node.bytes} color={complete ? 'var(--success)' : 'var(--amber)'} className="mt-0.5 h-1" />}
        </button>
      </div>
      {open && children.map((n) => <FolderNode key={n.path} node={n} depth={depth + 1} selected={selected} onSelect={onSelect} differencesOnly={differencesOnly} text={text} />)}
    </div>
  )
}

function statePill(f: CompareRow, label: string) {
  const cls = f.state === 'matched' ? (f.crc === 'match' ? okPill : 'pill-muted') : f.state === 'missing' ? 'pill-amber' : f.state === 'extra' ? 'chip' : badPill
  return <span className={clsx(cls, 'whitespace-nowrap')}>{label}</span>
}

function FileLine({ f, dir, label, onOpen }: { f: CompareRow; dir: string; label: string; onOpen: (entryId: number) => void }) {
  const rel = dir ? f.path.slice(dir.length + 1) : f.path
  const folder = parentOf(rel)
  const leaf = rel.slice(folder ? folder.length + 1 : 0)
  const inSource = f.state !== 'extra'
  return (
    <tr className="border-t border-line/50 align-top hover:bg-accent-faint/50">
      <td className="max-w-0 py-1 pr-2">
        <div className="truncate font-mono text-xs" title={f.target && f.target !== f.path ? `${f.path}\non disk: ${f.target}` : f.path}>
          {folder && <span className="muted">{folder}/</span>}
          {leaf}
        </div>
        {f.elsewhere && (
          <div className="truncate text-xs muted" title={f.elsewhere}>
            same content at <span className="font-mono">{f.elsewhere}</span>
          </div>
        )}
      </td>
      <td className="py-1 pr-2 text-right text-xs whitespace-nowrap tabular">
        {!inSource ? (
          <span className="muted">—</span>
        ) : f.sourceId != null ? (
          <button className="hover:text-accent" onClick={() => onOpen(f.sourceId!)} title="Open in the file panel">
            {bytes(f.size)}
            {f.sourceMTime ? <span className="ml-1.5 muted">{date(f.sourceMTime)}</span> : null}
          </button>
        ) : (
          bytes(f.size)
        )}
        {inSource && !f.checkable && (
          <span className="ml-1 muted" title="No checksum known for this file">
            ·
          </span>
        )}
      </td>
      <td className="py-1 pr-2 text-right text-xs whitespace-nowrap tabular">
        {f.entryId != null ? (
          <button className="hover:text-accent" onClick={() => onOpen(f.entryId!)} title={`Open ${f.target ?? ''} in the file panel`}>
            <span className={clsx(f.diskSize != null && inSource && f.diskSize !== f.size && 'text-danger')}>{bytes(f.diskSize)}</span>
            {f.diskMTime ? <span className="ml-1.5 muted">{date(f.diskMTime)}</span> : null}
          </button>
        ) : (
          <span className="muted">not there</span>
        )}
      </td>
      <td className="py-1">{statePill(f, label)}</td>
    </tr>
  )
}
