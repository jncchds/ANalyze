import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../api/client'
import type { LogLevel, LogLine } from '../api/types'
import { useLogStream } from '../hooks/useJobs'
import { Empty, ErrorBox, Loading, PageTitle, Section } from '../components/ui'

type MinLevel = 'debug' | 'info' | 'warning' | 'error'

const order: Record<LogLevel, number> = { debug: 1, information: 2, warning: 3, error: 4 }
const minOrder: Record<MinLevel, number> = { debug: 1, info: 2, warning: 3, error: 4 }

const levelStyle: Record<LogLevel, string> = {
  debug: 'pill-muted',
  information: 'chip',
  warning: 'pill-amber',
  error: 'inline-flex items-center rounded px-2 py-0.5 text-xs bg-danger/10 text-danger',
}

const levelLabel: Record<LogLevel, string> = { debug: 'debug', information: 'info', warning: 'warning', error: 'error' }

const PAGE = 300
const MAX_LINES = 3000

function matches(l: LogLine, level: MinLevel, source: string, q: string) {
  return (
    order[l.level] >= minOrder[level] &&
    (!source || l.source.toLowerCase().startsWith(source.toLowerCase())) &&
    (!q || l.message.toLowerCase().includes(q.toLowerCase()))
  )
}

function time(iso: string) {
  const d = new Date(iso)
  const today = new Date().toDateString() === d.toDateString()
  return today ? d.toLocaleTimeString() : d.toLocaleString()
}

/** The activity log: what ANalyze is doing, newest first, following live. Kept in memory on the server (last 20,000 lines). */
export default function Logs() {
  const [level, setLevel] = useState<MinLevel>('info')
  const [source, setSource] = useState('')
  const [q, setQ] = useState('')
  const [search, setSearch] = useState('')
  const [lines, setLines] = useState<LogLine[]>([])
  const [paused, setPaused] = useState(false)
  const [loadingOlder, setLoadingOlder] = useState(false)
  const [atEnd, setAtEnd] = useState(false)
  const [open, setOpen] = useState<number | null>(null)

  useEffect(() => {
    const t = setTimeout(() => setSearch(q.trim()), 300)
    return () => clearTimeout(t)
  }, [q])

  const params = (before?: number) =>
    new URLSearchParams({ level, limit: String(PAGE), ...(source ? { source } : {}), ...(search ? { q: search } : {}), ...(before ? { before: String(before) } : {}) })

  const first = useQuery({
    queryKey: ['logs', level, source, search],
    queryFn: () => api<LogLine[]>(`/logs?${params()}`),
    staleTime: Infinity,
  })
  const sources = useQuery({ queryKey: ['logs', 'sources'], queryFn: () => api<string[]>('/logs/sources'), staleTime: 30_000 })

  useEffect(() => {
    if (first.data) {
      setLines(first.data)
      setAtEnd(first.data.length < PAGE)
    }
  }, [first.data])

  useLogStream((incoming) => {
    if (paused) return
    const fresh = incoming.filter((l) => matches(l, level, source, search)).reverse()
    if (fresh.length === 0) return
    setLines((prev) => {
      const newest = prev[0]?.id ?? 0
      return [...fresh.filter((l) => l.id > newest), ...prev].slice(0, MAX_LINES)
    })
  })

  const older = async () => {
    const oldest = lines.at(-1)?.id
    if (!oldest) return
    setLoadingOlder(true)
    try {
      const more = await api<LogLine[]>(`/logs?${params(oldest)}`)
      setLines((prev) => [...prev, ...more])
      setAtEnd(more.length < PAGE)
    } finally {
      setLoadingOlder(false)
    }
  }

  const sourceList = Array.from(new Set(['scan', 'integration', 'actions', 'ai', 'system', ...(sources.data ?? [])])).sort()

  return (
    <>
      <PageTitle
        subtitle="What ANalyze is doing, step by step: scan phases and their results, folders it skipped and why, syncs, plans and AI runs. Not every file — problems with single files are listed up to 50 per step. The full history is in the log files of the data folder (and docker logs)."
        actions={
          <button className={clsx(paused ? 'btn-primary' : 'btn-secondary')} onClick={() => setPaused((p) => !p)}>
            {paused ? '▶ Follow live' : '⏸ Pause'}
          </button>
        }
      >
        Activity log
      </PageTitle>

      <Section>
        <div className="mb-3 flex flex-wrap gap-2">
          <div className="flex gap-1" role="group" aria-label="Level">
            {(['debug', 'info', 'warning', 'error'] as MinLevel[]).map((l) => (
              <button key={l} className={clsx('px-2.5 py-1 text-xs', level === l ? 'btn-primary' : 'btn-secondary')} onClick={() => setLevel(l)}>
                {l === 'debug' ? 'Everything' : l === 'info' ? 'Info +' : l === 'warning' ? 'Warnings +' : 'Errors'}
              </button>
            ))}
          </div>
          <select className="input w-auto" value={source} onChange={(e) => setSource(e.target.value)} aria-label="Area">
            <option value="">All areas</option>
            {sourceList.map((s) => (
              <option key={s} value={s}>
                {s === 'integration' ? 'integrations (all)' : s}
              </option>
            ))}
          </select>
          <input className="input min-w-48 flex-1" type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Find in messages…" aria-label="Find in messages" />
        </div>

        {first.isLoading ? (
          <Loading />
        ) : first.error ? (
          <ErrorBox error={first.error} />
        ) : lines.length === 0 ? (
          <Empty>No log lines yet{level !== 'debug' || source || search ? ' that match the filter' : ''}. Start a scan to see what happens.</Empty>
        ) : (
          <ol className="divide-y divide-line text-sm">
            {lines.map((l) => (
              <li key={l.id} className="py-1.5">
                <button
                  className={clsx('flex w-full items-start gap-2 text-left', !l.detail && 'cursor-default')}
                  onClick={() => l.detail && setOpen(open === l.id ? null : l.id)}
                  aria-expanded={l.detail ? open === l.id : undefined}
                >
                  <span className="w-[5.5rem] shrink-0 pt-0.5 text-xs muted tabular" title={new Date(l.at).toLocaleString()}>
                    {time(l.at)}
                  </span>
                  <span className={clsx('shrink-0', levelStyle[l.level])}>{levelLabel[l.level]}</span>
                  <span className="pill-muted shrink-0">{l.source}</span>
                  <span className={clsx('min-w-0 flex-1 break-words', l.level === 'error' && 'text-danger')}>
                    {l.message}
                    {l.detail && <span className="ml-1 text-xs muted">{open === l.id ? '▾ details' : '▸ details'}</span>}
                  </span>
                </button>
                {open === l.id && l.detail && (
                  <pre className="mt-1 ml-[5.5rem] overflow-x-auto rounded bg-surface-2 p-2 text-xs whitespace-pre-wrap">{l.detail}</pre>
                )}
              </li>
            ))}
          </ol>
        )}

        {lines.length > 0 && !atEnd && (
          <div className="mt-3 text-center">
            <button className="btn-secondary" onClick={older} disabled={loadingOlder}>
              {loadingOlder ? 'Loading…' : 'Older lines'}
            </button>
          </div>
        )}
      </Section>
    </>
  )
}
