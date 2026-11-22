import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { nextTick, ref } from 'vue'
import type { DesktopApi } from '../src/shared/health'
import { defaultEditShortcuts } from '../src/shared/desktop-controls'
import { useShortcutRecording } from '../src/renderer/useShortcutRecording'

const lifecycle = vi.hoisted(() => ({ mounted: [] as Array<() => void>, unmounted: [] as Array<() => void> }))
vi.mock('vue', async (original) => ({
  ...await original<typeof import('vue')>(),
  onMounted: (callback: () => void) => lifecycle.mounted.push(callback),
  onUnmounted: (callback: () => void) => lifecycle.unmounted.push(callback)
}))

beforeEach(() => { vi.stubGlobal('window', new EventTarget()) })
afterEach(() => {
  lifecycle.unmounted.splice(0).forEach(callback => callback())
  lifecycle.mounted.length = 0
  vi.unstubAllGlobals()
})

async function setup() {
  const active = ref(true)
  const api = {
    setShortcutCapture: vi.fn(),
    getHotkey: vi.fn(async () => ({ result: { accelerator: 'Control+K', registered: true } })),
    setHotkey: vi.fn(async (accelerator: string) => ({ result: { accelerator, registered: true } })),
    getEditShortcuts: vi.fn(async () => ({ result: defaultEditShortcuts })),
    setEditShortcuts: vi.fn(async (value) => ({ result: value }))
  }
  const controls = useShortcutRecording(api as unknown as DesktopApi, active)
  lifecycle.mounted.splice(0).forEach(callback => callback())
  await vi.waitFor(() => expect(controls.hotkeys.search.busy).toBe(false))
  return { api, active, controls }
}

function press(key: string) {
  const event = new Event('keydown', { cancelable: true })
  Object.assign(event, { key, code: `Key${key}`, ctrlKey: true, altKey: false, shiftKey: false, metaKey: false })
  window.dispatchEvent(event)
  return event
}

it('captures copy and paste combinations instead of executing clipboard edits while recording', async () => {
  const { controls } = await setup()
  controls.startRecording({ kind: 'edit', target: 'copy' })
  expect(press('C').defaultPrevented).toBe(true)
  expect(controls.editKeys.value?.copy).toBe('Ctrl+C')
  controls.startRecording({ kind: 'edit', target: 'paste' })
  expect(press('V').defaultPrevented).toBe(true)
  expect(controls.editKeys.value?.paste).toBe('Ctrl+V')
})

it('keeps one recording target and restores unfinished input when leaving the category', async () => {
  const { api, active, controls } = await setup()
  controls.startRecording({ kind: 'global', target: 'search' })
  expect(press('J').defaultPrevented).toBe(true)
  expect(controls.hotkeys.search.text).toBe('Ctrl+J')
  controls.startRecording({ kind: 'global', target: 'main' })
  expect(controls.hotkeys.search.text).toBe('Ctrl+K')
  press('L')
  active.value = false
  await nextTick()
  expect(controls.hotkeys.main.text).toBe('Ctrl+K')
  expect(controls.recording.value).toBeUndefined()
  expect(api.setShortcutCapture).toHaveBeenLastCalledWith(false)
})

it('saves the recorded global shortcut and releases capture before saving', async () => {
  const { api, controls } = await setup()
  controls.startRecording({ kind: 'global', target: 'search' })
  press('J')
  await controls.saveHotkey('search')
  expect(api.setHotkey).toHaveBeenCalledWith('Ctrl+J', 'search')
  expect(api.setShortcutCapture).toHaveBeenLastCalledWith(false)
  expect(controls.hotkeys.search.current?.accelerator).toBe('Ctrl+J')
})

it('releases capture and removes keyboard listeners on unmount', async () => {
  const { api, controls } = await setup()
  controls.startRecording({ kind: 'edit', target: 'copy' })
  press('J')
  lifecycle.unmounted.splice(0).forEach(callback => callback())
  expect(api.setShortcutCapture).toHaveBeenLastCalledWith(false)
  expect(controls.editKeys.value?.copy).toBe(defaultEditShortcuts.copy.replace('Control', 'Ctrl'))
  expect(press('L').defaultPrevented).toBe(false)
})

it('saves edit shortcuts together and restores all defaults after recording', async () => {
  const { api, controls } = await setup()
  controls.startRecording({ kind: 'edit', target: 'copy' })
  press('J')
  await controls.saveEditShortcuts(controls.editKeys.value!)
  expect(api.setEditShortcuts).toHaveBeenCalledWith(expect.objectContaining({ copy: 'Ctrl+J', cut: expect.any(String) }))
  await controls.saveEditShortcuts(defaultEditShortcuts)
  expect(api.setEditShortcuts).toHaveBeenLastCalledWith(defaultEditShortcuts)
  expect(controls.editKeys.value?.copy).toBe(defaultEditShortcuts.copy.replace('Control', 'Ctrl'))
})
