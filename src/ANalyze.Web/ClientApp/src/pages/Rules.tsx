import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../api/client'
import { useCategories, useCategoryMap, useConfig, useRules, useTemplates } from '../api/hooks'
import type { ActionOp, ActionTemplate, Category, ConflictPolicy, Rule, RuleClause, RuleCondition, RuleField, RuleOp } from '../api/types'
import { ConfirmButton, Empty, ErrorBox, Field, Loading, Modal, PageTitle, Tabs, Toggle } from '../components/ui'
import { bytes, count } from '../lib/format'

const FIELDS: { id: RuleField; label: string; numeric?: boolean; hint?: string }[] = [
  { id: 'extension', label: 'Extension', hint: 'mkv,mp4' },
  { id: 'name', label: 'File name' },
  { id: 'path', label: 'Path' },
  { id: 'pathSegment', label: 'Inside folder named', hint: 'node_modules' },
  { id: 'kind', label: 'Type', hint: 'Video, Image, Archive…' },
  { id: 'mime', label: 'MIME type' },
  { id: 'size', label: 'Size', numeric: true, hint: '100MB' },
  { id: 'modifiedDays', label: 'Days since modified', numeric: true },
  { id: 'accessedDays', label: 'Days since accessed', numeric: true },
  { id: 'depth', label: 'Folder depth', numeric: true },
]

const OPS: { id: RuleOp; label: string; numeric?: boolean; text?: boolean }[] = [
  { id: 'in', label: 'is one of', text: true },
  { id: 'equals', label: 'equals', text: true, numeric: true },
  { id: 'contains', label: 'contains', text: true },
  { id: 'startsWith', label: 'starts with', text: true },
  { id: 'endsWith', label: 'ends with', text: true },
  { id: 'glob', label: 'matches glob', text: true },
  { id: 'regex', label: 'matches regex', text: true },
  { id: 'greaterThan', label: 'greater than', numeric: true },
  { id: 'lessThan', label: 'less than', numeric: true },
]

export default function Rules() {
  const [tab, setTab] = useState<'rules' | 'categories' | 'templates'>('rules')
  return (
    <>
      <PageTitle subtitle="Rules run top to bottom on every file; the first rule that assigns a category wins, tags add up. Manual choices always win, AI fills the gaps.">
        Categories & rules
      </PageTitle>
      <Tabs
        tabs={[
          { id: 'rules', label: 'Rules' },
          { id: 'categories', label: 'Categories' },
          { id: 'templates', label: 'Action templates' },
        ]}
        value={tab}
        onChange={setTab}
      />
      {tab === 'rules' && <RulesTab />}
      {tab === 'categories' && <CategoriesTab />}
      {tab === 'templates' && <TemplatesTab />}
    </>
  )
}

