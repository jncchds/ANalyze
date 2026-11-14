import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../api/client'
import { useRoots } from '../api/hooks'
import type { ActionOp, PlanAdded, PlanDetail, PlanItem } from '../api/types'
import { ActionFields, actionBody, usePlanOps, useKeepLocal } from '../components/PlanDialog'
import { ConfirmButton, Empty, ErrorBox, Loading, PageTitle, Progress, Stat } from '../components/ui'
import { isActive, useJob } from '../hooks/useJobs'
import { bytes, count, dateTime } from '../lib/format'
import { useUrlNumber, useUrlParam } from '../hooks/useUrlState'
import { OP_LABEL, STATUS_STYLE } from './Actions'

/** Another action for one item (`ids`) or every item a filter shows: the items are planned again from their files. */
function ChangeAction({ planId, ids, filter, q, initial, onDone }: { planId: number; ids?: number[]; filter?: string; q?: string; initial?: PlanItem; onDone: () => void }) {
  const qc = useQueryClient()
  const ops = usePlanOps(initial?.op)
  const [op, setOp] = useState<ActionOp>(initial && initial.op !== 'hardlink' ? initial.op : 'trash')
  const [destination, setDestination] = useState(initial?.op === 'move' && initial.targetPath ? initial.targetPath : 'Archive/{relDir}/{name}')
  const keep = useKeepLocal()
  const change = useMutation({
    mutationFn: () => api<PlanAdded>(`/plans/${planId}/items/change`, { method: 'POST', json: { ids, filter, q: q || undefined, ...actionBody(op, destination, keep.keep) } }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['plan', String(planId)] })
      qc.invalidateQueries({ queryKey: ['plans'] })
      qc.invalidateQueries({ queryKey: ['entry'] })
      onDone()
    },
  })
  return (
    <div className="space-y-3 border-t border-line/60 bg-surface-2/50 px-3 py-3">
      <ActionFields ops={ops} op={op} setOp={setOp} destination={destination} setDestination={setDestination} keep={keep} />
      {change.error && <ErrorBox error={change.error} />}
      <div className="flex justify-end gap-2">
        <button className="btn-secondary" onClick={onDone}>
          Cancel
        </button>
        <button className="btn-primary" onClick={() => change.mutate()} disabled={change.isPending}>
          Change action
        </button>
      </div>
    </div>
  )
}

const PAGE = 200
const FILTERS = [
  { id: 'all', label: 'All' },
  { id: 'included', label: 'Included' },
  { id: 'excluded', label: 'Excluded' },
  { id: 'warnings', label: 'Warnings' },
  { id: 'failed', label: 'Skipped / failed' },
  { id: 'done', label: 'Done' },
]

