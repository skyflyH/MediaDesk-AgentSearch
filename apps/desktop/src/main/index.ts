import { existsSync, mkdirSync, readFileSync } from 'node:fs'
import { realpath } from 'node:fs/promises'
import { randomUUID } from 'node:crypto'
import { isAbsolute, join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { app, BrowserWindow, dialog, globalShortcut, ipcMain, Menu, nativeImage, safeStorage, screen, shell, Tray, type IpcMainEvent, type IpcMainInvokeEvent } from 'electron'
import { z } from 'zod'
import * as channels from '../shared/channels'
import { healthSchema, type Result } from '../shared/health'
import { assetId, draftSchema, folderDetailsSchema, folderEntriesSchema, importSchema, revision, saveDraftSchema, savedSourcePropertiesSchema, workspaceSchema } from '../shared/workspace'
import { EditShortcutSettings, HotkeySettings, normalizeHotkey } from './desktop-lifecycle'
import { LoginStartup } from './login-startup'
import { importAssetPaths } from './asset-import'
import { openDirectoryInExplorer, openFolderInExplorer, openQueueItemInExplorer } from './open-folder'
import { defaultEditShortcuts, defaultGlobalHotkeys, editShortcutsSchema, globalHotkeyTarget, viewActionSchema, type GlobalHotkeyTarget } from '../shared/desktop-controls'
import { verifyLifecycle, verifyMedia, verifyModel, verifyPhotoSearch, verifySearch, verifyWorkspace } from './desktop-smoke'
import { ServiceClient } from './service-client'
import { archivePlanSchema, archiveStatusSchema, directorySchema, planToken, type ArchiveDirectory } from '../shared/archive'
import { previewKind, proxyStatus } from '../shared/preview'
import { PreviewProtocol, registerPreviewScheme } from './preview-protocol'
import { searchDetailsSchema, searchFilterSchema, searchPageSchema, searchDirectoryPaths } from '../shared/search'
import { saveModelSettingsSchema, suggestionKind } from '../shared/model'
import { ModelSettings } from './model-settings'
import { ModelFeatures } from './model-features'
import { openAiTransport, probeModelConnection } from './model-agent'
import trayIcon from './tray.png?asset'
import appIcon from './app-icon.png?asset'

const smokeProfile = !app.isPackaged ? process.env.APM_SMOKE_PROFILE : undefined
const smokePhase = smokeProfile && process.env.APM_SMOKE_WORKSPACE
const rendererFile = join(__dirname, '../renderer/index.html')
const rendererUrl = !app.isPackaged && process.env.ELECTRON_RENDERER_URL
  ? new URL(process.env.ELECTRON_RENDERER_URL).href
  : pathToFileURL(rendererFile).href
const quickSearchUrl = new URL(rendererUrl)
quickSearchUrl.hash = 'quick-search'

const debugProfile = !app.isPackaged && process.env.APM_WINDOWS_DEBUG === '1'
  ? join(app.getAppPath(), 'artifacts', 'desktop-user-data') : undefined
const profile = smokeProfile ?? debugProfile ?? (process.env.LOCALAPPDATA
  ? join(process.env.LOCALAPPDATA, 'ai-photos-manager') : undefined)
if (profile) {
  mkdirSync(profile, { recursive: true })
  app.setPath('userData', profile)
}

let window: BrowserWindow | undefined
let quickSearch: BrowserWindow | undefined
let showQuickSearchOnLoad = false
let quickSearchDirectoryPickerOpen = false
let service: ServiceClient | undefined
let shuttingDown = false
let exitCode = 0
let tray: Tray | undefined
const hotkeys = {} as Record<GlobalHotkeyTarget, HotkeySettings>
const loginStartup = new LoginStartup(app)
let editShortcuts: EditShortcutSettings
let shortcutCapture = false
function syncShortcutCapture(): void {
  const suspend = shortcutCapture && window?.isFocused()
  for (const setting of Object.values(hotkeys)) {
    if (suspend) setting.suspend()
    else setting.resume()
  }
}
let exitRequest: { id: string; finish: (ready: boolean) => void } | undefined
let lastSmokeReveal: string | undefined
const local = () => service ?? startService()
registerPreviewScheme()
const media = new PreviewProtocol(local)
const modelSettings = new ModelSettings(join(app.getPath('userData'), 'model-settings.json'), safeStorage)
const modelFeatures = new ModelFeatures(local, async () => {
  const config = await modelSettings.runtime()
  return config && { transport: openAiTransport(config), scopes: config.scopes }
})

function startService(): ServiceClient {
  const dbPath = join(app.getPath('userData'), 'library.sqlite3')
  let sharedRuntime = false
  if (app.isPackaged) {
    try {
      const config = JSON.parse(readFileSync(join(process.resourcesPath, 'service', 'MediaLibrary.Service.runtimeconfig.json'), 'utf8'))
      sharedRuntime = Boolean(config.runtimeOptions?.frameworks?.length || config.runtimeOptions?.framework)
    } catch { /* Self-contained packages use their native service executable. */ }
  }
  const command = app.isPackaged && !sharedRuntime
    ? join(process.resourcesPath, 'service', 'MediaLibrary.Service.exe')
    : 'dotnet'
  const serviceFolder = smokeProfile ? 'service-smoke' : 'service'
  const servicePath = join(app.getAppPath(), 'artifacts', serviceFolder, 'MediaLibrary.Service.dll')
  const args = app.isPackaged ? sharedRuntime ? [join(process.resourcesPath, 'service', 'MediaLibrary.Service.dll')] : [] : [servicePath]
  const models = join(app.getAppPath(), 'artifacts', 'models', 'chinese-clip')
  const localModel = !app.isPackaged && existsSync(join(models, 'manifest.json')) ? models : undefined
  const client = new ServiceClient(command, [...args, '--db', dbPath], () => {
    if (service !== client) return
    service = undefined
    if (window && !window.isDestroyed()) window.webContents.send(channels.serviceExitChannel)
  }, { ...process.env, APM_PHOTO_MODEL_DIR: smokeProfile && smokePhase === 'photo_missing'
    ? join(smokeProfile, 'missing-photo-model') : process.env.APM_PHOTO_MODEL_DIR || localModel })
  service = client
  return client
}

function createWindow(): BrowserWindow {
  const mainWindow = new BrowserWindow({
    width: 1120,
    height: 710,
    minWidth: 360,
    minHeight: 440,
    backgroundColor: '#1f1f1f',
    titleBarStyle: 'hidden',
    titleBarOverlay: { color: '#00000000', symbolColor: '#cccccc', height: 44 },
    title: 'MediaDesk',
    icon: appIcon,
    show: !smokeProfile,
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false
    }
  })
  const contents = mainWindow.webContents
  contents.on('before-input-event', (event, input) => {
    if (shortcutCapture) return
    if (input.type !== 'keyDown' || input.isComposing) return
    if (input.key === 'F11' && !input.control && !input.alt && !input.shift && !input.meta) {
      event.preventDefault()
      if (!input.isAutoRepeat) mainWindow.setFullScreen(!mainWindow.isFullScreen())
      return
    }
    if (!input.control || input.meta || input.key === 'AltGraph') return
    const accelerator = normalizeHotkey([
      'Control', ...(input.alt ? ['Alt'] : []), ...(input.shift ? ['Shift'] : []),
      input.key === ' ' ? 'Space' : input.key
    ].join('+'))
    // Let the focused queue consume Ctrl+A before falling back to the configured edit action.
    if (!accelerator || accelerator === defaultEditShortcuts.selectAll) return
    const action = editShortcuts.actionFor(accelerator)
    if (action || editShortcuts.replacesStandardKey(accelerator)) event.preventDefault()
    if (action) contents[action]()
  })
  mainWindow.on('focus', syncShortcutCapture)
  mainWindow.on('blur', syncShortcutCapture)
  contents.on('did-start-loading', () => { shortcutCapture = false; syncShortcutCapture() })
  contents.on('render-process-gone', () => { shortcutCapture = false; syncShortcutCapture() })
  mainWindow.webContents.setWindowOpenHandler(() => ({ action: 'deny' }))
  mainWindow.webContents.on('will-navigate', (event, url) => {
    if (url !== rendererUrl) event.preventDefault()
  })
  mainWindow.on('close', (event) => {
    if (shuttingDown) return
    event.preventDefault()
    mainWindow.hide()
  })
  mainWindow.on('closed', () => { window = undefined })
  window = mainWindow
  return mainWindow
}

