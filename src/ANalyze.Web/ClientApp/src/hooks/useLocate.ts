import { useSearchParams } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'

/**
 * Integration labels in the browser link to `/integrations/{id}?entry={entryId}`. This reads that entry and asks the integration's
 * `locate` endpoint where it belongs on the page (a title, a folder…). The page then replaces `entry` with its own URL state for
 * that place (e.g. `?item=<key>`), so Back and reloads work like anywhere else on the page.
 */
export function useLocate<T>(integrationId: string) {
  const [params] = useSearchParams()
  const entry = params.get('entry')
  const q = useQuery({
    queryKey: [integrationId, 'locate', entry],
    queryFn: () => api<T>(`/integrations/${integrationId}/locate`, { query: { entryId: entry } }),
    enabled: !!entry,
  })
  return { entry, target: entry ? q.data : undefined }
}
