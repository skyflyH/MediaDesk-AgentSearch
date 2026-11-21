import type { Asset } from '../shared/workspace'

export function queuePathKey(path: string): string {
  return path.replaceAll('/', '\\').replace(/\\+$/, '').toLowerCase()
}

export function folderAssetIds(items: Asset[], paths: string[]): string[] {
  const prefixes = paths.map(path => `${queuePathKey(path)}\\`)
  return items.filter(item => item.sourcePath && prefixes.some(prefix => queuePathKey(item.sourcePath!).startsWith(prefix)))
    .map(item => item.assetId)
}

export type QueueOpenItem = { kind: 'folder'; path: string } | { kind: 'file'; assetId: string; path?: string | null }
export type QueueOpenTarget = { path: string } | { assetId: string }

// Use explicit selections, not the descendants included for batch operations.
export function queueOpenTarget(items: QueueOpenItem[]): QueueOpenTarget | undefined {
  const first = items[0]
  if (!first) return
  if (items.length === 1) return first.kind === 'folder' ? { path: first.path } : { assetId: first.assetId }
  if (items.some(item => item.kind === 'folder' || !item.path)) return
  const parents = items.map(item => {
    const path = queuePathKey(item.path!)
    return path.slice(0, path.lastIndexOf('\\'))
  })
  if (first.kind === 'file' && parents[0] && parents.every(parent => parent === parents[0])) return { assetId: first.assetId }
}
