import { lazy, type ComponentType, type LazyExoticComponent } from 'react'

/**
 * Frontend half of the integration plugins. A backend IIntegration with the same id gets its settings form
 * rendered generically on the Integrations page; registering it here also adds a dedicated page to the sidebar
 * (shown only while the integration is enabled).
 */
export interface IntegrationUi {
  id: string
  label: string
  icon: string
  page: LazyExoticComponent<ComponentType>
}

/** Plugins built on ItemIntegration share one configurable page. */
const items = (id: string) => lazy(() => import('./items/configs').then((m) => ({ default: m.pageFor(id) })))

export const integrationUis: IntegrationUi[] = [
  { id: 'plex', label: 'Plex', icon: '🎞️', page: lazy(() => import('./plex/PlexPage')) },
  { id: 'jellyfin', label: 'Jellyfin', icon: '🪼', page: items('jellyfin') },
  { id: 'emby', label: 'Emby', icon: '📀', page: items('emby') },
  { id: 'radarr', label: 'Radarr', icon: '🎬', page: items('radarr') },
  { id: 'sonarr', label: 'Sonarr', icon: '📺', page: items('sonarr') },
  { id: 'lidarr', label: 'Lidarr', icon: '🎵', page: items('lidarr') },
  { id: 'qbittorrent', label: 'qBittorrent', icon: '🧲', page: items('qbittorrent') },
  { id: 'transmission', label: 'Transmission', icon: '🧲', page: items('transmission') },
  { id: 'sabnzbd', label: 'SABnzbd', icon: '📥', page: items('sabnzbd') },
  { id: 'immich', label: 'Immich', icon: '🖼️', page: items('immich') },
  { id: 'photoprism', label: 'PhotoPrism', icon: '🌈', page: items('photoprism') },
  { id: 'docker', label: 'Docker', icon: '🐳', page: lazy(() => import('./docker/DockerPage')) },
]

export const integrationUi = (id: string) => integrationUis.find((i) => i.id === id)