/** Review screen: untick items to keep them, then apply. Applied plans show results and can be undone. */
export default function PlanPage() {
  const { id } = useParams()
  const qc = useQueryClient()
  const navigate = useNavigate()
  const roots = useRoots()
  const job = useJob('apply')
  // Filter, path filter and page are in the URL, so Back steps through them.
  const [filter, setFilter] = useUrlParam('filter', 'all')
  const [search, setSearch] = useUrlParam('q')
  const [offset, setOffset] = useUrlNumber('offset')
  const running = isActive(job) && job?.refId === Number(id)
  // The item whose action is being changed, or 'shown' for every item the filter shows.
  const [editing, setEditing] = useState<number | 'shown' | null>(null)
  const q = useQuery({
    queryKey: ['plan', id, filter, search, offset],
    queryFn: () => api<PlanDetail>(`/plans/${id}`, { query: { filter, q: search, offset, limit: PAGE } }),
    refetchInterval: running ? 1500 : false,
  })
  const refresh = () => qc.invalidateQueries({ queryKey: ['plan', id] })
  const setItems = useMutation({
    mutationFn: (body: { ids?: number[]; included: boolean; filter?: string }) => api(`/plans/${id}/items`, { method: 'PATCH', json: body }),
    onSuccess: refresh,
  })
  const apply = useMutation({ mutationFn: () => api(`/plans/${id}/apply`, { method: 'POST' }), onSuccess: refresh })
  const undo = useMutation({ mutationFn: () => api(`/plans/${id}/undo`, { method: 'POST' }), onSuccess: refresh })
  const cancel = useMutation({ mutationFn: () => api('/plans/cancel', { method: 'POST' }) })
  const remove = useMutation({
    mutationFn: () => api(`/plans/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['plans'] })
      navigate('/actions', { replace: true })
    },
  })

  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const { plan, summary, page, parents = {} } = q.data!
  const draft = plan.status === 'draft'
  const rootName = (rid: number) => roots.data?.find((r) => r.id === rid)?.name ?? '?'
  const hasDelete = page.items.some((i) => i.op === 'delete')

  return (
    <>
      <PageTitle
        subtitle={
          <>
            <Link to="/actions" className="link">
              ← Plans
            </Link>{' '}
            · created {dateTime(plan.createdAt)}
            {plan.appliedAt ? ` · applied ${dateTime(plan.appliedAt)}` : ''}
          </>
        }
        actions={
          <>
            <span className={clsx('rounded px-2 py-0.5 text-xs font-semibold', STATUS_STYLE[plan.status])}>{plan.status}</span>
            {(draft || plan.status === 'cancelled' || plan.status === 'failed') && (
              <ConfirmButton
                className="btn-primary"
                disabled={summary.included === 0 || running}
                confirmLabel={`Apply ${count(summary.included)} items now?`}
                onConfirm={() => apply.mutate()}
              >
                Apply {count(summary.included)} items ({bytes(summary.bytes)})
              </ConfirmButton>
            )}
            {running && (
              <button className="btn-secondary" onClick={() => cancel.mutate()}>
                Stop
              </button>
            )}
            {!draft && !running && summary.undoable > 0 && (
              <ConfirmButton className="btn-secondary" confirmLabel="Restore everything?" onConfirm={() => undo.mutate()}>
                Undo ({count(summary.undoable)})
              </ConfirmButton>
            )}
            {!running && (
              <ConfirmButton className="btn-ghost" confirmLabel="Delete plan?" onConfirm={() => remove.mutate()}>
                Delete plan
              </ConfirmButton>
            )}
          </>
        }
      >
        {plan.name}
      </PageTitle>
      {(apply.error || undo.error || remove.error) && <ErrorBox error={apply.error ?? undo.error ?? remove.error} />}
      {draft && hasDelete && (
        <div className="mb-3 rounded-md bg-danger/10 p-3 text-sm text-danger">This plan deletes files permanently. Consider recreating it with “Move to trash”.</div>
      )}
      {running && job && (
        <div className="card mb-4 p-3">
          <div className="mb-1 flex text-sm">
            <span className="flex-1">{job.phase === 'undo' ? 'Restoring' : 'Applying'}… {job.current}</span>
            <span className="tabular">
              {count(job.done)} / {count(job.total)}
            </span>
          </div>
          <Progress value={job.done} max={job.total} />
        </div>
      )}
      {plan.message && !running && <p className="mb-3 text-sm muted">{plan.message}</p>}

      <div className="mb-4 grid grid-cols-2 gap-3 md:grid-cols-4">
        <Stat label="Included" value={`${count(summary.included)} / ${count(summary.items)}`} />
        <Stat label={draft ? 'Will free / move' : 'Processed size'} value={bytes(summary.bytes)} tone="amber" />
        <Stat label="Warnings" value={count(summary.warnings)} />
        <Stat label="Done · skipped · failed" value={`${count(summary.done)} · ${count(summary.skipped)} · ${count(summary.failed)}`} hint={summary.undone ? `${count(summary.undone)} undone` : undefined} />
      </div>

      <div className="mb-2 flex flex-wrap items-center gap-2">
        {FILTERS.map((f) => (
          <button
            key={f.id}
            className={clsx('btn px-2.5 py-1 text-xs', filter === f.id ? 'bg-accent-faint text-accent' : 'text-muted hover:text-fg')}
            onClick={() => setFilter(f.id, { also: { offset: null } })}
          >
            {f.label}
          </button>
        ))}
        <input
          className="input ml-auto max-w-xs py-1"
          placeholder="Filter paths…"
          value={search}
          onChange={(e) => setSearch(e.target.value, { replace: true, also: { offset: null } })}
        />
        {draft && (
          <>
            <button className="btn-ghost px-2 py-1 text-xs" onClick={() => setItems.mutate({ included: true, filter })}>
              Include shown
            </button>
            <button className="btn-ghost px-2 py-1 text-xs" onClick={() => setItems.mutate({ included: false, filter })}>
              Exclude shown
            </button>
            <button className={clsx('btn-ghost px-2 py-1 text-xs', editing === 'shown' && 'bg-accent-faint text-accent')} onClick={() => setEditing(editing === 'shown' ? null : 'shown')}>
              Change action of shown…
            </button>
          </>
        )}
      </div>
      {editing === 'shown' && (
        <div className="card mb-3">
          <p className="px-3 pt-2 text-sm muted">
            All {count(page.total)} item(s) the filter shows get the new action{search ? ` (paths containing “${search}”)` : ''}.
          </p>
          <ChangeAction planId={plan.id} filter={filter} q={search} initial={page.items[0]} onDone={() => setEditing(null)} />
        </div>
      )}

      {page.items.length === 0 ? (
        <Empty>No items.</Empty>
      ) : (
        <div className="card divide-y divide-line/60">
          {page.items.map((i) => (
            <div key={i.id}>
              <div className={clsx('flex items-start gap-3 px-3 py-2 text-sm', !i.included && 'opacity-50')}>
                <input
                  type="checkbox"
                  className="mt-0.5 h-4 w-4 accent-accent"
                  checked={i.included}
                  disabled={!draft || i.warning === 'Root is read-only'}
                  onChange={(e) => setItems.mutate({ ids: [i.id], included: e.target.checked })}
                  aria-label="Include"
                />
                <div className="min-w-0 flex-1">
                  <div className="break-all">
                    <span className="muted">{rootName(i.rootId)}/</span>
                    {i.isDir ? '📁 ' : ''}
                    {i.sourcePath}
                  </div>
                  {i.targetPath && <div className="break-all text-xs text-accent">{i.op === 'restore' ? '← from ' : '→ '}{i.targetPath}</div>}
                  {i.keepPath && (
                    <div className="break-all text-xs muted">
                      {i.op === 'hardlink' ? 'linked to' : i.op === 'restore' ? 'the local file is kept as' : 'kept copy:'} {i.keepPath}
                    </div>
                  )}
                  {i.op === 'restore' && !i.keepPath && <div className="text-xs muted">the local file goes to the trash</div>}
                  {i.warning && <div className="text-xs text-warning">⚠ {i.warning}</div>}
                  {i.error && <div className="text-xs text-danger">{i.error}</div>}
                </div>
                {(() => {
                  const to = i.entryId === undefined ? undefined : i.isDir ? `/browse/${i.entryId}` : parents[i.entryId] ? `/browse/${parents[i.entryId]}?file=${i.entryId}` : undefined
                  return to ? (
                    <Link className="btn-ghost shrink-0 px-2 py-0.5 text-xs" to={to} title={i.isDir ? 'Open the folder in Browse' : 'Show the file in Browse'}>
                      Show
                    </Link>
                  ) : null
                })()}
                {draft && i.entryId !== undefined ? (
                  <button
                    className={clsx('btn-ghost shrink-0 px-2 py-0.5 text-xs', editing === i.id && 'bg-accent-faint text-accent')}
                    onClick={() => setEditing(editing === i.id ? null : i.id)}
                    title="Give this item another action"
                  >
                    {OP_LABEL[i.op] ?? i.op} ▾
                  </button>
                ) : (
                  <span className="shrink-0 text-xs muted">{i.op}</span>
                )}
                {i.status !== 'pending' && <span className={clsx('shrink-0 rounded px-1.5 text-xs', i.status === 'done' ? 'bg-success/10 text-success' : i.status === 'undone' ? 'bg-muted/10 text-muted' : 'bg-danger/10 text-danger')}>{i.status}</span>}
                <span className="w-20 shrink-0 text-right tabular">{bytes(i.expectedSize)}</span>
              </div>
              {editing === i.id && <ChangeAction planId={plan.id} ids={[i.id]} initial={i} onDone={() => setEditing(null)} />}
            </div>
          ))}
        </div>
      )}
      {page.total > PAGE && (
        <div className="mt-3 flex items-center justify-center gap-3">
          <button className="btn-secondary" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>
            ← Previous
          </button>
          <span className="text-sm muted">
            {offset + 1}–{Math.min(offset + PAGE, page.total)} of {count(page.total)}
          </span>
          <button className="btn-secondary" disabled={offset + PAGE >= page.total} onClick={() => setOffset(offset + PAGE)}>
            Next →
          </button>
        </div>
      )}
    </>
  )
}
