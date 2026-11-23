import { useMutation } from '@tanstack/react-query'
import clsx from 'clsx'
import { api } from '../api/client'
import { isActive, useJob } from '../hooks/useJobs'
import { bytes, count } from '../lib/format'
import { Progress } from './ui'

/**
 * Emptying a plan's trash, shown like a plan's apply progress: the file being deleted, bytes freed so far and a Stop button.
 * `pending`: the trash is queued (another plan's trash is being emptied first). `compact`: inside a list row instead of a card.
 */
export function TrashProgress({ planId, pending, compact }: { planId: number; pending?: boolean; compact?: boolean }) {
  const job = useJob('trash')
  const cancel = useMutation({ mutationFn: () => api('/trash/cancel', { method: 'POST' }) })
  const mine = job?.refId === planId
  const running = mine && isActive(job)
  const failed = mine && !running && (job.state === 'failed' || job.state === 'cancelled') && job.message

  if (!running && !pending) {
    return failed ? <p className={clsx('text-sm', job.state === 'failed' ? 'text-danger' : 'muted', compact ? 'mt-1' : 'mb-3')}>Emptying the trash: {job.message}</p> : null
  }

  return (
    <div className={compact ? 'mt-1.5 w-full' : 'card mb-4 p-3'}>
      <div className="mb-1 flex items-center gap-2 text-sm">
        <span className="min-w-0 flex-1 truncate" title={running ? job.current : undefined}>
          {running ? `Emptying trash… ${job.current ?? ''}` : 'Waiting to empty the trash…'}
        </span>
        {running && (
          <>
            <span className="tabular">
              {bytes(job.bytes)}
              {job.totalBytes ? ` / ${bytes(job.totalBytes)}` : ''} · {count(job.done)} files
            </span>
            <button className="btn-secondary px-2 py-0.5 text-xs" onClick={() => cancel.mutate()} disabled={cancel.isPending}>
              Stop
            </button>
          </>
        )}
      </div>
      <Progress value={running ? job.bytes : 0} max={running ? job.totalBytes : 0} />
    </div>
  )
}
