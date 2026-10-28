import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import type { JobProgress } from '../api/types'

const JobsContext = createContext<Record<string, JobProgress>>({})

/** One SignalR connection for the whole app; finished jobs refresh cached data. */
export function JobsProvider({ children }: { children: ReactNode }) {
  const [jobs, setJobs] = useState<Record<string, JobProgress>>({})
  const qc = useQueryClient()
  const conn = useRef<HubConnection | null>(null)

  useEffect(() => {
    const c = new HubConnectionBuilder().withUrl('/hubs/jobs').withAutomaticReconnect([0, 2000, 5000, 10000, 30000]).configureLogging(LogLevel.Warning).build()
    conn.current = c
    const apply = (p: JobProgress) => {
      setJobs((prev) => {
        const before = prev[p.kind]
        if (before && before.state !== p.state && (p.state === 'completed' || p.state === 'failed' || p.state === 'cancelled')) {
          // Anything may have changed: sizes, categories, plans, annotations.
          setTimeout(() => qc.invalidateQueries(), 100)
        }
        return { ...prev, [p.kind]: p }
      })
    }
    c.on('progress', apply)
    const load = () =>
      c.invoke<JobProgress[]>('GetStatus').then((all) => setJobs(Object.fromEntries(all.map((j) => [j.kind, j])))).catch(() => {})
    c.onreconnected(load)
    c.start().then(load).catch(() => {})
    return () => {
      c.stop()
    }
  }, [qc])

  return <JobsContext.Provider value={jobs}>{children}</JobsContext.Provider>
}

export function useJobs() {
  return useContext(JobsContext)
}

export function useJob(kind: string): JobProgress | undefined {
  return useContext(JobsContext)[kind]
}

export const isActive = (j?: JobProgress) => j?.state === 'running' || j?.state === 'queued'
