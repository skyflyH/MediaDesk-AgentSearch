export type BackgroundColor = [number, number, number]

export function dominantColor(pixels: ArrayLike<number>): BackgroundColor {
  const buckets = new Map<number, { count: number; sum: BackgroundColor }>()
  for (let i = 0; i + 3 < pixels.length; i += 4) {
    if (pixels[i + 3]! < 128) continue
    const r = pixels[i]!, g = pixels[i + 1]!, b = pixels[i + 2]!
    const key = (r >> 4) * 256 + (g >> 4) * 16 + (b >> 4)
    const bucket = buckets.get(key) ?? { count: 0, sum: [0, 0, 0] }
    bucket.count++
    bucket.sum[0] += r; bucket.sum[1] += g; bucket.sum[2] += b
    buckets.set(key, bucket)
  }
  const winner = [...buckets.values()].sort((a, b) => b.count - a.count)[0]
  return winner ? winner.sum.map((value) => Math.round(value / winner.count)) as BackgroundColor : [100, 120, 140]
}

export function backgroundPalette(color: BackgroundColor, light: boolean): Record<string, string> {
  const [r, g, b] = color.map((value) => value / 255) as BackgroundColor
  const max = Math.max(r, g, b), min = Math.min(r, g, b), delta = max - min
  const hue = delta === 0 ? 210 : ((max === r ? (g - b) / delta : max === g ? (b - r) / delta + 2 : (r - g) / delta + 4) * 60 + 360) % 360
  const saturation = delta === 0 ? 0 : Math.min(65, Math.max(25, delta / (1 - Math.abs(max + min - 1)) * 100))
  const tone = (l: number, alpha = 1, s = saturation) => `hsl(${hue.toFixed(1)} ${s.toFixed(1)}% ${l}% / ${alpha})`
  return {
    '--background-base': tone(light ? 96 : 9),
    '--paper': tone(light ? 97 : 12, .86), '--panel': tone(light ? 98 : 13, .84),
    '--surface': tone(light ? 94 : 10, .9), '--input': tone(light ? 99 : 16, .94),
    '--button': tone(light ? 91 : 23, .96), '--hover': tone(light ? 86 : 28, .96),
    '--line': tone(light ? 80 : 29, .75), '--control-border': tone(light ? 56 : 47),
    '--selected': tone(light ? 84 : 29), '--selected-hover': tone(light ? 78 : 35),
    '--accent': tone(light ? 30 : 76), '--focus': tone(light ? 36 : 65),
    '--primary': tone(light ? 32 : 34), '--primary-hover': tone(light ? 26 : 28),
    '--scroll-thumb': tone(light ? 48 : 56)
  }
}
