import { useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { api, fileUrl } from '../../api/client'
import type { ActionOp, Entry, ItemStatus } from '../../api/types'
import { BackupCompareView } from '../../components/BackupCompareView'
import { PlanDialog } from '../../components/PlanDialog'
import { Empty, ErrorBox, Loading, PageTitle, Progress, Tabs } from '../../components/ui'
import { useUrlEnum, useUrlNumber, useUrlOverlay, useUrlParams } from '../../hooks/useUrlState'
import { bytes, count, date } from '../../lib/format'
import { KEEP_DAMAGED_DEFAULT, OP_LABEL, keepDamaged, reviewPlan, useDraftPlans } from '../../lib/plans'
import { ComparePanel } from './ComparePanel'

interface FileRow {
  entryId: number
  rootId: number
  relPath: string
  parentPath?: string
  state: string
  size: number
  parentSize?: number
}

interface Decision {
  itemId: number
  entryId: number
  op: ActionOp
  included: boolean
  status: ItemStatus
  keepPath?: string
  warning?: string
  /** Listed and left out on purpose: "keep the file here". */
  kept: boolean
}

interface Decisions {
  plan: { id: number; name: string; status: string }
  items: Decision[]
}

interface Detail {
  entry: Entry
  root?: { name: string; readOnly: boolean }
  fullPath?: string
}

type Choice = 'replace' | 'aside' | 'keep' | 'clear'
type View = 'side' | 'why'

const LIMIT = 1000

function decisionText(d: Decision) {
  if (d.kept) return '✓ Keep the file here'
  if (!d.included) return `Left out: ${d.warning ?? 'excluded in the plan'}`
  if (d.op === 'restore') return d.keepPath ? `🛟 Replace, keep the damaged file as ${d.keepPath}` : '🛟 Replace, damaged file to the trash'
  return OP_LABEL[d.op]
}

function decisionMark(d?: Decision) {
  if (!d) return { icon: '·', tone: 'muted', title: 'Not decided' }
  if (d.kept) return { icon: '✓', tone: 'text-accent', title: 'Keep the file here' }
  if (!d.included) return { icon: '!', tone: 'text-amber', title: decisionText(d) }
  if (d.op === 'restore') return { icon: d.keepPath ? '~' : '🛟', tone: 'text-success', title: decisionText(d) }
  return { icon: '•', tone: 'text-danger', title: OP_LABEL[d.op] }
}

/**
 * The corrupted files one at a time, each next to the backup's copy, with a decision per file: replace it with the backup's copy
 * (damaged file to the trash, or kept under another name), keep the file here, or skip it for now. Decisions are items of a draft
 * plan, so the review can be left and continued, and nothing changes on disk until that plan is applied. The current file
 * (`?at=`), the comparison shown and the "undecided only" filter are in the URL.
 */
export default function DamagedReview() {
  const qc = useQueryClient()
  const [params, setParams] = useUrlParams()
  const [at] = useUrlNumber('at', -1)
  const [view, setView] = useUrlEnum<View>('view', ['side', 'why'], 'side')
  const todoOnly = params.has('todo')
  const other = useUrlOverlay('other')
  const summary = useQuery({ queryKey: ['parent', 'summary'], queryFn: () => api<{ snapshot?: { parentName: string } }>('/integrations/parent/summary') })
  const parentName = summary.data?.snapshot?.parentName ?? 'the backup'
  const files = useQuery({
    queryKey: ['parent', 'files', 'damaged'],
    queryFn: () => api<{ total: number; items: FileRow[] }>('/integrations/parent/files', { query: { damaged: true, sort: 'path', limit: LIMIT } }),
  })
  const drafts = useDraftPlans()

  // The plan the decisions go to: from the link that opened the review, else the one of the last review, while it is a draft.
  const [planId, setPlanIdState] = useState<number | null>(() => {
    const p = Number(params.get('plan'))
    return params.has('plan') && Number.isInteger(p) ? p : reviewPlan.get()
  })
  const setPlanId = (id: number | null) => {
    setPlanIdState(id)
    reviewPlan.set(id)
  }
  const decisions = useQuery({
    queryKey: ['plan', String(planId), 'decisions'],
    queryFn: () => api<Decisions>(`/plans/${planId}/decisions`),
    enabled: planId !== null,
    retry: false,
  })
  const plan = planId !== null && decisions.data?.plan.status === 'draft' ? decisions.data.plan : undefined
  const waiting = planId !== null && decisions.isLoading
  const byEntry = useMemo(() => new Map((plan ? decisions.data!.items : []).map((d) => [d.entryId, d])), [plan, decisions.data])

  const [keepAs, setKeepAs] = useState(() => keepDamaged.get() ?? KEEP_DAMAGED_DEFAULT)
  const items = useMemo(() => files.data?.items ?? [], [files.data])
  const index = Math.max(0, items.findIndex((f) => f.entryId === at))
  const current = items[index] as FileRow | undefined
  const detail = useQuery({ queryKey: ['entry', current?.entryId], queryFn: () => api<Detail>(`/entries/${current!.entryId}`), enabled: !!current })

  const go = (i: number) => items[i] && setParams({ at: items[i].entryId }, { replace: true })
  /** The next file without a decision after `from` (wrapping around), not counting `decided`. */
  const nextUndecided = (from: number, decided?: number) => {
    for (let k = 1; k <= items.length; k++) {
      const i = (from + k) % items.length
      if (!byEntry.has(items[i].entryId) && items[i].entryId !== decided) return i
    }
    return -1
  }

  const decide = useMutation({
    mutationFn: async ({ entryId, choice }: { entryId: number; choice: Choice }) => {
      if (choice === 'clear') {
        if (plan) await api(`/plans/${plan.id}/items/remove`, { method: 'POST', json: { entryIds: [entryId] } })
        return plan?.id
      }
      const made = await api<{ id: number }>('/plans', {
        method: 'POST',
        json: {
          planId: plan?.id,
          name: plan ? undefined : `Corrupted files review ${new Date().toLocaleDateString()}`,
          op: 'restore',
          entryIds: [entryId],
          keepDamaged: choice === 'aside' ? keepAs.trim() || KEEP_DAMAGED_DEFAULT : undefined,
          leaveOut: choice === 'keep',
        },
      })
      return made.id
    },
    onSuccess: (id, v) => {
      if (id !== undefined && id !== planId) setPlanId(id)
      if (v.choice === 'aside') keepDamaged.set(keepAs.trim() || KEEP_DAMAGED_DEFAULT)
      qc.invalidateQueries({ queryKey: ['plan'] })
      qc.invalidateQueries({ queryKey: ['plans'] })
      qc.invalidateQueries({ queryKey: ['summary'] })
      if (v.choice === 'clear') return
      const next = nextUndecided(index, v.entryId)
      if (next >= 0) go(next)
    },
  })

  const busy = decide.isPending || waiting
  const readOnly = !!detail.data?.root?.readOnly
  const choose = (choice: Choice) => current && !busy && (choice === 'clear' || choice === 'keep' || !readOnly) && decide.mutate({ entryId: current.entryId, choice })

  // Keys: 1 / 2 / 3 decide, ← → step, S skips to the next undecided file. The handler reads the latest state through a ref.
  const keys = useRef<(e: KeyboardEvent) => void>(() => {})
  useEffect(() => {
    keys.current = (e) => {
      const t = e.target as HTMLElement | null
      if (e.altKey || e.ctrlKey || e.metaKey || other.value !== null || t?.closest('input, textarea, select, video, audio, dialog, [contenteditable="true"]')) return
      if (e.key === '1') choose('replace')
      else if (e.key === '2') choose('aside')
      else if (e.key === '3') choose('keep')
      else if (e.key === 'ArrowLeft') go(index - 1)
      else if (e.key === 'ArrowRight') go(index + 1)
      else if (e.key === 's' || e.key === 'S') go(nextUndecided(index))
      else return
      e.preventDefault()
    }
  })
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => keys.current(e)
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const back = (
    <Link to="/integrations/parent?tab=corrupt" className="link">
      ← Backup
    </Link>
  )
  if (files.isLoading) return <Loading />
  if (files.error) return <ErrorBox error={files.error} />
  if (!items.length)
    return (
      <>
        <PageTitle subtitle={back}>Review corrupted files</PageTitle>
        <Empty>No corrupted files with a copy on the backup. 🎉</Empty>
      </>
    )

  const decided = items.filter((f) => byEntry.has(f.entryId)).length
  const tally = (test: (d: Decision) => boolean) => items.filter((f) => byEntry.has(f.entryId) && test(byEntry.get(f.entryId)!)).length
  const replacing = tally((d) => d.included && d.op === 'restore')
  const keeping = tally((d) => d.kept)
  const shown = todoOnly ? items.filter((f) => !byEntry.has(f.entryId) || f.entryId === current?.entryId) : items
  const d = current ? byEntry.get(current.entryId) : undefined
  const e = detail.data?.entry
  const is = (c: Choice) => !!d && (c === 'keep' ? d.kept : d.included && d.op === 'restore' && (c === 'aside') === !!d.keepPath)
  const choiceBtn = (c: Choice) => clsx('btn', is(c) ? 'btn-primary' : 'btn-secondary')

  return (
    <>
      <PageTitle
        subtitle={
          <>
            {back} · each file next to its copy on {parentName}. Decisions are collected in a draft plan — nothing changes until you apply it.
          </>
        }
        actions={
          <>
            <select
              className="input w-auto max-w-72"
              value={plan?.id ?? ''}
              onChange={(ev) => setPlanId(ev.target.value ? Number(ev.target.value) : null)}
              aria-label="Plan the decisions go to"
              title="The draft plan the decisions go to"
            >
              <option value="">A new plan (made by the first decision)</option>
              {plan && !drafts.data?.some((p) => p.id === plan.id) && <option value={plan.id}>#{plan.id} {plan.name}</option>}
              {drafts.data?.map((p) => (
                <option key={p.id} value={p.id}>
                  #{p.id} {p.name}
                </option>
              ))}
            </select>
            {plan && (
              <Link className="btn-primary" to={`/actions/${plan.id}`}>
                Open the plan ({count(replacing)} to replace) →
              </Link>
            )}
          </>
        }
      >
        Review corrupted files
      </PageTitle>

      <div className="card mb-4 p-3">
        <div className="mb-1 flex flex-wrap gap-x-4 text-sm">
          <span className="flex-1">
            {count(decided)} of {count(items.length)} decided
            {files.data!.total > items.length ? ` (the first ${count(LIMIT)} of ${count(files.data!.total)}; apply the plan to see the rest)` : ''}
          </span>
          <span className="muted">
            {count(replacing)} replace · {count(keeping)} keep · {count(decided - replacing - keeping)} other · {count(items.length - decided)} open
          </span>
        </div>
        <Progress value={decided} max={items.length} />
      </div>
      {decide.error && <ErrorBox error={decide.error} />}

      <div className="grid gap-4 lg:grid-cols-[19rem_minmax(0,1fr)]">
        <div className="card self-start">
          <label className="flex cursor-pointer items-center gap-2 border-b border-line/60 px-3 py-2 text-xs muted">
            <input type="checkbox" className="h-3.5 w-3.5 accent-accent" checked={todoOnly} onChange={(ev) => setParams({ todo: ev.target.checked ? 1 : null }, { replace: true })} />
            Undecided only
          </label>
          <div className="max-h-[70vh] divide-y divide-line/60 overflow-y-auto">
            {shown.map((f) => {
              const m = decisionMark(byEntry.get(f.entryId))
              const slash = f.relPath.lastIndexOf('/')
              return (
                <button
                  key={f.entryId}
                  ref={f.entryId === current?.entryId ? (el) => el?.scrollIntoView({ block: 'nearest' }) : undefined}
                  className={clsx('flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm hover:bg-accent-faint', f.entryId === current?.entryId && 'bg-accent-faint')}
                  onClick={() => go(items.indexOf(f))}
                  title={`${f.relPath} — ${m.title}`}
                >
                  <span className={clsx('w-5 shrink-0 text-center', m.tone)}>{m.icon}</span>
                  <span className="min-w-0 flex-1">
                    <span className="block truncate">{f.relPath.slice(slash + 1)}</span>
                    <span className="block truncate text-xs muted">{slash > 0 ? f.relPath.slice(0, slash) : '/'}</span>
                  </span>
                  <span className="shrink-0 text-xs tabular muted">{bytes(f.size)}</span>
                </button>
              )
            })}
          </div>
        </div>

        {current && (
          <div className="card min-w-0 p-4">
            <div className="mb-3 flex flex-wrap items-start gap-2">
              <div className="min-w-0 flex-1">
                <div className="text-xs muted">
                  {index + 1} / {items.length}
                  {detail.data?.root ? ` · ${detail.data.root.name}` : ''}
                </div>
                <h2 className="text-lg font-semibold break-all">{current.relPath}</h2>
                <div className="text-xs break-all muted">
                  {parentName}: {current.parentPath ?? '?'}
                </div>
                <div className="mt-1 text-sm">
                  Here <b className="tabular">{bytes(current.size)}</b>
                  {e ? ` (${date(e.mTime)})` : ''} · on the backup <b className="tabular">{bytes(current.parentSize)}</b>
                  {current.parentSize !== undefined && current.parentSize !== current.size ? (
                    <span className="text-danger"> · sizes differ by {bytes(Math.abs(current.parentSize - current.size))}</span>
                  ) : current.state === 'corrupt' ? (
                    <span className="text-danger"> · same size, other content</span>
                  ) : null}
                  {e?.corruptAt ? <span className="text-danger"> · changed without a new date on {date(e.corruptAt)} (integrity check)</span> : null}
                </div>
              </div>
              <button className="btn-ghost px-2 py-1" disabled={index === 0} onClick={() => go(index - 1)} title="Previous file (←)" aria-label="Previous file">
                ‹
              </button>
              <button className="btn-ghost px-2 py-1" disabled={index >= items.length - 1} onClick={() => go(index + 1)} title="Next file (→)" aria-label="Next file">
                ›
              </button>
            </div>

            <div className="mb-2 flex flex-wrap items-center gap-2">
              <button className={choiceBtn('replace')} disabled={busy || readOnly} onClick={() => choose('replace')} title="Download the backup's copy in its place; the damaged file goes to the trash (key 1)">
                <kbd className="opacity-60">1</kbd> 🛟 Replace
              </button>
              <button className={choiceBtn('aside')} disabled={busy || readOnly} onClick={() => choose('aside')} title="Download the backup's copy in its place; the damaged file is kept under the name on the right (key 2)">
                <kbd className="opacity-60">2</kbd> 🛟 Replace, keep damaged as
              </button>
              <input className="input w-48 py-1 font-mono text-xs" value={keepAs} onChange={(ev) => setKeepAs(ev.target.value)} aria-label="Where the damaged file is kept" title="Path template, relative to the root" />
              <button className={choiceBtn('keep')} disabled={busy} onClick={() => choose('keep')} title="The file here is right: nothing is done with it (key 3)">
                <kbd className="opacity-60">3</kbd> ✓ Keep
              </button>
              <button className="btn-ghost" disabled={busy} onClick={() => go(nextUndecided(index))} title="Decide later: go to the next undecided file (key S)">
                <kbd className="opacity-60">S</kbd> Skip
              </button>
              <span className="flex-1" />
              <button className="btn-ghost text-xs" disabled={busy || readOnly} onClick={() => other.open(1)} title="Trash, move or delete this file instead">
                Other action…
              </button>
              {e && (
                <a className="btn-ghost text-xs" href={fileUrl(e.id, 'content', { download: true })}>
                  Download
                </a>
              )}
            </div>
            <div className="mb-4 flex flex-wrap items-center gap-2 text-sm">
              {readOnly && <span className="text-amber">The root is read-only: the file cannot be replaced.</span>}
              {d ? (
                <>
                  <span className={clsx(d.kept ? 'text-accent' : !d.included ? 'text-amber' : 'text-success')}>Decision: {decisionText(d)}</span>
                  <button className="link text-xs" disabled={busy} onClick={() => choose('clear')}>
                    Clear
                  </button>
                </>
              ) : (
                <span className="muted">Not decided yet.</span>
              )}
            </div>

            <Tabs<View>
              value={view}
              onChange={(v) => setView(v, { replace: true })}
              tabs={[
                { id: 'side', label: 'Side by side' },
                { id: 'why', label: 'What happened?' },
              ]}
            />
            {view === 'why' ? (
              <ComparePanel key={current.entryId} entryId={current.entryId} parentName={parentName} />
            ) : detail.isLoading ? (
              <Loading />
            ) : detail.error ? (
              <ErrorBox error={detail.error} />
            ) : (
              e && <BackupCompareView key={e.id} entry={e} />
            )}
          </div>
        )}
      </div>

      {current && (
        <PlanDialog
          open={other.value !== null}
          onClose={other.close}
          entryIds={[current.entryId]}
          count={1}
          size={current.size}
          title={`Other action: ${current.relPath.slice(current.relPath.lastIndexOf('/') + 1)}`}
          target={{ planId: plan?.id, onDone: (id) => id !== planId && setPlanId(id) }}
        />
      )}
    </>
  )
}
