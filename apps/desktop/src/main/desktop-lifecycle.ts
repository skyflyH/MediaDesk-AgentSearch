import { readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { randomUUID } from 'node:crypto'
import { globalShortcut } from 'electron'
import type { Result } from '../shared/health'
import type { Hotkey } from '../shared/workspace'
import { defaultEditShortcuts, defaultGlobalHotkeys, editActions, editShortcutsSchema, type EditAction, type EditShortcuts } from '../shared/desktop-controls'

const modifiers = ['Control', 'Alt', 'Shift', 'Super']

export function normalizeHotkey(input: string): string | null {
  const tokens = input.split('+').map((token) => token.trim())
  const key = tokens.pop()?.toUpperCase()
  const requested = tokens.map((token) =>
    token.toLowerCase() === 'ctrl' ? 'Control' : modifiers.find((item) => item.toLowerCase() === token.toLowerCase()))
  if (!key || !/^(?:[A-Z0-9]|F(?:[1-9]|1[0-9]|2[0-4])|SPACE)$/.test(key) ||
      !requested.length || requested.includes(undefined) || new Set(requested).size !== requested.length) return null
  return [...modifiers.filter((modifier) => requested.includes(modifier)), key === 'SPACE' ? 'Space' : key].join('+')
}

function writeSetting(path: string, value: string): void {
  const temp = `${path}.${randomUUID()}.tmp`
  try {
    writeFileSync(temp, value, { flag: 'wx' })
    renameSync(temp, path)
  } finally {
    try { rmSync(temp, { force: true }) } catch { /* Keep the original write error. */ }
  }
}

function normalizeEditShortcuts(input: EditShortcuts): EditShortcuts | null {
  const values = { ...input }
  for (const action of editActions) {
    const value = normalizeHotkey(input[action])
    if (!value?.startsWith('Control+') || value.includes('+Super+')) return null
    values[action] = value
  }
  return values
}

const redoAlternative = 'Control+Shift+Z'
const standardEditKeys = new Set([...Object.values(defaultEditShortcuts), redoAlternative])

export class EditShortcutSettings {
  private shortcuts = { ...defaultEditShortcuts }

  constructor(private readonly path: string, ...globalAccelerators: string[]) {
    try {
      const saved = editShortcutsSchema.parse(JSON.parse(readFileSync(path, 'utf8')))
      const values = normalizeEditShortcuts(saved)
      if (values && this.valid(values, globalAccelerators)) this.shortcuts = values
    } catch { /* Use defaults for a new profile or invalid settings. */ }
  }

  state(): EditShortcuts {
    return this.shortcuts
  }

  uses(accelerator: string): boolean {
    return Object.values(this.shortcuts).includes(accelerator) ||
      (this.shortcuts.redo === defaultEditShortcuts.redo && accelerator === redoAlternative)
  }

  actionFor(accelerator: string): EditAction | undefined {
    return editActions.find((action) => this.shortcuts[action] === accelerator) ??
      (this.shortcuts.redo === defaultEditShortcuts.redo && accelerator === redoAlternative ? 'redo' : undefined)
  }

  replacesStandardKey(accelerator: string): boolean {
    return standardEditKeys.has(accelerator)
  }

  update(input: EditShortcuts, ...globalAccelerators: string[]): Result<EditShortcuts> {
    const next = normalizeEditShortcuts(input)
    if (!next) return { error: {
      code: 'INVALID_ARGUMENT', message: '请使用含 Ctrl 的组合，例如 Ctrl+Shift+C。', retryable: false
    } }
    if (!this.valid(next, globalAccelerators)) return { error: {
      code: 'HOTKEY_CONFLICT', message: '快捷键重复或与全局快捷键冲突。', retryable: false
    } }
    try {
      writeSetting(this.path, JSON.stringify(next))
    } catch {
      return { error: { code: 'SETTINGS_WRITE_FAILED', message: '无法保存编辑快捷键。', retryable: true } }
    }
    this.shortcuts = next
    return { result: next }
  }

  private valid(values: EditShortcuts, globalAccelerators: string[]): boolean {
    const accelerators = Object.values(values)
    if (values.redo === defaultEditShortcuts.redo) accelerators.push(redoAlternative)
    return new Set(accelerators).size === accelerators.length && !globalAccelerators.some((key) => accelerators.includes(key))
  }
}

export class HotkeySettings {
  private accelerator: string
  private registered = false
  private suspended = false

  suspend(): void {
    if (this.suspended) return
    this.suspended = true
    if (this.registered) globalShortcut.unregister(this.accelerator)
  }

  resume(): void {
    if (!this.suspended) return
    this.suspended = false
    this.registered = !this.conflicts(this.accelerator) && this.register(this.accelerator)
  }

  constructor(private readonly path: string, private readonly show: () => void, defaultAccelerator = defaultGlobalHotkeys.search,
    private readonly conflicts: (accelerator: string) => boolean = () => false) {
    this.accelerator = defaultAccelerator
    try {
      const saved = readFileSync(path, 'utf8').trim()
      this.accelerator = normalizeHotkey(saved) ?? defaultAccelerator
    } catch { /* A new profile starts with the default accelerator. */ }
    this.registered = !this.conflicts(this.accelerator) && this.register(this.accelerator)
  }

  state(): Hotkey {
    return { accelerator: this.accelerator, registered: this.registered }
  }

  update(input: string): Result<Hotkey> {
    const next = normalizeHotkey(input)
    if (!next) return { error: {
      code: 'INVALID_ARGUMENT', message: '请使用修饰键和字母、数字、功能键或 Space，例如 Ctrl+Alt+Space。', retryable: false
    } }
    if (next === this.accelerator && this.registered) return { result: this.state() }
    if (this.conflicts(next)) return { error: {
      code: 'HOTKEY_CONFLICT', message: '此组合已用作其它快捷键。', retryable: false
    } }
    if (!this.register(next)) return { error: {
      code: 'HOTKEY_UNAVAILABLE', message: '此快捷键无法注册或已被占用，请尝试其他组合。', retryable: true
    } }

    try {
      writeSetting(this.path, next)
    } catch {
      globalShortcut.unregister(next)
      return { error: { code: 'SETTINGS_WRITE_FAILED', message: '无法保存快捷键。', retryable: true } }
    }

    if (this.registered) globalShortcut.unregister(this.accelerator)
    this.accelerator = next
    this.registered = true
    return { result: this.state() }
  }

  private register(accelerator: string): boolean {
    try { return globalShortcut.register(accelerator, this.show) } catch { return false }
  }
}
