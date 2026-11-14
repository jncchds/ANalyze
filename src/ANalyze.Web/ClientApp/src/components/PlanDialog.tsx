import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { useConfig, useIntegrations, useTemplates } from '../api/hooks'
import type { ActionOp, ConflictPolicy, PlanRow, SearchFilter } from '../api/types'
import { bytes, count as fmt } from '../lib/format'
import { KEEP_DAMAGED_DEFAULT, keepDamaged, lastPlan, useDraftPlans } from '../lib/plans'
import { leaveOverlay } from '../hooks/useUrlState'
import { ErrorBox, Field, Modal } from './ui'

interface Props {
  open: boolean
  onClose: () => void
  entryIds?: number[]
  filter?: SearchFilter
  /** Every damaged file the backup has a copy of (instead of a selection or a filter): only "replace with the backup's copy". */
  damaged?: boolean
  count?: number
  size?: number
  defaultOp?: ActionOp
  title?: string
  /**
   * The caller collects items in a plan of its own (the damaged-files review): no plan to choose, and instead of opening the plan
   * the dialog reports the plan the items went to and closes.
   */
  target?: { planId?: number; onDone: (planId: number) => void }
}

interface Created {
  id: number
  name: string
  items: number
  included: number
}

/**
 * Plans an action for a selection (entry ids), a filter or every damaged file: a new draft plan, or more items for a draft plan
 * that exists. Nothing changes on disk here: plans are reviewed and applied on their own page.
 */
export function PlanDialog(props: Props) {
  return (
    <Modal open={props.open} onClose={props.onClose} title={props.title ?? (props.damaged ? 'Corrupted files' : 'Plan an action')}>
      <PlanForm {...props} />
    </Modal>
  )
}

/** "Add to": a new plan or one of the draft plans. Hidden while there is no draft. */
export function PlanTargetField({ drafts, value, onChange }: { drafts?: PlanRow[]; value?: number; onChange: (id: number | null) => void }) {
  if (!drafts?.length) return null
  return (
    <Field label="Add to">
      <select className="input" value={value ?? ''} onChange={(e) => onChange(e.target.value ? Number(e.target.value) : null)}>
        <option value="">A new plan</option>
        {drafts.map((p) => (
          <option key={p.id} value={p.id}>
            #{p.id} {p.name} · {fmt(p.items)} item(s)
          </option>
        ))}
      </select>
    </Field>
  )
}

/** What happens to the damaged file when the backup's copy replaces it: the trash, or kept under another name. */
export function KeepDamagedField({ keep, onKeep, template, onTemplate }: { keep: boolean; onKeep: (v: boolean) => void; template: string; onTemplate: (v: string) => void }) {
  return (
    <fieldset className="space-y-1.5">
      <legend className="label">The damaged file</legend>
      <label className="flex cursor-pointer items-center gap-2 text-sm">
        <input type="radio" className="h-4 w-4 accent-accent" checked={!keep} onChange={() => onKeep(false)} />
        goes to the trash (emptied after the retention period)
      </label>
      <label className="flex cursor-pointer flex-wrap items-center gap-2 text-sm">
        <input type="radio" className="h-4 w-4 accent-accent" checked={keep} onChange={() => onKeep(true)} />
        is kept, just in case, as
        <input
          className="input w-auto min-w-48 flex-1 py-1 font-mono"
          value={template}
          onFocus={() => onKeep(true)}
          onChange={(e) => onTemplate(e.target.value)}
          aria-label="Where the damaged file is kept"
        />
      </label>
      {keep && (
        <span className="block text-xs muted">
          Relative to the root, with the tokens of a move: <code>{KEEP_DAMAGED_DEFAULT}</code> puts it next to the restored file as <code>~name</code>; a plain
          folder keeps the names. A taken name gets a number.
        </span>
      )}
    </fieldset>
  )
}

