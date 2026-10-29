import type { ReactNode } from 'react'
import clsx from 'clsx'
import { useToHostPath } from '../api/hooks'

/** A path as ANalyze sees it, with the host path after it when one is known (Settings → Host paths, or Docker mounts). */
export function PathText({ path, label, className }: { path?: string | null; label?: ReactNode; className?: string }) {
  const toHost = useToHostPath()
  if (!path) return null
  const host = toHost(path)
  return (
    <span className={clsx('block min-w-0 truncate font-mono text-xs', className)} title={host ? `${path}\nOn the host: ${host}` : path}>
      {label && <span className="font-sans muted">{label} </span>}
      {path}
      {host && <span className="font-sans muted"> · host {host}</span>}
    </span>
  )
}