async function verifyWindow(mainWindow: BrowserWindow): Promise<void> {
  // Read the Vue health indicator so the smoke covers the normal renderer entry point and IPC.
  const database = await mainWindow.webContents.executeJavaScript(`
    new Promise((resolve, reject) => {
      const read = () => {
        const indicator = document.querySelector('[data-health-state]')
        if (indicator?.dataset.healthState === 'ready') resolve(Number(indicator.dataset.schemaVersion))
        if (indicator?.dataset.healthState === 'error') reject(new Error('Local service unavailable'))
      }
      const observer = new MutationObserver(read)
      observer.observe(document.body, { subtree: true, attributes: true, childList: true })
      read()
      setTimeout(() => reject(new Error('Window health check timed out')), 12000)
    })
  `)
  const result = await (service ?? startService()).health()
  const health = healthSchema.parse('result' in result ? result.result : undefined)
  if (database !== health.database.schemaVersion ||
      !health.database.foreignKeys || !health.database.fts5Trigram) {
    throw new Error('SQLite capabilities did not match the S0 requirements')
  }
  console.log(JSON.stringify({ electron: process.versions.electron, node: process.versions.node, ...health }))
}

function isTrusted(event: IpcMainEvent | IpcMainInvokeEvent, allowQuickSearch = false): boolean {
  if (event.senderFrame !== event.sender.mainFrame) return false
  return event.sender === window?.webContents && event.senderFrame?.url === rendererUrl ||
    allowQuickSearch && event.sender === quickSearch?.webContents && event.senderFrame?.url === quickSearchUrl.href
}

