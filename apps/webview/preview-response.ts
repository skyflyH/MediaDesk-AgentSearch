import { Readable } from 'node:stream'
import { pipeline } from 'node:stream/promises'
import type { ServerResponse } from 'node:http'

export async function pipePreviewResponse(response: Response, reply: ServerResponse): Promise<void> {
  const headers: Record<string, string> = {}
  response.headers.forEach((value, name) => { headers[name] = value })
  reply.writeHead(response.status, headers)
  if (!response.body) { reply.end(); return }
  // Releasing a preview destroys its file stream. A plain .pipe() leaves the
  // resulting AbortError unhandled; pipeline also cancels on client disconnect.
  try { await pipeline(Readable.fromWeb(response.body as never), reply) }
  catch { reply.destroy() }
}
