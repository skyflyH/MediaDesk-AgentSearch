import { afterEach, beforeEach, expect, it, vi } from 'vitest'

const values = new Map<string, string>()
const setProperty = vi.fn((key: string, value: string) => { values.set(key, value) })
const removeProperty = vi.fn((key: string) => { values.delete(key) })
const saved = new Map<string, string>()
const setItem = vi.fn((key: string, value: string) => { saved.set(key, value) })

beforeEach(() => {
  vi.resetModules()
  values.clear(); saved.clear(); vi.clearAllMocks()
  vi.stubGlobal('document', { createElement: () => ({ content: {} }), documentElement: { dataset: { theme: 'dark' }, style: { setProperty, removeProperty } } })
  vi.stubGlobal('window', { addEventListener: vi.fn() })
  vi.stubGlobal('location', { hash: '' })
  vi.stubGlobal('localStorage', { getItem: (key: string) => saved.get(key) ?? null, setItem, removeItem: (key: string) => saved.delete(key) })
})
afterEach(() => { vi.unstubAllGlobals() })

it('previews slider values without touching the image, palette or persistent storage, then saves the latest options together', async () => {
  const settings = await import('../src/renderer/background')
  settings.applyBackground('dark')
  vi.clearAllMocks()
  settings.updateBackgroundOption('blur', 25, false)
  settings.updateBackgroundOption('transparency', 35, false)
  expect(setProperty.mock.calls).toEqual([['--background-blur', '25px'], ['--background-opacity', '0.65']])
  expect(removeProperty).not.toHaveBeenCalled()
  expect(setItem).not.toHaveBeenCalled()
  settings.saveBackgroundOptions()
  expect(JSON.parse(saved.get('apm.background-options')!)).toEqual({ blur: 25, transparency: 35 })
})

it('removes each palette override once and restores the manual accent or default colors after removing the background', async () => {
  saved.set('apm.background-image', JSON.stringify({ image: 'data:image/jpeg;base64,AAAA', name: 'test.jpg', color: [10, 80, 150] }))
  const settings = await import('../src/renderer/background')
  const accent = await import('../src/renderer/accent')
  settings.applyBackground('dark')
  expect(new Set(removeProperty.mock.calls.map(([key]) => key)).size).toBe(removeProperty.mock.calls.length)
  accent.saveAccent('#ff0000')
  settings.removeBackground()
  const { backgroundPalette } = await import('../src/shared/background')
  expect(values.get('--accent')).toBe(backgroundPalette([255, 0, 0], false)['--accent'])
  accent.saveAccent('')
  settings.applyBackground('light')
  expect(values.has('--accent')).toBe(false)
  expect(values.get('--background-image')).toBe('none')
})

it('reports storage failures and keeps persisted updates atomic', async () => {
  const settings = await import('../src/renderer/background')
  setItem.mockImplementationOnce(() => { throw Error('quota') })
  expect(() => settings.updateBackgroundOption('blur', 30)).toThrow('无法保存背景设置')
  expect(settings.backgroundOptions.value.blur).toBe(12)
  expect(setProperty).not.toHaveBeenCalled()
})
