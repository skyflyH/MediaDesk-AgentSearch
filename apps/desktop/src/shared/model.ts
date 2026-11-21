import { z } from 'zod'

export const suggestionKind = z.enum(['name', 'remark', 'directory'])
export const modelError = z.enum(['MODEL_UNAVAILABLE', 'MODEL_INVALID_OUTPUT', 'MODEL_NOT_CONFIGURED', 'MODEL_INTERRUPTED'])
export const directoryCandidate = z.strictObject({ directoryId: z.uuid(), reason: z.string().trim().min(1).max(240) })
export const suggestionState = z.object({
  requestId: z.uuid(), status: z.enum(['pending', 'ready', 'retry']),
  usesRemark: z.boolean(), usesAuthors: z.boolean(),
  errorCode: modelError.nullish(), names: z.array(z.string()).max(3).nullish(),
  remark: z.string().max(4000).nullish(), candidates: z.array(directoryCandidate).max(5).nullish()
})
export const draftSuggestions = z.object({
  name: suggestionState.nullish(), remark: suggestionState.nullish(), directory: suggestionState.nullish()
})

const modelScopes = z.strictObject({
  fileNames: z.boolean(), remarks: z.boolean(), authors: z.boolean(), directoryNames: z.boolean()
})
const modelUrl = z.string().trim().max(2048).url().refine((value) => {
  const url = new URL(value)
  return (url.protocol === 'http:' || url.protocol === 'https:') && !url.username && !url.password && !url.search && !url.hash
}, '填写 HTTP 或 HTTPS 接口基础地址')
export const modelConfig = z.strictObject({
  enabled: z.boolean(),
  baseUrl: modelUrl,
  model: z.string().trim().min(1).max(160),
  apiMode: z.enum(['responses', 'completions']),
  scopes: modelScopes
})
export const modelProvider = z.strictObject({
  id: z.string().min(1).max(100), name: z.string().trim().min(1).max(100), baseUrl: modelUrl,
  apiMode: z.enum(['responses', 'completions']), model: z.string().trim().max(160),
  models: z.array(z.string().trim().min(1).max(160)).max(100)
}).refine((value) => value.models.includes(value.model) || (!value.models.length && !value.model), '请选择供应商的模型')
const providerSelection = {
  activeProviderId: z.string().min(1).max(100).optional(),
  providers: z.array(modelProvider.safeExtend({
    apiKey: z.string().trim().max(4096).optional(), clearKey: z.boolean().optional()
  })).min(1).max(50).optional()
}
export const saveModelSettingsSchema = modelConfig.extend({
  apiKey: z.string().trim().max(4096).optional(),
  clearKey: z.boolean().optional(), ...providerSelection
}).superRefine((value, context) => {
  if (!value.providers) return
  const active = value.providers.find(p => p.id === value.activeProviderId)
  if (!active || new Set(value.providers.map(p => p.id)).size !== value.providers.length) {
    context.addIssue({ code: 'custom', path: ['activeProviderId'], message: '请选择有效的供应商' })
    return
  }
  if (!active.models.length || !active.models.includes(active.model))
    context.addIssue({ code: 'custom', path: ['providers', value.providers.indexOf(active), 'models'], message: '请为当前供应商添加并选择一个模型' })
})
export const modelSettingsView = modelConfig.extend({ hasKey: z.boolean(), configured: z.boolean(),
  activeProviderId: z.string().optional(), providers: z.array(modelProvider.safeExtend({ hasKey: z.boolean() })).optional() })

export type ModelConfig = z.infer<typeof modelConfig>
export type SaveModelSettings = z.infer<typeof saveModelSettingsSchema>
export type ModelSettingsView = z.infer<typeof modelSettingsView>
export type ModelConnection = {
  model: string
  state: 'disabled' | 'unconfigured' | 'connected' | 'error'
  message?: string
}
export type SuggestionKind = z.infer<typeof suggestionKind>
export type SuggestionState = z.infer<typeof suggestionState>

export const defaultModelConfig: ModelConfig = {
  enabled: false, baseUrl: 'https://api.openai.com/v1', model: 'gpt-5-mini', apiMode: 'responses',
  scopes: { fileNames: false, remarks: false, authors: false, directoryNames: false }
}

export function modelStatus(code: string): string {
  switch (code) {
    case 'MODEL_NOT_CONFIGURED': return '请在设置中启用并配置大模型。'
    case 'MODEL_INVALID_OUTPUT': return '模型返回的结果格式无效或包含未经核对的引用。'
    case 'MODEL_INTERRUPTED': return '上次请求中断，可以重新生成。'
    default: return '模型服务超时或暂不可用，请检查配置后重试。'
  }
}
