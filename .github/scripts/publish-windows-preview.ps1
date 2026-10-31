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
    # Runtime-specific restore can expose dependencies absent from the portable
    # solution graph. Do not upload a preview with reported dependency advisories.
    dotnet publish src/NexusMods.App/NexusMods.App.csproj --configuration Release --runtime win-x64 --self-contained true --output $outputPath -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:CreateHardLinksForCopyLocalIfPossible=true '-warnaserror:NU1901,NU1902,NU1903,NU1904'
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }

    # These inherited installer helpers can revert managed games or terminate
    # other NexusMods.App processes. A portable development ZIP has no uninstaller.
    foreach ($helper in @('uninstall.bat', 'uninstall-helper.ps1')) {
        $helperPath = Join-Path $outputPath $helper
        if (Test-Path $helperPath) { Remove-Item -LiteralPath $helperPath }
    }

    Copy-Item LICENSE.md $outputPath
    (Get-Content docs/FEATURES_AND_COMPATIBILITY.md -Raw).Replace('(REWORK_ROADMAP.md)', '(PORTS-AND-ROADMAP.md)') |
        Set-Content (Join-Path $outputPath 'FEATURES_AND_COMPATIBILITY.md') -Encoding utf8
    $previewImagesPath = Join-Path $outputPath 'images'
    New-Item -ItemType Directory -Path $previewImagesPath -Force | Out-Null
    Copy-Item -LiteralPath @('docs/images/profiles-preview.png', 'docs/images/profiles-compact-preview.png') -Destination $previewImagesPath
    Copy-Item docs/WINDOWS_PREVIEW.md (Join-Path $outputPath 'README.md')
    # Keep the packaged documents navigable after renaming the preview guide
    # and roadmap. Developer-only instructions remain linked to the source repo.
    $roadmap = Get-Content docs/REWORK_ROADMAP.md -Raw
    $roadmap.Replace('(WINDOWS_PREVIEW.md)', '(README.md)').Replace('(developers/Contributing.md)', '(https://github.com/beryllium22/NexusMods.AppRework/blob/main/docs/developers/Contributing.md)') |
        Set-Content (Join-Path $outputPath 'PORTS-AND-ROADMAP.md') -Encoding utf8
    (Get-Content docs/STABILITY.md -Raw).Replace('(REWORK_ROADMAP.md)', '(PORTS-AND-ROADMAP.md)') |
        Set-Content (Join-Path $outputPath 'STABILITY.md') -Encoding utf8

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