function handle<Params extends unknown[], T>(
  channel: string, args: z.ZodType<Params>, action: (...values: Params) => Result<T> | Promise<Result<T>>,
  allowQuickSearch = false
): void {
  handleFrom(channel, args, (_event, ...values) => action(...values), allowQuickSearch)
}

function handleFrom<Params extends unknown[], T>(
  channel: string, args: z.ZodType<Params>,
  action: (event: IpcMainInvokeEvent, ...values: Params) => Result<T> | Promise<Result<T>>,
  allowQuickSearch: boolean
): void {
  ipcMain.handle(channel, (event, ...values: unknown[]) => {
    const parsed = args.safeParse(values)
    if (!isTrusted(event, allowQuickSearch) || !parsed.success) {
      return { error: { code: 'INVALID_ARGUMENT', message: '无效的本地请求。', retryable: false } }
    }
    return action(event, ...parsed.data)
  })
}

function registerIpc(): void {
  const noArgs = z.tuple([])
  handle(channels.getLoginStartupChannel, noArgs, () => {
    try { return { result: loginStartup.get() } }
    catch (error) { return { error: { code: 'SETTINGS_READ_FAILED',
      message: error instanceof Error ? error.message : '无法读取开机自动启动设置。', retryable: true } } }
  })
  handle(channels.setLoginStartupChannel, z.tuple([z.boolean()]), (enabled) => {
    try { return { result: loginStartup.set(enabled) } }
    catch (error) { return { error: { code: 'SETTINGS_WRITE_FAILED',
      message: error instanceof Error ? error.message : '无法修改开机自动启动设置。', retryable: true } } }
  })
  ipcMain.on(channels.windowThemeChannel, (event, theme: unknown) => {
    if (!isTrusted(event, true) || (theme !== 'dark' && theme !== 'light')) return
    const owner = BrowserWindow.fromWebContents(event.sender)
    if (owner && owner !== quickSearch) {
      owner.setTitleBarOverlay({ color: '#00000000', symbolColor: theme === 'dark' ? '#cccccc' : '#3b3b3b', height: 44 })
    }
    owner?.setBackgroundColor(theme === 'dark' ? '#1f1f1f' : '#ffffff')
  })
  ipcMain.on(channels.quickSearchSizeChannel, (event, controlsHeight: unknown, hasResults: unknown) => {
    if (!quickSearch || event.sender !== quickSearch.webContents || !isTrusted(event, true) ||
        typeof controlsHeight !== 'number' || !Number.isInteger(controlsHeight) || controlsHeight < 1 ||
        typeof hasResults !== 'boolean') return
    const bounds = quickSearch.getBounds()
    const workArea = screen.getDisplayMatching(bounds).workArea
    const height = Math.min(workArea.height, hasResults ? Math.max(530, controlsHeight) : controlsHeight)
    const y = Math.max(workArea.y, Math.min(bounds.y, workArea.y + workArea.height - height))
    if (bounds.height !== height || bounds.y !== y) quickSearch.setBounds({ ...bounds, height, y })
  })
  const importFiles = (paths: string[]) => local().request('asset.import', { paths }, importSchema)
  const importPaths = (paths: string[]) => importAssetPaths(paths, importFiles,
    (path) => local().request('workspace.addImportFolder', { path }, z.null()))
  const absolutePath = z.string().max(32767).refine(isAbsolute)
  const paths = z.array(absolutePath).min(1).max(1000)
  handle(channels.healthChannel, noArgs, () => local().health())
  handle(channels.workspaceChannel, noArgs, () => local().request('workspace.load', {}, workspaceSchema))
  handle(channels.removeQueueAssetsChannel, z.tuple([z.array(assetId).min(1)]),
    (assetIds) => local().request('workspace.removeAssets', { assetIds }, z.number().int().nonnegative()))
  handle(channels.removeQueueFolderChannel, z.tuple([absolutePath, absolutePath]),
    (root, path) => local().request('workspace.removeFolder', { root, path }, z.number().int().nonnegative()))
  handle(channels.browseFolderChannel, z.tuple([absolutePath, absolutePath]),
    (root, path) => local().request('workspace.browseFolder', { root, path }, folderEntriesSchema))
  handle(channels.folderDetailsChannel, z.tuple([absolutePath, absolutePath]),
    (root, path) => local().request('workspace.describeFolder', { root, path }, folderDetailsSchema))
  handle(channels.setCurrentChannel, z.tuple([assetId.nullable()]),
    (id) => local().request('workspace.setCurrent', { assetId: id }, draftSchema.nullable()))
  handle(channels.getDraftChannel, z.tuple([assetId]),
    (id) => local().request('draft.get', { assetId: id }, draftSchema))
  handle(channels.saveDraftChannel, z.tuple([saveDraftSchema]),
    (input) => local().request('draft.save', input, draftSchema))
  handle(channels.saveSourcePropertiesChannel, z.tuple([assetId, revision]), (id, expectedRevision) =>
    local().request('draft.saveSourceProperties', { assetId: id, expectedRevision }, savedSourcePropertiesSchema, 300_000))
  handle(channels.importAssetsChannel, z.tuple([paths]), importPaths)
  handle(channels.chooseAssetPathsChannel, z.tuple([z.literal('folder').optional()]), async (mode?) => {
    let selected
    try {
      selected = await dialog.showOpenDialog(window!, {
        title: mode === 'folder' ? '选择要导入的素材文件夹' : '导入照片或视频',
        properties: [mode === 'folder' ? 'openDirectory' : 'openFile', 'multiSelections']
      })
    } catch {
      return { error: { code: 'IMPORT_UNAVAILABLE', message: '无法打开选择器。', retryable: true } }
    }
    return { result: selected.canceled ? [] : selected.filePaths }
  })
  handle(channels.getHotkeyChannel, z.tuple([globalHotkeyTarget.optional()]),
    (target?) => ({ result: hotkeys[target ?? 'search'].state() }))
  ipcMain.on(channels.shortcutCaptureChannel, (event, active: unknown) => {
    if (!isTrusted(event) || typeof active !== 'boolean') return
    shortcutCapture = active
    syncShortcutCapture()
  })
  handle(channels.setHotkeyChannel, z.tuple([z.string().max(80), globalHotkeyTarget.optional()]),
    (accelerator, target?) => hotkeys[target ?? 'search'].update(accelerator))
  handle(channels.getEditShortcutsChannel, noArgs, () => ({ result: editShortcuts.state() }))
  ipcMain.on(channels.unhandledControlAChannel, (event) => {
    if (!isTrusted(event)) return
    const action = editShortcuts.actionFor(defaultEditShortcuts.selectAll)
    if (action) event.sender[action]()
  })
  handle(channels.setEditShortcutsChannel, z.tuple([editShortcutsSchema]),
    (shortcuts) => editShortcuts.update(shortcuts, hotkeys.search.state().accelerator,
      hotkeys.main.state().accelerator))
  handle(channels.viewActionChannel, z.tuple([viewActionSchema]), (action) => {
    const mainWindow = window!
    const contents = mainWindow.webContents
    switch (action) {
      case 'reload': contents.reload(); break
      case 'forceReload': contents.reloadIgnoringCache(); break
      case 'toggleDevTools': contents.toggleDevTools(); break
    }
    return { result: null }
  })
  handleFrom(channels.previewChannel, z.tuple([assetId, previewKind]), (event, id, kind) =>
    event.sender === quickSearch?.webContents && kind !== 'thumbnail'
      ? { error: { code: 'INVALID_ARGUMENT', message: '快捷搜索仅提供缩略图。', retryable: false } }
      : media.issue(id, kind, event.sender === quickSearch?.webContents ? event.sender.id : undefined), true)
  handleFrom(channels.releasePreviewChannel, z.tuple([assetId, z.boolean()]), async (event, id, thumbnail) => {
    await media.release(id, thumbnail, event.sender === quickSearch?.webContents ? event.sender.id : undefined)
    return { result: null }
  }, true)
  handle(channels.proxyChannel, z.tuple([assetId, z.enum(['request', 'status', 'cancel'])]), (id, action) => {
    const method = { request: 'preview.requestProxy', status: 'preview.proxyStatus', cancel: 'preview.cancelProxy' }[action]
    return local().request(method, { assetId: id }, proxyStatus.nullable())
  })
  handle(channels.clearPreviewCacheChannel, noArgs, () =>
    local().request('preview.clearCache', {}, z.number().int().nonnegative()))
  handle(channels.previewCacheDirectoryChannel, noArgs, () =>
    local().request('preview.cacheDirectory', {}, absolutePath))
  handle(channels.choosePreviewCacheDirectoryChannel, noArgs, async (): Promise<Result<string | null>> => {
    const current = await local().request('preview.cacheDirectory', {}, absolutePath)
    const selection = await dialog.showOpenDialog(window!, {
      title: '选择预览缓存保存文件夹', properties: ['openDirectory', 'createDirectory'],
      ...('result' in current ? { defaultPath: current.result } : {})
    })
    if (selection.canceled || !selection.filePaths.length) return { result: null }
    return local().request('preview.setCacheDirectory', { path: selection.filePaths[0] }, absolutePath)
  })
  const locateAsset = (id: string) =>
    local().request('asset.revealPath', { assetId: id }, z.object({ path: z.string().refine(isAbsolute) }))
  handle(channels.revealChannel, z.tuple([assetId]), async (id) => {
    const location = await locateAsset(id)
    if ('error' in location) return location
    try { await shell.showItemInFolder(location.result.path) }
    catch { return { error: { code: 'SOURCE_UNAVAILABLE', message: '无法在资源管理器中定位素材，请确认文件仍可访问。', retryable: true } } }
    if (smokePhase) lastSmokeReveal = location.result.path
    return { result: null }
  }, true)
  handle(channels.openAssetFolderChannel, z.tuple([assetId]), async (id) => {
    const location = await locateAsset(id)
    if ('error' in location) return location
    return { result: await openFolderInExplorer(location.result.path, item => shell.showItemInFolder(item)) }
  })
  handle(channels.openQueueItemFolderChannel, z.tuple([absolutePath]), async (path): Promise<Result<null>> => {
    try {
      await openQueueItemInExplorer(path, item => shell.showItemInFolder(item), folder => shell.openPath(folder))
      return { result: null }
    } catch (error) {
      console.error('Opening queue item location failed:', error)
      return { error: { code: 'SOURCE_UNAVAILABLE', message: '无法打开所在文件夹，请检查文件或目录是否仍可访问。', retryable: true } }
    }
  })
  handle(channels.listDirectoriesChannel, noArgs, () => local().request('archive.listDirectories', {}, z.array(directorySchema)))
  handle(channels.chooseDirectoryChannel, z.tuple([z.boolean()]), async (root): Promise<Result<ArchiveDirectory | null>> => {
    try {
      const choice = smokeProfile && smokePhase === 'media_prepare' && root
        ? { canceled: false, filePaths: [join(smokeProfile, '归档')] }
        : await dialog.showOpenDialog(window!, {
        title: root ? '添加归档根目录' : '选择已有归档子目录',
        properties: ['openDirectory']
      })
      const path = choice.filePaths[0]
      if (choice.canceled || !path) return { result: null }
      return local().request(root ? 'archive.addRoot' : 'archive.addDirectory', { path }, directorySchema)
    } catch {
      return { error: { code: 'DESTINATION_UNAVAILABLE', message: '无法打开目录选择器。', retryable: true } }
    }
  })
  handle(channels.archivePreviewChannel, z.tuple([assetId, z.number().int().nonnegative().max(2147483647), assetId]),
    (id, archiveRevision, directoryId) => local().request('archive.preview', { assetId: id, archiveRevision, directoryId }, archivePlanSchema))
  handle(channels.archiveCommitChannel, z.tuple([assetId, planToken]), (operationId, token) =>
    local().request('archive.commit', { operationId, planToken: token }, archiveStatusSchema))
  handle(channels.archiveStatusChannel, z.tuple([assetId]), (operationId) =>
    local().request('archive.status', { operationId }, archiveStatusSchema))
  handle(channels.retryPropertiesChannel, z.tuple([assetId]), (operationId) =>
    local().request('archive.retryProperties', { operationId }, archiveStatusSchema))
  handleFrom(channels.searchIndexDirectoriesChannel, z.tuple([searchDirectoryPaths, z.uuid().optional()]), async (event, paths, requestId?: string) => {
    const reply = await local().request('search.indexDirectories', { directoryPaths: paths, includeSubdirectories: true }, searchPageSchema, 10_000, progress => {
      if (requestId && !event.sender.isDestroyed()) event.sender.send(channels.searchIndexProgressChannel, requestId, progress)
    })
    return 'error' in reply ? reply : { result: null }
  }, true)
  handle(channels.searchFilterChannel, z.tuple([searchFilterSchema]), (filter) =>
    local().request('search.filter', filter, searchPageSchema), true)
  handle(channels.defaultSearchDirectoryChannel, noArgs, async (): Promise<Result<string | null>> => {
    const workspace = await local().request('workspace.load', {}, workspaceSchema)
    if ('error' in workspace) return workspace
    const id = workspace.result.draft?.data.directoryId
    if (!id) return { result: null }
    const directories = await local().request('archive.listDirectories', {}, z.array(directorySchema))
    if ('error' in directories) return directories
    return { result: directories.result.find((directory) => directory.directoryId === id)?.fullPath ?? null }
  }, true)
  handle(channels.openSearchDirectoryChannel, z.tuple([absolutePath]), async (path): Promise<Result<null>> => {
    try {
      await openDirectoryInExplorer(path, async folder => {
        const error = await shell.openPath(folder)
        if (error) throw new Error(error)
      })
      return { result: null }
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code
      const message = code === 'EACCES' || code === 'EPERM' ? '没有读取此目录的权限，请在资源管理器中检查目录权限。'
        : code === 'ENOENT' || code === 'ENOTDIR' ? '目录已不存在或磁盘未连接，请重新选择搜索目录。'
          : '无法启动资源管理器或访问此目录，请确认目录可用。'
      return { error: { code: 'SOURCE_UNAVAILABLE', message: `${message}\n${path}`, retryable: true } }
    }
  }, true)
  handleFrom(channels.chooseSearchDirectoriesChannel, noArgs, async (event): Promise<Result<string[]>> => {
    const owner = BrowserWindow.fromWebContents(event.sender)!
    const fromQuickSearch = owner === quickSearch
    try {
      if (smokeProfile && (smokePhase === 'search' || smokePhase === 'model'))
        return { result: [join(smokeProfile, '分类筛选归档')] }
      if (fromQuickSearch) quickSearchDirectoryPickerOpen = true
      const choice = await dialog.showOpenDialog(owner, {
        title: '添加搜索目录', properties: ['openDirectory', 'multiSelections']
      })
      return { result: choice.canceled ? [] : await Promise.all(choice.filePaths.map((path) => realpath(path))) }
    } catch {
      return { error: { code: 'DESTINATION_UNAVAILABLE', message: '无法读取所选搜索目录。', retryable: true } }
    } finally {
      if (fromQuickSearch) quickSearchDirectoryPickerOpen = false
    }
  }, true)
  handle(channels.searchDetailsChannel, z.tuple([assetId]), (id) =>
    local().request('search.getDetails', { assetId: id }, searchDetailsSchema))
  handle(channels.modelSettingsChannel, noArgs, async () => ({ result: await modelSettings.view() }))
  handle(channels.modelConnectionChannel, noArgs, async () => {
    const settings = await modelSettings.view()
    if (!settings.enabled) return { result: { model: settings.model, state: 'disabled' as const } }
    const config = await modelSettings.runtime()
    if (!config) return { result: { model: settings.model, state: 'unconfigured' as const,
      message: '请检查 API Key 是否已保存并且可以读取。' } }
    return { result: await probeModelConnection(config) }
  })
  handle(channels.saveModelSettingsChannel, z.tuple([saveModelSettingsSchema]), async (settings) => {
    try { return { result: await modelSettings.save(settings) } }
    catch { return { error: { code: 'SETTINGS_WRITE_FAILED', message: '无法保存模型设置或加密密钥。', retryable: true } } }
  })
  handle(channels.modelSuggestChannel, z.tuple([assetId, suggestionKind]), (id, kind) => modelFeatures.suggest(id, kind))
  handle(channels.modelRemarkChannel, z.tuple([z.string().trim().min(1).max(4000)]),
    (text) => modelFeatures.suggestBatchRemark(text))
  handleFrom(channels.modelSearchChannel, z.tuple([z.string().trim().min(1).max(2000), searchFilterSchema.optional(), z.uuid().optional()]),
    (event, text, filters?, requestId?) => {
      if (!filters?.directoryPaths) return { error: {
        code: 'INVALID_ARGUMENT', message: '请添加查找目录。', retryable: false
      } }
      return modelFeatures.search(text, filters, (step) => {
        if (requestId && !event.sender.isDestroyed()) event.sender.send(channels.agentSearchStepChannel, requestId, step)
      })
    }, true)
  ipcMain.handle(channels.requestExitChannel, (event, ...args: unknown[]) => {
    if (isTrusted(event) && !args.length) app.quit()
  })
  ipcMain.on(channels.exitReadyChannel, (event, id: unknown, ready: unknown) => {
    if (isTrusted(event) && exitRequest && id === exitRequest.id && typeof ready === 'boolean') {
      exitRequest.finish(ready)
    }
  })
}

