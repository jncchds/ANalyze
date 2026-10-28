import { lazy, type ComponentType, type LazyExoticComponent } from 'react'

/**
 * Frontend half of the integration plugins. A backend IIntegration with the same id gets its settings form
 * rendered generically on the Integrations page; registering it here also adds a dedicated page to the sidebar.
 */
export interface IntegrationUi {
  id: string
  label: string
  icon: string
  page: LazyExoticComponent<ComponentType>
}

export const integrationUis: IntegrationUi[] = [
  { id: 'plex', label: 'Plex', icon: '🎞️', page: lazy(() => import('./plex/PlexPage')) },
]

export const integrationUi = (id: string) => integrationUis.find((i) => i.id === id)
