import { readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { randomUUID } from 'node:crypto'
import { z } from 'zod'
import { defaultModelConfig, modelConfig, modelProvider, type ModelConfig, type ModelSettingsView, type SaveModelSettings } from '../shared/model'

const storedModel = modelConfig.extend({ encryptedKey: z.string().max(16384).optional() })
const storedProvider = modelProvider.safeExtend({ encryptedKey: z.string().max(16384).optional() })
const storedSettings = storedModel.extend({ activeProviderId: z.string(), providers: z.array(storedProvider).min(1) })
type Provider = z.infer<typeof storedProvider>

type SecretStorage = {
  isAsyncEncryptionAvailable(): Promise<boolean>
  encryptStringAsync(value: string): Promise<Buffer>
  decryptStringAsync(value: Buffer): Promise<{ result: string; shouldReEncrypt: boolean }>
}

export class ModelSettings {
  constructor(private readonly path: string, private readonly secret: SecretStorage) {}

  private read() {
    try {
      const raw = JSON.parse(readFileSync(this.path, 'utf8'))
      if (raw.providers) {
        const saved = storedSettings.parse(raw)
        const selected = saved.providers.find(p => p.id === saved.activeProviderId)
        if (!selected) throw Error('Invalid provider')
        return { ...saved, ...this.connection(selected) }
      }
      return this.migrate(storedModel.parse(raw))
    } catch { return this.migrate(defaultModelConfig) }
  }

  private migrate(config: z.infer<typeof storedModel>) {
    const provider = { id: 'default', name: '默认供应商', baseUrl: config.baseUrl, model: config.model,
      apiMode: config.apiMode, models: [config.model], encryptedKey: config.encryptedKey }
    return { ...config, activeProviderId: provider.id, providers: [provider] }
  }

  private connection(provider: Provider) {
    return { baseUrl: provider.baseUrl, model: provider.model, apiMode: provider.apiMode, encryptedKey: provider.encryptedKey }
  }

  private usableWithoutKey(url: string) {
    return ['localhost', '127.0.0.1', '[::1]'].includes(new URL(url).hostname)
  }

  async view(): Promise<ModelSettingsView> {
    const { encryptedKey, providers, ...config } = this.read()
    const hasKey = Boolean(encryptedKey)
    return { ...config, providers: providers.map(({ encryptedKey, ...provider }) => ({ ...provider, hasKey: Boolean(encryptedKey) })),
      hasKey, configured: config.enabled && (
      hasKey && await this.secret.isAsyncEncryptionAvailable() || this.usableWithoutKey(config.baseUrl)
    ) }
  }

  async runtime(): Promise<(ModelConfig & { apiKey: string }) | null> {
    const { encryptedKey, providers: _providers, activeProviderId: _active, ...config } = this.read()
    if (!config.enabled) return null
    try {
      if (encryptedKey && await this.secret.isAsyncEncryptionAvailable()) {
        const encrypted = Buffer.from(encryptedKey, 'base64')
        let decrypted = await this.secret.decryptStringAsync(encrypted)
        if (decrypted.shouldReEncrypt) decrypted = await this.secret.decryptStringAsync(encrypted)
        return { ...config, apiKey: decrypted.result }
      }
    } catch { return null }
    return this.usableWithoutKey(config.baseUrl) ? { ...config, apiKey: 'local-no-key' } : null
  }

  async save(input: SaveModelSettings): Promise<ModelSettingsView> {
    const old = this.read()
    const entries = input.providers ?? [{ id: old.activeProviderId,
      name: old.providers.find(p => p.id === old.activeProviderId)!.name,
      baseUrl: input.baseUrl, apiMode: input.apiMode, model: input.model,
      models: [...new Set([...old.providers.find(p => p.id === old.activeProviderId)!.models, input.model])],
      apiKey: input.apiKey, clearKey: input.clearKey }]
    const providers: Provider[] = []
    for (const { apiKey, clearKey, ...provider } of entries) {
      provider.baseUrl = new URL(provider.baseUrl).href.replace(/\/+$/, '')
      const previous = old.providers.find(p => p.id === provider.id)
      let encryptedKey = provider.baseUrl === previous?.baseUrl && !clearKey ? previous.encryptedKey : undefined
      if (apiKey && !clearKey) {
        if (!await this.secret.isAsyncEncryptionAvailable()) throw Error('系统密钥存储不可用。')
        encryptedKey = (await this.secret.encryptStringAsync(apiKey)).toString('base64')
      }
      providers.push({ ...provider, encryptedKey })
    }
    if (!input.providers) providers.push(...old.providers.filter(p => p.id !== old.activeProviderId))
    const activeProviderId = input.providers ? input.activeProviderId! : old.activeProviderId
    const active = providers.find(p => p.id === activeProviderId)
    if (!active) throw Error('请选择供应商。')
    const config = { enabled: input.enabled, scopes: input.scopes, ...this.connection(active), activeProviderId, providers }
    const temporary = `${this.path}.${randomUUID()}.tmp`
    try {
      writeFileSync(temporary, JSON.stringify(config), { encoding: 'utf8', flag: 'wx' })
      renameSync(temporary, this.path)
    } catch {
      try { rmSync(temporary, { force: true }) } catch { /* Preserve the original settings error. */ }
      throw Error('无法保存模型设置。')
    }
    return this.view()
  }
}
