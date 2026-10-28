// Bridge for the UI: lets the Settings page add and remove analyzed folders (roots) in the desktop app.
const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('analyzeDesktop', {
  platform: process.platform,
  addRoot: () => ipcRenderer.invoke('desktop:add-root'),
  removeRoot: (path) => ipcRenderer.invoke('desktop:remove-root', String(path)),
  openDataFolder: () => ipcRenderer.invoke('desktop:open-data'),
})
