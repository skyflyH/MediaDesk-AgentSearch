import { randomUUID } from 'node:crypto'
import { z } from 'zod'
import { directorySchema } from '../shared/archive'
import type { Result } from '../shared/health'
import { modelStatus, type ModelConfig, type SuggestionKind, type SuggestionState } from '../shared/model'
import { searchDetailsSchema, searchFilterSchema, searchPageSchema, type AgentSearchResult, type SearchFilter, type SearchItem } from '../shared/search'
import { assetSchema, draftSchema, type AssetDraft } from '../shared/workspace'
import { ModelOutputError, modelOutput, type ModelTransport, type ReadTools, toolFilter } from './model-agent'
import type { ServiceClient } from './service-client'
import { hasPhotoConstraints, photoSceneResult } from '../shared/photos'

type Local = Pick<ServiceClient, 'request'>
type RuntimeModel = { transport: ModelTransport; scopes: ModelConfig['scopes'] }
type ModelCode = 'MODEL_NOT_CONFIGURED' | 'MODEL_INVALID_OUTPUT' | 'MODEL_UNAVAILABLE'
const pathsInText = /(?:\b[a-z]:[\\/]|\\\\|file:\/\/)/i
const mediaExtension = /\.(?:jpe?g|png|heic|heif|webp|gif|bmp|tiff?|mp4|mov|m4v|webm|mkv|avi)$/i

export function redact(value: string): string {
  return pathsInText.test(value) ? '[本地路径已省略]' : value
}

function modelError<T>(code: ModelCode): Result<T> {
  return { error: { code, message: modelStatus(code), retryable: true } }
}

function legalName(value: string, extension: string) {
  const prefix = value.split('.')[0]?.trimEnd().toUpperCase() ?? ''
  return value.length > 0 && value.length + extension.length <= 255 && !/^[\s]*$|[. ]$|[<>:"/\\|?*\x00-\x1f]/.test(value) &&
    !/^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[1-9¹²³])$/.test(prefix) && !mediaExtension.test(value)
}

const invalidOutput = (error: unknown) => error instanceof ModelOutputError || error instanceof z.ZodError
type CaptureHours = readonly [start: number, end: number]
const captureHours = new Map<string, CaptureHours>([
  ['凌晨', [0, 6]],
  ['早上', [6, 12]], ['上午', [6, 12]],
  ['中午', [11, 14]],
  ['下午', [12, 18]],
  ['晚上', [18, 24]], ['夜间', [18, 24]]
])
const capturePeriodQuery = new RegExp([
  '^(?:请)?(?:帮我|帮忙)?',
  '(?:(?:找|查)(?:找|查)?(?:一下)?)?',
  '(?:(?:在)?(?:该|这个|所选|当前)目录(?:内|里|中|下)?(?:中)?)?(?:的)?',
  '(?:拍摄日期(?:的|是)?)?',
  `(?<period>${[...captureHours.keys()].join('|')})`,
  '(?:拍摄|拍)(?:的)?(?<media>素材|照片|图片|视频|文件)[。！？?!]?$'
].join(''), 'u')
const captureClock = new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })

function formatHour(hour: number) {
  return `${String(hour).padStart(2, '0')}:00`
}

export class ModelFeatures {
  constructor(
    private readonly local: () => Local,
    private readonly getModel: () => RuntimeModel | null | Promise<RuntimeModel | null>,
    private readonly timeoutMs = 20_000,
    private readonly log = (kind: string, elapsed: number, code: string) => console.info('[model]', kind, elapsed, code)
  ) {}

