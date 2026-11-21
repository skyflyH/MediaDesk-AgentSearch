import { expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { runInNewContext } from 'node:vm'
import ts from 'typescript'

it('initializes before the DOM exists and preserves optional IPC arguments over JSON', () => {
  const sent: unknown[] = []
  const listeners: Record<string, Function> = {}
  const view = { addEventListener() {}, postMessage(value: unknown) { sent.push(JSON.parse(JSON.stringify(value))) } }
  const window = { chrome: { webview: view }, __mediaDeskCaption: true, addEventListener() {} }
  const document = { readyState: 'loading', documentElement: null,
    addEventListener(name: string, listener: Function) { listeners[name] = listener } }
  const exports: Record<string, any> = {}
  const code = ts.transpileModule(readFileSync('apps/webview/renderer-bridge.ts', 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 }
  }).outputText
  runInNewContext(code, { window, document, exports, Map, Set })
  expect(listeners.DOMContentLoaded).toBeTypeOf('function')
  void exports.ipcRenderer.invoke('search:index-directories', ['C:\\fixtures'], undefined)
  exports.ipcRenderer.send('example', false, undefined)
  expect(sent).toEqual([
    { id: 1, channel: 'search:index-directories', args: [['C:\\fixtures']] },
    { channel: 'example', args: [false] }
  ])
})
