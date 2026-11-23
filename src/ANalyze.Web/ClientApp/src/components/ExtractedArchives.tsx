import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { Empty, ErrorBox, FilterInput, Loading, Stat } from './ui'
import { usePlanAdded, usePlanTarget } from './PlanDialog'
import { useScope } from '../hooks/useScope'
import { leaveOverlay, useUrlNumber, useUrlParam } from '../hooks/useUrlState'
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
  /** Every extraction into this folder: one per identical copy of the archive. */
  ids: number[]
}

interface ArchiveGroup {
  archive: Place
  archiveSize: number
  archiveMTime: number
  format?: string
  /** The archive's content hash, the same for every copy; absent when not calculated yet (then copies of it are not known). */
  hash?: string
  /** The archive and its identical copies (same content hash). */
  copies: Place[]
  /** Every folder the archive was found extracted to, complete ones first. */
  extractions: Extraction[]
}

interface ListResponse {
  items: ArchiveGroup[]
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
  /** Extracted files: the archive's CRC32 checked and equal, checked and different, not checked (or not checkable), or not stored in the archive. */
  crc?: 'match' | 'differs' | 'unchecked' | 'none'
}

const PAGE = 50
const okPill = 'inline-flex items-center rounded px-2 py-0.5 text-xs text-success bg-success/10'
const badPill = 'inline-flex items-center rounded px-2 py-0.5 text-xs text-danger bg-danger/10'

const STATE_LABEL: Record<FileRow['state'], string> = {
  matched: 'extracted',
  missing: 'missing',
  smaller: 'smaller (cut short)',
  differs: 'different size',
  corrupt: 'same size, CRC32 differs from the archive',
}

/** An extracted file's label: whether only its size matched, or its CRC32 was compared with the archive's too. */
function fileLabel(f: FileRow) {
  if (f.state !== 'matched') return STATE_LABEL[f.state]
  switch (f.crc) {
    case 'match':
      return 'same size, same CRC32'
    case 'none':
      return 'same size · no CRC32 in the archive'
    default:
      return 'same size · CRC32 not checked'
  }
}

