import type { SearchDirectoryItem, SearchItem } from '../shared/search'

export const directoryGroupKey = (path: string) => path.replaceAll('/', '\\').replace(/\\+$/, '').toUpperCase()

export function groupSearchResults(items: SearchItem[], folders: SearchDirectoryItem[], expandedItems: SearchItem[] = []) {
  const groups = new Map<string, { key: string; path: string; name: string; items: SearchItem[]; matchedDirectory: boolean; matchedFiles: boolean }>()
  function ensure(path: string) {
    const key = directoryGroupKey(path)
    if (!groups.has(key)) groups.set(key, {
      key, path, name: path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || path || '目录未记录',
      items: [], matchedDirectory: false, matchedFiles: false
    })
    return groups.get(key)!
  }
  for (const folder of folders) ensure(folder.path).matchedDirectory = true
  function addItem(item: SearchItem, matched: boolean) {
    const path = item.directoryPath ?? item.sourcePath?.replace(/[\\/][^\\/]+$/, '') ?? ''
    const group = ensure(path)
    if (matched) group.matchedFiles = true
    if (!group.items.some((existing) => existing.assetId === item.assetId)) group.items.push(item)
  }
  for (const item of items) addItem(item, true)
  for (const item of expandedItems) addItem(item, false)
  return [...groups.values()].sort((a, b) => Number(b.matchedFiles) - Number(a.matchedFiles)
    || a.path.localeCompare(b.path, 'zh-CN'))
}
