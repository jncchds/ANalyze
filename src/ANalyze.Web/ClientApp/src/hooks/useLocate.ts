import { useSearchParams } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'

/**
 * Integration labels in the browser link to `/integrations/{id}?entry={entryId}`. This reads that entry and asks the integration's
 * `locate` endpoint where it belongs on the page (a title, a folder…). `clear` drops the parameter, e.g. when the opened dialog closes.
 */
export function useLocate<T>(integrationId: string) {
  const [params, setParams] = useSearchParams()
  const entry = params.get('entry')
  const q = useQuery({
    queryKey: [integrationId, 'locate', entry],
    queryFn: () => api<T>(`/integrations/${integrationId}/locate`, { query: { entryId: entry } }),
    enabled: !!entry,
  })
  const clear = () =>
    setParams(
      (p) => {
        p.delete('entry')
        return p
      },
      { replace: true },
    )
  return { entry, target: entry ? q.data : undefined, clear }
}
