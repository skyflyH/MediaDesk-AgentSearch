export type QueueSortKey = 'name' | 'modified' | 'size'
export type QueueSorting = { key: QueueSortKey; direction: 'ascending' | 'descending' }
export type QueueSortItem = { name: string; modifiedAt?: string | null; sizeBytes?: number | null }
const collator = new Intl.Collator('zh-CN', { numeric: true, sensitivity: 'base' })

export function compareQueueItems(left: QueueSortItem, right: QueueSortItem, sorting: QueueSorting): number {
  const { key, direction } = sorting
  const value = (item: QueueSortItem) => key === 'name' ? item.name : key === 'size' ? item.sizeBytes
    : item.modifiedAt ? Date.parse(item.modifiedAt) : null
  const a = value(left)
  const b = value(right)
  const missing = (value: unknown) => value == null || typeof value === 'number' && !Number.isFinite(value)
  if (missing(a)) return missing(b) ? collator.compare(left.name, right.name) : 1
  if (missing(b)) return -1
  const order = typeof a === 'number' && typeof b === 'number' ? a - b : collator.compare(String(a), String(b))
  return (direction === 'ascending' ? order : -order) || collator.compare(left.name, right.name)
}
