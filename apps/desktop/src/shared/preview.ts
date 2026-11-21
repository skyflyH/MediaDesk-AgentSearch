import { z } from 'zod'

export const previewKind = z.enum(['image', 'video', 'thumbnail', 'proxy'])
export const previewFile = z.object({
  path: z.string(), size: z.number().int().nonnegative(), mime: z.string(),
  volume: z.string().regex(/^[0-9A-F]{8}$/), fileId: z.string().regex(/^[0-9A-F]{16}$/),
  createdNs: z.string().regex(/^-?\d+$/), writtenNs: z.string().regex(/^-?\d+$/)
})
export const proxyStatus = z.object({
  state: z.enum(['idle', 'queued', 'running', 'complete', 'failed', 'cancelled', 'unavailable']),
  message: z.string().nullable()
})
export type PreviewKind = z.infer<typeof previewKind>
export type ProxyStatus = z.infer<typeof proxyStatus>
