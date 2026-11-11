import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { api, fileUrl } from '../api/client'
import type { Entry } from '../api/types'
import { bytes, count } from '../lib/format'
import { ErrorBox, Loading } from './ui'

/** URL of the backup's copy of a local file (streamed through this instance from the parent ANalyze). */
export const backupUrl = (id: number) => `/api/v1/integrations/parent/content?entryId=${id}`

/** Bytes [from, from+length) of a file; shorter at the end, empty past it. */
async function readRange(url: string, from: number, length: number, signal?: AbortSignal): Promise<Uint8Array> {
  const res = await fetch(url, { headers: { Range: `bytes=${from}-${from + length - 1}` }, credentials: 'same-origin', signal })
  if (res.status === 416) return new Uint8Array()
  if (!res.ok) {
    const text = await res.text()
    let message = res.statusText || `HTTP ${res.status}`
    try {
      const b = JSON.parse(text)
      message = b.detail ?? b.title ?? message
    } catch {
      /* not JSON */
    }
    throw new Error(message)
  }
  const buf = new Uint8Array(await res.arrayBuffer())
  // A server without Range support sends the whole file.
  return res.status === 200 ? buf.slice(from, from + length) : buf
}

type Mode = 'visual' | 'bytes'

/**
 * This file next to its copy on the backup server, for looking at the difference: images side by side, flipped or as a
 * difference map; video and audio with linked playback; PDFs side by side; text as a line diff; any file byte by byte.
 */
export function BackupCompareView({ entry }: { entry: Entry }) {
  const summary = useQuery({ queryKey: ['parent', 'summary'], queryFn: () => api<{ snapshot?: { parentName: string } }>('/integrations/parent/summary') })
  const parentName = summary.data?.snapshot?.parentName ?? 'backup'
  const visual = ['image', 'video', 'audio', 'pdf', 'text', 'code'].includes(entry.kind)
  const [mode, setMode] = useState<Mode>(visual ? 'visual' : 'bytes')
  const here = fileUrl(entry.id, 'content')
  const there = backupUrl(entry.id)
  return (
    <div className="space-y-3">
      {visual && (
        <div className="flex gap-1">
          {(['visual', 'bytes'] as Mode[]).map((m) => (
            <button key={m} className={clsx('btn-ghost px-2 py-1 text-xs', mode === m && 'bg-accent-faint text-accent')} onClick={() => setMode(m)}>
              {m === 'visual' ? 'View' : 'Bytes'}
            </button>
          ))}
        </div>
      )}
      {mode === 'bytes' ? (
        <HexCompare entry={entry} here={here} there={there} parentName={parentName} />
      ) : entry.kind === 'image' ? (
        <ImageCompare here={here} there={there} parentName={parentName} />
      ) : entry.kind === 'video' || entry.kind === 'audio' ? (
        <MediaCompare kind={entry.kind} here={here} there={there} parentName={parentName} />
      ) : entry.kind === 'pdf' ? (
        <SideBySide parentName={parentName} a={<iframe src={here} title="Here" className="h-[70vh] w-full rounded-md border border-line bg-white" />} b={<iframe src={there} title={parentName} className="h-[70vh] w-full rounded-md border border-line bg-white" />} />
      ) : (
        <TextCompare here={here} there={there} parentName={parentName} />
      )}
    </div>
  )
}

function SideBySide({ a, b, parentName }: { a: ReactNode; b: ReactNode; parentName: string }) {
  return (
    <div className="grid gap-3 md:grid-cols-2">
      <div className="min-w-0">
        <div className="mb-1 text-xs font-semibold">Here</div>
        {a}
      </div>
      <div className="min-w-0">
        <div className="mb-1 text-xs font-semibold">{parentName}</div>
        {b}
      </div>
    </div>
  )
}

// ---- Images ----

type ImageMode = 'side' | 'flip' | 'diff'

