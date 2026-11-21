import { z } from 'zod'
import { searchFilterSchema, searchItem } from './search'

export const photoStatus = z.enum(['loading', 'ready', 'missing', 'invalid', 'indexing'])
export const photoModelState = z.object({ status: photoStatus, version: z.string().nullable(), message: z.string().nullable() })
export const photoSceneQuery = z.string().trim().min(2).max(240).refine((value) => [...value].length >= 2)
export const photoSceneFilter = searchFilterSchema.omit({ cursor: true }).extend({ mediaKind: z.literal('photo').optional() })
export const photoSceneResult = z.object({ model: photoModelState, items: z.array(searchItem) })
export const photoJobState = z.enum(['queued', 'running', 'completed', 'failed'])
export const photoJobList = z.object({
  model: photoModelState, queued: z.number(), running: z.number(), completed: z.number(), failed: z.number(),
  items: z.array(z.object({ jobId: z.uuid(), assetId: z.uuid(), fileName: z.string(), state: photoJobState,
    errorCode: z.string().nullable() }))
})
export type PhotoSceneFilter = z.infer<typeof photoSceneFilter>

export function hasPhotoConstraints(filter: PhotoSceneFilter): boolean {
  return Boolean(filter.remarkTerms?.length || filter.nameTerms?.length || filter.author || filter.directoryId ||
    filter.directoryPaths?.length || filter.keywordTerms?.length || filter.format || filter.dateFrom || filter.dateToExclusive ||
    filter.captureMinuteFrom !== undefined || filter.captureMinuteToExclusive !== undefined)
}
