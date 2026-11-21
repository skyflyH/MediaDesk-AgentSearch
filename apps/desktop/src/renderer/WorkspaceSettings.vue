<script setup lang="ts">
import { computed, ref } from 'vue'
import { History, Keyboard, LogOut, Settings, Sparkles } from '@lucide/vue'
import OperationFeedback from './OperationFeedback.vue'
import ModelSettings from './ModelSettings.vue'
import ThemeSettings from './ThemeSettings.vue'
import FontSettings from './FontSettings.vue'
import LoginStartupSettings from './LoginStartupSettings.vue'
import BackgroundSettings from './BackgroundSettings.vue'
import ShortcutSettings from './ShortcutSettings.vue'
import { settingsSections, type SettingsSection } from './workspaceSettings'
import type { ArchiveStatus } from '../shared/archive'
import type { ModelConnection, ModelSettingsView } from '../shared/model'
import type { useWorkspacePreferences } from './useWorkspacePreferences'
import type { useShortcutRecording } from './useShortcutRecording'

const props = defineProps<{
  section: SettingsSection; disabled: boolean; exiting: boolean
  modelConnection?: ModelConnection; checkingModel: boolean; archiveLog: ArchiveStatus[]
  preferences: ReturnType<typeof useWorkspacePreferences>; shortcuts: ReturnType<typeof useShortcutRecording>
}>()
const emit = defineEmits<{
  'update:section': [value: SettingsSection]; exit: []; 'model-saved': [settings: ModelSettingsView]
}>()
const settingsSection = computed(() => props.section)
const sectionIcons = { general: Settings, model: Sparkles, shortcuts: Keyboard, history: History }
const exitDialog = ref<HTMLDialogElement>()
function confirmExit() {
  if (!props.exiting && !exitDialog.value?.open) exitDialog.value?.showModal()
}
function exitApplication() {
  exitDialog.value?.close()
  emit('exit')
}
const { cacheMessage, cacheDirectory, cacheBusy, cacheError, viewMessage,
  chooseCacheDirectory, clearPreviewCache, changeView } = props.preferences
const archivePhases: Record<ArchiveStatus['phase'], string> = {
  accepted: '已确认', copying: '正在复制', ready_to_publish: '等待发布', destination_published: '目标已发布',
  properties_pending: '写入副本属性', complete: '已复制', failed: '未生成副本', needs_review: '复制中断，需要核对'
}
</script>

<template>
  <div class="workspace-settings">
    <nav class="workspace-settings-nav" aria-label="设置分类">
      <button v-for="(label, key) in settingsSections" :key="key" type="button"
        :aria-current="settingsSection === key ? 'page' : undefined" aria-controls="workspace-settings-content"
        @click="emit('update:section', key)"><component :is="sectionIcons[key]" :size="17" aria-hidden="true" /><span>{{ label }}</span></button>
      <button type="button" class="workspace-settings-exit" :disabled="exiting" @click="confirmExit"><LogOut :size="17" aria-hidden="true" /><span>退出应用</span></button>
    </nav>
    <div id="workspace-settings-content" class="workspace-settings-content" role="region" :aria-label="settingsSections[settingsSection]">
      <section v-show="settingsSection === 'general'" class="workspace-settings-card" aria-labelledby="workspace-general-heading">
        <h2 id="workspace-general-heading">常规</h2>
        <ThemeSettings />
        <FontSettings />
        <LoginStartupSettings />
        <BackgroundSettings />
        <div class="workspace-cache-settings">
          <div class="workspace-actions workspace-cache-actions">
            <label class="workspace-cache-path" for="preview-cache-directory">
              <span>预览缓存保存路径</span>
              <input id="preview-cache-directory" :value="cacheDirectory" :title="cacheDirectory" readonly placeholder="正在读取缓存路径…">
            </label>
            <button type="button" :disabled="cacheBusy" @click="chooseCacheDirectory">修改路径</button>
            <button type="button" :disabled="cacheBusy" @click="clearPreviewCache">清理预览缓存</button>
          </div>
          <p class="workspace-muted">修改后即刻生效，旧目录中的缓存不会自动迁移或删除。</p>
          <OperationFeedback :message="cacheMessage" :warning="cacheError" />
        </div>
        <div class="workspace-actions workspace-view-actions">
          <button type="button" :disabled="disabled" @click="changeView('reload')">重新加载</button>
          <button type="button" :disabled="disabled" @click="changeView('forceReload')">忽略缓存并重新加载</button>
          <button type="button" @click="changeView('toggleDevTools')">切换开发者工具</button>
        </div>
        <OperationFeedback :message="viewMessage" :warning="true" />
      </section>
      <ModelSettings v-show="settingsSection === 'model'" class="workspace-settings-card" :connection="modelConnection" :checking="checkingModel"
        @saved="emit('model-saved', $event)" />
      <ShortcutSettings v-show="settingsSection === 'shortcuts'" :controls="shortcuts" />
      <section v-if="settingsSection === 'history'" class="workspace-settings-card workspace-archive-log"
        aria-labelledby="workspace-archive-log-heading">
        <h2 id="workspace-archive-log-heading">保存记录</h2>
        <ul v-if="archiveLog.length">
          <li v-for="entry in archiveLog" :key="entry.operationId" :data-archive-phase="entry.phase">
            <strong>{{ entry.errorCode === 'PROPERTY_WRITE_FAILED' ? '属性写入失败' : archivePhases[entry.phase] }}</strong>
            <span>{{ entry.actualPath ?? entry.finalPath }}</span>
          </li>
        </ul>
        <p v-else class="workspace-muted">暂无保存记录。</p>
      </section>
    </div>
  </div>
  <dialog ref="exitDialog" class="workspace-error-dialog workspace-exit-dialog" aria-labelledby="workspace-exit-title" aria-describedby="workspace-exit-description">
    <h2 id="workspace-exit-title">退出应用？</h2>
    <p id="workspace-exit-description">退出后将关闭主窗口和快捷搜索。确定要退出应用吗？</p>
    <form method="dialog">
      <button type="submit" autofocus>取消</button>
      <button type="button" class="workspace-primary" :disabled="exiting" @click="exitApplication">确定退出</button>
    </form>
  </dialog>
</template>
