const UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']

/** Binary units, one decimal under 100: 1.5 GB, 742 MB. */
export function bytes(n: number | undefined | null) {
  if (n === undefined || n === null || !Number.isFinite(n)) return '—'
  if (n < 1024) return `${n} B`
  let v = n
  let u = 0
  while (v >= 1024 && u < UNITS.length - 1) {
    v /= 1024
    u++
  }
  return `${v < 100 ? v.toFixed(1) : Math.round(v)} ${UNITS[u]}`
}

export function count(n: number | undefined | null) {
  return n === undefined || n === null ? '—' : n.toLocaleString()
}

export function pct(part: number, whole: number) {
  if (!whole) return '0%'
  const p = (part / whole) * 100
  return p < 0.1 && p > 0 ? '<0.1%' : `${p < 10 ? p.toFixed(1) : Math.round(p)}%`
}

export function date(unixSeconds: number | undefined | null) {
  if (!unixSeconds) return '—'
  return new Date(unixSeconds * 1000).toLocaleDateString()
}

export function dateTime(iso: string | undefined | null) {
  if (!iso) return '—'
  return new Date(iso).toLocaleString()
}

/** "3 h ago", "2 d ago". */
export function ago(iso: string | number | undefined | null) {
  if (!iso) return 'never'
  const t = typeof iso === 'number' ? iso * 1000 : new Date(iso).getTime()
  const s = Math.max(0, (Date.now() - t) / 1000)
  if (s < 60) return 'just now'
  if (s < 3600) return `${Math.floor(s / 60)} min ago`
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`
  if (s < 86400 * 60) return `${Math.floor(s / 86400)} d ago`
  if (s < 86400 * 730) return `${Math.floor(s / (86400 * 30))} mo ago`
  return `${Math.floor(s / (86400 * 365))} y ago`
}

export function duration(fromIso: string, toIso?: string) {
  const s = Math.max(0, ((toIso ? new Date(toIso).getTime() : Date.now()) - new Date(fromIso).getTime()) / 1000)
  if (s < 60) return `${Math.round(s)} s`
  if (s < 3600) return `${Math.floor(s / 60)} min ${Math.round(s % 60)} s`
  return `${Math.floor(s / 3600)} h ${Math.floor((s % 3600) / 60)} min`
}

export function signedBytes(n: number) {
  return `${n > 0 ? '+' : n < 0 ? '−' : ''}${bytes(Math.abs(n))}`
}

/** Parses "10 MB", "1.5g", "500k" (binary units) into bytes. */
export function parseSize(s: string): number | undefined {
  const m = /^\s*(\d+(?:\.\d+)?)\s*([kmgtp]?)i?b?\s*$/i.exec(s)
  if (!m) return undefined
  const mul = { '': 1, k: 1024, m: 1024 ** 2, g: 1024 ** 3, t: 1024 ** 4, p: 1024 ** 5 }[m[2].toLowerCase() as '' | 'k' | 'm' | 'g' | 't' | 'p']
  return Math.round(parseFloat(m[1]) * mul)
}

export function kindLabel(kind: string) {
  const map: Record<string, string> = {
    diskImage: 'Disk image',
    model3D: '3D model',
    pdf: 'PDF',
  }
  return map[kind] ?? kind.charAt(0).toUpperCase() + kind.slice(1)
}

export function kindIcon(kind: string, isDir = false) {
  if (isDir) return '📁'
  return (
    {
      image: '🖼️', video: '🎬', audio: '🎵', document: '📄', pdf: '📕', text: '📝', code: '💻', archive: '🗜️',
      diskImage: '💿', executable: '⚙️', font: '🔤', database: '🗄️', ebook: '📚', model3D: '🧊',
    } as Record<string, string>
  )[kind] ?? '📄'
}
