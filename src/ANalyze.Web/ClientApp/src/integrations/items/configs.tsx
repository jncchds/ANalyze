import { ItemsPage, type ItemsConfig } from './ItemsPage'

const unmatched = (app: string) => ({
  id: 'unmatched' as const,
  label: 'Not found on disk',
  hint: `${app} lists these but ANalyze cannot find them: usually a missing path mapping (enable the Docker integration or add one), a folder not mounted here, or deleted files.`,
})

const arr = (id: string, name: string, kind: string, unit: string, noun: string): ItemsConfig => ({
  id,
  name,
  noun,
  unmonitor: true,
  kinds: [{ id: kind, label: noun[0].toUpperCase() + noun.slice(1), unit }],
  states: [
    { id: 'monitored', label: 'Monitored' },
    { id: 'unmonitored', label: 'Unmonitored' },
  ],
  filters: [{ id: 'missing', label: 'Files not found' }],
  tabs: [
    { id: 'titles', label: `${noun[0].toUpperCase() + noun.slice(1)} by size`, hint: `What ${name} manages, largest first. Unmonitor before cleaning up so ${name} does not download it again.` },
    {
      id: 'untracked',
      label: `Files ${name} does not track`,
      hint: `Media files inside ${name}'s root folders that it does not manage: old copies left by upgrades, extras, samples, failed or manual imports.`,
    },
    { id: 'orphans', label: 'Orphan folders', hint: `Folders in the root folders that belong to no ${noun.replace(/s$/, '')} — usually leftovers of deleted titles.` },
    unmatched(name),
  ],
})

const torrents = (id: string, name: string, noun = 'torrents'): ItemsConfig => ({
  id,
  name,
  noun,
  states: [
    { id: 'seeding', label: 'Seeding' },
    { id: 'completed', label: 'Finished, not seeding' },
    { id: 'downloading', label: 'Downloading' },
    { id: 'error', label: 'Errored' },
    ...(id === 'sabnzbd' ? [{ id: 'failed', label: 'Failed' }] : []),
  ],
  filters: [
    { id: 'old', label: 'Finished long ago' },
    { id: 'missing', label: 'Files not found' },
  ],
  tabs: [
    { id: 'titles', label: `${noun[0].toUpperCase() + noun.slice(1)} by size`, hint: `Seeding and downloading ${noun} are protected: cleanup plans skip them. Finished ones that are no longer seeding are the usual candidates.` },
    { id: 'orphans', label: 'Not in the client', hint: `Files and folders in ${name}'s download folders that ${name} no longer knows — removed jobs whose data stayed behind.` },
    {
      id: 'copies',
      label: 'Copied, not hard-linked',
      hint: 'Files elsewhere (usually your library) with the same content as a download but stored twice. Replace them with hard links in Duplicates → Dedupe (keep the download, hard-link the rest) to free the space and keep seeding.',
    },
    unmatched(name),
  ],
})

const mediaServer = (id: string, name: string): ItemsConfig => ({
  id,
  name,
  noun: 'titles',
  watch: true,
  kinds: [
    { id: 'movie', label: '🎬 Movies', unit: 'files' },
    { id: 'episode', label: '📺 Shows', unit: 'episodes' },
    { id: 'track', label: '🎵 Music', unit: 'tracks' },
  ],
  filters: [
    { id: 'watchedOld', label: 'Watched long ago' },
    { id: 'neverWatched', label: 'Never watched' },
    { id: 'missing', label: 'Files not found' },
  ],
  tabs: [
    { id: 'titles', label: 'Titles by size', hint: 'Movies, shows and artists ranked by size with watch state of the chosen user.' },
    { id: 'untracked', label: `Media ${name} does not know`, hint: `Video and audio files inside ${name}'s library folders that it does not list: samples, extras, unsupported or misnamed files, leftovers.` },
    unmatched(name),
  ],
})

const photos = (id: string, name: string): ItemsConfig => ({
  id,
  name,
  noun: id === 'immich' ? 'libraries' : 'photos',
  states: [{ id: 'favorite', label: 'Favorites' }, ...(id === 'immich' ? [{ id: 'archived', label: 'Archived' }, { id: 'trashed', label: 'In trash' }] : [])],
  tabs: [
    {
      id: 'copies',
      label: 'Copies outside the library',
      hint: `Photos and videos elsewhere on disk with exactly the same content as a file in ${name}: exports, old backups, phone dumps. The library keeps its copy.`,
    },
    { id: 'titles', label: id === 'immich' ? 'Libraries by size' : 'Photos by size', hint: `What ${name} manages, largest first.` },
    { id: 'untracked', label: 'Not in the library', hint: `Photos and videos inside ${name}'s folders that it has not indexed (yet).` },
    unmatched(name),
  ],
})

export const configs: Record<string, ItemsConfig> = {
  sonarr: arr('sonarr', 'Sonarr', 'episode', 'episodes', 'shows'),
  radarr: arr('radarr', 'Radarr', 'movie', 'files', 'movies'),
  lidarr: arr('lidarr', 'Lidarr', 'track', 'tracks', 'artists'),
  qbittorrent: torrents('qbittorrent', 'qBittorrent'),
  transmission: torrents('transmission', 'Transmission'),
  sabnzbd: torrents('sabnzbd', 'SABnzbd', 'downloads'),
  jellyfin: mediaServer('jellyfin', 'Jellyfin'),
  emby: mediaServer('emby', 'Emby'),
  immich: photos('immich', 'Immich'),
  photoprism: photos('photoprism', 'PhotoPrism'),
}

/** Default export per plugin for lazy loading: `import('./configs').then(m => page(m, 'sonarr'))`. */
export function pageFor(id: string) {
  const config = configs[id]
  return function PluginPage() {
    return <ItemsPage key={id} config={config} />
  }
}
