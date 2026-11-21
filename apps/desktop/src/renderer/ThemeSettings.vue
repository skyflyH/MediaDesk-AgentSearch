<script setup lang="ts">
import { theme, setTheme, applyTheme } from './theme'
import { accentColor, saveAccent } from './accent'
import { notify } from './notices'

function changeAccent(value: string) {
  try {
    saveAccent(value)
    applyTheme()
    notify(value ? '主题色已保存。' : '已恢复默认主题色。')
  } catch { notify('无法保存主题色，请检查本地存储空间。', true) }
}
</script>

<template>
  <div class="theme-setting">
    <label for="color-theme">颜色主题</label>
    <select id="color-theme" :value="theme" @change="setTheme(($event.target as HTMLSelectElement).value)">
      <option value="dark">深色</option>
      <option value="light">浅色</option>
    </select>
  </div>
  <div class="theme-accent-setting">
    <label id="theme-accent-label">主题色</label>
    <div class="theme-accent-options" role="group" aria-labelledby="theme-accent-label">
      <label class="theme-accent-custom">自定义
        <input type="color" aria-label="自定义主题色" :value="accentColor || '#0078d4'"
          @change="changeAccent(($event.target as HTMLInputElement).value)">
      </label>
      <button type="button" class="workspace-text-button" :disabled="!accentColor" @click="changeAccent('')">恢复默认</button>
    </div>
  </div>
</template>