function showWindow(): void {
  if (!window || window.isDestroyed()) return
  if (window.isMinimized()) window.restore()
  window.show()
  window.focus()
}

function showMainPage(): void {
  if (shuttingDown || !window || window.isDestroyed()) return
  showQuickSearchOnLoad = false
  quickSearch?.hide()
  showWindow()
  window.webContents.send(channels.showMainPageChannel)
}

function showQuickSearch(): void {
  if (shuttingDown) return
  showQuickSearchOnLoad = true
  if (quickSearch && !quickSearch.isDestroyed()) {
    quickSearch.show()
    quickSearch.focus()
    return
  }
  const { x, y, width, height } = screen.getDisplayNearestPoint(screen.getCursorScreenPoint()).workArea
  const popupWidth = Math.min(760, width)
  const popupHeight = Math.min(530, height)
  const popup = new BrowserWindow({
    title: 'MediaDesk · 快捷搜索',
    icon: appIcon,
    width: popupWidth, height: Math.min(140, popupHeight),
    x: x + Math.round((width - popupWidth) / 2),
    y: y + Math.round((height - popupHeight) * .28),
    show: false, frame: false, resizable: false, skipTaskbar: true, alwaysOnTop: true,
    backgroundColor: '#1f1f1f',
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true, sandbox: true, nodeIntegration: false
    }
  })
  quickSearch = popup
  popup.on('blur', () => {
    if (!quickSearchDirectoryPickerOpen) popup.hide()
  })
  popup.on('hide', () => { showQuickSearchOnLoad = false })
  const contents = popup.webContents
  contents.setWindowOpenHandler(() => ({ action: 'deny' }))
  contents.on('will-navigate', (event, url) => {
    if (url !== quickSearchUrl.href) event.preventDefault()
  })
  contents.on('before-input-event', (event, input) => {
    if (input.type === 'keyDown' && input.key === 'Escape' && !input.isComposing) {
      event.preventDefault()
      popup.hide()
    }
  })
  popup.on('close', (event) => {
    if (shuttingDown) return
    event.preventDefault()
    popup.hide()
  })
  popup.on('closed', () => { if (quickSearch === popup) quickSearch = undefined })
  void popup.loadURL(quickSearchUrl.href).then(() => {
    if (!popup.isDestroyed() && !shuttingDown && showQuickSearchOnLoad) popup.show()
  }).catch(() => { if (!popup.isDestroyed()) popup.destroy() })
}

