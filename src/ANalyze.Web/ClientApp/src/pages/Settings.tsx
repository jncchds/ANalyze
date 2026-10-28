import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { useConfig, useRoots } from '../api/hooks'
import { ErrorBox, Field, Loading, PageTitle, Section, Toggle } from '../components/ui'
import { ago, bytes, dateTime, duration } from '../lib/format'

interface AiSettings {
  enabled: boolean
  baseUrl: string
  apiKey?: string | null
  model: string
  batchSize: number
  maxFilesPerRun: number
  maxConcurrency: number
  privacy: 'metadataOnly' | 'textSnippet'
  minConfidence: number
  allowNewCategories: boolean
  useJsonSchema: boolean
  autoClassifyAfterScan: boolean
  extraInstructions?: string
}

interface AppSettings {
  scanCron?: string
  scanOnStartup: boolean
  ignorePatterns: string[]
  crossFilesystems: boolean
  sniffUnknownFiles: boolean
  findDuplicates: boolean
  minDuplicateSize: number
  hashThreads: number
  trashRetentionDays: number
  verifyHashBeforeDelete: boolean
  historyDepth: number
  pinnedFolders: number[]
  ai: AiSettings
}

interface ScanRun {
  id: number
  status: string
  startedAt: string
  finishedAt?: string
  filesSeen: number
  added: number
  changed: number
  removed: number
  hashed: number
  errors: number
  message?: string
  subPath?: string
}

const CRON_PRESETS = [
  { label: 'Manual only', value: '' },
  { label: 'Every night at 03:00', value: '0 3 * * *' },
  { label: 'Every 6 hours', value: '0 */6 * * *' },
  { label: 'Sundays at 04:00', value: '0 4 * * 0' },
]

