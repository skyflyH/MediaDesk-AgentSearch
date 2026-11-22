import { createServer, type IncomingMessage } from 'node:http'
import { randomUUID } from 'node:crypto'
import { expect, it } from 'vitest'
import { openAiTransport, probeModelConnection, parseSearchAnswer, normalizeSearchAnswer } from '../src/main/model-agent'
import { defaultModelConfig } from '../src/shared/model'

const assetId = randomUUID()

it('accepts one JSON answer with surrounding prose and defaults optional narrative fields', () => {
  const answer = parseSearchAnswer('检索完成：\n```json\n' + JSON.stringify({ resultIds: [assetId], summary: null, extra: true }) + '\n```\n以上为结果。')
  expect(normalizeSearchAnswer(answer)).toMatchObject({ resultIds: [assetId], summary: '', unresolvedConditions: [] })
  expect(() => parseSearchAnswer('{"resultIds":[]} {"resultIds":[]}')).toThrow()
  expect(() => normalizeSearchAnswer({ resultIds: ['invented'] })).toThrow()
  expect(() => parseSearchAnswer('{"resultIds":[')).toThrow()
})
type RequestBody = {
  tools?: Array<{ function?: { name: string }; name?: string }>
  messages?: Array<{ role: string; content: string }>
  response_format?: unknown
  store?: boolean
  input?: unknown
  text?: { format?: unknown }
}

async function body(request: IncomingMessage): Promise<RequestBody> {
  let data = ''
  for await (const chunk of request) data += String(chunk)
  return JSON.parse(data) as RequestBody
}

