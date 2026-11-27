import { useState } from 'react'
import { useNavigate } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { useConfig, useIntegrations, useTemplates } from '../api/hooks'
import type { ActionOp, ConflictPolicy, PlanAdded, PlanRow, SearchFilter } from '../api/types'
import { bytes } from '../lib/format'
import { leaveOverlay } from '../hooks/useUrlState'
import { ErrorBox, Field, Modal } from './ui'

const KEEP_KEY = 'analyze.restore.keep'
export const DEFAULT_KEEP = '{relDir}/~{name}'

const read = (k: string) => {
  try {
    return localStorage.getItem(k)
  } catch {
    return null
  }
}
const write = (k: string, v: string | null) => {
  try {
    const s = localStorage
    if (v === null) s.removeItem(k)
    else s.setItem(k, v)
  } catch {
    // not stored: only a convenience
  }
}

/**
 * Where a restore keeps the local file: the last template used, or null for the trash (remembered in this browser; keeping it
 * as `{relDir}/~{name}` until chosen otherwise).
 */
export function useKeepLocal() {
  const [keep, setKeepState] = useState<string | null>(() => {
    const v = read(KEEP_KEY)
    return v === null ? DEFAULT_KEEP : v || null
  })
  const [template, setTemplate] = useState(() => read(KEEP_KEY) || DEFAULT_KEEP)
  const setKeep = (on: boolean, t = template) => {
    setTemplate(t)
    setKeepState(on ? t : null)
    write(KEEP_KEY, on ? t : '')
  }
  return { keep, template, setKeep }
}

export const OP_TEXT: Record<ActionOp, string> = {
  trash: '🗑️ Move to trash',
  move: '📦 Move / rename',
  delete: '⚠️ Delete permanently',
  restore: "🛟 Replace with the backup's copy",
  hardlink: '🔗 Hard link',
  extract: '🗜 Extract from the archive',
}

/** The actions a plan can take here: restore only with the backup server (the ANalyze parent integration). */
export function usePlanOps(always?: ActionOp): ActionOp[] {
  const backup = !!useIntegrations().data?.some((i) => i.id === 'parent' && i.settings.enabled)
  return ['trash', 'move', 'delete', ...(backup || always === 'restore' ? ['restore' as const] : [])]
}

/** Action, move destination and — for a restore — whether the local file is kept (e.g. as `{relDir}/~{name}`) instead of trashed. */
export function ActionFields({ ops, op, setOp, destination, setDestination, keep }: {
  ops: ActionOp[]
  op: ActionOp
  setOp: (o: ActionOp) => void
  destination: string
  setDestination: (d: string) => void
  keep: ReturnType<typeof useKeepLocal>
}) {
  const config = useConfig()
  const tokens = <>Tokens: {config.data?.pathTokens.map((t) => `{${t}}`).join(' ')}</>
  return (
    <>
      <Field label="Action">
        <div className="flex flex-wrap gap-2">
          {ops.map((o) => (
            <label key={o} className={`btn ${op === o ? 'btn-primary' : 'btn-secondary'} cursor-pointer`}>
              <input type="radio" className="sr-only" checked={op === o} onChange={() => setOp(o)} />
              {OP_TEXT[o]}
            </label>
          ))}
        </div>
      </Field>
      {op === 'move' && (
        <Field label="Destination (relative to the root)" hint={<>{tokens} — e.g. {'{mtime:yyyy}'}. A plain folder keeps the names.</>}>
          <input className="input font-mono" value={destination} onChange={(e) => setDestination(e.target.value)} />
        </Field>
      )}
      {op === 'restore' && (
        <div className="space-y-2 rounded-md bg-surface-2 p-2 text-sm">
          <p className="muted">
            Damaged files (other size or content than on the backup, or flagged by the integrity check) are replaced with the backup server's copy, checked
            against its size and content hash. Selected folders stand for the damaged files inside them; files that are not damaged are listed but left out.
          </p>
          <label className="flex items-center gap-2">
            <input type="radio" className="accent-accent" checked={keep.keep === null} onChange={() => keep.setKeep(false)} />
            The local file goes to the trash (the plan can be undone)
          </label>
          <label className="flex flex-wrap items-center gap-2">
            <input type="radio" className="accent-accent" checked={keep.keep !== null} onChange={() => keep.setKeep(true)} />
            Keep the local file as
            <input
              className="input w-64 py-1 font-mono"
              value={keep.template}
              onChange={(e) => keep.setKeep(true, e.target.value)}
              onFocus={() => keep.keep === null && keep.setKeep(true)}
              aria-label="Where the local file is kept"
            />
          </label>
          {keep.keep !== null && <p className="text-xs muted">{tokens}. An existing name gets “(2)”. Undo puts the local file back.</p>}
        </div>
      )}
      {op === 'delete' && <p className="rounded-md bg-danger/10 p-2 text-sm text-danger">Permanent deletion cannot be undone. Prefer the trash unless space is needed right now.</p>}
    </>
  )
}

