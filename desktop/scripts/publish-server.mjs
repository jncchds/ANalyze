// Publishes the ANalyze server (with the built UI) self-contained into desktop/server/<arch>.
// Usage: node scripts/publish-server.mjs [--arch=x64|arm64] [--os=linux|osx|win] [--skip-spa]  (defaults: this machine)
import { execFileSync } from 'node:child_process'
import { readFileSync, rmSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const desktop = join(here, '..')
const repo = join(desktop, '..')

const arg = (name, fallback) => process.argv.find((a) => a.startsWith(`--${name}=`))?.split('=')[1] ?? fallback
const os = arg('os', { linux: 'linux', darwin: 'osx', win32: 'win' }[process.platform])
const arch = arg('arch', process.arch)
if (!os || !['x64', 'arm64'].includes(arch)) throw new Error(`Unsupported platform ${process.platform}/${arch}`)

const out = join(desktop, 'server', arch)
rmSync(out, { recursive: true, force: true })
const version = readFileSync(join(repo, 'VERSION'), 'utf8').trim()
const shell = process.platform === 'win32'
const env = { ...process.env, APP_VERSION: version }

// Like the Docker build: the UI goes into wwwroot before publishing, so MapStaticAssets knows its files
// (the csproj's own SPA step runs after the static-asset manifest is computed).
const spa = join(repo, 'src', 'ANalyze.Web', 'ClientApp')
if (!process.argv.includes('--skip-spa')) {
  console.log('Building the UI')
  execFileSync('npm', ['ci'], { cwd: spa, stdio: 'inherit', env, shell })
  execFileSync('npm', ['run', 'build'], { cwd: spa, stdio: 'inherit', env, shell })
}

console.log(`Publishing server ${version} for ${os}-${arch} → ${out}`)
execFileSync(
  'dotnet',
  [
    'publish', join(repo, 'src', 'ANalyze.Web', 'ANalyze.Web.csproj'),
    '-c', 'Release',
    '-r', `${os}-${arch}`,
    '--self-contained', 'true',
    '-p:DebugType=none',
    '-p:SkipSpaBuild=true',
    '-o', out,
  ],
  { stdio: 'inherit', env, shell },
)
