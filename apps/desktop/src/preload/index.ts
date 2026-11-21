import { contextBridge, ipcRenderer, webUtils } from 'electron'
import * as channels from '../shared/channels'
import { isControlA } from '../shared/keyboard'
import type { DesktopApi } from '../shared/health'
import type { ColorTheme } from '../shared/desktop-controls'

const api: DesktopApi = {
  resizeQuickSearch: (controlsHeight, hasResults) => ipcRenderer.send(channels.quickSearchSizeChannel, controlsHeight, hasResults),
  health: () => ipcRenderer.invoke(channels.healthChannel),
  onLocalServiceExited: (callback) => {
    const listener = () => callback()
    ipcRenderer.on(channels.serviceExitChannel, listener)
    return () => ipcRenderer.removeListener(channels.serviceExitChannel, listener)
  },
  loadWorkspace: () => ipcRenderer.invoke(channels.workspaceChannel),
  removeQueueAssets: (assetIds) => ipcRenderer.invoke(channels.removeQueueAssetsChannel, assetIds),
  removeQueueFolder: (root, path) => ipcRenderer.invoke(channels.removeQueueFolderChannel, root, path),
  browseFolder: (root, path) => ipcRenderer.invoke(channels.browseFolderChannel, root, path),
  folderDetails: (root, path) => ipcRenderer.invoke(channels.folderDetailsChannel, root, path),
  setCurrent: (assetId) => ipcRenderer.invoke(channels.setCurrentChannel, assetId),
  getDraft: (assetId) => ipcRenderer.invoke(channels.getDraftChannel, assetId),
  saveDraft: (input) => ipcRenderer.invoke(channels.saveDraftChannel, input),
  saveSourceProperties: (assetId, expectedRevision) =>
    ipcRenderer.invoke(channels.saveSourcePropertiesChannel, assetId, expectedRevision),
  chooseAssetPaths: (mode) => ipcRenderer.invoke(channels.chooseAssetPathsChannel, mode),
  importAssets: (paths) => ipcRenderer.invoke(channels.importAssetsChannel, paths),
  onAssetsDropped: (callback) => {
    const allowDrop = (event: DragEvent) => {
      if (event.dataTransfer?.types.includes('Files')) event.preventDefault()
    }
    const drop = (event: DragEvent) => {
      allowDrop(event)
      const zone = event.target instanceof Element && event.target.closest('[data-import-drop]')
      if (!zone || !event.dataTransfer?.files.length) return
      const paths = Array.from(event.dataTransfer.files, (file) => webUtils.getPathForFile(file)).filter(Boolean)
      if (paths.length) callback(paths)
    }
    window.addEventListener('dragover', allowDrop)
    window.addEventListener('drop', drop)
    return () => {
      window.removeEventListener('dragover', allowDrop)
      window.removeEventListener('drop', drop)
    }
  },
  getHotkey: (target) => ipcRenderer.invoke(channels.getHotkeyChannel, target),
  getLoginStartup: () => ipcRenderer.invoke(channels.getLoginStartupChannel),
  setLoginStartup: (enabled) => ipcRenderer.invoke(channels.setLoginStartupChannel, enabled),
  setHotkey: (accelerator, target) => ipcRenderer.invoke(channels.setHotkeyChannel, accelerator, target),
  setShortcutCapture: (active) => ipcRenderer.send(channels.shortcutCaptureChannel, active),
  onShowMainPage: (callback) => {
    const listener = () => callback()
    ipcRenderer.on(channels.showMainPageChannel, listener)
    return () => ipcRenderer.removeListener(channels.showMainPageChannel, listener)
  },
  getEditShortcuts: () => ipcRenderer.invoke(channels.getEditShortcutsChannel),
  setEditShortcuts: (shortcuts) => ipcRenderer.invoke(channels.setEditShortcutsChannel, shortcuts),
  viewAction: (action) => ipcRenderer.invoke(channels.viewActionChannel, action),
  requestExit: () => ipcRenderer.invoke(channels.requestExitChannel),
  onExitRequested: (callback) => {
    const listener = async (_event: Electron.IpcRendererEvent, id: string) => {
      let ready = false
      try { ready = await callback() } catch { /* Keep the window open if saving fails. */ }
      ipcRenderer.send(channels.exitReadyChannel, id, ready)
    }
    ipcRenderer.on(channels.exitRequestedChannel, listener)
    return () => ipcRenderer.removeListener(channels.exitRequestedChannel, listener)
  },
  onExitCancelled: (callback) => {
    const listener = () => callback()
    ipcRenderer.on(channels.exitCancelledChannel, listener)
    return () => ipcRenderer.removeListener(channels.exitCancelledChannel, listener)
  },
  preview: (assetId, kind) => ipcRenderer.invoke(channels.previewChannel, assetId, kind),
  releasePreview: (assetId, thumbnail = false) => ipcRenderer.invoke(channels.releasePreviewChannel, assetId, thumbnail),
  proxy: (assetId, action) => ipcRenderer.invoke(channels.proxyChannel, assetId, action),
  clearPreviewCache: () => ipcRenderer.invoke(channels.clearPreviewCacheChannel),
  getPreviewCacheDirectory: () => ipcRenderer.invoke(channels.previewCacheDirectoryChannel),
  choosePreviewCacheDirectory: () => ipcRenderer.invoke(channels.choosePreviewCacheDirectoryChannel),
  revealAsset: (assetId) => ipcRenderer.invoke(channels.revealChannel, assetId),
  openAssetFolder: (assetId) => ipcRenderer.invoke(channels.openAssetFolderChannel, assetId),
  openQueueItemFolder: (path) => ipcRenderer.invoke(channels.openQueueItemFolderChannel, path),
  chooseDirectory: (root) => ipcRenderer.invoke(channels.chooseDirectoryChannel, root),
  listDirectories: () => ipcRenderer.invoke(channels.listDirectoriesChannel),
  archivePreview: (assetId, revision, directoryId) => ipcRenderer.invoke(channels.archivePreviewChannel, assetId, revision, directoryId),
  archiveCommit: (operationId, token) => ipcRenderer.invoke(channels.archiveCommitChannel, operationId, token),
  archiveStatus: (operationId) => ipcRenderer.invoke(channels.archiveStatusChannel, operationId),
  retryProperties: (operationId) => ipcRenderer.invoke(channels.retryPropertiesChannel, operationId),
  filterAssets: (filter) => ipcRenderer.invoke(channels.searchFilterChannel, filter),
  indexSearchDirectories: (paths, requestId) => ipcRenderer.invoke(channels.searchIndexDirectoriesChannel, paths, requestId),
  onSearchIndexProgress: (callback) => {
    const listener = (_event: Electron.IpcRendererEvent, requestId: string, progress: Parameters<typeof callback>[1]) => callback(requestId, progress)
    ipcRenderer.on(channels.searchIndexProgressChannel, listener)
    return () => ipcRenderer.removeListener(channels.searchIndexProgressChannel, listener)
  },
  chooseSearchDirectories: () => ipcRenderer.invoke(channels.chooseSearchDirectoriesChannel),
  getDefaultSearchDirectory: () => ipcRenderer.invoke(channels.defaultSearchDirectoryChannel),
  openSearchDirectory: (path) => ipcRenderer.invoke(channels.openSearchDirectoryChannel, path),
  getAssetDetails: (assetId) => ipcRenderer.invoke(channels.searchDetailsChannel, assetId),
  getModelSettings: () => ipcRenderer.invoke(channels.modelSettingsChannel),
  checkModelConnection: () => ipcRenderer.invoke(channels.modelConnectionChannel),
  saveModelSettings: (input) => ipcRenderer.invoke(channels.saveModelSettingsChannel, input),
  modelSuggest: (assetId, kind) => ipcRenderer.invoke(channels.modelSuggestChannel, assetId, kind),
  suggestBatchRemark: (text) => ipcRenderer.invoke(channels.modelRemarkChannel, text),
  intelligentSearch: (query, filters, requestId) => ipcRenderer.invoke(channels.modelSearchChannel, query, filters, requestId),
  onAgentSearchStep: (callback) => {
    const listener = (_event: Electron.IpcRendererEvent, requestId: string, step: string) => callback(requestId, step)
    ipcRenderer.on(channels.agentSearchStepChannel, listener)
    return () => ipcRenderer.removeListener(channels.agentSearchStepChannel, listener)
  }
}

contextBridge.exposeInMainWorld('setWindowTheme', (theme: ColorTheme) => ipcRenderer.send(channels.windowThemeChannel, theme))

if (!process.argv.includes('--apm-ui-preview')) {
  window.addEventListener('keydown', (event) => {
    if (event.isTrusted && !event.defaultPrevented && isControlA(event)) {
      event.preventDefault()
      ipcRenderer.send(channels.unhandledControlAChannel)
    }
  })
  contextBridge.exposeInMainWorld('mediaLibrary', api)
}
