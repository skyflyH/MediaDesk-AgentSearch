import { z } from 'zod'
import { archiveStatusSchema } from './archive'
import { draftSuggestions } from './model'

export const assetId = z.uuid()
const intent = z.enum(['unchanged', 'set', 'clear'])
export const draftDataSchema = z.strictObject({
  name: z.string().max(255),
  finalized: z.boolean(),
  remark: z.strictObject({ intent, value: z.string().max(4000) }),
  authors: z.strictObject({ intent, value: z.array(z.string().min(1).max(120)).max(50) }),
  keywords: z.string().max(1000),
  previewPositionMs: z.number().int().nonnegative(),
  directoryId: z.uuid().nullable()
})

export const draftChangesSchema = draftDataSchema.partial().refine((changes) => Object.keys(changes).length > 0)
export const revision = z.number().int().nonnegative().max(2147483647)
export const saveDraftSchema = z.strictObject({ assetId, expectedRevision: revision, changes: draftChangesSchema })
export const draftSchema = z.object({
  assetId, revision, archiveRevision: revision, data: draftDataSchema.extend({ suggestions: draftSuggestions.nullish() })
})
const sourcePropertyState = z.enum(['unchanged', 'written', 'library_only', 'write_failed'])
export const savedSourcePropertiesSchema = z.object({
  draft: draftSchema, remark: sourcePropertyState, authors: sourcePropertyState,
  remarkFailure: z.string().nullish(), authorsFailure: z.string().nullish(),
  renamedPath: z.string().nullish()
})
export type SavedSourceProperties = z.infer<typeof savedSourcePropertiesSchema>
export const assetSchema = z.object({
  assetId, originalName: z.string(), extension: z.string(),
  mediaKind: z.enum(['photo', 'video']), createdAt: z.iso.datetime({ offset: true }), available: z.boolean(),
  sizeBytes: z.number().int().nonnegative().nullish(), modifiedAt: z.iso.datetime({ offset: true }).nullish(),
  sourcePath: z.string().nullish()
})
const folderDate = z.iso.datetime({ offset: true })
export const folderEntriesSchema = z.array(z.object({
  name: z.string(), path: z.string(), directory: z.boolean(), modifiedAt: folderDate.nullish(),
  sizeBytes: z.number().int().nonnegative().nullish()
}))
export const folderDetailsSchema = z.object({ modifiedAt: folderDate })
export type FolderDetails = z.infer<typeof folderDetailsSchema>
export const workspaceSchema = z.object({
  queueIds: z.array(assetId),
  currentAssetId: assetId.nullable(),
  draft: draftSchema.nullable(),
  items: z.array(assetSchema),
  importFolders: z.array(z.string()).default([]),
  incompleteOperations: z.array(archiveStatusSchema),
  recentArchives: z.array(archiveStatusSchema)
})
export const importSchema = z.array(z.object({
  name: z.string(),
  status: z.enum(['imported', 'duplicate', 'unsupported', 'unavailable']),
  assetId: assetId.nullable(),
  reason: z.string().nullable()
}))
export const hotkeySchema = z.object({ accelerator: z.string(), registered: z.boolean() })

export type Asset = z.infer<typeof assetSchema>
export type FolderEntry = z.infer<typeof folderEntriesSchema>[number]
export type AssetDraft = z.infer<typeof draftSchema>
export type DraftData = z.infer<typeof draftDataSchema>
export type DraftChanges = z.infer<typeof draftChangesSchema>
export type SaveDraft = z.infer<typeof saveDraftSchema>
export type Workspace = z.infer<typeof workspaceSchema>
export type ImportItem = z.infer<typeof importSchema>[number]
export type Hotkey = z.infer<typeof hotkeySchema>
