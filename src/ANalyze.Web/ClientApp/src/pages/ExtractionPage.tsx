import { useMemo, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { useConfig } from '../api/hooks'
import { fileLabel, place, useExtractionPlan, verified, type Extraction } from '../components/ExtractedArchives'
import { FolderCompare, type CompareRow, type CompareText } from '../components/FolderCompare'
import { useKeepLocal, usePlanAdded, usePlanChoice } from '../components/PlanDialog'
import { Empty, ErrorBox, Field, Loading, Modal, PageTitle, ShareBar, Stat } from '../components/ui'
import { leaveOverlay, useEntryPreview, useUrlOverlay } from '../hooks/useUrlState'
import { bytes, count, date } from '../lib/format'

interface CompareResponse {
  extraction: Extraction
  files: CompareRow[]
  total: number
  /** More files on disk than listed were found that the archive does not have. */
  extrasCapped: boolean
}

const FILTERS: CompareText['filters'] = [
  { id: 'problems', label: 'Not extracted right', title: 'Missing, cut short, another size or another CRC32 than in the archive' },
  { id: 'missing', label: 'Missing', title: 'In the archive, not on disk' },
  { id: 'smaller', label: 'Cut short', title: 'Smaller on disk than in the archive: an interrupted extraction' },
  { id: 'differs', label: 'Other size', title: 'Larger on disk than in the archive, or a folder where the file should be' },
  { id: 'corrupt', label: 'CRC32 differs', title: 'Same size, but the check found other content than the archive stores' },
  { id: 'matched', label: 'Extracted', title: 'Same path and size as in the archive' },
  { id: 'extra', label: 'Only on disk', title: 'Files in the archive’s folders on disk that the archive does not have' },
  { id: 'all', label: 'All', title: 'Every file of the archive and every file only on disk' },
]

/** One extraction of an archive, compared with the archive folder by folder: what is there, missing, cut short, different or only on disk. */
export default function ExtractionPage() {
  const id = Number(useParams().id)
  const qc = useQueryClient()
  const preview = useEntryPreview()

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
  const repair = useUrlOverlay('repair')

  const x = data.data?.extraction
  const files = useMemo(() => data.data?.files ?? [], [data.data])
  const extrasCapped = !!data.data?.extrasCapped
  const extras = useMemo(() => files.filter((f) => f.state === 'extra'), [files])
  const name = x?.archive.relPath.split('/').pop() ?? '/'
  const checked = x?.verifiedAt != null || files.some((f) => f.state === 'corrupt')
  const text = useMemo<CompareText>(
    () => ({
      tree: 'Folders in the archive',
      rootIcon: '🗜',
      rootLabel: name,
      a: 'In the archive',
      b: 'On disk',
      filters: FILTERS,
      problems: ['missing', 'smaller', 'differs', 'corrupt'],
      label: fileLabel,
      noProblems: `Nothing missing, cut short or different{where}${checked ? '' : ' — sizes only: use “Check against archive” to compare the content too'}.`,
      footer: (show) => (
        <>
          {extrasCapped && show === 'extra' && <p className="mt-2 text-xs muted">Only the first {count(extras.length)} files found only on disk are listed.</p>}
          <p className="mt-2 text-xs muted">
            Compared by path and size{checked ? ', and by CRC32 where checked' : ''}. “Only on disk” lists files next to the archive’s files that the archive does not have;
            folders the archive does not have are not searched.
          </p>
        </>
      ),
    }),
    [name, checked, extrasCapped, extras.length],
  )

  if (data.isLoading) return <Loading />
  if (data.error) return <ErrorBox error={data.error} />
  if (!x) return <Empty>This extraction is no longer known: the next scan may have found it elsewhere.</Empty>

  const missingBytes = files.filter((f) => f.state === 'missing').reduce((n, f) => n + f.size, 0)
  const short = files.filter((f) => f.state === 'smaller' || f.state === 'differs')
  const fixable = repairCounts(files)
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
              className={x.state === 'complete' ? 'btn-secondary' : 'btn-primary'}
              disabled={fixable.damaged + fixable.missing === 0}
              onClick={() => repair.open('1')}
              title={
                fixable.damaged + fixable.missing
                  ? 'Extract the missing and damaged files again from the archive (a draft plan for review)'
                  : checked
                    ? 'Nothing is missing or damaged'
                    : 'Nothing is missing or cut short; “Check against archive” finds files with other content'
              }
            >
              Repair from archive…
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
          value={`${count(extras.length)}${extrasCapped ? '+' : ''}`}
          hint={extras.length ? `${bytes(extras.reduce((n, f) => n + f.size, 0))} not in the archive` : 'nothing added in its folders'}
        />
      </div>

      <FolderCompare files={files} text={text} defaultShow={x.state === 'partial' ? 'problems' : 'all'} />
      {repair.value && <RepairDialog x={x} counts={fixable} checked={checked} onClose={repair.close} />}
    </>
  )
}

