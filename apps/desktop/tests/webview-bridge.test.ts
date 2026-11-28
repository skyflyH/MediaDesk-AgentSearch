import { expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { runInNewContext } from 'node:vm'
import ts from 'typescript'
import { defaultEditShortcuts } from '../src/shared/desktop-controls'

it('initializes before the DOM exists and preserves optional IPC arguments over JSON', () => {
  const sent: unknown[] = []
  const listeners: Record<string, Function> = {}
  const view = { addEventListener() {}, postMessage(value: unknown) { sent.push(JSON.parse(JSON.stringify(value))) } }
  const window = { chrome: { webview: view }, __mediaDeskCaption: true, devicePixelRatio: 1.5, addEventListener() {} }
  const document = { readyState: 'loading', documentElement: null,
    addEventListener(name: string, listener: Function) { listeners[name] = listener } }
  const exports: Record<string, any> = {}
  const code = ts.transpileModule(readFileSync('apps/webview/renderer-bridge.ts', 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 }
  }).outputText
  runInNewContext(code, { window, document, exports, Map, Set, require: () => ({ defaultEditShortcuts }) })
  expect(listeners.DOMContentLoaded).toBeTypeOf('function')
  void exports.ipcRenderer.invoke('search:index-directories', ['C:\\fixtures'], undefined)
  exports.ipcRenderer.send('example', false, undefined)
  exports.ipcRenderer.send('search:quick-window-size', 151, true)
  expect(sent).toEqual([
    { id: 1, channel: 'search:index-directories', args: [['C:\\fixtures']] },
    { channel: 'example', args: [false] },
    { channel: 'search:quick-window-size', args: [227, true] }
  ])
})

it('leaves standard clipboard shortcuts native in both main and quick search windows', () => {
  const listeners: Record<string, Function> = {}
  const sent: any[] = []
  const window = { chrome: { webview: { addEventListener() {}, postMessage(value: unknown) { sent.push(value) } } },
    addEventListener(name: string, handler: Function) { listeners[name] = handler } }
  const document = { querySelector: () => null }
  const code = ts.transpileModule(readFileSync('apps/webview/renderer-bridge.ts', 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 }
  }).outputText
  runInNewContext(code, { window, document, exports: {}, Map, Set, require: () => ({ defaultEditShortcuts }) })
  for (const key of ['c', 'v', 'x']) {
    let prevented = false
    listeners.keydown!({ key, ctrlKey: true, altKey: false, shiftKey: false, metaKey: false,
      preventDefault() { prevented = true } })
    expect(prevented).toBe(false)
  }
  expect(sent).toEqual([])
})
