$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /main:TailcatTray.Program /out:TailcatTray.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll TailcatTray.cs Control.cs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $compiler /nologo /target:exe /main:TailcatTray.Cli /out:tailcat-tray.exe /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll Control.cs
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed.' }
    & $compiler /nologo /target:exe /main:TailcatTray.TunnelService /out:TailcatTunnel.exe /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.ServiceProcess.dll TailcatTray.cs Control.cs TunnelService.cs
    if ($LASTEXITCODE -ne 0) { throw 'Service build failed.' }
} finally { Pop-Location }