function PlanForm({ onClose, entryIds, filter, damaged, count, size, defaultOp = 'trash', target }: Props) {
  const templates = useTemplates()
  const config = useConfig()
  const navigate = useNavigate()
  const qc = useQueryClient()
  // "Replace with the backup's copy" needs the backup server (the ANalyze parent integration).
  const backup = !!useIntegrations().data?.some((i) => i.id === 'parent' && i.settings.enabled)
  const ops: ActionOp[] = damaged ? ['restore'] : ['trash', 'move', 'delete', ...(backup || defaultOp === 'restore' ? ['restore' as const] : [])]
  const drafts = useDraftPlans()
  const [chosen, setChosen] = useState(() => lastPlan.get())
  const planId = target ? target.planId : drafts.data?.some((p) => p.id === chosen) ? chosen! : undefined
  const plan = drafts.data?.find((p) => p.id === planId)
  const [templateId, setTemplateId] = useState<number | ''>('')
  const [op, setOp] = useState<ActionOp>(damaged ? 'restore' : defaultOp)
  const [destination, setDestination] = useState('Archive/{relDir}/{name}')
  const [conflict, setConflict] = useState<ConflictPolicy>('rename')
  const [name, setName] = useState('')
  const [keep, setKeep] = useState(() => keepDamaged.get() !== null)
  const [keepAs, setKeepAs] = useState(() => keepDamaged.get() ?? KEEP_DAMAGED_DEFAULT)
  const [added, setAdded] = useState<Created>()

  const tpl = damaged ? undefined : templates.data?.find((t) => t.id === templateId)
  const effectiveOp = tpl?.op ?? op
  const keeping = effectiveOp === 'restore' && keep && keepAs.trim() !== ''

  const create = useMutation({
    mutationFn: () => {
      const common = { name: planId ? undefined : name || undefined, planId, keepDamaged: keeping ? keepAs.trim() : undefined }
      if (damaged) return api<Created>('/plans/restore-damaged', { method: 'POST', json: common })
      const body = { ...common, op: effectiveOp, destination: tpl ? undefined : destination, conflict: plan ? plan.conflict : conflict, templateId: templateId || undefined }
      return entryIds
        ? api<Created>('/plans', { method: 'POST', json: { ...body, entryIds } })
        : api<Created>('/plans/from-filter', { method: 'POST', json: { ...body, filter } })
    },
    onSuccess: (p) => {
      if (effectiveOp === 'restore') keepDamaged.set(keeping ? keepAs.trim() : null)
      qc.invalidateQueries({ queryKey: ['summary'] })
      qc.invalidateQueries({ queryKey: ['plans'] })
      qc.invalidateQueries({ queryKey: ['plan'] })
      if (target) {
        target.onDone(p.id)
        return onClose()
      }
      lastPlan.set(planId ?? null)
      // A new plan opens for review; with an existing one you are probably still collecting.
      if (planId) setAdded(p)
      else leaveOverlay(navigate, `/actions/${p.id}`)
    },
  })

  if (added)
    return (
      <div className="space-y-4">
        <p className="text-sm">
          Added {fmt(added.items)} item(s) to <b>{added.name}</b>
          {added.included < added.items ? ` — ${fmt(added.items - added.included)} of them left out with a warning (see the plan)` : ''}. Nothing is changed until
          you apply the plan.
        </p>
        <div className="flex justify-end gap-2">
          <Link className="btn-secondary" to={`/actions/${added.id}`} replace>
            Open the plan
          </Link>
          <button className="btn-primary" onClick={onClose}>
            Done
          </button>
        </div>
      </div>
    )

  return (
    <div className="space-y-4">
      <p className="text-sm muted">
        {count !== undefined ? `${count.toLocaleString()} item(s)` : damaged ? 'Every corrupted file the backup has a copy of' : 'Selected items'}
        {size !== undefined ? ` · ${bytes(size)}` : ''}.{' '}
        {planId ? 'They are added to a draft plan' : 'A draft plan is created for review'} — nothing is changed until you apply it.
      </p>
      {!target && <PlanTargetField drafts={drafts.data} value={planId} onChange={setChosen} />}
      {planId !== undefined && (
        <p className="text-xs muted">Items the plan already has for the same files are replaced: the latest choice wins.</p>
      )}
      {!damaged && (
        <Field label="Template">
          <select className="input" value={templateId} onChange={(e) => setTemplateId(e.target.value ? Number(e.target.value) : '')}>
            <option value="">Custom…</option>
            {templates.data?.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
                {t.destination ? ` → ${t.destination}` : ''}
              </option>
            ))}
          </select>
        </Field>
      )}
      {!tpl && (
        <>
          {ops.length > 1 && (
            <Field label="Action">
              <div className="flex flex-wrap gap-2">
                {ops.map((o) => (
                  <label key={o} className={`btn ${op === o ? 'btn-primary' : 'btn-secondary'} cursor-pointer`}>
                    <input type="radio" className="sr-only" checked={op === o} onChange={() => setOp(o)} />
                    {o === 'trash' ? '🗑️ Move to trash' : o === 'move' ? '📦 Move / rename' : o === 'delete' ? '⚠️ Delete permanently' : "🛟 Replace with the backup's copy"}
                  </label>
                ))}
              </div>
            </Field>
          )}
          {op === 'move' && (
            <Field label="Destination (relative to the root)" hint={<>Tokens: {config.data?.pathTokens.map((t) => `{${t}}`).join(' ')} — e.g. {'{mtime:yyyy}'}. A plain folder keeps the names.</>}>
              <input className="input font-mono" value={destination} onChange={(e) => setDestination(e.target.value)} />
            </Field>
          )}
          {op === 'restore' && (
            <p className="rounded-md bg-surface-2 p-2 text-sm muted">
              Damaged files (other size or content than on the backup, or flagged by the integrity check) are replaced with the backup server's copy, checked
              against its size and content hash. The plan can be undone. Selected folders stand for the damaged files inside them; files that are not damaged
              are listed but left out.
            </p>
          )}
          {op === 'delete' && <p className="rounded-md bg-danger/10 p-2 text-sm text-danger">Permanent deletion cannot be undone. Prefer the trash unless space is needed right now.</p>}
        </>
      )}
      {effectiveOp === 'restore' && <KeepDamagedField keep={keep} onKeep={setKeep} template={keepAs} onTemplate={setKeepAs} />}
      {effectiveOp === 'move' && (
        <Field label="When the destination exists" hint={plan ? 'A plan has one rule for all its moves: this is the one of the plan you add to.' : undefined}>
          <select className="input" value={plan ? plan.conflict : conflict} disabled={!!plan} onChange={(e) => setConflict(e.target.value as ConflictPolicy)}>
            <option value="rename">Rename (add “(2)”)</option>
            <option value="skip">Skip the item</option>
            <option value="overwrite">Overwrite (files only)</option>
          </select>
        </Field>
      )}
      {planId === undefined && (
        <Field label="Plan name (optional)">
          <input className="input" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
      )}
      {create.error && <ErrorBox error={create.error} />}
      <div className="flex justify-end gap-2">
        <button className="btn-secondary" onClick={onClose}>
          Cancel
        </button>
        <button className="btn-primary" onClick={() => create.mutate()} disabled={create.isPending}>
          {planId !== undefined ? 'Add to the plan' : 'Create plan for review'}
        </button>
      </div>
    </div>
  )
}
