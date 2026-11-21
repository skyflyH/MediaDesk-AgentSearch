import type { App } from 'electron'

type LoginApp = Pick<App, 'getLoginItemSettings' | 'setLoginItemSettings' | 'getAppPath' | 'isPackaged'>

export class LoginStartup {
  constructor(private readonly app: LoginApp, private readonly executable = process.execPath,
    private readonly platform = process.platform) {}

  private options() {
    return { path: this.executable, args: this.app.isPackaged ? [] : [`"${this.app.getAppPath()}"`] }
  }

  get(): boolean {
    if (this.platform !== 'win32') throw Error('当前系统不支持开机自动启动设置。')
    const state = this.app.getLoginItemSettings(this.options())
    return state.openAtLogin && state.executableWillLaunchAtLogin
  }

  set(enabled: boolean): boolean {
    if (this.platform !== 'win32') throw Error('当前系统不支持开机自动启动设置。')
    this.app.setLoginItemSettings({ ...this.options(), name: 'ai-photos-manager', openAtLogin: enabled, enabled })
    const actual = this.get()
    if (actual !== enabled) throw Error('开机自动启动设置未生效，请检查 Windows 的启动应用设置。')
    return actual
  }
}
