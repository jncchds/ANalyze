import { useCallback, useMemo, useSyncExternalStore } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import type { ActionOp, ItemStatus, PlanItem, PlanRow } from '../api/types'

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

const REVIEW_KEY = 'analyze.plan.review'
const reviewListeners = new Set<() => void>()

/**
 * The plan the decisions about corrupted files go to (file panel), shared by every component on the page and remembered between
 * visits. Null: a new plan, made by the first decision.
 */
export function useReviewPlan() {
  const id = useSyncExternalStore(
    (notify) => {
      reviewListeners.add(notify)
      return () => reviewListeners.delete(notify)
    },
    () => number(read(REVIEW_KEY)),
  )
  const set = useCallback((next: number | null) => {
    write(REVIEW_KEY, next === null ? null : String(next))
    reviewListeners.forEach((l) => l())
  }, [])
  return [id, set] as const
}

/** What a plan does with one entry (`GET /plans/{id}/decisions`). */
export interface Decision {
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

/** The decisions of a plan by entry; `plan` only while it is a draft (an applied or deleted plan takes no more decisions). */
export function usePlanDecisions(planId: number | null | undefined) {
  const q = useQuery({
    queryKey: ['plan', String(planId), 'decisions'],
    queryFn: () => api<Decisions>(`/plans/${planId}/decisions`),
    enabled: planId != null,
    retry: false,
  })
  const plan = planId != null && q.data?.plan.status === 'draft' ? q.data.plan : undefined
  const byEntry = useMemo(() => new Map((plan ? q.data!.items : []).map((d) => [d.entryId, d])), [plan, q.data])
  return { plan, byEntry, waiting: planId != null && q.isLoading }
}

export function decisionText(d: Decision) {
  if (d.kept) return '✓ Keep the file here'
  if (!d.included) return `Left out: ${d.warning ?? 'excluded in the plan'}`
  if (d.op === 'restore') return d.keepPath ? `🛟 Replace, keep the damaged file as ${d.keepPath}` : '🛟 Replace, damaged file to the trash'
  return OP_LABEL[d.op]
}

/** A one-character mark for lists. */
export function decisionMark(d: Decision) {
  if (d.kept) return { icon: '✓', tone: 'text-accent' }
  if (!d.included) return { icon: '!', tone: 'text-amber' }
  if (d.op === 'restore') return { icon: d.keepPath ? '🛟~' : '🛟', tone: 'text-success' }
  return { icon: '•', tone: 'text-danger' }
}

/** Template for keeping damaged files on a restore; null when the last restore sent them to the trash. */
export const keepDamaged = {
  get: () => read('analyze.plan.keepDamaged'),
  set: (template: string | null) => write('analyze.plan.keepDamaged', template),
}
