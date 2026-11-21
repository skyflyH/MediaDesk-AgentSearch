import { runInNewContext } from 'node:vm'
import { resolveConfig } from 'electron-vite'
import { build } from 'vite'
import { expect, it, vi } from 'vitest'

it('exposes the desktop API with the sandboxed preload module loader', async () => {
  const { config } = await resolveConfig({ logLevel: 'silent' }, 'build', 'production')
  const preload = config?.preload
  if (!preload) throw new Error('Missing desktop preload build configuration')
  const built = await build({ ...preload, build: { ...preload.build, write: false, emptyOutDir: false } })
  const entry = [built].flat().flatMap((result) => 'output' in result ? result.output : [])
    .find((file) => file.type === 'chunk' && file.isEntry)
  if (entry?.type !== 'chunk') throw new Error('Missing desktop preload entry')

  const exposeInMainWorld = vi.fn()
  const send = vi.fn()
  runInNewContext(entry.code, {
    require(module: string) {
      if (module !== 'electron') throw new Error(`Unsupported module in sandboxed preload: ${module}`)
      return { contextBridge: { exposeInMainWorld }, ipcRenderer: { send } }
    },
    process: { argv: [] },
    window: { addEventListener: vi.fn() }
  })

  expect(exposeInMainWorld).toHaveBeenCalledWith('mediaLibrary', expect.objectContaining({
    loadWorkspace: expect.any(Function)
  }))
  const api = exposeInMainWorld.mock.calls.find(([name]) => name === 'mediaLibrary')?.[1]
  for (const removed of ['searchPhotos', 'listPhotoJobs', 'retryPhotoJob']) expect(api).not.toHaveProperty(removed)
  const setWindowTheme = exposeInMainWorld.mock.calls.find(([name]) => name === 'setWindowTheme')?.[1]
  expect(setWindowTheme).toEqual(expect.any(Function))
  setWindowTheme('light')
  expect(send).toHaveBeenCalledWith('settings:window-theme', 'light')
})
