import { useState } from 'react'
import { Link } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { useCategories } from '../api/hooks'
import type { Page, RuleCondition } from '../api/types'
import { Empty, ErrorBox, Field, Loading, PageTitle, Section, Stat, Tabs } from '../components/ui'
import { isActive, useJob } from '../hooks/useJobs'
import { useScope } from '../hooks/useScope'
import { bytes, count, dateTime, kindIcon } from '../lib/format'
import { describe } from './Rules'

interface AiStatus {
  enabled: boolean
  configured: boolean
  model: string
  running: boolean
  uncategorized: number
  review: number
  suggestions: number
  history: { kind: string; startedAt: string; finishedAt?: string; files: number; applied: number; suggested: number; cached: number; failed: number; inputTokens: number; outputTokens: number; error?: string }[]
}

interface ReviewRow {
  id: number
  category: string
  tags: string[]
  confidence: number
  reason?: string
  model: string
  entry: { id: number; rootId: number; name: string; relPath: string; size: number; kind: string }
}

interface Suggestion {
  id: number
  name: string
  categoryName: string
  condition: RuleCondition
  tags: string[]
  rationale?: string
  matchCount: number
  matchBytes: number
  status: 'pending' | 'accepted' | 'rejected'
  createdAt: string
}

export default function Ai() {
  const qc = useQueryClient()
  const { rootId } = useScope()
  const job = useJob('ai')
  const [tab, setTab] = useState<'review' | 'suggestions' | 'history'>('suggestions')
  const [limit, setLimit] = useState(200)
  const status = useQuery({ queryKey: ['ai-status'], queryFn: () => api<AiStatus>('/ai/status'), refetchInterval: isActive(job) ? 3000 : false })
  const classify = useMutation({ mutationFn: () => api('/ai/classify', { method: 'POST', json: { limit, rootId } }) })
  const suggest = useMutation({ mutationFn: () => api('/ai/suggest', { method: 'POST' }) })
  const cancel = useMutation({ mutationFn: () => api('/ai/cancel', { method: 'POST' }) })
  const s = status.data
  const running = isActive(job)

  if (status.isLoading) return <Loading />
  if (status.error) return <ErrorBox error={status.error} />

  return (
    <>
      <PageTitle subtitle={`OpenAI-compatible model: ${s?.model}. Only names, paths, sizes and dates are sent unless text snippets are enabled in Settings.`}>AI</PageTitle>
      {!s?.enabled && (
        <div className="mb-4 rounded-lg border border-line bg-surface-2 p-4 text-sm">
          AI is off. Configure an OpenAI-compatible endpoint (OpenAI, Ollama, LM Studio, vLLM…) in <Link to="/settings" className="link">Settings → AI</Link>.
        </div>
      )}
      <div className="mb-4 grid grid-cols-2 gap-3 md:grid-cols-4">
        <Stat label="Uncategorized files" value={count(s?.uncategorized)} />
        <Stat label="Waiting for review" value={count(s?.review)} tone={s?.review ? 'amber' : undefined} />
        <Stat label="Rule suggestions" value={count(s?.suggestions)} tone={s?.suggestions ? 'amber' : undefined} />
        <Stat label="Status" value={running ? job?.phase ?? 'running' : 'idle'} hint={running && job?.total ? `${count(job.done)} / ${count(job.total)}` : undefined} />
      </div>
      <Section title="Run">
        <div className="flex flex-wrap items-end gap-3">
          <Field label="Classify up to (largest first)">
            <input className="input w-32" type="number" min={1} value={limit} onChange={(e) => setLimit(Number(e.target.value))} />
          </Field>
          <button className="btn-primary" disabled={!s?.enabled || running} onClick={() => classify.mutate()}>
            ✨ Classify uncategorized
          </button>
          <button className="btn-secondary" disabled={!s?.enabled || running} onClick={() => suggest.mutate()}>
            Suggest rules
          </button>
          {running && (
            <button className="btn-ghost" onClick={() => cancel.mutate()}>
              Cancel
            </button>
          )}
        </div>
        {(classify.error || suggest.error) && <div className="mt-2"><ErrorBox error={classify.error ?? suggest.error} /></div>}
        {job && !running && job.message && <p className="mt-2 text-sm muted">Last run: {job.message}</p>}
      </Section>

      <div className="mt-6">
        <Tabs
          tabs={[
            { id: 'suggestions', label: `Rule suggestions (${s?.suggestions ?? 0})` },
            { id: 'review', label: `Review (${s?.review ?? 0})` },
            { id: 'history', label: 'History' },
          ]}
          value={tab}
          onChange={setTab}
        />
        {tab === 'suggestions' && <Suggestions onChange={() => qc.invalidateQueries()} />}
        {tab === 'review' && <Review onChange={() => qc.invalidateQueries()} />}
        {tab === 'history' && (
          <div className="card divide-y divide-line/60">
            {!s?.history.length && <Empty>No runs since the last restart.</Empty>}
            {s?.history.map((h, i) => (
              <div key={i} className="flex flex-wrap items-center gap-3 px-3 py-2 text-sm">
                <span className="font-semibold">{h.kind === 'classify' ? 'Classification' : 'Rule suggestions'}</span>
                <span className="muted">{dateTime(h.startedAt)}</span>
                <span className="flex-1" />
                {h.kind === 'classify' && (
                  <span>
                    {h.applied} applied · {h.suggested} to review · {h.cached} cached{h.failed ? ` · ${h.failed} failed` : ''}
                  </span>
                )}
                <span className="text-xs muted">
                  {count(h.inputTokens)} in / {count(h.outputTokens)} out tokens
                </span>
                {h.error && <span className="w-full text-xs text-danger">{h.error}</span>}
              </div>
            ))}
          </div>
        )}
      </div>
    </>
  )
}

