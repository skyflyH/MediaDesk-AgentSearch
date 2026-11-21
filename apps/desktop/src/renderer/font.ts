import { ref } from 'vue'

const storageKey = 'apm.font-family'
export const defaultFontFamily = '"Instrument Sans", "Segoe UI", "Microsoft YaHei UI", sans-serif'

function normalize(value: string): string {
  const name = value.trim()
  if (name.length > 160 || /[\x00-\x1f\x7f]/.test(name)) throw Error('字体名称最多 160 个字符，不能包含换行或控制字符。')
  return name
}

function readFont(): string {
  try { return normalize(localStorage.getItem(storageKey) ?? '') }
  catch { return '' }
}

export const fontFamily = ref(readFont())

export function applyFont(): void {
  const name = fontFamily.value.replace(/\\/g, '\\\\').replace(/"/g, '\\"')
  document.documentElement.style.setProperty('--app-font-family',
    name ? `"${name}", ${defaultFontFamily}` : defaultFontFamily)
}

export function setFont(value: string): void {
  const name = normalize(value)
  try {
    if (name) localStorage.setItem(storageKey, name)
    else localStorage.removeItem(storageKey)
  } catch { throw Error('无法保存字体设置，请检查本地存储空间。') }
  fontFamily.value = name
  applyFont()
}

export function initializeFont(): void {
  applyFont()
  window.addEventListener('storage', (event) => {
    if (event.storageArea === localStorage && (event.key === storageKey || event.key === null)) {
      fontFamily.value = readFont()
      applyFont()
    }
  })
}
