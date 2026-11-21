import { expect, it, vi } from 'vitest'
import type { DesktopApi, Result } from '../src/shared/health'
import type { AssetDraft, ImportItem } from '../src/shared/workspace'
import { useWorkspace } from '../src/renderer/useWorkspace'

vi.mock('vue', async (importOriginal) => ({
  ...await importOriginal<typeof import('vue')>(),
  onMounted: (callback: () => void) => callback(),
  onUnmounted: vi.fn(),
}))

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: Error) => void
  const promise = new Promise<T>((accept, fail) => {
    resolve = accept
    reject = fail
  })
  return { promise, resolve, reject }
}

async function setup() {
  let drop: (paths: string[]) => void = () => {}
  const snapshot = { result: {
    queueIds: [], currentAssetId: null, items: [], importFolders: [], incompleteOperations: [], recentArchives: [], draft: null,
  } }
  const loadWorkspace = vi.fn<DesktopApi['loadWorkspace']>().mockResolvedValue(snapshot)
  const importAssets = vi.fn<DesktopApi['importAssets']>()
  const chooseAssetPaths = vi.fn<DesktopApi['chooseAssetPaths']>()
  const getDraft = vi.fn<DesktopApi['getDraft']>()
  const saveDraft = vi.fn<DesktopApi['saveDraft']>()
  const saveSourceProperties = vi.fn<DesktopApi['saveSourceProperties']>()
  const api: Pick<DesktopApi, 'loadWorkspace' | 'importAssets' | 'chooseAssetPaths' |
    'onAssetsDropped' | 'onExitRequested' | 'onExitCancelled' | 'getDraft' | 'saveDraft' | 'saveSourceProperties'> = {
    loadWorkspace, importAssets, chooseAssetPaths, getDraft, saveDraft, saveSourceProperties,
    onAssetsDropped(callback) { drop = callback; return () => {} },
    onExitRequested: () => () => {},
    onExitCancelled: () => () => {},
  }
  const workspace = useWorkspace(api as DesktopApi, () => {})
  await vi.waitFor(() => expect(workspace.loading.value).toBe(false))
  return { workspace, loadWorkspace, importAssets, chooseAssetPaths, getDraft, saveDraft, saveSourceProperties, snapshot, drop }
}

it('shows activity from a drop until the last overlapping import has refreshed the queue', async () => {
  const { workspace, loadWorkspace, importAssets, snapshot, drop } = await setup()
  const first = deferred<Result<ImportItem[]>>()
  const second = deferred<Result<ImportItem[]>>()
  const refreshed = deferred<typeof snapshot>()
  importAssets.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
  loadWorkspace.mockReturnValueOnce(refreshed.promise)
  const reply = { result: [{ name: 'portrait.dng', status: 'imported', assetId: 'photo' } as ImportItem] }

  drop(['C:\\RAW'])
  expect(workspace.importing.value).toBe(true)
  drop(['C:\\video.mov'])
  first.resolve(reply)
  await vi.waitFor(() => expect(loadWorkspace).toHaveBeenCalledTimes(2))
  second.resolve(reply)
  expect(workspace.importing.value).toBe(true)
  refreshed.resolve(snapshot)
  await vi.waitFor(() => expect(workspace.importing.value).toBe(false))
  expect(loadWorkspace).toHaveBeenCalledTimes(3)
})

it('waits for picker selection and clears import activity on failure', async () => {
  const { workspace, importAssets, chooseAssetPaths } = await setup()
  const selection = deferred<Result<string[]>>()
  const importing = deferred<Result<ImportItem[]>>()
  chooseAssetPaths.mockReturnValueOnce(selection.promise)
  importAssets.mockReturnValueOnce(importing.promise)
  const operation = workspace.choose('folder')
  expect(workspace.importing.value).toBe(false)

  selection.resolve({ result: ['C:\\RAW'] })
  await vi.waitFor(() => expect(workspace.importing.value).toBe(true))
  importing.reject(new Error('Import failed'))
  await operation
  expect(workspace.importing.value).toBe(false)
  expect(workspace.workspaceError.value).toBe('Import failed')
})

