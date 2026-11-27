import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { keepPreviousData, useMutation, useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import { FolderCompare, type CompareRow, type CompareText } from '../components/FolderCompare'
import { usePlanAdded, usePlanChoice } from '../components/PlanDialog'
import { Empty, ErrorBox, Field, Loading, Modal, PageTitle, ShareBar, Stat } from '../components/ui'
import { leaveOverlay, useUrlOverlay, useUrlParams } from '../hooks/useUrlState'
import { bytes, count } from '../lib/format'

interface Side {
  id: number
  rootId: number
  root?: string
  relPath: string
  parentId?: number | null
  files: number
  size: number
}

interface Copy {
  id: number
  rootId: number
  root?: string
  relPath: string
  parentId?: number | null
}

interface CompareResponse {
  a: Side
  b: Side
  files: CompareRow[]
  /** A folder has more files than are compared. */
  capped: boolean
  /** With `hash`: every copy of that duplicate group. */
  copies?: Copy[] | null
}

const FILTERS: CompareText['filters'] = [
  { id: 'problems', label: 'Differences', title: 'Only in one folder, another size or other content' },
  { id: 'missing', label: 'Only in A', title: 'In the first folder, not at the same path in the second' },
  { id: 'extra', label: 'Only in B', title: 'In the second folder, not at the same path in the first' },
  { id: 'differs', label: 'Other size', title: 'At the same path in both, with another size' },
  { id: 'corrupt', label: 'Other content', title: 'At the same path and size in both, with another content hash' },
  { id: 'matched', label: 'Same', title: 'Same path and size in both (same content hash where both are hashed)' },
  { id: 'all', label: 'All', title: 'Every file of both folders' },
]

function label(f: CompareRow) {
  switch (f.state) {
    case 'matched':
      return f.crc === 'match' ? 'same content' : 'same size (not hashed)'
    case 'missing':
      return 'only in A'
    case 'extra':
      return 'only in B'
    case 'corrupt':
      return 'same size, other content'
    default:
      return 'other size'
  }
}

const folderName = (s: Side) => (s.relPath ? s.relPath.split('/').pop()! : (s.root ?? '/'))
const where = (s: { root?: string; relPath: string }) => `${s.root ?? '?'}/${s.relPath}`

/** What a cleanup of A would do, as the server plans it (`POST /duplicates/compare/plan`): mirrors `PicksFor`. */
function cleanupCounts(files: CompareRow[]) {
  const c = { copies: 0, copiesBytes: 0, moves: 0, movesBytes: 0, left: 0, leftBytes: 0 }
  for (const f of files) {
    if (f.state === 'extra') continue
    if ((f.state === 'matched' && f.crc === 'match') || (f.state === 'missing' && f.elsewhere)) {
      c.copies++
      c.copiesBytes += f.size
    } else if (f.state === 'missing') {
      c.moves++
      c.movesBytes += f.size
    } else {
      c.left++
      c.leftBytes += f.size
    }
  }
  return c
}

/**
 * Two folders compared file by file with the extraction page's view — usually the parent folders of two copies of a duplicate, since
 * folders cannot be matched reliably by themselves: the duplicates show they belong together, the comparison what else differs.
 * `?a=&b=` are entry ids (a file stands for its folder), `?hash=` the duplicate group, to pick other copies.
 */
export default function FolderComparePage() {
  const [params, set] = useUrlParams()
  const a = Number(params.get('a'))
  const b = Number(params.get('b'))
  const hash = params.get('hash') ?? ''
  const data = useQuery({
    queryKey: ['folder-compare', a, b, hash],
    queryFn: () => api<CompareResponse>('/duplicates/compare', { query: { a, b, hash: hash || undefined } }),
    enabled: Number.isInteger(a) && Number.isInteger(b) && a > 0 && b > 0,
    placeholderData: keepPreviousData,
  })
  const d = data.data
  const files = useMemo(() => d?.files ?? [], [d])
  const stats = useMemo(() => {
    const s = { same: 0, sameBytes: 0, onlyA: 0, onlyABytes: 0, onlyB: 0, onlyBBytes: 0, other: 0, moved: 0, aBytes: 0 }
    for (const f of files) {
      if (f.state !== 'extra') s.aBytes += f.size
      if (f.state === 'matched') {
        s.same++
        s.sameBytes += f.size
      } else if (f.state === 'missing') {
        s.onlyA++
        s.onlyABytes += f.size
        if (f.elsewhere) s.moved++
      } else if (f.state === 'extra') {
        s.onlyB++
        s.onlyBBytes += f.size
      } else s.other++
    }
    return s
  }, [files])
  const capped = !!d?.capped
  const cleanup = useUrlOverlay('cleanup')
  const plan = useMemo(() => cleanupCounts(files), [files])
  // Moves stay within one root, so across roots only A's copies of B's files can be planned.
  const plannable = plan.copies + (d && d.a.rootId === d.b.rootId ? plan.moves : 0)
  const text = useMemo<CompareText>(
    () => ({
      tree: 'Folders in A and B',
      rootIcon: '📁',
      rootLabel: d ? folderName(d.a) : '/',
      a: 'In A',
      b: 'In B',
      filters: FILTERS,
      problems: ['missing', 'extra', 'differs', 'corrupt'],
      label,
      noProblems: 'Both folders hold the same files{where}.',
      footer: () => (
        <p className="mt-2 text-xs muted">
          Files are paired by their path below each folder and compared by size, and by content hash where both have one. “Same content at …” names a file at another path
          in the other folder with the same hash: moved or renamed.
          {capped && ' A folder has more files than are compared: only the first 50,000 of each are.'}
        </p>
      ),
    }),
    [d, capped],
  )

  if (!(a > 0 && b > 0)) return <Empty>Choose two copies of a duplicate to compare their folders.</Empty>
  if (data.isLoading) return <Loading />
  if (data.error) return <ErrorBox error={data.error} />
  if (!d) return <Empty>These files are no longer indexed.</Empty>

  // Another pair of folders: the chosen folder, filter and text belong to the old pair's paths.
  const go = (patch: { a?: number; b?: number }) => set({ ...patch, dir: null, show: null, q: null })
  const canGoUp = d.a.parentId != null && d.b.parentId != null && d.a.parentId !== d.b.parentId && d.a.parentId !== d.b.id && d.b.parentId !== d.a.id
  const copies = d.copies ?? []
  const pick = (side: 'a' | 'b', value: string) => go({ [side]: Number(value) })
  const copyOf = (s: Side) => copies.find((c) => c.parentId === s.id)?.id ?? ''

  return (
    <>
      <div className="mb-2 text-sm">
        <Link to="/duplicates" className="muted hover:text-accent">
          ← Duplicates
        </Link>
      </div>
      <PageTitle
        subtitle="The folders two copies of a duplicate are in, file by file: whether the rest of the folder was copied along, and what changed since."
        actions={
          <>
            <button
              className="btn-primary"
              disabled={capped || plannable === 0}
              onClick={() => cleanup.open('a')}
              title={
                capped
                  ? 'A folder has more than 50,000 files: compare smaller folders to plan from them'
                  : plannable === 0
                    ? 'Nothing in A that B has, or that could move to B'
                    : 'Remove from A what B already has, or merge A into B'
              }
            >
              Clean up A…
            </button>
            <button className="btn-secondary" onClick={() => go({ a: d.b.id, b: d.a.id })} title="Swap A and B">
              ⇄ Swap
            </button>
            <button
              className="btn-secondary"
              disabled={!canGoUp}
              onClick={() => go({ a: d.a.parentId!, b: d.b.parentId! })}
              title={canGoUp ? 'Compare the folders one level up on both sides' : 'One of the folders is a root, or both have the same parent'}
            >
              ↑ One level up
            </button>
          </>
        }
      >
        Compare folders
      </PageTitle>

      <div className="mb-4 grid gap-2 md:grid-cols-2">
        {(['a', 'b'] as const).map((k) => {
          const s = d[k]
          return (
            <div key={k} className="card flex min-w-0 items-center gap-2 px-3 py-2 text-sm">
              <span className="pill-muted font-semibold">{k.toUpperCase()}</span>
              <Link to={`/browse/${s.id}`} className="min-w-0 flex-1 truncate font-mono text-xs hover:text-accent" title={where(s)}>
                📁 {where(s)}
              </Link>
              <span className="shrink-0 text-xs muted tabular">
                {count(s.files)} files · {bytes(s.size)}
              </span>
              {copies.length > 2 && (
                <select className="input w-auto max-w-40 py-0.5 text-xs" value={copyOf(s)} onChange={(e) => pick(k, e.target.value)} title="The folder of another copy">
                  <option value="" disabled>
                    another copy…
                  </option>
                  {copies
                    .filter((c) => c.parentId !== d[k === 'a' ? 'b' : 'a'].id)
                    .map((c) => (
                      <option key={c.id} value={c.id}>
                        {where(c)}
                      </option>
                    ))}
                </select>
              )}
            </div>
          )
        })}
      </div>

      <div className="mb-4 grid grid-cols-2 gap-3 lg:grid-cols-4">
        <div className="card px-4 py-3">
          <div className="text-xs font-medium tracking-wide text-muted uppercase">Same in B</div>
          <div className="mt-1 text-2xl font-bold tabular">{Math.floor((100 * stats.sameBytes) / Math.max(1, stats.aBytes))}%</div>
          <ShareBar value={stats.sameBytes} max={stats.aBytes} color="var(--success)" className="my-1" />
          <div className="text-xs muted">
            {count(stats.same)} files · {bytes(stats.sameBytes)} of A’s {bytes(stats.aBytes)}
          </div>
        </div>
        <Stat label="Only in A" value={count(stats.onlyA)} hint={stats.onlyA ? `${bytes(stats.onlyABytes)}${stats.moved ? ` · ${count(stats.moved)} elsewhere in B` : ''}` : 'nothing'} tone={stats.onlyA ? 'amber' : undefined} />
        <Stat label="Only in B" value={count(stats.onlyB)} hint={stats.onlyB ? bytes(stats.onlyBBytes) : 'nothing'} tone={stats.onlyB ? 'amber' : undefined} />
        <Stat label="Same path, different" value={count(stats.other)} hint={stats.other ? 'another size or content' : 'none'} />
      </div>

      <FolderCompare files={files} text={text} defaultShow="all" />
      {cleanup.value && <CleanupDialog a={d.a} b={d.b} counts={plan} onClose={cleanup.close} />}
    </>
  )
}

/**
 * Plans folder A's cleanup from the comparison: remove A's files whose content B has (re-hashed with B's copy at apply), or also move
 * the files only A has into B, at the same path below it. Same path with another size or content is left alone.
 */
function CleanupDialog({ a, b, counts, onClose }: { a: Side; b: Side; counts: ReturnType<typeof cleanupCounts>; onClose: () => void }) {
  const navigate = useNavigate()
  const added = usePlanAdded()
  const { existing, field } = usePlanChoice()
  const sameRoot = a.rootId === b.rootId
  const [mode, setMode] = useState<'copies' | 'merge'>(counts.copies === 0 && sameRoot ? 'merge' : 'copies')
  const [op, setOp] = useState<'trash' | 'delete' | 'hardlink'>('trash')
  const create = useMutation({
    mutationFn: () => api<{ id: number }>('/duplicates/compare/plan', { method: 'POST', json: { a: a.id, b: b.id, mode, op, planId: existing?.id } }),
    onSuccess: (p) => {
      added(p)
      leaveOverlay(navigate, `/actions/${p.id}`)
    },
  })
  const nothing = mode === 'copies' ? counts.copies === 0 : counts.copies + counts.moves === 0
  return (
    <Modal open onClose={onClose} title="Clean up folder A">
      <div className="space-y-4">
        <div className="space-y-1 text-sm">
          <div className="truncate font-mono text-xs" title={where(a)}>
            <span className="pill-muted mr-1 font-semibold">A</span>
            {where(a)}
          </div>
          <div className="truncate font-mono text-xs" title={where(b)}>
            <span className="pill-muted mr-1 font-semibold">B</span>
            {where(b)} <span className="muted">(kept as it is)</span>
          </div>
        </div>
        {field}
        <Field label="What to do">
          <div className="space-y-2 text-sm">
            <label className="flex items-start gap-2">
              <input type="radio" className="mt-1" checked={mode === 'copies'} onChange={() => setMode('copies')} disabled={counts.copies === 0} />
              <span>
                <span className="font-semibold">Remove A’s copies of what B has</span> — {count(counts.copies)} files · {bytes(counts.copiesBytes)}
                <span className="block text-xs muted">Same content hash in B, at the same path or elsewhere (moved or renamed).</span>
              </span>
            </label>
            <label className="flex items-start gap-2">
              <input type="radio" className="mt-1" checked={mode === 'merge'} onChange={() => setMode('merge')} disabled={!sameRoot || counts.copies + counts.moves === 0} />
              <span>
                <span className="font-semibold">Merge A into B</span> — also move {count(counts.moves)} files · {bytes(counts.movesBytes)} only A has into B
                <span className="block text-xs muted">
                  {sameRoot ? 'At the same path below B, so A ends up holding nothing B lacks.' : 'Only within one root: A and B are on different roots.'}
                </span>
              </span>
            </label>
          </div>
        </Field>
        <Field label="A’s copies">
          <select className="input" value={op} onChange={(e) => setOp(e.target.value as typeof op)}>
            <option value="trash">Move to trash (undoable)</option>
            <option value="hardlink">Replace with a hard link to B’s copy (keeps every path, frees the space)</option>
            <option value="delete">Delete permanently</option>
          </select>
        </Field>
        {counts.left > 0 && (
          <p className="text-sm text-amber">
            {count(counts.left)} files ({bytes(counts.leftBytes)}) are left alone: B has another size or content at the same path, or only the size could be compared (not hashed yet).
          </p>
        )}
        <p className="text-sm muted">Each copy is re-hashed together with B’s right before it is removed, so files changed since the scan are skipped.</p>
        {create.error && <ErrorBox error={create.error} />}
        <div className="flex justify-end gap-2">
          <button className="btn-secondary" onClick={onClose}>
            Cancel
          </button>
          <button className="btn-primary" onClick={() => create.mutate()} disabled={create.isPending || nothing}>
            {existing ? `Add to “${existing.name}”` : 'Create plan for review'}
          </button>
        </div>
      </div>
    </Modal>
  )
}