function Suggestions({ onChange }: { onChange: () => void }) {
  const q = useQuery({ queryKey: ['ai-suggestions'], queryFn: () => api<Suggestion[]>('/ai/suggestions') })
  const accept = useMutation({ mutationFn: (id: number) => api(`/ai/suggestions/${id}/accept`, { method: 'POST' }), onSuccess: onChange })
  const reject = useMutation({ mutationFn: (id: number) => api(`/ai/suggestions/${id}/reject`, { method: 'POST' }), onSuccess: onChange })
  if (q.isLoading) return <Loading />
  const pending = q.data?.filter((s) => s.status === 'pending') ?? []
  if (!pending.length) return <Empty>No pending suggestions. “Suggest rules” looks at uncategorized files and proposes reusable rules.</Empty>
  return (
    <div className="space-y-2">
      {pending.map((s) => (
        <div key={s.id} className="card p-3">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-semibold">{s.name}</span>
            <span className="chip">→ {s.categoryName}</span>
            {s.tags.map((t) => (
              <span key={t} className="pill-muted">#{t}</span>
            ))}
            <span className="flex-1" />
            <span className="text-sm muted">
              {count(s.matchCount)} files · {bytes(s.matchBytes)}
            </span>
          </div>
          <p className="mt-1 font-mono text-xs muted">{describe(s.condition)}</p>
          {s.rationale && <p className="mt-1 text-sm">{s.rationale}</p>}
          <div className="mt-2 flex justify-end gap-2">
            <button className="btn-ghost" onClick={() => reject.mutate(s.id)}>
              Reject
            </button>
            <button className="btn-primary" onClick={() => accept.mutate(s.id)} disabled={accept.isPending}>
              Add rule
            </button>
          </div>
        </div>
      ))}
    </div>
  )
}

function Review({ onChange }: { onChange: () => void }) {
  const categories = useCategories()
  const q = useQuery({ queryKey: ['ai-review'], queryFn: () => api<Page<ReviewRow>>('/ai/review', { query: { limit: 200 } }) })
  const accept = useMutation({ mutationFn: ({ id, category }: { id: number; category?: string }) => api(`/ai/review/${id}/accept`, { method: 'POST', json: { category } }), onSuccess: onChange })
  const dismiss = useMutation({ mutationFn: (id: number) => api(`/ai/review/${id}/dismiss`, { method: 'POST' }), onSuccess: onChange })
  if (q.isLoading) return <Loading />
  if (!q.data?.items.length) return <Empty>Nothing to review. Answers below the confidence threshold land here.</Empty>
  return (
    <div className="card divide-y divide-line/60">
      {q.data.items.map((r) => (
        <div key={r.id} className="flex flex-wrap items-center gap-3 px-3 py-2 text-sm">
          <span>{kindIcon(r.entry.kind)}</span>
          <div className="min-w-0 flex-1">
            <div className="truncate" title={r.entry.relPath}>
              {r.entry.relPath}
            </div>
            <div className="text-xs muted">
              {bytes(r.entry.size)} · {r.reason}
            </div>
          </div>
          <span className="text-xs muted tabular">{Math.round(r.confidence * 100)}%</span>
          <select
            className="input w-44 py-1"
            defaultValue={r.category}
            onChange={(e) => accept.mutate({ id: r.id, category: e.target.value })}
            aria-label="Category"
          >
            {!categories.data?.some((c) => c.name === r.category) && <option value={r.category}>{r.category} (new)</option>}
            {categories.data?.map((c) => (
              <option key={c.id} value={c.name}>
                {c.name}
              </option>
            ))}
          </select>
          <button className="btn-secondary px-2 py-1 text-xs" onClick={() => accept.mutate({ id: r.id })}>
            Accept
          </button>
          <button className="btn-ghost px-2 py-1 text-xs" onClick={() => dismiss.mutate(r.id)}>
            Dismiss
          </button>
        </div>
      ))}
    </div>
  )
}
