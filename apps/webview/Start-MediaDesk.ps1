$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
try {
    $dotnetCommand = Get-Command dotnet.exe -ErrorAction Stop
    $runtimes = & $dotnetCommand.Source --list-runtimes
    if (-not ($runtimes | Select-String '^Microsoft.WindowsDesktop.App 10\.')) {
        throw 'Please install the Microsoft .NET 10 Desktop Runtime (x64), then launch MediaDesk again.'
    }
    $assembly = Join-Path $PSScriptRoot 'MediaDesk.dll'
    Start-Process -FilePath $dotnetCommand.Source -ArgumentList ('"' + $assembly + '"') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
} catch {
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'MediaDesk', 'OK', 'Error') | Out-Null
}