/**
 * The draft plans and the one plans go to unless another is picked: with Settings → "Plan buttons add to" set to the last
 * changed draft, the draft whose items changed last; otherwise none (a new plan).
 */
export function usePlanTarget(enabled = true) {
  const config = useConfig()
  const plans = useQuery({ queryKey: ['plans'], queryFn: () => api<PlanRow[]>('/plans'), enabled })
  const drafts = plans.data?.filter((p) => p.status === 'draft') ?? []
  const changed = (p: PlanRow) => Date.parse(p.updatedAt ?? p.createdAt)
  const fallback = config.data?.planTarget === 'lastDraft' ? drafts.reduce<PlanRow | undefined>((a, p) => (!a || changed(p) > changed(a) ? p : a), undefined) : undefined
  return { drafts, fallback, loading: plans.isLoading || config.isLoading }
}

/** After planning: lists, counters and the entry panel show the new items. */
export function usePlanAdded() {
  const qc = useQueryClient()
  return (p: { id: number }) => {
    qc.invalidateQueries({ queryKey: ['summary'] })
    qc.invalidateQueries({ queryKey: ['plans'] })
    qc.invalidateQueries({ queryKey: ['plan', String(p.id)] })
    qc.invalidateQueries({ queryKey: ['entry'] })
  }
}

/** "New draft plan" or "Add to …" (the default from {@link usePlanTarget} preselected). Returns the chosen draft, if any. */
export function usePlanChoice(enabled = true) {
  const { drafts, fallback } = usePlanTarget(enabled)
  const [chosen, setChosen] = useState<number | 'new' | null>(null)
  const target = chosen ?? fallback?.id ?? 'new'
  const existing = target === 'new' ? undefined : drafts.find((p) => p.id === target)
  const field = (
    <Field label="Plan" hint={existing ? 'Files the plan already has get this action instead.' : undefined}>
      <select className="input" value={existing?.id ?? 'new'} onChange={(e) => setChosen(e.target.value === 'new' ? 'new' : Number(e.target.value))}>
        <option value="new">New draft plan</option>
        {drafts.map((p) => (
          <option key={p.id} value={p.id}>
            Add to “{p.name}” ({p.items.toLocaleString()} items)
          </option>
        ))}
      </select>
    </Field>
  )
  return { existing, field }
}

/** Request fields for an action chosen with {@link ActionFields}. */
export function actionBody(op: ActionOp, destination: string, keep: string | null) {
  return { op, destination: op === 'move' ? destination : op === 'restore' ? (keep ?? undefined) : undefined }
}

/**
 * Plans an action for a selection (entry ids), a filter or every damaged file: in a new draft plan, or added to an existing draft
 * (preselected per Settings: a new plan, or the draft changed last — so files can be added one by one). Nothing changes on disk here. Without `stay` the plan opens
 * for review; with it the dialog just closes and `onDone` is told (e.g. the file panel, to go on with the next file).
 */
