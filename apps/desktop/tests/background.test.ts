import { describe, expect, it } from 'vitest'
import { backgroundPalette, dominantColor } from '../src/shared/background'

describe('background colors', () => {
  it('chooses the largest color cluster instead of averaging opposite colors', () => {
    expect(dominantColor([200, 30, 30, 255, 202, 31, 31, 255, 0, 0, 255, 255])).toEqual([201, 31, 31])
  })
  it('ignores transparent pixels and handles an empty image', () => {
    expect(dominantColor([255, 0, 0, 0, 0, 150, 0, 255])).toEqual([0, 150, 0])
    expect(dominantColor([])).toEqual([100, 120, 140])
  })
  it('keeps pale or dark images from producing unreadable control colors', () => {
    for (const color of [[255, 255, 255], [0, 0, 0], [255, 0, 0]] as [number, number, number][]) {
      const light = backgroundPalette(color, true), dark = backgroundPalette(color, false)
      expect(light['--primary']).toContain('32%')
      expect(dark['--accent']).toContain('76%')
      expect(Object.values(light).join('')).not.toContain('NaN')
      expect(Object.values(dark).join('')).not.toContain('NaN')
    }
  })
})
