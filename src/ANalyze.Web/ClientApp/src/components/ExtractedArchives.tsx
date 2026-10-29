import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { Empty, ErrorBox, Loading, Stat } from './ui'
import { useScope } from '../hooks/useScope'
import { bytes, count, date } from '../lib/format'

interface Place {
  id?: number
  rootId: number
  root?: string
  relPath: string
}

interface Extraction {
  id: number
  state: 'complete' | 'partial'
  archive: Place
  archiveSize: number
  archiveMTime: number
  format?: string
  folder: Place
  baseRelPath: string
  strip: string
  files: number
  matched: number
  missing: number
  smaller: number
  differs: number
  bytes: number
  matchedBytes: number
  verifiedAt?: number
  verifiedOk: number
  verifiedBad: number
  unverified: number
}

interface ListResponse {
  items: Extraction[]
  total: number
  counts: { complete: number; verified: number; partial: number; bad: number; completeArchiveBytes: number }
  listed: { archives: number; unreadable: number }
}

interface FileRow {
  path: string
  size: number
  state: 'matched' | 'missing' | 'smaller' | 'differs' | 'corrupt'
  target?: string
  entryId?: number
  diskSize?: number
  checkable: boolean
}

const PAGE = 50
const okPill = 'inline-flex items-center rounded px-2 py-0.5 text-xs text-success bg-success/10'
const badPill = 'inline-flex items-center rounded px-2 py-0.5 text-xs text-danger bg-danger/10'

const STATE_LABEL: Record<FileRow['state'], string> = {
  matched: 'extracted',
  missing: 'missing',
  smaller: 'smaller (cut short)',
  differs: 'different size',
  corrupt: 'content differs from the archive',
}

function place(p: Place) {
  return `${p.root ?? '?'}/${p.relPath}`
}

function verified(x: Extraction) {
  return x.verifiedAt != null && x.verifiedBad === 0 && x.unverified === 0
}

