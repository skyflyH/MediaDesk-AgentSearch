import type { DesktopApi } from '../shared/health'
import type { ColorTheme } from '../shared/desktop-controls'

declare global {
  interface Window {
    mediaLibrary: DesktopApi
    setWindowTheme?: (theme: ColorTheme) => void
  }
}