/** Says what "identical" rests on: the shared content hash, or that it has not been calculated. */
function hashNote(hash: string | undefined, copies: number) {
  if (!hash) return ' · content hash not calculated, so identical copies are not known'
  return copies > 1 ? ` · ${count(copies)} identical copies (same content hash ${hash.slice(0, 12)}…)` : ''
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
  // In the URL (prefixed, as this is a tab of Duplicates), so Back steps through filters, pages and opened file lists.
  const [state, setState] = useUrlParam('astate')
  const [q, setQ] = useUrlParam('aq')
  const [sort, setSort] = useUrlParam('asort', 'size')
  const [offset, setOffset] = useUrlNumber('aoffset')
  const [openParam, setOpen] = useUrlNumber('archive', -1)
  const open = openParam < 0 ? null : openParam
  const list = useQuery({
    queryKey: ['extractions', rootId, state, q, sort, offset],
    queryFn: () => api<ListResponse>('/archives/extractions', { query: { rootId, state: state || undefined, q: q || undefined, sort, offset, limit: PAGE } }),
    placeholderData: keepPreviousData,
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
        <FilterInput
          className="input max-w-xs"
          placeholder="Archive or folder contains…"
          value={q}
          onChange={(v) => setQ(v, { replace: true, also: { aoffset: null } })}
        />
        <select
          className="input w-auto"
          value={state}
          onChange={(e) => setState(e.target.value, { also: { aoffset: null } })}
        >
          <option value="">All</option>
          <option value="complete">Completely extracted</option>
          <option value="unverified">Complete, not checked yet</option>
          <option value="verified">Complete and checked</option>
          <option value="bad">Copy differs from the archive</option>
          <option value="partial">Partly extracted</option>
        </select>
        <select className="input w-auto" value={sort} onChange={(e) => setSort(e.target.value, { also: { aoffset: null } })}>
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
      {verify.data && <p className="mb-3 text-sm muted">{verify.data.queued ? `Checking ${count(verify.data.queued)} extracted copies — progress is shown in the sidebar.` : 'Nothing to check.'}</p>}
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
          {d.items.map((g) => (
            <ArchiveCard key={g.archive.id} g={g} open={open} onToggle={(id) => setOpen(open === id ? -1 : id)} onVerify={(ids) => verify.mutate({ ids })} />
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

/** One archive with every folder it was found extracted to. */
function ArchiveCard({ g, open, onToggle, onVerify }: { g: ArchiveGroup; open: number | null; onToggle: (id: number) => void; onVerify: (ids: number[]) => void }) {
  const navigate = useNavigate()
  const added = usePlanAdded()
  // Settings → "Plan buttons add to": a new plan, or the draft changed last (files it has get the new action).
  const { fallback } = usePlanTarget()
  const into = fallback ? `Adds to the draft plan “${fallback.name}”` : 'Creates a new draft plan'
  const plan = useMutation({
    mutationFn: ({ ids, remove }: { ids: number[]; remove: 'archive' | 'extracted' }) =>
      api<{ id: number }>('/archives/extractions/plan', { method: 'POST', json: { ids, remove, op: 'trash', planId: fallback?.id } }),
    onSuccess: (p) => {
      added(p)
      leaveOverlay(navigate, `/actions/${p.id}`)
    },
  })
  const complete = g.extractions.filter((x) => x.state === 'complete')
  const removeArchive = () => {
    if (!complete.some(verified) && !confirm('The extracted files have not been checked against the archive yet (or some differ). Create the plan anyway?')) return
    plan.mutate({ ids: complete.flatMap((x) => x.ids), remove: 'archive' })
  }
  const many = g.extractions.length > 1
  const copies = g.copies.length
  return (
    <div className="card p-3">
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <div className="min-w-0 flex-1 space-y-0.5">
          {g.copies.map((c) => (
            <div key={c.id} className="truncate" title={place(c)}>
              🗜 <span className="muted">{c.root}/</span>
              {c.relPath}
              {c.id === g.archive.id && (
                <span className="ml-2 text-xs muted">
                  {bytes(g.archiveSize)} · {g.format}
                  {hashNote(g.hash, copies)}
                </span>
              )}
            </div>
          ))}
        </div>
        {many && <span className="pill-muted">extracted to {count(g.extractions.length)} folders</span>}
        {many && (
          <button
            className="btn-secondary px-2.5 py-1 text-xs"
            onClick={() => onVerify(g.extractions.flatMap((x) => x.ids))}
            title="Read the extracted files in every folder and compare them with the CRC32s stored in the archive"
          >
            Check all
          </button>
        )}
        {complete.length > 0 && (
          <button className="btn-secondary px-2.5 py-1 text-xs" disabled={plan.isPending} onClick={removeArchive} title={`${copies > 1 ? 'Every copy of the archive. ' : ''}${into}.`}>
            {copies > 1 ? `Remove all ${count(copies)} archives…` : 'Remove archive…'}
          </button>
        )}
      </div>
      <div className="mt-2 divide-y divide-line/60">
        {g.extractions.map((x) => (
          <ExtractionRow
            key={x.id}
            x={x}
            many={many}
            open={open === x.id}
            busy={plan.isPending}
            onToggle={() => onToggle(x.id)}
            onVerify={() => onVerify(x.ids)}
            into={into}
            onRemoveCopy={() => plan.mutate({ ids: [x.id], remove: 'extracted' })}
          />
        ))}
      </div>
      {plan.error && <ErrorBox error={plan.error} />}
    </div>
  )
}

/** One folder the archive was found extracted to. */
function ExtractionRow({
  x,
  many,
  open,
  busy,
  into,
  onToggle,
  onVerify,
  onRemoveCopy,
}: {
  x: Extraction
  many: boolean
  open: boolean
  busy: boolean
  into: string
  onToggle: () => void
  onVerify: () => void
  onRemoveCopy: () => void
}) {
  const complete = x.state === 'complete'
  return (
    <div className="py-2 first:pt-0 last:pb-0">
      <div className="flex flex-wrap items-start gap-3 text-sm">
        <div className="min-w-0 flex-1 truncate" title={place(x.folder)}>
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
        <div className="flex flex-wrap items-center gap-1.5">
          {complete ? <span className="pill-amber">complete</span> : <span className="pill-muted">partial · {Math.round((100 * x.matchedBytes) / Math.max(1, x.bytes))}%</span>}
          <span className="pill-muted">
            {count(x.matched)} / {count(x.files)} files
          </span>
          {x.missing > 0 && <span className="pill-muted">{count(x.missing)} missing</span>}
          {x.smaller > 0 && <span className={badPill}>{count(x.smaller)} cut short</span>}
          {x.differs > 0 && <span className={badPill}>{count(x.differs)} other size</span>}
          {x.verifiedAt == null ? (
            <span className="pill-muted" title="Only names and sizes were compared so far">CRC32 not checked</span>
          ) : x.verifiedBad > 0 ? (
            <span className={badPill} title={`Checked ${date(x.verifiedAt)}`}>
              {count(x.verifiedBad)} with another CRC32 than the archive
            </span>
          ) : (
            <span className={x.unverified ? 'pill-muted' : okPill} title={`Checked ${date(x.verifiedAt)}`}>
              ✓ {count(x.verifiedOk)} same CRC32{x.unverified ? ` · ${count(x.unverified)} not checkable` : ''}
            </span>
          )}
        </div>
      </div>
      <div className="mt-2 flex flex-wrap gap-2">
        <button className="btn-secondary px-2.5 py-1 text-xs" onClick={onToggle}>
          {open ? 'Hide files' : complete ? 'Files' : 'What is missing'}
        </button>
        <button className="btn-secondary px-2.5 py-1 text-xs" onClick={onVerify} title="Read the extracted files and compare them with the CRC32s stored in the archive">
          {many ? 'Check this copy' : 'Check against archive'}
        </button>
        <button className="btn-secondary px-2.5 py-1 text-xs" disabled={busy} onClick={onRemoveCopy} title={`Only the files that came from the archive; other files in the folder stay. ${into}.`}>
          {many ? 'Remove this copy…' : 'Remove extracted copy…'}
        </button>
      </div>
      {open && <ExtractionFiles id={x.id} complete={complete} checked={x.verifiedAt != null && x.verifiedBad === 0 && x.unverified === 0} />}
    </div>
  )
}

function ExtractionFiles({ id, complete, checked }: { id: number; complete: boolean; checked: boolean }) {
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
        <p className="text-sm muted">
          No problems: every file is extracted with the same size
          {checked ? ' and the same CRC32 as in the archive.' : '. The CRC32s have not all been compared with the archive yet: use “Check against archive” to be sure the content is the same.'}
        </p>
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
              <span className={f.state === 'matched' ? (f.crc === 'match' ? okPill : 'pill-muted') : f.state === 'missing' ? 'pill-amber' : badPill}>{fileLabel(f)}</span>
            </li>
          ))}
          {d.total > d.files.length && <li className="text-xs muted">… and {count(d.total - d.files.length)} more</li>}
        </ul>
      )}
    </div>
  )
}
