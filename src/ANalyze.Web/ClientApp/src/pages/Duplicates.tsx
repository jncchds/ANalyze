import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { keepPreviousData, useMutation, useQuery } from '@tanstack/react-query'
import { api, fileUrl } from '../api/client'
import type { DedupeKeep } from '../api/types'
import { EntryDrawer } from '../components/EntryDrawer'
import { ExtractedArchives } from '../components/ExtractedArchives'
import { usePlanAdded, usePlanChoice } from '../components/PlanDialog'
import { Empty, ErrorBox, Field, FilterInput, Loading, Modal, PageTitle, Section, ShareBar, Stat, Tabs } from '../components/ui'
import { useScope } from '../hooks/useScope'
import { leaveOverlay, useEntryPreview, useUrlEnum, useUrlNumber, useUrlOverlay, useUrlParam } from '../hooks/useUrlState'
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

interface PairSide {
  id: number
  root?: string
  relPath: string
  files: number
  size: number
  /** Files directly inside the folder (not below), which the pair is compared on. */
  ownFiles: number
  ownBytes: number
}

interface FolderPair {
  a: PairSide
  b: PairSide
  sharedFiles: number
  sharedBytes: number
  /** Shared bytes over the larger folder's own bytes: 1 = the same files. */
  similarity: number
}

const pctOf = (v: number) => (v >= 1 ? '100%' : `${Math.floor(v * 100)}%`)

const PAGE = 50
const folderOf = (m: Member) => `${m.rootId}:${m.relPath.includes('/') ? m.relPath.slice(0, m.relPath.lastIndexOf('/')) : ''}`

/** The first copy and the first copy in another folder, whose folders the comparison page opens on; null when all are in one folder. */
function comparePair(g: Group): [number, number] | null {
  const [first, ...rest] = g.members
  const other = first && rest.find((m) => folderOf(m) !== folderOf(first))
  return other ? [first.id, other.id] : null
}

const IMAGE_EXT = /\.(jpe?g|png|gif|webp|bmp|heic|avif|tiff?)$/i

