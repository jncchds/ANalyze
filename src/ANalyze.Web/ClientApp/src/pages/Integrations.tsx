import { useState } from 'react'
import { Link } from 'react-router'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { useIntegrations } from '../api/hooks'
import type { AutoMappings, Integration, IntegrationField, IntegrationSettings } from '../api/types'
import { Empty, ErrorBox, Field, Loading, PageTitle, SavedLater, Section, Toggle } from '../components/ui'
import { isActive, useJob } from '../hooks/useJobs'
import { integrationUi } from '../integrations/registry'
import { ago } from '../lib/format'
import { phaseHint } from '../lib/phases'

interface Mapping {
  from: string
  to: string
}

/** Lists integration plugins; each settings form is rendered from the plugin's field definitions. */
export default function Integrations() {
  const q = useIntegrations()
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  return (
    <>
      <PageTitle subtitle="Plugins that cross-reference your files with other apps. Labels they add show up next to files and folders in Browse and Search.">Integrations</PageTitle>
      {!q.data?.length ? (
        <Empty>No integrations installed.</Empty>
      ) : (
        <div className="space-y-4">
          {/* Enabled ones first; enabling one also adds its page to the sidebar. */}
          {[...q.data].sort((a, b) => Number(b.settings.enabled) - Number(a.settings.enabled)).map((i) => (
            <IntegrationCard key={i.id} integration={i} />
          ))}
        </div>
      )}
    </>
  )
}

