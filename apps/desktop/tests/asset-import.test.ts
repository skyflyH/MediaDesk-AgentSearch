import { mkdtempSync, mkdirSync, rmSync, rmdirSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { basename, extname, join } from 'node:path'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { importAssetPaths } from '../src/main/asset-import'
import type { Result } from '../src/shared/health'
import type { ImportItem } from '../src/shared/workspace'

let root: string
let folders: string[]
let files: string[]

beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), 'media-library-import-'))
  folders = [root]
  files = []
})

afterEach(() => {
  for (const file of files) rmSync(file, { force: true })
  for (const folder of folders.reverse()) rmdirSync(folder)
})

function folder(parent: string, name: string) {
  const path = join(parent, name)
  mkdirSync(path)
  folders.push(path)
  return path
}

function file(parent: string, name: string) {
  const path = join(parent, name)
  writeFileSync(path, 'fixture')
  files.push(path)
  return path
}

async function importFiles(paths: string[]): Promise<Result<ImportItem[]>> {
  return { result: paths.map((path) => ({
    name: basename(path), status: extname(path) === '.txt' ? 'unsupported' : 'imported', assetId: null, reason: null
  })) }
}

it('imports mixed file and folder selections recursively through the existing importer', async () => {
  const picked = folder(root, 'picked')
  const nested = folder(picked, 'nested')
  const empty = folder(root, 'empty')
  const photo = file(picked, 'one.jpg')
  const clip = file(nested, 'two.mp4')
  const ignored = file(nested, 'notes.txt')
  const standalone = file(root, 'three.png')
  const link = join(nested, 'loop')
  symlinkSync(picked, link, process.platform === 'win32' ? 'junction' : 'dir')
  files.push(link)

  const handoff = vi.fn(importFiles)
  const register = vi.fn(async () => ({ result: null }))
  const reply = await importAssetPaths([standalone, picked, empty], handoff, register)
  expect(register.mock.calls).toEqual([[picked], [empty]])
  expect(handoff.mock.calls.map(([paths]) => [...paths].sort())).toEqual([
    [standalone, photo, clip, ignored].sort()
  ])
  expect(reply).toHaveProperty('result', expect.arrayContaining([
    expect.objectContaining({ name: 'empty', status: 'unsupported' }),
    expect.objectContaining({ name: 'notes.txt', status: 'unsupported' }),
    expect.objectContaining({ name: 'two.mp4', status: 'imported' })
  ]))
})

it('keeps completed batches visible when a later import fails', async () => {
  for (let index = 0; index < 101; index++) file(root, `${index}.jpg`)
  const handoff = vi.fn(importFiles).mockResolvedValueOnce({
    result: [{ name: 'registered.jpg', status: 'imported', assetId: null, reason: null }]
  }).mockResolvedValueOnce({
    error: { code: 'SERVICE_EXITED', message: '服务退出。', retryable: true }
  })

  const reply = await importAssetPaths([root], handoff)
  expect(handoff.mock.calls.map(([paths]) => paths.length)).toEqual([100, 1])
  expect(reply).toHaveProperty('result', [
    expect.objectContaining({ name: 'registered.jpg', status: 'imported' }),
    expect.objectContaining({ status: 'unavailable', reason: expect.stringContaining('服务退出。') })
  ])
})
