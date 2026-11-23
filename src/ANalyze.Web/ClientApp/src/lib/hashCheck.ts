/** How a file's content hash compares with its backup copy's: equal, different, or not calculated on one or both sides. */
export type HashCheck = 'same' | 'differs' | 'notHere' | 'notOnBackup' | 'neither'

/** What a "same file" on the backup rests on: the content hash, or only the size when a hash is missing. */
export function hashCheckText(h: HashCheck | undefined) {
  switch (h) {
    case 'same':
      return 'same size and same content hash'
    case 'differs':
      return 'same size, but another content hash'
    case 'notHere':
      return 'same size; content hash not calculated here, so the content was not compared'
    case 'notOnBackup':
      return 'same size; content hash not calculated on the backup, so the content was not compared'
    default:
      return 'same size; content hash not calculated on either side, so the content was not compared'
  }
}
