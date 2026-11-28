import { EventEmitter } from 'node:events'
import { createRequire } from 'node:module'
import { runInNewContext } from 'node:vm'
import { build } from 'rolldown'
import { expect, it, vi } from 'vitest'

const nodeRequire = createRequire(import.meta.url)

async function launch(args: string[]) {
  const windows: any[] = []
  let trayMenu: any[] = []
  class BrowserWindow extends EventEmitter {
    visible: boolean
    webContents = Object.assign(new EventEmitter(), { setWindowOpenHandler() {}, send: vi.fn() })
    constructor(readonly options: any) {
      super(); this.visible = options.show; windows.push(this)
    }
    loadURL = vi.fn(async () => {})
    isDestroyed = () => false
    isFocused = () => false
    isMinimized = () => false
    show = vi.fn(() => { this.visible = true })
    hide = vi.fn(() => { this.visible = false })
    focus = vi.fn()
  }
  const app = Object.assign(new EventEmitter(), {
    isPackaged: true, getPath: () => '/unused-profile',
    requestSingleInstanceLock: () => true, whenReady: async () => {}
  })
  const electron = {
    app, BrowserWindow, globalShortcut: { register: () => true },
    ipcMain: { handle() {}, on() {} },
    protocol: { registerSchemesAsPrivileged() {}, handle() {} },
    Menu: { setApplicationMenu() {}, buildFromTemplate: (items: any[]) => items },
    nativeImage: { createFromPath: () => null },
    Tray: class extends EventEmitter {
      setToolTip() {}
      setContextMenu(items: any[]) { trayMenu = items }
    }
  }
  const output = await build({
    input: 'apps/desktop/src/main/index.ts', platform: 'node',
    external: [/^node:/, 'electron'],
    plugins: [{ name: 'test-assets',
      resolveId: id => id.endsWith('?asset') ? '\0asset' : null,
      load: id => id === '\0asset' ? 'export default ""' : null
    }],
    output: { format: 'cjs' }
  })
  const entry = output.output.find(item => item.type === 'chunk' && item.isEntry)
  if (!entry || entry.type !== 'chunk') throw Error('Missing main entry')
  const exports = {}
  runInNewContext(entry.code, {
    exports, module: { exports }, __dirname: '/app/main', URL, Buffer, console,
    Response, Request, Headers, fetch, AbortController, AbortSignal,
    TextEncoder, TextDecoder, ReadableStream, TransformStream, setTimeout, clearTimeout,
    process: { argv: args, env: {}, resourcesPath: '/app' },
    require: (name: string) => name === 'electron' ? electron : nodeRequire(name)
  })
  await vi.waitFor(() => expect(windows).toHaveLength(1))
  return { app, main: windows[0], trayMenu }
}

it('login launch stays hidden and opens through the tray or a manual second launch', async () => {
  const { app, main, trayMenu } = await launch(['MediaDesk.exe', '--login-startup'])
  expect(main.options.show).toBe(false)
  expect(main.visible).toBe(false)
  expect(main.show).not.toHaveBeenCalled()
  expect(main.loadURL).toHaveBeenCalledOnce()
  expect(trayMenu.map(item => item.label)).toEqual(['打开', '快捷搜索', '退出'])
  app.emit('second-instance', {}, ['MediaDesk.exe', '--login-startup'])
  expect(main.visible).toBe(false)
  trayMenu[0].click()
  expect(main.visible).toBe(true)
  expect(main.focus).toHaveBeenCalledOnce()
  main.hide()
  app.emit('second-instance', {}, ['MediaDesk.exe'])
  expect(main.visible).toBe(true)
})

it('manual launch still shows the main window', async () => {
  const { main } = await launch(['MediaDesk.exe'])
  expect(main.options.show).toBe(true)
  expect(main.visible).toBe(true)
})
