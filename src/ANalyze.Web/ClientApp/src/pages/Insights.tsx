import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../api/client'
import type { Insight, SearchFilter } from '../api/types'
import { PlanDialog } from '../components/PlanDialog'
import { Empty, ErrorBox, Loading, PageTitle, Section } from '../components/ui'
import { useScope } from '../hooks/useScope'
import { bytes, count, dateTime, signedBytes } from '../lib/format'

interface Growth {
  from?: string
  to?: string
  items: { id: number; rootId: number; relPath: string; size: number; previous: number; delta: number; filesDelta: number }[]
}

const SEVERITY: Record<Insight['severity'], string> = {
  high: 'border-l-amber',
  medium: 'border-l-amber/60',
  low: 'border-l-line',
  info: 'border-l-accent/50',
}

export function filterToQuery(f: SearchFilter) {
  return new URLSearchParams(Object.entries(f).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => [k, String(v)])).toString()
}

export default function Insights() {
  const { rootId } = useScope()
  const navigate = useNavigate()
  const [plan, setPlan] = useState<Insight | null>(null)
  const insights = useQuery({ queryKey: ['insights', rootId], queryFn: () => api<Insight[]>('/insights', { query: { rootId } }) })
  const growth = useQuery({ queryKey: ['growth', rootId], queryFn: () => api<Growth>('/insights/growth', { query: { rootId } }) })

  return (
    <>
      <PageTitle subtitle="Findings that usually free space. Each opens as a search; nothing is changed without a reviewed plan.">Insights</PageTitle>
      {insights.isLoading ? (
        <Loading />
      ) : insights.error ? (
        <ErrorBox error={insights.error} />
      ) : !insights.data?.length ? (
        <Empty>No findings yet — run a scan first.</Empty>
      ) : (
        <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
          {insights.data.map((i) => (
            <div key={i.key} className={clsx('card flex flex-col border-l-4 p-4', SEVERITY[i.severity])}>
              <div className="flex items-baseline gap-2">
                <h3 className="min-w-0 flex-1 font-semibold">{i.title}</h3>
                <span className={clsx('font-bold tabular', i.severity === 'high' || i.severity === 'medium' ? 'text-amber' : '')}>{bytes(i.bytes)}</span>
              </div>
              <p className="mt-1 flex-1 text-sm muted">{i.description}</p>
              <div className="mt-3 flex items-center gap-2">
                <span className="text-xs muted">{count(i.count)} {i.filter.dirs || i.filter.emptyDirs ? 'folders' : 'files'}</span>
                <span className="flex-1" />
                <button className="btn-ghost px-2 py-1 text-xs" onClick={() => navigate(`/search?${filterToQuery(i.filter)}`)}>
                  View
                </button>
                {i.suggestedOp && (
                  <button className="btn-secondary px-2 py-1 text-xs" onClick={() => setPlan(i)}>
                    Clean up…
                  </button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      <Section
        className="mt-6"
        title="Growth since the previous scan"
        actions={growth.data?.from ? <span className="text-xs muted">{dateTime(growth.data.from)} → {dateTime(growth.data.to)}</span> : undefined}
      >
        {!growth.data?.items.length ? (
          <Empty>Needs two full scans to compare.</Empty>
        ) : (
          <ul className="divide-y divide-line/60">
            {growth.data.items.map((g) => (
              <li key={g.id} className="flex items-center gap-3 py-1.5 text-sm">
                <Link to={`/browse/${g.id}`} className="min-w-0 flex-1 truncate hover:text-accent">
                  📁 {g.relPath}
                </Link>
                <span className="text-xs muted tabular">{bytes(g.size)}</span>
                <span className={clsx('w-24 text-right font-semibold tabular', g.delta > 0 ? 'text-amber' : 'text-success')}>{signedBytes(g.delta)}</span>
              </li>
            ))}
          </ul>
        )}
      </Section>

      {plan && (
        <PlanDialog
          open
          onClose={() => setPlan(null)}
          filter={plan.filter}
          count={plan.count}
          size={plan.bytes}
          defaultOp={plan.suggestedOp}
          title={`Clean up: ${plan.title}`}
        />
      )}
    </>
  )
}
