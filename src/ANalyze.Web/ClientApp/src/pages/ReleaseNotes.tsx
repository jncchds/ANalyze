import { Fragment, useEffect, type ReactNode } from 'react'
import { useConfig, useReleaseNotes } from '../api/hooks'
import { markSeen } from '../hooks/useWhatsNew'
import { Empty, ErrorBox, Loading, PageTitle } from '../components/ui'

const KINDS: Record<string, { label: string; className: string }> = {
  feat: { label: 'New', className: 'chip' },
  fix: { label: 'Fix', className: 'pill-amber' },
  refactor: { label: 'Refactor', className: 'pill-muted' },
  perf: { label: 'Faster', className: 'chip' },
  docs: { label: 'Docs', className: 'pill-muted' },
}

/** Renders `code` spans; everything else stays plain text. */
function inline(text: string): ReactNode {
  return text.split(/(`[^`]+`)/).map((part, i) =>
    part.startsWith('`') && part.endsWith('`') && part.length > 1 ? (
      <code key={i} className="rounded bg-surface-2 px-1 font-mono text-[0.85em]">
        {part.slice(1, -1)}
      </code>
    ) : (
      <Fragment key={i}>{part}</Fragment>
    ),
  )
}

export default function ReleaseNotes() {
  const notes = useReleaseNotes()
  const config = useConfig()
  const current = config.data?.version

  useEffect(() => {
    if (current) markSeen(current)
  }, [current])

  return (
    <>
      <PageTitle subtitle="What changed in each version of ANalyze by CHDS.">Release notes</PageTitle>

      {notes.isLoading && <Loading />}
      {notes.error && <ErrorBox error={notes.error} />}
      {notes.data && notes.data.length === 0 && <Empty>No release notes in this build.</Empty>}

      <div className="space-y-4">
        {notes.data?.map((r) => (
          <section key={r.version} className="card p-4" aria-labelledby={`v-${r.version}`}>
            <div className="mb-3 flex flex-wrap items-baseline gap-2">
              <h2 id={`v-${r.version}`} className="text-lg font-bold">
                v{r.version}
              </h2>
              {r.version === current && <span className="chip">Installed</span>}
              {r.date && <span className="text-sm muted tabular">{r.date}</span>}
            </div>
            <ul className="space-y-2 text-sm">
              {r.items.map((item, i) => {
                const kind = KINDS[item.kind]
                return (
                  <li key={i} className="flex items-start gap-2">
                    <span className={`${kind?.className ?? 'pill-muted'} mt-px w-[4.75rem] shrink-0 justify-center`}>{kind?.label ?? item.kind}</span>
                    <span className="min-w-0 flex-1 leading-relaxed">{inline(item.text)}</span>
                  </li>
                )
              })}
            </ul>
          </section>
        ))}
      </div>

      <section className="card mt-6 p-4 text-sm" aria-label="Credits">
        <h2 className="mb-2 text-sm font-semibold tracking-wide text-muted uppercase">Credits</h2>
        <p className="leading-relaxed">
          <strong>ANalyze by CHDS</strong> — created and maintained by{' '}
          <a className="link" href="https://github.com/jncchds" target="_blank" rel="noopener noreferrer">
            CHDS
          </a>
          , built together with{' '}
          <a className="link" href="https://claude.com/claude-code" target="_blank" rel="noopener noreferrer">
            Claude
          </a>{' '}
          by Anthropic.
        </p>
        <p className="mt-1 muted">
          Source code and issues on{' '}
          <a className="link" href="https://github.com/jncchds/ANalyze" target="_blank" rel="noopener noreferrer">
            GitHub
          </a>
          .
        </p>
      </section>
    </>
  )
}
