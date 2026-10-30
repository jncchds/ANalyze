/** Plain-language explanations of job phases, shown when hovering the phase name. */
const scan: Record<string, string> = {
  walking:
    'Listing every folder and file on disk and comparing them with the index: new files are added, changed ones updated, deleted ones removed. Folder sizes fill in as folders finish.',
  detecting:
    'Working out each new or changed file\'s type (video, image, document…) from its extension, or from its first 4 KB when the extension is unknown. The folder tree and sizes are already final.',
  categorizing: 'Applying your category rules to the files, so they get their category and tags.',
  archives:
    'Listing the files inside new or changed archives: zip, rar and 7z from their headers; tar and compressed files (when enabled) by decompressing them once.',
  extractions: 'Looking on all roots for folders that hold the files of each archive (same paths and sizes), completely or partly.',
  checking: 'Reading extracted files and comparing each with the CRC32 its archive stores for it. The archives themselves are not read.',
  pruning: 'Removing files and folders that the ignore list now covers from the index, so they leave every result list.',
  hashing: 'Reading files that have no content fingerprint yet ("Hash every file" is on). Needed to verify backups and notice corruption.',
  duplicates:
    'Looking for identical files: files of the same size get a quick fingerprint of 3×64 KB, and a full read only when that matches another file.',
  verifying: 'Re-reading files fingerprinted long ago to catch content that changed without a new size or date (possible corruption).',
  finishing: 'Saving the folder size history for the growth view and tidying the database.',
}

const other: Record<string, string> = {
  apply: 'Carrying out the plan: each item is checked again (still there, same size and date) before it is changed.',
  undo: 'Putting the plan\'s items back where they were.',
  classify: 'Asking the AI model for categories of files the rules do not cover.',
  suggest: 'Asking the AI model to propose new rules.',
  connecting: 'Contacting the app.',
  matching:
    'Finding each of the app\'s files on disk: first through the path mappings, then mappings learned from sample files, then by name and size.',
  labels: 'Writing the labels you see on files and folders.',
  comparing: 'Comparing this instance\'s files with the parent\'s copies.',
}

/** Tooltip for a job's phase; integration phases starting with "reading" are fetches from the app. */
export function phaseHint(kind: string, phase?: string): string | undefined {
  if (!phase) return undefined
  if ((kind === 'scan' || kind === 'processing') && scan[phase]) return scan[phase]
  if (other[phase]) return other[phase]
  if (phase.startsWith('reading')) return `Fetching the list from the app (${phase.slice(8) || 'items'}).`
  return scan[phase]
}