export default function Duplicates() {
  const { rootId } = useScope()
  // Tab, sort, page, filter, the file panel and the dedupe dialog (`?dedupe=all|<hash>`) are in the URL, so Back steps through them.
  const [tab, setTab] = useUrlEnum('tab', ['groups', 'folders', 'archives'] as const, 'groups')
  const [sort, setSort] = useUrlParam('sort', 'wasted')
  const [offset, setOffset] = useUrlNumber('offset')
  const [q, setQ] = useUrlParam('q')
  const preview = useEntryPreview()
  const dedupe = useUrlOverlay('dedupe')
  const wizard = dedupe.value === null ? null : dedupe.value === 'all' ? {} : { hashes: [dedupe.value] }

  const groups = useQuery({
    queryKey: ['duplicates', rootId, sort, offset, q],
    queryFn: () => api<{ groups: Group[]; total: number; wasted: number }>('/duplicates', { query: { rootId, sort, offset, limit: PAGE, q } }),
    placeholderData: keepPreviousData,
  })
  const [folderSort, setFolderSort] = useUrlEnum('fsort', ['shared', 'similar'] as const, 'shared')
  const [folderMin, setFolderMin] = useUrlParam('fmin', '0')
  const folders = useQuery({
    queryKey: ['duplicate-folders', folderSort, folderMin],
    queryFn: () => api<FolderPair[]>('/duplicates/folders', { query: { sort: folderSort === 'similar' ? 'similar' : undefined, min: Number(folderMin) || undefined, limit: 60 } }),
    enabled: tab === 'folders',
    placeholderData: keepPreviousData,
  })
  const d = groups.data

  return (
    <>
      <PageTitle
        subtitle="Identical content (same full hash). Hard links already share storage and are not counted."
        actions={
          <button className="btn-primary" disabled={!d?.total} onClick={() => dedupe.open('all')}>
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
            <FilterInput
              className="input max-w-xs"
              placeholder="File name contains…"
              value={q}
              onChange={(v) => setQ(v, { replace: true, also: { offset: null } })}
            />
            <select className="input w-auto" value={sort} onChange={(e) => setSort(e.target.value, { also: { offset: null } })}>
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
                    {comparePair(g) && (
                      <Link
                        to={`/duplicates/compare?${new URLSearchParams({ a: String(comparePair(g)![0]), b: String(comparePair(g)![1]), hash: g.hash })}`}
                        className="btn-secondary px-2.5 py-1 text-xs"
                        title="The folders these copies are in, compared file by file: whether the rest of the folder is a copy too, and what differs"
                      >
                        Compare folders
                      </Link>
                    )}
                    <button className="btn-secondary px-2.5 py-1 text-xs" onClick={() => dedupe.open(g.hash)}>
                      Dedupe this…
                    </button>
                  </div>
                  <ul className="space-y-1">
                    {g.members.map((m) => (
                      <li key={m.id} className="flex items-center gap-2 rounded px-1 py-0.5 text-sm hover:bg-accent-faint">
                        {IMAGE_EXT.test(m.relPath) && <img src={fileUrl(m.id, "thumb", { size: 96 })} alt="" className="h-8 w-8 shrink-0 rounded object-cover" loading="lazy" onError={(e) => (e.currentTarget.style.visibility = "hidden")} />}
                        <button className="min-w-0 flex-1 truncate text-left" title={m.relPath} onClick={() => preview.open(m.id)}>
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
          <div className="mb-3 flex flex-wrap items-center gap-2">
            <select className="input w-auto" value={folderSort} onChange={(e) => setFolderSort(e.target.value as 'shared' | 'similar')}>
              <option value="shared">Most shared space</option>
              <option value="similar">Most alike</option>
            </select>
            <select className="input w-auto" value={folderMin} onChange={(e) => setFolderMin(e.target.value, { replace: true })} title="How much of the larger folder's own files the other one has too">
              <option value="0">Any overlap</option>
              <option value="0.5">At least half the same</option>
              <option value="0.9">Near copies (90%+)</option>
              <option value="1">The same files</option>
            </select>
          </div>
          {folders.isLoading ? (
            <Loading />
          ) : folders.error ? (
            <ErrorBox error={folders.error} />
          ) : !folders.data?.length ? (
            <Empty>{folderMin !== '0' ? 'No folder pairs are this much alike.' : 'No folder pairs share files.'}</Empty>
          ) : (
            <ul className="divide-y divide-line/60">
              {folders.data.map((p) => (
                <li key={`${p.a.id}-${p.b.id}`} className="flex flex-wrap items-center gap-3 py-2 text-sm">
                  <div className="min-w-0 flex-1 space-y-0.5">
                    {[p.a, p.b].map((s) => (
                      <div key={s.id} className="flex min-w-0 items-baseline gap-2">
                        <Link to={`/browse/${s.id}`} className="min-w-0 flex-1 truncate hover:text-accent" title={`${s.root ?? '?'}/${s.relPath}`}>
                          📁 {s.relPath || '/'}
                        </Link>
                        <span className="shrink-0 text-xs muted tabular" title="Files directly in this folder, which the pair is compared on">
                          {count(s.ownFiles)} files · {bytes(s.ownBytes)}
                        </span>
                      </div>
                    ))}
                  </div>
                  <div className="w-28 text-right">
                    <div
                      className={p.similarity >= 0.9 ? 'font-semibold text-success tabular' : 'font-semibold tabular'}
                      title="Shared bytes over the larger folder's own files: 100% = the same files"
                    >
                      {pctOf(p.similarity)} alike
                    </div>
                    <ShareBar value={p.similarity} max={1} color="var(--success)" className="my-0.5" />
                    <div className="text-xs muted">
                      <span className="text-amber">{bytes(p.sharedBytes)}</span> · {count(p.sharedFiles)} shared
                    </div>
                  </div>
                  <Link to={`/duplicates/compare?a=${p.a.id}&b=${p.b.id}`} className="btn-secondary px-2.5 py-1 text-xs" title="Compare the two folders file by file">
                    Compare
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </Section>
      )}

      <EntryDrawer preview={preview} list={d?.groups.flatMap((g) => g.members.map((m) => m.id))} />
      {wizard && <DedupeWizard key={dedupe.value} hashes={wizard.hashes} onClose={dedupe.close} />}
    </>
  )
}

/** Chooses which copy to keep and what to do with the others; creates a draft plan for review, or adds to a draft. */
function DedupeWizard({ hashes, onClose }: { hashes?: string[]; onClose: () => void }) {
  const navigate = useNavigate()
  const added = usePlanAdded()
  const { existing, field } = usePlanChoice()
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
          planId: existing?.id,
        },
      }),
    onSuccess: (p) => {
      added(p)
      leaveOverlay(navigate, `/actions/${p.id}`)
    },
  })
  return (
    <Modal open onClose={onClose} title={hashes ? 'Dedupe this group' : 'Dedupe all duplicates'}>
      <div className="space-y-4">
        {field}
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
            {existing ? `Add to “${existing.name}”` : 'Create plan for review'}
          </button>
        </div>
      </div>
    </Modal>
  )
}
