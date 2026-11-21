import { ref } from 'vue'
import { backgroundPalette, dominantColor, type BackgroundColor } from '../shared/background'
import type { ColorTheme } from '../shared/desktop-controls'
import { applyAccent, clearAccentStyles } from './accent'

const imageKey = 'apm.background-image'
const optionsKey = 'apm.background-options'
type Background = { image: string; name: string; color: BackgroundColor }
const defaults = { blur: 12, transparency: 65 }

function readBackground(): Background | null {
  try {
    const value = JSON.parse(localStorage.getItem(imageKey) ?? 'null')
    if (value && /^data:image\/jpeg;base64,[A-Za-z0-9+/=]+$/.test(value.image) &&
      typeof value.name === 'string' && Array.isArray(value.color) && value.color.length === 3 &&
      value.color.every((v: unknown) => typeof v === 'number' && v >= 0 && v <= 255)) return value
  } catch { /* Start without a background when storage is unavailable or invalid. */ }
  return null
}

function readOptions() {
  try {
    const value = JSON.parse(localStorage.getItem(optionsKey) ?? 'null')
    if (value && Number.isFinite(value.blur) && value.blur >= 0 && value.blur <= 40 &&
      Number.isFinite(value.transparency) && value.transparency >= 0 && value.transparency <= 100) return { blur: value.blur as number, transparency: value.transparency as number }
  } catch { /* Use the defaults. */ }
  return { ...defaults }
}

export const background = ref(readBackground())
export const backgroundOptions = ref(readOptions())

export function applyBackground(theme: ColorTheme): void {
  const root = document.documentElement
  const activeBackground = location.hash === '#quick-search' ? null : background.value
  clearAccentStyles()
  root.dataset.background = activeBackground ? 'true' : 'false'
  root.style.setProperty('--background-image', activeBackground ? `url("${activeBackground.image}")` : 'none')
  root.style.setProperty('--background-blur', `${backgroundOptions.value.blur}px`)
  root.style.setProperty('--background-opacity', String(1 - backgroundOptions.value.transparency / 100))
  if (activeBackground) {
    const palette = backgroundPalette(activeBackground.color, theme === 'light')
    for (const [key, value] of Object.entries(palette)) root.style.setProperty(key, value)
  }
  applyAccent(theme === 'light')
}

function refresh() { applyBackground(document.documentElement.dataset.theme === 'light' ? 'light' : 'dark') }

export async function importBackground(file: File): Promise<void> {
  if (!['image/jpeg', 'image/png', 'image/webp', 'image/bmp', 'image/gif'].includes(file.type)) throw new Error('请选择 JPG、PNG、WebP、BMP 或 GIF 图片。')
  if (file.size > 30 * 1024 * 1024) throw new Error('图片不能超过 30 MB。')
  let bitmap: ImageBitmap
  try { bitmap = await createImageBitmap(file) } catch { throw new Error('无法读取这张图片，请更换图片后重试。') }
  try {
    const scale = Math.min(1, 1600 / Math.max(bitmap.width, bitmap.height))
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(bitmap.width * scale)); canvas.height = Math.max(1, Math.round(bitmap.height * scale))
    const context = canvas.getContext('2d')!
    context.fillStyle = '#808080'; context.fillRect(0, 0, canvas.width, canvas.height)
    context.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
    const image = canvas.toDataURL('image/jpeg', .82)
    const sample = document.createElement('canvas'); sample.width = 64; sample.height = 64
    const sampling = sample.getContext('2d')!
    sampling.drawImage(canvas, 0, 0, 64, 64)
    const value: Background = { image, name: file.name, color: dominantColor(sampling.getImageData(0, 0, 64, 64).data) }
    try { localStorage.setItem(imageKey, JSON.stringify(value)) } catch { throw new Error('背景图片保存失败，本地存储空间不足或不可用。原背景已保留。') }
    background.value = value
    refresh()
  } finally { bitmap.close() }
}

export function saveBackgroundOptions(): void {
  try { localStorage.setItem(optionsKey, JSON.stringify(backgroundOptions.value)) } catch { throw new Error('无法保存背景设置，请检查本地存储空间。') }
}

export function updateBackgroundOption(key: keyof typeof defaults, value: number, persist = true): void {
  if (!Number.isFinite(value)) return
  const next = { ...backgroundOptions.value, [key]: Math.min(key === 'blur' ? 40 : 100, Math.max(0, value)) }
  if (persist) {
    try { localStorage.setItem(optionsKey, JSON.stringify(next)) } catch { throw new Error('无法保存背景设置，请检查本地存储空间。') }
  }
  backgroundOptions.value = next
  document.documentElement.style.setProperty(key === 'blur' ? '--background-blur' : '--background-opacity',
    key === 'blur' ? `${next.blur}px` : String(1 - next.transparency / 100))
}

export function removeBackground(): void {
  try { localStorage.removeItem(imageKey) } catch { throw new Error('无法移除已保存的背景，请重试。') }
  background.value = null
  refresh()
}

window.addEventListener('storage', (event) => {
  if (event.key !== imageKey && event.key !== optionsKey && event.key !== null) return
  background.value = readBackground()
  backgroundOptions.value = readOptions()
  refresh()
})