  async suggest(assetId: string, kind: SuggestionKind): Promise<Result<AssetDraft>> {
    let model: RuntimeModel | null
    try { model = await this.getModel() } catch { return modelError('MODEL_UNAVAILABLE') }
    if (!model) return modelError('MODEL_NOT_CONFIGURED')
    const initial = await this.draft(assetId)
    if ('error' in initial) return initial
    const draft = initial.result
    const data = draft.data
    const asset = await this.local().request('draft.assetContext', { assetId }, assetSchema)
    if ('error' in asset) return asset
    const scopes = model.scopes
    const state: SuggestionState = {
      requestId: randomUUID(), status: 'pending', usesRemark: kind === 'remark' || scopes.remarks,
      usesAuthors: scopes.authors && kind === 'directory'
    }

    if (kind === 'remark' && !data.remark.value.trim()) {
      return { error: { code: 'INVALID_ARGUMENT', message: '请输入备注内容或关键词。', retryable: false } }
    }
    const pending = await this.saveState(draft, kind, state)
    if ('error' in pending) return pending

    const start = Date.now()
    let payload: Partial<SuggestionState>
    let code: ModelCode | undefined
    try {
      const metadata = { mediaKind: asset.result.mediaKind, format: asset.result.extension, createdAt: asset.result.createdAt }
      const remark = scopes.remarks ? { remark: redact(data.remark.value) } : {}
      const names = scopes.fileNames ? { name: redact(data.name), originalName: redact(asset.result.originalName) } : {}
      const authors = scopes.authors ? { authors: data.authors.value.map(redact) } : {}
      if (kind === 'name') {
        const raw = await this.timed((signal) => model.transport.suggest(kind, { ...metadata, ...remark, ...names }, signal))
        const seen = new Set<string>()
        const suggestions = modelOutput.name.parse(raw).suggestions.map((item) => item.trimStart())
        if (suggestions.some((item) => !legalName(item, asset.result.extension))) throw new ModelOutputError()
        payload = { names: suggestions.filter((item) => {
          const key = item.normalize('NFKC').toUpperCase()
          if (seen.has(key)) return false
          seen.add(key)
          return true
        }) }
      } else if (kind === 'remark') {
        payload = { remark: await this.generateRemark(model, data.remark.value) }
      } else {
        const directories = await this.local().request('archive.recommendationDirectories', { assetId }, z.array(directorySchema))
        if ('error' in directories) throw Error('Local directory listing failed')
        const candidates = directories.result.filter((directory) => directory.available)
          .slice(0, 30).map((directory) => ({ directoryId: directory.directoryId,
            ...(scopes.directoryNames ? { name: redact(localDirectoryName(directory.displayName)) } : {}) }))
        const allowed = new Set(candidates.map((candidate) => candidate.directoryId))
        const raw = candidates.length && scopes.directoryNames
          ? await this.timed((signal) => model.transport.suggest(kind, { ...metadata, ...names, ...remark, ...authors, candidates }, signal))
          : { items: [] }
        const items = modelOutput.directory.parse(raw).items
        if (items.some((item) => !allowed.has(item.directoryId)) || new Set(items.map((item) => item.directoryId)).size !== items.length)
          throw new ModelOutputError()
        payload = { candidates: items.map((item) => ({ ...item, reason: redact(item.reason) })) }
      }
    } catch (error) {
      code = invalidOutput(error) ? 'MODEL_INVALID_OUTPUT' : 'MODEL_UNAVAILABLE'
      payload = { errorCode: code }
    }
    this.log(kind, Date.now() - start, code ?? 'ok')
    const saved = await this.finishState(assetId, kind, { ...state, ...payload, status: code ? 'retry' : 'ready' })
    if ('error' in saved && saved.error.code === 'DESTINATION_UNAVAILABLE') {
      await this.finishState(assetId, kind, { ...state, status: 'retry', errorCode: 'MODEL_INVALID_OUTPUT' })
      return modelError('MODEL_INVALID_OUTPUT')
    }
    if ('error' in saved) return saved
    return code ? modelError(code) : saved
  }

