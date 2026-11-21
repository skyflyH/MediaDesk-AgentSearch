import { computed, reactive, ref } from 'vue'
import { expect, it, vi } from 'vitest'
import type { Asset } from '../src/shared/workspace'
import { filterQueueAssets, filterQueueRows, type QueueMediaType } from '../src/renderer/queueTypeFilter'
import { folderAssetIds } from '../src/renderer/queueSelection'
import { useQueueTree } from '../src/renderer/useQueueTree'

const root = 'C:\\素材'
const photo: Asset = { assetId: 'photo', originalName: '照片.jpg', extension: '.jpg', mediaKind: 'photo',
  available: false, sourcePath: `${root}\\照片.jpg`, createdAt: '2026-01-01T00:00:00Z' }
const video: Asset = { ...photo, assetId: 'video', originalName: '视频.mp4', extension: '.mp4', mediaKind: 'video',
  available: true, sourcePath: `${root}\\视频.mp4` }

it('filters all layouts from the same tree without reclassifying hidden queued assets as unqueued files', async () => {
  const browse = vi.fn(async () => ({ result: [
    { name: photo.originalName, path: photo.sourcePath!, directory: false },
    { name: video.originalName, path: video.sourcePath!, directory: false },
    { name: '说明.txt', path: `${root}\\说明.txt`, directory: false }
  ] }))
  const tree = useQueueTree(reactive({ items: [photo, video], roots: [root] }), browse, () => 0, () => 0)
  const folder = tree.rows.value[0]
  if (folder?.kind !== 'folder') throw Error('Missing root')
  tree.toggle(folder.folder)
  await Promise.resolve()
  const type = ref<QueueMediaType>('photo')
  const rows = computed(() => filterQueueRows(tree.rows.value, type.value))
  expect(rows.value.map(row => row.kind)).toEqual(['folder', 'asset'])
  expect(rows.value.find(row => row.kind === 'asset')).toMatchObject({ asset: { assetId: 'photo', available: false } })
  type.value = 'video'
  expect(rows.value.find(row => row.kind === 'asset')).toMatchObject({ asset: { assetId: 'video' } })
  type.value = 'all'
  expect(rows.value.map(row => row.kind)).toEqual(['folder', 'asset', 'asset', 'file'])
  expect(browse).toHaveBeenCalledTimes(1)
})

it('limits whole-folder selection and batch candidates to the selected media type, including collapsed descendants', () => {
  const nested = { ...photo, assetId: 'nested', sourcePath: `${root}\\子目录\\照片.jpg` }
  const matching = filterQueueAssets([photo, video, nested], 'photo')
  expect(matching.map(item => item.assetId)).toEqual(['photo', 'nested'])
  expect(folderAssetIds(matching, [root])).toEqual(['photo', 'nested'])
  expect(filterQueueAssets([photo, video], 'all')).toEqual([photo, video])
})

it('preserves folder loading and errors when the selected type has no matching assets', () => {
  expect(filterQueueRows([
    { kind: 'asset', key: 'photo', depth: 0, asset: photo },
    { kind: 'message', key: 'error', depth: 0, error: true, message: '目录不可用' }
  ], 'video')).toEqual([{ kind: 'message', key: 'error', depth: 0, error: true, message: '目录不可用' }])
})
