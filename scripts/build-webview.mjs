import { build } from 'rolldown'
import fs from 'node:fs'
import path from 'node:path'

const root = process.cwd()
const output = path.resolve('artifacts/webview-app')
fs.mkdirSync(output, { recursive: true })
// These generated subdirectories are owned by this build. Avoid shipping stale
// renderer chunks or service files left by an earlier packaging workflow.
if (path.dirname(output) !== path.join(root, 'artifacts')) throw Error('Unexpected build directory')
for (const folder of ['main', 'preload', 'renderer', 'service'])
  fs.rmSync(path.join(output, folder), { recursive: true, force: true })
for (const folder of ['main', 'preload']) fs.mkdirSync(path.join(output, folder), { recursive: true })
const shim = path.resolve('apps/webview/electron-host.ts')
const browser = path.resolve('apps/webview/renderer-bridge.ts')
const smoke = path.resolve('apps/desktop/src/main/desktop-smoke.ts')
const plugin = {
  name: 'webview-host-adapter',
  resolveId(source, importer) {
    if (source === 'electron') return importer?.includes('preload') || importer?.endsWith('renderer-bridge.ts') ? browser : shim
    if (source.endsWith('desktop-smoke')) return '\0smoke-stub'
    if (source.endsWith('tray.png?asset') || source.endsWith('app-icon.png?asset')) return '\0tray-icon'
  },
  load(id) {
    if (id === '\0smoke-stub') return [...fs.readFileSync(smoke, 'utf8').matchAll(/export async function (\w+)/g)].map(m => `export async function ${m[1]}(){}`).join('\n')
    if (id === '\0tray-icon') return 'export default ""'
  }
}
plugin.transform = (code, id) => id.replaceAll('\\', '/').endsWith('/main/service-client.ts')
  ? code.replace('const child = spawn(command, args, {', `const useDotnet = process.env.APM_WEBVIEW_TEST_DOTNET && command.endsWith('MediaLibrary.Service.exe');
    const child = spawn(useDotnet ? process.env.APM_WEBVIEW_TEST_DOTNET! : command,
      useDotnet ? [command.replace(/\\.exe$/i, '.dll'), ...args] : args, {`) : null
await build({ input: 'apps/desktop/src/main/index.ts', platform: 'node', plugins: [plugin],
  external: [/^node:/], resolve: { alias: { electron: shim } },
  output: { file: path.join(output, 'main/index.cjs'), format: 'cjs' } })
await build({ input: 'apps/desktop/src/preload/index.ts', platform: 'browser', plugins: [{ ...plugin,
  resolveId: source => source === 'electron' ? browser : null,
  transform: (code, id) => id.replaceAll('\\', '/').endsWith('/preload/index.ts') ? code.replace('process.argv', '[]')
    .replace('onAssetsDropped: (callback) => {', `onAssetsDropped: (callback) => {
      const nativeDrop = (event: Event) => callback((event as CustomEvent).detail);
      window.addEventListener('apm-native-drop', nativeDrop);`)
    .replace("window.removeEventListener('drop', drop)", "window.removeEventListener('drop', drop); window.removeEventListener('apm-native-drop', nativeDrop)") : null }],
  output: { file: path.join(output, 'preload/index.js'), format: 'iife' } })
function copyTree(source, destination) {
  fs.mkdirSync(destination, { recursive: true })
  for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
    const from = path.join(source, entry.name), to = path.join(destination, entry.name)
    if (entry.isDirectory()) copyTree(from, to)
    else fs.copyFileSync(from, to)
  }
}
copyTree('out/renderer', path.join(output, 'renderer'))
copyTree('artifacts/service-release-app', path.join(output, 'service'))
for (const name of ['Start-MediaDesk.cmd', 'Start-MediaDesk.ps1'])
  fs.copyFileSync(path.join('apps/webview', name), path.join(output, name))
fs.copyFileSync('apps/webview/webview-caption.css', path.join(output, 'renderer/webview-caption.css'))
const html = path.join(output, 'renderer/index.html')
fs.writeFileSync(html, fs.readFileSync(html, 'utf8').replace('href="/mediadesk.svg"', 'href="./mediadesk.svg"'))
fs.copyFileSync(process.execPath, path.join(output, 'node.exe'))
console.log('Built WebView2 application bridge:', output)
