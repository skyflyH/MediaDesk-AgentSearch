import { expect, it, vi } from 'vitest'
import type { DesktopApi } from '../src/shared/health'
import type { ArchiveStatus } from '../src/shared/archive'
import { copyAssets } from '../src/renderer/copyAssets'

const status = (assetId: string, phase: ArchiveStatus['phase'] = 'complete'): ArchiveStatus => ({
  operationId: assetId, assetId, phase, sourcePath: `source/${assetId}.jpg`, finalPath: `target/${assetId}.jpg`,
  actualPath: phase === 'complete' ? `target/${assetId}.jpg` : null,
  errorCode: null, message: null, remark: 'unchanged', authors: 'unchanged'
})

function setup() {
  const api = {
    getDraft: vi.fn(async (id: string) => ({ result: { assetId: id, revision: 3, archiveRevision: 2 } })),
    saveDraft: vi.fn(async (input: { assetId: string }) => ({ result: { assetId: input.assetId, revision: 4, archiveRevision: 3 } })),
    archivePreview: vi.fn(async (id: string) => ({ result: { planToken: id, sourcePath: `source/${id}.jpg`, finalPath: `target/${id}.jpg` } })),
    archiveCommit: vi.fn(async (_operation: string, token: string) => ({ result: status(token, 'copying') })),
    archiveStatus: vi.fn(async (id: string) => ({ result: status(id) }))
  }
  return { api, run: (changes = {}) => copyAssets(api as unknown as DesktopApi, ['one', 'two', 'three'], 'directory', changes, vi.fn(), async () => {}) }
}

it('copies unchanged drafts in order and waits for each result before submitting the next', async () => {
  const { api, run } = setup()
  api.archiveStatus.mockImplementation(async (id) => {
    expect(api.archiveCommit).toHaveBeenCalledTimes(['one', 'two', 'three'].indexOf(id) + 1)
    return { result: status(id) }
  })
  const result = await run()
  expect(result.failure).toBeUndefined()
  expect(result.copied.map((item) => item.assetId)).toEqual(['one', 'two', 'three'])
  expect(api.saveDraft).toHaveBeenCalledWith({ assetId: 'one', expectedRevision: 3, changes: { directoryId: 'directory' } })
})

it('stops at a duplicate, retains completed paths, and never submits the remaining file', async () => {
  const { api, run } = setup()
  api.archivePreview.mockImplementation(async (id) => {
    if (id === 'two') throw new Error('目标已存在：target/two.jpg')
    return { result: { planToken: id, sourcePath: `source/${id}.jpg`, finalPath: `target/${id}.jpg` } }
  })
  const result = await run({ remark: { intent: 'set', value: '新备注' } })
  expect(result.copied).toEqual([{ assetId: 'one', path: 'target/one.jpg' }])
  expect(result.failure).toEqual({ assetId: 'two', message: '目标已存在：target/two.jpg' })
  expect(api.archiveCommit).toHaveBeenCalledTimes(1)
  expect(api.getDraft).not.toHaveBeenCalledWith('three')
  expect(api.saveDraft).toHaveBeenCalledWith(expect.objectContaining({ changes: {
    directoryId: 'directory', remark: { intent: 'set', value: '新备注' }
  } }))
})

it('also stops on a collision during copying or a failure after the copy has been published', async () => {
  for (const published of [false, true]) {
    const { api, run } = setup()
    api.archiveStatus.mockImplementation(async (id) => ({ result: {
      ...status(id, 'needs_review'), actualPath: published ? `target/${id}.jpg` : null,
      errorCode: published ? 'PROPERTY_WRITE_FAILED' : 'DESTINATION_EXISTS', message: '复制中断'
    } }))
    const result = await run()
    expect(api.archiveCommit).toHaveBeenCalledTimes(1)
    expect(result.failure?.assetId).toBe('one')
    expect(result.copied).toEqual(published ? [{ assetId: 'one', path: 'target/one.jpg' }] : [])
  }
})
