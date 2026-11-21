import { ref } from 'vue'
import { backgroundPalette, type BackgroundColor } from '../shared/background'

export const accentStorageKey = 'apm.accent-color'
const keys = Object.keys(backgroundPalette([0, 0, 0], false))
export function readAccent() {
  try {
    const value = localStorage.getItem(accentStorageKey)
    if (value && /^#[0-9a-f]{6}$/i.test(value)) return value.toLowerCase()
  } catch { /* Use the default palette. */ }
  return ''
}
export const accentColor = ref(readAccent())

export function saveAccent(value: string) {
  if (value && !/^#[0-9a-f]{6}$/i.test(value)) throw Error('请选择有效的主题色。')
  if (value) localStorage.setItem(accentStorageKey, value.toLowerCase())
  else localStorage.removeItem(accentStorageKey)
  accentColor.value = value.toLowerCase()
}

export function clearAccentStyles() {
  for (const key of keys) document.documentElement.style.removeProperty(key)
}

export function applyAccent(light: boolean) {
  const color = accentColor.value
  if (!color) return
  const rgb = [1, 3, 5].map(offset => parseInt(color.slice(offset, offset + 2), 16)) as BackgroundColor
  const palette = backgroundPalette(rgb, light)
  for (const [key, value] of Object.entries(palette)) document.documentElement.style.setProperty(key, value)
}
