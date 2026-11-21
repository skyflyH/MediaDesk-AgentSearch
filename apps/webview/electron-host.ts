import { EventEmitter } from 'node:events'
import { createInterface } from 'node:readline'
import { createServer } from 'node:http'
import { pipePreviewResponse } from './preview-response'
import { readFileSync, writeFileSync, existsSync, unlinkSync, mkdirSync } from 'node:fs'
import { join, resolve, extname, sep } from 'node:path'
import { randomUUID } from 'node:crypto'

const root = resolve(process.env.APM_WEBVIEW_ROOT!)
const userData = process.env.APM_WEBVIEW_PROFILE!
const syncRoot = process.env.APM_WEBVIEW_SYNC!
mkdirSync(syncRoot, { recursive: true })
const responses = new Map<string, { resolve: (value: any) => void; reject: (error: Error) => void }>()
function send(value: any) { process.stdout.write(JSON.stringify(value) + '\n') }
function native(method: string, args: any[] = []): Promise<any> {
  const id = randomUUID()
  return new Promise((resolve, reject) => { responses.set(id, { resolve, reject }); send({ native: method, args, id }) })
}
function nativeSync(method: string, args: any[] = []): any {
  const syncId = randomUUID(), file = join(syncRoot, syncId + '.json')
  send({ native: method, args, syncId })
  const timeout = Date.now() + 30000, signal = new Int32Array(new SharedArrayBuffer(4))
  while (!existsSync(file)) { if (Date.now() > timeout) throw Error('Native host timeout: ' + method); Atomics.wait(signal, 0, 0, 5) }
  const reply = JSON.parse(readFileSync(file, 'utf8')); unlinkSync(file)
  if (reply.error) throw Error(reply.error)
  return reply.result
}
const handles = new Map<string, Function>()
export const ipcMain = Object.assign(new EventEmitter(), { handle: (channel: string, handler: Function) => handles.set(channel, handler) })
const windows = new Map<number, BrowserWindow>()
const shortcuts = new Map<string, () => void>()
let nextWindow = 0
let protocolHandler: ((request: Request) => Promise<Response>) | undefined
const capability = randomUUID()
let origin = ''
const serverReady = (async () => {
  const server = createServer(async (request, reply) => {
    try {
      const url = new URL(request.url!, 'http://localhost')
      if (!url.pathname.startsWith('/' + capability + '/')) { reply.writeHead(404).end(); return }
      const name = decodeURIComponent(url.pathname.slice(capability.length + 2))
      if (name.startsWith('__media/')) {
        if (!protocolHandler) { reply.writeHead(404).end(); return }
        const headers = new Headers()
        if (request.headers.range) headers.set('range', request.headers.range)
        const response = await protocolHandler(new Request('media-preview://asset/' + name.slice(8), { method: request.method, headers }))
        await pipePreviewResponse(response, reply)
        return
      }
      const file = resolve(root, 'renderer', name || 'index.html')
      if (!file.startsWith(join(root, 'renderer') + sep)) { reply.writeHead(403).end(); return }
      const types: Record<string, string> = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.jpg': 'image/jpeg', '.mp4': 'video/mp4' }
      reply.writeHead(200, { 'Content-Type': types[extname(file)] ?? 'application/octet-stream', 'X-Content-Type-Options': 'nosniff' })
      reply.end(readFileSync(file))
    } catch { if (!reply.headersSent) reply.writeHead(404); reply.end() }
  })
  const portFile = join(userData, 'webview-port.txt')
  let port = 38761
  try { port = Number(readFileSync(portFile, 'utf8')) || port } catch {}
  for (let attempt = 0; attempt < 100; attempt++, port++) {
    try { await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(port, '127.0.0.1', () => { server.removeAllListeners('error'); resolve() }) }); break }
    catch { if (attempt === 99) throw Error('Cannot open local application endpoint') }
  }
  writeFileSync(portFile, String(port))
  origin = `http://127.0.0.1:${port}/${capability}/`
})()
function mappedUrl(url: string) { return origin + 'index.html' + (url.includes('#') ? '#' + url.split('#')[1] : '') }
function translate(value: any): any {
  if (typeof value === 'string' && value.startsWith('media-preview://asset/')) return origin + '__media/' + value.slice('media-preview://asset/'.length)
  if (Array.isArray(value)) return value.map(translate)
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, translate(v)]))
  return value
}
class WebContents extends EventEmitter {
  readonly mainFrame = { url: '' }
  constructor(readonly id: number) { super() }
  send(channel: string, ...args: any[]) { void native('event', [this.id, channel, translate(args)]) }
  executeJavaScript(script: string) { return native('script', [this.id, script]) }
  isDestroyed() { return !windows.has(this.id) }
  isCrashed() { return false }
  isLoadingMainFrame() { return false }
  setWindowOpenHandler(_handler: any) {}
  reload() { void native('reload', [this.id, false]) }
  reloadIgnoringCache() { void native('reload', [this.id, true]) }
  toggleDevTools() { void native('devtools', [this.id]) }
  getURL() { return this.mainFrame.url }
  cut() { this.command('cut') } copy() { this.command('copy') } paste() { this.command('paste') }
  pasteAndMatchStyle() { void native('pastePlain', [this.id]) }
  selectAll() { this.command('selectAll') } undo() { this.command('undo') } redo() { this.command('redo') }
  private command(command: string) { void this.executeJavaScript(`document.execCommand(${JSON.stringify(command)})`) }
}
export class BrowserWindow extends EventEmitter {
  readonly id = ++nextWindow
  readonly webContents = new WebContents(this.id)
  private bounds: any
  private visible: boolean
  private focused = false
  private full = false
  constructor(options: any) {
    super(); windows.set(this.id, this); this.visible = options.show !== false
    this.bounds = { x: options.x ?? 0, y: options.y ?? 0, width: options.width, height: options.height }
    void native('createWindow', [this.id, options]).catch(console.error)
  }
  async loadURL(url: string) { this.webContents.mainFrame.url = url; await serverReady; await native('load', [this.id, mappedUrl(url)]) }
  isDestroyed() { return !windows.has(this.id) }
  isFocused() { return this.focused }
  isVisible() { return this.visible }
  show() { this.visible = true; void native('show', [this.id]) }
  hide() { this.visible = false; this.emit('hide'); void native('hide', [this.id]) }
  focus() { this.focused = true; void native('focus', [this.id]) }
  destroy() { windows.delete(this.id); void native('destroy', [this.id]); this.emit('closed') }
  isMinimized() { return nativeSync('minimized', [this.id]) }
  restore() { void native('restore', [this.id]) }
  setTitleBarOverlay(options: any) { void native('theme', [this.id, options.symbolColor === '#3b3b3b' ? 'light' : 'dark']) }
  setBackgroundColor(_color: string) {}
  getBounds() { return this.bounds }
  setBounds(bounds: any) { this.bounds = bounds; void native('bounds', [this.id, bounds]) }
  isFullScreen() { return this.full }
  setFullScreen(value: boolean) { this.full = value; void native('fullscreen', [this.id, value]) }
  static fromWebContents(contents: WebContents) { return windows.get(contents.id) }
  _focus(value: boolean) { this.focused = value; this.emit(value ? 'focus' : 'blur') }
}
export const app = Object.assign(new EventEmitter(), {
  isPackaged: true,
  getAppPath: () => root,
  getPath: (name: string) => name === 'userData' ? userData : process.env.LOCALAPPDATA!,
  setPath: (_name: string, _path: string) => {},
  requestSingleInstanceLock: () => true,
  whenReady: async () => { await serverReady },
  getLoginItemSettings: (_options: any) => nativeSync('getLoginStartup'),
  setLoginItemSettings: (options: any) => nativeSync('setLoginStartup', [options.openAtLogin]),
  quit: () => { const event = { preventDefault() {} }; app.emit('before-quit', event) },
  exit: (code: number) => { void native('exit', [code]).finally(() => process.exit(code)) }
})
;(process as any).resourcesPath = root
export const globalShortcut = {
  register(key: string, callback: () => void) { const registered = nativeSync('registerHotkey', [key]); if (registered) shortcuts.set(key, callback); return registered },
  unregister(key: string) { nativeSync('unregisterHotkey', [key]); shortcuts.delete(key) },
  unregisterAll() { for (const key of [...shortcuts.keys()]) globalShortcut.unregister(key) }
}
export const safeStorage = {
  isAsyncEncryptionAvailable: async () => true,
  encryptStringAsync: async (text: string) => Buffer.from(await native('encrypt', [text]), 'base64'),
  decryptStringAsync: async (bytes: Buffer) => ({ result: await native('decrypt', [bytes.toString('base64')]), shouldReEncrypt: false })
}
export const dialog = { showOpenDialog: (owner: BrowserWindow, options: any) => native('dialog', [owner.id, options]) }
export const shell = {
  openPath: (path: string) => native('openPath', [path]),
  showItemInFolder: (path: string) => native('reveal', [path])
}
export const protocol = { registerSchemesAsPrivileged: (_schemes: any) => {}, handle: (_scheme: string, handler: any) => { protocolHandler = handler } }
export const Menu = { setApplicationMenu: (_value: any) => {}, buildFromTemplate: (template: any[]) => template }
export const nativeImage = { createFromPath: (path: string) => path }
export class Tray extends EventEmitter {
  constructor(_icon: any) { super(); void native('tray') }
  setToolTip(_text: string) {}
  setContextMenu(template: any[]) { trayTemplate = template }
  destroy() { void native('destroyTray') }
}
let trayTemplate: any[] = []
export const screen = {
  getCursorScreenPoint: () => nativeSync('cursor'),
  getDisplayNearestPoint: (point: any) => nativeSync('display', [point]),
  getDisplayMatching: (bounds: any) => nativeSync('display', [bounds])
}
export const net = { fetch }

