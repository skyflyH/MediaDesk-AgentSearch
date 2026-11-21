import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { nextTick, ref } from 'vue'
import type { DesktopApi, Result } from '../src/shared/health'
import type { Asset, AssetDraft } from '../src/shared/workspace'
import type { ArchiveStatus } from '../src/shared/archive'
import { useArchiveOperations } from '../src/renderer/useArchiveOperations'

const lifecycle = vi.hoisted(() => ({ mounted: [] as Array<() => void>, unmounted: [] as Array<() => void> }))
vi.mock('vue', async (original) => ({
  ...await original<typeof import('vue')>(),
  onMounted: (callback: () => void) => lifecycle.mounted.push(callback),
  onUnmounted: (callback: () => void) => lifecycle.unmounted.push(callback)
}))
beforeEach(() => {
  vi.useFakeTimers()
  vi.stubGlobal('document', Object.assign(new EventTarget(), { visibilityState: 'visible' }))
})
afterEach(() => {
  lifecycle.unmounted.splice(0).forEach(callback => callback())
  lifecycle.mounted.length = 0
  vi.useRealTimers()
  vi.unstubAllGlobals()
})
const status = { operationId: 'op', assetId: 'asset', phase: 'copying', actualPath: null } as ArchiveStatus
function setup() {
  const api = {
    listDirectories: vi.fn(async () => ({ result: [] })),
    archivePreview: vi.fn(async () => ({ result: { planToken: 'token', finalPath: 'copy.jpg', sourcePath: 'original.jpg' } })),
    archiveCommit: vi.fn<DesktopApi['archiveCommit']>().mockResolvedValue({ result: status }),
    archiveStatus: vi.fn<DesktopApi['archiveStatus']>().mockResolvedValue({ result: status })
  }
  const player = { closePreview: vi.fn(async () => {}), reopen: vi.fn(async () => {}) }
  const dependencies = {
    asset: ref<Asset>(), draft: ref<AssetDraft | null>({ assetId: 'asset', archiveRevision: 3, data: { directoryId: 'dir' } } as AssetDraft),
    archiveLog: ref<ArchiveStatus[]>([]), saveState: ref('saved'), saveError: ref('draft failed'), disabled: ref(false),
    flush: vi.fn(async () => true), operate: async (task: () => Promise<void>) => task(), edit: vi.fn(),
    reload: vi.fn(async () => {}), refreshDraft: vi.fn(async () => {}), getPreview: () => player,
    directoryDraftId: () => 'asset', showSaveError: vi.fn(async () => {}), showSaveSuccess: vi.fn(async () => {})
  }
  const batch = { batchItems: ref<Asset[]>([{ assetId: 'asset' } as Asset]), batchIds: ref(['asset']),
    batchDrafts: ref(new Map<string, AssetDraft>()), batchMessage: ref(''), batchBlocked: ref(false),
    batchErrors: ref(new Map<string, string>()), rememberBatchDraft: vi.fn() }
  const operations = useArchiveOperations(api as unknown as DesktopApi, dependencies, batch)
  lifecycle.mounted.splice(0).forEach(callback => callback())
  return { api, player, dependencies, batch, operations }
}

it('blocks single and batch copying when draft flush fails, retaining batch selection', async () => {
  const { api, player, dependencies, batch, operations } = setup()
  dependencies.flush.mockResolvedValue(false)
  await operations.saveAs()
  expect(dependencies.showSaveError).toHaveBeenCalledWith('draft failed')
  await operations.copyBatch('dir', {})
  expect(api.archivePreview).not.toHaveBeenCalled()
  expect(api.archiveCommit).not.toHaveBeenCalled()
  expect(player.closePreview).not.toHaveBeenCalled()
  expect(batch.batchIds.value).toEqual(['asset'])
  expect(batch.batchBlocked.value).toBe(true)
})

it('flushes before planning and releases the preview before committing', async () => {
  const { api, player, dependencies, operations } = setup()
  await operations.saveAs()
  expect(api.archivePreview).toHaveBeenCalledWith('asset', 3, 'dir')
  expect(api.archiveCommit).toHaveBeenCalledWith(expect.any(String), 'token')
  expect(dependencies.flush.mock.invocationCallOrder[0]!).toBeLessThan(api.archivePreview.mock.invocationCallOrder[0]!)
  expect(player.closePreview.mock.invocationCallOrder[0]!).toBeLessThan(api.archiveCommit.mock.invocationCallOrder[0]!)
})

it('reuses the operation and token for a retry, then replans when the archive revision changes', async () => {
  const { api, dependencies, operations } = setup()
  api.archiveCommit.mockResolvedValue({ error: { code: 'TEMPORARY', message: 'try again', retryable: true } })
  await operations.saveAs()
  await operations.saveAs()
  expect(api.archivePreview).toHaveBeenCalledTimes(1)
  expect(api.archiveCommit.mock.calls[1]).toEqual(api.archiveCommit.mock.calls[0])
  dependencies.draft.value = { ...dependencies.draft.value!, archiveRevision: 4 }
  await operations.saveAs()
  expect(api.archivePreview).toHaveBeenLastCalledWith('asset', 4, 'dir')
  expect(api.archiveCommit.mock.calls[2]?.[0]).not.toBe(api.archiveCommit.mock.calls[0]?.[0])
})

it('pauses hidden polling, resumes once visible and avoids overlapping requests', async () => {
  const { api, dependencies } = setup()
  let resolve!: (value: Result<ArchiveStatus>) => void
  api.archiveStatus.mockReturnValueOnce(new Promise(accept => { resolve = accept }))
  dependencies.archiveLog.value = [status]
  await nextTick()
  Object.assign(document, { visibilityState: 'hidden' })
  document.dispatchEvent(new Event('visibilitychange'))
  await vi.advanceTimersByTimeAsync(1000)
  expect(api.archiveStatus).not.toHaveBeenCalled()
  Object.assign(document, { visibilityState: 'visible' })
  document.dispatchEvent(new Event('visibilitychange'))
  await vi.advanceTimersByTimeAsync(450)
  document.dispatchEvent(new Event('visibilitychange'))
  await vi.advanceTimersByTimeAsync(1000)
  expect(api.archiveStatus).toHaveBeenCalledTimes(1)
  resolve({ result: { ...status, phase: 'complete' } })
  await vi.advanceTimersByTimeAsync(1000)
  expect(dependencies.archiveLog.value[0]?.phase).toBe('complete')
  expect(api.archiveStatus).toHaveBeenCalledTimes(1)
})

it('ignores a pending poll response after unmount and clears future polling', async () => {
  const { api, dependencies } = setup()
  let resolve!: (value: Result<ArchiveStatus>) => void
  api.archiveStatus.mockReturnValueOnce(new Promise(accept => { resolve = accept }))
  dependencies.archiveLog.value = [status]
  await nextTick()
  await vi.advanceTimersByTimeAsync(450)
  lifecycle.unmounted.splice(0).forEach(callback => callback())
  resolve({ result: { ...status, phase: 'complete', actualPath: 'copy.jpg' } })
  document.dispatchEvent(new Event('visibilitychange'))
  await vi.advanceTimersByTimeAsync(2000)
  expect(dependencies.archiveLog.value[0]?.phase).toBe('copying')
  expect(dependencies.reload).not.toHaveBeenCalled()
  expect(api.archiveStatus).toHaveBeenCalledTimes(1)
})

