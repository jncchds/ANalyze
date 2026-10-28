import { useQuery } from '@tanstack/react-query'
import { api } from './client'
import type { Category, Config, Integration, Root, Rule, Summary, ActionTemplate } from './types'

export const useConfig = () => useQuery({ queryKey: ['config'], queryFn: () => api<Config>('/config'), staleTime: 60_000 })

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
