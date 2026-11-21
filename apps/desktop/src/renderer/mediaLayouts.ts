import { Grid2X2, Image, List } from '@lucide/vue'

export const mediaLayouts = [
  { key: 'list', label: '列表', icon: List },
  { key: 'medium', label: '中图', icon: Grid2X2 },
  { key: 'large', label: '大图', icon: Image }
] as const

export type MediaLayout = typeof mediaLayouts[number]['key']
