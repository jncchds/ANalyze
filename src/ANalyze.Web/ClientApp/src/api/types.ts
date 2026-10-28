export type FileKind =
  | 'unknown' | 'image' | 'video' | 'audio' | 'document' | 'pdf' | 'text' | 'code' | 'archive'
  | 'diskImage' | 'executable' | 'font' | 'database' | 'ebook' | 'model3D' | 'other'

export type CategorySource = 'none' | 'rule' | 'ai' | 'manual'
export type ActionOp = 'trash' | 'delete' | 'move' | 'hardlink'
export type ConflictPolicy = 'skip' | 'rename' | 'overwrite'
export type PlanStatus = 'draft' | 'running' | 'done' | 'failed' | 'undone' | 'cancelled'
export type ItemStatus = 'pending' | 'done' | 'skipped' | 'failed' | 'undone'
export type DedupeKeep = 'oldest' | 'newest' | 'shortestPath' | 'longestPath' | 'preferredFolder'

export interface Entry {
  id: number
  rootId: number
  parentId?: number
  name: string
  relPath: string
  isDir: boolean
  isSymlink: boolean
  brokenLink: boolean
  size: number
  alloc: number
  files: number
  dirs: number
  mTime: number
  aTime: number
  kind: FileKind
  mime?: string
  ext?: string
  categoryId?: number
  categorySource: CategorySource
  tags: string[]
  hash?: string
  nLink: number
  error: boolean
}

export interface Annotation {
  entryId: number
  source: string
  kind: string
  label: string
  detail?: string
  state?: string
  url?: string
}

export type Annotations = Record<string, Annotation[]>

export interface EntryList {
  items: Entry[]
  total: number
  totalSize?: number
  annotations: Annotations
}

export interface Page<T> {
  items: T[]
  total: number
  totalSize?: number
}

export interface Root {
  id: number
  name: string
  path: string
  readOnly: boolean
  missing: boolean
  lastScanAt?: string
  rootEntryId?: number
  size: number
  files: number
  diskTotal?: number
  diskFree?: number
}

export interface Config {
  version?: string
  readOnly: boolean
  forcedReadOnly: boolean
  authRequired: boolean
  ffmpeg: boolean
  ai: boolean
  pathTokens: string[]
}

export interface ReleaseItem {
  kind: string
  text: string
}

export interface Release {
  version: string
  date?: string
  items: ReleaseItem[]
}

export interface JobProgress {
  kind: string
  state: 'queued' | 'running' | 'completed' | 'failed' | 'cancelled'
  phase?: string
  title?: string
  done: number
  total: number
  bytes: number
  totalBytes: number
  current?: string
  message?: string
  refId?: number
  startedAt: string
  finishedAt?: string
  counters?: Record<string, number>
}

export interface Category {
  id: number
  name: string
  color: string
  icon?: string
  description?: string
  sortOrder: number
  builtIn: boolean
}

export type RuleField = 'name' | 'path' | 'extension' | 'mime' | 'kind' | 'size' | 'modifiedDays' | 'accessedDays' | 'pathSegment' | 'depth'
export type RuleOp = 'equals' | 'contains' | 'startsWith' | 'endsWith' | 'glob' | 'regex' | 'in' | 'greaterThan' | 'lessThan'

export interface RuleClause {
  field: RuleField
  op: RuleOp
  value: string
  negate: boolean
}

export interface RuleCondition {
  match: 'all' | 'any'
  clauses: RuleClause[]
}

export interface Rule {
  id: number
  name: string
  order: number
  enabled: boolean
  condition: RuleCondition
  categoryId?: number
  tags: string[]
  stopProcessing: boolean
  source: 'user' | 'builtin' | 'ai'
  description?: string
}

export interface ActionTemplate {
  id: number
  name: string
  op: ActionOp
  destination?: string
  conflict: ConflictPolicy
  description?: string
}

export interface PlanRow {
  id: number
  name: string
  source: string
  status: PlanStatus
  conflict: ConflictPolicy
  createdAt: string
  appliedAt?: string
  trashPurgedAt?: string
  message?: string
  items: number
  included: number
  bytes: number
  done: number
  failed: number
  op?: ActionOp
}

export interface PlanItem {
  id: number
  planId: number
  entryId?: number
  rootId: number
  op: ActionOp
  sourcePath: string
  targetPath?: string
  keepPath?: string
  isDir: boolean
  expectedSize: number
  included: boolean
  status: ItemStatus
  warning?: string
  error?: string
  undoPath?: string
}

export interface PlanDetail {
  plan: PlanRow
  summary: {
    items: number
    included: number
    bytes: number
    warnings: number
    done: number
    skipped: number
    failed: number
    undone: number
    undoable: number
  }
  page: Page<PlanItem>
}

/** Same shape as the backend SearchFilter; every field optional. */
export interface SearchFilter {
  q?: string
  rootId?: number
  path?: string
  categoryId?: number
  uncategorized?: boolean
  kind?: FileKind
  ext?: string
  tag?: string
  minSize?: number
  maxSize?: number
  olderThanDays?: number
  newerThanDays?: number
  notAccessedDays?: number
  duplicates?: boolean
  dirs?: boolean
  emptyDirs?: boolean
  brokenLinks?: boolean
  sparse?: boolean
  hardlinked?: boolean
  segment?: string
  sort?: string
  asc?: boolean
}

export interface Insight {
  key: string
  title: string
  description: string
  severity: 'high' | 'medium' | 'low' | 'info'
  count: number
  bytes: number
  filter: SearchFilter
  suggestedOp?: ActionOp
}

export interface Summary {
  reclaimable: {
    duplicates: { bytes: number; groups: number }
    categories: { id: number; name: string; color: string; files: number; bytes: number }[]
  }
  trashBytes: number
  trashItems: number
  draftPlans: number
  aiReview: number
  aiSuggestions: number
  pinned: { id: number; rootId: number; name: string; relPath: string; size: number; delta?: number }[]
}

export interface IntegrationField {
  key: string
  label: string
  type: 'text' | 'url' | 'password' | 'bool' | 'number' | 'mappings' | 'select'
  help?: string
  default?: unknown
  options?: string[]
  placeholder?: string
}

export interface IntegrationSettings {
  enabled: boolean
  syncAfterScan: boolean
  values: Record<string, unknown>
}

export interface Integration {
  id: string
  name: string
  description: string
  icon: string
  fields: IntegrationField[]
  settings: IntegrationSettings
  status: { running: boolean; lastSync?: string; lastResult?: { ok: boolean; message: string; items: number; matched: number } }
}
