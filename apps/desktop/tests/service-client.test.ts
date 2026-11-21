import { afterEach, describe, expect, it, vi } from 'vitest'
import { ServiceClient } from '../src/main/service-client'
import { z } from 'zod'

const health = {
  protocolVersion: 1,
  runtime: '10.0.15',
  database: {
    status: 'ready',
    sqliteVersion: '3.50.0',
    schemaVersion: 1,
    journalMode: 'wal',
    foreignKeys: true,
    fts5Trigram: true
  },
  capabilities: {
    wic: 'not_implemented',
    ffmpeg: 'not_implemented',
    photoModel: 'not_implemented'
  }
}

let client: ServiceClient | undefined

afterEach(async () => {
  await client?.close()
  client = undefined
})

function startFixture(source: string, onExit = vi.fn()): ServiceClient {
  client = new ServiceClient(process.execPath, ['-e', source], onExit)
  return client
}

describe('JSON Lines health client', () => {
  it('forwards actual indexing progress including scanning, counted processing and completion', async () => {
    const fixture = startFixture(`
      const lines = require('node:readline').createInterface({ input: process.stdin });
      let polls = 0;
      lines.on('line', line => {
        const request = JSON.parse(line);
        let result;
        if (request.method === 'search.indexStart') result = { jobId: 'index-job' };
        else if (request.method === 'search.status') {
          polls++;
          result = polls === 1 ? { state: 'running', progress: { phase: 'scanning', processed: 0, discovered: 2, total: null } }
            : polls === 2 ? { state: 'running', progress: { phase: 'indexing', processed: 1, discovered: 2, total: 2 } }
            : { state: 'complete', page: { items: [], nextCursor: null }, progress: { phase: 'complete', processed: 2, discovered: 2, total: 2 } };
        } else return;
        console.log(JSON.stringify({ v: 1, id: request.id, result }));
      });
    `)
    const progress = vi.fn()
    const result = await fixture.request('search.indexDirectories', { directoryPaths: ['C:/photos'] },
      z.object({ items: z.array(z.unknown()), nextCursor: z.null() }), 1000, progress)
    expect(result).toEqual({ result: { items: [], nextCursor: null } })
    expect(progress.mock.calls.map(([value]) => value)).toEqual([
      { phase: 'scanning', processed: 0, discovered: 2, total: null },
      { phase: 'indexing', processed: 1, discovered: 2, total: 2 },
      { phase: 'complete', processed: 2, discovered: 2, total: 2 }
    ])
  })
  it('waits for a directory search job without timing out while health remains responsive', async () => {
    const fixture = startFixture(`
      const lines = require('node:readline').createInterface({ input: process.stdin });
      let polls = 0;
      lines.on('line', line => {
        const request = JSON.parse(line);
        let result;
        if (request.method === 'search.start') result = { jobId: 'job-1' };
        else if (request.method === 'search.status') result = ++polls < 3
          ? { state: 'running' } : { state: 'complete', page: { items: [], nextCursor: null } };
        else if (request.method === 'app.health') result = ${JSON.stringify(health)};
        else return;
        console.log(JSON.stringify({ v: 1, id: request.id, result }));
      });
    `)
    await fixture.health()
    const search = fixture.request('search.filter', { directoryPaths: ['C:\\photos'] },
      z.object({ items: z.array(z.unknown()), nextCursor: z.null() }), 100)
    await expect(fixture.health()).resolves.toMatchObject({ result: health })
    await expect(search).resolves.toEqual({ result: { items: [], nextCursor: null } })
  })
  it('matches overlapping replies by request ID and keeps UTF-8 text', async () => {
    const fixture = startFixture(`
      const lines = require('node:readline').createInterface({ input: process.stdin });
      const requests = [];
      lines.on('line', (line) => {
        const request = JSON.parse(line);
        require('node:assert/strict').deepEqual(
          { v: request.v, method: request.method, params: request.params },
          { v: 1, method: 'app.health', params: {} }
        );
        requests.push(request);
        if (requests.length < 2) return;
        const reply = (id, body) => console.log(JSON.stringify({ v: 1, id, ...body }));
        reply(requests[1].id, {
          error: { code: 'TEST_ERROR', message: '本地服务错误', retryable: false }
        });
        reply(requests[0].id, { result: ${JSON.stringify(health)} });
      });
    `)

    const first = fixture.health()
    const second = fixture.health()
    await expect(second).resolves.toEqual({
      error: { code: 'TEST_ERROR', message: '本地服务错误', retryable: false }
    })
    await expect(first).resolves.toEqual({ result: health })
  })

  it.each([
    'this line is not JSON',
    '{"v":1,"id":"REQUEST_ID","result":{"protocolVersion":2}}'
  ])('rejects an invalid response and stops using that process', async (line) => {
    const fixture = startFixture(`
      process.stdin.once('data', (buffer) => {
        console.log(${JSON.stringify(line)}.replace('REQUEST_ID', JSON.parse(buffer).id));
      });
      process.stdin.resume();
    `)

    await expect(fixture.health()).resolves.toMatchObject({
      error: { code: 'SERVICE_PROTOCOL_ERROR', retryable: true }
    })
    await expect(fixture.health()).resolves.toMatchObject({
      error: { code: 'SERVICE_UNAVAILABLE' }
    })
  })

  it('resolves pending requests with an error and notifies the caller when the subprocess exits', async () => {
    let notifyExit: () => void = () => {}
    const exited = new Promise<void>((resolve) => { notifyExit = resolve })
    const fixture = startFixture(`process.stdin.once('data', () => process.exit(1));`, vi.fn(notifyExit))

    await expect(fixture.health()).resolves.toMatchObject({
      error: { code: 'SERVICE_UNAVAILABLE', retryable: true }
    })
    await exited
  })
})
