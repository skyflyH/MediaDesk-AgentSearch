import { win32 } from 'node:path'
import { opendir, realpath } from 'node:fs/promises'

export function explorerDirectoryPath(path: string): string {
  if (path.startsWith('\\\\?\\UNC\\')) path = '\\\\' + path.slice(8)
  else if (path.startsWith('\\\\?\\')) path = path.slice(4)
  if (!/^(?:[a-z]:[\\/]|\\\\[^\\]+\\[^\\]+)/i.test(path) || /[\0"<>|?*]/.test(path) || path.startsWith('\\\\.\\'))
    throw Error('目录路径无效。')
  return win32.normalize(path)
}

export async function openQueueItemInExplorer(path: string,
  reveal: (path: string) => void | Promise<void>, openRoot: (path: string) => Promise<string>): Promise<void> {
  const item = explorerDirectoryPath(await realpath(explorerDirectoryPath(path)))
  // Reveal the selected item, including a directory, in its parent. Reuse the
  // same native operation as search results instead of spawning hidden Explorer.
  if (win32.dirname(item) !== item) await reveal(item)
  else {
    const error = await openRoot(item)
    if (error) throw new Error(error)
  }
}

export async function openDirectoryInExplorer(path: string, launch: (folder: string) => Promise<void>): Promise<void> {
  const folder = explorerDirectoryPath(await realpath(explorerDirectoryPath(path)))
  // Verify directory access before launching Explorer, so missing drives and denied
  // folders produce an application error instead of a generic Windows dialog.
  const directory = await opendir(folder)
  await directory.close()
  await launch(folder)
}

export async function openFolderInExplorer(filePath: string, reveal: (path: string) => void | Promise<void>): Promise<{ folder: string; launched: boolean }> {
  const folder = win32.dirname(filePath)
  try {
    await reveal(filePath)
    return { folder, launched: true }
  } catch {
    return { folder, launched: false }
  }
}
