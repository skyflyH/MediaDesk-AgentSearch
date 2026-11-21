import { createApp } from 'vue'
import { applyTheme } from './theme'
import { initializeFont } from './font'

applyTheme()
initializeFont()

if (!window.mediaLibrary) {
  document.getElementById('app')!.textContent = '桌面接口初始化失败，请重新启动 MediaDesk。'
}
const entry = window.mediaLibrary
  ? location.hash === '#quick-search' ? import('./QuickSearch.vue') : import('./WorkspaceApp.vue')
  : undefined
void entry?.then(({ default: App }) => createApp(App).mount('#app'))