/** What a repair would extract again, as `POST /archives/extractions/repair` picks it: damaged copies on disk and missing files. */
function repairCounts(files: CompareRow[]) {
  const c = { damaged: 0, damagedBytes: 0, missing: 0, missingBytes: 0 }
  for (const f of files) {
    if (f.entryId != null && (f.state === 'smaller' || f.state === 'differs' || f.state === 'corrupt')) {
      c.damaged++
      c.damagedBytes += f.size
    } else if (f.state === 'missing') {
      c.missing++
      c.missingBytes += f.size
    }
  }
  return c
}

/**
 * Plans extracting files again from the archive, like replacing damaged files with the backup's copy: damaged copies (cut short,
 * another size, CRC32 differs) go to the trash or are kept (e.g. as `~name`), missing files are created; each file is checked against the
 * archive's size and CRC32 before it takes its place.
 */
function RepairDialog({ x, counts, checked, onClose }: { x: Extraction; counts: ReturnType<typeof repairCounts>; checked: boolean; onClose: () => void }) {
  const navigate = useNavigate()
  const added = usePlanAdded()
  const config = useConfig()
  const { existing, field } = usePlanChoice()
  const keep = useKeepLocal()
  const [damaged, setDamaged] = useState(counts.damaged > 0)
  const [missing, setMissing] = useState(counts.missing > 0)
  const create = useMutation({
    mutationFn: () =>
      api<{ id: number }>('/archives/extractions/repair', {
        method: 'POST',
        json: { ids: [x.id], damaged, missing, keep: damaged ? (keep.keep ?? undefined) : undefined, planId: existing?.id },
      }),
    onSuccess: (p) => {
      added(p)
      leaveOverlay(navigate, `/actions/${p.id}`)
    },
  })
  const tokens = config.data?.pathTokens.map((t) => `{${t}}`).join(' ')
  return (
    <Modal open onClose={onClose} title="Repair from the archive">
      <div className="space-y-4">
        <p className="truncate text-sm muted" title={`${place(x.archive)} → ${place(x.folder)}`}>
          🗜 {place(x.archive)} → 📁 {place(x.folder)}
        </p>
        {field}
        <Field label="Extract again">
          <div className="space-y-2 text-sm">
            <label className="flex items-start gap-2">
              <input type="checkbox" className="mt-1" checked={damaged} disabled={!counts.damaged} onChange={(e) => setDamaged(e.target.checked)} />
              <span>
                <span className="font-semibold">Damaged files</span> — {count(counts.damaged)} · {bytes(counts.damagedBytes)}
                <span className="block text-xs muted">
                  Cut short, another size, or another CRC32 than the archive{checked ? '' : ' (run “Check against archive” first to find files with other content)'}.
                </span>
              </span>
            </label>
            <label className="flex items-start gap-2">
              <input type="checkbox" className="mt-1" checked={missing} disabled={!counts.missing} onChange={(e) => setMissing(e.target.checked)} />
              <span>
                <span className="font-semibold">Missing files</span> — {count(counts.missing)} · {bytes(counts.missingBytes)}
                <span className="block text-xs muted">Created where the archive puts them, with any missing folders.</span>
              </span>
            </label>
          </div>
        </Field>
        {damaged && (
          <div className="space-y-2 rounded-md bg-surface-2 p-2 text-sm">
            <label className="flex items-center gap-2">
              <input type="radio" className="accent-accent" checked={keep.keep === null} onChange={() => keep.setKeep(false)} />
              The damaged file goes to the trash (the plan can be undone)
            </label>
            <label className="flex flex-wrap items-center gap-2">
              <input type="radio" className="accent-accent" checked={keep.keep !== null} onChange={() => keep.setKeep(true)} />
              Keep the damaged file as
              <input
                className="input w-64 py-1 font-mono"
                value={keep.template}
                onChange={(e) => keep.setKeep(true, e.target.value)}
                onFocus={() => keep.keep === null && keep.setKeep(true)}
                aria-label="Where the damaged file is kept"
              />
            </label>
            {keep.keep !== null && <p className="text-xs muted">Tokens: {tokens}. An existing name gets “(2)”. Undo puts the damaged file back.</p>}
          </div>
        )}
        <p className="text-sm muted">
          Each file is checked against the archive’s size and CRC32 before it takes its place; one that does not match is skipped and nothing changes for it. Undo
          removes the files created and puts the damaged ones back.
        </p>
        {create.error && <ErrorBox error={create.error} />}
        <div className="flex justify-end gap-2">
          <button className="btn-secondary" onClick={onClose}>
            Cancel
          </button>
          <button className="btn-primary" onClick={() => create.mutate()} disabled={create.isPending || (!damaged && !missing)}>
            {existing ? `Add to “${existing.name}”` : 'Create plan for review'}
          </button>
        </div>
      </div>
    </Modal>
  )
}