function IntegrationCard({ integration }: { integration: Integration }) {
  const qc = useQueryClient()
  const job = useJob(`integration:${integration.id}`)
  const [s, setS] = useState<IntegrationSettings>(integration.settings)
  const [dirty, setDirty] = useState(false)
  const ui = integrationUi(integration.id)
  const update = (patch: Partial<IntegrationSettings>) => {
    setS({ ...s, ...patch })
    setDirty(true)
  }
  const setValue = (key: string, value: unknown) => update({ values: { ...s.values, [key]: value } })

  const [savedLater, setSavedLater] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: () => api<IntegrationSettings & { savedLater?: string | null }>(`/integrations/${integration.id}`, { method: 'PUT', json: s }),
    onSuccess: ({ savedLater, ...saved }) => {
      setSavedLater(savedLater ?? null)
      setS(saved)
      setDirty(false)
      qc.invalidateQueries({ queryKey: ['integrations'] })
    },
  })
  const test = useMutation({ mutationFn: () => api<{ ok: boolean; message: string; details?: unknown }>(`/integrations/${integration.id}/test`, { method: 'POST', json: s }) })
  const sync = useMutation({ mutationFn: () => api(`/integrations/${integration.id}/sync`, { method: 'POST' }) })
  const suggest = useMutation({
    mutationFn: () => api<Mapping[]>(`/integrations/${integration.id}/suggest-mappings`, { method: 'POST', json: s }),
  })
  const syncing = isActive(job)
  const last = integration.status.lastResult

  return (
    <Section
      title={
        <span className="normal-case">
          <span className="text-base font-semibold text-fg">{integration.name}</span>
        </span>
      }
      actions={
        <>
          {ui && s.enabled && (
            <Link to={`/integrations/${integration.id}`} className="btn-ghost">
              Open {ui.label} view →
            </Link>
          )}
        </>
      }
    >
      <p className="mb-3 text-sm muted">{integration.description}</p>
      <div className="mb-3 flex flex-wrap gap-6">
        <Toggle checked={s.enabled} onChange={(v) => update({ enabled: v })} label="Enabled" hint={ui ? 'Also shows its page in the sidebar.' : undefined} />
        {s.enabled && <Toggle checked={s.syncAfterScan} onChange={(v) => update({ syncAfterScan: v })} label="Sync after every scan" />}
      </div>
      {!s.enabled ? (
        dirty && (
          <div className="flex justify-end">
            <button className="btn-primary" onClick={() => save.mutate()} disabled={save.isPending}>
              Save
            </button>
          </div>
        )
      ) : (
      <>
      <div className="grid gap-3 md:grid-cols-2">
        {integration.fields.map((f) => (
          <FieldInput key={f.key} field={f} value={s.values[f.key]} onChange={(v) => setValue(f.key, v)} onSuggest={f.type === 'mappings' ? () => suggest.mutate() : undefined} suggestions={suggest.data} suggesting={suggest.isPending} auto={integration.auto} />
        ))}
      </div>
      {suggest.error && <div className="mt-2"><ErrorBox error={suggest.error} /></div>}
      {test.data && (
        <div className={`mt-3 rounded-md p-3 text-sm ${test.data.ok ? 'bg-success/10 text-success' : 'bg-danger/10 text-danger'}`}>
          {test.data.ok ? '✓ ' : '✕ '}
          {test.data.message}
          {Array.isArray(test.data.details) && (
            <ul className="mt-1 text-xs text-fg">
              {(test.data.details as { title: string; type: string; locations: string[] }[]).map((d) => (
                <li key={d.title}>
                  <b>{d.title}</b> ({d.type}): <span className="font-mono">{d.locations.join(', ')}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
      {savedLater && !dirty && <div className="mt-2"><SavedLater holder={savedLater} /></div>}
      {(save.error || test.error || sync.error) && <div className="mt-2"><ErrorBox error={save.error ?? test.error ?? sync.error} /></div>}
      <div className="mt-4 flex flex-wrap items-center gap-2">
        <span className="flex-1 text-xs muted" title={syncing ? phaseHint(`integration:${integration.id}`, job?.phase) : undefined}>
          {job?.state === 'queued' ? `Queued: ${job.message ?? 'waiting for the running job'}` : syncing ? `Syncing… ${job?.phase ?? ''} ${job?.done ? job.done.toLocaleString() : ''}` : last ? `Last sync ${ago(integration.status.lastSync)}: ${last.message}` : job?.message ?? 'Never synced'}
        </span>
        <button className="btn-secondary" onClick={() => test.mutate()} disabled={test.isPending}>
          Test connection
        </button>
        <button className="btn-secondary" onClick={() => sync.mutate()} disabled={dirty || !s.enabled || syncing}>
          Sync now
        </button>
        <button className="btn-primary" onClick={() => save.mutate()} disabled={!dirty || save.isPending}>
          Save
        </button>
      </div>
      </>
      )}
    </Section>
  )
}

function FieldInput({ field, value, onChange, onSuggest, suggestions, suggesting, auto }: {
  field: IntegrationField
  value: unknown
  onChange: (v: unknown) => void
  onSuggest?: () => void
  suggestions?: Mapping[]
  suggesting?: boolean
  auto?: AutoMappings[]
}) {
  switch (field.type) {
    case 'bool':
      return <Toggle checked={!!value} onChange={onChange} label={field.label} hint={field.help} />
    case 'number':
      return (
        <Field label={field.label} hint={field.help}>
          <input className="input" type="number" value={(value as number | undefined) ?? ''} onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))} />
        </Field>
      )
    case 'mappings': {
      const list = (Array.isArray(value) ? value : []) as Mapping[]
      const set = (next: Mapping[]) => onChange(next)
      return (
        <div className="md:col-span-2">
          <span className="label">{field.label}</span>
          <div className="space-y-2">
            {list.map((m, i) => (
              <div key={i} className="flex items-center gap-2">
                <input className="input font-mono" placeholder="Path in the other app (/data/movies)" value={m.from} onChange={(e) => set(list.map((x, j) => (j === i ? { ...x, from: e.target.value } : x)))} />
                <span className="muted">→</span>
                <input className="input font-mono" placeholder="Path here (/library/Movies)" value={m.to} onChange={(e) => set(list.map((x, j) => (j === i ? { ...x, to: e.target.value } : x)))} />
                <button className="btn-ghost px-2" onClick={() => set(list.filter((_, j) => j !== i))} aria-label="Remove mapping">
                  ✕
                </button>
              </div>
            ))}
          </div>
          <div className="mt-2 flex flex-wrap items-center gap-2">
            <button className="btn-ghost px-2 py-1 text-sm" onClick={() => set([...list, { from: '', to: '' }])}>
              + Add mapping
            </button>
            {onSuggest && (
              <button className="btn-ghost px-2 py-1 text-sm" onClick={onSuggest} disabled={suggesting}>
                {suggesting ? 'Looking for matching files…' : '✨ Suggest from files'}
              </button>
            )}
          </div>
          {suggestions && (
            <div className="mt-2 rounded-md border border-line p-2 text-sm">
              {suggestions.length === 0 ? (
                <span className="muted">No mapping needed or none found (scan first, then try again).</span>
              ) : (
                suggestions.map((m) => (
                  <div key={m.from + m.to} className="flex items-center gap-2">
                    <span className="min-w-0 flex-1 truncate font-mono text-xs">
                      {m.from} → {m.to}
                    </span>
                    <button className="btn-secondary px-2 py-0.5 text-xs" onClick={() => set([...list.filter((x) => x.from !== m.from), m])}>
                      Use
                    </button>
                  </div>
                ))
              )}
            </div>
          )}
          {auto?.map((a) => (
            <div key={a.source} className="mt-2 rounded-md border border-line bg-surface-2 p-2 text-xs">
              <div className="mb-1 font-semibold">
                🐳 From {a.source === 'docker' ? 'Docker' : a.source}
                {a.container ? `: container "${a.container}"` : ''} <span className="font-normal muted">(used automatically)</span>
              </div>
              {a.note && <div className="muted">{a.note}</div>}
              {a.mappings.map((m) => (
                <div key={m.from} className="truncate font-mono">
                  {m.from} <span className="muted">→</span> {m.to}
                </div>
              ))}
              {a.container && a.mappings.length === 0 && <div className="muted">The container shares no folder with ANalyze's roots.</div>}
            </div>
          ))}
          {field.help && <span className="mt-1 block text-xs muted">{field.help}</span>}
        </div>
      )
    }
    default:
      return (
        <Field label={field.label} hint={field.help}>
          <input
            className="input"
            type={field.type === 'password' ? 'password' : field.type === 'url' ? 'url' : 'text'}
            placeholder={field.placeholder}
            value={(value as string | undefined) ?? ''}
            autoComplete="off"
            onChange={(e) => onChange(e.target.value)}
          />
        </Field>
      )
  }
}
