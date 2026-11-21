import { afterEach, expect, it } from 'vitest'
import { createServer, get, type Server } from 'node:http'
import { Readable } from 'node:stream'
import { once } from 'node:events'
import { pipePreviewResponse } from '../../webview/preview-response'

const servers: Server[] = []
afterEach(async () => {
  for (const server of servers.splice(0)) {
    server.closeAllConnections()
    await new Promise<void>(resolve => server.close(() => resolve()))
  }
})
async function start(response: () => Response) {
  const server = createServer((request, reply) => {
    if (request.url === '/health') { reply.end('alive'); return }
    void pipePreviewResponse(response(), reply)
  })
  servers.push(server)
  server.listen(0, '127.0.0.1')
  await once(server, 'listening')
  return `http://127.0.0.1:${(server.address() as { port: number }).port}`
}
it('retains status, Range headers and bytes for successful previews', async () => {
  const url = await start(() => new Response('photo', { status: 206, headers: { 'Content-Range': 'bytes 0-4/10' } }))
  const response = await fetch(url)
  expect(response.status).toBe(206)
  expect(response.headers.get('content-range')).toBe('bytes 0-4/10')
  expect(await response.text()).toBe('photo')
})
it('keeps the host alive when changing pages revokes an active file stream', async () => {
  const file = new Readable({ read() {} })
  const url = await start(() => new Response(Readable.toWeb(file) as never))
  const disconnected = new Promise<void>((resolve, reject) => {
    const request = get(url, response => {
      response.once('data', () => file.destroy())
      response.once('aborted', resolve)
      response.on('error', () => {})
    })
    request.once('error', reject)
  })
  file.push(Buffer.alloc(1024))
  await disconnected
  expect(await (await fetch(url + '/health')).text()).toBe('alive')
})
it('cancels the source and keeps the host alive after the browser aborts a request', async () => {
  let cancelled!: () => void
  const cancellation = new Promise<void>(resolve => { cancelled = resolve })
  const url = await start(() => new Response(new ReadableStream({
    start(controller) { controller.enqueue(new Uint8Array(1024)) },
    cancel() { cancelled() }
  })))
  await new Promise<void>((resolve, reject) => {
    const request = get(url, response => {
      response.once('data', () => { response.destroy(); resolve() })
      response.on('error', () => {})
    })
    request.once('error', reject)
  })
  await cancellation
  expect(await (await fetch(url + '/health')).text()).toBe('alive')
})
