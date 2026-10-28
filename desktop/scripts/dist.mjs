// Builds the desktop installers for this OS: publishes the server for each arch, then runs electron-builder.
// Usage: node scripts/dist.mjs [--arch=x64,arm64]  (default: this machine's arch)
import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const desktop = join(here, '..')
const version = readFileSync(join(desktop, '..', 'VERSION'), 'utf8').trim()
const archs = (process.argv.find((a) => a.startsWith('--arch='))?.split('=')[1] ?? process.arch).split(',')
const os = { linux: '--linux', darwin: '--mac', win32: '--win' }[process.platform]
const shell = process.platform === 'win32'

archs.forEach((arch, i) => {
  // The UI is the same for every arch: build it once.
  const args = [join(here, 'publish-server.mjs'), `--arch=${arch}`, ...(i > 0 ? ['--skip-spa'] : [])]
  execFileSync(process.execPath, args, { stdio: 'inherit' })
})

execFileSync(
  'npx',
  ['electron-builder', os, ...archs.map((a) => `--${a}`), '--publish', 'never', `-c.extraMetadata.version=${version}`],
  { stdio: 'inherit', cwd: desktop, shell },
)
