import { useSyncExternalStore } from 'react'

const KEY = 'analyze.seenVersion'
const listeners = new Set<() => void>()

function read() {
  try {
    return localStorage.getItem(KEY)
  } catch {
    return null
  }
}

/** Remembers that this browser has opened the release notes for `version`. */
export function markSeen(version: string) {
  try {
    localStorage.setItem(KEY, version)
  } catch {
    /* ignore */
  }
  listeners.forEach((l) => l())
}

/** True when the running version's release notes have not been opened in this browser yet. */
export function useHasUnseenRelease(version?: string) {
  const seen = useSyncExternalStore(
    (l) => {
      listeners.add(l)
      return () => listeners.delete(l)
    },
    read,
  )
  return !!version && seen !== version
}
