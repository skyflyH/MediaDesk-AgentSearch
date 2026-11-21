import { expect, it, vi } from 'vitest'
import { explorerDirectoryPath, openDirectoryInExplorer, openFolderInExplorer, openQueueItemInExplorer } from '../src/main/open-folder'
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs'
import { realpath } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'

it('normalizes extended Windows and network paths while preserving spaces, Chinese and commas', () => {
  expect(explorerDirectoryPath('\\\\?\\D:\\照片库\\2026 年,原片')).toBe('D:\\照片库\\2026 年,原片')
  expect(explorerDirectoryPath('\\\\?\\UNC\\server\\share\\照片')).toBe('\\\\server\\share\\照片')
  expect(explorerDirectoryPath('J:/ExamplePhotos/Album')).toBe('J:\\ExamplePhotos\\Album')
  expect(explorerDirectoryPath('C:\\')).toBe('C:\\')
  for (const path of ['relative', 'https://example.com', '\\\\.\\PhysicalDrive0', 'C:\\bad"path'])
    expect(() => explorerDirectoryPath(path)).toThrow()
})

it('opens the directory itself with its verified path, without relying on the folder shell association', async () => {
  const folder = mkdtempSync(join(tmpdir(), 'apm-open-directory-'))
  const target = join(folder, '照片库 2026,原片')
  mkdirSync(target)
  const launch = vi.fn().mockResolvedValue(undefined)
  await openDirectoryInExplorer(target, launch)
  expect(launch).toHaveBeenCalledExactlyOnceWith(explorerDirectoryPath(await realpath(target)))
})

it('does not launch Explorer for a missing directory or a file and propagates a launch failure', async () => {
  const folder = mkdtempSync(join(tmpdir(), 'apm-open-invalid-'))
  const launch = vi.fn().mockResolvedValue(undefined)
  const file = join(folder, 'photo.jpg')
  writeFileSync(file, 'fixture')
  await expect(openDirectoryInExplorer(join(folder, 'missing'), launch)).rejects.toThrow()
  await expect(openDirectoryInExplorer(file, launch)).rejects.toThrow()
  expect(launch).not.toHaveBeenCalled()
  launch.mockRejectedValueOnce(new Error('Explorer unavailable'))
  await expect(openDirectoryInExplorer(folder, launch)).rejects.toThrow('Explorer unavailable')
})

it('opens the parent and selects the actual file through the native reveal operation', async () => {
  const reveal = vi.fn().mockResolvedValue(undefined)
  const folder = 'D:\\照片库\\2026 年,原片'

  const file = folder + '\\素材.jpg'
  await expect(openFolderInExplorer(file, reveal)).resolves.toEqual({ folder, launched: true })
  expect(reveal).toHaveBeenCalledExactlyOnceWith(file)
})

it('returns a copyable directory when native file selection fails', async () => {
  const folder = 'D:\\照片库'
  const filePath = folder + '\\素材.jpg'
  const reveal = vi.fn().mockRejectedValueOnce(new Error('Shell unavailable'))

  await expect(openFolderInExplorer(filePath, reveal)).resolves.toEqual({ folder, launched: false })
  expect(reveal).toHaveBeenCalledExactlyOnceWith(filePath)
})

it('reveals the selected directory itself in its parent through the same native operation as search results', async () => {
  const folder = mkdtempSync(join(tmpdir(), 'apm-native-folder-'))
  const selected = join(folder, '目录 2026,原片')
  mkdirSync(selected)
  const reveal = vi.fn().mockResolvedValue(undefined), openRoot = vi.fn()
  await openQueueItemInExplorer(selected, reveal, openRoot)
  expect(reveal).toHaveBeenCalledExactlyOnceWith(explorerDirectoryPath(await realpath(selected)))
  expect(openRoot).not.toHaveBeenCalled()
})

it('awaits and reports native reveal failures instead of silently claiming success', async () => {
  const folder = mkdtempSync(join(tmpdir(), 'apm-native-folder-error-'))
  const reveal = vi.fn().mockRejectedValueOnce(new Error('Access denied'))
  await expect(openQueueItemInExplorer(folder, reveal, vi.fn())).rejects.toThrow('Access denied')
})

it('reveals non-queued files and rejects missing items before invoking the native host', async () => {
  const folder = mkdtempSync(join(tmpdir(), 'apm-native-item-'))
  const file = join(folder, '备注.txt')
  writeFileSync(file, 'fixture')
  const reveal = vi.fn().mockResolvedValue(undefined)
  await openQueueItemInExplorer(file, reveal, vi.fn())
  expect(reveal).toHaveBeenCalledExactlyOnceWith(explorerDirectoryPath(await realpath(file)))
  reveal.mockClear()
  await expect(openQueueItemInExplorer(join(folder, 'missing'), reveal, vi.fn())).rejects.toThrow()
  expect(reveal).not.toHaveBeenCalled()
})

it('opens a drive root itself because it has no containing folder to select it in', async () => {
  const root = (await realpath(tmpdir())).slice(0, 3)
  const reveal = vi.fn(), openRoot = vi.fn().mockResolvedValueOnce('').mockResolvedValueOnce('Access denied')
  await openQueueItemInExplorer(root, reveal, openRoot)
  expect(reveal).not.toHaveBeenCalled()
  expect(openRoot).toHaveBeenCalledExactlyOnceWith(root)
  await expect(openQueueItemInExplorer(root, reveal, openRoot)).rejects.toThrow('Access denied')
})
