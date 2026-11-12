param([switch]$Disable)
$ErrorActionPreference = 'Stop'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$name = 'TailcatTray'
if ($Disable) {
    if (Get-ItemProperty -LiteralPath $runKey -Name $name -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -LiteralPath $runKey -Name $name
    }
    Write-Output 'Tray startup disabled for the current user. The tunnel service is unchanged.'
    return
}
$exe = Join-Path $PSScriptRoot 'TailcatTray.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build TailcatTray.exe before enabling startup.' }
New-Item -Path $runKey -Force | Out-Null
New-ItemProperty -LiteralPath $runKey -Name $name -PropertyType String -Value ('"' + $exe + '"') -Force | Out-Null
Write-Output 'Tray startup enabled for the current user. It will display the service status after login.'
