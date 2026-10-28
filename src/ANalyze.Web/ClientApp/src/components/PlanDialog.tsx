import { useState } from 'react'
import { useNavigate } from 'react-router'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { useConfig, useTemplates } from '../api/hooks'
import type { ActionOp, ConflictPolicy, SearchFilter } from '../api/types'
import { bytes } from '../lib/format'
import { ErrorBox, Field, Modal } from './ui'

/**
 * Creates a draft plan from a selection (entry ids) or a filter. Nothing changes on disk here:
 * the plan opens for review and is applied from there.
 */
export function PlanDialog({ open, onClose, entryIds, filter, count, size, defaultOp = 'trash', title }: {
  open: boolean
  onClose: () => void
  entryIds?: number[]
  filter?: SearchFilter
  count?: number
  size?: number
  defaultOp?: ActionOp
  title?: string
}) {
  const templates = useTemplates()
  const config = useConfig()
  const navigate = useNavigate()
  const qc = useQueryClient()
  const [templateId, setTemplateId] = useState<number | ''>('')
  const [op, setOp] = useState<ActionOp>(defaultOp)
  const [destination, setDestination] = useState('Archive/{relDir}/{name}')
  const [conflict, setConflict] = useState<ConflictPolicy>('rename')
  const [name, setName] = useState('')

  const tpl = templates.data?.find((t) => t.id === templateId)
  const effectiveOp = tpl?.op ?? op

  const create = useMutation({
    mutationFn: () => {
      const body = { op: effectiveOp, destination: tpl ? undefined : destination, conflict, name: name || undefined, templateId: templateId || undefined }
      return entryIds
        ? api<{ id: number }>('/plans', { method: 'POST', json: { ...body, entryIds } })
        : api<{ id: number }>('/plans/from-filter', { method: 'POST', json: { ...body, filter } })
    },
    onSuccess: (p) => {
      qc.invalidateQueries({ queryKey: ['summary'] })
      onClose()
      navigate(`/actions/${p.id}`)
    },
  })

  return (
    <Modal open={open} onClose={onClose} title={title ?? 'Plan an action'}>
      <div className="space-y-4">
        <p className="text-sm muted">
          {count !== undefined ? `${count.toLocaleString()} item(s)` : 'Selected items'}
          {size !== undefined ? ` · ${bytes(size)}` : ''}. A draft plan is created for review — nothing is changed until you apply it.
        </p>
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
        {!tpl && (
          <>
            <Field label="Action">
              <div className="flex flex-wrap gap-2">
                {(['trash', 'move', 'delete'] as ActionOp[]).map((o) => (
                  <label key={o} className={`btn ${op === o ? 'btn-primary' : 'btn-secondary'} cursor-pointer`}>
                    <input type="radio" className="sr-only" checked={op === o} onChange={() => setOp(o)} />
                    {o === 'trash' ? '🗑️ Move to trash' : o === 'move' ? '📦 Move / rename' : '⚠️ Delete permanently'}
                  </label>
                ))}
              </div>
            </Field>
            {op === 'move' && (
              <Field label="Destination (relative to the root)" hint={<>Tokens: {config.data?.pathTokens.map((t) => `{${t}}`).join(' ')} — e.g. {'{mtime:yyyy}'}. A plain folder keeps the names.</>}>
                <input className="input font-mono" value={destination} onChange={(e) => setDestination(e.target.value)} />
              </Field>
            )}
            {op === 'delete' && <p className="rounded-md bg-danger/10 p-2 text-sm text-danger">Permanent deletion cannot be undone. Prefer the trash unless space is needed right now.</p>}
          </>
        )}
        {effectiveOp === 'move' && (
          <Field label="When the destination exists">
            <select className="input" value={conflict} onChange={(e) => setConflict(e.target.value as ConflictPolicy)}>
              <option value="rename">Rename (add “(2)”)</option>
              <option value="skip">Skip the item</option>
              <option value="overwrite">Overwrite (files only)</option>
            </select>
          </Field>
        )}
        <Field label="Plan name (optional)">
          <input className="input" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        {create.error && <ErrorBox error={create.error} />}
        <div className="flex justify-end gap-2">
          <button className="btn-secondary" onClick={onClose}>
            Cancel
          </button>
          <button className="btn-primary" onClick={() => create.mutate()} disabled={create.isPending}>
            Create plan for review
          </button>
        </div>
      </div>
    </Modal>
  )
}
