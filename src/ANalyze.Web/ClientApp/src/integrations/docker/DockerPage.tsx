import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { api } from '../../api/client'
import { Empty, ErrorBox, Loading, PageTitle, Section } from '../../components/ui'

interface Mount {
  type?: string
  source: string
  destination: string
}

interface Containers {
  version?: string
  inContainer: boolean
  self?: string
  selfMounts: Mount[]
  containers: {
    name: string
    image: string
    state?: string
    isSelf: boolean
    mounts: Mount[]
    apps: { id: string; name: string; mappings: { from: string; to: string }[] }[]
  }[]
  missing: { id: string; name: string }[]
}

/** Docker view: which container belongs to which integration and the path mappings worked out from their mounts. */
export default function DockerPage() {
  const q = useQuery({ queryKey: ['docker-containers'], queryFn: () => api<Containers>('/integrations/docker/containers') })
  if (q.isLoading) return <Loading />
  if (q.error)
    return (
      <>
        <PageTitle>Docker</PageTitle>
        <ErrorBox error={q.error} />
        <p className="mt-2 text-sm muted">
          Mount the Docker socket read-only into the ANalyze container and check the endpoint on the <Link to="/integrations" className="link">Integrations</Link> page.
        </p>
      </>
    )
  const d = q.data!
  const apps = d.containers.filter((c) => c.apps.length > 0)
  const rest = d.containers.filter((c) => c.apps.length === 0 && !c.isSelf)
  return (
    <>
      <PageTitle subtitle={`Docker ${d.version ?? ''} · ${d.containers.length} running containers`}>Docker</PageTitle>

      <Section title="ANalyze" className="mb-4">
        {d.inContainer && !d.self ? (
          <p className="text-sm text-danger">ANalyze's own container was not found. Set its name in Integrations → Docker; without it no mappings can be worked out.</p>
        ) : (
          <>
            <p className="mb-2 text-sm muted">{d.self ? `Container "${d.self}"` : 'Not in a container: paths here are host paths.'}</p>
            <MountList mounts={d.selfMounts} />
          </>
        )}
      </Section>

      <Section title="Apps with an integration" className="mb-4">
        {apps.length === 0 ? (
          <Empty>No enabled integration matched a container yet.</Empty>
        ) : (
          <div className="space-y-4">
            {apps.map((c) => (
              <div key={c.name}>
                <div className="flex flex-wrap items-center gap-2 text-sm">
                  <span className="font-semibold">{c.name}</span>
                  <span className="truncate text-xs muted">{c.image}</span>
                  {c.apps.map((a) => (
                    <Link key={a.id} to={`/integrations/${a.id}`} className="chip">
                      {a.name}
                    </Link>
                  ))}
                </div>
                {c.apps[0].mappings.length === 0 ? (
                  <p className="mt-1 text-xs text-amber">No shared folders with ANalyze — its files are not under a scanned root.</p>
                ) : (
                  <ul className="mt-1 space-y-0.5 font-mono text-xs">
                    {c.apps[0].mappings.map((m) => (
                      <li key={m.from}>
                        {m.from} <span className="muted">→</span> {m.to}
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            ))}
          </div>
        )}
        {d.missing.length > 0 && (
          <p className="mt-3 text-sm muted">
            No container found for {d.missing.map((m) => m.name).join(', ')}. Use the app's container name in its URL, or name it under Integrations → Docker → Container per app.
          </p>
        )}
      </Section>

      <Section title="Other containers">
        {rest.length === 0 ? (
          <Empty>None.</Empty>
        ) : (
          <div className="divide-y divide-line/60">
            {rest.map((c) => (
              <div key={c.name} className="py-2">
                <div className="text-sm">
                  <span className="font-semibold">{c.name}</span> <span className="text-xs muted">{c.image}</span>
                </div>
                <MountList mounts={c.mounts} />
              </div>
            ))}
          </div>
        )}
      </Section>
    </>
  )
}

function MountList({ mounts }: { mounts: Mount[] }) {
  if (mounts.length === 0) return <p className="text-xs muted">No mounts.</p>
  return (
    <ul className="space-y-0.5 font-mono text-xs">
      {mounts.map((m) => (
        <li key={m.destination} className="truncate">
          {m.source} <span className="muted">→</span> {m.destination}
        </li>
      ))}
    </ul>
  )
}
