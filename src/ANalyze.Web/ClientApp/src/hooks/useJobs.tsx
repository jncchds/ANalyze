import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import type { JobProgress, LogLine } from '../api/types'

type LogListener = (lines: LogLine[]) => void

const JobsContext = createContext<Record<string, JobProgress>>({})
const LogContext = createContext<(fn: LogListener) => () => void>(() => () => {})

/** While a scan runs, data shown on screen is refreshed this often (the scan stores its results as it goes). */
const LIVE_REFRESH_MS = 10_000

/** One SignalR connection for the whole app; jobs that finish or move to their next step refresh cached data. */
export function JobsProvider({ children }: { children: ReactNode }) {
  const [jobs, setJobs] = useState<Record<string, JobProgress>>({})
  const qc = useQueryClient()
  const conn = useRef<HubConnection | null>(null)
  const listeners = useRef(new Set<LogListener>())

  useEffect(() => {
    const c = new HubConnectionBuilder().withUrl('/hubs/jobs').withAutomaticReconnect([0, 2000, 5000, 10000, 30000]).configureLogging(LogLevel.Warning).build()
    conn.current = c
    const apply = (p: JobProgress) => {
      setJobs((prev) => {
        const before = prev[p.kind]
        const finished = before && before.state !== p.state && (p.state === 'completed' || p.state === 'failed' || p.state === 'cancelled')
        const nextStep = before && p.state === 'running' && before.phase !== p.phase
        if (finished || nextStep) {
          // Anything may have changed: sizes, categories, plans, annotations.
          setTimeout(() => qc.invalidateQueries(), 100)
        }
        return { ...prev, [p.kind]: p }
      })
    }
    c.on('progress', apply)
    c.on('logs', (lines: LogLine[]) => listeners.current.forEach((fn) => fn(lines)))
    const load = () =>
      c.invoke<JobProgress[]>('GetStatus').then((all) => setJobs(Object.fromEntries(all.map((j) => [j.kind, j])))).catch(() => {})
    c.onreconnected(load)
    c.start().then(load).catch(() => {})
    return () => {
      c.stop()
    }
  }, [qc])

  const scanRunning = isActive(jobs.scan)
  useEffect(() => {
    if (!scanRunning) return
    // Folders and sizes appear in the index during the walk: keep what is on screen current (only queries in use refetch).
    const t = setInterval(() => qc.invalidateQueries({ predicate: (q) => q.queryKey[0] !== 'logs' }), LIVE_REFRESH_MS)
    return () => clearInterval(t)
  }, [scanRunning, qc])

  const subscribe = (fn: LogListener) => {
    listeners.current.add(fn)
    return () => {
      listeners.current.delete(fn)
    }
  }

  return (
    <JobsContext.Provider value={jobs}>
      <LogContext.Provider value={subscribe}>{children}</LogContext.Provider>
    </JobsContext.Provider>
  )
}

export function useJobs() {
  return useContext(JobsContext)
}

export function useJob(kind: string): JobProgress | undefined {
  return useContext(JobsContext)[kind]
}

/** Calls <paramref name="fn"/> with each batch of new activity log lines. */
export function useLogStream(fn: LogListener) {
  const subscribe = useContext(LogContext)
  const latest = useRef(fn)
  latest.current = fn
  useEffect(() => subscribe((lines) => latest.current(lines)), [subscribe])
}

export const isActive = (j?: JobProgress) => j?.state === 'running' || j?.state === 'queued'
