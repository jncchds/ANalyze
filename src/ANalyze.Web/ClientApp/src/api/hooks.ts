import { useQuery } from '@tanstack/react-query'
import { api } from './client'
import { desktop } from '../lib/desktop'
import type { Category, Config, HostPath, Integration, Release, Root, Rule, Summary, ActionTemplate } from './types'

/** Host paths: configured in Settings, then ANalyze's own Docker mounts. Not used in the desktop app (it sees host paths). */
export const useHostPaths = () =>
  useQuery({
    queryKey: ['host-paths'],
    queryFn: () => api<{ configured: HostPath[]; auto: HostPath[] }>('/host-paths'),
    staleTime: 5 * 60_000,
    enabled: !desktop,
  })

const trimEnd = (p: string) => p.replace(/\/+$/, '') || '/'

/** Turns a path ANalyze sees into the host path (longest matching prefix); undefined when no mapping covers it. */
export function useToHostPath() {
  const { data } = useHostPaths()
  const all = [...(data?.configured ?? []), ...(data?.auto ?? [])]
  return (path?: string | null): string | undefined => {
    if (!path) return undefined
    let best: HostPath | undefined
    for (const h of all) {
      const p = trimEnd(h.path)
      const covers = p === '/' || path === p || path.startsWith(p + '/')
      if (covers && (!best || p.length > trimEnd(best.path).length)) best = h
    }
    if (!best) return undefined
    const from = trimEnd(best.path)
    const rest = from === '/' ? path : path.slice(from.length)
    const host = trimEnd(best.host)
    const result = host === '/' ? rest || '/' : host + (rest === '' || rest.startsWith('/') ? rest : '/' + rest)
    return result === path ? undefined : result
  }
}

export const useConfig = () => useQuery({ queryKey: ['config'], queryFn: () => api<Config>('/config'), staleTime: 60_000 })

export const useReleaseNotes = () => useQuery({ queryKey: ['release-notes'], queryFn: () => api<Release[]>('/release-notes'), staleTime: Infinity })

export const useRoots = () => useQuery({ queryKey: ['roots'], queryFn: () => api<Root[]>('/roots') })

export const useSummary = () => useQuery({ queryKey: ['summary'], queryFn: () => api<Summary>('/summary'), staleTime: 15_000 })

export const useCategories = () => useQuery({ queryKey: ['categories'], queryFn: () => api<Category[]>('/categories'), staleTime: 60_000 })

export const useRules = () => useQuery({ queryKey: ['rules'], queryFn: () => api<Rule[]>('/rules') })

export const useTemplates = () => useQuery({ queryKey: ['templates'], queryFn: () => api<ActionTemplate[]>('/templates') })

export const useIntegrations = () => useQuery({ queryKey: ['integrations'], queryFn: () => api<Integration[]>('/integrations') })

/** Category lookup by id with a neutral fallback. */
export function useCategoryMap() {
  const { data } = useCategories()
  const map = new Map<number, Category>()
  for (const c of data ?? []) map.set(c.id, c)
  return map
}
