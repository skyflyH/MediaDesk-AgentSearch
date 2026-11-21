import { expect, it } from 'vitest'
import { spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { tmpdir } from 'node:os'
import { ServiceClient } from '../src/main/service-client'
import { searchPageSchema } from '../src/shared/search'

it('loads directory indexes without returning search results or adding files to the workspace queue', async () => {
  const folder = mkdtempSync(join(tmpdir(), 'apm-search-index-'))
  const media = join(folder, 'photos')
  mkdirSync(join(media, 'child'), { recursive: true })
  writeFileSync(join(media, 'Indexed-photo.jpg'), Buffer.from([11, 12, 13]))
  writeFileSync(join(media, 'child', 'Indexed-video.mp4'), Buffer.from([21, 22, 23]))
  const client = new ServiceClient('dotnet', [resolve('artifacts/service/MediaLibrary.Service.dll'), '--db', join(folder, 'library.sqlite3')], () => {})
  try {
    const progress: Array<{ phase: string; processed: number; total?: number | null }> = []
    expect(await client.request('search.indexDirectories', { directoryPaths: [media], includeSubdirectories: true }, searchPageSchema, 10000,
      value => progress.push(value)))
      .toEqual({ result: { items: [], nextCursor: null } })
    expect(progress.at(-1)).toMatchObject({ phase: 'complete', processed: 2, total: 2 })
    expect(await client.health()).toHaveProperty('result')
    const response = await client.request('workspace.load', {}, (await import('../src/shared/workspace')).workspaceSchema)
    expect(response).toHaveProperty('result.items', [])
    const search = await client.request('search.filter', { directoryPaths: [media], keywordTerms: ['Indexed'] }, searchPageSchema)
    if ('error' in search) throw Error(search.error.message)
    expect(search.result.items).toHaveLength(2)
    const missing = await client.request('search.indexDirectories', { directoryPaths: [join(folder, 'missing')] }, searchPageSchema)
    expect(missing).toHaveProperty('error.code', 'SOURCE_UNAVAILABLE')
  } finally { await client.close() }
})

it('builds the service and renderer before assembling the WebView2 host', () => {
  const { scripts } = JSON.parse(readFileSync(resolve('package.json'), 'utf8'))
  expect(scripts['build']).toBe('npm run build:webview')
  const stages = scripts['build:webview'].split(' && ')
  expect(stages.slice(0, 3)).toEqual([
    'npm run publish:service:lean', 'npm run build:renderer', 'node scripts/build-webview.mjs'
  ])
  expect(scripts['package:win']).toBe('npm run build && node scripts/package-webview.cjs')
  expect(scripts['package:compare']).toBeUndefined()
})

it('finds files already on disk without requiring import or copying and keeps them out of the workspace queue', () => {
  const folder = mkdtempSync(join(tmpdir(), 'apm-search-files-'))
  const media = join(folder, 'photos')
  mkdirSync(join(media, 'child'), { recursive: true })
  writeFileSync(join(media, '青岛海边日落.JPG'), Buffer.from([1, 2, 3]))
  writeFileSync(join(media, 'child', '青岛旅行.mp4'), Buffer.from([4, 5, 6]))
  writeFileSync(join(media, '青岛笔记.txt'), 'not media')
  const requests = [
    { method: 'search.filter', params: { directoryPaths: [media], keywordTerms: ['青岛'] } },
    { method: 'search.filter', params: { directoryPaths: [media], nameTerms: ['海边日落.jpg'] } },
    { method: 'search.filter', params: { directoryPaths: [media], includeSubdirectories: false } },
    { method: 'workspace.load', params: {} },
    { method: 'asset.import', params: { paths: [join(media, '青岛海边日落.JPG')] } },
    { method: 'workspace.load', params: {} },
    { method: 'search.filter', params: { directoryPaths: [media], keywordTerms: ['不存在的名称'] } }
  ]
  const reply = spawnSync('dotnet', [resolve('artifacts/service/MediaLibrary.Service.dll'), '--db', join(folder, 'library.sqlite3')], {
    input: requests.map((request, index) => JSON.stringify({ v: 1, id: String(index), ...request })).join('\n') + '\n',
    encoding: 'utf8', windowsHide: true, timeout: 10000
  })
  expect(reply.status, reply.stderr).toBe(0)
  const responses = reply.stdout.trim().split(/\r?\n/).map((line) => JSON.parse(line))
  expect(responses[0].result.items.map((item: { fileName: string }) => item.fileName).sort())
    .toEqual(['青岛旅行.mp4', '青岛海边日落.JPG'].sort())
  expect(responses[1].result.items.map((item: { fileName: string }) => item.fileName)).toEqual(['青岛海边日落.JPG'])
  expect(responses[2].result.items).toHaveLength(1)
  expect(responses[3].result.items).toEqual([])
  expect(responses[4].result[0].status).toBe('imported')
  expect(responses[5].result.items).toHaveLength(1)
  expect(responses[6].result.items).toEqual([])
})

it('accepts a directory with all optional search fields left blank over the actual service protocol', () => {
  const folder = mkdtempSync(join(tmpdir(), 'apm-search-defaults-'))
  const request = { v: 1, id: 'directory-defaults', method: 'search.filter',
    params: { directoryPaths: [folder], includeSubdirectories: true } }
  const reply = spawnSync('dotnet', [resolve('artifacts/service/MediaLibrary.Service.dll'), '--db', join(folder, 'library.sqlite3')], {
    input: JSON.stringify(request) + '\n', encoding: 'utf8', windowsHide: true, timeout: 10000
  })
  expect(reply.error).toBeUndefined()
  expect(reply.status, reply.stderr).toBe(0)
  expect(JSON.parse(reply.stdout.trim())).toEqual({ v: 1, id: request.id, result: { items: [], nextCursor: null } })
})

it('finds empty directories by name, respects recursion and result type, and deduplicates overlapping roots', async () => {
  const folder = mkdtempSync(join(tmpdir(), 'apm-search-folders-'))
  const media = join(folder, 'photos')
  const direct = join(media, '青岛旅行')
  const nested = join(direct, '青岛晚霞')
  mkdirSync(nested, { recursive: true })
  mkdirSync(join(media, '其他目录'))
  writeFileSync(join(media, '青岛海边.jpg'), Buffer.from([1, 2, 3]))
  const client = new ServiceClient('dotnet', [resolve('artifacts/service/MediaLibrary.Service.dll'), '--db', join(folder, 'library.sqlite3')], () => {})
  const search = async (fields: object) => {
    const reply = await client.request('search.filter', { directoryPaths: [media], keywordTerms: ['青岛'], ...fields }, searchPageSchema)
    if ('error' in reply) throw Error(reply.error.message)
    return reply.result
  }
  try {
    const onlyFolders = await search({ resultKind: 'directories' })
    expect(onlyFolders.items).toEqual([])
    expect(onlyFolders.directories?.map((item) => item.path.toUpperCase()).sort()).toEqual([direct, nested].map((path) => path.toUpperCase()).sort())
    const shallow = await search({ resultKind: 'directories', includeSubdirectories: false })
    expect(shallow.directories?.map((item) => item.path.toUpperCase())).toEqual([direct.toUpperCase()])
    const files = await search({ resultKind: 'files' })
    expect(files.directories).toBeUndefined()
    expect(files.items.map((item) => item.fileName)).toEqual(['青岛海边.jpg'])
    const all = await search({ resultKind: 'all', directoryPaths: [media, direct] })
    expect(all.directories).toHaveLength(2)
    expect(all.items).toHaveLength(1)
    expect((await search({ resultKind: 'directories', keywordTerms: ['没有此目录'] })).directories).toBeUndefined()
    expect((await search({ resultKind: 'directories', format: 'jpg' })).directories).toBeUndefined()
  } finally { await client.close() }
})

it('searches a larger directory in the background, answers health checks, and discovers newly added files on the next search', async () => {
  const folder = mkdtempSync(join(tmpdir(), 'apm-search-background-'))
  const media = join(folder, 'photos')
  mkdirSync(media)
  for (let index = 0; index < 300; index++) writeFileSync(join(media, `素材-${index}.jpg`), Buffer.from([1, 2, 3]))
  writeFileSync(join(media, '_DSC5039-已增强-降噪-1.jpg'), Buffer.from([4, 5, 6]))
  const client = new ServiceClient('dotnet', [resolve('artifacts/service/MediaLibrary.Service.dll'), '--db', join(folder, 'library.sqlite3')], () => {})
  try {
    await client.health()
    const search = client.request('search.filter', { directoryPaths: [media], keywordTerms: ['_DSC5039-已增强-降噪-1'] }, searchPageSchema)
    expect(await client.health()).toHaveProperty('result')
    const reply = await search
    expect(reply).toHaveProperty('result')
    if ('error' in reply) throw Error(reply.error.message)
    expect(reply.result.items.map((item) => item.fileName)).toEqual(['_DSC5039-已增强-降噪-1.jpg'])
    writeFileSync(join(media, '新增素材.jpg'), Buffer.from([7, 8, 9]))
    const next = await client.request('search.filter', { directoryPaths: [media], nameTerms: ['新增素材'] }, searchPageSchema)
    expect(next).toHaveProperty('result')
    if ('error' in next) throw Error(next.error.message)
    expect(next.result.items.map((item) => item.fileName)).toEqual(['新增素材.jpg'])
  } finally { await client.close() }
})
