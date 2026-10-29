import { useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { useMutation, useQuery } from '@tanstack/react-query'
import { api, fileUrl } from '../api/client'
import type { DedupeKeep } from '../api/types'
import { EntryDrawer } from '../components/EntryDrawer'
import { ExtractedArchives } from '../components/ExtractedArchives'
import { Empty, ErrorBox, Field, Loading, Modal, PageTitle, Section, Stat, Tabs } from '../components/ui'
import { useScope } from '../hooks/useScope'
import { bytes, count, date } from '../lib/format'

interface Member {
  id: number
  rootId: number
  rootName: string
  relPath: string
  mTime: number
  nLink: number
  identity: string
}

interface Group {
  hash: string
  size: number
  copies: number
  wasted: number
  members: Member[]
}

interface FolderPair {
  a: { id: number; root?: string; relPath: string; files: number; size: number }
  b: { id: number; root?: string; relPath: string; files: number; size: number }
  sharedFiles: number
  sharedBytes: number
}

const PAGE = 50
const IMAGE_EXT = /\.(jpe?g|png|gif|webp|bmp|heic|avif|tiff?)$/i

export default function Duplicates() {
  const { rootId } = useScope()
  const [params] = useSearchParams()
  const [tab, setTab] = useState<'groups' | 'folders' | 'archives'>(params.get('tab') === 'archives' ? 'archives' : 'groups')
  const [sort, setSort] = useState('wasted')
  const [offset, setOffset] = useState(0)
  const [q, setQ] = useState(params.get('q') ?? '')
  const [preview, setPreview] = useState<number | null>(null)
  const [wizard, setWizard] = useState<{ hashes?: string[] } | null>(null)

  const groups = useQuery({
    queryKey: ['duplicates', rootId, sort, offset, q],
    queryFn: () => api<{ groups: Group[]; total: number; wasted: number }>('/duplicates', { query: { rootId, sort, offset, limit: PAGE, q } }),
  })
  const folders = useQuery({ queryKey: ['duplicate-folders'], queryFn: () => api<FolderPair[]>('/duplicates/folders'), enabled: tab === 'folders' })
  const d = groups.data

  return (
    <>
      <PageTitle
        subtitle="Identical content (same full hash). Hard links already share storage and are not counted."
        actions={
          <button className="btn-primary" disabled={!d?.total} onClick={() => setWizard({})}>
            Dedupe all…
          </button>
        }
      >
        Duplicates
      </PageTitle>
      <div className="mb-4 grid grid-cols-2 gap-3 md:grid-cols-3">
        <Stat label="Wasted space" value={bytes(d?.wasted)} tone="amber" />
        <Stat label="Duplicate groups" value={count(d?.total)} />
      </div>
      <Tabs
        tabs={[
          { id: 'groups', label: 'Groups' },
          { id: 'folders', label: 'Similar folders' },
          { id: 'archives', label: 'Extracted archives' },
        ]}
        value={tab}
        onChange={setTab}
      />

      {tab === 'groups' && (
        <>
          <div className="mb-3 flex flex-wrap items-center gap-2">
            <input
              className="input max-w-xs"
              placeholder="File name contains…"
              value={q}
              onChange={(e) => {
                setQ(e.target.value)
                setOffset(0)
              }}
            />
            <select className="input w-auto" value={sort} onChange={(e) => setSort(e.target.value)}>
              <option value="wasted">Most wasted space</option>
              <option value="size">Largest files</option>
              <option value="copies">Most copies</option>
            </select>
          </div>
          {groups.isLoading ? (
            <Loading />
          ) : groups.error ? (
            <ErrorBox error={groups.error} />
          ) : !d?.groups.length ? (
            <Empty>No duplicates found{q ? ' for this filter' : ''}. Duplicates are detected during scans (Settings → duplicate search).</Empty>
          ) : (
            <div className="space-y-3">
              {d.groups.map((g) => (
                <div key={g.hash} className="card p-3">
                  <div className="mb-2 flex flex-wrap items-center gap-2 text-sm">
                    <span className="font-semibold">{g.copies} copies</span>
                    <span className="muted">· {bytes(g.size)} each</span>
                    <span className="pill-amber">{bytes(g.wasted)} wasted</span>
                    <span className="flex-1" />
                    <button className="btn-secondary px-2.5 py-1 text-xs" onClick={() => setWizard({ hashes: [g.hash] })}>
                      Dedupe this…
                    </button>
                  </div>
                  <ul className="space-y-1">
                    {g.members.map((m) => (
                      <li key={m.id} className="flex items-center gap-2 rounded px-1 py-0.5 text-sm hover:bg-accent-faint">
                        {IMAGE_EXT.test(m.relPath) && <img src={fileUrl(m.id, "thumb", { size: 96 })} alt="" className="h-8 w-8 shrink-0 rounded object-cover" loading="lazy" onError={(e) => (e.currentTarget.style.visibility = "hidden")} />}
                        <button className="min-w-0 flex-1 truncate text-left" title={m.relPath} onClick={() => setPreview(m.id)}>
                          <span className="muted">{m.rootName}/</span>
                          {m.relPath}
                        </button>
                        {m.nLink > 1 && <span className="pill-muted" title="Shares storage with other names">linked ×{m.nLink}</span>}
                        <span className="shrink-0 text-xs muted">{date(m.mTime)}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              ))}
              <div className="flex items-center justify-center gap-3">
                <button className="btn-secondary" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>
                  ← Previous
                </button>
                <span className="text-sm muted">
                  {offset + 1}–{Math.min(offset + PAGE, d.total)} of {count(d.total)}
                </span>
                <button className="btn-secondary" disabled={offset + PAGE >= d.total} onClick={() => setOffset(offset + PAGE)}>
                  Next →
                </button>
              </div>
            </div>
          )}
        </>
      )}

      {tab === 'archives' && <ExtractedArchives />}

      {tab === 'folders' && (
        <Section title="Folder pairs sharing identical files" actions={<span className="text-xs muted">often copied backups or extracted archives</span>}>
          {folders.isLoading ? (
            <Loading />
          ) : !folders.data?.length ? (
            <Empty>No folder pairs share files.</Empty>
          ) : (
            <ul className="divide-y divide-line/60">
              {folders.data.map((p) => (
                <li key={`${p.a.id}-${p.b.id}`} className="flex flex-wrap items-center gap-3 py-2 text-sm">
                  <div className="min-w-0 flex-1 space-y-0.5">
                    <Link to={`/browse/${p.a.id}`} className="block truncate hover:text-accent">📁 {p.a.relPath || '/'}</Link>
                    <Link to={`/browse/${p.b.id}`} className="block truncate hover:text-accent">📁 {p.b.relPath || '/'}</Link>
                  </div>
                  <div className="text-right">
                    <div className="font-semibold text-amber tabular">{bytes(p.sharedBytes)}</div>
                    <div className="text-xs muted">{count(p.sharedFiles)} shared files</div>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </Section>
      )}

      <EntryDrawer id={preview} onClose={() => setPreview(null)} />
      {wizard && <DedupeWizard hashes={wizard.hashes} onClose={() => setWizard(null)} />}
    </>
  )
}

/** Chooses which copy to keep and what to do with the others; creates a draft plan for review. */
function DedupeWizard({ hashes, onClose }: { hashes?: string[]; onClose: () => void }) {
  const navigate = useNavigate()
  const { rootId } = useScope()
  const [keep, setKeep] = useState<DedupeKeep>('oldest')
  const [op, setOp] = useState<'trash' | 'delete' | 'hardlink'>('trash')
  const [preferred, setPreferred] = useState('')
  const [minSize, setMinSize] = useState(0)
  const create = useMutation({
    mutationFn: () =>
      api<{ id: number }>('/plans/dedupe', {
        method: 'POST',
        json: {
          keep,
          op,
          hashes,
          rootId,
          minSize,
          preferredFolders: preferred.split('\n').map((s) => s.trim()).filter(Boolean),
        },
      }),
    onSuccess: (p) => navigate(`/actions/${p.id}`),
  })
  return (
    <Modal open onClose={onClose} title={hashes ? 'Dedupe this group' : 'Dedupe all duplicates'}>
      <div className="space-y-4">
        <Field label="Keep">
          <select className="input" value={keep} onChange={(e) => setKeep(e.target.value as DedupeKeep)}>
            <option value="oldest">The oldest copy (usually the original)</option>
            <option value="newest">The newest copy</option>
            <option value="shortestPath">The copy closest to the root</option>
            <option value="longestPath">The most deeply organized copy</option>
            <option value="preferredFolder">The copy in a preferred folder…</option>
          </select>
        </Field>
        {keep === 'preferredFolder' && (
          <Field label="Preferred folders, in order (relative to the root)" hint="The first folder that contains a copy wins; otherwise the oldest copy is kept.">
            <textarea className="input font-mono" rows={4} value={preferred} onChange={(e) => setPreferred(e.target.value)} placeholder={'Photos\nMusic/Library'} />
          </Field>
        )}
        <Field label="Other copies">
          <select className="input" value={op} onChange={(e) => setOp(e.target.value as typeof op)}>
            <option value="trash">Move to trash (undoable)</option>
            <option value="hardlink">Replace with a hard link to the kept copy (keeps every path, frees the space)</option>
            <option value="delete">Delete permanently</option>
          </select>
        </Field>
        {!hashes && (
          <Field label="Only files larger than (MB)">
            <input className="input" type="number" min={0} value={minSize / 1048576} onChange={(e) => setMinSize(Number(e.target.value) * 1048576)} />
          </Field>
        )}
        <p className="text-sm muted">Each copy is re-hashed right before it is removed, so files changed since the scan are skipped.</p>
        {create.error && <ErrorBox error={create.error} />}
        <div className="flex justify-end gap-2">
          <button className="btn-secondary" onClick={onClose}>
            Cancel
          </button>
          <button className="btn-primary" onClick={() => create.mutate()} disabled={create.isPending}>
            Create plan for review
          </button>
        </div>
      </div>
    </Modal>
  )
}
