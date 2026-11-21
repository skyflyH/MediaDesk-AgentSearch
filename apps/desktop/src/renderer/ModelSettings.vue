<script setup lang="ts">
import OperationFeedback from './OperationFeedback.vue'
import { computed, onMounted, ref } from 'vue'
import { defaultModelConfig, saveModelSettingsSchema, type ModelConnection, type ModelSettingsView, type SaveModelSettings } from '../shared/model'

const api = window.mediaLibrary
const props = defineProps<{ connection?: ModelConnection; checking: boolean }>()
const emit = defineEmits<{ saved: [settings: ModelSettingsView] }>()
const form = ref<SaveModelSettings>({ ...defaultModelConfig, scopes: { ...defaultModelConfig.scopes }, apiKey: '' })
const state = ref<ModelSettingsView>()
const busy = ref(true)
const message = ref('')
const failed = ref(false)
type Provider = NonNullable<SaveModelSettings['providers']>[number]
const providers = ref<Provider[]>([])
const activeProviderId = ref('')
const provider = computed(() => providers.value.find(p => p.id === activeProviderId.value))
const newModel = ref('')
const savedKeyForAddress = computed(() => {
  const saved = state.value?.providers?.find(p => p.id === provider.value?.id)
  if (!saved?.hasKey || provider.value?.clearKey) return false
  try { return new URL(provider.value!.baseUrl).href.replace(/\/+$/, '') === saved.baseUrl }
  catch { return false }
})
const connectionMessage = computed(() => {
  if (props.checking) return '正在检测模型连接…'
  return props.connection?.message ?? (props.connection?.state === 'connected' ? '模型连接正常。' : '')
})

function show(value: ModelSettingsView) {
  const { hasKey: _hasKey, configured: _configured, ...config } = value
  state.value = value
  form.value = { ...config, scopes: { ...config.scopes }, apiKey: '', clearKey: false }
  providers.value = value.providers?.map(({ hasKey: _key, ...p }) => ({ ...p, models: [...p.models], apiKey: '', clearKey: false })) ??
    [{ id: 'default', name: '默认供应商', baseUrl: value.baseUrl, apiMode: value.apiMode, model: value.model, models: [value.model], apiKey: '' }]
  activeProviderId.value = value.activeProviderId ?? providers.value[0]!.id
  newModel.value = ''
}

function addProvider() {
  const id = crypto.randomUUID()
  providers.value.push({ id, name: '新供应商', baseUrl: 'http://localhost:11434/v1', apiMode: 'completions',
    model: '', models: [], apiKey: '' })
  activeProviderId.value = id
  newModel.value = ''
}

function removeProvider() {
  if (providers.value.length < 2) return
  providers.value = providers.value.filter(p => p.id !== activeProviderId.value)
  activeProviderId.value = providers.value[0]!.id
  newModel.value = ''
}

function addModel() {
  const id = newModel.value.trim()
  if (!provider.value || !id || id.length > 160) return
  if (!provider.value.models.includes(id)) provider.value.models.push(id)
  provider.value.model = id
  newModel.value = ''
}

function removeModel() {
  if (!provider.value) return
  provider.value.models = provider.value.models.filter(id => id !== provider.value!.model)
  provider.value.model = provider.value.models[0] ?? ''
}

async function save() {
  if (!provider.value) return
  if (newModel.value.trim()) {
    if (newModel.value.trim().length > 160 || (provider.value.models.length >= 100 && !provider.value.models.includes(newModel.value.trim()))) {
      message.value = '模型 ID 最多 160 个字符，每家供应商最多添加 100 个模型。'
      failed.value = true
      return
    }
    addModel()
  }
  const parsed = saveModelSettingsSchema.safeParse({ ...form.value, baseUrl: provider.value.baseUrl,
    model: provider.value.model, apiMode: provider.value.apiMode, providers: providers.value, activeProviderId: activeProviderId.value })
  if (!parsed.success) {
    const issue = parsed.error.issues.find(issue => issue.path[0] === 'providers') ?? parsed.error.issues[0]!
    const entry = issue.path[0] === 'providers' ? providers.value[Number(issue.path[1])] : provider.value
    const field = String(issue.path.at(-1))
    const prefix = `供应商「${entry?.name.trim() || '未命名'}」：`
    message.value = prefix + (field === 'baseUrl' ? '请填写有效的 HTTP 或 HTTPS 接口基础地址，不包含账号、密码或查询参数。'
      : field === 'name' ? '请填写供应商名称（最多 100 个字符）。'
      : field === 'model' || field === 'models' || (issue.path[0] === 'providers' && !entry?.model)
        ? '请添加并选择一个有效的模型 ID。'
      : field === 'apiKey' ? 'API Key 最多 4096 个字符。'
      : issue.message)
    failed.value = true
    return
  }
  busy.value = true
  try {
    const reply = await api.saveModelSettings(parsed.data)
    if ('error' in reply) throw Error(reply.error.message)
    show(reply.result)
    emit('saved', reply.result)
    failed.value = false
    message.value = '模型设置已保存。'
  } catch (error) {
    failed.value = true
    message.value = error instanceof Error ? error.message : '无法保存模型设置。'
  } finally { busy.value = false }
}

