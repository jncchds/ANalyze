import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import type { ActionOp, PlanItem, PlanRow } from '../api/types'

export const OP_LABEL: Record<ActionOp, string> = {
  trash: '🗑️ Trash',
  delete: '⚠️ Delete',
  move: '📦 Move',
  hardlink: '🔗 Hard link',
  restore: '🛟 Restore from backup',
}

/** Where a damaged file is kept by default when the backup's copy replaces it (same as `PlanBuilder.KeepDamagedDefault`). */
export const KEEP_DAMAGED_DEFAULT = '{relDir}/~{name}'

/** What a plan item does, in a few words ("restore" says what happens to the damaged file). */
export function itemAction(i: Pick<PlanItem, 'op' | 'keepPath'>) {
  if (i.op === 'restore') return i.keepPath ? '🛟 Restore, keep damaged' : '🛟 Restore, trash damaged'
  return OP_LABEL[i.op]
}

/** The plans that can still take items. */
export function useDraftPlans(enabled = true) {
  return useQuery({ queryKey: ['plans'], queryFn: () => api<PlanRow[]>('/plans'), enabled, select: (plans) => plans.filter((p) => p.status === 'draft') })
}

function read(key: string) {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}

function write(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key)
    else localStorage.setItem(key, value)
  } catch {
    // Private mode: the choice is just not remembered.
  }
}

const number = (v: string | null) => (v !== null && Number.isInteger(Number(v)) ? Number(v) : null)

/** The draft plan the last "add to plan" went to; offered again while it is still a draft. */
export const lastPlan = {
  get: () => number(read('analyze.plan.target')),
  set: (id: number | null) => write('analyze.plan.target', id === null ? null : String(id)),
}

/** The plan the damaged-files review collects its decisions in. */
export const reviewPlan = {
  get: () => number(read('analyze.plan.review')),
  set: (id: number | null) => write('analyze.plan.review', id === null ? null : String(id)),
}

/** Template for keeping damaged files on a restore; null when the last restore sent them to the trash. */
export const keepDamaged = {
  get: () => read('analyze.plan.keepDamaged'),
  set: (template: string | null) => write('analyze.plan.keepDamaged', template),
}