function ImageCompare({ here, there, parentName }: { here: string; there: string; parentName: string }) {
  const [mode, setMode] = useState<ImageMode>('side')
  const [showBackup, setShowBackup] = useState(false)
  const [auto, setAuto] = useState(false)
  useEffect(() => {
    if (mode !== 'flip' || !auto) return
    const t = setInterval(() => setShowBackup((s) => !s), 700)
    return () => clearInterval(t)
  }, [mode, auto])
  const img = 'max-h-[70vh] w-full rounded-md bg-surface-2 object-contain'
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-1 text-xs">
        {(
          [
            ['side', 'Side by side'],
            ['flip', 'Flip'],
            ['diff', 'Difference'],
          ] as [ImageMode, string][]
        ).map(([m, label]) => (
          <button key={m} className={clsx('btn-ghost px-2 py-1 text-xs', mode === m && 'bg-accent-faint text-accent')} onClick={() => setMode(m)}>
            {label}
          </button>
        ))}
        {mode === 'flip' && (
          <>
            <span className="mx-2 muted">showing {showBackup ? parentName : 'here'} — click the image to switch</span>
            <label className="flex items-center gap-1">
              <input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} /> auto
            </label>
          </>
        )}
      </div>
      {mode === 'side' && <SideBySide parentName={parentName} a={<img src={here} alt="Here" className={img} />} b={<img src={there} alt={parentName} className={img} />} />}
      {mode === 'flip' && (
        // Both stay loaded at the same place so switching shows only what changed.
        <div className="relative cursor-pointer select-none" onClick={() => setShowBackup((s) => !s)}>
          <img src={here} alt="Here" className={clsx(img, showBackup && 'invisible')} />
          <img src={there} alt={parentName} className={clsx(img, 'absolute inset-0', !showBackup && 'invisible')} />
        </div>
      )}
      {mode === 'diff' && <ImageDiff here={here} there={there} parentName={parentName} />}
    </div>
  )
}

function loadImage(src: string) {
  return new Promise<HTMLImageElement>((resolve, reject) => {
    const i = new Image()
    i.onload = () => resolve(i)
    i.onerror = () => reject(new Error(`Could not load ${src.includes('integrations') ? 'the backup copy' : 'this copy'} as an image.`))
    i.src = src
  })
}

/** Pixels that differ in red over a dimmed copy of this image. */
function ImageDiff({ here, there, parentName }: { here: string; there: string; parentName: string }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const [state, setState] = useState<{ error?: string; note?: string; changed?: number; total?: number }>({})
  useEffect(() => {
    let cancelled = false
    Promise.all([loadImage(here), loadImage(there)])
      .then(([a, b]) => {
        if (cancelled || !canvas.current) return
        if (a.naturalWidth !== b.naturalWidth || a.naturalHeight !== b.naturalHeight) {
          setState({ note: `Different dimensions: ${a.naturalWidth}×${a.naturalHeight} here, ${b.naturalWidth}×${b.naturalHeight} on ${parentName}.` })
          return
        }
        const scale = Math.min(1, 4096 / Math.max(a.naturalWidth, a.naturalHeight))
        const w = Math.max(1, Math.round(a.naturalWidth * scale))
        const h = Math.max(1, Math.round(a.naturalHeight * scale))
        const c = canvas.current
        c.width = w
        c.height = h
        const ctx = c.getContext('2d', { willReadFrequently: true })!
        ctx.drawImage(b, 0, 0, w, h)
        const pb = ctx.getImageData(0, 0, w, h).data
        ctx.drawImage(a, 0, 0, w, h)
        const img = ctx.getImageData(0, 0, w, h)
        const pa = img.data
        let changed = 0
        for (let i = 0; i < pa.length; i += 4) {
          const d = Math.abs(pa[i] - pb[i]) + Math.abs(pa[i + 1] - pb[i + 1]) + Math.abs(pa[i + 2] - pb[i + 2])
          if (d > 0) {
            changed++
            pa[i] = 255
            pa[i + 1] = 0
            pa[i + 2] = 0
          } else {
            const g = (pa[i] + pa[i + 1] + pa[i + 2]) / 3 / 3
            pa[i] = pa[i + 1] = pa[i + 2] = g
          }
          pa[i + 3] = 255
        }
        ctx.putImageData(img, 0, 0)
        setState({ changed, total: w * h, note: scale < 1 ? `Compared at ${w}×${h}.` : undefined })
      })
      .catch((e: Error) => !cancelled && setState({ error: e.message }))
    return () => {
      cancelled = true
    }
  }, [here, there, parentName])
  if (state.error) return <ErrorBox error={state.error} />
  return (
    <div className="space-y-1">
      <p className="text-xs muted">
        {state.total === undefined
          ? state.note ?? 'Decoding both images…'
          : state.changed === 0
            ? `The decoded pixels are identical: the difference is in the file around them (metadata, compression). ${state.note ?? ''}`
            : `${count(state.changed)} of ${count(state.total)} pixels differ (${((state.changed! / state.total) * 100).toFixed(2)}%), in red. ${state.note ?? ''}`}
      </p>
      <canvas ref={canvas} className={clsx('max-h-[70vh] max-w-full rounded-md bg-surface-2', state.total === undefined && 'hidden')} />
    </div>
  )
}