  async search(query: string, constraints: SearchFilter = {}, onStep: (step: string) => void = () => {}): Promise<Result<AgentSearchResult>> {
    const steps: string[] = []
    const report = (step: string) => { steps.push(step); onStep(step) }
    const period = capturePeriodQuery.exec(query.replace(/\s+/gu, ''))?.groups
    const hours = captureHours.get(period?.period ?? '')
    if (constraints.directoryPaths?.length && period && hours) {
      const mediaKind = period.media === '视频' ? 'video'
        : period.media === '照片' || period.media === '图片' ? 'photo' : undefined
      return this.searchCapturesInPeriod(hours, {
        ...(mediaKind ? { mediaKind } : {}), ...constraints
      }, steps, report)
    }
    report('正在检查模型连接配置和搜索范围。')
    let model: RuntimeModel | null
    try { model = await this.getModel() } catch { return modelError('MODEL_UNAVAILABLE') }
    if (!model) return modelError('MODEL_NOT_CONFIGURED')
    let latestCreationDate: string | null = null
    if (constraints.directoryPaths?.length) {
      report(`正在准备 ${constraints.directoryPaths.length} 个搜索目录的文件和属性索引。`)
      const prepared = await this.local().request('search.filter', {
        directoryPaths: constraints.directoryPaths, includeSubdirectories: constraints.includeSubdirectories,
        resultKind: 'files', limit: 1
      }, searchPageSchema)
      if ('error' in prepared) return prepared
      const latest = prepared.result.items[0]
      if (latest) latestCreationDate = new Date(latest.createdAt).toLocaleDateString('sv-SE')
    }
    report('搜索目录已准备，Agent 正在解析要求并选择检索条件。')
    const start = Date.now()
    const sources = new Map<string, SearchItem>()
    let calls = 0
    let searched = false
    let sceneMessage = ''
    let active = true
    const scopes = model.scopes
    const tools: ReadTools = {
      search: async (input) => {
        if (!active || ++calls > 4) return { error: '本次工具次数已用完。' }
        const parsed = toolFilter.safeParse(input)
        if (!parsed.success) return { error: '筛选条件无效。' }
        const { photoSceneQuery, ...fields } = parsed.data
        if (queryContainsPathCriteria(fields)) return { error: '筛选条件无效。' }
        const nameTerms = [...new Set([...(fields.nameTerms ?? []), ...(constraints.nameTerms ?? [])])]
        const remarkTerms = [...new Set([...(fields.remarkTerms ?? []), ...(constraints.remarkTerms ?? [])])]
        const filter = searchFilterSchema.safeParse({
          ...fields, ...constraints,
          ...(nameTerms.length ? { nameTerms } : {}),
          ...(remarkTerms.length ? { remarkTerms } : {}),
          ...((fields.propertyRanges?.length || constraints.propertyRanges?.length) ? {
            propertyRanges: [...(fields.propertyRanges ?? []), ...(constraints.propertyRanges ?? [])]
          } : {}),
          resultKind: 'files',
          limit: Math.min(fields.limit ?? 15, 20)
        })
        if (!filter.success) return { error: '筛选条件无效。' }
        const labels = [fields.mediaKind === 'photo' ? '照片' : fields.mediaKind === 'video' ? '视频' : '',
          scopes.fileNames && fields.nameTerms?.length ? `文件名：${fields.nameTerms.map(redact).join('、')}` : '',
          scopes.remarks && fields.remarkTerms?.length ? `备注：${fields.remarkTerms.map(redact).join('、')}` : '',
          fields.keywordTerms?.length ? `文件名或备注：${fields.keywordTerms.map(redact).join('、')}` : '',
          filter.data.captureMinuteFrom !== undefined || filter.data.captureMinuteToExclusive !== undefined ? '按拍摄时段筛选' : '',
          filter.data.propertyRanges?.length ? '按文件及拍摄属性筛选' : '',
          fields.dateFrom ? `起始日期：${fields.dateFrom}` : '', fields.dateToExclusive ? `截止日期：${fields.dateToExclusive}` : ''].filter(Boolean)
        report(`第 ${calls} 次检索：${labels.join('；') || '按当前筛选条件查找候选素材'}。`)
        const wantsScene = !!photoSceneQuery && filter.data.mediaKind !== 'video'
        let sceneStatus: string | undefined
        let items: SearchItem[] | undefined
        let nextCursor: string | null = null
        if (wantsScene) {
          const { cursor: _cursor, ...constraints } = filter.data
          const photoFilter = { ...constraints, mediaKind: 'photo' as const }
          const scene = await this.local().request('search.photosByScene', {
            query: photoSceneQuery, filter: photoFilter
          }, photoSceneResult)
          if ('result' in scene) {
            sceneStatus = scene.result.model.status
            if (sceneStatus === 'ready') items = scene.result.items
            else sceneMessage = scene.result.model.message ?? '本地照片画面索引暂不可用。'
          } else {
            sceneStatus = 'invalid'
            sceneMessage = '本地照片画面索引暂不可用。'
          }
          if (!items && !hasPhotoConstraints(photoFilter)) {
            searched = true
            return { items: [], nextCursor: null, photoSceneStatus: sceneStatus }
          }
        }
        if (!items) {
          const fallback = wantsScene ? { ...filter.data, mediaKind: 'photo', cursor: undefined } : filter.data
          const reply = await this.local().request('search.filter', fallback, searchPageSchema)
          if ('error' in reply) return { error: '本地筛选失败，请检查条件。' }
          items = reply.result.items
          nextCursor = reply.result.nextCursor
        }
        searched = true
        report(`本次查询找到 ${items.length} 项候选素材${nextCursor ? '，还有更多候选' : ''}，正在核对匹配条件。`)
        for (const item of items) {
          const prior = sources.get(item.assetId)
          if (!prior) sources.set(item.assetId, item)
          else {
            const matches = [...prior.matches, ...item.matches]
            sources.set(item.assetId, { ...item, sceneScore: item.sceneScore ?? prior.sceneScore,
              matches: [...new Map(matches.map((match) => [JSON.stringify(match), match])).values()] })
          }
        }
        return { items: items.map((item) => safeSearchItem(item, scopes)), nextCursor, photoSceneStatus: sceneStatus }
      },
      details: async (id) => {
        if (!active || ++calls > 4) return { error: '本次工具次数已用完。' }
        if (!sources.has(id)) return { error: '只有本轮已命中的素材可以查看详情。' }
        report('正在读取候选素材详情，核对文件属性和备注。')
        const reply = await this.local().request('search.getDetails', { assetId: id }, searchDetailsSchema)
        if ('error' in reply) return { error: '素材详情不可用。' }
        return { ...safeSearchItem(reply.result.asset, scopes),
          ...(scopes.remarks ? { remark: redact(reply.result.remark).slice(0, 1000) } : {}) }
      }
    }

    let code: ModelCode | undefined
    let resultIds: string[] = []
    let unresolvedConditions: string[] = []
    let summary = ''
    let modelReasons = new Map<string, string>()
    try {
      const input = JSON.stringify({ query: redact(query), latestCreationDate,
        timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone })
      const output = modelOutput.search.parse(await this.timed((signal) => model.transport.search(input, tools, signal)))
      resultIds = [...new Set(output.resultIds.filter((id) => sources.has(id)))]
      summary = redact(output.summary)
      modelReasons = new Map((output.reasons ?? []).filter((item) => resultIds.includes(item.assetId))
        .map((item) => [item.assetId, redact(item.reason.trim().replace(/^匹配理由\s*[:：]\s*/u, ''))]))
      unresolvedConditions = output.unresolvedConditions.map(redact)
      if (!searched || output.resultIds.some((id) => !sources.has(id))) code = 'MODEL_INVALID_OUTPUT'
    } catch (error) {
      code = invalidOutput(error) ? 'MODEL_INVALID_OUTPUT' : 'MODEL_UNAVAILABLE'
    } finally {
      active = false
    }
    this.log('search', Date.now() - start, code ?? 'ok')
    if (code && !sources.size) return modelError(code)
    if (sceneMessage) unresolvedConditions.push(sceneMessage)
    const ids = code && !resultIds.length ? [...sources.keys()].slice(0, 20) : resultIds
    const items = ids.map((id) => sources.get(id)!)
    const reasons = items.map((item) => {
      const evidence = item.matches.filter((match) => match.field !== 'directory').map((match) =>
        `${match.field === 'remark' ? '备注' : match.field === 'name' ? '文件名' : match.field === 'author' ? '创作人' : '属性'}「${redact(match.value)}」`)
      return {
        assetId: item.assetId,
        reason: (!code && modelReasons.get(item.assetId)) || (evidence.length
          ? `本地检索匹配：${evidence.join('；')}。`
          : '该素材位于本次搜索范围内，暂无可展示的匹配依据。')
      }
    })
    report(`检索完成，返回 ${items.length} 项结果${code ? '；模型未完成核对，以下为本地候选' : ''}。`)
    return { result: { items, unresolvedConditions: [...new Set(unresolvedConditions)],
      summary: code ? 'Agent 未完成核对，下列候选尚不能确认符合全部要求。' : summary || (items.length ? `本地查询返回 ${items.length} 项素材。` : '本地查询没有找到符合条件的素材。'),
      reasons, steps,
      warning: code ? modelStatus(code) + ' 下列素材来自本次本地查询。' : null } }
  }