/** Archives whose files were found extracted somewhere on the roots: complete or partial, checked against the archive's CRC32s. */
export function ExtractedArchives() {
  const { rootId } = useScope()
  const qc = useQueryClient()
  const [state, setState] = useState('')
  const [q, setQ] = useState('')
  const [sort, setSort] = useState('size')
  const [offset, setOffset] = useState(0)
  const [open, setOpen] = useState<number | null>(null)
  const list = useQuery({
    queryKey: ['extractions', rootId, state, q, sort, offset],
    queryFn: () => api<ListResponse>('/archives/extractions', { query: { rootId, state: state || undefined, q: q || undefined, sort, offset, limit: PAGE } }),
  })
  const verify = useMutation({
    mutationFn: (body: { ids?: number[]; state?: string; rootId?: number }) => api<{ queued: number }>('/archives/extractions/verify', { method: 'POST', json: body }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['jobs'] }),
  })
  const d = list.data

  return (
    <>
      <div className="mb-4 grid grid-cols-2 gap-3 md:grid-cols-4">
        <Stat label="Completely extracted" value={count(d?.counts.complete)} hint={`${bytes(d?.counts.completeArchiveBytes)} of archives`} tone="amber" />
        <Stat label="Checked against the archive" value={count(d?.counts.verified)} hint="every file matches its CRC32" />
        <Stat label="Partly extracted" value={count(d?.counts.partial)} hint="files missing or cut short" />
        <Stat label="Copies that differ" value={count(d?.counts.bad)} hint={d?.listed.unreadable ? `${count(d.listed.unreadable)} archives could not be read` : 'content differs from the archive'} />
      </div>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <input
          className="input max-w-xs"
          placeholder="Archive or folder contains…"
          value={q}
          onChange={(e) => {
            setQ(e.target.value)
            setOffset(0)
          }}
        />
        <select
          className="input w-auto"
          value={state}
          onChange={(e) => {
            setState(e.target.value)
            setOffset(0)
          }}
        >
          <option value="">All</option>
          <option value="complete">Completely extracted</option>
          <option value="unverified">Complete, not checked yet</option>
          <option value="verified">Complete and checked</option>
          <option value="bad">Copy differs from the archive</option>
          <option value="partial">Partly extracted</option>
        </select>
        <select className="input w-auto" value={sort} onChange={(e) => setSort(e.target.value)}>
          <option value="size">Largest archives</option>
          <option value="matched">Most data extracted</option>
          <option value="name">Name</option>
        </select>
        <span className="flex-1" />
        <button
          className="btn-secondary"
          disabled={verify.isPending || !d?.counts.complete}
          onClick={() => verify.mutate({ state: 'unverified', rootId })}
          title="Reads the extracted files of every complete extraction not checked yet and compares them with the archives' CRC32s"
        >
          Check all unchecked
        </button>
      </div>
      {verify.data && <p className="mb-3 text-sm muted">{verify.data.queued ? `Checking ${count(verify.data.queued)} extractions — progress is shown in the sidebar.` : 'Nothing to check.'}</p>}
      {verify.error && <ErrorBox error={verify.error} />}

      {list.isLoading ? (
        <Loading />
      ) : list.error ? (
        <ErrorBox error={list.error} />
      ) : !d?.items.length ? (
        <Empty>
          No extracted archives found{q || state ? ' for this filter' : ''}. Archives are listed during scans (Settings → Archives){d?.listed.archives ? `; ${count(d.listed.archives)} are listed so far` : ''}.
        </Empty>
      ) : (
        <div className="space-y-3">
          {d.items.map((x) => (
            <ExtractionCard key={x.id} x={x} open={open === x.id} onToggle={() => setOpen(open === x.id ? null : x.id)} onVerify={() => verify.mutate({ ids: [x.id] })} />
          ))}
          <div className="flex items-center justify-center gap-3">
            <button className="btn-secondary" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>
              ← Previous
            </button>
            <span className="text-sm muted">
              {offset + 1}–{Math.min(offset + PAGE, d.total)} of {count(d.total)}
            </span>
            <button className="btn-secondary" disabled={offset + PAGE >= d.total} onClick={() => setOffset(offset + PAGE)}>
              Next →
            </button>
          </div>
        </div>
      )}
    </>
  )
}

