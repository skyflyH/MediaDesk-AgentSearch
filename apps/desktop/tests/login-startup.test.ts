import { describe, expect, it, vi } from 'vitest'
import type { App } from 'electron'
import { isLoginStartup, LoginStartup } from '../src/main/login-startup'

function setup(packaged = true) {
  let openAtLogin = false
  let approved = true
  const api = {
    isPackaged: packaged,
    getAppPath: () => 'C:\\Media Library\\app',
    getLoginItemSettings: vi.fn(() => ({ openAtLogin, executableWillLaunchAtLogin: openAtLogin && approved }) as ReturnType<App['getLoginItemSettings']>),
    setLoginItemSettings: vi.fn((value: Parameters<App['setLoginItemSettings']>[0]) => { openAtLogin = value.openAtLogin ?? false; approved = value.enabled ?? true })
  }
  return { api, settings: new LoginStartup(api, 'C:\\Media Library\\app.exe', 'win32'),
    disableInWindows: () => { approved = false } }
}

describe('Windows login startup settings', () => {
  it('recognizes only explicit login launches', () => {
    expect(isLoginStartup(['MediaDesk.exe', '--login-startup'])).toBe(true)
    expect(isLoginStartup(['node.exe', 'main/index.cjs', '--login-startup'])).toBe(true)
    expect(isLoginStartup(['MediaDesk.exe'])).toBe(false)
    expect(isLoginStartup(['MediaDesk.exe', '--login-startup-other'])).toBe(false)
    expect(isLoginStartup([])).toBe(false)
  })
  it('enables and removes the login item, using the same executable and arguments for read-back', () => {
    const { api, settings } = setup()
    expect(settings.get()).toBe(false)
    expect(settings.set(true)).toBe(true)
    expect(api.setLoginItemSettings).toHaveBeenLastCalledWith({
      name: 'ai-photos-manager', path: 'C:\\Media Library\\app.exe', args: ['--login-startup'], openAtLogin: true, enabled: true
    })
    expect(api.getLoginItemSettings).toHaveBeenLastCalledWith({ path: 'C:\\Media Library\\app.exe', args: ['--login-startup'] })
    expect(settings.set(false)).toBe(false)
    expect(api.setLoginItemSettings).toHaveBeenLastCalledWith(expect.objectContaining({ openAtLogin: false, enabled: false }))
  })

  it('reflects Task Manager disabled state and re-enables it when requested', () => {
    const { settings, disableInWindows } = setup()
    settings.set(true)
    disableInWindows()
    expect(settings.get()).toBe(false)
    expect(settings.set(true)).toBe(true)
  })

  it('does not report success when Windows fails to apply the setting', () => {
    const { api, settings } = setup()
    api.setLoginItemSettings.mockImplementation(() => {})
    expect(() => settings.set(true)).toThrow('未生效')
  })

  it('includes the quoted project path when launching a development build', () => {
    const { api, settings } = setup(false)
    settings.set(true)
    expect(api.setLoginItemSettings).toHaveBeenLastCalledWith(expect.objectContaining({ args: ['"C:\\Media Library\\app"', '--login-startup'] }))
    expect(api.getLoginItemSettings).toHaveBeenLastCalledWith(expect.objectContaining({ args: ['"C:\\Media Library\\app"', '--login-startup'] }))
  })

  it('does not change login items on an unsupported operating system', () => {
    const { api } = setup()
    expect(() => new LoginStartup(api, '/app', 'linux').set(true)).toThrow('不支持')
    expect(api.setLoginItemSettings).not.toHaveBeenCalled()
  })
})
