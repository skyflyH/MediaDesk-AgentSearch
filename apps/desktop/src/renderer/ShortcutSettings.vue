<script setup lang="ts">
import OperationFeedback from './OperationFeedback.vue'
import { defaultEditShortcuts, defaultGlobalHotkeys, editActions } from '../shared/desktop-controls'
import type { useShortcutRecording } from './useShortcutRecording'

const props = defineProps<{ controls: ReturnType<typeof useShortcutRecording> }>()
const { hotkeyLabels, hotkeys, editLabels, editKeys, editFeedback, editError, savingEditKeys,
  recording, recordingMessage, startRecording, updateShortcut, saveHotkey, saveEditShortcuts } = props.controls
</script>

<template>
  <section class="workspace-settings-card" aria-labelledby="workspace-shortcuts-heading">
    <h2 id="workspace-shortcuts-heading">快捷键</h2>
    <p class="workspace-muted">点击文本框后按下组合键，再点击对应的保存按钮；点击其它位置会取消本次录制。编辑快捷键须包含 Ctrl。</p>
    <p id="shortcut-recording-status" class="workspace-shortcut-recording-status"
      :class="{ 'is-recording': recording }" role="status">{{ recordingMessage || '\u00a0' }}</p>
    <form v-for="(label, target) in hotkeyLabels" :key="target" class="workspace-hotkey-form" @submit.prevent="saveHotkey(target)">
      <label :for="target === 'search' ? 'workspace-hotkey' : 'workspace-main-hotkey'">全局快捷键（{{ label }}）</label>
      <div class="workspace-actions">
        <input :id="target === 'search' ? 'workspace-hotkey' : 'workspace-main-hotkey'" :value="hotkeys[target].text"
          @input="updateShortcut({ kind: 'global', target }, ($event.target as HTMLInputElement).value)"
          readonly :aria-describedby="recording?.kind === 'global' && recording.target === target ? 'shortcut-recording-status' : undefined"
          :data-recording="recording?.kind === 'global' && recording.target === target"
          @focus="startRecording({ kind: 'global', target })" @click="startRecording({ kind: 'global', target })"
          maxlength="80" :placeholder="defaultGlobalHotkeys[target].replace('Control', 'Ctrl')" :disabled="hotkeys[target].busy">
        <button type="submit" :data-shortcut-save="target" :disabled="hotkeys[target].busy">保存</button>
      </div>
      <OperationFeedback :message="hotkeys[target].error" :warning="true" />
      <p v-if="!hotkeys[target].error && hotkeys[target].current && !hotkeys[target].current?.registered" class="workspace-alert" role="status">
        快捷键无法注册，仍可从托盘打开{{ target === 'search' ? '快捷搜索' : '应用主窗口' }}。
      </p>
    </form>
    <form v-if="editKeys" class="workspace-edit-shortcuts" @submit.prevent="saveEditShortcuts(editKeys)">
      <div v-for="action in editActions" :key="action" class="workspace-shortcut-row">
        <label :for="`workspace-shortcut-${action}`">{{ editLabels[action] }}</label>
        <input :id="`workspace-shortcut-${action}`" :value="editKeys[action]" maxlength="80" required readonly
          @input="updateShortcut({ kind: 'edit', target: action }, ($event.target as HTMLInputElement).value)"
          :disabled="savingEditKeys"
          :data-recording="recording?.kind === 'edit' && recording.target === action"
          :aria-describedby="recording?.kind === 'edit' && recording.target === action ? 'shortcut-recording-status' : undefined"
          @focus="startRecording({ kind: 'edit', target: action })" @click="startRecording({ kind: 'edit', target: action })">
      </div>
      <div class="workspace-actions workspace-view-actions">
        <button type="submit" data-shortcut-save="edit" :disabled="savingEditKeys">保存编辑快捷键</button>
        <button type="button" :disabled="savingEditKeys" @click="saveEditShortcuts(defaultEditShortcuts)">恢复默认</button>
      </div>
    </form>
    <OperationFeedback :message="editFeedback" :warning="editError" />
  </section>
</template>