function createTray(): Tray {
  const item = new Tray(nativeImage.createFromPath(trayIcon))
  item.setToolTip('MediaDesk')
  item.setContextMenu(Menu.buildFromTemplate([
    { label: '打开', click: showWindow },
    { label: '快捷搜索', click: showQuickSearch },
    { label: '退出', click: () => app.quit() }
  ]))
  item.on('double-click', showWindow)
  return item
}

function flushWindow(): Promise<boolean> {
  if (!window || window.isDestroyed() || window.webContents.isDestroyed() || window.webContents.isCrashed()) {
    return Promise.resolve(true)
  }
  const contents = window.webContents
  if (contents.isLoadingMainFrame()) return Promise.resolve(true)
  return new Promise((resolve) => {
    const id = randomUUID()
    const timeout = setTimeout(() => exitRequest?.finish(false), 15_000)
    exitRequest = { id, finish: (ready) => {
      clearTimeout(timeout)
      exitRequest = undefined
      resolve(ready)
    } }
    contents.send(channels.exitRequestedChannel, id)
  })
}

async function quit(): Promise<void> {
  try {
    if (!exitCode && !await flushWindow()) {
      window?.webContents.send(channels.exitCancelledChannel)
      showWindow()
      return
    }
    globalShortcut.unregisterAll()
    tray?.destroy()
    try {
      await service?.close()
    } catch {
      console.error('Local service cleanup failed.')
      if (smokeProfile) exitCode = 1
    } finally {
      app.exit(exitCode)
    }
  } finally {
    shuttingDown = false
  }
}

