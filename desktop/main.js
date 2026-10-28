// ANalyze desktop: starts the bundled ANalyze server on a private localhost port and shows its UI in a window.
//
// - The server is the normal ASP.NET app, published self-contained into resources/server (dev: desktop/server/<arch>).
// - Data (database, thumbnails, logs, keys) lives in <userData>/data; analyzed folders are kept in <userData>/desktop.json
//   and passed as ANalyze__Roots__N. Adding or removing a folder restarts the server.
// - The server gets a random password and API token on every launch; only this app's window sends the token
//   (injected as "Authorization: Bearer"), so other programs and web pages on this machine cannot use the API.
const { app, BrowserWindow, dialog, ipcMain, Menu, session, shell } = require('electron')
const { spawn } = require('node:child_process')
const crypto = require('node:crypto')
const fs = require('node:fs')
const net = require('node:net')
const path = require('node:path')

const HOST = '127.0.0.1'
const isMac = process.platform === 'darwin'

if (!app.requestSingleInstanceLock()) {
  app.quit()
} else {
  app.on('second-instance', () => {
    if (win) {
      if (win.isMinimized()) win.restore()
      win.focus()
    }
  })
}

let win = null
let server = null
let origin = null
let quitting = false
const token = crypto.randomBytes(32).toString('base64url')
const password = crypto.randomBytes(32).toString('base64url')
const output = []

// ---- configuration ----

const configPath = () => path.join(app.getPath('userData'), 'desktop.json')
const dataDir = () => path.join(app.getPath('userData'), 'data')

function loadConfig() {
  try {
    const c = JSON.parse(fs.readFileSync(configPath(), 'utf8'))
    return { roots: Array.isArray(c.roots) ? c.roots.filter((r) => typeof r === 'string') : [], port: Number(c.port) || 0, bounds: c.bounds }
  } catch {
    return { roots: [], port: 0 }
  }
}

let config = { roots: [], port: 0 }

function saveConfig() {
  fs.mkdirSync(path.dirname(configPath()), { recursive: true })
  fs.writeFileSync(configPath(), JSON.stringify(config, null, 2))
}

// ---- server process ----

function serverDir() {
  return app.isPackaged ? path.join(process.resourcesPath, 'server') : path.join(__dirname, 'server', process.arch)
}

function portFree(port) {
  return new Promise((resolve) => {
    const s = net.createServer()
    s.once('error', () => resolve(false))
    s.listen(port, HOST, () => s.close(() => resolve(true)))
  })
}

function randomPort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer()
    s.once('error', reject)
    s.listen(0, HOST, () => {
      const { port } = s.address()
      s.close(() => resolve(port))
    })
  })
}

// The same port on every launch keeps the UI's origin, so its saved preferences (theme, columns…) survive restarts.
async function choosePort() {
  if (config.port && (await portFree(config.port))) return config.port
  config.port = await randomPort()
  saveConfig()
  return config.port
}

