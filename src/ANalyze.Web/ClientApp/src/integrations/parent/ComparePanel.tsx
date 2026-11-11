import { useEffect, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../../api/client'
import { ErrorBox } from '../../components/ui'
import { bytes, count, date, pct } from '../../lib/format'

interface Side {
  size: number
  mTime: number
  hash: string
  indexHash?: string
  hashedAt?: number
  corruptAt?: number
  mime?: string
  startHex: string
  zeroBytes: number
}

interface Range {
  offset: number
  length: number
  parentOffset: number
  parentLength: number
  zeroHere: boolean
  zeroParent: boolean
}

interface Report {
  verdict: string
  summary: string
  advice: string
  details: string[]
  damaged?: 'here' | 'parent'
  newer?: 'here' | 'parent'
  here: Side
  parent: Side
  block: number
  blocks: number
  differentBlocks: number
  differentBytes: number
  commonPrefix: number
  commonSuffix: number
  exact: boolean
  ranges: Range[]
}

interface Status {
  entryId: number
  status: 'running' | 'done' | 'failed'
  phase?: 'reading' | 'waiting'
  size: number
  parentSize: number
  read: number
  parentRead: number
  report?: Report
  error?: string
}

const VERDICTS: Record<string, { label: string; tone: string }> = {
  identical: { label: 'Identical now', tone: 'chip' },
  truncated: { label: 'Cut off', tone: 'pill-danger' },
  appended: { label: 'Grew since the backup', tone: 'pill-amber' },
  zeros: { label: 'Zeros instead of data', tone: 'pill-danger' },
  bitrot: { label: 'Silent corruption', tone: 'pill-danger' },
  header: { label: 'Header / tags changed', tone: 'pill-amber' },
  trailer: { label: 'End changed', tone: 'pill-amber' },
  middle: { label: 'Middle changed', tone: 'pill-amber' },
  edited: { label: 'Edited', tone: 'pill-amber' },
  different: { label: 'Different file', tone: 'pill-amber' },
  changing: { label: 'Changing', tone: 'pill-muted' },
}

/**
 * Explains how a corrupted file differs from its copy on the backup: both instances read their copy (block hashes from the start
 * and the end) and the server compares them. Starts on mount and polls until done.
 */
export function ComparePanel({ entryId, parentName }: { entryId: number; parentName: string }) {
  const qc = useQueryClient()
  const key = ['parent', 'compare', entryId]
  const start = useMutation({
    mutationFn: () => api<Status>('/integrations/parent/compare', { method: 'POST', query: { entryId } }),
    onSuccess: (s) => qc.setQueryData(key, s),
  })
  const cancel = useMutation({ mutationFn: () => api('/integrations/parent/compare', { method: 'DELETE', query: { entryId } }) })
  const q = useQuery({
    queryKey: key,
    queryFn: () => api<Status>('/integrations/parent/compare', { query: { entryId } }),
    enabled: start.isSuccess,
    refetchInterval: (x) => (x.state.data?.status === 'running' ? 1000 : false),
  })
  const startNow = start.mutate
  useEffect(() => startNow(), [startNow])

  const s = q.data ?? start.data
  if (start.error) return <ErrorBox error={start.error} />
  if (!s) return <p className="text-sm muted">Starting…</p>
  if (s.status === 'running')
    return (
      <div className="space-y-2 text-sm">
        <Progress label="Here" read={s.read} total={s.size} />
        <Progress label={parentName} read={s.parentRead} total={s.parentSize} note={s.phase === 'waiting' && !s.parentRead ? 'waiting for another comparison there' : undefined} />
        <div className="flex items-center gap-2">
          <span className="text-xs muted">Both copies are read completely; large files take a while.</span>
          <span className="flex-1" />
          <button className="btn-ghost px-2 py-1 text-xs" onClick={() => cancel.mutate()} disabled={cancel.isPending}>
            Cancel
          </button>
        </div>
      </div>
    )
  if (s.status === 'failed' || !s.report)
    return (
      <div className="flex items-center gap-2 text-sm">
        <span className="text-danger">{s.error ?? 'The comparison failed.'}</span>
        <span className="flex-1" />
        <button className="btn-ghost px-2 py-1 text-xs" onClick={() => start.mutate()}>
          Try again
        </button>
      </div>
    )
  return <ReportView r={s.report} parentName={parentName} onAgain={() => start.mutate()} />
}

function Progress({ label, read, total, note }: { label: string; read: number; total: number; note?: string }) {
  return (
    <div>
      <div className="flex justify-between text-xs">
        <span>{label}</span>
        <span className="muted tabular">{note ?? `${bytes(read)} of ${bytes(total)} · ${pct(read, total)}`}</span>
      </div>
      <div className="mt-1 h-1.5 overflow-hidden rounded bg-surface-2">
        <div className="h-full bg-accent transition-all" style={{ width: `${total ? Math.min(100, (read / total) * 100) : 0}%` }} />
      </div>
    </div>
  )
}

function ReportView({ r, parentName, onAgain }: { r: Report; parentName: string; onAgain: () => void }) {
  const v = VERDICTS[r.verdict] ?? { label: r.verdict, tone: 'pill-muted' }
  const who = (side?: 'here' | 'parent') => (side === 'here' ? 'this copy' : side === 'parent' ? `the copy on ${parentName}` : null)
  return (
    <div className="space-y-3 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <span className={v.tone}>{v.label}</span>
        {r.damaged && <span className="text-xs muted">likely broken or outdated: {who(r.damaged)}</span>}
        <span className="flex-1" />
        <button className="btn-ghost px-2 py-1 text-xs" onClick={onAgain}>
          Compare again
        </button>
      </div>
      <p className="font-medium">{r.summary}</p>
      <p className="muted">{r.advice}</p>
      {r.verdict !== 'identical' && r.verdict !== 'changing' && <DiffBar r={r} parentName={parentName} />}
      <table className="w-full text-xs">
        <thead>
          <tr className="muted">
            <th className="py-1 text-left font-normal" />
            <th className="py-1 text-left font-normal">Here</th>
            <th className="py-1 text-left font-normal">{parentName}</th>
          </tr>
        </thead>
        <tbody className="tabular">
          <Row label="Size" a={bytes(r.here.size)} b={bytes(r.parent.size)} />
          <Row label="Modified" a={date(r.here.mTime)} b={date(r.parent.mTime)} />
          <Row label="Type" a={r.here.mime ?? 'unknown'} b={r.parent.mime ?? 'unknown'} />
          <Row label="First bytes" a={<span className="font-mono">{r.here.startHex}</span>} b={<span className="font-mono">{r.parent.startHex}</span>} />
          <Row label="Zero blocks" a={bytes(r.here.zeroBytes)} b={bytes(r.parent.zeroBytes)} />
          <Row label="Content hash" a={<Hash h={r.here.hash} stored={r.here.indexHash} />} b={<Hash h={r.parent.hash} stored={r.parent.indexHash} />} />
          {(r.here.corruptAt || r.parent.corruptAt) && (
            <Row label="Silent change seen" a={r.here.corruptAt ? date(r.here.corruptAt) : '—'} b={r.parent.corruptAt ? date(r.parent.corruptAt) : '—'} />
          )}
        </tbody>
      </table>
      {r.details.length > 0 && (
        <ul className="list-disc space-y-0.5 pl-5 text-xs muted">
          {r.details.map((d, i) => (
            <li key={i}>{d}</li>
          ))}
        </ul>
      )}
      {r.ranges.length > 0 && r.here.size === r.parent.size && (
        <details className="text-xs">
          <summary className="cursor-pointer muted">
            {count(r.differentBlocks)} of {count(r.blocks)} blocks of {bytes(r.block)} differ ({bytes(r.differentBytes)}) in {count(r.ranges.length)}
            {r.ranges.length >= 50 ? '+' : ''} places
          </summary>
          <ul className="mt-1 space-y-0.5 font-mono">
            {r.ranges.map((x) => (
              <li key={x.offset}>
                {x.offset.toLocaleString()} – {(x.offset + x.length).toLocaleString()} ({bytes(x.length)})
                {x.zeroParent ? ` · zeros on ${parentName}` : x.zeroHere ? ' · zeros here' : ''}
              </li>
            ))}
          </ul>
        </details>
      )}
    </div>
  )
}

function Row({ label, a, b }: { label: string; a: ReactNode; b: ReactNode }) {
  return (
    <tr className="border-t border-line/60">
      <td className="py-1 pr-2 muted">{label}</td>
      <td className="py-1 pr-2">{a}</td>
      <td className="py-1">{b}</td>
    </tr>
  )
}

function Hash({ h, stored }: { h: string; stored?: string }) {
  return (
    <span className="font-mono" title={h}>
      {h.slice(0, 12)}…{stored && stored !== h && <span className="text-amber"> (stored: {stored.slice(0, 8)}…, outdated)</span>}
    </span>
  )
}

/** Where the copies differ along the file: equal start and end in the accent colour, differences in red (zeros striped). */
function DiffBar({ r, parentName }: { r: Report; parentName: string }) {
  const len = Math.max(r.here.size, r.parent.size, 1)
  const at = (n: number) => `${(n / len) * 100}%`
  const sameSize = r.here.size === r.parent.size
  const lanes: { label: string; size: number; spans: { from: number; to: number; zero: boolean }[] }[] = sameSize
    ? [{ label: 'Both', size: r.here.size, spans: r.ranges.map((x) => ({ from: x.offset, to: x.offset + x.length, zero: x.zeroHere || x.zeroParent })) }]
    : [
        { label: 'Here', size: r.here.size, spans: r.ranges.map((x) => ({ from: x.offset, to: x.offset + x.length, zero: x.zeroHere })) },
        { label: parentName, size: r.parent.size, spans: r.ranges.map((x) => ({ from: x.parentOffset, to: x.parentOffset + x.parentLength, zero: x.zeroParent })) },
      ]
  return (
    <div className="space-y-1">
      {lanes.map((l) => (
        <div key={l.label} className="flex items-center gap-2 text-xs">
          <span className="w-16 shrink-0 truncate muted">{l.label}</span>
          <div className="relative h-3 flex-1 rounded bg-surface-2">
            <div className="absolute inset-y-0 left-0 rounded bg-accent/40" style={{ width: at(l.size) }} />
            {l.spans.map((s, i) => (
              <div
                key={i}
                className={clsx('absolute inset-y-0', s.zero ? 'bg-danger/40' : 'bg-danger')}
                style={{ left: at(s.from), width: `max(2px, ${at(s.to - s.from)})` }}
                title={`${bytes(s.to - s.from)} at ${s.from.toLocaleString()}${s.zero ? ' (zeros)' : ''}`}
              />
            ))}
          </div>
        </div>
      ))}
      <p className="text-xs muted">
        Identical start {bytes(r.commonPrefix)} · identical end {bytes(r.commonSuffix)}
        {r.exact ? '' : ' (to the block)'}
      </p>
    </div>
  )
}