if (!app.requestSingleInstanceLock()) {
  app.exit(0)
} else {
  app.on('second-instance', showWindow)
  app.on('window-all-closed', () => app.quit())
  app.on('before-quit', (event) => {
    event.preventDefault()
    if (shuttingDown) return
    shuttingDown = true
    void quit()
  })
  void app.whenReady().then(async () => {
    Menu.setApplicationMenu(null)
    const settingsPath = app.getPath('userData')
    hotkeys.search = new HotkeySettings(join(settingsPath, 'hotkey.txt'), showQuickSearch, defaultGlobalHotkeys.search,
      (key) => Boolean(editShortcuts?.uses(key) || hotkeys.main?.state().accelerator === key))
    editShortcuts = new EditShortcutSettings(join(settingsPath, 'edit-shortcuts.json'), hotkeys.search.state().accelerator)
    hotkeys.main = new HotkeySettings(join(settingsPath, 'main-hotkey.txt'), showMainPage, defaultGlobalHotkeys.main,
      (key) => editShortcuts.uses(key) || hotkeys.search.state().accelerator === key)
    registerIpc()
    media.register()
    if (!smokeProfile || smokePhase === 'lifecycle') tray = createTray()
    const mainWindow = createWindow()
    await mainWindow.loadURL(rendererUrl)
    if (smokeProfile) {
      await verifyWindow(mainWindow)
      if (smokePhase === 'lifecycle') {
        await verifyLifecycle(mainWindow, tray!, showMainPage)
      } else if (smokePhase === 'media_prepare' || smokePhase === 'media_archive') {
        await verifyMedia(mainWindow, smokeProfile, smokePhase)
      } else if (smokePhase === 'search') {
        await verifySearch(mainWindow, smokeProfile, local(), () => lastSmokeReveal, showQuickSearch)
      } else if (smokePhase === 'model') {
        await verifyModel(mainWindow, smokeProfile, local(), () => lastSmokeReveal)
      } else if (smokePhase === 'photo_missing' || smokePhase === 'photo_scene') {
        await verifyPhotoSearch(mainWindow, smokeProfile, local(), smokePhase === 'photo_missing')
      } else if (smokePhase) {
        await verifyWorkspace(mainWindow, smokeProfile, smokePhase)
      }
      app.quit()
    }
  }).catch((error: unknown) => {
    console.error('Desktop initialization or smoke verification failed.', smokeProfile ? error : '')
    exitCode = 1
    app.quit()
  })
}
