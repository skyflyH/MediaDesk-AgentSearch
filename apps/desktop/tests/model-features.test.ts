import { afterEach, expect, it, vi } from 'vitest'
import { randomUUID } from 'node:crypto'
import { mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { basename, dirname, join } from 'node:path'
import { tmpdir } from 'node:os'
import { z } from 'zod'
import { ModelFeatures } from '../src/main/model-features'
import { ModelSettings } from '../src/main/model-settings'
import { defaultModelConfig, saveModelSettingsSchema, type SuggestionKind, type SuggestionState } from '../src/shared/model'
import { draftSchema, type AssetDraft } from '../src/shared/workspace'
import type { Result } from '../src/shared/health'
import type { ModelTransport } from '../src/main/model-agent'

const assetId = randomUUID()
const knownDirectory = randomUUID()
const scopes = { fileNames: true, remarks: true, authors: true, directoryNames: true }
const sample = {
  assetId, fileName: 'V001.mp4', mediaKind: 'video' as const, format: '.mp4', available: true,
  sourcePath: 'D:\\private\\original.mp4',
  createdAt: '2026-11-19T00:00:00Z', capturedAt: null, authors: ['林安'], remarkSummary: '青岛海边日落',
  properties: { sizeBytes: 12000000, focalLengthMm: 85, fNumber: 2.8 },
  matches: [{ field: 'remark', value: '青岛海边日落', terms: ['青岛'] }]
}
const photo = { ...sample, fileName: 'coast.jpg', mediaKind: 'photo', format: '.jpg' }

function fixture(transport: Partial<ModelTransport>, timeout = 1500, send = scopes, photoModel: 'ready' | 'missing' = 'missing') {
  const current: AssetDraft = draftSchema.parse({
    assetId, revision: 0, archiveRevision: 1,
    data: { name: '海边', finalized: true, remark: { intent: 'set', value: 'D:\\private\\secret.mp4' },
      authors: { intent: 'set', value: ['林安'] }, keywords: '青岛、海边、日落', previewPositionMs: 0, directoryId: null }
  })
  const calls: string[] = []
  const local = {
    async request<T>(method: string, args: object, schema: z.ZodType<T>): Promise<Result<T>> {
      calls.push(method)
      let value: unknown
      switch (method) {
        case 'draft.get': value = current; break
        case 'draft.assetContext': value = { ...sample, originalName: sample.fileName, extension: sample.format }; break
        case 'draft.saveRecommendation': {
          const { kind, requestState } = args as { kind: SuggestionKind; requestState: SuggestionState }
          current.data.suggestions = { ...current.data.suggestions, [kind]: requestState }
          current.revision++
          value = current
          break
        }
        case 'archive.listDirectories':
        case 'archive.recommendationDirectories':
          value = [{ directoryId: knownDirectory, rootId: null, displayName: '青岛', relativePath: '.',
            fullPath: 'D:\\private\\青岛', available: true }]
          break
        case 'search.filter':
          value = { items: [(args as { mediaKind?: string }).mediaKind === 'photo' ? photo : sample], nextCursor: null }
          break
        case 'search.photosByScene':
          value = photoModel === 'ready' ? { model: { status: 'ready', version: 'test', message: null },
            items: [{ ...photo, sceneScore: 0.75, matches: [...photo.matches, { field: 'scene', value: '海边晚霞', terms: [] }] }] }
            : { model: { status: 'missing', version: null, message: '未找到本地照片模型。' }, items: [] }
          break
        case 'search.getDetails': value = { asset: sample, path: 'D:\\private\\V001.mp4', remark: sample.remarkSummary,
          remarkState: 'written', authorsState: 'written', captureTimeSource: null }; break
        default: throw Error(method)
      }
      return { result: schema.parse(value) }
    }
  }
  const features = new ModelFeatures(() => local, () => ({
    scopes: send, transport: { suggest: async () => ({}), search: async () => ({}), ...transport }
  }), timeout, vi.fn())
  return { features, current, calls, request: vi.spyOn(local, 'request') }
}

it('keeps a generated remark in the suggestion and rejects extra output fields', async () => {
  const response = vi.fn(async () => ({ draft: '在青岛海边拍摄的日落。' }))
  const app = fixture({ suggest: response })
  app.current.data.remark = { intent: 'set', value: '青岛、海边、日落' }
  app.current.data.keywords = ''
  expect(await app.features.suggest(assetId, 'remark')).toMatchObject({ result: { data: {
    suggestions: { remark: { status: 'ready', remark: '在青岛海边拍摄的日落。' } }
  } } })
  expect(app.current.data.remark.value).toBe('青岛、海边、日落')
  expect(response).toHaveBeenCalledWith('remark', { keywords: '青岛、海边、日落' }, expect.anything())
  app.current.data.remark.value = '  '
  expect(await app.features.suggest(assetId, 'remark')).toMatchObject({ error: { code: 'INVALID_ARGUMENT' } })
  app.current.data.remark.value = '青岛、海边、日落'
  response.mockResolvedValueOnce({ draft: '青岛，海边，日落。' })
  expect(await app.features.suggest(assetId, 'remark')).toMatchObject({ error: { code: 'MODEL_INVALID_OUTPUT' } })

  response.mockResolvedValueOnce({ draft: '原话', extra: true } as { draft: string })
  expect(await app.features.suggest(assetId, 'remark')).toMatchObject({ error: { code: 'MODEL_INVALID_OUTPUT' } })
  expect(app.current.data.suggestions?.remark?.status).toBe('retry')
})

it('generates a batch remark without reading or changing individual drafts', async () => {
  const suggest = vi.fn(async () => ({ draft: '在青岛海边拍摄的日落。' }))
  const app = fixture({ suggest })
  const before = structuredClone(app.current)
  expect(await app.features.suggestBatchRemark('青岛、海边、日落'))
    .toEqual({ result: '在青岛海边拍摄的日落。' })
  expect(suggest).toHaveBeenCalledWith('remark', { keywords: '青岛、海边、日落' }, expect.anything())
  expect(app.calls).toEqual([])
  expect(app.current).toEqual(before)
  suggest.mockResolvedValueOnce({ draft: '青岛、海边、日落' })
  expect(await app.features.suggestBatchRemark('青岛、海边、日落'))
    .toMatchObject({ error: { code: 'MODEL_INVALID_OUTPUT' } })
  expect(await app.features.suggestBatchRemark('  ')).toMatchObject({ error: { code: 'INVALID_ARGUMENT' } })
})

it('returns a retryable timeout and aborts the transport', async () => {
  let signal: AbortSignal | undefined
  const app = fixture({ suggest: async (_kind, _input, incoming) => {
    signal = incoming
    return new Promise(() => {})
  } }, 25)
  expect(await app.features.suggest(assetId, 'remark')).toMatchObject({ error: { code: 'MODEL_UNAVAILABLE' } })
  expect(signal?.aborted).toBe(true)
  expect(app.current.data.suggestions?.remark).toMatchObject({ status: 'retry', errorCode: 'MODEL_UNAVAILABLE' })
})

it('rejects unknown directory IDs and never sends a full path', async () => {
  const app = fixture({ suggest: async (_kind, input) => {
    expect(JSON.stringify(input)).not.toContain('private')
    return { items: [{ directoryId: knownDirectory, reason: '项目相符' }, { directoryId: randomUUID(), reason: '无效' }] }
  } })
  expect(await app.features.suggest(assetId, 'directory')).toMatchObject({ error: { code: 'MODEL_INVALID_OUTPUT' } })
  expect(app.current.data.suggestions?.directory?.candidates).toBeUndefined()
})

it('deduplicates Windows basename suggestions and rejects an extension or reserved name', async () => {
  const suggest = vi.fn(async () => ({ suggestions: ['海边日落', '海边日落', '日落'] }))
  const app = fixture({ suggest })
  expect(await app.features.suggest(assetId, 'name')).toMatchObject({ result: { data: {
    suggestions: { name: { names: ['海边日落', '日落'] } }
  } } })
  for (const name of ['CON', '照片.mp4', '尾随空格 ', 'D:\\青岛']) {
    suggest.mockResolvedValueOnce({ suggestions: [name] })
    expect(await app.features.suggest(assetId, 'name')).toMatchObject({ error: { code: 'MODEL_INVALID_OUTPUT' } })
  }
})

it('supplies the edited remark for filename suggestions and respects the remark sharing setting', async () => {
  for (const remarks of [true, false]) {
    const suggest = vi.fn(async (_kind: SuggestionKind, input: unknown) => {
      expect(input).toMatchObject({ name: '海边', originalName: 'V001.mp4' })
      if (remarks) expect(input).toHaveProperty('remark', '青岛海边拍摄的日落，海浪拍打礁石。')
      else expect(input).not.toHaveProperty('remark')
      return { suggestions: ['青岛海边日落'] }
    })
    const app = fixture({ suggest }, 1500, { ...scopes, remarks })
    app.current.data.remark = { intent: 'set', value: '青岛海边拍摄的日落，海浪拍打礁石。' }
    expect(await app.features.suggest(assetId, 'name')).toMatchObject({ result: { data: {
      suggestions: { name: { names: ['青岛海边日落'], usesRemark: remarks } }
    } } })
    expect(suggest).toHaveBeenCalledOnce()
    expect(app.current.data.name).toBe('海边')
  }
})

it('ignores a recommendation that arrives after an input edit', async () => {
  const app = fixture({ suggest: async () => {
    app.current.revision++
    app.current.data.suggestions = {}
    return { items: [{ directoryId: knownDirectory, reason: '项目相符' }] }
  } })
  expect(await app.features.suggest(assetId, 'directory')).toMatchObject({ error: { code: 'SUGGESTION_STALE' } })
  expect(app.current.data.suggestions?.directory).toBeUndefined()
})

it('checks local result provenance, limits to four tool calls and redacts disabled metadata', async () => {
  const unknown = randomUUID()
  const app = fixture({ search: async (_query, tool) => {
    expect(await tool.details(unknown)).toHaveProperty('error')
    const results = await tool.search({ mediaKind: 'video', author: '林安', remarkTerms: ['青岛'] })
    expect(JSON.stringify(results)).not.toMatch(/V001|remarkSummary|青岛/)
    const details = await tool.details(assetId)
    expect(JSON.stringify(details)).not.toContain('private')
    await tool.search({ mediaKind: 'video' })
    expect(await tool.search({})).toHaveProperty('error')
    return { resultIds: [assetId, unknown], summary: '模型声称的结果', unresolvedConditions: [] }
  } }, 1500, defaultModelConfig.scopes)
  const result = await app.features.search('林安在青岛拍的日落视频')
  expect(result).toMatchObject({ result: {
    items: [{ assetId, fileName: 'V001.mp4', matches: sample.matches }], warning: expect.any(String),
    reasons: [{ assetId, reason: expect.stringContaining('青岛海边日落') }]
  } })
  expect(app.calls).toEqual(['search.filter', 'search.getDetails', 'search.filter'])
})

it('keeps user-selected directory paths local while applying them to intelligent search', async () => {
  const directoryPaths = ['D:\\private\\archive', 'D:\\private\\other']
  const app = fixture({ search: async (query, tool) => {
    expect(query).not.toContain('private')
    const results = await tool.search({ mediaKind: 'video', remarkTerms: ['青岛'] })
    expect(JSON.stringify(results)).not.toContain('private')
    expect(await tool.search({ directoryPaths } as never)).toHaveProperty('error')
    return { resultIds: [assetId], summary: '', unresolvedConditions: [] }
  } })
  const result = await app.features.search('青岛的视频', { directoryPaths, remarkTerms: ['海边'] })
  expect(app.request).toHaveBeenCalledWith('search.filter', {
    directoryPaths, mediaKind: 'video', remarkTerms: ['青岛', '海边'], resultKind: 'files', limit: 15
  }, expect.anything())
  expect(result).toMatchObject({ result: { items: [{ sourcePath: sample.sourcePath }], warning: null } })
  expect(app.calls).not.toContain('archive.listDirectories')
})

it('requires file-level keyword matching instead of returning every file from a matching directory', async () => {
  const app = fixture({ search: async (_query, tools) => {
    expect(await tools.search({ directoryId: knownDirectory } as never)).toHaveProperty('error')
    expect(await tools.search({ resultKind: 'all', keywordTerms: ['海景'] } as never)).toHaveProperty('error')
    await tools.search({ keywordTerms: ['海景'] })
    return { resultIds: [assetId], summary: '', unresolvedConditions: [] }
  } })
  await app.features.search('海景')
  expect(app.request).toHaveBeenCalledWith('search.filter', {
    keywordTerms: ['海景'], resultKind: 'files', limit: 15
  }, expect.anything())
})

it('uses the latest scoped creation date for relative time and searches by creation date', async () => {
  const directoryPaths = ['D:\\private\\archive']
  const dates = { dateField: 'createdAt' as const, dateFrom: '2026-11-01', dateToExclusive: '2026-12-01' }
  const app = fixture({ search: async (query, tool) => {
    expect(JSON.parse(query)).not.toHaveProperty('directories')
    expect(JSON.parse(query)).toHaveProperty('latestCreationDate', new Date(sample.createdAt).toLocaleDateString('sv-SE'))
    expect(JSON.parse(query)).not.toHaveProperty('today')
    expect(await tool.search({ ...dates, directoryId: knownDirectory } as never)).toHaveProperty('error')
    await tool.search({ ...dates, mediaKind: 'photo' })
    return { resultIds: [assetId], summary: '', unresolvedConditions: [] }
  } })
  const result = await app.features.search('找上个月创建的照片', { directoryPaths, resultKind: 'files' })
  expect(app.request).toHaveBeenCalledWith('search.filter', {
    directoryPaths, resultKind: 'files', ...dates, mediaKind: 'photo', limit: 15
  }, expect.anything())
  expect(result).toMatchObject({ result: { items: [{ assetId }], warning: null } })
})

it('passes property ranges to local filtering and exposes real metadata without paths', async () => {
  const propertyRanges = [
    { field: 'sizeBytes' as const, min: 10000000, minExclusive: true },
    { field: 'focalLengthMm' as const, min: 80, max: 90 },
    { field: 'fNumber' as const, max: 2.8 }
  ]
  const app = fixture({ search: async (_query, tools) => {
    const page = await tools.search({ propertyRanges })
    expect(page).toMatchObject({ items: [{ properties: sample.properties }] })
    const details = await tools.details(assetId)
    expect(details).toMatchObject({ properties: sample.properties })
    expect(JSON.stringify(details)).not.toContain('private')
    return { resultIds: [assetId], summary: '', unresolvedConditions: [],
      reasons: [{ assetId, reason: '大小12MB，焦距85mm，光圈f/2.8。' }] }
  } })
  const directoryPaths = ['D:\\private\\archive']
  const result = await app.features.search('大于10MB、焦距80到90mm、光圈不高于2.8的素材', { directoryPaths })
  expect(app.request).toHaveBeenCalledWith('search.filter', expect.objectContaining({
    propertyRanges, directoryPaths, resultKind: 'files'
  }), expect.anything())
  expect(result).toMatchObject({ result: { warning: null, reasons: [{ reason: '大小12MB，焦距85mm，光圈f/2.8。' }] } })
})

it('combines capture date and time with location and subject in the selected directories', async () => {
  const directoryPaths = ['D:\\private\\archive']
  const conditions = {
    mediaKind: 'video' as const, dateField: 'capturedAt' as const,
    dateFrom: '2026-11-19', dateToExclusive: '2026-11-20',
    captureMinuteFrom: 720, captureMinuteToExclusive: 1080, keywordTerms: ['怀化', '火车']
  }
  const app = fixture({ search: async (_query, tools) => {
    await tools.search(conditions)
    return { resultIds: [assetId], summary: '已找到匹配的视频。', unresolvedConditions: [] }
  } })
  const result = await app.features.search('找11月19日下午在怀化拍摄的火车视频', { directoryPaths, resultKind: 'files' })
  expect(app.request).toHaveBeenCalledWith('search.filter', {
    ...conditions, directoryPaths, resultKind: 'files', limit: 15
  }, expect.anything())
  expect(result).toMatchObject({ result: { items: [{ assetId }], warning: null } })
})

it.each([
  ['早上', 6, 12],
  ['中午', 11, 14],
  ['下午', 12, 18],
  ['晚上', 18, 24],
  ['凌晨', 0, 6]
])('finds %s captures across pages using the selected directory and the actual capture time', async (period, start, end) => {
  const directoryPaths = ['D:\\private\\archive']
  const captured = (hour: number, minute = 0) => ({
    ...photo, assetId: randomUUID(), capturedAt: new Date(2026, 10, 19, hour, minute).toISOString()
  })
  const first = captured(start)
  const last = captured(end - 1, 59)
  const search = vi.fn(async () => ({ resultIds: [], summary: '', unresolvedConditions: [] }))
  const app = fixture({ search }, 1500, { ...scopes, directoryNames: false })
  app.request.mockResolvedValueOnce({
    result: { items: [captured(start - 1, 59), captured(end), first, sample], nextCursor: 'page-2' }
  })
  app.request.mockResolvedValueOnce({ result: { items: [last], nextCursor: null } })

  const result = await app.features.search(`帮我找一下该目录拍摄日期的${period}拍的素材`, { directoryPaths, resultKind: 'files' })

  expect(search).not.toHaveBeenCalled()
  expect(app.request).toHaveBeenNthCalledWith(2, 'search.filter', {
    directoryPaths, resultKind: 'files', includeSubdirectories: true, limit: 100, cursor: 'page-2'
  }, expect.anything())
  expect(result).toMatchObject({ result: {
    items: [first, last], warning: null,
    unresolvedConditions: [expect.stringContaining('缺少拍摄时间')]
  } })
})

it('returns the agent conclusion, grounded result reasons and live tool activity for the selected directories', async () => {
  const steps: string[] = []
  const app = fixture({ search: async (_query, tools) => {
    expect(steps.some((step) => step.includes('搜索目录已准备'))).toBe(true)
    await tools.search({ mediaKind: 'video', remarkTerms: ['青岛'] })
    await tools.details(assetId)
    return { resultIds: [assetId], summary: '找到备注包含青岛海边日落的视频。',
      reasons: [{ assetId, reason: '匹配理由: 素材类型为视频，备注明确记录青岛海边日落。' },
        { assetId: randomUUID(), reason: '无来源的结果应忽略' }], unresolvedConditions: [] }
  } })
  const result = await app.features.search('找青岛海边日落视频', { directoryPaths: ['D:\\private\\archive'] }, (step) => steps.push(step))
  expect(result).toMatchObject({ result: {
    summary: '找到备注包含青岛海边日落的视频。',
    reasons: [{ assetId, reason: '素材类型为视频，备注明确记录青岛海边日落。' }], steps
  } })
  if ('result' in result) expect(result.result.reasons).toHaveLength(1)
  expect(steps.some((step) => step.includes('备注：青岛'))).toBe(true)
  expect(steps.some((step) => step.includes('候选素材详情'))).toBe(true)
  expect(JSON.stringify(steps)).not.toContain('private')
  expect(app.calls[0]).toBe('search.filter')
})

it('passes the location remark to the local scene encoder and retains its evidence', async () => {
  const app = fixture({ search: async (_query, tool) => {
    expect(await tool.search({ photoSceneQuery: '海边晚霞', remarkTerms: ['青岛'] })).toMatchObject({
      photoSceneStatus: 'ready', items: [{ assetId }]
    })
    return { resultIds: [assetId], summary: '', unresolvedConditions: [] }
  } }, 1500, scopes, 'ready')
  expect(await app.features.search('青岛海边晚霞照片')).toMatchObject({ result: { items: [{
    assetId, sceneScore: 0.75, matches: [...photo.matches, { field: 'scene', value: '海边晚霞', terms: [] }]
  }] } })
  expect(app.request).toHaveBeenCalledWith('search.photosByScene', {
    query: '海边晚霞', filter: { mediaKind: 'photo', remarkTerms: ['青岛'], resultKind: 'files', limit: 15 }
  }, expect.anything())
  expect(app.calls).not.toContain('search.filter')
})

it('falls back to explicit photo metadata with the missing model notice', async () => {
  const app = fixture({ search: async (_query, tool) => {
    expect(await tool.search({ photoSceneQuery: '海边晚霞', author: '林安' })).toMatchObject({
      photoSceneStatus: 'missing', items: [{ assetId }]
    })
    return { resultIds: [assetId], summary: '', unresolvedConditions: [] }
  } })
  expect(await app.features.search('林安拍的海边晚霞照片')).toMatchObject({ result: {
    items: [{ assetId }], unresolvedConditions: ['未找到本地照片模型。']
  } })
  expect(app.request).toHaveBeenCalledWith('search.filter', {
    mediaKind: 'photo', author: '林安', resultKind: 'files', limit: 15
  }, expect.anything())
})

it('does not call local tools or models when unconfigured', async () => {
  const local = vi.fn()
  const features = new ModelFeatures(local, () => null)
  expect(await features.suggest(assetId, 'name')).toMatchObject({ error: { code: 'MODEL_NOT_CONFIGURED' } })
  expect(await features.search('青岛')).toMatchObject({ error: { code: 'MODEL_NOT_CONFIGURED' } })
  expect(local).not.toHaveBeenCalled()
})

const temporary = new Set<string>()
afterEach(() => {
  const root = realpathSync(tmpdir())
  for (const directory of temporary) {
    const resolved = realpathSync(directory)
    if (dirname(resolved) === root && basename(resolved).startsWith('apm-model-settings-')) rmSync(resolved, { recursive: true })
  }
  temporary.clear()
})

it('does not expose or reuse saved credentials at a different endpoint', async () => {
  const directory = mkdtempSync(join(realpathSync(tmpdir()), 'apm-model-settings-'))
  temporary.add(directory)
  const path = join(directory, 'settings.json')
  const settings = new ModelSettings(path, {
    isAsyncEncryptionAvailable: async () => true,
    encryptStringAsync: async () => Buffer.from('fake-encrypted-value'),
    decryptStringAsync: async () => ({ result: 'dummy-test-key', shouldReEncrypt: false })
  })
  expect(await settings.runtime()).toBeNull()
  const saved = await settings.save({ ...defaultModelConfig, enabled: true, apiKey: 'dummy-test-key' })
  expect(saved).toMatchObject({ configured: true, hasKey: true })
  expect(JSON.stringify(saved)).not.toContain('dummy-test-key')
  expect(readFileSync(path, 'utf8')).not.toContain('dummy-test-key')
  expect((await settings.runtime())?.apiKey).toBe('dummy-test-key')
  expect(await settings.save({ ...defaultModelConfig, enabled: true, apiKey: '', apiMode: 'completions' }))
    .toMatchObject({ configured: true, hasKey: true, apiMode: 'completions' })
  expect((await settings.runtime())?.apiKey).toBe('dummy-test-key')
  expect(await settings.save({ ...defaultModelConfig, enabled: true, baseUrl: 'https://new.example/v1' })).toMatchObject({
    configured: false, hasKey: false
  })
})

it('preserves providers and models across switches and restart without mixing credentials', async () => {
  const directory = mkdtempSync(join(realpathSync(tmpdir()), 'apm-model-settings-'))
  temporary.add(directory)
  const path = join(directory, 'settings.json')
  const secret = {
    isAsyncEncryptionAvailable: async () => true,
    encryptStringAsync: async (value: string) => Buffer.from(`encrypted:${value}`),
    decryptStringAsync: async (value: Buffer) => ({ result: value.toString().slice(10), shouldReEncrypt: false })
  }
  const settings = new ModelSettings(path, secret)
  await settings.save({ ...defaultModelConfig, enabled: true, apiKey: 'first-key' })
  const legacy = JSON.parse(readFileSync(path, 'utf8'))
  delete legacy.providers
  delete legacy.activeProviderId
  writeFileSync(path, JSON.stringify(legacy))
  const migrated = await settings.view()
  expect(migrated.providers).toMatchObject([{ id: 'default', hasKey: true, models: [defaultModelConfig.model] }])
  const providers = [
    { id: 'default', name: 'Cloud A', baseUrl: defaultModelConfig.baseUrl, apiMode: 'completions' as const,
      model: 'model-a', models: ['model-a', 'model-b'] },
    { id: 'second', name: 'Cloud B', baseUrl: 'https://second.example/v1', apiMode: 'responses' as const,
      model: 'model-c', models: ['model-c'], apiKey: 'second-key' },
    { id: 'local', name: 'Local', baseUrl: 'http://localhost:11434/v1', apiMode: 'completions' as const,
      model: 'local-model', models: ['local-model'] }
  ]
  const save = (activeProviderId: string) => settings.save({ ...defaultModelConfig, enabled: true,
    providers, activeProviderId })
  const first = await save('second')
  expect(first).toMatchObject({ model: 'model-c', baseUrl: 'https://second.example/v1', configured: true })
  expect(JSON.stringify(first)).not.toMatch(/first-key|second-key|encryptedKey/)
  expect(readFileSync(path, 'utf8')).not.toMatch(/first-key|second-key/)
  expect(await settings.runtime()).toMatchObject({ model: 'model-c', apiKey: 'second-key', apiMode: 'responses' })
  providers[1]!.apiKey = ''
  providers[0]!.model = 'model-b'
  await save('default')
  expect(await new ModelSettings(path, secret).runtime()).toMatchObject({ model: 'model-b', apiKey: 'first-key' })
  await save('second')
  expect(await settings.runtime()).toMatchObject({ apiKey: 'second-key' })
  providers[1]!.baseUrl = 'https://changed.example/v1'
  expect(await save('second')).toMatchObject({ hasKey: false, configured: false })
  expect(await settings.runtime()).toBeNull()
  await save('local')
  expect(await settings.runtime()).toMatchObject({ model: 'local-model', apiKey: 'local-no-key' })
  providers.splice(1, 1)
  expect((await save('local')).providers).toHaveLength(2)
})

it('saves a completed provider alongside unfinished providers and retains them across restart', async () => {
  const directory = mkdtempSync(join(realpathSync(tmpdir()), 'apm-model-settings-'))
  temporary.add(directory)
  const path = join(directory, 'settings.json')
  const secret = { isAsyncEncryptionAvailable: async () => false,
    encryptStringAsync: async () => Buffer.alloc(0),
    decryptStringAsync: async () => ({ result: '', shouldReEncrypt: false }) }
  const input = { ...defaultModelConfig, enabled: true, model: 'local-model', baseUrl: 'http://localhost:11434/v1',
    apiMode: 'completions' as const, activeProviderId: 'ready', providers: [
      { id: 'unfinished', name: 'Unfinished', baseUrl: 'http://localhost:11434/v1', apiMode: 'completions' as const, model: '', models: [] },
      { id: 'ready', name: 'Ready', baseUrl: 'http://localhost:11434/v1', apiMode: 'completions' as const, model: 'local-model', models: ['local-model'] }
    ] }
  expect(saveModelSettingsSchema.safeParse(input).success).toBe(true)
  await new ModelSettings(path, secret).save(saveModelSettingsSchema.parse(input))
  const reopened = new ModelSettings(path, secret)
  expect((await reopened.view()).providers).toMatchObject([{ id: 'unfinished', models: [] }, { id: 'ready', models: ['local-model'] }])
  expect(await reopened.runtime()).toMatchObject({ model: 'local-model', apiKey: 'local-no-key' })
  expect(saveModelSettingsSchema.safeParse({ ...input, activeProviderId: 'unfinished' }).success).toBe(false)
})
