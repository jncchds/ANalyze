import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'

const KEY = 'analyze.root'

interface ScopeState {
  /** Selected root; undefined = all roots. */
  rootId?: number
  setRootId: (id: number | undefined) => void
}

const ScopeContext = createContext<ScopeState>({ setRootId: () => {} })

export function ScopeProvider({ children }: { children: ReactNode }) {
  const [rootId, setRootId] = useState<number | undefined>(() => {
    try {
      const v = localStorage.getItem(KEY)
      return v ? Number(v) : undefined
    } catch {
      return undefined
    }
  })
  useEffect(() => {
    try {
      if (rootId === undefined) localStorage.removeItem(KEY)
      else localStorage.setItem(KEY, String(rootId))
    } catch {
      /* ignore */
    }
  }, [rootId])
  return <ScopeContext.Provider value={{ rootId, setRootId }}>{children}</ScopeContext.Provider>
}

export const useScope = () => useContext(ScopeContext)
