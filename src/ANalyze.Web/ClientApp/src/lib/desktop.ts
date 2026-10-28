/** Present when the UI runs inside the ANalyze desktop app (see desktop/preload.js). */
export interface DesktopBridge {
  platform: string
  /** Opens a folder picker; resolves true when folders were added (the app then restarts the server and reloads). */
  addRoot(): Promise<boolean>
  removeRoot(path: string): Promise<boolean>
  openDataFolder(): Promise<string>
}

declare global {
  interface Window {
    analyzeDesktop?: DesktopBridge
  }
}

export const desktop: DesktopBridge | undefined = typeof window === 'undefined' ? undefined : window.analyzeDesktop
