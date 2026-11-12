[CmdletBinding(SupportsShouldProcess=$true)]
param()
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this uninstaller from an Administrator PowerShell.'
}
$name = 'TailcatTunnel'
$bin = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'TailcatTray'))
$data = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'TailcatTray'))
$service = Get-Service $name -ErrorAction SilentlyContinue
if (-not $service) { Write-Output 'Tailcat Tunnel service is not installed.'; return }
$service.Dispose()
if (-not $PSCmdlet.ShouldProcess($name, 'Stop and uninstall service, restore startup settings, and delete service keys and data')) { return }
Stop-Service $name
$previous = Join-Path $data 'previous-startup.json'
if (Test-Path -LiteralPath $previous) {
    $startup = Get-Content -Raw -LiteralPath $previous | ConvertFrom-Json
    foreach ($item in $startup.PSObject.Properties) {
        if ($item.Name -notin @('sshd','TermService') -or $item.Value -notin @('Automatic','Manual','Disabled')) { throw 'Invalid saved startup settings.' }
        if ((Get-Service $item.Name).StartType -eq 'Automatic') { Set-Service $item.Name -StartupType $item.Value }
    }
}
& sc.exe delete $name | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Service deletion failed.' }
if (Test-Path -LiteralPath $data) {
    if ($data -ne [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'TailcatTray'))) { throw 'Unexpected service data path.' }
    Remove-Item -LiteralPath $data -Recurse -Force
    Write-Output 'Service key copy, configuration, logs, and state deleted.'
}
if (Test-Path -LiteralPath $bin) {
    if ($bin -ne [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'TailcatTray'))) { throw 'Unexpected installation path.' }
    Remove-Item -LiteralPath $bin -Recurse -Force
}
Write-Output 'Service uninstalled. Restart the tray to return to per-user mode. Original user keys and settings were not changed.'