function RulesTab() {
  const rules = useRules()
  const cats = useCategoryMap()
  const qc = useQueryClient()
  const [editing, setEditing] = useState<Partial<Rule> | null>(null)
  const [filter, setFilter] = useState('')
  const invalidate = () => qc.invalidateQueries({ queryKey: ['rules'] })
  const apply = useMutation({ mutationFn: () => api('/rules/apply', { method: 'POST' }) })
  const restore = useMutation({ mutationFn: () => api<{ added: number }>('/rules/restore-builtins', { method: 'POST' }), onSuccess: invalidate })
  const reorder = useMutation({ mutationFn: (ids: number[]) => api('/rules/reorder', { method: 'POST', json: ids }), onSuccess: invalidate })
  const toggle = useMutation({ mutationFn: (r: Rule) => api(`/rules/${r.id}`, { method: 'PUT', json: { ...r, enabled: !r.enabled } }), onSuccess: invalidate })
  const remove = useMutation({ mutationFn: (id: number) => api(`/rules/${id}`, { method: 'DELETE' }), onSuccess: invalidate })
  const importRules = useMutation({
    mutationFn: async (file: File) => api('/rules/import', { method: 'POST', json: JSON.parse(await file.text()) }),
    onSuccess: () => {
      invalidate()
      qc.invalidateQueries({ queryKey: ['categories'] })
    },
  })

  if (rules.isLoading) return <Loading />
  if (rules.error) return <ErrorBox error={rules.error} />
  const list = rules.data ?? []
  const shown = list.filter((r) => !filter || r.name.toLowerCase().includes(filter.toLowerCase()))
  const move = (index: number, dir: -1 | 1) => {
    const ids = list.map((r) => r.id)
    const j = index + dir
    if (j < 0 || j >= ids.length) return
    ;[ids[index], ids[j]] = [ids[j], ids[index]]
    reorder.mutate(ids)
  }

  return (
    <>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <input className="input max-w-xs" placeholder="Filter rules…" value={filter} onChange={(e) => setFilter(e.target.value)} />
        <span className="flex-1" />
        <button className="btn-secondary" onClick={() => restore.mutate()} title="Re-adds built-in rules you deleted">
          Restore built-ins{restore.data ? ` (+${restore.data.added})` : ''}
        </button>
        <a className="btn-secondary" href="/api/v1/rules/export" download="analyze-rules.json">
          Export
        </a>
        <label className="btn-secondary cursor-pointer">
          Import
          <input type="file" accept="application/json" className="hidden" onChange={(e) => e.target.files?.[0] && importRules.mutate(e.target.files[0])} />
        </label>
        <button className="btn-secondary" onClick={() => apply.mutate()} disabled={apply.isPending}>
          {apply.isSuccess ? 'Applying…' : 'Re-apply rules'}
        </button>
        <button className="btn-primary" onClick={() => setEditing({ enabled: true, stopProcessing: true, tags: [], condition: { match: 'all', clauses: [{ field: 'extension', op: 'in', value: '', negate: false }] } })}>
          New rule
        </button>
      </div>
      {importRules.error && <ErrorBox error={importRules.error} />}
      <div className="card divide-y divide-line/60">
        {shown.map((r) => {
          const index = list.indexOf(r)
          const cat = r.categoryId ? cats.get(r.categoryId) : undefined
          return (
            <div key={r.id} className={clsx('flex items-center gap-3 px-3 py-2', !r.enabled && 'opacity-50')}>
              <div className="flex flex-col">
                <button className="btn-ghost px-1 py-0 text-xs" onClick={() => move(index, -1)} disabled={index === 0} aria-label="Move up">
                  ▲
                </button>
                <button className="btn-ghost px-1 py-0 text-xs" onClick={() => move(index, 1)} disabled={index === list.length - 1} aria-label="Move down">
                  ▼
                </button>
              </div>
              <button className="min-w-0 flex-1 text-left" onClick={() => setEditing(r)}>
                <div className="flex flex-wrap items-center gap-2 text-sm">
                  <span className="font-semibold">{r.name}</span>
                  {r.source !== 'user' && <span className="pill-muted">{r.source === 'ai' ? '✨ AI' : 'built-in'}</span>}
                  {cat && (
                    <span className="inline-flex items-center gap-1 text-xs">
                      → <span className="h-2 w-2 rounded-full" style={{ background: cat.color }} /> {cat.name}
                    </span>
                  )}
                  {r.tags.map((t) => (
                    <span key={t} className="chip">#{t}</span>
                  ))}
                </div>
                <div className="truncate text-xs muted">{describe(r.condition)}</div>
              </button>
              <input type="checkbox" className="h-4 w-4 accent-accent" checked={r.enabled} onChange={() => toggle.mutate(r)} title="Enabled" />
              <ConfirmButton className="btn-ghost px-2 py-1 text-xs" confirmLabel="Delete?" onConfirm={() => remove.mutate(r.id)}>
                ✕
              </ConfirmButton>
            </div>
          )
        })}
        {shown.length === 0 && <Empty>No rules.</Empty>}
      </div>
      {editing && <RuleEditor rule={editing} onClose={() => setEditing(null)} />}
    </>
  )
}

export function describe(c: RuleCondition) {
  return c.clauses
    .map((cl) => `${cl.negate ? 'not ' : ''}${FIELDS.find((f) => f.id === cl.field)?.label ?? cl.field} ${OPS.find((o) => o.id === cl.op)?.label ?? cl.op} “${cl.value}”`)
    .join(c.match === 'all' ? ' and ' : ' or ')
}