createInterface({ input: process.stdin }).on('line', async line => {
  try {
    const message = JSON.parse(line)
    if (message.reply) { const item = responses.get(message.reply); responses.delete(message.reply); if (message.error) item?.reject(new Error(message.error)); else item?.resolve(message.result); return }
    if (message.hotkey) { shortcuts.get(message.hotkey)?.(); return }
    if (message.tray !== undefined) { trayTemplate[message.tray]?.click?.(); return }
    if (message.secondInstance) { app.emit('second-instance'); return }
    if (message.quit) { app.quit(); return }
    const window = windows.get(message.window)
    if (!window) return
    if (message.focus !== undefined) { window._focus(message.focus); return }
    if (message.close) { window.emit('close', { preventDefault() {} }); return }
    if (message.input) { window.webContents.emit('before-input-event', { preventDefault() {} }, message.input); return }
    if (message.drop) { window.webContents.send('asset:dropped', message.drop); return }
    const sender = window.webContents, event = { sender, senderFrame: sender.mainFrame }
    if (message.channel) {
      if (message.id !== undefined) {
        let result
        try { result = translate(await handles.get(message.channel)?.(event, ...(message.args ?? []))) }
        catch { result = { error: { code: 'HOST_FAILED', message: '系统操作失败，请重试。', retryable: true } } }
        void native('reply', [window.id, { reply: message.id, result }])
      } else ipcMain.emit(message.channel, event, ...(message.args ?? []))
    }
  } catch (error) { console.error(error) }
}).on('close', () => process.exit(0))
