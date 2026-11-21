import { computed, shallowReactive, watch } from 'vue'
import type { DesktopApi } from '../shared/health'
import type { Asset, FolderEntry } from '../shared/workspace'
import type { QueueSortItem } from './queueSort'

type Folder = { key: string; path: string; name: string; root: string; modifiedAt?: string | null }
type FolderState = { expanded: boolean; entries: FolderEntry[]; loading: boolean; error: string }
export type QueueRow = { depth: number } & (
  | { kind: 'folder'; key: string; folder: Folder; expanded: boolean; loading: boolean }
  | { kind: 'asset'; key: string; asset: Asset }
  | { kind: 'file'; key: string; file: FolderEntry }
  | { kind: 'message'; key: string; message: string; error: boolean }
)

function pathKey(path: string) {
  return path.replaceAll('/', '\\').replace(/\\+$/, '').toLowerCase()
}

function folderFor(path: string, root: string): Folder {
  return { path, root, key: pathKey(path), name: path.split(/[\\/]/).filter(Boolean).at(-1) ?? path }
}

export function useQueueTree(props: { items: Asset[]; roots: string[] }, browseFolder: DesktopApi['browseFolder'],
  compareAssets: (left: Asset, right: Asset) => number, compareNames: (left: string, right: string) => number,
  compareEntries?: (left: QueueSortItem, right: QueueSortItem, directory: boolean) => number) {
  const states = shallowReactive(new Map<string, FolderState>())
  const roots = computed(() => {
    const unique = new Map(props.roots.map((path) => [pathKey(path), folderFor(path, path)]))
    const keys = [...unique.keys()]
    // An imported subdirectory already has a place under its enclosing root.
    return [...unique.values()].filter((folder) => !keys.some((key) => folder.key.startsWith(`${key}\\`)))
  })

  const indexed = computed(() => {
    const folders = new Map<string, Map<string, Folder>>()
    const assets = new Map<string, Asset[]>()
    const loose: Asset[] = []
    // Index queue paths even when their original files or directories are unavailable.
    for (const asset of props.items) {
      const source = asset.sourcePath?.replaceAll('/', '\\')
      const key = source && pathKey(source)
      const root = key && roots.value.find((folder) => key.startsWith(`${folder.key}\\`))
      if (!source || !root) {
        loose.push(asset)
        continue
      }

      let parent = root
      for (let end = source.indexOf('\\', root.key.length + 1); end !== -1; end = source.indexOf('\\', end + 1)) {
        const path = source.slice(0, end)
        const key = pathKey(path)
        let children = folders.get(parent.key)
        if (!children) {
          children = new Map()
          folders.set(parent.key, children)
        }
        const child = children.get(key) ?? folderFor(path, root.path)
        children.set(key, child)
        parent = child
      }
      const siblings = assets.get(parent.key)
      if (siblings) siblings.push(asset)
      else assets.set(parent.key, [asset])
    }
    return { folders, assets, loose }
  })

  const rows = computed(() => {
    const rows: QueueRow[] = []
    const { folders, assets, loose } = indexed.value

    function addAssets(items: Asset[], depth: number) {
      for (const asset of [...items].sort(compareAssets)) {
        rows.push({ kind: 'asset', key: asset.assetId, asset, depth })
      }
    }

    function addFolders(items: Folder[], depth: number) {
      for (const folder of items.sort((left, right) => compareEntries
        ? compareEntries(left, right, true) : compareNames(left.name, right.name))) {
        const state = states.get(folder.key)
        rows.push({ kind: 'folder', key: `folder:${folder.key}`, folder, depth,
          expanded: state?.expanded ?? false, loading: state?.loading ?? false })
        if (!state?.expanded) continue

        const children = new Map(folders.get(folder.key))
        const queued = assets.get(folder.key) ?? []
        const queuedPaths = new Set(queued.map((asset) => pathKey(asset.sourcePath!)))
        const otherFiles: FolderEntry[] = []
        for (const entry of state.entries) {
          const key = pathKey(entry.path)
          if (entry.directory) children.set(key, { ...folderFor(entry.path, folder.root), modifiedAt: entry.modifiedAt })
          else if (!queuedPaths.has(key)) otherFiles.push(entry)
        }
        const childDepth = depth + 1
        if (state.error || (!children.size && !queued.length && !otherFiles.length)) {
          rows.push({ kind: 'message', key: `message:${folder.key}`, depth: childDepth, error: !!state.error,
            message: state.error || (state.loading ? '正在读取目录…' : '此文件夹为空') })
        }
        addFolders([...children.values()], childDepth)
        const files: QueueRow[] = [
          ...queued.map(asset => ({ kind: 'asset' as const, key: asset.assetId, asset, depth: childDepth })),
          ...otherFiles.map(file => ({ kind: 'file' as const, key: `file:${pathKey(file.path)}`, file, depth: childDepth }))
        ]
        if (compareEntries) {
          const item = (row: QueueRow): QueueSortItem => row.kind === 'asset'
            ? { name: row.asset.originalName, modifiedAt: row.asset.modifiedAt, sizeBytes: row.asset.sizeBytes }
            : (row as Extract<QueueRow, { kind: 'file' }>).file
          files.sort((left, right) => compareEntries(item(left), item(right), false))
          rows.push(...files)
        } else {
          addAssets(queued, childDepth)
          for (const file of otherFiles.sort((left, right) => compareNames(left.name, right.name)))
            rows.push({ kind: 'file', key: `file:${pathKey(file.path)}`, file, depth: childDepth })
        }
      }
    }

    addFolders([...roots.value], 0)
    addAssets(loose, 0)
    return rows
  })

  async function refresh(folder: Folder) {
    const pending = { expanded: true, entries: states.get(folder.key)?.entries ?? [], loading: true, error: '' }
    states.set(folder.key, pending)
    try {
      const reply = await browseFolder(folder.root, folder.path)
      if ('error' in reply) throw new Error(reply.error.message)
      if (states.get(folder.key) === pending) {
        states.set(folder.key, { ...pending, entries: reply.result, loading: false })
      }
    } catch (error) {
      if (states.get(folder.key) === pending) states.set(folder.key, {
        ...pending, entries: [], loading: false,
        error: error instanceof Error ? error.message : '无法读取原始文件夹。'
      })
    }
  }

  function toggle(folder: Folder) {
    const state = states.get(folder.key)
    if (state?.expanded) states.set(folder.key, { ...state, expanded: false, loading: false })
    else void refresh(folder)
  }

  watch(() => props.items, () => {
    for (const row of rows.value) {
      if (row.kind === 'folder' && row.expanded) void refresh(row.folder)
    }
  })

  return { rows, toggle, refresh }
}