/** Rule form with a live preview of matching files. */
export function RuleEditor({ rule, onClose }: { rule: Partial<Rule>; onClose: () => void }) {
  const categories = useCategories()
  const qc = useQueryClient()
  const [r, setR] = useState<Partial<Rule>>(rule)
  const [debounced, setDebounced] = useState(r.condition)
  useEffect(() => {
    const t = setTimeout(() => setDebounced(r.condition), 400)
    return () => clearTimeout(t)
  }, [r.condition])
  const test = useQuery({
    queryKey: ['rule-test', debounced],
    queryFn: () => api<{ count: number; bytes: number; samples: { id: number; relPath: string; size: number }[] }>('/rules/test', { method: 'POST', json: debounced }),
    enabled: !!debounced?.clauses.some((c) => c.value.trim()),
  })
  const save = useMutation({
    mutationFn: () => (r.id ? api(`/rules/${r.id}`, { method: 'PUT', json: r }) : api('/rules', { method: 'POST', json: r })),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['rules'] })
      onClose()
    },
  })
  const cond = r.condition ?? { match: 'all', clauses: [] }
  const setClause = (i: number, patch: Partial<RuleClause>) =>
    setR({ ...r, condition: { ...cond, clauses: cond.clauses.map((c, j) => (j === i ? { ...c, ...patch } : c)) } })

  return (
    <Modal open onClose={onClose} title={r.id ? 'Edit rule' : 'New rule'} wide>
      <div className="space-y-4">
        <div className="grid gap-3 md:grid-cols-2">
          <Field label="Name">
            <input className="input" value={r.name ?? ''} onChange={(e) => setR({ ...r, name: e.target.value })} />
          </Field>
          <Field label="Category">
            <select className="input" value={r.categoryId ?? ''} onChange={(e) => setR({ ...r, categoryId: e.target.value ? Number(e.target.value) : undefined })}>
              <option value="">— tags only —</option>
              {categories.data?.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </Field>
        </div>
        <div>
          <div className="mb-2 flex items-center gap-2 text-sm">
            <span>Match</span>
            <select className="input w-auto py-1" value={cond.match} onChange={(e) => setR({ ...r, condition: { ...cond, match: e.target.value as 'all' | 'any' } })}>
              <option value="all">all conditions</option>
              <option value="any">any condition</option>
            </select>
          </div>
          <div className="space-y-2">
            {cond.clauses.map((c, i) => {
              const numeric = FIELDS.find((f) => f.id === c.field)?.numeric
              return (
                <div key={i} className="flex flex-wrap items-center gap-2">
                  <select className="input w-auto py-1" value={c.negate ? 'not' : ''} onChange={(e) => setClause(i, { negate: e.target.value === 'not' })}>
                    <option value="">if</option>
                    <option value="not">if not</option>
                  </select>
                  <select
                    className="input w-auto py-1"
                    value={c.field}
                    onChange={(e) => {
                      const f = e.target.value as RuleField
                      const isNum = FIELDS.find((x) => x.id === f)?.numeric
                      setClause(i, { field: f, op: isNum ? 'greaterThan' : f === 'pathSegment' || f === 'extension' || f === 'kind' ? 'in' : 'contains' })
                    }}
                  >
                    {FIELDS.map((f) => (
                      <option key={f.id} value={f.id}>
                        {f.label}
                      </option>
                    ))}
                  </select>
                  <select className="input w-auto py-1" value={c.op} onChange={(e) => setClause(i, { op: e.target.value as RuleOp })}>
                    {OPS.filter((o) => (numeric ? o.numeric : o.text)).map((o) => (
                      <option key={o.id} value={o.id}>
                        {o.label}
                      </option>
                    ))}
                  </select>
                  <input
                    className="input min-w-40 flex-1 py-1 font-mono"
                    value={c.value}
                    placeholder={FIELDS.find((f) => f.id === c.field)?.hint}
                    onChange={(e) => setClause(i, { value: e.target.value })}
                  />
                  <button className="btn-ghost px-2 py-1" onClick={() => setR({ ...r, condition: { ...cond, clauses: cond.clauses.filter((_, j) => j !== i) } })} aria-label="Remove condition">
                    ✕
                  </button>
                </div>
              )
            })}
          </div>
          <button className="btn-ghost mt-2 px-2 py-1 text-sm" onClick={() => setR({ ...r, condition: { ...cond, clauses: [...cond.clauses, { field: 'name', op: 'contains', value: '', negate: false }] } })}>
            + Add condition
          </button>
        </div>
        <div className="grid gap-3 md:grid-cols-2">
          <Field label="Tags (comma separated)">
            <input className="input" value={(r.tags ?? []).join(', ')} onChange={(e) => setR({ ...r, tags: e.target.value.split(',').map((t) => t.trim()).filter(Boolean) })} />
          </Field>
          <Field label="Description">
            <input className="input" value={r.description ?? ''} onChange={(e) => setR({ ...r, description: e.target.value })} />
          </Field>
        </div>
        <div className="flex flex-wrap gap-4">
          <Toggle checked={r.enabled ?? true} onChange={(v) => setR({ ...r, enabled: v })} label="Enabled" />
          <Toggle checked={r.stopProcessing ?? true} onChange={(v) => setR({ ...r, stopProcessing: v })} label="Stop after assigning a category" hint="Off: later rules may still add tags" />
        </div>

        <div className="rounded-md border border-line bg-surface-2/50 p-3">
          <div className="mb-1 text-sm font-semibold">
            Preview{' '}
            {test.data && (
              <span className="font-normal muted">
                · {count(test.data.count)} files · {bytes(test.data.bytes)}
              </span>
            )}
          </div>
          {test.isFetching && <span className="text-xs muted">Testing…</span>}
          <ul className="max-h-40 overflow-y-auto text-xs">
            {test.data?.samples.map((s) => (
              <li key={s.id} className="flex gap-2 py-0.5">
                <span className="min-w-0 flex-1 truncate">{s.relPath}</span>
                <span className="muted tabular">{bytes(s.size)}</span>
              </li>
            ))}
          </ul>
        </div>

        {save.error && <ErrorBox error={save.error} />}
        <div className="flex justify-end gap-2">
          <button className="btn-secondary" onClick={onClose}>
            Cancel
          </button>
          <button className="btn-primary" onClick={() => save.mutate()} disabled={!r.name?.trim() || save.isPending}>
            Save
          </button>
        </div>
        <p className="text-xs muted">After saving, use “Re-apply rules” (or the next scan) to update existing files.</p>
      </div>
    </Modal>
  )
}