it('saves per-file batch names with each draft revision and returns successful updates alongside failures', async () => {
  const { workspace, getDraft, saveDraft, saveSourceProperties } = await setup()
  getDraft.mockImplementation(async (id) => ({ result: { assetId: id, revision: 4 } as AssetDraft }))
  saveDraft.mockImplementation(async (input) => input.assetId === 'two'
    ? { error: { code: 'REVISION_CONFLICT', message: 'Changed elsewhere', retryable: false } }
    : { result: { assetId: input.assetId, revision: 5, data: { name: input.changes.name } } as AssetDraft })
  const changes = { one: 'Travel1', two: 'Travel2', three: 'Travel3' }
  const result = await workspace.saveBatch(Object.keys(changes), (id) => ({ name: changes[id as keyof typeof changes] }))
  expect(saveDraft.mock.calls.map(([input]) => input)).toEqual([
    { assetId: 'one', expectedRevision: 4, changes: { name: 'Travel1' } },
    { assetId: 'two', expectedRevision: 4, changes: { name: 'Travel2' } },
    { assetId: 'three', expectedRevision: 4, changes: { name: 'Travel3' } }
  ])
  expect(result).toMatchObject([
    { id: 'one', draft: { data: { name: 'Travel1' } } },
    { id: 'two', error: 'Changed elsewhere' },
    { id: 'three', draft: { data: { name: 'Travel3' } } }
  ])
  expect(saveSourceProperties).not.toHaveBeenCalled()
})

it('writes batch attributes to original files and retains renamed paths even when remark writing fails', async () => {
  const { workspace, loadWorkspace, getDraft, saveDraft, saveSourceProperties } = await setup()
  const makeDraft = (id: string, revision: number) => ({ assetId: id, revision, archiveRevision: revision,
    data: { name: `Renamed${id}`, remark: { intent: 'set', value: 'Comment' } } } as AssetDraft)
  loadWorkspace.mockResolvedValue({ result: { items: ['one', 'two', 'three', 'four'].map((id) => ({
    assetId: id, originalName: `${id}.jpg`, sourcePath: `C:/${id}.jpg`, extension: '.jpg', available: true,
    mediaKind: 'photo', createdAt: '2026-11-20T00:00:00Z'
  })), draft: makeDraft('one', 3), importFolders: [], incompleteOperations: [], recentArchives: [] } } as never)
  await workspace.reload()
  getDraft.mockImplementation(async (id) => ({ result: makeDraft(id, 3) }))
  saveDraft.mockImplementation(async ({ assetId }) => ({ result: makeDraft(assetId, 4) }))
  saveSourceProperties.mockImplementation(async (id) => id === 'three'
    ? { error: { code: 'SOURCE_RENAME_FAILED', message: 'File is in use', retryable: true } }
    : { result: { draft: makeDraft(id, 6), renamedPath: `C:/Renamed${id}.jpg`,
      remark: id === 'two' ? 'write_failed' : id === 'four' ? 'library_only' : 'written',
      authors: 'unchanged', remarkFailure: id === 'two' ? 'Comment write failed' : undefined } })
  const result = await workspace.saveBatch(['one', 'two', 'three', 'four'],
    (id) => ({ name: `Renamed${id}`, remark: { intent: 'set', value: 'Comment' } }), { writeSourceProperties: true })
  expect(saveSourceProperties.mock.calls).toEqual([['one', 4], ['two', 4], ['three', 4], ['four', 4]])
  expect(result).toMatchObject([
    { id: 'one', draft: { revision: 6 } },
    { id: 'two', draft: { revision: 6 }, error: 'Comment write failed' },
    { id: 'three', draft: { revision: 4 }, error: 'File is in use' },
    { id: 'four', draft: { revision: 6 }, error: expect.any(String) }
  ])
  expect(result?.[0]?.error).toBeUndefined()
  expect(workspace.draft.value?.revision).toBe(6)
  expect(workspace.items.value.map((item) => item.sourcePath)).toEqual([
    'C:/Renamedone.jpg', 'C:/Renamedtwo.jpg', 'C:/three.jpg', 'C:/Renamedfour.jpg'
  ])
})
