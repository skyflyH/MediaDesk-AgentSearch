import { reactive, ref } from 'vue'
import { expect, it } from 'vitest'
import { compareQueueItems, type QueueSortItem, type QueueSorting } from '../src/renderer/queueSort'
import { useQueueTree } from '../src/renderer/useQueueTree'
import type { Asset } from '../src/shared/workspace'

const samples: QueueSortItem[] = [
  { name: 'IMG_10.jpg', modifiedAt: '2026-01-02T00:00:00Z', sizeBytes: 2 },
  { name: 'IMG_2.jpg', modifiedAt: '2026-01-01T00:00:00Z', sizeBytes: 100 },
  { name: 'IMG_1.jpg', modifiedAt: '2026-01-03T00:00:00Z', sizeBytes: 0 },
  { name: 'unknown.jpg', modifiedAt: 'invalid', sizeBytes: null }
]
it.each([
  ['name', 'ascending', ['IMG_1.jpg', 'IMG_2.jpg', 'IMG_10.jpg', 'unknown.jpg']],
  ['name', 'descending', ['unknown.jpg', 'IMG_10.jpg', 'IMG_2.jpg', 'IMG_1.jpg']],
  ['modified', 'ascending', ['IMG_2.jpg', 'IMG_10.jpg', 'IMG_1.jpg', 'unknown.jpg']],
  ['modified', 'descending', ['IMG_1.jpg', 'IMG_10.jpg', 'IMG_2.jpg', 'unknown.jpg']],
  ['size', 'ascending', ['IMG_1.jpg', 'IMG_10.jpg', 'IMG_2.jpg', 'unknown.jpg']],
  ['size', 'descending', ['IMG_2.jpg', 'IMG_10.jpg', 'IMG_1.jpg', 'unknown.jpg']]
] as const)('sorts %s %s with numeric values and unknown metadata last', (key, direction, expected) => {
  expect([...samples].sort((a, b) => compareQueueItems(a, b, { key, direction })).map(item => item.name)).toEqual(expected)
})

it('sorts queued and unqueued sibling files together and keeps nested directories attached', async () => {
  const root = 'C:\\Photos'
  const sorting = ref<QueueSorting>({ key: 'name', direction: 'ascending' })
  const asset: Asset = { assetId: 'queued', originalName: 'b.jpg', sourcePath: `${root}\\b.jpg`,
    extension: '.jpg', mediaKind: 'photo', available: true, createdAt: '2026-01-01T00:00:00Z',
    modifiedAt: '2026-01-02T00:00:00Z', sizeBytes: 5 }
  const props = reactive({ roots: [root], items: [asset] })
  const tree = useQueueTree(props, async () => ({ result: [
    { name: 'z-folder', path: `${root}\\z-folder`, directory: true, modifiedAt: '2026-01-01T00:00:00Z' },
    { name: 'a-folder', path: `${root}\\a-folder`, directory: true, modifiedAt: '2026-01-03T00:00:00Z' },
    { name: 'a.txt', path: `${root}\\a.txt`, directory: false, modifiedAt: '2026-01-01T00:00:00Z', sizeBytes: 10 },
    { name: 'b.jpg', path: asset.sourcePath!, directory: false }
  ] }), () => 0, () => 0, (a, b) => compareQueueItems(a, b, sorting.value))
  const row = tree.rows.value[0]!
  if (row.kind !== 'folder') throw Error('Missing root')
  tree.toggle(row.folder)
  await Promise.resolve()
  const labels = () => tree.rows.value.slice(1).map(row => row.kind === 'folder' ? row.folder.name
    : row.kind === 'asset' ? row.asset.originalName : row.kind === 'file' ? row.file.name : '')
  expect(labels()).toEqual(['a-folder', 'z-folder', 'a.txt', 'b.jpg'])
  sorting.value = { key: 'modified', direction: 'ascending' }
  expect(labels()).toEqual(['z-folder', 'a-folder', 'a.txt', 'b.jpg'])
  sorting.value = { key: 'modified', direction: 'descending' }
  expect(labels()).toEqual(['a-folder', 'z-folder', 'b.jpg', 'a.txt'])
  sorting.value = { key: 'size', direction: 'ascending' }
  expect(labels().slice(-2)).toEqual(['b.jpg', 'a.txt'])
  sorting.value = { key: 'size', direction: 'descending' }
  expect(labels().slice(-2)).toEqual(['a.txt', 'b.jpg'])
})
