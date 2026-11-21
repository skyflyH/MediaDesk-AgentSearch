import { onMounted, onUnmounted, reactive, ref, watch, type Ref } from 'vue'
import type { DesktopApi } from '../shared/health'
import { defaultEditShortcuts, editActions, type EditAction, type EditShortcuts, type GlobalHotkeyTarget } from '../shared/desktop-controls'
import type { Hotkey } from '../shared/workspace'
import { capturedShortcut } from '../shared/keyboard'

export function useShortcutRecording(api: DesktopApi, active: Readonly<Ref<boolean>>) {
  const hotkeyLabels = { search: '快捷搜索', main: '打开应用主页面' }
  const hotkeys = reactive<Record<GlobalHotkeyTarget, { current?: Hotkey; text: string; error: string; busy: boolean }>>({
    search: { text: '', error: '', busy: true },
    main: { text: '', error: '', busy: true }
  })
  const editLabels: Record<EditAction, string> = {
    cut: '剪切', copy: '复制', paste: '粘贴', pasteAndMatchStyle: '粘贴为纯文本',
    selectAll: '全选', undo: '撤销', redo: '重做'
  }
  const editKeys = ref<EditShortcuts>()
  let savedEditKeys: EditShortcuts | undefined
  const editFeedback = ref('')
  const editError = ref(false)
  const savingEditKeys = ref(false)
  const recording = ref<{ kind: 'global'; target: GlobalHotkeyTarget } | { kind: 'edit'; target: EditAction }>()
  const recordingMessage = ref('')
  const recordingError = ref('')
  let recordingOriginal = ''

  function updateShortcut(value: NonNullable<typeof recording.value>, text: string) {
    if (value.kind === 'global') hotkeys[value.target].text = text
    else if (editKeys.value) editKeys.value[value.target] = text
  }

  function startRecording(value: NonNullable<typeof recording.value>) {
    if (recording.value?.kind === value.kind && recording.value.target === value.target) return
    stopRecording(true)
    recordingOriginal = value.kind === 'global' ? hotkeys[value.target].text : editKeys.value?.[value.target] ?? ''
    recording.value = value
    recordingError.value = ''
    recordingMessage.value = '正在录制，请按下快捷键组合；点击对应保存按钮保存，点击其它位置取消。'
    api.setShortcutCapture(true)
  }

  function stopRecording(cancel = false) {
    const current = recording.value
    if (!current) return
    if (cancel) {
      if (current.kind === 'global') hotkeys[current.target].text = recordingOriginal
      else if (editKeys.value) editKeys.value[current.target] = recordingOriginal
    }
    recording.value = undefined
    recordingMessage.value = ''
    recordingError.value = ''
    api.setShortcutCapture(false)
  }

  function cancelRecordingOutside(event: Event) {
    const current = recording.value
    const target = event.target
    if (!current || !(target instanceof Element)) return
    const inputId = current.kind === 'global'
      ? current.target === 'search' ? 'workspace-hotkey' : 'workspace-main-hotkey'
      : `workspace-shortcut-${current.target}`
    if (target.closest('input')?.id === inputId || target.closest('label')?.htmlFor === inputId) return
    const save = target.closest('[data-shortcut-save]')?.getAttribute('data-shortcut-save')
    if (save === (current.kind === 'global' ? current.target : 'edit')) return
    stopRecording(true)
  }

  function cancelRecording() { stopRecording(true) }

  function recordShortcut(event: KeyboardEvent) {
    const current = recording.value
    if (!current) return
    event.preventDefault()
    event.stopImmediatePropagation()
    if (event.type !== 'keydown') return
    const value = capturedShortcut(event)
    if (!value) {
      recordingError.value = '请使用修饰键和字母、数字、F1–F24 或空格组成快捷键。'
      recordingMessage.value = recordingError.value
      return
    }
    if (current.kind === 'edit' && (!event.ctrlKey || event.metaKey)) {
      recordingError.value = '编辑快捷键必须包含 Ctrl，且不能包含 Windows 键。'
      recordingMessage.value = recordingError.value
      return
    }
    updateShortcut(current, value)
    recordingError.value = ''
    recordingMessage.value = `已录制 ${value}，可继续按键修改；点击对应保存按钮保存，点击其它位置取消。`
  }
  function applyHotkey(target: GlobalHotkeyTarget, value: Hotkey) {
    hotkeys[target].current = value
    hotkeys[target].text = value.accelerator.replace('Control', 'Ctrl')
  }

  async function saveHotkey(target: GlobalHotkeyTarget) {
    const invalid = recording.value?.kind === 'global' && recording.value.target === target ? recordingError.value : ''
    stopRecording()
    const setting = hotkeys[target]
    if (invalid) {
      if (setting.current) applyHotkey(target, setting.current)
      setting.error = invalid
      return
    }
    setting.error = ''
    setting.busy = true
    try {
      const reply = await api.setHotkey(setting.text, target)
      if ('error' in reply) throw new Error(reply.error.message)
      applyHotkey(target, reply.result)
    } catch (error) {
      if (setting.current) applyHotkey(target, setting.current)
      setting.error = error instanceof Error ? error.message : '无法保存快捷键。'
    } finally { setting.busy = false }
  }

  function applyEditShortcuts(values: EditShortcuts) {
    savedEditKeys = { ...values }
    editKeys.value = { ...values }
    for (const action of editActions) editKeys.value[action] = values[action].replace('Control', 'Ctrl')
  }

  async function saveEditShortcuts(values: EditShortcuts) {
    const invalid = values !== defaultEditShortcuts && recording.value?.kind === 'edit' ? recordingError.value : ''
    stopRecording()
    if (invalid) {
      if (savedEditKeys) applyEditShortcuts(savedEditKeys)
      editError.value = true
      editFeedback.value = invalid
      return
    }
    savingEditKeys.value = true
    editFeedback.value = ''
    editError.value = false
    try {
      const reply = await api.setEditShortcuts({ ...values })
      if ('error' in reply) throw new Error(reply.error.message)
      applyEditShortcuts(reply.result)
      editFeedback.value = '快捷键已保存。'
    } catch (error) {
      if (savedEditKeys) applyEditShortcuts(savedEditKeys)
      editError.value = true
      editFeedback.value = error instanceof Error ? error.message : '无法保存编辑快捷键。'
    } finally {
      savingEditKeys.value = false
    }
  }

  watch(active, cancelRecording)
  onMounted(() => {
    window.addEventListener('pointerdown', cancelRecordingOutside, true)
    window.addEventListener('click', cancelRecordingOutside, true)
    window.addEventListener('blur', cancelRecording)
    window.addEventListener('keydown', recordShortcut, true)
    window.addEventListener('keyup', recordShortcut, true)
    for (const target of ['search', 'main'] as const) {
      const setting = hotkeys[target]
      void api.getHotkey(target).then((reply) => {
        if ('result' in reply) applyHotkey(target, reply.result)
        else setting.error = reply.error.message
      }).catch(() => { setting.error = '无法读取快捷键。' })
        .finally(() => { setting.busy = false })
    }
    void api.getEditShortcuts().then((reply) => {
      if ('error' in reply) throw new Error(reply.error.message)
      applyEditShortcuts(reply.result)
    }).catch(() => {
      editError.value = true
      editFeedback.value = '无法读取编辑快捷键。'
    })
  })
  onUnmounted(() => {
    cancelRecording()
    window.removeEventListener('pointerdown', cancelRecordingOutside, true)
    window.removeEventListener('click', cancelRecordingOutside, true)
    window.removeEventListener('blur', cancelRecording)
    window.removeEventListener('keydown', recordShortcut, true)
    window.removeEventListener('keyup', recordShortcut, true)
  })
  return { hotkeyLabels, hotkeys, editLabels, editKeys, editFeedback, editError, savingEditKeys,
    recording, recordingMessage, startRecording, updateShortcut, saveHotkey, saveEditShortcuts }
}
