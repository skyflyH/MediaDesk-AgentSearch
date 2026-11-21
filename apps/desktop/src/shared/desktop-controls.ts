import { z } from 'zod'

export type ColorTheme = 'dark' | 'light'

export const globalHotkeyTarget = z.enum(['search', 'main'])
export type GlobalHotkeyTarget = z.infer<typeof globalHotkeyTarget>
export const defaultGlobalHotkeys = { search: 'Control+Alt+Space', main: 'Control+Alt+M' }

export const defaultEditShortcuts = {
  cut: 'Control+X',
  copy: 'Control+C',
  paste: 'Control+V',
  pasteAndMatchStyle: 'Control+Shift+V',
  selectAll: 'Control+A',
  undo: 'Control+Z',
  redo: 'Control+Y'
}

export type EditAction = keyof typeof defaultEditShortcuts
export const editActions = Object.keys(defaultEditShortcuts) as EditAction[]
export const editShortcutsSchema = z.record(z.enum(editActions), z.string().max(80))
export type EditShortcuts = z.infer<typeof editShortcutsSchema>

export const viewActionSchema = z.enum([
  'reload', 'forceReload', 'toggleDevTools'
])
export type ViewAction = z.infer<typeof viewActionSchema>
