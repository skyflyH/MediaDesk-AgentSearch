import type { DesktopApi, Result } from '../shared/health'
import type { DraftChanges } from '../shared/workspace'
import type { ArchiveStatus } from '../shared/archive'

type CopyApi = Pick<DesktopApi, 'getDraft' | 'saveDraft' | 'archivePreview' | 'archiveCommit' | 'archiveStatus'>
export interface CopyOutcome {
  copied: Array<{ assetId: string; path: string }>
  failure?: { assetId: string; message: string }
}

function unwrap<T>(reply: Result<T>): T {
  if ('error' in reply) throw new Error(reply.error.message)
  return reply.result
}

/** Submit one file at a time. Never queue the next file until the current one has finished. */
export async function copyAssets(api: CopyApi, ids: string[], directoryId: string, changes: DraftChanges,
  onStatus: (status: ArchiveStatus) => void,
  pause = () => new Promise<void>((resolve) => setTimeout(resolve, 300))): Promise<CopyOutcome> {
  const outcome: CopyOutcome = { copied: [] }
  for (const assetId of ids) {
    let path: string | undefined
    try {
      const current = unwrap(await api.getDraft(assetId))
      const saved = unwrap(await api.saveDraft({ assetId, expectedRevision: current.revision,
        changes: { ...changes, directoryId } }))
      const plan = unwrap(await api.archivePreview(assetId, saved.archiveRevision, directoryId))
      let status = unwrap(await api.archiveCommit(crypto.randomUUID(), plan.planToken))
      while (true) {
        onStatus(status)
        path = status.actualPath ?? path
        if (status.phase === 'complete') break
        if (status.phase === 'failed' || status.phase === 'needs_review' || status.errorCode)
          throw new Error(status.message || '复制或属性写入失败，请核对保存记录。')
        await pause()
        status = unwrap(await api.archiveStatus(status.operationId))
      }
      outcome.copied.push({ assetId, path: path ?? status.finalPath })
    } catch (error) {
      if (path) outcome.copied.push({ assetId, path })
      outcome.failure = { assetId, message: error instanceof Error ? error.message : '无法确认复制结果，请核对保存记录后再试。' }
      break
    }
  }
  return outcome
}
