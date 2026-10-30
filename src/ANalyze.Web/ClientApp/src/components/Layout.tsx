import { Suspense, useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router'
import { useMutation } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../api/client'
import { useConfig, useIntegrations, useRoots, useSummary } from '../api/hooks'
import { useTheme, type Theme } from '../hooks/useTheme'
import { useHasUnseenRelease } from '../hooks/useWhatsNew'
import { isActive, useJob, useJobs } from '../hooks/useJobs'
import { useScope } from '../hooks/useScope'
import { integrationUis } from '../integrations/registry'
import { ago, bytes, pct, signedBytes } from '../lib/format'
import { phaseHint } from '../lib/phases'
import { Loading, Progress } from './ui'

const COLLAPSE_KEY = 'analyze.sidebar'

function initialCollapsed() {
  try {
    const v = localStorage.getItem(COLLAPSE_KEY)
    if (v !== null) return v === '1'
  } catch {
    /* ignore */
  }
  return window.matchMedia('(max-width: 768px)').matches
}

/** App shell modelled on nopds/ABook: collapsible sidebar with navigation, live scan, roots, reclaimable space and pins. */
export function Layout({ onLogout }: { onLogout?: () => void }) {
  const config = useConfig()
  const roots = useRoots()
  const summary = useSummary()
  const integrations = useIntegrations()
  const { rootId, setRootId } = useScope()
  const { theme, setTheme } = useTheme()
  const [collapsed, setCollapsed] = useState(initialCollapsed)
  const [query, setQuery] = useState('')
  const navigate = useNavigate()
  const location = useLocation()

  useEffect(() => {
    try {
      localStorage.setItem(COLLAPSE_KEY, collapsed ? '1' : '0')
    } catch {
      /* ignore */
    }
  }, [collapsed])

  useEffect(() => {
    if (window.matchMedia('(max-width: 768px)').matches) setCollapsed(true)
  }, [location.pathname])

  const unseenRelease = useHasUnseenRelease(config.data?.version)
  const s = summary.data
  const enabledIntegrations = integrationUis.filter((u) => integrations.data?.some((i) => i.id === u.id && i.settings.enabled))
  const nav: { to: string; icon: string; label: string; end?: boolean; badge?: number }[] = [
    { to: '/', icon: '📊', label: 'Overview', end: true },
    { to: '/browse', icon: '🗂️', label: 'Browse' },
    { to: '/search', icon: '🔎', label: 'Search' },
    { to: '/duplicates', icon: '👯', label: 'Duplicates' },
    { to: '/insights', icon: '💡', label: 'Insights' },
    { to: '/rules', icon: '🏷️', label: 'Categories & rules' },
    { to: '/ai', icon: '✨', label: 'AI', badge: (s?.aiReview ?? 0) + (s?.aiSuggestions ?? 0) || undefined },
    { to: '/actions', icon: '🧹', label: 'Actions', badge: s?.draftPlans || undefined },
    ...enabledIntegrations.map((i) => ({ to: `/integrations/${i.id}`, icon: i.icon, label: i.label })),
    { to: '/integrations', icon: '🔌', label: 'Integrations', end: true },
    { to: '/logs', icon: '📜', label: 'Activity log' },
    { to: '/settings', icon: '⚙️', label: 'Settings' },
  ]

  const submit = (e: FormEvent) => {
    e.preventDefault()
    const q = query.trim()
    if (q) navigate(`/search?q=${encodeURIComponent(q)}`)
  }

  const themes: Theme[] = ['system', 'light', 'dark']
  const nextTheme = themes[(themes.indexOf(theme) + 1) % themes.length]
  const rootList = roots.data ?? []

  return (
    <div className="flex h-dvh overflow-hidden">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:top-2 focus:left-2 focus:z-50 btn-primary">
        Skip to content
      </a>
      <aside
        className={clsx(
          'z-30 flex shrink-0 flex-col overflow-hidden border-r border-line bg-surface transition-[width] duration-200',
          collapsed ? 'w-12' : 'w-[min(80vw,16.5rem)] max-md:absolute max-md:inset-y-0 max-md:left-0 max-md:shadow-2xl',
        )}
      >
        <div className="shrink-0 px-1 pt-1.5 pb-1">
          <button className="sidebar-btn" onClick={() => setCollapsed((v) => !v)} title="Menu" aria-expanded={!collapsed}>
            <span className="w-[22px] shrink-0 text-center">☰</span>
            <Label collapsed={collapsed}>
              <span className="font-semibold">ANalyze</span> <span className="text-[0.72rem] text-muted">by CHDS</span>
            </Label>
            {!collapsed && config.data?.version && (
              <Link
                to="/release-notes"
                onClick={(e) => e.stopPropagation()}
                title={unseenRelease ? "What's new in this version" : 'Release notes'}
                className="relative shrink-0 rounded-full border border-accent/25 bg-accent-faint px-1.5 text-[0.65rem] leading-relaxed text-muted no-underline hover:text-accent"
              >
                v{config.data.version}
                {unseenRelease && <span className="absolute -top-0.5 -right-0.5 h-1.5 w-1.5 rounded-full bg-amber" aria-label="New" />}
              </Link>
            )}
          </button>
          {collapsed ? (
            <Link to="/search" className="sidebar-btn" title="Search">
              <span className="w-[22px] shrink-0 text-center">🔍</span>
            </Link>
          ) : (
            <form onSubmit={submit} role="search" className="px-1 pt-1">
              <input type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Find files…" aria-label="Find files" className="input" />
            </form>
          )}
        </div>

        <nav className="flex-1 overflow-x-hidden overflow-y-auto px-1 py-0.5" aria-label="Main">
          {nav.map((n) => (
            <NavLink
              key={n.to}
              to={n.to}
              end={n.end}
              title={n.label}
              className={({ isActive }) => clsx('sidebar-btn', isActive && 'bg-accent-faint text-accent')}
            >
              <span className="w-[22px] shrink-0 text-center">{n.icon}</span>
              <Label collapsed={collapsed}>{n.label}</Label>
              {!collapsed && n.badge ? <span className="rounded-full bg-amber-faint px-1.5 text-[0.7rem] font-semibold text-amber">{n.badge}</span> : null}
            </NavLink>
          ))}

          {!collapsed && (
            <>
              <ScanWidget />

              {rootList.length > 0 && (
                <>
                  <Divider />
                  <SectionTitle>Roots</SectionTitle>
                  {rootList.length > 1 && (
                    <SidebarEntry icon="🗄️" title="All roots" active={rootId === undefined} onClick={() => setRootId(undefined)} />
                  )}
                  {rootList.map((r) => (
                    <SidebarEntry
                      key={r.id}
                      icon={r.missing ? '⚠️' : r.readOnly ? '🔒' : '💾'}
                      title={r.name}
                      subtitle={
                        r.missing
                          ? 'Not mounted'
                          : `${bytes(r.size)}${r.diskTotal ? ` · ${bytes(r.diskFree)} free of ${bytes(r.diskTotal)}` : ''}${r.readOnly ? ' · read-only' : ''}`
                      }
                      active={rootId === r.id || rootList.length === 1}
                      onClick={() => setRootId(rootList.length === 1 ? undefined : r.id)}
                    >
                      {r.diskTotal ? (
                        <div className="mt-1 h-1 overflow-hidden rounded-full bg-surface-2">
                          <div className="h-full rounded-full bg-accent" style={{ width: pct(r.diskTotal - (r.diskFree ?? 0), r.diskTotal) }} />
                        </div>
                      ) : null}
                    </SidebarEntry>
                  ))}
                </>
              )}

              {s && (
                <>
                  <Divider />
                  <SectionTitle>Reclaimable</SectionTitle>
                  {s.reclaimable.duplicates.bytes > 0 && (
                    <SidebarEntry
                      icon="👯"
                      title="Duplicates"
                      value={bytes(s.reclaimable.duplicates.bytes)}
                      subtitle={`${s.reclaimable.duplicates.groups} groups`}
                      onClick={() => navigate('/duplicates')}
                    />
                  )}
                  {s.reclaimable.categories
                    .filter((c) => c.bytes > 0)
                    .map((c) => (
                      <SidebarEntry
                        key={c.id}
                        icon={<span className="inline-block h-2 w-2 rounded-full" style={{ background: c.color }} />}
                        title={c.name}
                        value={bytes(c.bytes)}
                        subtitle={`${c.files.toLocaleString()} files`}
                        onClick={() => navigate(`/search?categoryId=${c.id}`)}
                      />
                    ))}
                  {s.trashBytes > 0 && (
                    <SidebarEntry icon="🗑️" title="Trash" value={bytes(s.trashBytes)} subtitle={`${s.trashItems} items`} onClick={() => navigate('/actions?tab=trash')} />
                  )}
                </>
              )}

              {s && s.pinned.length > 0 && (
                <>
                  <Divider />
                  <SectionTitle>Pinned</SectionTitle>
                  {s.pinned.map((p) => (
                    <SidebarEntry
                      key={p.id}
                      icon="📌"
                      title={p.name || '/'}
                      value={bytes(p.size)}
                      subtitle={p.delta ? `${signedBytes(p.delta)} since last scan` : p.relPath}
                      active={location.pathname === `/browse/${p.id}`}
                      onClick={() => navigate(`/browse/${p.id}`)}
                    />
                  ))}
                </>
              )}
            </>
          )}
        </nav>

        <div className="shrink-0 border-t border-line px-1 pt-1 pb-2">
          <NavLink
            to="/release-notes"
            title="Release notes"
            className={({ isActive }) => clsx('sidebar-btn', isActive && 'bg-accent-faint text-accent')}
          >
            <span className="w-[22px] shrink-0 text-center">📝</span>
            <Label collapsed={collapsed}>Release notes</Label>
            {!collapsed && unseenRelease && <span className="rounded-full bg-amber-faint px-1.5 text-[0.7rem] font-semibold text-amber">new</span>}
          </NavLink>
          {onLogout && (
            <button className="sidebar-btn" onClick={onLogout} title="Sign out">
              <span className="w-[22px] shrink-0 text-center">🚪</span>
              <Label collapsed={collapsed}>Sign out</Label>
            </button>
          )}
          <button className="sidebar-btn" onClick={() => setTheme(nextTheme)} title={`Theme: ${theme}`}>
            <span className="w-[22px] shrink-0 text-center">{theme === 'dark' ? '🌙' : theme === 'light' ? '☀️' : '◐'}</span>
            <Label collapsed={collapsed}>Theme: {theme}</Label>
          </button>
          {!collapsed && (
            <div className="truncate px-2 pt-1.5 text-[0.68rem] text-muted">
              by CHDS · built with Claude
            </div>
          )}
        </div>
      </aside>

      {!collapsed && <div className="fixed inset-0 z-20 bg-black/40 md:hidden" onClick={() => setCollapsed(true)} />}

      <main id="main" className="min-w-0 flex-1 overflow-y-auto bg-bg px-4 py-6 md:px-8">
        <div className="mx-auto max-w-7xl">
          {config.data?.readOnly && (
            <div className="mb-4 rounded-lg border border-line bg-surface-2 px-4 py-2 text-sm muted">
              🔒 {config.data.forcedReadOnly ? 'Read-only mode is on' : 'All roots are mounted read-only'}: analysis works, actions are disabled.
            </div>
          )}
          <Suspense fallback={<Loading />}>
            <Outlet />
          </Suspense>
        </div>
      </main>
    </div>
  )
}

/** Live progress of whatever runs now (scan, apply, AI, integration sync), plus Scan now / Cancel. */
function ScanWidget() {
  const jobs = useJobs()
  const scan = useJob('scan')
  // Types, categories, hashes and duplicates follow the walk in the background; Cancel stops both.
  const processing = useJob('processing')
  const roots = useRoots()
  const { rootId } = useScope()
  const start = useMutation({ mutationFn: (only?: number) => api('/scan', { method: 'POST', json: { rootId: only } }) })
  const scoped = roots.data?.find((r) => r.id === rootId)
  const cancel = useMutation({ mutationFn: () => api('/scan/cancel', { method: 'POST' }) })
  const active = Object.values(jobs).filter(isActive)
  const lastScan = roots.data?.map((r) => r.lastScanAt).filter(Boolean).sort().at(-1)
  return (
    <>
      <Divider />
      <SectionTitle>Activity</SectionTitle>
      <div className="space-y-2 px-2 py-1 text-[0.8rem]">
        {active.length === 0 && (
          <div className="muted">
            Last scan {ago(lastScan)}
            {scan?.state === 'failed' && <span className="block text-danger">Last run failed: {scan.message}</span>}
          </div>
        )}
        {active.map((j) => (
          <div key={j.kind}>
            <div className="flex items-center gap-2">
              <span className="min-w-0 flex-1 truncate font-semibold">{j.title ?? j.kind}</span>
              {j.phase && (
                <span className="cursor-help muted underline decoration-dotted underline-offset-2" title={phaseHint(j.kind, j.phase) ?? j.phase}>
                  {j.phase}
                </span>
              )}
            </div>
            <Progress value={j.done} max={j.total} className="mt-1" />
            <div className="mt-0.5 truncate muted" title={j.current}>
              {j.kind === 'scan' && j.phase === 'walking'
                ? `${(j.counters?.files ?? 0).toLocaleString()} files · ${bytes(j.counters?.bytes ?? 0)}`
                : j.total
                  ? `${j.done.toLocaleString()} / ${j.total.toLocaleString()}`
                  : j.current}
            </div>
          </div>
        ))}
        <div className="flex gap-1.5">
          {isActive(scan) ? (
            <button className="btn-secondary px-2 py-1 text-xs" onClick={() => cancel.mutate()}>
              Cancel scan
            </button>
          ) : (
            <>
              {scoped && (roots.data?.length ?? 0) > 1 && (
                <button className="btn-primary min-w-0 truncate px-2 py-1 text-xs" onClick={() => start.mutate(scoped.id)} disabled={start.isPending} title={`Scan only ${scoped.path}`}>
                  Scan {scoped.name}
                </button>
              )}
              <button
                className={clsx(scoped && (roots.data?.length ?? 0) > 1 ? 'btn-secondary' : 'btn-primary', 'px-2 py-1 text-xs')}
                onClick={() => start.mutate(undefined)}
                disabled={start.isPending}
              >
                {(roots.data?.length ?? 0) > 1 ? 'Scan all' : 'Scan now'}
              </button>
            </>
          )}
          {!isActive(scan) && isActive(processing) && (
            <button className="btn-secondary px-2 py-1 text-xs" onClick={() => cancel.mutate()} title="Stop detecting types and hashing in the background; the next scan picks it up again">
              Stop
            </button>
          )}
          <Link to="/logs" className="btn-ghost px-2 py-1 text-xs no-underline" title="What ANalyze is doing, step by step">
            Log
          </Link>
        </div>
      </div>
    </>
  )
}

function Label({ collapsed, children }: { collapsed: boolean; children: ReactNode }) {
  return <span className={clsx('min-w-0 flex-1 truncate transition-opacity', collapsed && 'w-0 opacity-0')}>{children}</span>
}

function Divider() {
  return <div className="mx-0.5 my-1 h-px bg-line" />
}

function SectionTitle({ children }: { children: ReactNode }) {
  return <div className="truncate px-2 pt-1.5 pb-0.5 text-[0.7rem] font-bold tracking-wider text-muted uppercase">{children}</div>
}

function SidebarEntry({ icon, title, subtitle, value, active, onClick, children }: {
  icon: ReactNode
  title: string
  subtitle?: string
  value?: string
  active?: boolean
  onClick: () => void
  children?: ReactNode
}) {
  return (
    <button
      onClick={onClick}
      title={title}
      className={clsx(
        'flex w-full items-start gap-1.5 overflow-hidden rounded-md px-2 py-1.5 text-left text-[0.82rem] leading-snug hover:bg-accent-faint',
        active && 'bg-accent-faint text-accent',
      )}
    >
      <span className="flex w-4 shrink-0 justify-center pt-0.5 text-[0.85rem]">{icon}</span>
      <span className="min-w-0 flex-1">
        <span className="flex items-baseline gap-2">
          <span className="block min-w-0 flex-1 truncate font-semibold">{title}</span>
          {value && <span className="shrink-0 text-[0.76rem] font-semibold text-amber tabular">{value}</span>}
        </span>
        {subtitle && <span className="mt-px block truncate text-[0.74rem] text-muted">{subtitle}</span>}
        {children}
      </span>
    </button>
  )
}
