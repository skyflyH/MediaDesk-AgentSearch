import { afterEach, expect, it, vi } from 'vitest'
import { nextTick, ref } from 'vue'
import type { DesktopApi, Result } from '../src/shared/health'
import type { AssetDraft, DraftData } from '../src/shared/workspace'
import type { ArchiveStatus } from '../src/shared/archive'
import type { ModelConnection } from '../src/shared/model'
import { useModelSuggestions } from '../src/renderer/useModelSuggestions'

const lifecycle = vi.hoisted(() => ({ unmounted: [] as Array<() => void> }))
vi.mock('vue', async (original) => ({
  ...await original<typeof import('vue')>(), onMounted: vi.fn(),
  onUnmounted: (callback: () => void) => lifecycle.unmounted.push(callback)
}))
afterEach(() => { lifecycle.unmounted.splice(0).forEach(callback => callback()) })
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(accept => { resolve = accept })
  return { promise, resolve }
}
function makeDraft(id: string): AssetDraft {
  return { assetId: id, revision: 1, archiveRevision: 1, data: { name: id, finalized: false,
    remark: { intent: 'set', value: 'remark' }, authors: { intent: 'unchanged', value: [] },
    keywords: '', directoryId: null, previewPositionMs: 0 } }
}
function setup() {
  const draft = ref<AssetDraft | null>(makeDraft('one'))
  const edited = ref<DraftData | null>(draft.value!.data)
  const api = { modelSuggest: vi.fn<DesktopApi['modelSuggest']>(),
    getModelSettings: vi.fn<DesktopApi['getModelSettings']>(),
    checkModelConnection: vi.fn<DesktopApi['checkModelConnection']>() }
  const dependencies = { draft, edited, activeArchive: ref<ArchiveStatus>(),
    flush: vi.fn(async () => true), refreshDraft: vi.fn(async () => {}) }
  return { api, ...dependencies,
    model: useModelSuggestions(api as unknown as DesktopApi, dependencies) }
}

it('ignores errors for a previous asset and clears generation only for the completed request', async () => {
  const { api, draft, model, refreshDraft } = setup()
  const first = deferred<Awaited<ReturnType<DesktopApi['modelSuggest']>>>()
  const second = deferred<Awaited<ReturnType<DesktopApi['modelSuggest']>>>()
  api.modelSuggest.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
  const oldRequest = model.generate('name')
  await nextTick()
  expect(model.generating('name')).toBe(true)
  draft.value = makeDraft('two')
  const newRequest = model.generate('name')
  await nextTick()
  first.resolve({ error: { code: 'MODEL_UNAVAILABLE', message: 'old error', retryable: true } })
  await oldRequest
  expect(model.suggestionMessage('name')).toBe('')
  expect(model.generating('name')).toBe(true)
  expect(refreshDraft).toHaveBeenCalledWith('one')
  second.resolve({ error: { code: 'MODEL_UNAVAILABLE', message: 'current error', retryable: true } })
  await newRequest
  expect(model.suggestionMessage('name')).toBe('current error')
  expect(model.generating('name')).toBe(false)
})

it('does not request a model result if flushing fails or selection changes while flushing', async () => {
  const { api, draft, flush, model } = setup()
  flush.mockResolvedValueOnce(false)
  await model.generate('name')
  expect(api.modelSuggest).not.toHaveBeenCalled()
  const saving = deferred<boolean>()
  flush.mockReturnValueOnce(saving.promise)
  const request = model.generate('name')
  draft.value = makeDraft('two')
  saving.resolve(true)
  await request
  expect(api.modelSuggest).not.toHaveBeenCalled()
})

it('hides saved suggestions when their input has changed without mutating the draft', () => {
  const { draft, edited, model } = setup()
  draft.value!.data.suggestions = { name: { requestId: 'r', status: 'ready', usesRemark: true,
    usesAuthors: false, names: ['suggestion'] } }
  edited.value = { ...draft.value!.data, remark: { intent: 'set', value: 'changed' } }
  expect(model.suggestions.value.name).toBeUndefined()
  expect(draft.value!.data.suggestions.name?.names).toEqual(['suggestion'])
})

it('keeps the latest connection response and ignores a pending request after unmount', async () => {
  const { api, model } = setup()
  const first = deferred<Result<ModelConnection>>()
  const second = deferred<Result<ModelConnection>>()
  model.modelConnection.value = { model: 'existing', state: 'connected' }
  api.checkModelConnection.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
  const oldRequest = model.checkModelConnection()
  const newRequest = model.checkModelConnection()
  second.resolve({ result: { model: 'new', state: 'connected' } })
  await newRequest
  first.resolve({ result: { model: 'old', state: 'error' } })
  await oldRequest
  expect(model.modelConnection.value.model).toBe('new')
  const pending = deferred<Result<ModelConnection>>()
  api.checkModelConnection.mockReturnValueOnce(pending.promise)
  const request = model.checkModelConnection()
  lifecycle.unmounted.splice(0).forEach(callback => callback())
  pending.resolve({ result: { model: 'unmounted', state: 'error' } })
  await request
  expect(model.modelConnection.value.model).toBe('new')
})
