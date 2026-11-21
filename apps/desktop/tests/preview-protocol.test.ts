import { expect, it, vi } from 'vitest'
import { PreviewProtocol } from '../src/main/preview-protocol'
import type { ServiceClient } from '../src/main/service-client'

const { register, open } = vi.hoisted(() => ({ register: vi.fn(), open: vi.fn() }))
vi.mock('electron', () => ({ protocol: { handle: register } }))
vi.mock('node:fs/promises', () => ({ open }))

it('keeps the queue thumbnail and editor preview leases separate for the same asset', async () => {
  const request = vi.fn().mockResolvedValue({ result: {} })
  const media = new PreviewProtocol(() => ({ request }) as unknown as ServiceClient)
  media.register()
  const handler = register.mock.calls[0]?.[1] as ((request: Request) => Promise<Response>) | undefined
  if (!handler) throw new Error('Preview protocol was not registered')
  const thumbnail = await media.issue('asset', 'thumbnail')
  const editor = await media.issue('asset', 'image')
  if ('error' in thumbnail || 'error' in editor) throw new Error('Preview lease was not issued')

  const canReachService = async (url: string) => {
    request.mockClear()
    request.mockResolvedValueOnce({ error: { code: 'PREVIEW_UNAVAILABLE' } })
    await handler(new Request(url))
    return request.mock.calls.length === 1
  }

  expect(await canReachService(thumbnail.result)).toBe(true)
  expect(await canReachService(editor.result)).toBe(true)
  await media.release('asset', true)
  expect(await canReachService(editor.result)).toBe(true)
  expect(await canReachService(thumbnail.result)).toBe(false)
})

it('waits for an in-flight preview file to close before allowing a write', async () => {
  const request = vi.fn().mockResolvedValue({ result: { path: 'source.mp4' } })
  const media = new PreviewProtocol(() => ({ request }) as unknown as ServiceClient)
  media.register()
  const handler = register.mock.lastCall?.[1] as (request: Request) => Promise<Response>
  const editor = await media.issue('asset', 'video')
  if ('error' in editor) throw new Error('Preview lease was not issued')

  const close = vi.fn().mockResolvedValue(undefined)
  let finishOpening!: () => void
  open.mockImplementationOnce(() => new Promise((resolve) => {
    finishOpening = () => resolve({ stat: vi.fn().mockResolvedValue({ isFile: () => true }), close })
  }))
  const response = handler(new Request(editor.result))
  await vi.waitFor(() => expect(open).toHaveBeenCalledOnce())
  const closedAtRelease = media.release('asset').then(() => close.mock.calls.length)
  const closedAtSecondRelease = media.release('asset').then(() => close.mock.calls.length)
  await new Promise<void>((resolve) => setImmediate(resolve))
  finishOpening()
  expect(await closedAtRelease).toBe(1)
  expect(await closedAtSecondRelease).toBe(1)
  expect((await response).status).toBe(404)
})

it('keeps quick search and workspace thumbnails independent for the same asset', async () => {
  const request = vi.fn().mockResolvedValue({ result: {} })
  const media = new PreviewProtocol(() => ({ request }) as unknown as ServiceClient)
  media.register()
  const handler = register.mock.lastCall?.[1] as (request: Request) => Promise<Response>
  const workspace = await media.issue('asset', 'thumbnail')
  const quickSearch = await media.issue('asset', 'thumbnail', 42)
  if ('error' in workspace || 'error' in quickSearch) throw new Error('Preview lease was not issued')

  await media.release('asset', true, 42)
  request.mockClear()
  expect((await handler(new Request(quickSearch.result))).status).toBe(404)
  expect(request).not.toHaveBeenCalled()
  request.mockResolvedValueOnce({ error: { code: 'PREVIEW_UNAVAILABLE' } })
  await handler(new Request(workspace.result))
  expect(request).toHaveBeenCalledOnce()
})
