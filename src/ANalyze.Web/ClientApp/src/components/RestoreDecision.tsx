import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../api/client'
import { KEEP_DAMAGED_DEFAULT, decisionText, keepDamaged, useDraftPlans, usePlanDecisions, useReviewPlan } from '../lib/plans'
import { ErrorBox } from './ui'

type Choice = 'replace' | 'aside' | 'keep' | 'clear'

/**
 * The decision about one corrupted file the backup has a copy of, in the file panel: replace it with the backup's copy (damaged
 * file to the trash, or kept under another name) or keep the file here. A decision is saved at once as an item of a draft plan
 * (`fixedPlan`, or the one chosen here; a new one is made by the first decision), so nothing changes on disk until that plan is
 * applied. Keys 1 / 2 / 3 decide; `onDecided` lets the panel step to the next file.
 */
export function RestoreDecision({ entryId, readOnly, fixedPlan, disabled, onDecided }: {
  entryId: number
  readOnly?: boolean
  /** The plan the panel was opened from (its review page): decisions go there. */
  fixedPlan?: number
  /** A dialog is open over the panel: keys are not for us. */
  disabled?: boolean
  onDecided?: () => void
}) {
  const qc = useQueryClient()
  const [stored, setStored] = useReviewPlan()
  const planId = fixedPlan ?? stored
  const drafts = useDraftPlans(fixedPlan === undefined)
  const { plan, byEntry, waiting } = usePlanDecisions(planId)
  const [keepAs, setKeepAs] = useState(() => keepDamaged.get() ?? KEEP_DAMAGED_DEFAULT)
  const d = byEntry.get(entryId)

  const decide = useMutation({
    mutationFn: async (choice: Choice) => {
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
    onSuccess: (id, choice) => {
      if (id !== undefined && id !== planId && fixedPlan === undefined) setStored(id)
      if (choice === 'aside') keepDamaged.set(keepAs.trim() || KEEP_DAMAGED_DEFAULT)
      qc.invalidateQueries({ queryKey: ['plan'] })
      qc.invalidateQueries({ queryKey: ['plans'] })
      qc.invalidateQueries({ queryKey: ['summary'] })
      if (choice !== 'clear') onDecided?.()
    },
  })

  const busy = decide.isPending || waiting
  const choose = (c: Choice) => !busy && (c === 'clear' || c === 'keep' || !readOnly) && decide.mutate(c)

  // The handler reads the latest state through a ref, so the listener is added once.
  const keys = useRef<(e: KeyboardEvent) => void>(() => {})
  useEffect(() => {
    keys.current = (e) => {
      const t = e.target as HTMLElement | null
      if (disabled || e.altKey || e.ctrlKey || e.metaKey || t?.closest('input, textarea, select, video, audio, dialog, [contenteditable="true"]')) return
      if (e.key === '1') choose('replace')
      else if (e.key === '2') choose('aside')
      else if (e.key === '3') choose('keep')
      else return
      e.preventDefault()
    }
  })
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => keys.current(e)
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const is = (c: Choice) => !!d && (c === 'keep' ? d.kept : d.included && d.op === 'restore' && (c === 'aside') === !!d.keepPath)
  const btn = (c: Choice) => clsx('btn px-2.5 py-1 text-sm', is(c) ? 'btn-primary' : 'btn-secondary')

  return (
    <div className="space-y-2 rounded-md bg-surface-2 p-3 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <button className={btn('replace')} disabled={busy || readOnly} onClick={() => choose('replace')} title="Download the backup's copy in its place; the damaged file goes to the trash (key 1)">
          <kbd className="opacity-60">1</kbd> 🛟 Replace
        </button>
        <button className={btn('aside')} disabled={busy || readOnly} onClick={() => choose('aside')} title="Download the backup's copy in its place; the damaged file is kept under the name on the right (key 2)">
          <kbd className="opacity-60">2</kbd> 🛟 Replace, keep damaged as
        </button>
        <input className="input w-44 py-1 font-mono text-xs" value={keepAs} onChange={(ev) => setKeepAs(ev.target.value)} aria-label="Where the damaged file is kept" title="Path template, relative to the root" />
        <button className={btn('keep')} disabled={busy} onClick={() => choose('keep')} title="The file here is right: nothing is done with it (key 3)">
          <kbd className="opacity-60">3</kbd> ✓ Keep
        </button>
      </div>
      <div className="flex flex-wrap items-center gap-2 text-xs">
        {readOnly ? (
          <span className="text-amber">The root is read-only: the file cannot be replaced.</span>
        ) : d ? (
          <>
            <span className={clsx('break-all', d.kept ? 'text-accent' : !d.included ? 'text-amber' : 'text-success')}>Decided: {decisionText(d)}</span>
            <button className="link" disabled={busy} onClick={() => choose('clear')}>
              Clear
            </button>
          </>
        ) : (
          <span className="muted">Not decided. Nothing changes until the plan is applied.</span>
        )}
        <span className="flex-1" />
        {fixedPlan === undefined && (
          <select
            className="input w-auto max-w-56 py-0.5 text-xs"
            value={plan?.id ?? ''}
            onChange={(ev) => setStored(ev.target.value ? Number(ev.target.value) : null)}
            aria-label="Plan the decisions go to"
            title="The draft plan the decisions go to"
          >
            <option value="">A new plan</option>
            {plan && !drafts.data?.some((p) => p.id === plan.id) && <option value={plan.id}>#{plan.id} {plan.name}</option>}
            {drafts.data?.map((p) => (
              <option key={p.id} value={p.id}>
                #{p.id} {p.name}
              </option>
            ))}
          </select>
        )}
        {plan && fixedPlan === undefined && (
          <Link className="link" to={`/actions/${plan.id}`}>
            Open the plan →
          </Link>
        )}
      </div>
      {decide.error && <ErrorBox error={decide.error} />}
    </div>
  )
}