function CategoriesTab() {
  const cats = useCategories()
  const qc = useQueryClient()
  const [editing, setEditing] = useState<Partial<Category> | null>(null)
  const save = useMutation({
    mutationFn: (c: Partial<Category>) => (c.id ? api(`/categories/${c.id}`, { method: 'PUT', json: c }) : api('/categories', { method: 'POST', json: c })),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['categories'] })
      setEditing(null)
    },
  })
  const remove = useMutation({ mutationFn: (id: number) => api(`/categories/${id}`, { method: 'DELETE' }), onSuccess: () => qc.invalidateQueries() })
  if (cats.isLoading) return <Loading />
  return (
    <>
      <div className="mb-3 flex justify-end">
        <button className="btn-primary" onClick={() => setEditing({ name: '', color: '#2a78d6' })}>
          New category
        </button>
      </div>
      <div className="grid gap-2 md:grid-cols-2 xl:grid-cols-3">
        {cats.data?.map((c) => (
          <div key={c.id} className="card flex items-start gap-3 p-3">
            <span className="mt-1 h-3 w-3 shrink-0 rounded-full" style={{ background: c.color }} />
            <button className="min-w-0 flex-1 text-left" onClick={() => setEditing(c)}>
              <div className="font-semibold">{c.name}</div>
              <div className="text-xs muted">{c.description}</div>
            </button>
            <ConfirmButton className="btn-ghost px-2 py-1 text-xs" confirmLabel="Delete?" onConfirm={() => remove.mutate(c.id)}>
              ✕
            </ConfirmButton>
          </div>
        ))}
      </div>
      {editing && (
        <Modal open onClose={() => setEditing(null)} title={editing.id ? 'Edit category' : 'New category'}>
          <div className="space-y-3">
            <Field label="Name">
              <input className="input" value={editing.name ?? ''} onChange={(e) => setEditing({ ...editing, name: e.target.value })} />
            </Field>
            <Field label="Color">
              <input type="color" className="h-9 w-20 rounded border border-line" value={editing.color ?? '#2a78d6'} onChange={(e) => setEditing({ ...editing, color: e.target.value })} />
            </Field>
            <Field label="Description (also shown to the AI)">
              <input className="input" value={editing.description ?? ''} onChange={(e) => setEditing({ ...editing, description: e.target.value })} />
            </Field>
            {save.error && <ErrorBox error={save.error} />}
            <div className="flex justify-end gap-2">
              <button className="btn-secondary" onClick={() => setEditing(null)}>
                Cancel
              </button>
              <button className="btn-primary" onClick={() => save.mutate(editing)} disabled={!editing.name?.trim()}>
                Save
              </button>
            </div>
          </div>
        </Modal>
      )}
    </>
  )
}

