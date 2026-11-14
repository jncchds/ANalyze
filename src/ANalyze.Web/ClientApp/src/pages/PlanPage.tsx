import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../api/client'
import { useRoots } from '../api/hooks'
import type { PlanDetail, PlanItem } from '../api/types'
import { EntryDrawer } from '../components/EntryDrawer'
import { ConfirmButton, Empty, ErrorBox, Loading, PageTitle, Progress, Stat } from '../components/ui'
import { isActive, useJob } from '../hooks/useJobs'
import { bytes, count, dateTime } from '../lib/format'
import { useEntryPreview, useUrlNumber, useUrlParam } from '../hooks/useUrlState'
import { itemAction } from '../lib/plans'
import { STATUS_STYLE } from './Actions'

const PAGE = 200
const FILTERS = [
  { id: 'all', label: 'All' },
  { id: 'included', label: 'Included' },
  { id: 'excluded', label: 'Excluded' },
  { id: 'warnings', label: 'Warnings' },
  { id: 'failed', label: 'Skipped / failed' },
  { id: 'done', label: 'Done' },
]

/** The item's file is still where the plan found it (not trashed, deleted or moved away), so it can be opened. */
const inPlace = (i: PlanItem) => !!i.entryId && (i.status !== 'done' || i.op === 'restore' || i.op === 'hardlink')

/**
 * Review screen: untick items to keep them, then apply. Applied plans show results and can be undone. Files open in the file panel
 * (previous/next through the page's items); for corrupted files it holds the decision, saved in this plan while it is a draft.
 */
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
  const preview = useEntryPreview()
  const running = isActive(job) && job?.refId === Number(id)
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
  const removeItem = useMutation({
    mutationFn: (itemId: number) => api(`/plans/${id}/items/remove`, { method: 'POST', json: { ids: [itemId] } }),
    onSuccess: refresh,
  })
  const [renaming, setRenaming] = useState<string>()
  const rename = useMutation({
    mutationFn: (name: string) => api(`/plans/${id}`, { method: 'PATCH', json: { name } }),
    onSuccess: () => {
      setRenaming(undefined)
      qc.invalidateQueries({ queryKey: ['plans'] })
      refresh()
    },
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
  const { plan, summary, page } = q.data!
  const draft = plan.status === 'draft'
  const rootName = (rid: number) => roots.data?.find((r) => r.id === rid)?.name ?? '?'
  const hasDelete = page.items.some((i) => i.op === 'delete')
  // The files of this page the panel can show, in order.
  const viewable = page.items.filter((i) => !i.isDir && inPlace(i))
  const viewIds = viewable.map((i) => i.entryId!)
  const firstRestore = viewable.find((i) => i.op === 'restore')

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
            {draft && firstRestore && (
              <button className="btn-secondary" onClick={() => preview.open(firstRestore.entryId!, 'backup')} title="Opens the first file next to the backup's copy; decide there and step through the items with ‹ ›">
                Review one by one
              </button>
            )}
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
        {renaming !== undefined ? (
          <form
            className="flex items-center gap-2"
            onSubmit={(e) => {
              e.preventDefault()
              if (renaming.trim()) rename.mutate(renaming.trim())
            }}
          >
            <input className="input max-w-md text-base font-normal" value={renaming} autoFocus onChange={(e) => setRenaming(e.target.value)} onKeyDown={(e) => e.key === 'Escape' && setRenaming(undefined)} aria-label="Plan name" />
            <button className="btn-primary text-sm font-normal" disabled={rename.isPending || !renaming.trim()}>
              Save
            </button>
            <button type="button" className="btn-ghost text-sm font-normal" onClick={() => setRenaming(undefined)}>
              Cancel
            </button>
          </form>
        ) : (
          <>
            {plan.name}{' '}
            <button className="btn-ghost px-1.5 py-0.5 align-middle text-sm font-normal" onClick={() => setRenaming(plan.name)} title="Rename the plan" aria-label="Rename the plan">
              ✎
            </button>
          </>
        )}
      </PageTitle>
      {(apply.error || undo.error || remove.error || rename.error || removeItem.error) && <ErrorBox error={apply.error ?? undo.error ?? remove.error ?? rename.error ?? removeItem.error} />}
      {draft && (
        <p className="mb-3 text-sm muted">
          A draft takes more items: choose this plan under “Add to” in any “Clean up…” dialog. Untick an item to leave it out, or remove it with ✕.
        </p>
      )}
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
          </>
        )}
      </div>

      {page.items.length === 0 ? (
        <Empty>No items.</Empty>
      ) : (
        <div className="card divide-y divide-line/60">
          {page.items.map((i) => (
            <div key={i.id} className={clsx('flex items-start gap-3 px-3 py-2 text-sm', !i.included && 'opacity-50')}>
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
                  {inPlace(i) ? (
                    <Link to={`/browse/${i.entryId}`} className="hover:text-accent hover:underline" title="Open in the file browser">
                      {i.sourcePath}
                    </Link>
                  ) : (
                    i.sourcePath
                  )}
                </div>
                {i.targetPath && <div className="break-all text-xs text-accent">{i.op === 'restore' ? '← from ' : '→ '}{i.targetPath}</div>}
                {i.keepPath && (
                  <div className="break-all text-xs muted">
                    {i.op === 'restore' ? 'damaged file kept as' : i.op === 'hardlink' ? 'linked to' : 'kept copy:'} {i.keepPath}
                  </div>
                )}
                {i.warning && <div className="text-xs text-warning">⚠ {i.warning}</div>}
                {i.error && <div className="text-xs text-danger">{i.error}</div>}
              </div>
              {!i.isDir && inPlace(i) && (
                <button className="btn-ghost shrink-0 px-2 py-0 text-xs" onClick={() => preview.open(i.entryId!, i.op === 'restore' ? 'backup' : undefined)} title="Open the file panel: preview, comparison with the backup, decision">
                  View
                </button>
              )}
              <span className="shrink-0 text-xs muted">{itemAction(i)}</span>
              {i.status !== 'pending' && <span className={clsx('shrink-0 rounded px-1.5 text-xs', i.status === 'done' ? 'bg-success/10 text-success' : i.status === 'undone' ? 'bg-muted/10 text-muted' : 'bg-danger/10 text-danger')}>{i.status}</span>}
              <span className="w-20 shrink-0 text-right tabular">{bytes(i.expectedSize)}</span>
              {draft && (
                <button className="btn-ghost shrink-0 px-1.5 py-0 text-xs" onClick={() => removeItem.mutate(i.id)} disabled={removeItem.isPending} title="Remove from the plan" aria-label="Remove from the plan">
                  ✕
                </button>
              )}
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
      <EntryDrawer preview={preview} list={viewIds} planId={draft ? plan.id : undefined} />
    </>
  )
}
