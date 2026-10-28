import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Loader2 } from 'lucide-react'
import { Link } from 'react-router'
import clsx from 'clsx'
import type { Annotation, Category } from '../api/types'

export function Spinner({ className }: { className?: string }) {
  return <Loader2 className={clsx('animate-spin text-accent', className ?? 'h-6 w-6')} aria-hidden />
}

export function Loading({ label = 'Loading…' }: { label?: string }) {
  return (
    <div className="flex items-center justify-center gap-3 py-16 muted" role="status">
      <Spinner />
      <span>{label}</span>
    </div>
  )
}

export function ErrorBox({ error }: { error: unknown }) {
  const message = error instanceof Error ? error.message : String(error)
  return (
    <div className="rounded-lg border border-danger/30 bg-danger/10 p-4 text-sm text-danger" role="alert">
      <strong>Error:</strong> {message}
    </div>
  )
}

export function Empty({ children }: { children?: ReactNode }) {
  return <div className="py-16 text-center muted">{children ?? 'Nothing here.'}</div>
}

export function PageTitle({ children, actions, subtitle }: { children: ReactNode; actions?: ReactNode; subtitle?: ReactNode }) {
  return (
    <div className="mb-6">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="min-w-0 flex-1 text-[1.4rem] font-bold">{children}</h1>
        {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
      </div>
      {subtitle && <p className="mt-1 text-sm muted">{subtitle}</p>}
    </div>
  )
}

export function Section({ title, actions, children, className }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={clsx('card min-w-0 p-4', className)}>
      {(title || actions) && (
        <div className="mb-3 flex flex-wrap items-center gap-2">
          {title && <h2 className="min-w-0 flex-1 text-sm font-semibold tracking-wide text-muted uppercase">{title}</h2>}
          {actions}
        </div>
      )}
      {children}
    </section>
  )
}

export function Stat({ label, value, hint, tone }: { label: string; value: ReactNode; hint?: ReactNode; tone?: 'amber' | 'accent' }) {
  return (
    <div className="card px-4 py-3">
      <div className="text-xs font-medium tracking-wide text-muted uppercase">{label}</div>
      <div className={clsx('mt-1 text-2xl font-bold tabular', tone === 'amber' && 'text-amber', tone === 'accent' && 'text-accent')}>{value}</div>
      {hint && <div className="mt-0.5 text-xs muted">{hint}</div>}
    </div>
  )
}

export function Toggle({ checked, onChange, label, hint, disabled }: { checked: boolean; onChange: (v: boolean) => void; label: ReactNode; hint?: ReactNode; disabled?: boolean }) {
  return (
    <label className={clsx('flex items-start gap-3 py-1.5', disabled ? 'opacity-50' : 'cursor-pointer')}>
      <input type="checkbox" disabled={disabled} className="mt-0.5 h-4 w-4 accent-accent" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span>
        <span className="text-sm font-medium">{label}</span>
        {hint && <span className="block text-xs muted">{hint}</span>}
      </span>
    </label>
  )
}

export function Field({ label, hint, children }: { label: ReactNode; hint?: ReactNode; children: ReactNode }) {
  return (
    <label className="block">
      <span className="label">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-xs muted">{hint}</span>}
    </label>
  )
}

export function Modal({ open, onClose, title, children, wide }: { open: boolean; onClose: () => void; title: ReactNode; children: ReactNode; wide?: boolean }) {
  const ref = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const d = ref.current
    if (!d) return
    if (open && !d.open) d.showModal()
    if (!open && d.open) d.close()
  }, [open])
  return (
    <dialog
      ref={ref}
      onClose={onClose}
      onClick={(e) => e.target === ref.current && onClose()}
      className={clsx(
        'm-auto rounded-xl border border-line bg-surface p-0 text-fg shadow-xl backdrop:bg-black/40',
        wide ? 'w-[min(64rem,calc(100vw-2rem))]' : 'w-[min(40rem,calc(100vw-2rem))]',
      )}
    >
      {open && (
        <>
          <div className="flex items-center justify-between border-b border-line px-5 py-3">
            <h2 className="font-semibold">{title}</h2>
            <button className="btn-ghost px-2 py-1" onClick={onClose} aria-label="Close">
              ✕
            </button>
          </div>
          <div className="max-h-[78vh] overflow-y-auto p-5">{children}</div>
        </>
      )}
    </dialog>
  )
}

