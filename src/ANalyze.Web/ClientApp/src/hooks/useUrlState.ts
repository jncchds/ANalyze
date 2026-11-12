import { useCallback, useMemo } from 'react'
import { useNavigate, useSearchParams, type NavigateFunction } from 'react-router'

/**
 * Page state kept in the query string, so every view (tab, filter, page, open dialog or file panel) has its own URL and the
 * browser's Back and Forward buttons step through them.
 *
 * - Choices (tabs, selects, toggles, pages) add a history entry; typing in a text box replaces the current one (`replace`).
 * - Overlays (dialogs, the file panel) are pushed with a marker in the history state; closing one goes Back to the entry it was
 *   opened from, so the history does not fill with "open, closed" pairs. Opened from a link (no marker) it closes in place.
 * - A value equal to its default, empty, `false`, `null` or `undefined` removes the parameter, which keeps URLs short.
 */

type Value = string | number | boolean | null | undefined
export type Patch = Record<string, Value>
export interface SetOptions {
  replace?: boolean
  /** Marks the new entry as opened over the current one (see `useUrlOverlay`). */
  overlay?: string
}

interface NavState {
  overlay?: string
}

const currentState = (): NavState | undefined => (window.history.state as { usr?: NavState } | null)?.usr ?? undefined
const currentKey = () => (window.history.state as { key?: string } | null)?.key

// The entry a Back is on its way from: a second close before the browser has moved must not go Back twice.
let leaving: string | undefined

function applyPatch(search: string, patch: Patch) {
  const p = new URLSearchParams(search)
  for (const [k, v] of Object.entries(patch)) {
    if (v === undefined || v === null || v === '' || v === false) p.delete(k)
    else p.set(k, String(v))
  }
  return p
}

/** Search params plus a setter that patches the current URL (read live, so several sets in one tick all apply). */
export function useUrlParams() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const set = useCallback(
    (patch: Patch, opts?: SetOptions) => {
      const next = applyPatch(window.location.search, patch).toString()
      const cur = new URLSearchParams(window.location.search).toString()
      if (next === cur && !opts?.overlay) return
      // A replace keeps the entry's marker, so stepping inside an overlay (next file, another tab) still closes it with Back.
      const state = opts?.overlay ? { overlay: opts.overlay } : opts?.replace ? currentState() : undefined
      navigate(`?${next}${window.location.hash}`, { replace: opts?.replace, state })
    },
    [navigate],
  )
  return [params, set] as const
}

/** One string parameter with a default. `set` adds a history entry unless `replace` is passed. */
export function useUrlParam<T extends string = string>(key: string, fallback: NoInfer<T> = '' as T) {
  const [params, set] = useUrlParams()
  const value = (params.get(key) ?? fallback) as T
  const setValue = useCallback((v: T, opts?: SetOptions & { also?: Patch }) => set({ [key]: v === fallback ? null : v, ...opts?.also }, opts), [key, fallback, set])
  return [value, setValue] as const
}

/** A parameter limited to a set of values; anything else reads as the fallback. */
export function useUrlEnum<T extends string>(key: string, values: readonly T[], fallback: T) {
  const [raw, setValue] = useUrlParam<string>(key, fallback)
  const value = (values as readonly string[]).includes(raw) ? (raw as T) : fallback
  return [value, setValue as (v: T, opts?: SetOptions & { also?: Patch }) => void] as const
}

/** A non-negative integer parameter (offsets, ids). */
export function useUrlNumber(key: string, fallback = 0) {
  const [params, set] = useUrlParams()
  const raw = Number(params.get(key))
  const value = params.has(key) && Number.isFinite(raw) ? raw : fallback
  const setValue = useCallback((v: number, opts?: SetOptions & { also?: Patch }) => set({ [key]: v === fallback ? null : v, ...opts?.also }, opts), [key, fallback, set])
  return [value, setValue] as const
}

/**
 * A dialog or panel whose content is named by a parameter (`?file=12`, `?edit=new`). `open` pushes it; `close` goes Back when this
 * entry was pushed by `open`, otherwise removes the parameter (and `related` ones) in place. `change` swaps the value in place.
 */
export function useUrlOverlay(key: string, related: string[] = []) {
  const [params, set] = useUrlParams()
  const navigate = useNavigate()
  const value = params.get(key)
  const relatedKey = related.join(',')
  return useMemo(() => {
    const clear: Patch = Object.fromEntries([key, ...(relatedKey ? relatedKey.split(',') : [])].map((k) => [k, null]))
    return {
      value,
      open: (v: string | number, also?: Patch) => set({ ...clear, ...also, [key]: v }, { overlay: key }),
      change: (v: string | number, also?: Patch) => set({ ...also, [key]: v }, { replace: true }),
      close: () => {
        if (!new URLSearchParams(window.location.search).has(key)) return
        const k = currentKey()
        if (k !== undefined && k === leaving) return
        if (currentState()?.overlay === key) {
          leaving = k
          navigate(-1)
        } else set(clear, { replace: true })
      },
    }
  }, [key, relatedKey, value, set, navigate])
}

export type UrlOverlay = ReturnType<typeof useUrlOverlay>

/**
 * Navigation away from an open overlay (e.g. to the plan a dialog just created): replaces the overlay's entry when it was pushed,
 * so Back returns to the page without the dialog instead of reopening it.
 */
export function leaveOverlay(navigate: NavigateFunction, to: string) {
  navigate(to, { replace: !!currentState()?.overlay })
}

/** The file panel (`EntryDrawer`) of a page: `?file=<entry id>` and its viewer tab `?fileTab=`. Dialogs use their own key. */
export function useEntryPreview(key = 'file') {
  const tabKey = `${key}Tab`
  const overlay = useUrlOverlay(key, [tabKey, `${key}Plan`])
  const [params, set] = useUrlParams()
  const n = overlay.value === null ? NaN : Number(overlay.value)
  const id = Number.isInteger(n) ? n : null
  const tab = params.get(tabKey) ?? 'preview'
  return useMemo(
    () => ({
      key,
      id,
      tab,
      open: (entryId: number) => overlay.open(entryId),
      /** Previous/next file: in place, so Back still closes the panel. */
      step: (entryId: number) => overlay.change(entryId),
      close: overlay.close,
      setTab: (t: string) => set({ [tabKey]: t === 'preview' ? null : t }, { replace: true }),
    }),
    [key, id, tab, overlay, set, tabKey],
  )
}

export type EntryPreview = ReturnType<typeof useEntryPreview>
