import { spawn } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { createInterface } from 'node:readline'
import { z } from 'zod'
import { directoryIndexProgressSchema, type DirectoryIndexProgress } from '../shared/search'
import { healthSchema, serviceErrorSchema, type HealthResult, type Result, type ServiceError } from '../shared/health'

const responseEnvelope = z.object({ v: z.literal(1), id: z.string() })
const responseSchema = z.union([
  responseEnvelope.extend({ result: z.unknown(), error: z.never().optional() })
    .refine((reply) => Object.hasOwn(reply, 'result')),
  responseEnvelope.extend({ error: serviceErrorSchema, result: z.never().optional() })
])

const unavailable: ServiceError = {
  code: 'SERVICE_UNAVAILABLE', message: '本地服务暂不可用。', retryable: true
}

type Pending = {
  receive: (value: unknown) => boolean
  reject: (error: ServiceError) => void
  timer: ReturnType<typeof setTimeout>
}

export class ServiceClient {
  private readonly child
  private readonly pending = new Map<string, Pending>()
  private available = true
  private stopping = false
  private readonly closed: Promise<void>

  constructor(command: string, args: string[], onExit: () => void, env?: NodeJS.ProcessEnv) {
    const child = spawn(command, args, {
      windowsHide: true,
      env,
      stdio: ['pipe', 'pipe', 'pipe']
    })
    this.child = child
    this.closed = new Promise((resolve) => child.once('close', () => resolve()))

    const lines = createInterface({ input: child.stdout, crlfDelay: Infinity })
    lines.on('line', (line) => this.receive(line))
    lines.on('error', () => this.fail())
    child.stdout.on('error', () => this.fail())
    if (env?.APM_SMOKE_PROFILE) child.stderr.pipe(process.stderr, { end: false })
    else child.stderr.resume()
    child.stdin.on('error', () => this.fail())
    child.on('error', () => this.fail())
    child.once('exit', () => this.fail())
    child.once('close', () => {
      this.fail()
      if (!this.stopping) onExit()
    })
  }

  health(): Promise<HealthResult> {
    return this.request('app.health', {}, healthSchema)
  }

  request<T>(method: string, params: object, schema: z.ZodType<T>, timeoutMs = 10_000,
    onProgress?: (progress: DirectoryIndexProgress) => void): Promise<Result<T>> {
    if (!this.available) return Promise.resolve({ error: unavailable })
    if (method === 'search.filter' && Array.isArray((params as { directoryPaths?: unknown }).directoryPaths))
      return this.directorySearch(params, schema, timeoutMs)
    if (method === 'search.indexDirectories') return this.directorySearch(params, schema, timeoutMs, true, onProgress)
    const id = randomUUID()
    return new Promise((resolve) => {
      const timer = setTimeout(() => this.take(id)?.reject({
        code: 'SERVICE_TIMEOUT', message: '本地服务响应超时。', retryable: true
      }), timeoutMs)
      this.pending.set(id, {
        timer,
        reject: (error) => resolve({ error }),
        receive: (value) => {
          const parsed = schema.safeParse(value)
          if (!parsed.success) return false
          resolve({ result: parsed.data })
          return true
        }
      })
      const line = JSON.stringify({ v: 1, id, method, params })
      this.child.stdin.write(line + '\n', (error) => {
        if (error) this.fail()
      })
    })
  }

  private async directorySearch<T>(params: object, schema: z.ZodType<T>, timeoutMs: number, indexOnly = false,
    onProgress?: (progress: DirectoryIndexProgress) => void): Promise<Result<T>> {
    const started = await this.request(indexOnly ? 'search.indexStart' : 'search.start', params, z.object({ jobId: z.string() }), timeoutMs)
    if ('error' in started) return started
    const statusSchema = z.discriminatedUnion('state', [
      z.object({ state: z.literal('running'), progress: directoryIndexProgressSchema.nullish() }),
      z.object({ state: z.literal('complete'), page: schema, progress: directoryIndexProgressSchema.nullish() }),
      z.object({ state: z.literal('failed'), error: serviceErrorSchema })
    ])
    while (this.available && !this.stopping) {
      const status = await this.request('search.status', { jobId: started.result.jobId }, statusSchema, timeoutMs)
      if ('error' in status) return status
      if ('progress' in status.result && status.result.progress) onProgress?.(status.result.progress)
      if (status.result.state === 'complete') return { result: status.result.page }
      if (status.result.state === 'failed') return { error: status.result.error }
      await new Promise<void>((resolve) => setTimeout(resolve, 300))
    }
    return { error: unavailable }
  }

  async close(): Promise<void> {
    this.stopping = true
    this.fail()
    this.child.stdin.end()
    const timeout = setTimeout(() => this.child.kill(), 2_000)
    try {
      await this.closed
    } finally {
      clearTimeout(timeout)
    }
  }

  private receive(line: string): void {
    let value: unknown
    try {
      value = JSON.parse(line)
    } catch {
      this.protocolFailure()
      return
    }
    const parsed = responseSchema.safeParse(value)
    if (!parsed.success) {
      this.protocolFailure()
      return
    }
    const reply = parsed.data
    const pending = this.pending.get(reply.id)
    if (!pending) return
    if (reply.error) pending.reject(reply.error)
    else if (!pending.receive(reply.result)) {
      this.protocolFailure()
      return
    }
    this.take(reply.id)
  }

  private protocolFailure(): void {
    const error = {
      code: 'SERVICE_PROTOCOL_ERROR',
      message: '本地服务返回了无效的响应。',
      retryable: true
    }
    for (const id of this.pending.keys()) this.take(id)?.reject(error)
    this.fail()
    this.child.kill()
  }

  private fail(): void {
    this.available = false
    for (const id of this.pending.keys()) this.take(id)?.reject(unavailable)
  }

  private take(id: string): Pending | undefined {
    const request = this.pending.get(id)
    if (!request) return
    this.pending.delete(id)
    clearTimeout(request.timer)
    return request
  }
}
