param(
    [string] $OutputDirectory = 'artifacts/windows-preview'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows) { throw 'The Windows preview must be published and checked on Windows.' }

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $repositoryRoot
try {
    $outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path $outputPath) { throw "Use an empty output directory: $outputPath" }

    # Keep the manual installation mode: inherited archive builds query the
    # original application's update feed, which is not this fork's update feed.
    dotnet publish src/NexusMods.App/NexusMods.App.csproj --configuration Release --runtime win-x64 --self-contained true --output $outputPath -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:CreateHardLinksForCopyFilesToOutputDirectoryIfPossible=true -p:CreateHardLinksForCopyLocalIfPossible=true
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }

    # These inherited installer helpers can revert managed games or terminate
    # other NexusMods.App processes. A portable development ZIP has no uninstaller.
    foreach ($helper in @('uninstall.bat', 'uninstall-helper.ps1')) {
        $helperPath = Join-Path $outputPath $helper
        if (Test-Path $helperPath) { Remove-Item -LiteralPath $helperPath }
    }

    Copy-Item LICENSE.md $outputPath
    Copy-Item docs/WINDOWS_PREVIEW.md (Join-Path $outputPath 'README.md')
    Copy-Item docs/REWORK_ROADMAP.md (Join-Path $outputPath 'PORTS-AND-ROADMAP.md')

    $buildCommit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Unable to identify the published source.' }
    $sourceCommit = if ($env:SOURCE_HEAD_SHA) { $env:SOURCE_HEAD_SHA } else { $buildCommit }
    $repository = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'beryllium22/NexusMods.AppRework' }
    [ordered]@{
        application = 'App Rework development preview'
        platform = 'win-x64'
        source = "https://github.com/$repository"
        sourceCommit = $sourceCommit
        buildCommit = $buildCommit
        buildRun = if ($env:GITHUB_RUN_ID) { "https://github.com/$repository/actions/runs/$env:GITHUB_RUN_ID" } else { $null }
        selfContained = $true
        installationMode = 'Manually'
        signed = $false
    } | ConvertTo-Json | Set-Content (Join-Path $outputPath 'BUILD-INFO.json') -Encoding utf8

    Write-Output "Published Windows preview to $outputPath"
} finally {
    Pop-Location
}
