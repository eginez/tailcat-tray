param(
    [Parameter(Mandatory=$true)][string]$UserSettingsPath,
    [Parameter(Mandatory=$true)][string]$ServerKeyPath,
    [Parameter(Mandatory=$true)][string]$ControlSid,
    [ValidateSet('ssh','ssh-rdp')][string]$Mode = 'ssh-rdp'
)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this installer from an Administrator PowerShell.'
}
$name = 'TailcatTunnel'
$bin = Join-Path $env:ProgramFiles 'TailcatTray'
$data = Join-Path $env:ProgramData 'TailcatTray'
if ((Get-Service $name -ErrorAction SilentlyContinue) -or (Test-Path $data) -or (Test-Path $bin)) {
    throw 'Service installation already exists. Refusing to overwrite service state or keys.'
}
$settings = Get-Content -Raw -LiteralPath $UserSettingsPath | ConvertFrom-Json
$key = (Get-Content -Raw -LiteralPath $settings.MacKeyPath).Trim()
if ($key -notmatch '^nodekey:[a-fA-F0-9]{64}$') { throw 'Invalid client allowlist.' }
foreach ($file in @($settings.TailcatPath, $ServerKeyPath, (Join-Path $PSScriptRoot 'TailcatTunnel.exe'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing file: $file" }
}
$null = [Security.Principal.SecurityIdentifier]::new($ControlSid)
$created = $false
$createdDirectories = @()
$prior = @{}
function Set-DirectoryAcl([string]$Path, [string]$ServiceRights, [bool]$UserRead) {
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    if ($ServiceRights) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($script:serviceSid,$ServiceRights,'ContainerInherit,ObjectInherit','None','Allow'))
    }
    if ($UserRead) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($ControlSid),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
try {
    foreach ($path in @($bin,$data)) {
        New-Item -ItemType Directory -Path $path | Out-Null
        $createdDirectories += $path
    }
    Set-DirectoryAcl $bin '' $false
    Set-DirectoryAcl $data '' $false
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TailcatTunnel.exe') -Destination $bin
    Copy-Item -LiteralPath $settings.TailcatPath -Destination (Join-Path $bin 'tailcat.exe')
    New-Service -Name $name -BinaryPathName ('"' + (Join-Path $bin 'TailcatTunnel.exe') + '"') -DisplayName 'Tailcat Tunnel' -StartupType Automatic -DependsOn Tcpip -Credential ([pscredential]::new('NT AUTHORITY\LocalService',(New-Object Security.SecureString))) | Out-Null
    $created = $true
    & sc.exe sidtype $name unrestricted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable service SID.' }
    $script:serviceSid = ([Security.Principal.NTAccount]::new("NT SERVICE\$name")).Translate([Security.Principal.SecurityIdentifier])
    Set-DirectoryAcl $bin 'ReadAndExecute' $false
    Set-DirectoryAcl $data 'ReadAndExecute' $false
    $state = Join-Path $data 'state'
    New-Item -ItemType Directory -Path $state | Out-Null
    Set-DirectoryAcl $state 'Modify' $true
    Copy-Item -LiteralPath $ServerKeyPath -Destination (Join-Path $data 'server.private.json')
    [IO.File]::WriteAllText((Join-Path $data 'client.pub'),$key)
    $config = @{ControlSid=$ControlSid; Tunnel=@{
        TailcatPath=(Join-Path $bin 'tailcat.exe'); MacKeyPath=(Join-Path $data 'client.pub');
        SshIdentity='windows'; ServerKey='home-pc'; ServerKeyPath=(Join-Path $data 'server.private.json')
    }}
    $config | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $data 'service.json') -Encoding utf8
    [IO.File]::WriteAllText((Join-Path $state 'mode.txt'), $Mode)
    foreach ($service in @('sshd','TermService')) {
        $current = Get-Service $service
        $prior[$service] = $current.StartType.ToString()
        Set-Service $service -StartupType Automatic
        if ($current.Status -ne 'Running') { Start-Service $service }
    }
    $prior | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'previous-startup.json')
    & sc.exe failure $name reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set service recovery.' }
    & sc.exe failureflag $name 1 | Out-Null
    Start-Service $name
    (Get-Service $name).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
    'Service installed and running as LocalService.'
} catch {
    if ($created) {
        Stop-Service $name -ErrorAction SilentlyContinue
        & sc.exe delete $name | Out-Null
    }
    foreach ($service in $prior.Keys) { Set-Service $service -StartupType $prior[$service] -ErrorAction SilentlyContinue }
    if ($createdDirectories.Count) {
        foreach ($path in $createdDirectories) {
            $resolved = [IO.Path]::GetFullPath($path)
            $allowed = @([IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'TailcatTray')), [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'TailcatTray')))
            if ($resolved -notin $allowed) { throw 'Refusing to clean up an unexpected installation path.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Continue
        }
    }
    throw
}