// ---- Video and audio ----

/** Both players side by side; with "linked", play, pause, seeking and speed follow each other and only one is heard. */
function MediaCompare({ kind, here, there, parentName }: { kind: 'video' | 'audio'; here: string; there: string; parentName: string }) {
  const a = useRef<HTMLVideoElement & HTMLAudioElement>(null)
  const b = useRef<HTMLVideoElement & HTMLAudioElement>(null)
  const [linked, setLinked] = useState(true)
  const [hear, setHear] = useState<'here' | 'backup'>('here')
  useEffect(() => {
    if (a.current) a.current.muted = linked && hear !== 'here'
    if (b.current) b.current.muted = linked && hear !== 'backup'
  }, [linked, hear])
  useEffect(() => {
    const x = a.current
    const y = b.current
    if (!linked || !x || !y) return
    let busy = false
    const follow = (from: HTMLMediaElement, to: HTMLMediaElement) => (e: Event) => {
      if (busy) return
      busy = true
      if (e.type === 'seeked' || Math.abs(to.currentTime - from.currentTime) > 0.3) to.currentTime = from.currentTime
      if (e.type === 'play') void to.play().catch(() => undefined)
      if (e.type === 'pause') to.pause()
      if (e.type === 'ratechange') to.playbackRate = from.playbackRate
      setTimeout(() => (busy = false), 50)
    }
    const events = ['play', 'pause', 'seeked', 'ratechange']
    const fx = follow(x, y)
    const fy = follow(y, x)
    events.forEach((ev) => {
      x.addEventListener(ev, fx)
      y.addEventListener(ev, fy)
    })
    return () =>
      events.forEach((ev) => {
        x.removeEventListener(ev, fx)
        y.removeEventListener(ev, fy)
      })
  }, [linked])
  const cls = kind === 'video' ? 'max-h-[60vh] w-full rounded-md bg-black' : 'w-full'
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-3 text-xs">
        <label className="flex items-center gap-1">
          <input type="checkbox" checked={linked} onChange={(e) => setLinked(e.target.checked)} /> Linked playback
        </label>
        {linked && (
          <span className="flex items-center gap-1">
            <span className="muted">Sound from</span>
            {(['here', 'backup'] as const).map((s) => (
              <button key={s} className={clsx('btn-ghost px-2 py-0.5 text-xs', hear === s && 'bg-accent-faint text-accent')} onClick={() => setHear(s)}>
                {s === 'here' ? 'here' : parentName}
              </button>
            ))}
          </span>
        )}
      </div>
      {kind === 'video' ? (
        <SideBySide parentName={parentName} a={<video ref={a} src={here} controls preload="metadata" className={cls} />} b={<video ref={b} src={there} controls preload="metadata" className={cls} />} />
      ) : (
        <SideBySide parentName={parentName} a={<audio ref={a} src={here} controls preload="metadata" className={cls} />} b={<audio ref={b} src={there} controls preload="metadata" className={cls} />} />
      )}
    </div>
  )
}

// ---- Text ----

