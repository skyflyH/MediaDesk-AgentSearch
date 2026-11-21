import { nextTick, reactive } from 'vue'
import { expect, it, vi } from 'vitest'
import type { Result } from '../src/shared/health'
import type { Asset, FolderEntry } from '../src/shared/workspace'
import { useQueueTree } from '../src/renderer/useQueueTree'

const root = 'C:\\素材'
const folder = `${root}\\相机`
const sibling = `${root}\\手机`
const otherRoot = 'D:\\素材'

function asset(path: string): Asset {
  return {
    assetId: path, sourcePath: path, originalName: path.split('\\').at(-1)!,
    extension: '.jpg', mediaKind: 'photo', available: true, createdAt: '2026-01-01T00:00:00Z'
  }
}

function entry(path: string, directory = false): FolderEntry {
  return { path, name: path.split('\\').at(-1)!, directory }
}

function setup(items: Asset[], browseFolder: (root: string, path: string) => Promise<Result<FolderEntry[]>>) {
  const props = reactive({ items, roots: [root, otherRoot] })
  const tree = useQueueTree(props, browseFolder, () => 0, (left, right) => left < right ? -1 : Number(left > right))
  return {
    props,
    rows: tree.rows,
    toggle(path: string) {
      const row = tree.rows.value.find((row) => row.kind === 'folder' && row.folder.path === path)
      if (row?.kind !== 'folder') throw new Error(`Missing folder ${path}`)
      tree.toggle(row.folder)
    },
    labels: () => tree.rows.value.map((row) => {
      if (row.kind === 'folder') return row.folder.path
      if (row.kind === 'asset') return row.asset.originalName
      return row.kind === 'file' ? row.file.name : row.message
    })
  }
}

it('toggles branches independently and keeps their sibling files in the same list', async () => {
  const camera = asset(`${folder}\\camera.jpg`)
  const phone = asset(`${sibling}\\phone.jpg`)
  const cover = asset(`${root}\\cover.jpg`)
  const loose = asset('C:\\素材备份\\loose.jpg')
  const notes = entry(`${folder}\\notes.txt`)
  const browse = vi.fn(async (_root: string, path: string) => ({ result: path === root
    ? [entry(folder, true), entry(sibling, true), entry(cover.sourcePath!)]
    : path === folder ? [entry(camera.sourcePath!), notes] : [entry(phone.sourcePath!)] }))
  const tree = setup([camera, phone, cover, loose], browse)
  expect(tree.labels()).toEqual([root, otherRoot, 'loose.jpg'])
  expect(browse).not.toHaveBeenCalled()

  tree.toggle(root)
  tree.toggle(folder)
  tree.toggle(sibling)
  await Promise.resolve()
  expect(browse.mock.calls).toEqual([[root, root], [root, folder], [root, sibling]])
  expect(tree.labels()).toEqual([root, sibling, 'phone.jpg', folder, 'camera.jpg', 'notes.txt', 'cover.jpg', otherRoot, 'loose.jpg'])

  tree.toggle(folder)
  expect(tree.labels()).toEqual([root, sibling, 'phone.jpg', folder, 'cover.jpg', otherRoot, 'loose.jpg'])
  tree.toggle(root)
  tree.toggle(root)
  expect(tree.labels()).toContain('phone.jpg')

  tree.props.items = [camera, cover, loose]
  await nextTick()
  expect(tree.labels()).toContain('phone.jpg')
  expect(tree.rows.value).toContainEqual(expect.objectContaining({ kind: 'file', file: entry(phone.sourcePath!) }))
})

it('ignores a stale read after a rapid collapse and reopen, and keeps unavailable assets visible', async () => {
  let finishFirst!: (result: Result<FolderEntry[]>) => void
  const first = new Promise<Result<FolderEntry[]>>((resolve) => { finishFirst = resolve })
  const unavailable = { ...asset(`${root}\\missing.jpg`), available: false }
  const browse = vi.fn().mockReturnValueOnce(first).mockResolvedValue({
    error: { code: 'SOURCE_UNAVAILABLE', message: '目录不可用', retryable: false }
  })
  const tree = setup([unavailable], browse)
  tree.toggle(root)
  tree.toggle(root)
  tree.toggle(root)
  await Promise.resolve()
  finishFirst({ result: [entry(`${root}\\old.txt`)] })
  await Promise.resolve()
  expect(tree.labels()).toEqual([root, '目录不可用', 'missing.jpg', otherRoot])
})
