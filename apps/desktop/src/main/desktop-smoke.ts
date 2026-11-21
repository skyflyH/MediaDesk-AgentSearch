import { spawn, spawnSync } from 'node:child_process'
import { copyFileSync, existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { app, BrowserWindow, globalShortcut, net, type Tray } from 'electron'
import { archivePlanSchema, archiveStatusSchema, directorySchema } from '../shared/archive'
import { draftSchema, importSchema } from '../shared/workspace'
import type { Result } from '../shared/health'
import type { ServiceClient } from './service-client'
import { randomUUID } from 'node:crypto'
import { createServer } from 'node:http'
import { photoJobList, photoSceneResult } from '../shared/photos'
import { searchPageSchema } from '../shared/search'

const source = '原片.jpg'
const copy = '镜头.mov'
const invalid = '文档.txt'
const original = Buffer.from([23, 42, 128, 255])

function evaluate<T>(window: BrowserWindow, expression: string): Promise<T> {
  return window.webContents.executeJavaScript(expression)
}

async function waitFor(window: BrowserWindow, expression: string, timeout = 6000): Promise<void> {
  await evaluate(window, `new Promise((resolve, reject) => {
    const end = Date.now() + ${timeout};
    const check = () => {
      try {
        if (${expression}) resolve(true);
        else if (Date.now() > end) reject(new Error(${JSON.stringify(expression)}));
        else setTimeout(check, 25);
      } catch (error) { reject(error); }
    };
    check();
  })`)
}

export async function verifyPhotoSearch(window: BrowserWindow, directory: string, service: ServiceClient, missing: boolean) {
  window.show()
  if (missing) {
    const fixtureRoot = join(app.getAppPath(), 'apps', 'desktop', 'src', 'renderer', 'assets')
    const archiveRoot = join(directory, '画面索引归档')
    mkdirSync(archiveRoot)
    const roots = await service.request('archive.addRoot', { path: archiveRoot }, directorySchema)
    if ('error' in roots) throw Error(roots.error.message)
    const root = roots.result.directoryId
    const samples = [
      { original: 'coast.jpg', name: '海岸晚霞', remark: '青岛' },
      { original: 'forest.jpg', name: '林间步道', remark: '青岛' },
      { original: 'coast.jpg', name: '其它地点晚霞', remark: '三亚' }
    ]
    for (const sample of samples) {
      const path = join(directory, sample.name + '.jpg')
      copyFileSync(join(fixtureRoot, sample.original), path)
      const imported = await service.request('asset.import', { paths: [path] }, importSchema)
      if ('error' in imported || !imported.result[0]?.assetId) throw Error('Import photo fixture failed')
      const assetId = imported.result[0].assetId
      const draft = await service.request('draft.save', { assetId, expectedRevision: 0, changes: {
        name: sample.name, finalized: true, directoryId: root, remark: { intent: 'set', value: sample.remark }
      } }, draftSchema)
      if ('error' in draft) throw Error(draft.error.message)
      const plan = await service.request('archive.preview', { assetId, archiveRevision: draft.result.archiveRevision, directoryId: root }, archivePlanSchema)
      if ('error' in plan) throw Error(plan.error.message)
      const operationId = randomUUID()
      const accepted = await service.request('archive.commit', { operationId, planToken: plan.result.planToken }, archiveStatusSchema)
      if ('error' in accepted) throw Error(accepted.error.message)
      const deadline = Date.now() + 12_000
      while (true) {
        const status = await service.request('archive.status', { operationId }, archiveStatusSchema)
        if ('error' in status) throw Error(status.error.message)
        if (status.result.phase === 'complete' || status.result.errorCode === 'PROPERTY_WRITE_FAILED') break
        if (status.result.phase === 'needs_review' || Date.now() > deadline)
          throw Error(`Archive photo fixture ${sample.name} failed: ${status.result.phase}`)
        await new Promise((resolve) => setTimeout(resolve, 90))
      }
    }
  } else {
    const deadline = Date.now() + 24_000
    while (true) {
      const jobs = await service.request('job.list', {}, photoJobList)
      if ('error' in jobs) throw Error(jobs.error.message)
      if (jobs.result.completed === 3) break
      if (Date.now() > deadline || jobs.result.failed || ['invalid', 'missing'].includes(jobs.result.model.status))
        throw Error('Photo indexes unavailable: ' + JSON.stringify(jobs.result))
      await new Promise((resolve) => setTimeout(resolve, 250))
    }
  }
  const filter = { mediaKind: 'photo', remarkTerms: ['青岛'], directoryPaths: [join(directory, '画面索引归档')] }
  const scene = await service.request('search.photosByScene', { query: '海边晚霞', filter }, photoSceneResult)
  if ('error' in scene) throw Error(scene.error.message)
  if (missing) {
    const fallback = await service.request('search.filter', filter, searchPageSchema)
    if (scene.result.model.status !== 'missing' || 'error' in fallback || fallback.result.items.length !== 2)
      throw Error('Missing model fallback did not find the matching photos')
  } else if (scene.result.items.length !== 2 || !scene.result.items[0]?.fileName.includes('海岸晚霞') ||
      !scene.result.items.every((item) => item.sceneScore != null && item.matches.some((match) =>
        match.field === 'remark' && match.value.includes('青岛')))) {
    throw Error('Photo ranking did not retain scene and remark matches')
  }
  console.log(`Photo smoke: ${missing ? 'missing model text fallback' : 'local ONNX ranking and required remark'} passed`)
}

async function drop(window: BrowserWindow, paths: string[]): Promise<void> {
  const rect = await evaluate<{ x: number; y: number }>(window, `(() => {
    const rect = document.querySelector('[data-import-drop]').getBoundingClientRect()
    return { x: Math.floor(rect.x + rect.width / 2), y: Math.floor(rect.y + rect.height / 2) }
  })()`)
  const protocol = window.webContents.debugger
  protocol.attach('1.3')
  try {
    for (const type of ['dragEnter', 'dragOver', 'drop']) {
      await protocol.sendCommand('Input.dispatchDragEvent', {
        type, ...rect, data: { items: [], files: paths, dragOperationsMask: 1 }
      })
    }
  } finally {
    protocol.detach()
  }
}

function fill(window: BrowserWindow, selector: string, value: string): Promise<void> {
  return evaluate(window, `(() => {
    const field = document.querySelector(${JSON.stringify(selector)})
    field.value = ${JSON.stringify(value)}
    field.dispatchEvent(new Event('input', { bubbles: true }))
  })()`)
}

function click(window: BrowserWindow, selector: string): Promise<void> {
  return evaluate(window, `document.querySelector(${JSON.stringify(selector)}).click()`)
}

async function pressControlA(window: BrowserWindow, selector: string) {
  await evaluate(window, `document.querySelector(${JSON.stringify(selector)}).focus()`)
  window.webContents.sendInputEvent({ type: 'keyDown', keyCode: 'A', modifiers: ['control'] })
  window.webContents.sendInputEvent({ type: 'keyUp', keyCode: 'A', modifiers: ['control'] })
}

async function verifyBatchDrafts(window: BrowserWindow) {
  const lastAsset = '.workspace-queue tbody tr:last-child [data-asset-id]'
  const add = '.workspace-queue-menu button:first-child'
  window.show()
  await pressControlA(window, '#workspace-name')
  await waitFor(window, `(() => {
    const field = document.querySelector('#workspace-name')
    return field.selectionStart === 0 && field.selectionEnd === field.value.length
      && document.querySelectorAll('[data-asset-id][aria-pressed="true"]').length === 1
  })()`)
  await fill(window, '#workspace-name', '?')
  await pressControlA(window, lastAsset)
  await waitFor(window, `document.querySelectorAll('[data-asset-id][aria-pressed="true"]').length === 2`)
  await evaluate(window, `document.querySelector(${JSON.stringify(lastAsset)})
    .dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, button: 2 }))`)
  await waitFor(window, `document.querySelector('${add}')?.textContent.includes('添加到批量处理')`)
  await click(window, add)
  await waitFor(window, `document.querySelectorAll('.workspace-batch-items li').length === 2`)
  await evaluate(window, `document.querySelector(${JSON.stringify(lastAsset)})
    .dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, button: 2 }))`)
  await click(window, add)
  await waitFor(window, `document.querySelector('.workspace-batch [role="status"]')?.textContent.includes('已在批次中')
    && document.querySelectorAll('.workspace-batch-items li').length === 2`)
  await click(window, '[aria-label="批量修改备注"]')
  await waitFor(window, `document.querySelector('[aria-label="批量备注"]')`)
  await fill(window, '[aria-label="批量备注"]', '批量测试备注')
  await click(window, '.workspace-batch button[type="submit"]')
  // These deliberately invalid image/video fixtures cannot accept Explorer remarks.
  // Draft persistence alone must not produce a successful attribute-save notice.
  await waitFor(window, `document.querySelector('.workspace-batch [role="alert"]')?.textContent.includes('失败')
    && document.querySelector('.workspace-error-dialog')?.open
    && document.querySelector('#workspace-error-title')?.textContent === '操作未完成'
    && document.querySelectorAll('.workspace-batch-items li').length === 2`)
  await click(window, '.workspace-error-dialog button')
  await click(window, '.workspace-batch-views button:first-child')
  await waitFor(window, `document.querySelector('#workspace-name')`)
  await fill(window, '#workspace-name', '原片')
  await waitFor(window, `document.querySelector('#workspace-name')?.value === '原片'
    && document.querySelector('#workspace-remark')?.value === '批量测试备注'`)
}

async function expandSingleFields(window: BrowserWindow) {
  await waitFor(window, `document.querySelector('[aria-label="修改文件名"]')`)
  await evaluate(window, `(() => {
    for (const label of ['修改文件名', '修改备注']) {
      const checkbox = document.querySelector('input[aria-label="' + label + '"]')
      if (!checkbox.checked) checkbox.click()
    }
  })()`)
  await waitFor(window, `document.querySelector('#workspace-name') && document.querySelector('#workspace-remark')`)
}

export async function verifyWorkspace(window: BrowserWindow, directory: string, phase: string): Promise<void> {
  await waitFor(window, `document.querySelector('[data-import-files]:enabled')`)
  if (phase === 'write') {
    for (const file of [source, copy, invalid]) writeFileSync(join(directory, file), original)
    await drop(window, [source, copy, invalid].map((file) => join(directory, file)))
    await expandSingleFields(window)
    await waitFor(window, `document.querySelector('.workspace-import-notice')?.textContent.includes('导入成功 · 2 项素材')
      && document.querySelector('.workspace-import-notice')?.textContent.includes('1 项不支持')
      && document.querySelectorAll('[data-asset-id]').length === 2
      && document.querySelector('#workspace-name')?.value === '原片'`)
    await drop(window, [join(directory, source)])
    await waitFor(window, `document.querySelector('.workspace-import-notice')?.textContent.includes('素材已在队列中')`)
    await verifyBatchDrafts(window)
    await fill(window, '#workspace-name', '草稿切换前保存')
    await click(window, '.workspace-queue tbody tr:last-child button')
    await waitFor(window, `document.querySelector('#workspace-name')?.value === '镜头'
      && document.querySelector('#workspace-remark')?.value === '批量测试备注'`)
    await click(window, '.workspace-queue tbody tr:first-child button')
    await waitFor(window, `document.querySelector('#workspace-name')?.value === '草稿切换前保存'`)
    await click(window, '.workspace-queue tbody tr:last-child button')
    await waitFor(window, `document.querySelector('#workspace-name')?.value === '镜头'`)
    await fill(window, '#workspace-name', '青岛晚霞')
    await fill(window, '#workspace-remark', '青岛海边')
    await waitFor(window, "!document.querySelector('#workspace-authors')")
    await waitFor(window, `document.querySelector('[data-save-state="saved"]')`)
    await fill(window, '#workspace-remark', '退出前保存的青岛海边')
  } else {
    await expandSingleFields(window)
    await waitFor(window, `document.querySelector('#workspace-name')?.value === '青岛晚霞'
      && document.querySelector('.workspace-queue tbody tr:last-child button[aria-current="true"]')`)
    const expected = phase === 'recover' ? '自动保存后终止' : '退出前保存的青岛海边'
    await waitFor(window, `document.querySelector('#workspace-remark')?.value === ${JSON.stringify(expected)}`)
    if (phase === 'interrupt') {
      await fill(window, '#workspace-remark', '自动保存后终止')
      await waitFor(window, `document.querySelector('[data-save-state="saved"]')`)
    }
  }

  for (const file of [source, copy]) {
    if (!readFileSync(join(directory, file)).equals(original)) throw Error('Source bytes changed')
  }
  console.log(`Workspace smoke: ${phase} passed`)
  if (phase === 'interrupt') {
    writeFileSync(join(directory, 'interrupt-ready'), '')
    process.kill(process.pid, 'SIGKILL')
  }
}

export async function verifyMedia(window: BrowserWindow, directory: string, phase: string): Promise<void> {
  window.show()
  const archiveRoot = join(directory, '归档')
  const photo = join(directory, '实拍样本.jpg')
  const clip = join(directory, '视频样本.mp4')
  const fixture = (name: string) => join(app.getAppPath(), 'apps', 'desktop', 'src', 'renderer', 'assets', name)
  if (phase === 'media_prepare') {
    mkdirSync(archiveRoot)
    copyFileSync(fixture('coast.jpg'), photo)
    copyFileSync(fixture('flower.mp4'), clip)
    await waitFor(window, `document.querySelector('[data-import-files]:enabled')`)
    await drop(window, [photo, clip])
    await waitFor(window, `document.querySelector('.media-frame img')?.naturalWidth > 0`)
    await waitFor(window, `(() => {
      const name = document.querySelector('#workspace-preview-pane .media-file-name')
      const frame = document.querySelector('#workspace-preview-pane .media-frame')
      const details = document.querySelector('#workspace-preview-pane .media-file-details')
      const folder = document.querySelector('#workspace-preview-pane [data-preview-folder]')
      if (!name || !frame || !details || !folder) return false
      const title = name.getBoundingClientRect()
      const preview = frame.getBoundingClientRect()
      const metadata = details.getBoundingClientRect()
      const folderButton = folder.getBoundingClientRect()
      return name.textContent === '实拍样本.jpg'
        && title.bottom <= preview.top
        && Math.abs(title.left - preview.left) < 1
        && getComputedStyle(name).textAlign === 'start'
        && metadata.top >= preview.bottom
        && Math.abs((metadata.top + metadata.bottom) - (folderButton.top + folderButton.bottom)) < 2
        && folderButton.right < metadata.left
        && getComputedStyle(details).textAlign === 'end'
        && details.querySelector('time')?.dateTime
        && details.querySelector('.media-file-path')?.textContent === ${JSON.stringify(photo)}
        && !document.querySelector('.workspace-editor .media-file-details, .workspace-editor .workspace-original')
    })()`)
    const bounds = window.getBounds()
    const layout = (condition: string) => `(() => {
      const titlebar = document.querySelector('.workspace-titlebar').getBoundingClientRect()
      const body = document.querySelector('.workspace-body').getBoundingClientRect()
      const panels = ['.workspace-list', '.media-panel', '.workspace-editor'].map(selector => document.querySelector(selector))
      const [queue, preview, draft] = panels.map(panel => panel.getBoundingClientRect())
      const styles = panels.map(panel => getComputedStyle(panel))
      const [queueStyle, previewStyle, draftStyle] = styles
      return body.left === 0 && body.width === titlebar.width && body.top >= titlebar.bottom
        && draftStyle.borderBlockStartWidth === '1px'
        && styles.every(style => style.borderRadius === '0px')
        && (${condition})
    })()`
    await waitFor(window, layout(`queue.right === preview.left && queue.top === preview.top
      && preview.left === draft.left && preview.bottom === draft.top && queue.bottom === draft.bottom
      && queueStyle.borderInlineEndWidth === '1px' && previewStyle.borderBlockStartWidth === '0px'`))
    try {
      window.setSize(420, 510)
      await waitFor(window, layout(`queue.left === preview.left && preview.left === draft.left
        && queue.bottom === preview.top && preview.bottom === draft.top
        && queueStyle.borderInlineEndWidth === '0px' && previewStyle.borderBlockStartWidth === '1px'`))
    } finally { window.setBounds(bounds) }
    console.log('Workspace layout smoke: desktop columns and narrow stacking passed')
    await click(window, '[data-preview-folder]')
    await waitFor(window, `document.querySelector('[data-preview-folder]:enabled')
      && !document.querySelector('[data-preview-folder-path], .workspace-folder-error')`)
    const url = await evaluate<string>(window, `document.querySelector('.media-frame img').src`)
    const range = await net.fetch(url, { headers: { Range: 'bytes=0-1' } })
    if (range.status !== 206 || range.headers.get('content-type') !== 'image/jpeg' ||
        !Buffer.from(await range.arrayBuffer()).equals(Buffer.from([255, 216])))
      throw Error('Preview byte range did not return the JPEG signature')
    const invalid = await net.fetch(url, { headers: { Range: 'bytes=999999999-' } })
    if (invalid.status !== 416 || !invalid.headers.get('content-range')?.startsWith('bytes */'))
      throw Error('Preview did not reject an unsatisfiable byte range')
    await expandSingleFields(window)
    await fill(window, '#workspace-name', '切换设置前的草稿')
    await click(window, '[data-settings-tab]')
    await waitFor(window, `document.querySelector('[data-add-archive-root]:enabled')`)
    await click(window, '[data-add-archive-root]')
    await waitFor(window, `document.querySelector('.workspace-root-list')?.textContent.includes('归档')`)
    await click(window, '[data-workspace-tab]')
    await waitFor(window, `document.querySelector('#workspace-directory')?.value
      && document.querySelector('#workspace-name')?.value === '切换设置前的草稿'
      && document.querySelector('[data-save-state="saved"]')`)
    await click(window, '.workspace-queue tbody tr:last-child button')
    await waitFor(window, `document.querySelector('video')?.duration > 1`)
    await evaluate(window, `document.querySelector('video').currentTime = 0.65`)
    await waitFor(window, `!document.querySelector('video')?.seeking
      && document.querySelector('video')?.currentTime > 0.6
      && document.querySelector('[data-save-state="saved"]')`)
    if (!readFileSync(photo).equals(readFileSync(fixture('coast.jpg')))) throw Error('Preview changed the source')
  } else {
    await waitFor(window, `document.querySelector('video')?.currentTime > 0.6 && document.querySelector('video')?.paused`)
    await click(window, '.workspace-queue tbody tr:first-child button')
    await waitFor(window, `document.querySelector('.media-frame img')?.naturalWidth > 0
      && document.querySelector('#workspace-directory')?.value`)
    await expandSingleFields(window)
    await fill(window, '#workspace-name', '已整理照片')
    const archived = join(archiveRoot, '已整理照片.jpg')
    if (!existsSync(photo) || existsSync(archived)) throw Error('Save As changed a path before saving')
    await click(window, '[data-save-as]:enabled')
    await waitFor(window, `document.querySelectorAll('[data-asset-id]').length === 1
      && !document.querySelector('.workspace-archive-log') && document.querySelector('[data-settings-tab]:enabled')`)
    await click(window, '[data-settings-tab]')
    await evaluate(window, `[...document.querySelectorAll('.workspace-settings-nav button')]
      .find(button => button.textContent.trim() === '保存记录').click()`)
    await waitFor(window, `(() => {
      const record = document.querySelector('.workspace-archive-log [data-archive-phase="complete"]')
      return record?.querySelector('strong')?.textContent === '已复制'
        && record.querySelector('span')?.textContent === ${JSON.stringify(archived)}
        && !record.querySelector('button')
    })()`)
    await click(window, '[data-workspace-tab]')
    const original = readFileSync(fixture('coast.jpg'))
    if (!readFileSync(photo).equals(original) || !readFileSync(archived).equals(original))
      throw Error('Archive must preserve source bytes in both the original and the copy')
    if (process.env.APM_FFMPEG) {
      await waitFor(window, `document.querySelector('video')?.readyState >= 1`)
      await evaluate(window, `document.querySelector('video').dispatchEvent(new Event('error'))`)
      await click(window, '[data-preview-proxy]')
      await waitFor(window, `document.querySelector('video')?.readyState >= 1
        && document.querySelector('video')?.videoHeight > 0 && document.querySelector('video').videoHeight <= 720`)
      console.log('Media smoke: generated WebM proxy decoded in Electron')

      const png = join(directory, '样本.png')
      const mov = join(directory, '样本.mov')
      convert(['-i', archived, '-frames:v', '1', png])
      convert(['-i', clip, '-map', '0:v:0', '-c', 'copy', mov])
      await drop(window, [png, mov])
      await waitFor(window, `document.querySelectorAll('[data-asset-id]').length === 3`)
      await click(window, '.workspace-queue tbody tr:nth-child(2) button')
      await waitFor(window, `document.querySelector('.media-frame img')?.naturalWidth > 0`)
      await click(window, '.workspace-queue tbody tr:last-child button')
      await waitFor(window, `document.querySelector('video')?.readyState >= 1 || document.querySelector('[data-preview-proxy]')`)
      const movDirect = await evaluate<boolean>(window, `!!document.querySelector('video')?.videoWidth`)
      console.log(`Media smoke: PNG decoded; MOV direct playback ${movDirect ? 'available' : 'requires proxy'}`)
      const damaged = join(directory, '无法解码.mov')
      writeFileSync(damaged, original)
      await drop(window, [damaged])
      await waitFor(window, `document.querySelector('.workspace-queue tbody tr:last-child button')?.textContent.includes('无法解码.mov')`)
      await click(window, '.workspace-queue tbody tr:last-child button')
      await waitFor(window, `document.querySelector('[data-preview-proxy]')`)
      await click(window, '[data-preview-proxy]')
      await waitFor(window, `document.querySelector('.media-frame p')?.textContent.includes('无法生成 WebM')`)
      if (!readFileSync(damaged).equals(original) || !readFileSync(clip).equals(readFileSync(fixture('flower.mp4'))))
        throw Error('Preview proxy changed a source')
    }
    if (process.env.APM_TEST_HEIC) {
      const heic = join(directory, '样本.heic')
      copyFileSync(process.env.APM_TEST_HEIC, heic)
      await drop(window, [heic])
      await waitFor(window, `document.querySelector('.workspace-queue tbody tr:last-child button')?.textContent.includes('样本.heic')`)
      await click(window, '.workspace-queue tbody tr:last-child button')
      await waitFor(window, `document.querySelector('.media-frame img')?.naturalWidth > 0
        || document.querySelector('.media-frame p')?.textContent.includes('系统未能解码')`)
      const available = await evaluate<boolean>(window, `!!document.querySelector('.media-frame img')?.naturalWidth`)
      console.log(`Media smoke: HEIC ${available ? 'decoded by WIC' : 'decoder unavailable with explicit feedback'}`)
    }
  }
  console.log(`Media smoke: ${phase} passed`)
}

function convert(args: string[]) {
  const result = spawnSync(process.env.APM_FFMPEG!, ['-nostdin', '-v', 'error', ...args], {
    windowsHide: true, timeout: 8000, encoding: 'utf8'
  })
  if (result.status !== 0) throw Error(`FFmpeg fixture failed: ${result.error?.message ?? result.stderr}`)
}

async function seedSearch(directory: string, service: ServiceClient) {
  const fixtureRoot = join(app.getAppPath(), 'apps', 'desktop', 'src', 'renderer', 'assets')
  const archiveRoot = join(directory, '分类筛选归档')
  mkdirSync(archiveRoot)

  function unwrap<T>(reply: Result<T>) {
    if ('error' in reply) throw Error(reply.error.message)
    return reply.result
  }

  const root = unwrap(await service.request('archive.addRoot', { path: archiveRoot }, directorySchema))
  const fixtures = [
    { source: '原视频.mp4', asset: 'flower.mp4', final: 'V001', remark: '青岛海边日落' },
    { source: '另一段视频.mov', asset: 'flower.mp4', final: 'V002', remark: '三亚海边日落' },
    { source: '原照片.jpg', asset: 'coast.jpg', final: '晚霞', remark: '青岛海边日落' },
    { source: '待整理照片.jpg', asset: 'coast.jpg', final: '', remark: '' }
  ]
  for (const fixture of fixtures) copyFileSync(join(fixtureRoot, fixture.asset), join(directory, fixture.source))
  const imported = unwrap(await service.request('asset.import', {
    paths: fixtures.map((fixture) => join(directory, fixture.source))
  }, importSchema))

  const operations = []
  for (let index = 0; index < fixtures.length - 1; index++) {
    const fixture = fixtures[index]!
    const assetId = imported[index]?.assetId
    if (!assetId) throw Error('Could not import search fixture')
    const saved = unwrap(await service.request('draft.save', { assetId, expectedRevision: 0, changes: {
      name: fixture.final, finalized: true, directoryId: root.directoryId,
      remark: { intent: 'set', value: fixture.remark }, authors: { intent: 'set', value: ['林安'] }
    } }, draftSchema))
    const plan = unwrap(await service.request('archive.preview', {
      assetId, archiveRevision: saved.archiveRevision, directoryId: root.directoryId
    }, archivePlanSchema))
    const operationId = randomUUID()
    unwrap(await service.request('archive.commit', { operationId, planToken: plan.planToken }, archiveStatusSchema))
    operations.push(operationId)
  }
  const deadline = Date.now() + 15_000
  for (const operationId of operations) {
    while (true) {
      const entry = unwrap(await service.request('archive.status', { operationId }, archiveStatusSchema))
      if (entry.phase === 'complete' || entry.phase === 'properties_pending' && entry.errorCode === 'PROPERTY_WRITE_FAILED') break
      if (entry.phase === 'needs_review' || Date.now() > deadline) throw Error('Could not archive search fixture: ' + entry.phase)
      await new Promise((resolve) => setTimeout(resolve, 100))
    }
  }

  return { archiveRoot, imported }
}

async function waitForSearchFiles(window: BrowserWindow, count: number) {
  await waitFor(window, `document.querySelector('.library-results-heading [role="status"]')?.textContent.includes('${count} 个文件')`)
  await waitFor(window, `document.querySelectorAll('.library-results-scroll > ul > [data-library-result]').length === ${count}`)
}

async function verifySearchIsIdle(window: BrowserWindow) {
  await waitFor(window, `document.querySelector('.library-directories li')
    && document.querySelector('.library-query-box button:disabled')
    && !document.querySelector('.library-directory-group, [data-library-empty], .library-results-scroll [role="alert"]')`)
}

export async function verifySearch(window: BrowserWindow, directory: string, service: ServiceClient,
  lastReveal: () => string | undefined, showQuickSearch: () => void) {
  window.show()
  const { archiveRoot, imported } = await seedSearch(directory, service)
  const pageOrigin = new URL(window.webContents.getURL()).origin
  const webRequest = window.webContents.session.webRequest
  let nonLocalRequests = 0
  webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (request, callback) => {
    const external = new URL(request.url).origin !== pageOrigin
    if (external) nonLocalRequests++
    callback({ cancel: external })
  })
  try {
    await click(window, '[data-search-tab]')
    await waitFor(window, `document.querySelector('.library-directories')?.textContent.includes('请添加查找目录')`)
    await click(window, '[data-search-directory-add]')
    await verifySearchIsIdle(window)
    const matchedFolder = join(archiveRoot, '海边项目')
    mkdirSync(matchedFolder)
    await fill(window, '#library-query', '海边')
    await waitForSearchFiles(window, 3)
    showQuickSearch()
    const popup = BrowserWindow.getAllWindows().find((candidate) => candidate !== window)
    if (!popup) throw Error('Quick Search did not open')
    try {
      await waitFor(popup, `document.querySelector('.quick-search-window #library-query')
        && document.querySelector('.library-directories li')
        && !document.querySelector('.quick-search-titlebar, .library-results')`)
      const collapsedHeight = popup.getBounds().height
      await click(popup, '.library-mode-toggle')
      await waitFor(popup, `document.querySelector('.library-mode-toggle[aria-pressed="true"]')?.textContent.includes('已开启')
        && document.querySelector('#library-query').getAttribute('aria-label').includes('按回车搜索')`)
      await click(popup, '.library-mode-toggle')
      await waitFor(popup, `document.querySelector('.library-mode-toggle[aria-pressed="false"]')`)
      await fill(popup, '#library-query', '海边')
      await waitFor(popup, `document.querySelectorAll('[data-library-result]').length === 3 && innerHeight > ${collapsedHeight}`)
      await fill(popup, '#library-query', '没有对应的测试素材')
      await waitFor(popup, `!document.querySelector('.library-results')
        && document.querySelector('.library-quick-status')?.textContent === '无匹配结果'`)
      popup.emit('blur')
      if (popup.isVisible()) throw Error('Quick Search did not hide when it lost focus')
    } finally {
      if (!popup.isDestroyed()) popup.destroy()
    }
    await waitFor(window, `(() => {
      const results = document.querySelector('.library-results-scroll > .library-result-list')
      const folders = document.querySelectorAll('[data-library-directory]')
      return folders.length === 1 && folders[0].dataset.libraryDirectory.toUpperCase() === ${JSON.stringify(matchedFolder.toUpperCase())}
        && Boolean(results.compareDocumentPosition(folders[0]) & Node.DOCUMENT_POSITION_FOLLOWING)
    })()`)
    for (const [label, key] of Object.entries({ 中图: 'medium', 大图: 'large', 列表: 'list' })) {
      await evaluate(window, `[...document.querySelectorAll('.library-results-heading button')]
        .find(button => button.textContent.trim() === ${JSON.stringify(label)}).click()`)
      await waitFor(window, `document.querySelector('.library-results-${key} [data-library-result] .queue-card-preview')`)
    }
    await evaluate(window, `(() => {
      const select = document.querySelector('#library-kind')
      select.value = 'video'
      select.dispatchEvent(new Event('change', { bubbles: true }))
    })()`)
    await waitForSearchFiles(window, 2)
    const expectedId = imported[0]?.assetId
    for (const term of ['青岛', '青岛海边']) {
      await fill(window, '#library-query', term)
      await waitForSearchFiles(window, 1)
      await waitFor(window, `document.querySelectorAll('[data-library-result]').length === 1
        && document.querySelector('[data-library-result]').textContent.includes('V001.mp4')`)
      const actualId = await evaluate(window, "document.querySelector('[data-library-result]').dataset.libraryResult")
      if (actualId !== expectedId) throw Error('Search returned a different asset ID')
    }
    const expectedPath = join(archiveRoot, 'V001.mp4')
    const sourcePath = join(directory, '原视频.mp4')
    await waitFor(window, `document.querySelector('[data-library-result] time')?.dateTime
      && document.querySelector('[data-library-path]')?.textContent === ${JSON.stringify(sourcePath)}`)
    await click(window, '[data-library-reveal]')
    const handoffTimeout = Date.now() + 4000
    while (lastReveal() === undefined && Date.now() < handoffTimeout) {
      await new Promise((resolve) => setTimeout(resolve, 50))
    }
    if (lastReveal() !== expectedPath) throw Error('Reveal did not receive the archived path')

    renameSync(expectedPath, join(archiveRoot, 'V001.offline.mp4'))
    await click(window, '.library-query-box button')
    await waitForSearchFiles(window, 1)
    await waitFor(window, `document.querySelector('[data-library-result]')?.textContent.includes('原件不可用')`)
    await click(window, '[data-library-reveal]')
    await waitFor(window, `document.querySelector('.library-results [role="alert"]')?.textContent.includes('最近记录')`)
    if (nonLocalRequests !== 0) throw Error('Classification tried a nonlocal HTTP request')
    console.log('Search smoke: Chinese classification, archived preview, Explorer dispatch and offline path passed; nonlocal HTTP requests=0')
  } finally {
    webRequest.onBeforeRequest(null)
  }
}

