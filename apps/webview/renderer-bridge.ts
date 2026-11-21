const view = (window as any).chrome.webview
const pending = new Map<number, { resolve: (value: any) => void; reject: (error: Error) => void }>()
const listeners = new Map<string, Set<(...args: any[]) => void>>()
let sequence = 0
function wireArgs(args: any[]) {
  // JSON encodes undefined array entries as null, which fails optional IPC schemas.
  while (args.length && args[args.length - 1] === undefined) args.pop()
  return args
}
view.addEventListener('message', ({ data }: any) => {
  if (data.captionState) {
    document.documentElement.toggleAttribute('data-window-maximized', data.maximized)
    document.documentElement.toggleAttribute('data-window-fullscreen', data.fullscreen)
    const maximize = document.querySelector<HTMLButtonElement>('[data-caption="maximize"]')
    if (maximize) { maximize.title = maximize.ariaLabel = data.maximized ? '还原' : '最大化'; maximize.setAttribute('aria-pressed', String(data.maximized)) }
  } else if (data.reply !== undefined) {
    const request = pending.get(data.reply)
    if (!request) return
    pending.delete(data.reply)
    if (data.error) request.reject(new Error(data.error))
    else request.resolve(data.result)
  } else if (data.channel) {
    if (data.channel === 'asset:dropped') window.dispatchEvent(new CustomEvent('apm-native-drop', { detail: data.args[0] }))
    for (const listener of listeners.get(data.channel) ?? []) listener({}, ...(data.args ?? []))
  }
})
export const contextBridge = { exposeInMainWorld: (name: string, value: any) => { (window as any)[name] = value } }
export const ipcRenderer = {
  invoke(channel: string, ...args: any[]) {
    const id = ++sequence
    return new Promise<any>((resolve, reject) => {
      pending.set(id, { resolve, reject })
      view.postMessage({ id, channel, args: wireArgs(args) })
    })
  },
  send(channel: string, ...args: any[]) { view.postMessage({ channel, args: wireArgs(args) }) },
  on(channel: string, listener: (...args: any[]) => void) {
    if (!listeners.has(channel)) listeners.set(channel, new Set())
    listeners.get(channel)!.add(listener)
  },
  removeListener(channel: string, listener: (...args: any[]) => void) { listeners.get(channel)?.delete(listener) }
}
export const webUtils = { getPathForFile: (_file: File) => '' }

// Files remain native objects, not paths supplied by untrusted page scripts.
window.addEventListener('drop', event => {
  const zone = event.target instanceof Element && event.target.closest('[data-import-drop]')
  if (!zone || !event.dataTransfer?.files.length) return
  event.preventDefault()
  view.postMessageWithAdditionalObjects({ nativeDrop: true }, [...event.dataTransfer.files])
})
window.addEventListener('dragover', event => { if (event.dataTransfer?.types.includes('Files')) event.preventDefault() })
let editKeys: Record<string, string> = {}
const invoke = ipcRenderer.invoke
ipcRenderer.invoke = (channel, ...args) => invoke(channel, ...args).then(reply => {
  if ((channel === 'settings:get-edit-shortcuts' || channel === 'settings:set-edit-shortcuts') && reply?.result) editKeys = reply.result
  return reply
})
window.addEventListener('keydown', event => {
  if (event.defaultPrevented || document.querySelector('[data-recording="true"]')) return
  const key = event.key === ' ' ? 'Space' : event.key
  const accelerator = [event.ctrlKey && 'Control', event.altKey && 'Alt', event.shiftKey && 'Shift', event.metaKey && 'Super', key.length === 1 ? key.toUpperCase() : key].filter(Boolean).join('+')
  if (accelerator !== 'Control+A' && (Object.values(editKeys).includes(accelerator) || ['Control+X','Control+C','Control+V','Control+Shift+V','Control+Z','Control+Y','Control+Shift+Z'].includes(accelerator))) event.preventDefault()
  view.postMessage({ input: { type: 'keyDown', key, control: event.ctrlKey, alt: event.altKey,
    shift: event.shiftKey, meta: event.metaKey, isComposing: event.isComposing, isAutoRepeat: event.repeat } })
})
// WebView2 has no Electron titleBarOverlay. Keep the controls in the existing
// page header and forward only a fixed set of window operations to the host.
const caption = (window as any).__mediaDeskCaption === true
if (caption) {
  const install = () => {
    document.documentElement.setAttribute('data-webview-caption', '')
    const style = document.createElement('link')
    style.rel = 'stylesheet'; style.href = './webview-caption.css'
    document.head.append(style)
    const controls = document.createElement('div')
    controls.className = 'webview-caption'
    controls.setAttribute('role', 'group')
    controls.setAttribute('aria-label', '窗口控制')
    for (const [action, label, icon] of [
      ['minimize', '最小化', '<path d="M1 6h10"/>'],
      ['maximize', '最大化', '<path class="maximize-icon" d="M1.5 1.5h9v9h-9z"/><path class="restore-icon" d="M3.5 3.5v-2h7v7h-2m-7-5h7v7h-7z"/>'],
      ['close', '关闭', '<path d="m1 1 10 10M11 1 1 11"/>']
    ] as const) {
      const button = document.createElement('button')
      button.type = 'button'; button.dataset.caption = action; button.title = button.ariaLabel = label
      button.innerHTML = `<svg viewBox="0 0 12 12" aria-hidden="true">${icon}</svg>`
      button.addEventListener('click', () => view.postMessage({ nativeCaption: action }))
      controls.append(button)
    }
    document.body.append(controls)
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install, { once: true })
  else install()
}
function resizeEdge(event: PointerEvent): number {
  if (!caption || document.documentElement.hasAttribute('data-window-maximized') || document.documentElement.hasAttribute('data-window-fullscreen')) return 0
  const left = event.clientX < 5, right = event.clientX >= innerWidth - 5
  const top = event.clientY < 5, bottom = event.clientY >= innerHeight - 5
  return top ? left ? 13 : right ? 14 : 12 : bottom ? left ? 16 : right ? 17 : 15 : left ? 10 : right ? 11 : 0
}
window.addEventListener('pointermove', event => {
  const edge = resizeEdge(event)
  document.documentElement.style.cursor = ({ 10: 'ew-resize', 11: 'ew-resize', 12: 'ns-resize', 15: 'ns-resize', 13: 'nwse-resize', 17: 'nwse-resize', 14: 'nesw-resize', 16: 'nesw-resize' } as Record<number, string>)[edge] ?? ''
})
let lastCaptionDown = { time: 0, x: 0, y: 0 }
window.addEventListener('pointerdown', event => {
  if (event.button !== 0) return
  const edge = resizeEdge(event)
  if (edge) { event.preventDefault(); event.stopImmediatePropagation(); view.postMessage({ nativeResize: edge }); return }
  if (event.target instanceof Element && event.target.closest('.app-titlebar') &&
      !event.target.closest('button,input,textarea,select,a,[contenteditable],.workspace-model-status-text')) {
    // The native move loop consumes mouseup, so DOM dblclick is not reliable.
    const double = performance.now() - lastCaptionDown.time < 500 &&
      Math.abs(event.screenX - lastCaptionDown.x) < 5 && Math.abs(event.screenY - lastCaptionDown.y) < 5
    lastCaptionDown = { time: double ? 0 : performance.now(), x: event.screenX, y: event.screenY }
    view.postMessage(double && caption ? { nativeCaption: 'maximize' } : { nativeDrag: true })
  }
}, true)
