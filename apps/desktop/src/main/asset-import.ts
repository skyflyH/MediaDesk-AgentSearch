import { lstat, readdir } from 'node:fs/promises'
import { basename, join } from 'node:path'
import type { Result } from '../shared/health'
import type { ImportItem } from '../shared/workspace'

const batchSize = 100

function skipped(path: string, reason: string, status: 'unavailable' | 'unsupported' = 'unavailable'): ImportItem {
  return { name: basename(path) || path, status, assetId: null, reason }
}

async function* folderFiles(root: string): AsyncGenerator<string | ImportItem> {
  const directories = [root]
  let found = false
  while (directories.length) {
    const folder = directories.pop()!
    try {
      for (const entry of await readdir(folder, { withFileTypes: true })) {
        const path = join(folder, entry.name)
        if (entry.isDirectory()) directories.push(path)
        else if (entry.isFile()) {
          found = true
          yield path
        }
      }
    } catch {
      found = true
      yield skipped(folder, '无法读取文件夹。')
    }
  }
  if (!found) yield skipped(root, '文件夹中没有可扫描的文件。', 'unsupported')
}

async function* sources(paths: string[], registerFolder?: (path: string) => Promise<Result<null>>): AsyncGenerator<string | ImportItem> {
  for (const path of paths) {
    // Let the regular importer handle invalid file paths and explicitly selected file links.
    const info = await lstat(path).catch(() => undefined)
    if (!info?.isDirectory()) {
      yield path
      continue
    }
    const registered = await registerFolder?.(path)
    if (registered && 'error' in registered) yield skipped(path, registered.error.message)
    yield* folderFiles(path)
  }
}

export async function importAssetPaths(
  paths: string[], importFiles: (paths: string[]) => Promise<Result<ImportItem[]>>,
  registerFolder?: (path: string) => Promise<Result<null>>
): Promise<Result<ImportItem[]>> {
  const items: ImportItem[] = []
  let batch: string[] = []
  for await (const entry of sources(paths, registerFolder)) {
    if (typeof entry === 'string') batch.push(entry)
    else items.push(entry)
    if (batch.length < batchSize) continue
    const reply = await importFiles(batch)
    if ('error' in reply) return interrupted(reply.error.message)
    items.push(...reply.result)
    batch = []
  }
  if (batch.length) {
    const reply = await importFiles(batch)
    if ('error' in reply) return interrupted(reply.error.message)
    items.push(...reply.result)
  }
  return { result: items }

  function interrupted(message: string): Result<ImportItem[]> {
    return { result: [...items, {
      name: '导入中断', status: 'unavailable', assetId: null,
      reason: `${message} 可重新导入同一批素材，已登记的文件会自动去重。`
    }] }
  }
}