export async function verifyModel(window: BrowserWindow, directory: string, service: ServiceClient, lastReveal: () => string | undefined) {
  window.show()
  const { archiveRoot, imported } = await seedSearch(directory, service)
  let requestCount = 0
  const server = createServer(async (request, response) => {
    let raw = ''
    for await (const chunk of request) raw += String(chunk)
    const body = JSON.parse(raw)
    requestCount++
    const user = JSON.parse(body.messages.find((message: { role: string }) => message.role === 'user')?.content ?? '{}')
    const toolReply = body.messages.find((message: { role: string }) => message.role === 'tool')
    let message: object
    if (body.tools?.length && !toolReply) {
      message = { role: 'assistant', content: null, tool_calls: [{
        id: 'local-search', type: 'function', function: { name: 'search_assets',
          arguments: JSON.stringify({ mediaKind: 'video', author: '林安', remarkTerms: ['青岛', '日落'] }) }
      }] }
    } else {
      const result = toolReply ? { resultIds: [...JSON.parse(toolReply.content).items.map((item: { assetId: string }) => item.assetId), randomUUID()],
        summary: '检索结果', unresolvedConditions: [] }
        : user.keywords ? { draft: '在青岛海边拍摄的日落素材。' }
          : user.candidates ? { items: [{ directoryId: user.candidates[0].directoryId, reason: '项目相符' }] }
            : { suggestions: ['海边日落', '海岸晚霞', '海边暮色'] }
      message = { role: 'assistant', content: JSON.stringify(result) }
    }
    response.writeHead(200, { 'Content-Type': 'application/json' })
    response.end(JSON.stringify({ id: `chat-fixture-${requestCount}`, object: 'chat.completion',
      created: 1760000000, model: 'fixture-model', choices: [
        { index: 0, message, finish_reason: 'tool_calls' in message ? 'tool_calls' : 'stop' }
      ] }))
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  try {
    const port = (server.address() as { port: number }).port
    await waitFor(window, `document.querySelector('[data-settings-tab]:enabled')`)
    await click(window, '[data-settings-tab]')
    await waitFor(window, `document.querySelector('#model-enabled:enabled')`)
    await click(window, '#model-enabled')
    await click(window, '#model-scope-filenames')
    await click(window, '#model-scope-remarks')
    await click(window, '#model-scope-directories')
    await fill(window, '#model-url', `http://127.0.0.1:${port}/v1`)
    await fill(window, '#model-new-id', 'fixture-model')
    await evaluate(window, `document.querySelector('#model-new-id').dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))`)
    const dummyKey = 'fixture-test-key'
    await fill(window, '#model-key', dummyKey)
    await evaluate(window, `(() => {
      const select = document.querySelector('#model-api-mode')
      select.value = 'completions'
      select.dispatchEvent(new Event('change', { bubbles: true }))
    })()`)
    await click(window, '[data-model-settings] button[type=submit]')
    await waitFor(window, `document.querySelector('.model-settings [role="status"]')?.textContent.includes('已保存')`)
    if (readFileSync(join(directory, 'model-settings.json'), 'utf8').includes(dummyKey) ||
        await evaluate(window, "document.querySelector('#model-key').value")) throw Error('Model settings exposed the saved key')
    if (requestCount) throw Error('Saving model settings must not contact a provider')
    await click(window, '[data-search-tab]')
    await click(window, '[data-search-directory-add]')
    await verifySearchIsIdle(window)
    if (requestCount) throw Error('Adding a search directory unexpectedly contacted a model')
    await click(window, '[data-library-intelligent]')
    await waitFor(window, `document.querySelector('textarea#library-query')`)
    await fill(window, '#library-query', '林安在青岛拍的日落视频')
    if (requestCount) throw Error('Typing in intelligent search contacted the model')
    await click(window, '.library-query-box button')
    await waitForSearchFiles(window, 1)
    await waitFor(window, `document.querySelectorAll('[data-library-result]').length === 1
      && document.querySelector('[data-library-result]')?.textContent.includes('V001.mp4')`)
    if (await evaluate(window, "document.querySelector('[data-library-result]').dataset.libraryResult") !== imported[0]?.assetId)
      throw Error('Agent returned the wrong asset')
    await waitFor(window, `document.querySelector('[data-library-result] .queue-card-preview')
      && document.querySelector('[data-library-path]')?.textContent === ${JSON.stringify(join(directory, '原视频.mp4'))}`)
    await click(window, '[data-library-reveal]')
    const revealDeadline = Date.now() + 4000
    while (!lastReveal() && Date.now() < revealDeadline) await new Promise((resolve) => setTimeout(resolve, 50))
    if (lastReveal() !== join(archiveRoot, 'V001.mp4')) throw Error('Agent did not reveal the archived original')

    await click(window, '[data-workspace-tab]')
    const pending = join(directory, '建议测试.jpg')
    copyFileSync(join(app.getAppPath(), 'apps', 'desktop', 'src', 'renderer', 'assets', 'coast.jpg'), pending)
    await drop(window, [pending])
      await expandSingleFields(window)
    await waitFor(window, `document.querySelector('#workspace-remark')`)
    await click(window, '[data-generate-name]')
    await waitFor(window, `document.querySelectorAll('.workspace-name-suggestions li').length === 3
      && document.querySelector('.workspace-name-suggestions li span')?.textContent === '海边日落'`)
    await click(window, '.workspace-name-suggestions li button')
    await waitFor(window, `document.querySelector('#workspace-name')?.value === '海边日落'`)
    await waitFor(window, `document.querySelector('[data-generate-remark]:disabled')`)
    await fill(window, '#workspace-remark', '青岛、海边、日落')
    await click(window, '[data-generate-remark]')
    await waitFor(window, `document.querySelector('[data-remark-suggestion]')?.textContent.includes('在青岛海边')`)
    if (await evaluate(window, "document.querySelector('#workspace-remark').value") === '在青岛海边拍摄的日落素材。')
      throw Error('Agent edited the remark before adoption')
    await click(window, '[data-apply-remark]')
    await waitFor(window, `document.querySelector('#workspace-remark')?.value === '在青岛海边拍摄的日落素材。'`)
    if (!existsSync(pending)) throw Error('Adopting a remark moved the source')
    console.log(`Model smoke: manual settings, local-only filtering, read-only search and remark adoption passed (fixture calls=${requestCount})`)
  } finally {
    await new Promise<void>((resolve) => server.close(() => resolve()))
  }
}

export async function verifyLifecycle(window: BrowserWindow, tray: Tray, showMainPage: () => void): Promise<void> {
  const duplicate = () => new Promise<void>((resolve, reject) => {
    const args = app.commandLine.hasSwitch('no-sandbox') ? ['--no-sandbox'] : []
    const child = spawn(process.execPath, [...args, app.getAppPath()], { windowsHide: true, stdio: 'ignore' })
    const timeout = setTimeout(() => child.kill(), 6000)
    child.once('error', (error) => {
      clearTimeout(timeout)
      reject(error)
    })
    child.once('exit', (code) => {
      clearTimeout(timeout)
      if (code === 0) resolve()
      else reject(Error(`Duplicate instance exited with ${code}`))
    })
  })

  window.show()
  window.close()
  if (window.isVisible() || window.isDestroyed()) throw Error('Close did not hide the window')
  await duplicate()
  if (!window.isVisible() || BrowserWindow.getAllWindows().length !== 1) {
    throw Error('Duplicate launch did not restore one window')
  }
  window.close()
  tray.emit('double-click')
  if (!window.isVisible()) throw Error('Tray could not restore the window')

  await waitFor(window, `document.querySelector('[data-settings-tab]:enabled')`)
  await click(window, '[data-settings-tab]')
  await waitFor(window, `document.querySelector('.workspace-titlebar [data-settings-tab][aria-current="page"]')
    && document.querySelector('main[aria-label="设置"]')
    && document.querySelectorAll('main').length === 1
    && !document.querySelector('.workspace-layout')`)
  await waitFor(window, `document.querySelector('#color-theme')?.value === 'dark'
    && getComputedStyle(document.documentElement).colorScheme === 'dark'`)
  const selectTheme = (theme: 'light' | 'dark') => evaluate(window, `(() => {
    const select = document.querySelector('#color-theme')
    select.value = '${theme}'
    select.dispatchEvent(new Event('change', { bubbles: true }))
  })()`)
  await selectTheme('light')
  await waitFor(window, `getComputedStyle(document.documentElement).colorScheme === 'light'
    && getComputedStyle(document.querySelector('.workspace-content')).backgroundColor === 'rgb(255, 255, 255)'`)
  if (window.getBackgroundColor().toLowerCase() !== '#ffffff') throw Error('Native window theme did not update')
  await window.loadURL(window.webContents.getURL())
  await waitFor(window, `document.querySelector('.workspace-app')
    && getComputedStyle(document.documentElement).colorScheme === 'light'`)
  await waitFor(window, `document.querySelector('[data-settings-tab]:enabled')`)
  await click(window, '[data-settings-tab]')
  await waitFor(window, `document.querySelector('#color-theme')?.value === 'light'`)
  await selectTheme('dark')
  await waitFor(window, `getComputedStyle(document.documentElement).colorScheme === 'dark'`)
  if (window.getBackgroundColor().toLowerCase() !== '#1f1f1f') throw Error('Native window did not restore the dark theme')
  console.log('Theme smoke: defaults, both themes, native window colors and reload persistence passed')
  await waitFor(window, `(() => {
    const nav = document.querySelector('.workspace-titlebar nav')
    const bounds = nav.getBoundingClientRect()
    const status = document.querySelector('.workspace-titlebar .workspace-model-status').getBoundingClientRect()
    const buttons = [...nav.querySelectorAll('button')]
    const centers = buttons.map(button => {
      const rect = button.getBoundingClientRect()
      return rect.left + rect.width / 2
    })
    const spacing = centers[1] - centers[0]
    return buttons.length === 3 && status.width > 0 && status.right < bounds.left
      && Math.abs(bounds.left + bounds.width / 2 - innerWidth / 2) < 1
      && spacing > 0 && centers.slice(1).every((center, i) => Math.abs(center - centers[i] - spacing) < 1)
      && buttons.every(button => getComputedStyle(button).borderTopWidth === '0px'
        && button.getBoundingClientRect().height >= 32)
  })()`)
  const originalBounds = window.getBounds()
  try {
    window.setSize(420, 510)
    await waitFor(window, `(() => {
      const nav = document.querySelector('.workspace-titlebar nav')
      const bounds = nav.getBoundingClientRect()
      const status = document.querySelector('.workspace-titlebar .workspace-model-status').getBoundingClientRect()
      const overlay = navigator.windowControlsOverlay
      const titlebar = overlay?.visible ? overlay.getTitlebarAreaRect() : { left: 0, right: innerWidth }
      const content = document.querySelector('.workspace-content')
      return status.width > 0 && status.left >= titlebar.left - 1 && status.right < bounds.left
        && bounds.right <= titlebar.right + 1
        && nav.scrollWidth <= nav.clientWidth + 1 && content.getBoundingClientRect().width === innerWidth
        && content.scrollWidth <= content.clientWidth + 1
    })()`)
  } finally { window.setBounds(originalBounds) }
  await evaluate(window, `[...document.querySelectorAll('.workspace-settings-nav button')]
    .find(button => button.textContent.trim() === '快捷键').click()`)
  await waitFor(window, `document.querySelector('#workspace-hotkey:enabled') && document.querySelector('#workspace-main-hotkey:enabled')`)
  await fill(window, '#workspace-hotkey', 'Ctrl+Alt+F10')
  await click(window, '.workspace-hotkey-form:has(#workspace-hotkey) button')
  const hotkey = await evaluate<{ result?: { accelerator: string; registered: boolean } }>(
    window, 'window.mediaLibrary.getHotkey()')
  if (hotkey.result?.accelerator !== 'Control+Alt+F10' || !hotkey.result.registered) {
    throw Error('Updated hotkey did not register')
  }
  console.log('Lifecycle smoke: single instance, window hide, tray restore and hotkey registration passed')
  await verifyOccupiedHotkey(window)
  await fill(window, '#workspace-main-hotkey', 'Ctrl+Alt+F10')
  await click(window, '.workspace-hotkey-form:has(#workspace-main-hotkey) button')
  await waitFor(window, `document.querySelector('.workspace-hotkey-form:has(#workspace-main-hotkey) [role="alert"]')?.textContent.includes('已用作')`)
  await fill(window, '#workspace-main-hotkey', 'Ctrl+Alt+F12')
  await click(window, '.workspace-hotkey-form:has(#workspace-main-hotkey) button')
  await waitFor(window, `document.querySelector('#workspace-main-hotkey:enabled') && !document.querySelector('.workspace-hotkey-form:has(#workspace-main-hotkey) [role="alert"]')`)
  if (!globalShortcut.isRegistered('Control+Alt+F12')) throw Error('Main page hotkey was not registered')
  window.hide()
  showMainPage()
  if (!window.isVisible()) throw Error('Main page hotkey did not show the window')
  await waitFor(window, `document.querySelector('.workspace-titlebar [data-workspace-tab][aria-current="page"]')
    && document.querySelector('.workspace-layout') && !document.querySelector('.workspace-settings')`)
  console.log('Title bar smoke: centered navigation, narrow layout and workspace switching passed')
}

async function verifyOccupiedHotkey(window: BrowserWindow): Promise<void> {
  const reserved = 'Control+Alt+F11'
  if (!globalShortcut.register(reserved, () => {})) throw Error('Could not reserve test hotkey')
  try {
    await fill(window, '#workspace-hotkey', 'Ctrl+Alt+F11')
    await click(window, '.workspace-hotkey-form:has(#workspace-hotkey) button')
    await waitFor(window, `document.querySelector('.workspace-settings [role="alert"]')?.textContent.includes('无法注册')`)
    const current = await evaluate<{ result?: { accelerator: string; registered: boolean } }>(
      window, 'window.mediaLibrary.getHotkey()')
    if (current.result?.accelerator !== 'Control+Alt+F10' || !current.result.registered) {
      throw Error('Hotkey conflict affected the previous shortcut')
    }
    console.log('Lifecycle smoke: occupied shortcut feedback and previous shortcut passed')
  } finally {
    globalShortcut.unregister(reserved)
  }
}
