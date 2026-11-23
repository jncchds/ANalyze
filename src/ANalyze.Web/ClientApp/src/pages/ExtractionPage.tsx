import { useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../api/client'
import { EntryDrawer } from '../components/EntryDrawer'
import { badPill, fileLabel, okPill, place, useExtractionPlan, verified, type Extraction, type FileRow } from '../components/ExtractedArchives'
import { Empty, ErrorBox, FilterInput, Loading, PageTitle, ShareBar, Stat } from '../components/ui'
import { useEntryPreview, useUrlParam } from '../hooks/useUrlState'
import { bytes, count, date } from '../lib/format'

interface CompareResponse {
  extraction: Extraction
  files: FileRow[]
  total: number
  /** More files on disk than listed were found that the archive does not have. */
  extrasCapped: boolean
}

type State = FileRow['state']
type Show = 'problems' | 'all' | State

const PROBLEMS: State[] = ['missing', 'smaller', 'differs', 'corrupt']
const SHOW: { id: Show; label: string; title: string }[] = [
  { id: 'problems', label: 'Not extracted right', title: 'Missing, cut short, another size or another CRC32 than in the archive' },
  { id: 'missing', label: 'Missing', title: 'In the archive, not on disk' },
  { id: 'smaller', label: 'Cut short', title: 'Smaller on disk than in the archive: an interrupted extraction' },
  { id: 'differs', label: 'Other size', title: 'Larger on disk than in the archive, or a folder where the file should be' },
  { id: 'corrupt', label: 'CRC32 differs', title: 'Same size, but the check found other content than the archive stores' },
  { id: 'matched', label: 'Extracted', title: 'Same path and size as in the archive' },
  { id: 'extra', label: 'Only on disk', title: 'Files in the archive’s folders on disk that the archive does not have' },
  { id: 'all', label: 'All', title: 'Every file of the archive and every file only on disk' },
]

/** Counts per state below one folder of the archive (every level), and the bytes of the archive's files there. */
interface Folder {
  name: string
  path: string
  children: Map<string, Folder>
  counts: Record<State, number>
  bytes: number
  matchedBytes: number
}

const emptyCounts = (): Record<State, number> => ({ matched: 0, missing: 0, smaller: 0, differs: 0, corrupt: 0, extra: 0 })
const problems = (c: Record<State, number>) => PROBLEMS.reduce((n, s) => n + c[s], 0)
const parentOf = (path: string) => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')

function buildTree(files: FileRow[]) {
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

function matches(f: FileRow, show: Show) {
  if (show === 'all') return true
  if (show === 'problems') return PROBLEMS.includes(f.state)
  return f.state === show
}

function statePill(f: FileRow) {
  const cls = f.state === 'matched' ? (f.crc === 'match' ? okPill : 'pill-muted') : f.state === 'missing' ? 'pill-amber' : f.state === 'extra' ? 'chip' : badPill
  return <span className={clsx(cls, 'whitespace-nowrap')}>{fileLabel(f)}</span>
}

const ROWS = 500

/** One extraction of an archive, compared with the archive folder by folder: what is there, missing, cut short, different or only on disk. */
export default function ExtractionPage() {
  const id = Number(useParams().id)
  const qc = useQueryClient()
  const preview = useEntryPreview()
  const [dir, setDir] = useUrlParam('dir')
  const [showParam, setShow] = useUrlParam('show')
  const [q, setQ] = useUrlParam('q')
  const [problemFolders, setProblemFolders] = useState(false)
  const [rows, setRows] = useState(ROWS)

  const data = useQuery({
    queryKey: ['extraction', id, 'compare'],
    queryFn: () => api<CompareResponse>(`/archives/extractions/${id}`, { query: { extras: true, limit: 100000 } }),
    enabled: Number.isInteger(id),
  })
  const verify = useMutation({
    mutationFn: (ids: number[]) => api<{ queued: number }>('/archives/extractions/verify', { method: 'POST', json: { ids } }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['jobs'] }),
  })
  const { plan, into } = useExtractionPlan()

  const x = data.data?.extraction
  const files = useMemo(() => data.data?.files ?? [], [data.data])
  const tree = useMemo(() => buildTree(files), [files])
  // A partial extraction opens on what went wrong; a complete one on every file.
  const show = (showParam || (x && x.state === 'partial' ? 'problems' : 'all')) as Show
  const inDir = useMemo(() => (dir ? files.filter((f) => f.path.startsWith(dir + '/')) : files), [files, dir])
  const dirCounts = useMemo(() => {
    const c = emptyCounts()
    for (const f of inDir) c[f.state]++
    return c
  }, [inDir])
  const shown = useMemo(() => {
    const needle = q.toLowerCase()
    return inDir
      .filter((f) => matches(f, show) && (!needle || f.path.toLowerCase().includes(needle) || f.target?.toLowerCase().includes(needle)))
      .sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0))
  }, [inDir, show, q])
  useEffect(() => setRows(ROWS), [dir, show, q])

  if (data.isLoading) return <Loading />
  if (data.error) return <ErrorBox error={data.error} />
  if (!x) return <Empty>This extraction is no longer known: the next scan may have found it elsewhere.</Empty>

  const name = x.archive.relPath.split('/').pop()
  const missingBytes = files.filter((f) => f.state === 'missing').reduce((n, f) => n + f.size, 0)
  const short = files.filter((f) => f.state === 'smaller' || f.state === 'differs')
  const extras = files.filter((f) => f.state === 'extra')
  const counts = tree.counts
  const removeArchive = () => {
    if (!verified(x) && !confirm('The extracted files have not been checked against the archive yet (or some differ). Create the plan anyway?')) return
    plan.mutate({ ids: [x.id], remove: 'archive' })
  }

  return (
    <>
      <div className="mb-2 text-sm">
        <Link to="/duplicates?tab=archives" className="muted hover:text-accent">
          ← Extracted archives
        </Link>
      </div>
      <PageTitle
        subtitle={
          <>
            🗜 {place(x.archive)} · {bytes(x.archiveSize)}
            {x.format ? ` · ${x.format}` : ''} → 📁 {place(x.folder)}
            {x.strip && <> · extracted without its top folder “{x.strip.replace(/\/$/, '')}”</>}
          </>
        }
        actions={
          <>
            {x.archive.id != null && (
              <button className="btn-secondary" onClick={() => preview.open(x.archive.id!)}>
                Open archive
              </button>
            )}
            {x.folder.id != null && (
              <Link to={`/browse/${x.folder.id}`} className="btn-secondary">
                Open folder
              </Link>
            )}
            <button
              className="btn-secondary"
              disabled={verify.isPending}
              onClick={() => verify.mutate([x.id])}
              title="Read the extracted files and compare them with the CRC32s stored in the archive"
            >
              Check against archive
            </button>
            <button
              className="btn-secondary"
              disabled={plan.isPending || !x.matched}
              onClick={() => plan.mutate({ ids: [x.id], remove: 'extracted' })}
              title={`Only the files extracted with the same size; files only on disk, cut short or of another size stay. ${into}.`}
            >
              Remove extracted copy…
            </button>
            {x.state === 'complete' && (
              <button className="btn-primary" disabled={plan.isPending} onClick={removeArchive} title={`Every file is extracted. ${into}.`}>
                Remove archive…
              </button>
            )}
          </>
        }
      >
        {name}{' '}
        {x.state === 'complete' ? <span className="pill-amber align-middle">complete</span> : <span className="pill-muted align-middle">partial</span>}
      </PageTitle>
      {verify.data && <p className="mb-3 text-sm muted">{verify.data.queued ? 'Checking the extracted files — progress is shown in the sidebar; this page updates when it is done.' : 'Nothing to check.'}</p>}
      {verify.error && <ErrorBox error={verify.error} />}
      {plan.error && <ErrorBox error={plan.error} />}

      <div className="mb-4 grid grid-cols-2 gap-3 lg:grid-cols-5">
        <div className="card px-4 py-3">
          <div className="text-xs font-medium tracking-wide text-muted uppercase">Extracted</div>
          <div className="mt-1 text-2xl font-bold tabular">{Math.floor((100 * x.matchedBytes) / Math.max(1, x.bytes))}%</div>
          <ShareBar value={x.matchedBytes} max={x.bytes} color={x.state === 'complete' ? 'var(--success)' : 'var(--amber)'} className="my-1" />
          <div className="text-xs muted">
            {count(x.matched)} of {count(x.files)} files · {bytes(x.matchedBytes)} of {bytes(x.bytes)}
          </div>
        </div>
        <Stat label="Missing" value={count(x.missing)} hint={x.missing ? `${bytes(missingBytes)} not on disk` : 'every file has a copy'} tone={x.missing ? 'amber' : undefined} />
        <Stat
          label="Cut short or other size"
          value={count(short.length)}
          hint={x.smaller ? `${count(x.smaller)} cut short: interrupted extraction` : x.differs ? 'larger on disk, or a folder' : 'sizes agree'}
        />
        <Stat
          label="Content check"
          value={x.verifiedAt == null ? '—' : x.verifiedBad ? count(x.verifiedBad) : '✓'}
          hint={
            x.verifiedAt == null
              ? 'CRC32s not compared yet'
              : x.verifiedBad
                ? `differ from the archive · checked ${date(x.verifiedAt)}`
                : `${count(x.verifiedOk)} same CRC32${x.unverified ? `, ${count(x.unverified)} not checkable` : ''} · ${date(x.verifiedAt)}`
          }
        />
        <Stat
          label="Only on disk"
          value={`${count(extras.length)}${data.data?.extrasCapped ? '+' : ''}`}
          hint={extras.length ? `${bytes(extras.reduce((n, f) => n + f.size, 0))} not in the archive` : 'nothing added in its folders'}
        />
      </div>

      <div className="grid gap-4 lg:grid-cols-[minmax(16rem,22rem)_1fr]">
        <section className="card min-w-0 p-3">
          <div className="mb-2 flex items-center gap-2">
            <h2 className="flex-1 text-sm font-semibold tracking-wide text-muted uppercase">Folders in the archive</h2>
            <label className="flex items-center gap-1.5 text-xs muted" title="Hide folders where every file is extracted and nothing is only on disk">
              <input type="checkbox" className="accent-accent" checked={problemFolders} onChange={(e) => setProblemFolders(e.target.checked)} />
              differences only
            </label>
          </div>
          <div className="max-h-[70vh] overflow-auto text-sm">
            <FolderNode node={tree} depth={0} selected={dir} onSelect={(p) => setDir(p)} onlyProblems={problemFolders} rootLabel={name ?? '/'} />
          </div>
        </section>

        <section className="card min-w-0 p-3">
          <div className="mb-2 flex flex-wrap items-center gap-1.5">
            {SHOW.map((s) => {
              const n = s.id === 'all' ? inDir.length : s.id === 'problems' ? problems(dirCounts) : dirCounts[s.id]
              if (n === 0 && s.id !== show && s.id !== 'all' && s.id !== 'problems') return null
              return (
                <button
                  key={s.id}
                  title={s.title}
                  onClick={() => setShow(s.id === (x.state === 'partial' ? 'problems' : 'all') ? '' : s.id, { replace: true })}
                  className={clsx(
                    'rounded px-2 py-0.5 text-xs',
                    show === s.id ? 'bg-accent text-accent-contrast' : 'bg-surface-2 text-muted hover:text-fg',
                  )}
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
                whole archive
              </button>
            </div>
          )}
          {!shown.length ? (
            <p className="py-8 text-center text-sm muted">
              {show === 'problems'
                ? `Nothing missing, cut short or different${dir ? ' in this folder' : ''}${x.verifiedAt == null ? ' — sizes only: use “Check against archive” to compare the content too' : ''}.`
                : 'No files for this filter.'}
            </p>
          ) : (
            <div className="max-h-[70vh] overflow-auto">
              <table className="w-full text-sm">
                <thead className="sticky top-0 bg-surface text-left text-xs text-muted">
                  <tr>
                    <th className="py-1 pr-2 font-medium">File</th>
                    <th className="py-1 pr-2 text-right font-medium">In the archive</th>
                    <th className="py-1 pr-2 text-right font-medium">On disk</th>
                    <th className="py-1 font-medium">Status</th>
                  </tr>
                </thead>
                <tbody>
                  {shown.slice(0, rows).map((f) => (
                    <FileLine key={`${f.state}:${f.path}`} f={f} dir={dir} onOpen={preview.open} />
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
          {data.data?.extrasCapped && show === 'extra' && <p className="mt-2 text-xs muted">Only the first {count(extras.length)} files found only on disk are listed.</p>}
          <p className="mt-2 text-xs muted">
            Compared by path and size{counts.corrupt || x.verifiedAt != null ? ', and by CRC32 where checked' : ''}. “Only on disk” lists files next to the archive’s files that the archive does not
            have; folders the archive does not have are not searched.
          </p>
        </section>
      </div>
      <EntryDrawer preview={preview} list={shown.filter((f) => f.entryId != null).map((f) => f.entryId!)} />
    </>
  )
}

function FolderNode({
  node,
  depth,
  selected,
  onSelect,
  onlyProblems,
  rootLabel,
}: {
  node: Folder
  depth: number
  selected: string
  onSelect: (path: string) => void
  onlyProblems: boolean
  rootLabel: string
}) {
  const inside = selected === node.path || selected.startsWith(node.path + '/')
  const [open, setOpen] = useState(depth < 1 || inside)
  useEffect(() => {
    if (inside) setOpen(true)
  }, [inside])
  const c = node.counts
  const bad = problems(c)
  const children = [...node.children.values()]
    .filter((n) => !onlyProblems || problems(n.counts) > 0 || n.counts.extra > 0)
    .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))
  const complete = bad === 0 && node.matchedBytes === node.bytes
  return (
    <div>
      <div
        className={clsx('flex items-center gap-1.5 rounded px-1 py-0.5', selected === node.path ? 'bg-accent-faint' : 'hover:bg-accent-faint/60')}
        style={{ paddingLeft: `${depth * 0.9 + 0.25}rem` }}
      >
        <button className="w-4 shrink-0 text-xs muted" onClick={() => setOpen(!open)} disabled={!children.length} aria-label={open ? 'Collapse' : 'Expand'}>
          {children.length ? (open ? '▾' : '▸') : ''}
        </button>
        <button className="min-w-0 flex-1 text-left" onClick={() => onSelect(node.path)} title={node.path || rootLabel}>
          <div className="flex items-center gap-1.5">
            <span className="min-w-0 flex-1 truncate">{depth === 0 ? `🗜 ${rootLabel}` : `📁 ${node.name}`}</span>
            {bad > 0 && <span className={c.smaller || c.differs || c.corrupt ? badPill : 'pill-amber'}>{count(bad)}</span>}
            {c.extra > 0 && <span className="chip" title="Only on disk">+{count(c.extra)}</span>}
            {complete && <span className="text-xs text-success">✓</span>}
          </div>
          {node.bytes > 0 && <ShareBar value={node.matchedBytes} max={node.bytes} color={complete ? 'var(--success)' : 'var(--amber)'} className="mt-0.5 h-1" />}
        </button>
      </div>
      {open && children.map((n) => <FolderNode key={n.path} node={n} depth={depth + 1} selected={selected} onSelect={onSelect} onlyProblems={onlyProblems} rootLabel={rootLabel} />)}
    </div>
  )
}

function FileLine({ f, dir, onOpen }: { f: FileRow; dir: string; onOpen: (entryId: number) => void }) {
  const rel = dir ? f.path.slice(dir.length + 1) : f.path
  const folder = parentOf(rel)
  const leaf = rel.slice(folder ? folder.length + 1 : 0)
  const onDisk = f.entryId != null
  return (
    <tr className="border-t border-line/50 align-top hover:bg-accent-faint/50">
      <td className="max-w-0 py-1 pr-2">
        <div className="truncate font-mono text-xs" title={f.target && f.target !== f.path ? `${f.path}\non disk: ${f.target}` : f.path}>
          {folder && <span className="muted">{folder}/</span>}
          {leaf}
        </div>
      </td>
      <td className="py-1 pr-2 text-right text-xs whitespace-nowrap tabular">
        {f.state === 'extra' ? <span className="muted">—</span> : bytes(f.size)}
        {f.state !== 'extra' && !f.checkable && <span className="ml-1 muted" title="The archive stores no CRC32 for this file">·</span>}
      </td>
      <td className="py-1 pr-2 text-right text-xs whitespace-nowrap tabular">
        {onDisk ? (
          <button className="hover:text-accent" onClick={() => onOpen(f.entryId!)} title={`Open ${f.target ?? ''} in the file panel`}>
            <span className={clsx(f.diskSize != null && f.state !== 'extra' && f.diskSize !== f.size && 'text-danger')}>{bytes(f.diskSize)}</span>
            {f.diskMTime ? <span className="ml-1.5 muted">{date(f.diskMTime)}</span> : null}
          </button>
        ) : (
          <span className="muted">not there</span>
        )}
      </td>
      <td className="py-1">{statePill(f)}</td>
    </tr>
  )
}