onMounted(async () => {
  try {
    const reply = await api.getModelSettings()
    if ('error' in reply) throw Error(reply.error.message)
    show(reply.result)
  } catch {
    failed.value = true
    message.value = '无法读取模型设置。'
  } finally { busy.value = false }
})
</script>

<template>
  <section class="model-settings" aria-labelledby="model-settings-heading">
    <h2 id="model-settings-heading">大模型配置</h2>
    <p v-if="state" class="workspace-muted">
      {{ state.configured ? '已配置' : state.enabled ? '尚缺少可用凭据' : '未启用' }}
    </p>
    <p v-if="connectionMessage" :class="!checking && connection?.state === 'error' ? 'workspace-alert' : 'workspace-muted'"
      role="status">{{ connectionMessage }}</p>
    <form class="model-settings-form" data-model-settings @submit.prevent="save">
      <fieldset :disabled="busy">
        <label class="model-option"><input id="model-enabled" v-model="form.enabled" type="checkbox"> 启用模型功能</label>
        <label for="model-provider">当前供应商</label>
        <select id="model-provider" v-model="activeProviderId" @change="newModel = ''">
          <option v-for="entry in providers" :key="entry.id" :value="entry.id">{{ entry.name }}</option>
        </select>
        <div class="workspace-actions">
          <button type="button" @click="addProvider">添加供应商</button>
          <button type="button" :disabled="providers.length < 2" @click="removeProvider">移除供应商</button>
        </div>
        <template v-if="provider">
        <label for="model-provider-name">供应商名称</label>
        <input id="model-provider-name" v-model.trim="provider.name" required maxlength="100">
        <label for="model-url">接口基础地址</label>
        <input id="model-url" v-model.trim="provider.baseUrl" type="url" required maxlength="2048"
          placeholder="https://api.openai.com/v1" autocomplete="off">
        <label for="model-id">模型 ID</label>
        <select id="model-id" v-model="provider.model">
          <option v-if="!provider.models.length" value="" disabled>请先添加模型</option>
          <option v-for="id in provider.models" :key="id" :value="id">{{ id }}</option>
        </select>
        <label for="model-new-id">添加模型 ID</label>
        <input id="model-new-id" v-model.trim="newModel" maxlength="160" autocomplete="off"
          placeholder="填写供应商提供的准确模型 ID" @keydown.enter.prevent="addModel">
        <div class="workspace-actions">
          <button type="button" :disabled="!newModel.trim() || provider.models.length >= 100" @click="addModel">添加模型</button>
          <button type="button" :disabled="!provider.model" @click="removeModel">移除当前模型</button>
        </div>
        <label for="model-api-mode">接口模式</label>
        <select id="model-api-mode" v-model="provider.apiMode">
          <option value="responses">OpenAI Responses</option>
          <option value="completions">OpenAI 兼容 Chat Completions</option>
        </select>
        <label for="model-key">API Key</label>
        <input id="model-key" v-model.trim="provider.apiKey" type="password" autocomplete="new-password"
          maxlength="4096" :disabled="provider.clearKey"
          :placeholder="savedKeyForAddress ? '•••••••• 已加密保存' : '输入 API Key'" aria-describedby="model-key-note">
        <label v-if="state?.providers?.find(p => p.id === provider?.id)?.hasKey" class="model-option"><input v-model="provider.clearKey" type="checkbox"> 删除已保存的密钥</label>
        <p id="model-key-note" class="workspace-muted">{{ savedKeyForAddress
          ? '留空保留已保存的密钥，填写新密钥可替换。'
          : '更换接口地址时请重新填写密钥。localhost 可免密钥。' }}</p>
        </template>
        <p class="workspace-muted">选择供应商和模型后，点击保存即切换当前使用的模型。模型 ID 可直接输入后保存，其它供应商可暂不添加模型。</p>
        <fieldset class="model-scope">
          <legend>允许发送的素材信息</legend>
          <label class="model-option"><input id="model-scope-filenames" v-model="form.scopes.fileNames" type="checkbox"> 文件名</label>
          <label class="model-option"><input id="model-scope-remarks" v-model="form.scopes.remarks" type="checkbox"> 已有备注</label>
          <label class="model-option"><input id="model-scope-authors" v-model="form.scopes.authors" type="checkbox"> 创作人</label>
          <label class="model-option"><input id="model-scope-directories" v-model="form.scopes.directoryNames" type="checkbox"> 目录名称</label>
        </fieldset>
        <button type="submit">保存模型设置</button>
      </fieldset>
    </form>
    <OperationFeedback :message="message" :warning="failed" />
  </section>
</template>