function TemplatesTab() {
  const templates = useTemplates()
  const config = useConfig()
  const qc = useQueryClient()
  const [editing, setEditing] = useState<Partial<ActionTemplate> | null>(null)
  const save = useMutation({
    mutationFn: (t: Partial<ActionTemplate>) => (t.id ? api(`/templates/${t.id}`, { method: 'PUT', json: t }) : api('/templates', { method: 'POST', json: t })),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['templates'] })
      setEditing(null)
    },
  })
  const remove = useMutation({ mutationFn: (id: number) => api(`/templates/${id}`, { method: 'DELETE' }), onSuccess: () => qc.invalidateQueries({ queryKey: ['templates'] }) })
  return (
    <>
      <div className="mb-3 flex items-center">
        <p className="flex-1 text-sm muted">Reusable actions offered when cleaning up (moves use path tokens relative to the root).</p>
        <button className="btn-primary" onClick={() => setEditing({ name: '', op: 'move', destination: 'Sorted/{category}/{name}', conflict: 'rename' })}>
          New template
        </button>
      </div>
      <div className="card divide-y divide-line/60">
        {templates.data?.map((t) => (
          <div key={t.id} className="flex items-center gap-3 px-3 py-2">
            <button className="min-w-0 flex-1 text-left" onClick={() => setEditing(t)}>
              <div className="text-sm font-semibold">{t.name}</div>
              <div className="truncate text-xs muted">
                {t.op}
                {t.destination ? ` → ${t.destination}` : ''} · {t.description}
              </div>
            </button>
            <ConfirmButton className="btn-ghost px-2 py-1 text-xs" confirmLabel="Delete?" onConfirm={() => remove.mutate(t.id)}>
              ✕
            </ConfirmButton>
          </div>
        ))}
      </div>
      {editing && (
        <Modal open onClose={() => setEditing(null)} title={editing.id ? 'Edit template' : 'New template'}>
          <div className="space-y-3">
            <Field label="Name">
              <input className="input" value={editing.name ?? ''} onChange={(e) => setEditing({ ...editing, name: e.target.value })} />
            </Field>
            <Field label="Action">
              <select className="input" value={editing.op} onChange={(e) => setEditing({ ...editing, op: e.target.value as ActionOp })}>
                <option value="trash">Move to trash</option>
                <option value="move">Move / rename</option>
                <option value="delete">Delete permanently</option>
              </select>
            </Field>
            {editing.op === 'move' && (
              <Field label="Destination" hint={`Tokens: ${config.data?.pathTokens.map((t) => `{${t}}`).join(' ')}`}>
                <input className="input font-mono" value={editing.destination ?? ''} onChange={(e) => setEditing({ ...editing, destination: e.target.value })} />
              </Field>
            )}
            <Field label="When the destination exists">
              <select className="input" value={editing.conflict} onChange={(e) => setEditing({ ...editing, conflict: e.target.value as ConflictPolicy })}>
                <option value="rename">Rename</option>
                <option value="skip">Skip</option>
                <option value="overwrite">Overwrite</option>
              </select>
            </Field>
            <Field label="Description">
              <input className="input" value={editing.description ?? ''} onChange={(e) => setEditing({ ...editing, description: e.target.value })} />
            </Field>
            <div className="flex justify-end gap-2">
              <button className="btn-secondary" onClick={() => setEditing(null)}>
                Cancel
              </button>
              <button className="btn-primary" onClick={() => save.mutate(editing)} disabled={!editing.name?.trim()}>
                Save
              </button>
            </div>
          </div>
        </Modal>
      )}
    </>
  )
}
