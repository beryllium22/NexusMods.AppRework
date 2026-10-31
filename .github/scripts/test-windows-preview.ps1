param(
    [string] $PreviewDirectory = 'artifacts/windows-preview',
    [string] $ResultsDirectory = 'TestResults/windows-preview'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The native Windows preview check requires Windows.' }
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Run this startup check on a disposable GitHub Actions runner.' }

$previewPath = (Resolve-Path $PreviewDirectory).Path
$resultsPath = [System.IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $resultsPath -Force | Out-Null

foreach ($requiredFile in @('NexusMods.App.exe', 'coreclr.dll', 'hostpolicy.dll', 'BUILD-INFO.json')) {
    if (-not (Test-Path (Join-Path $previewPath $requiredFile))) { throw "Preview is missing $requiredFile" }
}
foreach ($excludedFile in @('uninstall.bat', 'uninstall-helper.ps1')) {
    if (Test-Path (Join-Path $previewPath $excludedFile)) { throw "Preview contains installer helper $excludedFile" }
}

function Get-DirectoryState([string] $Path) {
    if (-not (Test-Path $Path)) { return 'missing' }
    $entries = @(Get-ChildItem -LiteralPath $Path -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [System.IO.Path]::GetRelativePath($Path, $_.FullName)
            content = if ($_.PSIsContainer) { 'directory' } else { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        }
    })
    return ConvertTo-Json -InputObject $entries -Compress
}

function Get-NxmRegistrationState {
    $path = 'HKCU:\Software\Classes\nxm'
    if (-not (Test-Path $path)) { return 'missing' }
    $keys = @((Get-Item $path)) + @(Get-ChildItem $path -Recurse)
    $state = @($keys | Sort-Object Name | ForEach-Object {
        $key = $_
        [ordered]@{
            key = $key.Name
            values = @($key.GetValueNames() | Sort-Object | ForEach-Object {
                [ordered]@{ name = $_; kind = $key.GetValueKind($_).ToString(); value = $key.GetValue($_) }
            })
        }
    })
    return ConvertTo-Json -InputObject $state -Depth 6 -Compress
}

$localData = [Environment]::GetFolderPath('LocalApplicationData')
$originalData = Join-Path $localData 'NexusMods.App'
$reworkData = Join-Path $localData 'NexusMods.AppRework'
$originalBefore = Get-DirectoryState $originalData
$associationBefore = Get-NxmRegistrationState
$process = $null

try {
    # Exercise the published native host, service startup, database initialization,
    # and CLI server without requiring an account or a game installation.
    $process = Start-Process -FilePath (Join-Path $previewPath 'NexusMods.App.exe') -ArgumentList @('as-main', 'heartbeat') -WorkingDirectory $previewPath -PassThru -RedirectStandardOutput (Join-Path $resultsPath 'stdout.log') -RedirectStandardError (Join-Path $resultsPath 'stderr.log')
    if (-not $process.WaitForExit(120000)) { throw 'Published Windows application did not finish startup within two minutes.' }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Published Windows application exited with code $($process.ExitCode)." }

    $stdout = Get-Content (Join-Path $resultsPath 'stdout.log') -Raw
    if ($stdout -notmatch 'ProcessId:') { throw 'The published application did not execute its heartbeat command.' }
    if (-not (Test-Path (Join-Path $reworkData 'Configs'))) { throw 'The fork did not create its independent configuration directory.' }
    if (-not (Test-Path (Join-Path $reworkData 'DataModel/MnemonicDB.rocksdb'))) { throw 'The fork did not initialize its independent database.' }
    if ((Get-DirectoryState $originalData) -cne $originalBefore) { throw 'Startup modified the original application data directory.' }
    if ((Get-NxmRegistrationState) -cne $associationBefore) { throw 'Startup changed the existing nxm association without opt-in.' }

    [ordered]@{
        nativeStartup = 'passed'
        heartbeat = 'passed'
        separateConfiguration = 'passed'
        separateDatabase = 'passed'
        originalDataUnchanged = 'passed'
        nxmAssociationUnchanged = 'passed'
        guiAndRealGameTesting = 'not covered by this check'
    } | ConvertTo-Json | Set-Content (Join-Path $resultsPath 'startup-check.json') -Encoding utf8
    Write-Output 'Published Windows startup and isolation checks passed.'
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
}
