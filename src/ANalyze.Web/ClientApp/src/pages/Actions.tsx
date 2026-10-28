import { Link, useSearchParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../api/client'
import type { PlanRow } from '../api/types'
import { ConfirmButton, Empty, ErrorBox, Loading, PageTitle, Tabs } from '../components/ui'
import { bytes, count, dateTime } from '../lib/format'

interface TrashBin {
  planId: number
  planName: string
  appliedAt?: string
  items: number
  bytes: number
  expiresAt?: string
}

export const STATUS_STYLE: Record<string, string> = {
  draft: 'bg-amber-faint text-amber',
  running: 'bg-accent-faint text-accent',
  done: 'bg-success/10 text-success',
  failed: 'bg-danger/10 text-danger',
  undone: 'bg-muted/10 text-muted',
  cancelled: 'bg-muted/10 text-muted',
}

const OP_LABEL: Record<string, string> = { trash: '🗑️ Trash', delete: '⚠️ Delete', move: '📦 Move', hardlink: '🔗 Hard link' }

export default function Actions() {
  const [params, setParams] = useSearchParams()
  const tab = (params.get('tab') as 'plans' | 'trash') ?? 'plans'
  return (
    <>
      <PageTitle subtitle="Every change goes through a plan: review the list, untick anything you want to keep, then apply. Trash and moves can be undone.">Actions</PageTitle>
      <Tabs
        tabs={[
          { id: 'plans', label: 'Plans' },
          { id: 'trash', label: 'Trash' },
        ]}
        value={tab}
        onChange={(t) => setParams({ tab: t }, { replace: true })}
      />
      {tab === 'plans' ? <Plans /> : <Trash />}
    </>
  )
}

function Plans() {
  const q = useQuery({ queryKey: ['plans'], queryFn: () => api<PlanRow[]>('/plans') })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  if (!q.data?.length) return <Empty>No plans yet. Select files in Browse, Search, Duplicates or Insights and choose “Clean up…”.</Empty>
  return (
    <div className="card divide-y divide-line/60">
      {q.data.map((p) => (
        <Link key={p.id} to={`/actions/${p.id}`} className="flex flex-wrap items-center gap-3 px-3 py-2.5 hover:bg-accent-faint">
          <span className={clsx('rounded px-2 py-0.5 text-xs font-semibold', STATUS_STYLE[p.status])}>{p.status}</span>
          <div className="min-w-0 flex-1">
            <div className="truncate font-semibold">{p.name}</div>
            <div className="text-xs muted">
              {p.op ? OP_LABEL[p.op] : ''} · created {dateTime(p.createdAt)}
              {p.appliedAt ? ` · applied ${dateTime(p.appliedAt)}` : ''}
              {p.message ? ` · ${p.message}` : ''}
            </div>
          </div>
          <div className="text-right text-sm">
            <div className="font-semibold tabular">{bytes(p.bytes)}</div>
            <div className="text-xs muted">
              {count(p.included)} of {count(p.items)} items
            </div>
          </div>
        </Link>
      ))}
    </div>
  )
}

function Trash() {
  const qc = useQueryClient()
  const q = useQuery({ queryKey: ['trash'], queryFn: () => api<TrashBin[]>('/trash') })
  const purge = useMutation({ mutationFn: (id?: number) => api(id ? `/trash/${id}` : '/trash', { method: 'DELETE' }), onSuccess: () => qc.invalidateQueries() })
  if (q.isLoading) return <Loading />
  const bins = q.data ?? []
  if (!bins.length) return <Empty>The trash is empty.</Empty>
  return (
    <>
      <div className="mb-3 flex items-center gap-3">
        <p className="flex-1 text-sm muted">
          Trashed files stay in <code>.analyze-trash</code> inside their root until the retention period ends. Undo a plan to restore its files.
        </p>
        <ConfirmButton onConfirm={() => purge.mutate(undefined)} confirmLabel="Delete everything in the trash?">
          Empty trash ({bytes(bins.reduce((s, b) => s + b.bytes, 0))})
        </ConfirmButton>
      </div>
      {purge.error && <ErrorBox error={purge.error} />}
      <div className="card divide-y divide-line/60">
        {bins.map((b) => (
          <div key={b.planId} className="flex flex-wrap items-center gap-3 px-3 py-2.5">
            <Link to={`/actions/${b.planId}`} className="min-w-0 flex-1 hover:text-accent">
              <div className="truncate font-semibold">{b.planName}</div>
              <div className="text-xs muted">
                trashed {dateTime(b.appliedAt)}
                {b.expiresAt ? ` · emptied automatically ${dateTime(b.expiresAt)}` : ''}
              </div>
            </Link>
            <div className="text-right text-sm">
              <div className="font-semibold tabular">{bytes(b.bytes)}</div>
              <div className="text-xs muted">{count(b.items)} items</div>
            </div>
            <ConfirmButton className="btn-secondary px-2 py-1 text-xs" confirmLabel="Delete for good?" onConfirm={() => purge.mutate(b.planId)}>
              Empty
            </ConfirmButton>
          </div>
        ))}
      </div>
    </>
  )
}
