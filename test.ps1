$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /out:FakeTailcat.exe FakeTailcat.cs
    if ($LASTEXITCODE -ne 0) { throw 'Fake process build failed.' }
    & $compiler /nologo /out:Tests.exe /reference:TailcatTray.exe /reference:System.Windows.Forms.dll Tests.cs
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & .\Tests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally { Pop-Location }