export function PlanDialog({ open, onClose, entryIds, filter, damaged, count, size, defaultOp = 'trash', title, stay, onDone }: {
  open: boolean
  onClose: () => void
  entryIds?: number[]
  filter?: SearchFilter
  /** Every damaged file (corrupt against the backup, or flagged by the integrity check). */
  damaged?: boolean
  count?: number
  size?: number
  defaultOp?: ActionOp
  title?: string
  stay?: boolean
  onDone?: (p: PlanAdded) => void
}) {
  const templates = useTemplates()
  const navigate = useNavigate()
  const added = usePlanAdded()
  const ops = usePlanOps(defaultOp)
  const { existing, field } = usePlanChoice(open)
  const [templateId, setTemplateId] = useState<number | ''>('')
  const [op, setOp] = useState<ActionOp>(defaultOp)
  const [destination, setDestination] = useState('Archive/{relDir}/{name}')
  const keep = useKeepLocal()
  const [conflict, setConflict] = useState<ConflictPolicy>('rename')
  // With Overwrite: where the file at the destination is kept (null = it is lost).
  const [keepOverwritten, setKeepOverwritten] = useState<string | null>(DEFAULT_KEEP)
  const [keepTemplate, setKeepTemplate] = useState(DEFAULT_KEEP)
  const [name, setName] = useState('')

  const tpl = templates.data?.find((t) => t.id === templateId)
  const effectiveOp = tpl?.op ?? op

  const create = useMutation({
    mutationFn: () => {
      const body = {
        ...(tpl ? { op: effectiveOp } : actionBody(op, destination, keep.keep)),
        conflict: existing ? undefined : conflict,
        keepOverwritten: effectiveOp === 'move' && (existing || conflict === 'overwrite') ? (keepOverwritten ?? undefined) : undefined,
        name: existing ? undefined : name || undefined,
        templateId: templateId || undefined,
        planId: existing?.id,
      }
      if (damaged) return api<PlanAdded>('/plans/restore-damaged', { method: 'POST', json: body })
      return entryIds
        ? api<PlanAdded>('/plans', { method: 'POST', json: { ...body, entryIds } })
        : api<PlanAdded>('/plans/from-filter', { method: 'POST', json: { ...body, filter } })
    },
    onSuccess: (p) => {
      added(p)
      if (stay) {
        onDone?.(p)
        onClose()
      } else leaveOverlay(navigate, `/actions/${p.id}`)
    },
  })

  return (
    <Modal open={open} onClose={onClose} title={title ?? 'Plan an action'}>
      <div className="space-y-4">
        <p className="text-sm muted">
          {count !== undefined ? `${count.toLocaleString()} item(s)` : damaged ? 'Every damaged file' : 'Selected items'}
          {size !== undefined ? ` · ${bytes(size)}` : ''}. The plan is a draft for review — nothing is changed until you apply it.
        </p>
        {field}
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
        {!tpl && <ActionFields ops={ops} op={op} setOp={setOp} destination={destination} setDestination={setDestination} keep={keep} />}
        {effectiveOp === 'move' && !existing && (
          <Field label="When the destination exists">
            <select className="input" value={conflict} onChange={(e) => setConflict(e.target.value as ConflictPolicy)}>
              <option value="rename">Rename (add “(2)”)</option>
              <option value="skip">Skip the item</option>
              <option value="overwrite">Overwrite (files only)</option>
            </select>
          </Field>
        )}
        {effectiveOp === 'move' && (existing ? existing.conflict === 'overwrite' : conflict === 'overwrite') && (
          <div className="space-y-2 rounded-md bg-surface-2 p-2 text-sm">
            <label className="flex flex-wrap items-center gap-2">
              <input type="radio" className="accent-accent" checked={keepOverwritten !== null} onChange={() => setKeepOverwritten(keepTemplate)} />
              Keep the file being overwritten as
              <input
                className="input w-64 py-1 font-mono"
                value={keepTemplate}
                onChange={(e) => {
                  setKeepTemplate(e.target.value)
                  setKeepOverwritten(e.target.value)
                }}
                aria-label="Where the overwritten file is kept"
              />
            </label>
            <label className="flex items-center gap-2">
              <input type="radio" className="accent-accent" checked={keepOverwritten === null} onChange={() => setKeepOverwritten(null)} />
              Overwrite it: the old file is lost (undo cannot bring it back)
            </label>
            {keepOverwritten !== null && <p className="text-xs muted">The template is filled in for the file at the destination; an existing name gets “(2)”. Undo puts it back.</p>}
          </div>
        )}
        {!existing && (
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
            {existing ? `Add to “${existing.name}”` : stay ? 'Create draft plan' : 'Create plan for review'}
          </button>
        </div>
      </div>
    </Modal>
  )
}
