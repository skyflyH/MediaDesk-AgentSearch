import { ref } from 'vue'
import type { ColorTheme } from '../shared/desktop-controls'
import { applyBackground } from './background'
import { accentColor, accentStorageKey, readAccent } from './accent'

const storageKey = 'apm.color-theme'

function readTheme(): ColorTheme {
  try {
    if (localStorage.getItem(storageKey) === 'light') return 'light'
  } catch { /* Use the default if storage is unavailable. */ }
  return 'dark'
}

export const theme = ref<ColorTheme>(readTheme())

export function applyTheme(): void {
  document.documentElement.dataset.theme = theme.value
  applyBackground(theme.value)
  window.setWindowTheme?.(theme.value)
}

export function setTheme(value: string): void {
  if (value !== 'dark' && value !== 'light') return
  theme.value = value
  applyTheme()
  try { localStorage.setItem(storageKey, value) } catch { /* Keep the theme for this session. */ }
}

window.addEventListener('storage', event => {
  if (event.key !== null && event.key !== storageKey && event.key !== accentStorageKey) return
  theme.value = readTheme()
  accentColor.value = readAccent()
  applyTheme()
})