async function startServer() {
  const dir = serverDir()
  const exe = path.join(dir, process.platform === 'win32' ? 'ANalyze.Web.exe' : 'ANalyze.Web')
  if (!fs.existsSync(exe)) {
    throw new Error(`The ANalyze server was not found at ${exe}.\nBuild it with "npm run server" in the desktop folder.`)
  }

  const port = await choosePort()
  origin = `http://${HOST}:${port}`
  const env = { ...process.env }
  for (const k of Object.keys(env)) {
    if (/^(ANalyze__|ASPNETCORE_HTTP)/i.test(k)) delete env[k]
  }
  Object.assign(env, {
    ASPNETCORE_URLS: origin,
    ASPNETCORE_ENVIRONMENT: 'Production',
    DOTNET_gcServer: '0',
    ANalyze__DataDir: dataDir(),
    ANalyze__Password: password,
    ANalyze__ApiToken: token,
  })
  config.roots.forEach((r, i) => (env[`ANalyze__Roots__${i}`] = r))
  if (isMac) {
    // Apps started from Finder don't get the shell's PATH; ffmpeg from Homebrew lives here.
    env.PATH = ['/opt/homebrew/bin', '/usr/local/bin', env.PATH].filter(Boolean).join(':')
  }

  output.length = 0
  const child = spawn(exe, [], { cwd: dir, env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true })
  server = child
  const keep = (chunk) => {
    for (const line of chunk.toString().split(/\r?\n/)) {
      if (line) output.push(line)
    }
    if (output.length > 40) output.splice(0, output.length - 40)
  }
  child.stdout.on('data', keep)
  child.stderr.on('data', keep)
  child.on('exit', (code, signal) => {
    if (server === child) server = null
    if (!quitting && !child.expectedExit) {
      dialog.showErrorBox('ANalyze stopped', `The ANalyze server exited (${signal ?? code}).\n\n${output.slice(-15).join('\n')}`)
      app.quit()
    }
  })

  const deadline = Date.now() + 90_000
  while (Date.now() < deadline) {
    if (child.exitCode !== null || child.signalCode !== null) {
      throw new Error(`The ANalyze server failed to start.\n\n${output.slice(-15).join('\n')}`)
    }
    try {
      const res = await fetch(`${origin}/health`)
      if (res.ok) return
    } catch {
      // not listening yet
    }
    await new Promise((r) => setTimeout(r, 250))
  }
  throw new Error('The ANalyze server did not start within 90 seconds.')
}

function stopServer() {
  const child = server
  if (!child) return Promise.resolve()
  child.expectedExit = true
  server = null
  return new Promise((resolve) => {
    const kill = setTimeout(() => child.kill('SIGKILL'), 10_000)
    child.once('exit', () => {
      clearTimeout(kill)
      resolve()
    })
    child.kill()
  })
}

async function runningJobs() {
  try {
    const res = await fetch(`${origin}/api/v1/jobs`, { headers: { Authorization: `Bearer ${token}` } })
    const jobs = await res.json()
    return jobs.filter((j) => j.state === 'running' || j.state === 'queued')
  } catch {
    return []
  }
}

async function restart(page) {
  await win.loadFile(path.join(__dirname, 'loading.html'))
  await stopServer()
  await startServer()
  await win.loadURL(origin + page)
}

// ---- roots (asked for by the Settings page or the File menu) ----

async function confirmRestart() {
  const jobs = await runningJobs()
  if (jobs.length === 0) return true
  const { response } = await dialog.showMessageBox(win, {
    type: 'warning',
    buttons: ['Restart now', 'Cancel'],
    defaultId: 1,
    cancelId: 1,
    message: 'ANalyze restarts to apply this change.',
    detail: `Running work stops: ${jobs.map((j) => j.kind).join(', ')}. You can start it again afterwards.`,
  })
  return response === 0
}

async function addRoot() {
  const { canceled, filePaths } = await dialog.showOpenDialog(win, {
    title: 'Add folders to analyze',
    properties: ['openDirectory', 'multiSelections', 'createDirectory'],
  })
  if (canceled || filePaths.length === 0) return false
  const added = filePaths.map((p) => path.resolve(p)).filter((p) => !config.roots.includes(p))
  if (added.length === 0 || !(await confirmRestart())) return false
  config.roots.push(...added)
  saveConfig()
  await restart('/settings')
  return true
}

async function removeRoot(root) {
  if (!config.roots.includes(root)) return false
  const { response } = await dialog.showMessageBox(win, {
    type: 'question',
    buttons: ['Remove', 'Cancel'],
    defaultId: 1,
    cancelId: 1,
    message: `Stop analyzing ${root}?`,
    detail: 'Nothing on disk changes. Its index is kept and comes back if you add the folder again.',
  })
  if (response !== 0 || !(await confirmRestart())) return false
  config.roots = config.roots.filter((r) => r !== root)
  saveConfig()
  await restart('/settings')
  return true
}

