import { z } from 'zod'
import type { AssetDraft, FolderDetails, FolderEntry, Hotkey, ImportItem, SaveDraft, SavedSourceProperties, Workspace } from './workspace'
import type { ArchiveDirectory, ArchivePlan, ArchiveStatus } from './archive'
import type { PreviewKind, ProxyStatus } from './preview'
import type { AgentSearchResult, SearchDetails, SearchFilter, SearchPage, DirectoryIndexProgress } from './search'
import type { ModelConnection, ModelSettingsView, SaveModelSettings, SuggestionKind } from './model'
import type { EditShortcuts, GlobalHotkeyTarget, ViewAction } from './desktop-controls'

export const healthSchema = z.object({
  protocolVersion: z.literal(1),
  runtime: z.string(),
  database: z.object({
    status: z.literal('ready'),
    sqliteVersion: z.string(),
    schemaVersion: z.number().int().positive(),
    journalMode: z.literal('wal'),
    foreignKeys: z.boolean(),
    fts5Trigram: z.boolean()
  }),
  capabilities: z.object({
    wic: z.enum(['not_implemented', 'available', 'unavailable']),
    ffmpeg: z.enum(['not_implemented', 'available', 'unavailable']),
    photoModel: z.enum(['not_implemented', 'loading', 'ready', 'missing', 'invalid'])
  })
})

export const serviceErrorSchema = z.object({
  code: z.string(),
  message: z.string(),
  retryable: z.boolean()
})

export type Health = z.infer<typeof healthSchema>
export type ServiceError = z.infer<typeof serviceErrorSchema>
export type Result<T> = { result: T } | { error: ServiceError }
export type HealthResult = Result<Health>

export interface DesktopApi {
  resizeQuickSearch(controlsHeight: number, hasResults: boolean): void
  health(): Promise<HealthResult>
  onLocalServiceExited(callback: () => void): () => void
  loadWorkspace(): Promise<Result<Workspace>>
  removeQueueAssets(assetIds: string[]): Promise<Result<number>>
  removeQueueFolder(root: string, path: string): Promise<Result<number>>
  browseFolder(root: string, path: string): Promise<Result<FolderEntry[]>>
  folderDetails(root: string, path: string): Promise<Result<FolderDetails>>
  setCurrent(assetId: string | null): Promise<Result<AssetDraft | null>>
  getDraft(assetId: string): Promise<Result<AssetDraft>>
  saveDraft(input: SaveDraft): Promise<Result<AssetDraft>>
  saveSourceProperties(assetId: string, expectedRevision: number): Promise<Result<SavedSourceProperties>>
  chooseAssetPaths(mode?: 'folder'): Promise<Result<string[]>>
  importAssets(paths: string[]): Promise<Result<ImportItem[]>>
  onAssetsDropped(callback: (paths: string[]) => void): () => void
  getHotkey(target?: GlobalHotkeyTarget): Promise<Result<Hotkey>>
  getLoginStartup(): Promise<Result<boolean>>
  setLoginStartup(enabled: boolean): Promise<Result<boolean>>
  setHotkey(accelerator: string, target?: GlobalHotkeyTarget): Promise<Result<Hotkey>>
  setShortcutCapture(active: boolean): void
  onShowMainPage(callback: () => void): () => void
  getEditShortcuts(): Promise<Result<EditShortcuts>>
  setEditShortcuts(shortcuts: EditShortcuts): Promise<Result<EditShortcuts>>
  viewAction(action: ViewAction): Promise<Result<null>>
  requestExit(): Promise<void>
  onExitRequested(callback: () => Promise<boolean>): () => void
  onExitCancelled(callback: () => void): () => void
  preview(assetId: string, kind: PreviewKind): Promise<Result<string>>
  releasePreview(assetId: string, thumbnail?: boolean): Promise<Result<null>>
  proxy(assetId: string, action: 'request' | 'status' | 'cancel'): Promise<Result<ProxyStatus | null>>
  clearPreviewCache(): Promise<Result<number>>
  getPreviewCacheDirectory(): Promise<Result<string>>
  choosePreviewCacheDirectory(): Promise<Result<string | null>>
  revealAsset(assetId: string): Promise<Result<null>>
  openAssetFolder(assetId: string): Promise<Result<{ folder: string; launched: boolean }>>
  openQueueItemFolder(path: string): Promise<Result<null>>
  chooseDirectory(root: boolean): Promise<Result<ArchiveDirectory | null>>
  listDirectories(): Promise<Result<ArchiveDirectory[]>>
  archivePreview(assetId: string, archiveRevision: number, directoryId: string): Promise<Result<ArchivePlan>>
  archiveCommit(operationId: string, planToken: string): Promise<Result<ArchiveStatus>>
  archiveStatus(operationId: string): Promise<Result<ArchiveStatus>>
  retryProperties(operationId: string): Promise<Result<ArchiveStatus>>
  filterAssets(filter: SearchFilter): Promise<Result<SearchPage>>
  indexSearchDirectories(paths: string[], requestId?: string): Promise<Result<null>>
  onSearchIndexProgress(callback: (requestId: string, progress: DirectoryIndexProgress) => void): () => void
  chooseSearchDirectories(): Promise<Result<string[]>>
  getDefaultSearchDirectory(): Promise<Result<string | null>>
  openSearchDirectory(path: string): Promise<Result<null>>
  getAssetDetails(assetId: string): Promise<Result<SearchDetails>>
  getModelSettings(): Promise<Result<ModelSettingsView>>
  checkModelConnection(): Promise<Result<ModelConnection>>
  saveModelSettings(input: SaveModelSettings): Promise<Result<ModelSettingsView>>
  modelSuggest(assetId: string, kind: SuggestionKind): Promise<Result<AssetDraft>>
  suggestBatchRemark(text: string): Promise<Result<string>>
  intelligentSearch(query: string, filters?: SearchFilter, requestId?: string): Promise<Result<AgentSearchResult>>
  onAgentSearchStep(callback: (requestId: string, step: string) => void): () => void
}
