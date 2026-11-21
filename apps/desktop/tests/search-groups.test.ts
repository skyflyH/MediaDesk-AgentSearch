import { expect, it } from 'vitest'
import { groupSearchResults } from '../src/renderer/searchGroups'
import type { SearchItem } from '../src/shared/search'

const item = (assetId: string, directoryPath?: string, sourcePath?: string) => ({
  assetId, fileName: '同名.jpg', directoryPath, sourcePath
}) as SearchItem

it('puts file matches before directory-only matches regardless of directory name order', () => {
  const groups = groupSearchResults([item('file', 'D:\\Z-file-match'), item('both', 'D:\\M-both')], [
    { name: 'A-directory', path: 'D:\\A-directory', createdAt: '2026-11-20T00:00:00Z' },
    { name: 'M-both', path: 'D:\\M-both', createdAt: '2026-11-20T00:00:00Z' }
  ])
  expect(groups.map((group) => group.name)).toEqual(['M-both', 'Z-file-match', 'A-directory'])
})

it('does not promote a directory-only match when its contents are expanded', () => {
  const groups = groupSearchResults([item('matched', 'D:\\Z-file-match')], [
    { name: 'A-directory', path: 'D:\\A-directory', createdAt: '2026-11-20T00:00:00Z' }
  ], [item('loaded', 'D:\\A-directory')])
  expect(groups.map((group) => group.name)).toEqual(['Z-file-match', 'A-directory'])
  expect(groups[1]?.items).toHaveLength(1)
  expect(groups[1]?.matchedFiles).toBe(false)
})

it('groups files by their current directory rather than their original source directory', () => {
  const groups = groupSearchResults([
    item('one', 'D:\\保存\\海边', 'C:\\原片\\同名.jpg'),
    item('two', 'D:\\保存\\海边', 'C:\\另一个目录\\同名.jpg'),
    item('three', 'D:\\保存\\旅行', 'C:\\原片\\另一张.jpg')
  ], [])
  expect(groups).toHaveLength(2)
  expect(groups.find((group) => group.name === '海边')?.items.map((file) => file.assetId)).toEqual(['one', 'two'])
  expect(groups.every((group) => group.path.startsWith('D:'))).toBe(true)
})

it('merges matching directories, case variations and appended pages without duplicating files', () => {
  const groups = groupSearchResults([
    item('one', 'D:\\Photos\\海边'), item('two', 'd:/photos/海边/'), item('one', 'D:\\Photos\\海边')
  ], [{ name: '海边', path: 'D:\\Photos\\海边', createdAt: '2026-11-20T00:00:00Z' }])
  expect(groups).toHaveLength(1)
  expect(groups[0]?.matchedDirectory).toBe(true)
  expect(groups[0]?.items.map((file) => file.assetId)).toEqual(['one', 'two'])
})

it('retains empty directory matches and keeps missing paths in an explicit unknown directory group', () => {
  const groups = groupSearchResults([item('unknown'), item('legacy', undefined, 'C:\\照片\\a.jpg')], [
    { name: '空目录', path: 'D:\\空目录', createdAt: '2026-11-20T00:00:00Z' }
  ])
  expect(groups.find((group) => group.path === 'D:\\空目录')).toMatchObject({ items: [], matchedDirectory: true })
  expect(groups.find((group) => group.path === '')?.name).toBe('目录未记录')
  expect(groups.find((group) => group.path === 'C:\\照片')?.items[0]?.assetId).toBe('legacy')
})