function fromApp(event) {
  return origin !== null && event.senderFrame?.url.startsWith(origin + '/')
}

ipcMain.handle('desktop:add-root', (e) => (fromApp(e) ? addRoot() : false))
ipcMain.handle('desktop:remove-root', (e, root) => (fromApp(e) ? removeRoot(root) : false))
ipcMain.handle('desktop:open-data', (e) => (fromApp(e) ? shell.openPath(dataDir()) : ''))

// ---- window ----

function menu() {
  const template = [
    ...(isMac ? [{ role: 'appMenu' }] : []),
    {
      label: 'File',
      submenu: [
        { label: 'Add folder…', accelerator: 'CmdOrCtrl+O', click: () => addRoot().catch(fatal) },
        { label: 'Open data folder', click: () => shell.openPath(dataDir()) },
        { type: 'separator' },
        isMac ? { role: 'close' } : { role: 'quit' },
      ],
    },
    { role: 'editMenu' },
    { role: 'viewMenu' },
    { role: 'windowMenu' },
    {
      role: 'help',
      submenu: [{ label: 'ANalyze on GitHub', click: () => shell.openExternal('https://github.com/jncchds/ANalyze') }],
    },
  ]
  Menu.setApplicationMenu(Menu.buildFromTemplate(template))
}

function createWindow() {
  const b = config.bounds ?? {}
  win = new BrowserWindow({
    width: b.width ?? 1400,
    height: b.height ?? 900,
    x: b.x,
    y: b.y,
    minWidth: 720,
    minHeight: 480,
    title: 'ANalyze',
    icon: path.join(__dirname, 'build', 'icon.png'),
    backgroundColor: '#0b1416',
    show: false,
    webPreferences: { preload: path.join(__dirname, 'preload.js'), contextIsolation: true, sandbox: true },
  })
  win.once('ready-to-show', () => win.show())
  win.on('close', () => {
    config.bounds = win.getBounds()
    saveConfig()
  })
  win.on('closed', () => (win = null))

  // Links to other sites (integrations, GitHub…) open in the default browser.
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (origin && url.startsWith(origin + '/')) return { action: 'allow' }
    if (/^https?:/i.test(url)) shell.openExternal(url)
    return { action: 'deny' }
  })
  win.webContents.on('will-navigate', (e, url) => {
    if (origin && url.startsWith(origin + '/')) return
    if (url.startsWith('file:')) return
    e.preventDefault()
    if (/^https?:/i.test(url)) shell.openExternal(url)
  })
}

function fatal(err) {
  if (quitting) return
  dialog.showErrorBox('ANalyze', String(err?.message ?? err))
  app.quit()
}

app.whenReady().then(async () => {
  config = loadConfig()
  menu()

  // Only requests to our server carry the token.
  session.defaultSession.webRequest.onBeforeSendHeaders((details, cb) => {
    const u = details.url
    if (origin && (u.startsWith(origin + '/') || u.startsWith(origin.replace('http:', 'ws:') + '/'))) {
      details.requestHeaders.Authorization = `Bearer ${token}`
    }
    cb({ requestHeaders: details.requestHeaders })
  })

  createWindow()
  await win.loadFile(path.join(__dirname, 'loading.html'))
  await startServer()
  await win.loadURL(origin + (config.roots.length === 0 ? '/settings' : '/'))
  if (config.roots.length === 0) {
    const { response } = await dialog.showMessageBox(win, {
      type: 'info',
      buttons: ['Choose folders…', 'Later'],
      defaultId: 0,
      message: 'Welcome to ANalyze',
      detail: 'Pick the folders or drives to analyze. You can add more later in Settings or with File → Add folder.',
    })
    if (response === 0) await addRoot()
  }
}).catch(fatal)

app.on('window-all-closed', () => app.quit())

app.on('before-quit', (e) => {
  if (quitting || !server) return
  e.preventDefault()
  quitting = true
  stopServer().finally(() => app.quit())
})
