import { z } from 'zod'

const assetId = z.uuid()
export const directorySchema = z.object({
  directoryId: assetId, rootId: assetId.nullable(), displayName: z.string(), relativePath: z.string(),
  fullPath: z.string(), available: z.boolean()
})
export const planToken = z.string().regex(/^[0-9a-f]{48}$/)
export const archivePlanSchema = z.object({ planToken, sourcePath: z.string(), finalPath: z.string() })
const fieldState = z.enum(['unchanged', 'written', 'library_only', 'write_failed'])
export const archiveStatusSchema = z.object({
  operationId: assetId, assetId, phase: z.enum([
    'accepted', 'copying', 'ready_to_publish', 'destination_published', 'properties_pending', 'complete', 'failed', 'needs_review'
  ]),
  sourcePath: z.string(), finalPath: z.string(), actualPath: z.string().nullable(),
  errorCode: z.string().nullable(), message: z.string().nullable(), remark: fieldState, authors: fieldState
})
export type ArchiveDirectory = z.infer<typeof directorySchema>
export type ArchivePlan = z.infer<typeof archivePlanSchema>
export type ArchiveStatus = z.infer<typeof archiveStatusSchema>
