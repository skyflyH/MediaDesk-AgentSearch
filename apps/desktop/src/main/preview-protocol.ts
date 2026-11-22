import { randomUUID } from 'node:crypto'
import { open } from 'node:fs/promises'
import { Readable, type Readable as NodeReadable } from 'node:stream'
import { protocol } from 'electron'
import type { Result } from '../shared/health'
import { previewFile, type PreviewKind } from '../shared/preview'
import type { z } from 'zod'
import type { ServiceClient } from './service-client'
import { parseRange } from './byte-range'

const scheme = 'media-preview'
type Lease = {
  assetId: string
  owner?: number
  kind: PreviewKind
  file: z.infer<typeof previewFile>
  responses: Set<Promise<Response>>
  streams: Set<NodeReadable>
  revoked: boolean
  closing?: Promise<void>
}

export function registerPreviewScheme() {
  protocol.registerSchemesAsPrivileged([{ scheme, privileges: {
    standard: true, secure: true, stream: true
  } }])
}

export class PreviewProtocol {
  private readonly leases = new Map<string, Lease>()
  private readonly opening = new Map<string, { revoked: boolean; cancel?: () => void }>()
  private readonly waiting: Array<{ thumbnail: boolean; run: () => void }> = []
  private resolving = false

  constructor(private readonly local: () => ServiceClient) {}

  register() {
    protocol.handle(scheme, async (request) => {
      const url = new URL(request.url)
      const lease = url.hostname === 'asset' && !url.search && !url.hash
        ? this.leases.get(url.pathname.slice(1)) : undefined
      if (!lease || lease.revoked) return new Response(null, { status: 404 })
      if (request.method !== 'GET' && request.method !== 'HEAD') {
        return new Response(null, { status: 405, headers: { Allow: 'GET, HEAD' } })
      }
      const response = this.respond(lease, request)
      lease.responses.add(response)
      try {
        return await response
      } finally {
        lease.responses.delete(response)
      }
    })
  }

  private async respond(lease: Lease, request: Request): Promise<Response> {
    try {
      if (lease.revoked) return new Response(null, { status: 404 })
      const resolved = lease.file
      const file = await open(resolved.path, 'r')
      try {
        const stats = await file.stat({ bigint: true })
        const { size, volume, fileId, createdNs, writtenNs, mime } = resolved
        if (!stats.isFile() || lease.revoked || stats.size !== BigInt(size) ||
            stats.dev !== BigInt('0x' + volume) || stats.ino !== BigInt('0x' + fileId) ||
            stats.birthtimeNs !== BigInt(createdNs) || stats.mtimeNs !== BigInt(writtenNs)) throw Error('Preview changed')
        const range = parseRange(request.headers.get('range'), size)
        const headers = new Headers({
          'Content-Type': mime, 'Accept-Ranges': 'bytes',
          'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff'
        })
        if (range === false) {
          await file.close()
          headers.set('Content-Range', `bytes */${size}`)
          return new Response(null, { status: 416, headers })
        }
        if (range) headers.set('Content-Range', `bytes ${range.start}-${range.end}/${size}`)
        headers.set('Content-Length', String(range ? range.end - range.start + 1 : size))
        const status = range ? 206 : 200
        if (request.method === 'HEAD' || size === 0) {
          await file.close()
          return new Response(null, { status, headers })
        }
        const stream = file.createReadStream(range ?? undefined)
        lease.streams.add(stream)
        stream.once('close', () => lease.streams.delete(stream))
        return new Response(Readable.toWeb(stream) as never, { status, headers })
      } catch {
        await file.close()
        return new Response(null, { status: 404 })
      }
    } catch {
      return new Response(null, { status: 404 })
    }
  }

  async issue(assetId: string, kind: PreviewKind, owner?: number): Promise<Result<string>> {
    const thumbnail = kind === 'thumbnail'
    const key = this.leaseKey(assetId, thumbnail, owner)
    await this.release(assetId, thumbnail, owner)
    const previous = this.opening.get(key)
    if (previous) previous.revoked = true
    const opening: { revoked: boolean; cancel?: () => void } = { revoked: false }
    this.opening.set(key, opening)
    try {
      const result = await this.schedule(assetId, kind, opening)
      if ('error' in result) return result
      if (opening.revoked) return { error: { code: 'PREVIEW_CANCELLED', message: '预览已关闭。', retryable: true } }
      const token = randomUUID()
      this.leases.set(token, { assetId, kind, owner, file: result.result, responses: new Set(), streams: new Set(), revoked: false })
      return { result: `${scheme}://asset/${token}` }
    } finally {
      if (this.opening.get(key) === opening) this.opening.delete(key)
    }
  }

  async release(assetId: string, thumbnail = false, owner?: number): Promise<void> {
    const opening = this.opening.get(this.leaseKey(assetId, thumbnail, owner))
    if (opening) { opening.revoked = true; opening.cancel?.() }
    await Promise.all([...this.leases].filter(([, lease]) =>
      lease.assetId === assetId && lease.owner === owner && (lease.kind === 'thumbnail') === thumbnail)
      .map(([token, lease]) => lease.closing ??= this.closeLease(token, lease)))
  }

  private async closeLease(token: string, lease: Lease): Promise<void> {
    lease.revoked = true
    await Promise.all(lease.responses)
    await Promise.all([...lease.streams].map((stream) => new Promise<void>((resolve) => {
      stream.once('close', resolve)
      stream.destroy()
    })))
    this.leases.delete(token)
  }

  private leaseKey(assetId: string, thumbnail: boolean, owner?: number) {
    return `${owner ?? ''}:${thumbnail ? 'thumbnail:' : ''}${assetId}`
  }

  private resolve(assetId: string, kind: PreviewKind) {
    return this.local().request('preview.resolve', { assetId, kind }, previewFile)
  }

  private schedule(assetId: string, kind: PreviewKind, opening: { revoked: boolean; cancel?: () => void }): Promise<Result<z.infer<typeof previewFile>>> {
    return new Promise((resolve, reject) => {
      const cancelled: Result<z.infer<typeof previewFile>> = { error: {
        code: 'PREVIEW_CANCELLED', message: '预览已关闭。', retryable: true
      } }
      const job = { thumbnail: kind === 'thumbnail', run: () => {
        opening.cancel = undefined
        if (opening.revoked) { resolve(cancelled); this.pump(); return }
        this.resolving = true
        void this.resolve(assetId, kind).then(resolve, reject).finally(() => {
          this.resolving = false
          this.pump()
        })
      } }
      opening.cancel = () => {
        const index = this.waiting.indexOf(job)
        if (index >= 0) this.waiting.splice(index, 1)
        resolve(cancelled)
      }
      this.waiting.push(job)
      this.pump()
    })
  }

  private pump() {
    if (this.resolving) return
    const foreground = this.waiting.findIndex(job => !job.thumbnail)
    const job = this.waiting.splice(foreground < 0 ? 0 : foreground, 1)[0]
    job?.run()
  }
}
