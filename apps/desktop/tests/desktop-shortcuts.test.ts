import { mkdtempSync, rmdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { EditShortcutSettings, HotkeySettings } from '../src/main/desktop-lifecycle'
import { defaultEditShortcuts, defaultGlobalHotkeys, viewActionSchema } from '../src/shared/desktop-controls'
import { capturedShortcut } from '../src/shared/keyboard'

const registered = vi.hoisted(() => new Map<string, () => void>())
vi.mock('electron', () => ({ globalShortcut: {
  register: (key: string, action: () => void) => {
    if (registered.has(key)) return false
    registered.set(key, action)
    return true
  },
  unregister: (key: string) => registered.delete(key)
} }))

const globalAccelerator = 'Control+Alt+Space'
it('exposes only the view actions still available in general settings', () => {
  for (const action of ['reload', 'forceReload', 'toggleDevTools']) expect(viewActionSchema.safeParse(action).success).toBe(true)
  for (const action of ['zoomIn', 'zoomOut', 'resetZoom', 'toggleFullscreen']) expect(viewActionSchema.safeParse(action).success).toBe(false)
})
let directory: string
let path: string

beforeEach(() => {
  directory = mkdtempSync(join(tmpdir(), 'media-library-shortcuts-'))
  path = join(directory, 'edit-shortcuts.json')
})

afterEach(() => {
  registered.clear()
  rmSync(path, { force: true })
  rmSync(join(directory, 'main-hotkey.txt'), { force: true })
  rmdirSync(directory)
})

describe('desktop editing shortcuts', () => {
  it('uses Windows edit defaults and accepts both standard redo combinations', () => {
    const settings = new EditShortcutSettings(path, globalAccelerator)
    for (const [action, key] of Object.entries(defaultEditShortcuts)) {
      expect(settings.actionFor(key)).toBe(action)
    }
    expect(settings.actionFor('Control+Shift+Z')).toBe('redo')
  })

  it('persists changed keys and suppresses the Chromium fallback for replaced defaults', () => {
    const settings = new EditShortcutSettings(path, globalAccelerator)
    expect(settings.update({ ...defaultEditShortcuts, copy: 'shift + ctrl + c' }, globalAccelerator))
      .toEqual({ result: { ...defaultEditShortcuts, copy: 'Control+Shift+C' } })
    const reopened = new EditShortcutSettings(path, globalAccelerator)
    expect(reopened.actionFor('Control+Shift+C')).toBe('copy')
    expect(reopened.actionFor('Control+C')).toBeUndefined()
    expect(reopened.replacesStandardKey('Control+C')).toBe(true)
    expect(reopened.update(defaultEditShortcuts, globalAccelerator)).toEqual({ result: defaultEditShortcuts })
    expect(new EditShortcutSettings(path, globalAccelerator).actionFor('Control+C')).toBe('copy')
  })

  it('keeps the active keys when a combination is invalid or already in use', () => {
    const settings = new EditShortcutSettings(path, globalAccelerator)
    for (const copy of ['C', 'Shift+C', 'Ctrl+V', 'Ctrl+Shift+Z', 'Ctrl+Alt+Space']) {
      expect(settings.update({ ...defaultEditShortcuts, copy }, globalAccelerator)).toHaveProperty('error')
      expect(settings.state()).toEqual(defaultEditShortcuts)
    }
  })

  it('recovers defaults from an unreadable settings file', () => {
    writeFileSync(path, '{')
    expect(new EditShortcutSettings(path, globalAccelerator).state()).toEqual(defaultEditShortcuts)
  })

  it('checks both global shortcuts when saving editing shortcuts', () => {
    const settings = new EditShortcutSettings(path, ...Object.values(defaultGlobalHotkeys))
    expect(settings.update({ ...defaultEditShortcuts, copy: defaultGlobalHotkeys.main },
      ...Object.values(defaultGlobalHotkeys))).toMatchObject({ error: { code: 'HOTKEY_CONFLICT' } })
  })
})

it('registers the main page action independently and preserves its key on conflict and restart', () => {
  const showSearch = vi.fn()
  const showMain = vi.fn()
  const search = new HotkeySettings(path, showSearch)
  const mainPath = join(directory, 'main-hotkey.txt')
  const conflicts = (key: string) => key === search.state().accelerator
  const main = new HotkeySettings(mainPath, showMain, defaultGlobalHotkeys.main, conflicts)
  registered.get(defaultGlobalHotkeys.main)?.()
  registered.get(defaultGlobalHotkeys.search)?.()
  expect(showMain).toHaveBeenCalledOnce()
  expect(showSearch).toHaveBeenCalledOnce()
  expect(main.update('Ctrl+Alt+F12')).toEqual({ result: { accelerator: 'Control+Alt+F12', registered: true } })
  expect(registered.has(defaultGlobalHotkeys.main)).toBe(false)
  expect(main.update('Ctrl+Alt+Space')).toMatchObject({ error: { code: 'HOTKEY_CONFLICT' } })
  expect(registered.get('Control+Alt+F12')).toBe(showMain)
  registered.clear()
  expect(new HotkeySettings(mainPath, showMain, defaultGlobalHotkeys.main, conflicts).state())
    .toEqual({ accelerator: 'Control+Alt+F12', registered: true })
})

it('temporarily releases global shortcuts while recording and restores them afterwards', () => {
  const show = vi.fn()
  const setting = new HotkeySettings(path, show)
  setting.suspend()
  setting.suspend()
  expect(registered.has(defaultGlobalHotkeys.search)).toBe(false)
  setting.resume()
  setting.resume()
  expect(setting.state().registered).toBe(true)
  expect(registered.get(defaultGlobalHotkeys.search)).toBe(show)
  registered.set('Control+Alt+F12', () => {})
  expect(setting.update('Ctrl+Alt+F12')).toMatchObject({ error: { code: 'HOTKEY_UNAVAILABLE' } })
  expect(setting.state().accelerator).toBe(defaultGlobalHotkeys.search)
  expect(registered.get(defaultGlobalHotkeys.search)).toBe(show)
})

it('captures supported key combinations and rejects incomplete or unsupported input', () => {
  const event = { key: 'c', ctrlKey: true, altKey: false, shiftKey: true, metaKey: false, isComposing: false }
  expect(capturedShortcut(event)).toBe('Ctrl+Shift+C')
  expect(capturedShortcut({ ...event, key: ' ' })).toBe('Ctrl+Shift+Space')
  expect(capturedShortcut({ ...event, key: 'F12' })).toBe('Ctrl+Shift+F12')
  for (const key of ['Control', 'Shift', 'Enter', 'F25', 'AltGraph']) {
    expect(capturedShortcut({ ...event, key })).toBeNull()
  }
  expect(capturedShortcut({ ...event, ctrlKey: false, shiftKey: false })).toBeNull()
  expect(capturedShortcut({ ...event, isComposing: true })).toBeNull()
})