function ExtractionCard({ x, open, onToggle, onVerify }: { x: Extraction; open: boolean; onToggle: () => void; onVerify: () => void }) {
  const navigate = useNavigate()
  const plan = useMutation({
    mutationFn: (remove: 'archive' | 'extracted') => api<{ id: number }>('/archives/extractions/plan', { method: 'POST', json: { ids: [x.id], remove, op: 'trash' } }),
    onSuccess: (p) => navigate(`/actions/${p.id}`),
  })
  const removeArchive = () => {
    if (!verified(x) && !confirm('The extracted files have not been checked against the archive yet (or some differ). Create the plan anyway?')) return
    plan.mutate('archive')
  }
  const complete = x.state === 'complete'
  return (
    <div className="card p-3">
      <div className="flex flex-wrap items-start gap-3 text-sm">
        <div className="min-w-0 flex-1 space-y-0.5">
          <div className="truncate" title={place(x.archive)}>
            🗜 <span className="muted">{x.archive.root}/</span>
            {x.archive.relPath}
            <span className="ml-2 text-xs muted">
              {bytes(x.archiveSize)} · {x.format}
            </span>
          </div>
          <div className="truncate" title={place(x.folder)}>
            {'→ '}
            {x.folder.id ? (
              <Link to={`/browse/${x.folder.id}`} className="hover:text-accent">
                📁 <span className="muted">{x.folder.root}/</span>
                {x.folder.relPath || '/'}
              </Link>
            ) : (
              <span>📁 {place(x.folder)}</span>
            )}
            {x.strip && <span className="ml-2 text-xs muted" title="The archive's top folder was not kept when it was extracted">without “{x.strip.replace(/\/$/, '')}”</span>}
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          {complete ? <span className="pill-amber">complete</span> : <span className="pill-muted">partial · {Math.round((100 * x.matchedBytes) / Math.max(1, x.bytes))}%</span>}
          <span className="pill-muted">
            {count(x.matched)} / {count(x.files)} files
          </span>
          {x.missing > 0 && <span className="pill-muted">{count(x.missing)} missing</span>}
          {x.smaller > 0 && <span className={badPill}>{count(x.smaller)} cut short</span>}
          {x.differs > 0 && <span className={badPill}>{count(x.differs)} other size</span>}
          {x.verifiedAt == null ? (
            <span className="pill-muted">not checked</span>
          ) : x.verifiedBad > 0 ? (
            <span className={badPill} title={`Checked ${date(x.verifiedAt)}`}>
              {count(x.verifiedBad)} differ from the archive
            </span>
          ) : (
            <span className={x.unverified ? 'pill-muted' : okPill} title={`Checked ${date(x.verifiedAt)}`}>
              ✓ {count(x.verifiedOk)} match{x.unverified ? ` · ${count(x.unverified)} not checkable` : ''}
            </span>
          )}
        </div>
      </div>
      <div className="mt-2 flex flex-wrap gap-2">
        <button className="btn-secondary px-2.5 py-1 text-xs" onClick={onToggle}>
          {open ? 'Hide files' : complete ? 'Files' : 'What is missing'}
        </button>
        <button className="btn-secondary px-2.5 py-1 text-xs" onClick={onVerify} title="Read the extracted files and compare them with the CRC32s stored in the archive">
          Check against archive
        </button>
        {complete && (
          <button className="btn-secondary px-2.5 py-1 text-xs" disabled={plan.isPending} onClick={removeArchive}>
            Remove archive…
          </button>
        )}
        <button className="btn-secondary px-2.5 py-1 text-xs" disabled={plan.isPending} onClick={() => plan.mutate('extracted')} title="Only the files that came from the archive; other files in the folder stay">
          Remove extracted copy…
        </button>
      </div>
      {plan.error && <ErrorBox error={plan.error} />}
      {open && <ExtractionFiles id={x.id} complete={complete} />}
    </div>
  )
}

function ExtractionFiles({ id, complete }: { id: number; complete: boolean }) {
  const [all, setAll] = useState(complete)
  const files = useQuery({
    queryKey: ['extraction', id, all],
    queryFn: () => api<{ files: FileRow[]; total: number }>(`/archives/extractions/${id}`, { query: { problems: all ? undefined : true, limit: 2000 } }),
  })
  if (files.isLoading) return <Loading />
  if (files.error) return <ErrorBox error={files.error} />
  const d = files.data
  return (
    <div className="mt-3 border-t border-line/60 pt-2">
      <label className="mb-2 flex items-center gap-1.5 text-xs muted">
        <input type="checkbox" className="accent-accent" checked={!all} onChange={(e) => setAll(!e.target.checked)} />
        Only files with a problem
      </label>
      {!d?.files.length ? (
        <p className="text-sm muted">No problems: every file is extracted with the same size.</p>
      ) : (
        <ul className="max-h-96 space-y-0.5 overflow-auto text-sm">
          {d.files.map((f) => (
            <li key={f.path} className="flex items-center gap-2 rounded px-1 py-0.5 hover:bg-accent-faint">
              <span className="min-w-0 flex-1 truncate font-mono text-xs" title={f.target ?? f.path}>
                {f.path}
              </span>
              <span className="shrink-0 text-xs muted tabular">
                {f.diskSize != null && f.diskSize !== f.size ? `${bytes(f.diskSize)} of ${bytes(f.size)}` : bytes(f.size)}
              </span>
              <span className={f.state === 'matched' ? 'pill-muted' : f.state === 'missing' ? 'pill-amber' : badPill}>{STATE_LABEL[f.state]}</span>
            </li>
          ))}
          {d.total > d.files.length && <li className="text-xs muted">… and {count(d.total - d.files.length)} more</li>}
        </ul>
      )}
    </div>
  )
}