  async suggestBatchRemark(text: string): Promise<Result<string>> {
    if (!text.trim() || text.length > 4000)
      return { error: { code: 'INVALID_ARGUMENT', message: '请输入备注内容或关键词，最多 4000 字。', retryable: false } }
    try {
      const model = await this.getModel()
      if (!model) return modelError('MODEL_NOT_CONFIGURED')
      return { result: await this.generateRemark(model, text) }
    } catch (error) {
      return modelError(invalidOutput(error) ? 'MODEL_INVALID_OUTPUT' : 'MODEL_UNAVAILABLE')
    }
  }

  private async generateRemark(model: RuntimeModel, text: string): Promise<string> {
    const keywords = redact(text.trim())
    const raw = await this.timed((signal) => model.transport.suggest('remark', { keywords }, signal))
    const description = redact(modelOutput.remark.parse(raw).draft.trim())
    const terms = keywords.split(/[、，,；;\s]+/u).filter(Boolean)
    if (terms.length > 1 && description.replace(/[、，,；;。.!！?？\s]+/gu, '') === terms.join(''))
      throw new ModelOutputError()
    return description
  }

  private async searchCapturesInPeriod([start, end]: CaptureHours, constraints: SearchFilter,
    steps: string[], report: (step: string) => void): Promise<Result<AgentSearchResult>> {
    report('正在所选目录中核对素材的拍摄时间。')
    const items: SearchItem[] = []
    const reasons: NonNullable<AgentSearchResult['reasons']> = []
    let missingCaptureTime = 0
    let cursor: string | undefined
    do {
      const reply = await this.local().request('search.filter', {
        ...constraints, resultKind: 'files', includeSubdirectories: constraints.includeSubdirectories ?? true,
        limit: 100, ...(cursor ? { cursor } : {})
      }, searchPageSchema)
      if ('error' in reply) return reply
      for (const item of reply.result.items) {
        if (!item.capturedAt) {
          missingCaptureTime++
          continue
        }
        const captured = new Date(item.capturedAt)
        const hour = captured.getHours()
        if (hour < start || hour >= end) continue
        items.push(item)
        reasons.push({ assetId: item.assetId, reason: `本地拍摄时间为 ${captureClock.format(captured)}。` })
      }
      cursor = reply.result.nextCursor ?? undefined
    } while (cursor)

    const unresolvedConditions = []
    if (missingCaptureTime) unresolvedConditions.push(`已检查的素材中有 ${missingCaptureTime} 项缺少拍摄时间，无法核对拍摄时段。`)
    report(`拍摄时间核对完成，返回 ${items.length} 项素材。`)
    const range = `本地时间 ${formatHour(start)} 至 ${formatHour(end)} 前`
    return { result: {
      items, reasons, steps, warning: null, unresolvedConditions,
      summary: items.length ? `找到 ${items.length} 项在${range}拍摄的素材。`
        : `在所选目录中没有找到拍摄时间可确认为${range}的素材。`
    } }
  }

