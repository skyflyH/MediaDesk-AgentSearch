const fs = require('node:fs')
const path = require('node:path')
const { spawnSync } = require('node:child_process')
const { createHash } = require('node:crypto')
const root = path.resolve(__dirname, '..')
const version = require('../package.json').version
const source = path.join(root, 'artifacts/webview-app')
const destination = path.join(root, `dist/MediaDesk-${version}-win-x64`)
const archive = destination + '.zip'
if (path.dirname(destination) !== path.join(root, 'dist')) throw Error('Unexpected package directory')
fs.rmSync(destination, { recursive: true, force: true })
fs.mkdirSync(destination, { recursive: true })
function copy(directory, output) {
  fs.mkdirSync(output, { recursive: true })
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.name.endsWith('.pdb')) continue
    const from = path.join(directory, entry.name), to = path.join(output, entry.name)
    if (entry.isDirectory()) copy(from, to)
    else fs.copyFileSync(from, to)
  }
}
// Explicit allowlist: diagnostic profiles and obsolete build files never ship.
for (const name of ['MediaDesk.exe', 'MediaDesk.dll', 'MediaDesk.deps.json', 'MediaDesk.runtimeconfig.json',
  'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll',
  'node.exe', 'Start-MediaDesk.cmd', 'Start-MediaDesk.ps1'])
  fs.copyFileSync(path.join(source, name), path.join(destination, name))
for (const directory of ['main', 'preload', 'renderer', 'service']) copy(path.join(source, directory), path.join(destination, directory))
fs.writeFileSync(path.join(destination, 'README.txt'), `MediaDesk ${version} / Windows x64
Requires Microsoft .NET 10 Desktop Runtime x64 and Microsoft Edge WebView2 Runtime.
Extract the entire archive and run Start-MediaDesk.cmd.
Data: %LOCALAPPDATA%\\ai-photos-manager
Bundled Node.js license: https://github.com/nodejs/node/blob/v22.18.0/LICENSE
WebView2 SDK license: https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.2535.41/License
No personal database, API keys or photo model weights are included.
`)
const sevenzip = process.env.ELECTRON_BUILDER_7ZIP_PATH || process.env.SEVENZIP_PATH || 'C:/Program Files/7-Zip/7z.exe'
const temporary = archive + '.' + Date.now() + '.tmp'
for (const args of [['a', '-tzip', '-mx=7', temporary, '.'], ['t', temporary]]) {
  const result = spawnSync(sevenzip, args, { cwd: destination, stdio: 'inherit', windowsHide: true })
  if (result.error || result.status !== 0) throw result.error || Error('Archive creation or validation failed')
}
fs.renameSync(temporary, archive)
const checksum = createHash('sha256').update(fs.readFileSync(archive)).digest('hex')
fs.writeFileSync(archive + '.sha256', `${checksum}  ${path.basename(archive)}\n`)
console.log('WebView2 package:', archive)