export default function Settings() {
  const qc = useQueryClient()
  const roots = useRoots()
  const config = useConfig()
  const q = useQuery({ queryKey: ['settings'], queryFn: () => api<AppSettings>('/settings') })
  const scans = useQuery({ queryKey: ['scans'], queryFn: () => api<ScanRun[]>('/scans', { query: { limit: 10 } }) })
  const [s, setS] = useState<AppSettings>()
  const [dirty, setDirty] = useState(false)
  useEffect(() => {
    if (q.data && !dirty) setS(q.data)
  }, [q.data, dirty])
  const save = useMutation({
    mutationFn: (v: AppSettings) => api<AppSettings>('/settings', { method: 'PUT', json: v }),
    onSuccess: (v) => {
      setS(v)
      setDirty(false)
      qc.invalidateQueries({ queryKey: ['config'] })
      qc.invalidateQueries({ queryKey: ['ai-status'] })
    },
  })
  const test = useMutation({ mutationFn: () => api<{ ok: boolean; reply?: string; milliseconds: number; error?: string }>('/ai/test', { method: 'POST', json: s?.ai }) })
  const refreshRoots = useMutation({ mutationFn: () => api('/roots/refresh', { method: 'POST' }), onSuccess: () => qc.invalidateQueries({ queryKey: ['roots'] }) })

  if (q.isLoading || !s) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const set = (patch: Partial<AppSettings>) => {
    setS({ ...s, ...patch })
    setDirty(true)
  }
  const setAi = (patch: Partial<AiSettings>) => set({ ai: { ...s.ai, ...patch } })

  return (
    <>
      <PageTitle
        actions={
          <button className="btn-primary" onClick={() => save.mutate(s)} disabled={!dirty || save.isPending}>
            {dirty ? 'Save changes' : 'Saved'}
          </button>
        }
      >
        Settings
      </PageTitle>
      {save.error && <ErrorBox error={save.error} />}
      <div className="grid gap-4 lg:grid-cols-2">
        <Section
          title="Roots"
          actions={
            <button className="btn-ghost px-2 py-1 text-xs" onClick={() => refreshRoots.mutate()}>
              Re-check access
            </button>
          }
        >
          <p className="mb-2 text-sm muted">Mounted folders, configured with ANalyze__Roots__0, __1… A root mounted read-only (or ANalyze__ReadOnly=true) can be analyzed but never changed.</p>
          <ul className="space-y-2 text-sm">
            {roots.data?.map((r) => (
              <li key={r.id} className="flex items-center gap-2">
                <span>{r.missing ? '⚠️' : r.readOnly ? '🔒' : '💾'}</span>
                <span className="min-w-0 flex-1 truncate font-mono text-xs">{r.path}</span>
                <span className="text-xs muted">{r.missing ? 'not mounted' : r.readOnly ? 'read-only' : 'read-write'}</span>
                <span className="text-xs muted">{r.lastScanAt ? `scanned ${ago(r.lastScanAt)}` : 'never scanned'}</span>
              </li>
            ))}
          </ul>
          <p className="mt-3 text-xs muted">Video thumbnails: {config.data?.ffmpeg ? 'ffmpeg available' : 'ffmpeg not found'}</p>
        </Section>

        <Section title="Scanning">
          <div className="space-y-3">
            <Field label="Automatic scans" hint="Cron expression (minute hour day month weekday), server time zone.">
              <div className="flex gap-2">
                <select className="input w-auto" value={CRON_PRESETS.some((p) => p.value === (s.scanCron ?? '')) ? s.scanCron ?? '' : 'custom'} onChange={(e) => e.target.value !== 'custom' && set({ scanCron: e.target.value })}>
                  {CRON_PRESETS.map((p) => (
                    <option key={p.value} value={p.value}>
                      {p.label}
                    </option>
                  ))}
                  <option value="custom">Custom…</option>
                </select>
                <input className="input font-mono" value={s.scanCron ?? ''} onChange={(e) => set({ scanCron: e.target.value })} placeholder="0 3 * * *" />
              </div>
            </Field>
            <Toggle checked={s.scanOnStartup} onChange={(v) => set({ scanOnStartup: v })} label="Scan when the container starts" />
            <Toggle checked={s.crossFilesystems} onChange={(v) => set({ crossFilesystems: v })} label="Descend into other filesystems mounted inside a root" />
            <Toggle checked={s.sniffUnknownFiles} onChange={(v) => set({ sniffUnknownFiles: v })} label="Read file headers to detect unknown types" hint="One small read per file with an unknown extension" />
            <Field label="Ignore (one glob per line)" hint="Without '/' a pattern matches a name anywhere (e.g. .snapshot); with '/' it matches the path from the root (e.g. Media/Cache/**).">
              <textarea className="input font-mono" rows={4} value={s.ignorePatterns.join('\n')} onChange={(e) => set({ ignorePatterns: e.target.value.split('\n') })} />
            </Field>
            <Field label="Keep folder size history for this many levels">
              <input className="input w-24" type="number" min={0} max={4} value={s.historyDepth} onChange={(e) => set({ historyDepth: Number(e.target.value) })} />
            </Field>
          </div>
        </Section>

        <Section title="Duplicates & safety">
          <div className="space-y-3">
            <Toggle checked={s.findDuplicates} onChange={(v) => set({ findDuplicates: v })} label="Find duplicates during scans" hint="Only files with the same size are read; hashes are cached" />
            <div className="grid grid-cols-2 gap-3">
              <Field label="Ignore files smaller than">
                <select className="input" value={s.minDuplicateSize} onChange={(e) => set({ minDuplicateSize: Number(e.target.value) })}>
                  {[1, 1024, 65536, 1048576, 10485760, 104857600].map((v) => (
                    <option key={v} value={v}>
                      {v === 1 ? 'nothing (all files)' : bytes(v)}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="Hashing threads" hint="1–2 for spinning disks">
                <input className="input" type="number" min={1} max={16} value={s.hashThreads} onChange={(e) => set({ hashThreads: Number(e.target.value) })} />
              </Field>
            </div>
            <Toggle checked={s.verifyHashBeforeDelete} onChange={(v) => set({ verifyHashBeforeDelete: v })} label="Re-hash both copies before removing a duplicate" />
            <Field label="Empty trash after (days, 0 = never)">
              <input className="input w-24" type="number" min={0} value={s.trashRetentionDays} onChange={(e) => set({ trashRetentionDays: Number(e.target.value) })} />
            </Field>
          </div>
        </Section>

        <Section title="AI (OpenAI-compatible)">
          <div className="space-y-3">
            <Toggle checked={s.ai.enabled} onChange={(v) => setAi({ enabled: v })} label="Enable AI classification" />
            <Field label="Base URL" hint="OpenAI: https://api.openai.com/v1 · Ollama: http://host:11434/v1 · LM Studio: http://host:1234/v1">
              <input className="input font-mono" value={s.ai.baseUrl} onChange={(e) => setAi({ baseUrl: e.target.value })} />
            </Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label="API key">
                <input className="input" type="password" autoComplete="off" value={s.ai.apiKey ?? ''} onChange={(e) => setAi({ apiKey: e.target.value })} placeholder="not needed for local models" />
              </Field>
              <Field label="Model">
                <input className="input font-mono" value={s.ai.model} onChange={(e) => setAi({ model: e.target.value })} />
              </Field>
            </div>
            <Field label="What may be sent">
              <select className="input" value={s.ai.privacy} onChange={(e) => setAi({ privacy: e.target.value as AiSettings['privacy'] })}>
                <option value="metadataOnly">Paths, names, sizes and dates only</option>
                <option value="textSnippet">Also the first 2 KB of text files</option>
              </select>
            </Field>
            <div className="grid grid-cols-3 gap-3">
              <Field label="Files per request">
                <input className="input" type="number" min={1} max={100} value={s.ai.batchSize} onChange={(e) => setAi({ batchSize: Number(e.target.value) })} />
              </Field>
              <Field label="Parallel requests">
                <input className="input" type="number" min={1} max={8} value={s.ai.maxConcurrency} onChange={(e) => setAi({ maxConcurrency: Number(e.target.value) })} />
              </Field>
              <Field label="Apply when confidence ≥">
                <input className="input" type="number" min={0} max={1} step={0.05} value={s.ai.minConfidence} onChange={(e) => setAi({ minConfidence: Number(e.target.value) })} />
              </Field>
            </div>
            <Field label="Default files per run">
              <input className="input w-32" type="number" min={1} value={s.ai.maxFilesPerRun} onChange={(e) => setAi({ maxFilesPerRun: Number(e.target.value) })} />
            </Field>
            <Toggle checked={s.ai.allowNewCategories} onChange={(v) => setAi({ allowNewCategories: v })} label="Let the AI create new categories" />
            <Toggle checked={s.ai.useJsonSchema} onChange={(v) => setAi({ useJsonSchema: v })} label="Use structured output (json_schema)" hint="Turn off for servers that only support JSON mode" />
            <Toggle checked={s.ai.autoClassifyAfterScan} onChange={(v) => setAi({ autoClassifyAfterScan: v })} label="Classify new uncategorized files after each scan" />
            <Field label="Extra instructions for the model">
              <textarea className="input" rows={3} value={s.ai.extraInstructions ?? ''} onChange={(e) => setAi({ extraInstructions: e.target.value })} placeholder="e.g. Files under Work/ are business documents." />
            </Field>
            <div className="flex items-center gap-2">
              <button className="btn-secondary" onClick={() => test.mutate()} disabled={test.isPending}>
                Test connection
              </button>
              {test.data && (
                <span className={`text-sm ${test.data.ok ? 'text-success' : 'text-danger'}`}>
                  {test.data.ok ? `✓ “${test.data.reply}” in ${test.data.milliseconds} ms` : `✕ ${test.data.error}`}
                </span>
              )}
            </div>
          </div>
        </Section>

        <Section title="Recent scans" className="lg:col-span-2">
          <table className="w-full text-sm">
            <thead className="text-left text-xs text-muted uppercase">
              <tr>
                <th className="py-1">Started</th>
                <th>Status</th>
                <th>Duration</th>
                <th className="text-right">Files</th>
                <th className="text-right">+ / ~ / −</th>
                <th className="text-right">Hashed</th>
                <th className="text-right">Errors</th>
              </tr>
            </thead>
            <tbody>
              {scans.data?.map((r) => (
                <tr key={r.id} className="border-t border-line/60">
                  <td className="py-1">
                    {dateTime(r.startedAt)}
                    {r.subPath && <span className="block text-xs muted">{r.subPath}</span>}
                  </td>
                  <td title={r.message}>{r.status}</td>
                  <td>{duration(r.startedAt, r.finishedAt)}</td>
                  <td className="text-right tabular">{r.filesSeen.toLocaleString()}</td>
                  <td className="text-right tabular">
                    {r.added} / {r.changed} / {r.removed}
                  </td>
                  <td className="text-right tabular">{r.hashed}</td>
                  <td className="text-right tabular">{r.errors}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Section>
      </div>
    </>
  )
}