it.each(['responses', 'completions'] as const)('checks the configured %s model with a real request', async (apiMode) => {
  let status = 200
  const requests: Array<{ url?: string; key?: string; payload: RequestBody }> = []
  const server = createServer(async (request, response) => {
    requests.push({ url: request.url, key: request.headers.authorization, payload: await body(request) })
    response.writeHead(status, { 'Content-Type': 'application/json' })
    response.end(JSON.stringify(status !== 200 ? { error: { message: 'fixture secret' } } : apiMode === 'responses' ? {
      id: 'resp_check', object: 'response', status: 'completed', model: 'fixture-model',
      output: [{ id: 'msg_check', role: 'assistant', status: 'completed', type: 'message',
        content: [{ type: 'output_text', text: 'OK', annotations: [] }] }]
    } : {
      id: 'chatcmpl-check', object: 'chat.completion', model: 'fixture-model', created: 1760000000,
      choices: [{ index: 0, message: { role: 'assistant', content: 'OK' }, finish_reason: 'stop' }]
    }))
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  try {
    const port = (server.address() as { port: number }).port
    const config = { ...defaultModelConfig, apiMode, model: 'fixture-model',
      baseUrl: `http://127.0.0.1:${port}/v1`, apiKey: 'test-only' }
    expect(await probeModelConnection(config, AbortSignal.timeout(5000))).toEqual({ model: config.model, state: 'connected' })
    expect(requests[0]).toMatchObject({
      key: 'Bearer test-only', url: apiMode === 'responses' ? '/v1/responses' : '/v1/chat/completions',
      payload: apiMode === 'responses' ? { model: config.model, store: false, max_output_tokens: 256 }
        : { model: config.model, max_tokens: 256, messages: [{ role: 'user', content: 'Reply with only OK.' }] }
    })
    status = 401
    expect(await probeModelConnection(config, AbortSignal.timeout(5000))).toEqual({
      model: config.model, state: 'error', message: '认证失败，请检查 API Key 和模型权限。'
    })
    status = 402
    expect(await probeModelConnection(config, AbortSignal.timeout(5000))).toEqual({
      model: config.model, state: 'error', message: '模型账户余额不足，请在服务商控制台核对余额。'
    })
  } finally {
    await new Promise<void>((resolve) => server.close(() => resolve()))
  }
})

it('accepts a JSON code block from a Chat Completions tool loop', async () => {
  const requests: Array<{ url?: string; payload: RequestBody }> = []
  const server = createServer(async (request, response) => {
    const payload = await body(request)
    requests.push({ url: request.url, payload })
    const toolsRun = payload.messages?.some((message) => message.role === 'tool')
    const final = { resultIds: [assetId], summary: '从本机找到素材', unresolvedConditions: [] }
    const message = payload.tools?.length && !toolsRun ? {
      role: 'assistant', content: null, tool_calls: [{
        id: 'call_local_search', type: 'function', function: {
          name: 'search_assets', arguments: JSON.stringify({ mediaKind: 'video', author: '林安', remarkTerms: ['青岛'] })
        }
      }]
    } : { role: 'assistant', content: toolsRun
      ? '```json\n' + JSON.stringify(final) + '\n```'
      : JSON.stringify({ draft: '青岛海边日落' }) }
    response.writeHead(200, { 'Content-Type': 'application/json' })
    response.end(JSON.stringify({
      id: 'chatcmpl-fixture', object: 'chat.completion', created: 1760000000, model: 'fixture-model',
      choices: [{ index: 0, message, finish_reason: 'tool_calls' in message ? 'tool_calls' : 'stop' }]
    }))
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  try {
    const port = (server.address() as { port: number }).port
    const model = openAiTransport({ ...defaultModelConfig, model: 'fixture-model', apiMode: 'completions',
      baseUrl: `http://127.0.0.1:${port}/v1`, apiKey: 'test-only' })
    const signal = AbortSignal.timeout(5000)
    expect(await model.suggest('remark', { keywords: '青岛、海边、日落' }, signal)).toEqual({ draft: '青岛海边日落' })
    expect(requests[0]?.payload.response_format).toEqual({ type: 'json_object' })
    let toolCalls = 0
    const result = await model.search('检索视频', {
      search: async (filter) => {
        toolCalls++
        expect(filter).toMatchObject({ mediaKind: 'video', author: '林安', remarkTerms: ['青岛'] })
        return { items: [{ assetId }] }
      },
      details: async () => { throw Error('Unexpected details call') }
    }, signal)
    expect(result, JSON.stringify(requests.map(({ url, payload }) => ({ url, roles: payload.messages?.map((msg) => msg.role),
      tools: payload.tools?.length })))).toMatchObject({ resultIds: [assetId] })
    expect(toolCalls).toBe(1)
    expect(requests.every((request) => request.url === '/v1/chat/completions')).toBe(true)
    expect(requests[1]?.payload.tools?.map((entry) => entry.function?.name)).toEqual(['search_assets', 'get_asset_details'])
  } finally {
    await new Promise<void>((resolve) => server.close(() => resolve()))
  }
})

it('requests structured Responses output with storage disabled', async () => {
  let received: { url?: string; payload: RequestBody } | undefined
  const server = createServer(async (request, response) => {
    received = { url: request.url, payload: await body(request) }
    response.writeHead(200, { 'Content-Type': 'application/json' })
    response.end(JSON.stringify({
      id: 'resp_fixture', object: 'response', status: 'completed', model: 'fixture-model',
      output: [{ id: 'msg_fixture', role: 'assistant', status: 'completed', type: 'message', content: [
        { type: 'output_text', text: '{"draft":"青岛海边日落"}', annotations: [] }
      ] }]
    }))
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  try {
    const port = (server.address() as { port: number }).port
    const model = openAiTransport({ ...defaultModelConfig, model: 'fixture-model', apiMode: 'responses',
      baseUrl: `http://127.0.0.1:${port}/v1`, apiKey: 'test-only' })
    expect(await model.suggest('remark', { keywords: '青岛、海边、日落' }, AbortSignal.timeout(5000)))
      .toEqual({ draft: '青岛海边日落' })
    expect(received).toMatchObject({ url: '/v1/responses', payload: { store: false, text: { format: { type: 'json_schema' } } } })
  } finally {
    await new Promise<void>((resolve) => server.close(() => resolve()))
  }
})

it('resolves read-only tool calls over the default Responses API', async () => {
  const requests: RequestBody[] = []
  const server = createServer(async (request, response) => {
    const payload = await body(request)
    requests.push(payload)
    const first = requests.length === 1
    response.writeHead(200, { 'Content-Type': 'application/json' })
    response.end(JSON.stringify({
      id: `resp_fixture_${requests.length}`, object: 'response', status: 'completed', model: 'fixture-model',
      output: first ? [{ type: 'function_call', id: 'fc_fixture', call_id: 'call_fixture', name: 'search_assets',
        arguments: '{"mediaKind":"video","remarkTerms":["青岛"]}', status: 'completed' }] : [
        { id: 'msg_fixture', role: 'assistant', status: 'completed', type: 'message', content: [{
          type: 'output_text', text: JSON.stringify({ resultIds: [assetId], summary: '找到结果', unresolvedConditions: [] }),
          annotations: []
        }] }
      ]
    }))
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  try {
    const port = (server.address() as { port: number }).port
    const model = openAiTransport({ ...defaultModelConfig, model: 'fixture-model',
      baseUrl: `http://127.0.0.1:${port}/v1`, apiKey: 'test-only' })
    const signal = AbortSignal.timeout(5000)
    expect(await model.search('青岛视频', {
      search: async (filter) => {
        expect(filter).toMatchObject({ mediaKind: 'video', remarkTerms: ['青岛'] })
        return { items: [{ assetId }] }
      },
      details: async () => { throw Error('Unexpected details call') }
    }, signal)).toMatchObject({ resultIds: [assetId] })
    expect(requests).toHaveLength(2)
    expect(requests.map((request) => request.store)).toEqual([false, false])
    expect(requests[1]?.input).toEqual(expect.arrayContaining([expect.objectContaining({
      type: 'function_call_output', call_id: 'call_fixture'
    })]))
  } finally {
    await new Promise<void>((resolve) => server.close(() => resolve()))
  }
})
