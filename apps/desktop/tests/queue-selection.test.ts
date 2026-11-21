import { expect, it } from 'vitest'
import { folderAssetIds, queueOpenTarget, type QueueOpenItem } from '../src/renderer/queueSelection'
import type { Asset } from '../src/shared/workspace'

it('selects descendants of multiple folders including collapsed nested directories without duplicates', () => {
  const items = [
    { assetId: 'one', sourcePath: 'C:/Photos/Album/a.jpg' },
    { assetId: 'two', sourcePath: 'c:\\photos\\album\\Nested\\b.mp4' },
    { assetId: 'three', sourcePath: 'D:/Other/c.jpg' },
    { assetId: 'four', sourcePath: 'C:/Photos/Album-old/d.jpg' },
    { assetId: 'five' }
  ] as Asset[]
  expect(folderAssetIds(items, ['C:\\Photos\\ALBUM\\', 'C:/Photos/Album/Nested', 'D:/Other']))
    .toEqual(['one', 'two', 'three'])
  expect(folderAssetIds(items, ['C:/Empty'])).toEqual([])
})

const folder = (path: string): QueueOpenItem => ({ kind: 'folder', path })
const file = (assetId: string, path?: string): QueueOpenItem => ({ kind: 'file', assetId, path })

it.each([
  { label: 'empty selection', items: [], expected: undefined },
  { label: 'one folder including an empty folder', items: [folder('C:/Photos/Album')], expected: { path: 'C:/Photos/Album' } },
  { label: 'one file', items: [file('one', 'C:/Photos/a.jpg')], expected: { assetId: 'one' } },
  { label: 'one file whose path is resolved by the service', items: [file('one')], expected: { assetId: 'one' } },
  { label: 'files directly in one parent, case and separator insensitive', items: [file('one', 'C:/Photos/a.jpg'), file('two', 'c:\\PHOTOS\\b.jpg')], expected: { assetId: 'one' } },
  { label: 'files at a drive root', items: [file('one', 'C:/a.jpg'), file('two', 'c:/b.jpg')], expected: { assetId: 'one' } },
  { label: 'files directly in one UNC directory', items: [file('one', '//server/share/photos/a.jpg'), file('two', '\\\\SERVER\\share\\photos\\b.jpg')], expected: { assetId: 'one' } },
  { label: 'files in different directories', items: [file('one', 'C:/Photos/a.jpg'), file('two', 'C:/Other/b.jpg')], expected: undefined },
  { label: 'a file in the parent and a file in a child directory', items: [file('one', 'C:/Photos/a.jpg'), file('two', 'C:/Photos/Child/b.jpg')], expected: undefined },
  { label: 'multiple folders in one parent', items: [folder('C:/Photos/Album'), folder('C:/Photos/Other')], expected: undefined },
  { label: 'explicitly selected parent and child folders', items: [folder('C:/Photos'), folder('C:/Photos/Album')], expected: undefined },
  { label: 'a folder mixed with its explicitly selected child file', items: [folder('C:/Photos'), file('one', 'C:/Photos/a.jpg')], expected: undefined },
  { label: 'a folder mixed with a sibling file', items: [folder('C:/Photos/Album'), file('one', 'C:/Photos/a.jpg')], expected: undefined },
  { label: 'multiple files with an unknown parent', items: [file('one'), file('two', 'C:/Photos/b.jpg')], expected: undefined }
])('open containing folder: $label', ({ items, expected }) => {
  expect(queueOpenTarget(items)).toEqual(expected)
})
