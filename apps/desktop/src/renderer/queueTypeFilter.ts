import type { Asset } from '../shared/workspace'
import type { QueueRow } from './useQueueTree'

export type QueueMediaType = 'all' | Asset['mediaKind']

export function filterQueueAssets(items: Asset[], type: QueueMediaType): Asset[] {
  return type === 'all' ? items : items.filter(item => item.mediaKind === type)
}

export function filterQueueRows(rows: QueueRow[], type: QueueMediaType): QueueRow[] {
  if (type === 'all') return rows
  // Preserve the tree and its loading/error feedback without treating files outside the queue as assets.
  return rows.filter(row => row.kind === 'folder' || row.kind === 'message' ||
    (row.kind === 'asset' && row.asset.mediaKind === type))
}