export function Tabs<T extends string>({ tabs, value, onChange }: { tabs: { id: T; label: ReactNode }[]; value: T; onChange: (v: T) => void }) {
  return (
    <div className="mb-4 flex flex-wrap gap-1 border-b border-line" role="tablist">
      {tabs.map((t) => (
        <button
          key={t.id}
          role="tab"
          aria-selected={value === t.id}
          onClick={() => onChange(t.id)}
          className={clsx(
            '-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors',
            value === t.id ? 'border-accent text-accent' : 'border-transparent text-muted hover:text-fg',
          )}
        >
          {t.label}
        </button>
      ))}
    </div>
  )
}

/** Horizontal share bar used in lists: thin, rounded, colored by category when known. */
export function ShareBar({ value, max, color, className }: { value: number; max: number; color?: string; className?: string }) {
  const w = max > 0 ? Math.max(value > 0 ? 1.5 : 0, (value / max) * 100) : 0
  return (
    <div className={clsx('h-1.5 w-full overflow-hidden rounded-full bg-surface-2', className)} aria-hidden>
      <div className="h-full rounded-full" style={{ width: `${w}%`, background: color ?? 'var(--accent)' }} />
    </div>
  )
}

export function CategoryPill({ category, source }: { category?: Category; source?: string }) {
  if (!category) return <span className="pill-muted">Uncategorized</span>
  return (
    <span className="inline-flex items-center gap-1.5 rounded px-2 py-0.5 text-xs text-fg bg-surface-2" title={source ? `Assigned by ${source}` : undefined}>
      <span className="h-2 w-2 shrink-0 rounded-full" style={{ background: category.color }} />
      {category.name}
      {source === 'ai' && <span className="text-[0.65rem] text-accent">AI</span>}
      {source === 'manual' && <span className="text-[0.65rem] text-muted">✎</span>}
    </span>
  )
}

const KIND_ICON: Record<string, string> = {
  movie: '🎬', episode: '📺', show: '📺', season: '📺', track: '🎵', album: '💿', artist: '🎤', library: '📚', backup: '🛟', 'backup-root': '🛟',
}

/** Badge tone by annotation state: needs attention (amber), damaged (red), otherwise the accent. */
function annotationTone(state?: string) {
  if (state === 'corrupt') return 'bg-danger/10 text-danger'
  if (state === 'unwatched' || state === 'missing' || state === 'unsynced') return 'bg-amber-faint text-amber'
  return 'bg-accent-faint text-accent'
}

/** Integration labels (e.g. Plex) shown next to files and folders; each opens its integration's page at that file or folder. */
export function AnnotationBadges({ items, compact }: { items?: Annotation[]; compact?: boolean }) {
  if (!items?.length) return null
  return (
    <span className="inline-flex flex-wrap items-center gap-1">
      {items.map((a) => (
        <Link
          key={a.source + a.kind}
          to={`/integrations/${a.source}?entry=${a.entryId}`}
          onClick={(e) => e.stopPropagation()}
          title={`${a.source}: ${a.label}${a.detail ? ` · ${a.detail}` : ''}${a.state ? ` · ${a.state}` : ''}`}
          className={clsx(
            'inline-flex max-w-[22rem] items-center gap-1 truncate rounded px-1.5 py-0.5 text-[0.7rem] no-underline hover:underline',
            annotationTone(a.state),
          )}
        >
          <span>{KIND_ICON[a.kind] ?? '🔗'}</span>
          {!compact && <span className="truncate">{a.label}</span>}
          {a.state === 'watched' && <span title="Watched">✓</span>}
        </Link>
      ))}
    </span>
  )
}

/** Button that asks for a second click before running a destructive action. */
export function ConfirmButton({ onConfirm, children, className, confirmLabel = 'Click again to confirm', disabled }: {
  onConfirm: () => void
  children: ReactNode
  className?: string
  confirmLabel?: string
  disabled?: boolean
}) {
  const [armed, setArmed] = useState(false)
  useEffect(() => {
    if (!armed) return
    const t = setTimeout(() => setArmed(false), 4000)
    return () => clearTimeout(t)
  }, [armed])
  return (
    <button
      className={className ?? 'btn-danger'}
      disabled={disabled}
      onClick={() => {
        if (armed) {
          setArmed(false)
          onConfirm()
        } else setArmed(true)
      }}
    >
      {armed ? confirmLabel : children}
    </button>
  )
}

export function Progress({ value, max, className }: { value: number; max: number; className?: string }) {
  const indeterminate = !max
  return (
    <div className={clsx('h-1.5 w-full overflow-hidden rounded-full bg-surface-2', className)} role="progressbar" aria-valuenow={value} aria-valuemax={max}>
      <div
        className={clsx('h-full rounded-full bg-accent transition-[width]', indeterminate && 'w-1/3 animate-pulse')}
        style={indeterminate ? undefined : { width: `${Math.min(100, (value / max) * 100)}%` }}
      />
    </div>
  )
}
