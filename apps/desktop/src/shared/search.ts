import { z } from 'zod'
import { assetId } from './workspace'

const mediaKind = z.enum(['photo', 'video'])
const mediaDate = z.iso.datetime({ offset: true })
const dateField = z.enum(['createdAt', 'capturedAt'])
const term = z.string().trim().min(1).max(240)
const cursor = z.string().min(1).max(600)
export const searchDirectoryPaths = z.array(z.string().min(1).max(32767)).min(1).max(50)
export const directoryIndexProgressSchema = z.object({
  phase: z.enum(['queued', 'scanning', 'indexing', 'complete']), processed: z.number().int().nonnegative(),
  discovered: z.number().int().nonnegative(), total: z.number().int().nonnegative().nullish()
})
export type DirectoryIndexProgress = z.infer<typeof directoryIndexProgressSchema>
export const searchFilterSchema = z.strictObject({
  resultKind: z.enum(['all', 'files', 'directories']).optional(),
  mediaKind: mediaKind.optional(),
  directoryId: assetId.optional(),
  directoryPaths: searchDirectoryPaths.optional(),
  includeSubdirectories: z.boolean().optional(),
  format: z.string().trim().transform((value) => value.normalize('NFKC'))
    .pipe(z.string().regex(/^\.?[a-z0-9]{1,16}$/i)).optional(),
  dateField: dateField.optional(),
  dateFrom: z.iso.date().optional(),
  dateToExclusive: z.iso.date().optional(),
  captureMinuteFrom: z.number().int().min(0).max(1439).optional(),
  captureMinuteToExclusive: z.number().int().min(1).max(1440).optional(),
  propertyRanges: z.array(z.strictObject({
    field: z.enum(['sizeBytes', 'exposureTimeSeconds', 'fNumber', 'iso', 'focalLengthMm', 'exposureBiasEv', 'width', 'height', 'durationSeconds', 'frameRate', 'bitrate']),
    min: z.number().finite().optional(), max: z.number().finite().optional(),
    minExclusive: z.boolean().optional(), maxExclusive: z.boolean().optional()
  }).refine(range => (range.min !== undefined || range.max !== undefined) &&
    (range.min === undefined || range.max === undefined || range.min <= range.max))).max(12).optional(),
  nameTerms: z.array(term).max(12).optional(),
  remarkTerms: z.array(term).max(12).optional(),
  keywordTerms: z.array(term).max(12).optional(),
  keywordGroups: z.array(z.array(term).min(1).max(6)).max(6).optional(),
  includeDirectoryNames: z.boolean().optional(),
  author: z.string().trim().min(1).max(120).optional(),
  limit: z.number().int().min(1).max(100).optional(),
  cursor: cursor.optional()
})

const evidenceSchema = z.object({
    field: z.enum(['name', 'remark', 'author', 'directory', 'mediaKind', 'format', 'createdAt', 'capturedAt', 'scene', 'properties']),
  terms: z.array(z.string()),
  value: z.string()
})
export const searchItem = z.object({
  assetId, fileName: z.string(), mediaKind, format: z.string(),
  createdAt: mediaDate, capturedAt: mediaDate.nullable(),
  authors: z.array(z.string()), remarkSummary: z.string(), available: z.boolean(),
  matches: z.array(evidenceSchema), sceneScore: z.number().min(-1).max(1).nullish(),
  sourcePath: z.string().optional(), directoryPath: z.string().optional(),
  properties: z.record(z.string(), z.number().finite()).nullish()
})
export const searchDirectoryItem = z.object({ name: z.string(), path: z.string(), createdAt: mediaDate })
export type SearchDirectoryItem = z.infer<typeof searchDirectoryItem>
export const searchPageSchema = z.object({ items: z.array(searchItem), nextCursor: cursor.nullable(), directories: z.array(searchDirectoryItem).optional() })
export const searchDetailsSchema = z.object({
  asset: searchItem, path: z.string(), remark: z.string(), remarkState: z.string(), authorsState: z.string(),
  captureTimeSource: z.string().nullable()
})
export const agentSearchSchema = z.object({
  items: z.array(searchItem), summary: z.string(), unresolvedConditions: z.array(z.string()),
  reasons: z.array(z.object({ assetId, reason: z.string() })).optional(),
  steps: z.array(z.string()).optional(),
  warning: z.string().nullable()
})
export type AgentSearchResult = z.infer<typeof agentSearchSchema>
export type SearchFilter = z.infer<typeof searchFilterSchema>
export type SearchItem = z.infer<typeof searchItem>
export type SearchDetails = z.infer<typeof searchDetailsSchema>
export type SearchPage = z.infer<typeof searchPageSchema>
