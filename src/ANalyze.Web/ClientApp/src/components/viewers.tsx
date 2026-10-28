import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import clsx from 'clsx'
import { api, fileUrl } from '../api/client'
import type { Entry } from '../api/types'
import { bytes } from '../lib/format'
import { Empty, ErrorBox, Loading } from './ui'

interface TextChunk {
  text: string
  encoding: string
  offset: number
  nextOffset: number
  size: number
  truncated: boolean
}

const CODE_LANG: Record<string, string> = {
  js: 'javascript', mjs: 'javascript', cjs: 'javascript', jsx: 'javascript', ts: 'typescript', tsx: 'typescript', py: 'python', rb: 'ruby',
  cs: 'csharp', java: 'java', kt: 'kotlin', go: 'go', rs: 'rust', c: 'c', h: 'c', cpp: 'cpp', hpp: 'cpp', php: 'php', sh: 'bash', bash: 'bash',
  zsh: 'bash', ps1: 'powershell', sql: 'sql', json: 'json', yaml: 'yaml', yml: 'yaml', xml: 'xml', html: 'xml', htm: 'xml', css: 'css',
  scss: 'scss', less: 'less', ini: 'ini', toml: 'ini', md: 'markdown', swift: 'swift', lua: 'lua', r: 'r', dockerfile: 'dockerfile',
  makefile: 'makefile', diff: 'diff', patch: 'diff', csproj: 'xml', props: 'xml', svg: 'xml', vue: 'xml', graphql: 'graphql',
}