  private draft(assetId: string) {
    return this.local().request('draft.get', { assetId }, draftSchema)
  }

  private saveState(draft: AssetDraft, kind: SuggestionKind, requestState: SuggestionState) {
    return this.local().request('draft.saveRecommendation', {
      assetId: draft.assetId, expectedRevision: draft.revision, kind, requestState
    }, draftSchema)
  }

  private async finishState(id: string, kind: SuggestionKind, state: SuggestionState): Promise<Result<AssetDraft>> {
    for (let attempt = 0; attempt < 3; attempt++) {
      const latest = await this.draft(id)
      if ('error' in latest) return latest
      if (latest.result.data.suggestions?.[kind]?.requestId !== state.requestId) break
      const result = await this.saveState(latest.result, kind, state)
      if ('result' in result || result.error.code !== 'REVISION_CONFLICT') return result
    }
    return { error: { code: 'SUGGESTION_STALE', message: '输入已变化，请重新生成建议。', retryable: true } }
  }

  private async timed<T>(run: (signal: AbortSignal) => Promise<T>): Promise<T> {
    const cancellation = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined
    try {
      return await Promise.race([
        new Promise<never>((_, reject) => {
          timer = setTimeout(() => { reject(Error('Model timeout')); cancellation.abort() }, this.timeoutMs)
        }),
        run(cancellation.signal)
      ])
    } finally {
      clearTimeout(timer)
    }
  }
}

function localDirectoryName(name: string) {
  return !pathsInText.test(name) && !name.includes('\\') && !name.includes('/') ? name : '归档根目录'
}

function queryContainsPathCriteria(filter: z.infer<typeof searchFilterSchema>) {
  return [...(filter.nameTerms ?? []), ...(filter.remarkTerms ?? []), ...(filter.keywordTerms ?? []), filter.author ?? '']
    .some((text) => pathsInText.test(text))
}

function safeSearchItem(item: SearchItem, scopes: ModelConfig['scopes']) {
  return {
    assetId: item.assetId, mediaKind: item.mediaKind, format: item.format, createdAt: item.createdAt,
    capturedAt: item.capturedAt, available: item.available, sceneScore: item.sceneScore, properties: item.properties,
    ...(scopes.fileNames ? { fileName: redact(item.fileName) } : {}),
    ...(scopes.authors ? { authors: item.authors.map(redact) } : {}),
    ...(scopes.remarks ? { remarkSummary: redact(item.remarkSummary) } : {}),
    matches: item.matches.filter(({ field }) => field !== 'directory' &&
      (field !== 'name' || scopes.fileNames) && (field !== 'remark' || scopes.remarks) &&
      (field !== 'author' || scopes.authors)).map((match) => ({
        field: match.field, value: redact(match.value).slice(0, 400), terms: match.terms.map(redact)
      }))
  }
}