type Op = { t: ' ' | '-' | '+'; a?: number; b?: number }

/** Myers diff of two line arrays; null when more than maxD lines differ. */
export function diffLines(a: string[], b: string[], maxD = 2000): Op[] | null {
  const n = a.length
  const m = b.length
  const max = n + m
  const off = max + 1
  const v = new Int32Array(2 * max + 3)
  const trace: Int32Array[] = []
  for (let d = 0; d <= Math.min(max, maxD); d++) {
    for (let k = -d; k <= d; k += 2) {
      let x = k === -d || (k !== d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1
      let y = x - k
      while (x < n && y < m && a[x] === b[y]) {
        x++
        y++
      }
      v[off + k] = x
      if (x >= n && y >= m) {
        trace.push(v.slice(off - d, off + d + 1))
        return backtrack(trace, n, m)
      }
    }
    trace.push(v.slice(off - d, off + d + 1))
  }
  return null
}

function backtrack(trace: Int32Array[], n: number, m: number): Op[] {
  const ops: Op[] = []
  let x = n
  let y = m
  for (let d = trace.length - 1; d > 0; d--) {
    const vp = trace[d - 1]
    const at = (k: number) => vp[k + d - 1]
    const k = x - y
    const prevK = k === -d || (k !== d && at(k - 1) < at(k + 1)) ? k + 1 : k - 1
    const prevX = at(prevK)
    const prevY = prevX - prevK
    while (x > prevX && y > prevY) ops.push({ t: ' ', a: --x, b: --y })
    if (x === prevX) ops.push({ t: '+', b: --y })
    else ops.push({ t: '-', a: --x })
  }
  while (x > 0 && y > 0) ops.push({ t: ' ', a: --x, b: --y })
  return ops.reverse()
}

const TEXT_LIMIT = 1024 * 1024

function TextCompare({ here, there, parentName }: { here: string; there: string; parentName: string }) {
  const q = useQuery({
    queryKey: ['backup-text', here, there],
    queryFn: async () => {
      const [a, b] = await Promise.all([readRange(here, 0, TEXT_LIMIT), readRange(there, 0, TEXT_LIMIT)])
      const dec = new TextDecoder('utf-8')
      return { a: dec.decode(a).split(/\r?\n/), b: dec.decode(b).split(/\r?\n/), cut: a.length === TEXT_LIMIT || b.length === TEXT_LIMIT }
    },
  })
  const ops = useMemo(() => (q.data ? diffLines(q.data.a, q.data.b) : undefined), [q.data])
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const { a, b, cut } = q.data!
  if (!ops) return <p className="text-sm muted">Too many lines differ to show a line diff; use Bytes.</p>
  const changed = ops.filter((o) => o.t !== ' ').length
  // Hunks: changed lines with 3 lines of context, gaps folded.
  const keep = new Uint8Array(ops.length)
  ops.forEach((o, i) => {
    if (o.t !== ' ') for (let j = Math.max(0, i - 3); j <= Math.min(ops.length - 1, i + 3); j++) keep[j] = 1
  })
  const rows: ReactNode[] = []
  let skipped = 0
  ops.forEach((o, i) => {
    if (!keep[i]) {
      skipped++
      return
    }
    if (skipped) {
      rows.push(
        <div key={`s${i}`} className="bg-surface-2 px-2 muted">
          ⋯ {count(skipped)} identical lines
        </div>,
      )
      skipped = 0
    }
    rows.push(
      <div key={i} className={clsx('flex', o.t === '-' && 'bg-danger/10', o.t === '+' && 'bg-accent-faint')}>
        <span className="w-12 shrink-0 pr-2 text-right muted">{o.a !== undefined ? o.a + 1 : ''}</span>
        <span className="w-12 shrink-0 pr-2 text-right muted">{o.b !== undefined ? o.b + 1 : ''}</span>
        <span className={clsx('w-4 shrink-0', o.t === '-' ? 'text-danger' : o.t === '+' ? 'text-accent' : 'muted')}>{o.t}</span>
        <span className="whitespace-pre-wrap break-all">{o.t === '+' ? b[o.b!] : a[o.a!]}</span>
      </div>,
    )
  })
  if (skipped) rows.push(<div key="end" className="bg-surface-2 px-2 muted">⋯ {count(skipped)} identical lines</div>)
  return (
    <div>
      <p className="mb-2 text-xs muted">
        {changed === 0 ? 'The text is identical' : `${count(ops.filter((o) => o.t === '-').length)} lines only here (−), ${count(ops.filter((o) => o.t === '+').length)} only on ${parentName} (+)`}
        {cut ? ` in the first ${bytes(TEXT_LIMIT)}` : ''}
        {changed === 0 ? ' — the difference is in line endings, encoding or invisible bytes; see Bytes.' : '.'}
      </p>
      <div className="max-h-[65vh] overflow-auto rounded-md border border-line bg-input py-1 font-mono text-xs leading-5">{rows}</div>
    </div>
  )
}

// ---- Bytes ----

const PAGE = 4096
const SCAN = 1 << 20

interface CompareReport {
  report?: { ranges: { offset: number; length: number }[] }
}

/** Both copies byte by byte, 16 per row, differing bytes marked; steps to the next or previous difference. */
function HexCompare({ entry, here, there, parentName }: { entry: Entry; here: string; there: string; parentName: string }) {
  const qc = useQueryClient()
  const [offset, setOffset] = useState(0)
  const [jump, setJump] = useState('')
  const [scan, setScan] = useState<{ at: number; error?: string; none?: boolean } | null>(null)
  const abort = useRef<AbortController | null>(null)
  useEffect(() => () => abort.current?.abort(), [])
  const q = useQuery({
    queryKey: ['backup-hex', entry.id, offset],
    queryFn: async () => {
      const [a, b] = await Promise.all([readRange(here, offset, PAGE), readRange(there, offset, PAGE)])
      return { a, b }
    },
  })
  // Ranges from a finished "What happened?" comparison, as shortcuts.
  const ranges = qc.getQueryData<CompareReport>(['parent', 'compare', entry.id])?.report?.ranges ?? []

  const find = async (dir: 1 | -1) => {
    abort.current?.abort()
    const ctl = new AbortController()
    abort.current = ctl
    // Forward: from the end of this page on. Backward: everything before this page, nearest first. One MB per step from each side.
    let lo = dir === 1 ? offset + PAGE : Math.max(0, offset - SCAN)
    let hi = dir === 1 ? lo + SCAN : offset
    try {
      while (hi > lo) {
        setScan({ at: lo })
        const [a, b] = await Promise.all([readRange(here, lo, hi - lo, ctl.signal), readRange(there, lo, hi - lo, ctl.signal)])
        const len = Math.max(a.length, b.length)
        if (len === 0) break
        let hit = -1
        if (dir === 1) {
          for (let i = 0; i < len && hit < 0; i++) if (a[i] !== b[i]) hit = i
        } else {
          for (let i = len - 1; i >= 0 && hit < 0; i--) if (a[i] !== b[i]) hit = i
        }
        if (hit >= 0) {
          setOffset(Math.floor((lo + hit) / 16) * 16)
          setScan(null)
          return
        }
        if (dir === 1) {
          if (len < hi - lo) break
          lo = hi
          hi = lo + SCAN
        } else {
          if (lo === 0) break
          hi = lo
          lo = Math.max(0, hi - SCAN)
        }
      }
      setScan({ at: lo, none: true })
    } catch (e) {
      if (!ctl.signal.aborted) setScan({ at: lo, error: (e as Error).message })
    }
  }

  const size = Math.max(entry.size, offset + (q.data ? Math.max(q.data.a.length, q.data.b.length) : 0))
  const rows: ReactNode[] = []
  let diffs = 0
  if (q.data) {
    const { a, b } = q.data
    const len = Math.max(a.length, b.length)
    for (let i = 0; i < len; i += 16) {
      const cells = (x: Uint8Array, y: Uint8Array) => {
        const hex: ReactNode[] = []
        let text = ''
        const marks: boolean[] = []
        for (let j = i; j < i + 16; j++) {
          const v = x[j]
          const differs = j < len && x[j] !== y[j]
          if (differs && x === a) diffs++
          hex.push(
            <span key={j} className={clsx(differs && 'rounded-sm bg-danger/25 text-danger')}>
              {v === undefined ? '  ' : v.toString(16).padStart(2, '0')}
            </span>,
          )
          if (j < i + 15) hex.push(' ')
          text += v === undefined ? ' ' : v >= 32 && v < 127 ? String.fromCharCode(v) : '·'
          marks.push(differs)
        }
        return (
          <>
            <span className="whitespace-pre">{hex}</span>
            <span className="whitespace-pre">
              {[...text].map((c, k) => (
                <span key={k} className={marks[k] ? 'text-danger' : 'text-accent'}>
                  {c}
                </span>
              ))}
            </span>
          </>
        )
      }
      rows.push(
        <div key={i} className="flex gap-3">
          <span className="text-muted">{(offset + i).toString(16).padStart(8, '0')}</span>
          {cells(a, b)}
          <span className="text-muted">│</span>
          {cells(b, a)}
        </div>,
      )
    }
  }

  return (
    <div>
      <div className="mb-2 flex flex-wrap items-center gap-2 text-xs">
        <button className="btn-secondary px-2 py-1 text-xs" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>
          ← Page
        </button>
        <button className="btn-secondary px-2 py-1 text-xs" disabled={offset + PAGE >= size} onClick={() => setOffset(offset + PAGE)}>
          Page →
        </button>
        <button className="btn-secondary px-2 py-1 text-xs" disabled={offset === 0 || !!scan && !scan.none && !scan.error} onClick={() => find(-1)}>
          ⇤ Previous difference
        </button>
        <button className="btn-secondary px-2 py-1 text-xs" disabled={!!scan && !scan.none && !scan.error} onClick={() => find(1)}>
          Next difference ⇥
        </button>
        <form
          className="flex items-center gap-1"
          onSubmit={(e) => {
            e.preventDefault()
            const n = jump.startsWith('0x') ? parseInt(jump, 16) : parseInt(jump, 10)
            if (Number.isFinite(n)) setOffset(Math.max(0, Math.floor(n / 16) * 16))
          }}
        >
          <input className="input w-32 py-0.5 text-xs" placeholder="offset (0x…)" value={jump} onChange={(e) => setJump(e.target.value)} />
        </form>
        <span className="ml-auto muted">
          {offset.toLocaleString()}–{(offset + PAGE).toLocaleString()} · {diffs ? `${count(diffs)} bytes differ on this page` : 'no difference on this page'}
        </span>
      </div>
      {scan && (
        <p className={clsx('mb-2 text-xs', scan.error ? 'text-danger' : 'muted')}>
          {scan.error ?? (scan.none ? 'No further difference.' : `Searching… at ${bytes(scan.at)}`)}
          {!scan.none && !scan.error && (
            <button className="link ml-2" onClick={() => { abort.current?.abort(); setScan(null) }}>
              Stop
            </button>
          )}
        </p>
      )}
      {ranges.length > 0 && (
        <div className="mb-2 flex flex-wrap items-center gap-1 text-xs">
          <span className="muted">Differences found by “What happened?”:</span>
          {ranges.slice(0, 12).map((r) => (
            <button key={r.offset} className="chip" onClick={() => setOffset(Math.floor(r.offset / 16) * 16)}>
              {r.offset.toLocaleString()} ({bytes(r.length)})
            </button>
          ))}
        </div>
      )}
      <div className="mb-1 flex gap-3 font-mono text-xs font-semibold">
        <span className="w-[4.5rem]" />
        <span className="w-[31rem]">Here</span>
        <span>{parentName}</span>
      </div>
      {q.isLoading ? <Loading /> : q.error ? <ErrorBox error={q.error} /> : <div className="max-h-[60vh] overflow-auto rounded-md border border-line bg-input p-3 font-mono text-xs leading-5">{rows}</div>}
    </div>
  )
}