/** Paged text reader with syntax highlighting (code) and rendered markdown. */
export function TextView({ entry }: { entry: Entry }) {
  const [chunks, setChunks] = useState<TextChunk[]>([])
  const [error, setError] = useState<unknown>()
  const [mode, setMode] = useState<'plain' | 'rich'>('rich')
  const [html, setHtml] = useState<string>()
  const ext = (entry.ext ?? '').toLowerCase()
  const isMarkdown = ext === 'md' || ext === 'markdown'
  const lang = CODE_LANG[ext]

  const load = (offset: number) =>
    api<TextChunk>(`/files/${entry.id}/text`, { query: { offset, length: 64 * 1024 } })
      .then((c) => setChunks((prev) => (offset === 0 ? [c] : [...prev, c])))
      .catch(setError)

  useEffect(() => {
    setChunks([])
    setHtml(undefined)
    load(0)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [entry.id])

  const text = useMemo(() => chunks.map((c) => c.text).join(''), [chunks])
  const last = chunks.at(-1)

  useEffect(() => {
    let cancelled = false
    if (mode !== 'rich' || !text) {
      setHtml(undefined)
      return
    }
    if (isMarkdown) {
      Promise.all([import('marked'), import('dompurify')]).then(([{ marked }, { default: purify }]) => {
        const out = marked.parse(text, { async: false }) as string
        if (!cancelled) setHtml(purify.sanitize(out, { FORBID_TAGS: ['style', 'form', 'iframe'] }))
      })
    } else if (lang && text.length < 300_000) {
      import('highlight.js/lib/common').then(({ default: hljs }) => {
        if (cancelled) return
        try {
          setHtml(hljs.getLanguage(lang) ? hljs.highlight(text, { language: lang }).value : hljs.highlightAuto(text).value)
        } catch {
          setHtml(undefined)
        }
      })
    } else setHtml(undefined)
    return () => {
      cancelled = true
    }
  }, [text, mode, isMarkdown, lang])

  if (error) return <ErrorBox error={error} />
  if (!last) return <Loading />
  return (
    <div>
      <div className="mb-2 flex items-center gap-2 text-xs muted">
        <span>
          {last.encoding} · showing {bytes(last.nextOffset)} of {bytes(last.size)}
        </span>
        {(isMarkdown || lang) && (
          <button className="btn-ghost ml-auto px-2 py-0.5 text-xs" onClick={() => setMode(mode === 'rich' ? 'plain' : 'rich')}>
            {mode === 'rich' ? 'Plain text' : isMarkdown ? 'Rendered' : 'Highlighted'}
          </button>
        )}
      </div>
      {html && isMarkdown ? (
        <article className="prose-sm max-w-none space-y-2 rounded-md border border-line bg-input p-4 text-sm [&_a]:text-accent [&_code]:rounded [&_code]:bg-surface-2 [&_code]:px-1 [&_h1]:text-xl [&_h1]:font-bold [&_h2]:text-lg [&_h2]:font-semibold [&_li]:ml-5 [&_li]:list-disc [&_pre]:overflow-x-auto [&_pre]:bg-surface-2 [&_pre]:p-2" dangerouslySetInnerHTML={{ __html: html }} />
      ) : (
        <pre className={clsx('hljs-wrap max-h-[60vh] overflow-auto rounded-md border border-line bg-input p-3 font-mono text-xs leading-relaxed whitespace-pre-wrap break-all')}>
          {html ? <code dangerouslySetInnerHTML={{ __html: html }} /> : text}
        </pre>
      )}
      {last.truncated && (
        <button className="btn-secondary mt-2" onClick={() => load(last.nextOffset)}>
          Load more
        </button>
      )}
    </div>
  )
}

/** Hex + ASCII view, 16 bytes per row, paged by 4 KB with jump-to-offset. */
export function HexView({ entry }: { entry: Entry }) {
  const PAGE = 4096
  const [offset, setOffset] = useState(0)
  const [jump, setJump] = useState('')
  const q = useQuery({
    queryKey: ['hex', entry.id, offset],
    queryFn: () => api<{ offset: number; size: number; base64: string }>(`/files/${entry.id}/hex`, { query: { offset, length: PAGE } }),
  })
  const bytesArr = useMemo(() => (q.data ? Uint8Array.from(atob(q.data.base64), (c) => c.charCodeAt(0)) : new Uint8Array()), [q.data])
  const size = q.data?.size ?? entry.size
  const rows = []
  for (let i = 0; i < bytesArr.length; i += 16) {
    const slice = Array.from(bytesArr.slice(i, i + 16))
    rows.push(
      <div key={i} className="flex gap-4">
        <span className="text-muted">{(offset + i).toString(16).padStart(8, '0')}</span>
        <span className="whitespace-pre">{slice.map((b) => b.toString(16).padStart(2, '0')).join(' ').padEnd(47, ' ')}</span>
        <span className="whitespace-pre text-accent">{slice.map((b) => (b >= 32 && b < 127 ? String.fromCharCode(b) : '·')).join('')}</span>
      </div>,
    )
  }
  return (
    <div>
      <div className="mb-2 flex flex-wrap items-center gap-2 text-xs">
        <button className="btn-secondary px-2 py-1 text-xs" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>
          ← Prev
        </button>
        <button className="btn-secondary px-2 py-1 text-xs" disabled={offset + PAGE >= size} onClick={() => setOffset(offset + PAGE)}>
          Next →
        </button>
        <button className="btn-ghost px-2 py-1 text-xs" onClick={() => setOffset(Math.max(0, Math.floor((size - 1) / PAGE) * PAGE))}>
          End
        </button>
        <form
          className="flex items-center gap-1"
          onSubmit={(e) => {
            e.preventDefault()
            const n = jump.startsWith('0x') ? parseInt(jump, 16) : parseInt(jump, 10)
            if (Number.isFinite(n)) setOffset(Math.max(0, Math.min(size - 1, Math.floor(n / 16) * 16)))
          }}
        >
          <input className="input w-32 py-0.5 text-xs" placeholder="offset (0x…)" value={jump} onChange={(e) => setJump(e.target.value)} />
        </form>
        <span className="ml-auto muted">
          {offset.toLocaleString()}–{Math.min(size, offset + PAGE).toLocaleString()} of {size.toLocaleString()} bytes
        </span>
      </div>
      {q.isLoading ? <Loading /> : q.error ? <ErrorBox error={q.error} /> : <div className="max-h-[60vh] overflow-auto rounded-md border border-line bg-input p-3 font-mono text-xs leading-5">{rows}</div>}
    </div>
  )
}

export function ArchiveView({ entry }: { entry: Entry }) {
  const q = useQuery({
    queryKey: ['archive', entry.id],
    queryFn: () => api<{ items: { path: string; size: number; compressedSize: number; isDir: boolean; modified?: string }[]; total: number; truncated: boolean; error?: string }>(`/files/${entry.id}/archive`),
  })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  if (q.data?.error) return <ErrorBox error={q.data.error} />
  const items = q.data?.items ?? []
  const unpacked = items.reduce((s, i) => s + i.size, 0)
  return (
    <div>
      <p className="mb-2 text-xs muted">
        {q.data?.total.toLocaleString()} entries · {bytes(unpacked)} unpacked{q.data?.truncated ? ' (first 5000 shown)' : ''}
      </p>
      <div className="max-h-[60vh] overflow-auto rounded-md border border-line">
        <table className="w-full text-xs">
          <tbody>
            {items.map((i) => (
              <tr key={i.path} className="border-b border-line/60">
                <td className="px-2 py-1 break-all">{i.isDir ? '📁 ' : ''}{i.path}</td>
                <td className="px-2 py-1 text-right whitespace-nowrap tabular">{i.isDir ? '' : bytes(i.size)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

export function MetaView({ entry }: { entry: Entry }) {
  const q = useQuery({ queryKey: ['meta', entry.id], queryFn: () => api<{ name: string; tags: { key: string; value: string }[] }[]>(`/files/${entry.id}/meta`) })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  if (!q.data?.length) return <Empty>No embedded metadata.</Empty>
  return (
    <div className="max-h-[60vh] space-y-3 overflow-auto">
      {q.data.map((g) => (
        <div key={g.name}>
          <h4 className="mb-1 text-xs font-semibold tracking-wide text-muted uppercase">{g.name}</h4>
          <dl className="grid grid-cols-[minmax(8rem,auto)_1fr] gap-x-3 gap-y-0.5 text-xs">
            {g.tags.map((t, i) => (
              <div key={t.key + i} className="contents">
                <dt className="muted">{t.key}</dt>
                <dd className="break-all">{t.value}</dd>
              </div>
            ))}
          </dl>
        </div>
      ))}
    </div>
  )
}

/** Picks the best preview for a file kind. */
export function MainPreview({ entry }: { entry: Entry }) {
  const src = fileUrl(entry.id, 'content')
  switch (entry.kind) {
    case 'image':
      return <img src={src} alt={entry.name} className="max-h-[65vh] w-full rounded-md bg-surface-2 object-contain" />
    case 'video':
      return <video src={src} poster={fileUrl(entry.id, 'thumb', { size: 640 })} controls preload="metadata" className="max-h-[65vh] w-full rounded-md bg-black" />
    case 'audio':
      return (
        <div className="space-y-3">
          <img src={fileUrl(entry.id, 'thumb', { size: 320 })} alt="" className="mx-auto max-h-64 rounded-md" onError={(e) => (e.currentTarget.style.display = 'none')} />
          <audio src={src} controls preload="metadata" className="w-full" />
        </div>
      )
    case 'pdf':
      return <iframe src={src} title={entry.name} className="h-[70vh] w-full rounded-md border border-line bg-white" />
    case 'text':
    case 'code':
      return <TextView entry={entry} />
    case 'archive':
    case 'ebook':
      return entry.mime?.includes('zip') || ['zip', 'rar', '7z', 'tar', 'gz', 'tgz', 'epub', 'cbz', 'cbr', 'jar'].includes(entry.ext ?? '') ? <ArchiveView entry={entry} /> : <HexView entry={entry} />
    default:
      return entry.size === 0 ? <Empty>Empty file.</Empty> : <HexView entry={entry} />
  }
}

export function previewTabs(entry: Entry) {
  const tabs: { id: string; label: string }[] = [{ id: 'preview', label: 'Preview' }]
  if (!['text', 'code'].includes(entry.kind) && entry.size > 0 && entry.size < 50 * 1024 * 1024) tabs.push({ id: 'text', label: 'Text' })
  if (entry.size > 0) tabs.push({ id: 'hex', label: 'Hex' })
  if (['image', 'video', 'audio'].includes(entry.kind)) tabs.push({ id: 'meta', label: 'Metadata' })
  return tabs
}
