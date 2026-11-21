const { spawn } = require('node:child_process')
const { resolve, join } = require('node:path')
const directory = resolve(__dirname, '../artifacts/webview-app')
const dotnet = join(process.env.ProgramFiles || 'C:/Program Files', 'dotnet', 'dotnet.exe')
const child = spawn(dotnet, [join(directory, 'MediaDesk.dll')], {
  cwd: directory, detached: true, stdio: 'ignore', windowsHide: true
})
child.on('error', error => { console.error('Cannot start MediaDesk:', error.message); process.exitCode = 1 })
child.unref()
