param (
    [switch]$Build,
    [switch]$Publish,
    [switch]$Deploy,
    [switch]$Run,
    [string]$DeployPath = "D:\Scripts\ProcessLog.exe"
)

$RootDir = Split-Path -Parent $PSScriptRoot
$CsprojPath = Join-Path $RootDir "ProcessLog.csproj"
$PublishDir = Join-Path $RootDir "publish"

function Bump-Version {
    $content = Get-Content $CsprojPath -Raw
    if ($content -match '<Version>(?<version>.*)</Version>') {
        $version = [version]$Matches['version']
        $newVersion = "{0}.{1}.{2}" -f $version.Major, $version.Minor, ($version.Build + 1)
        $content = $content -replace "<Version>.*</Version>", "<Version>$newVersion</Version>"
        $content | Set-Content $CsprojPath
        Write-Host "Bumped version to $newVersion" -ForegroundColor Magenta
        return $newVersion
    }
    return "1.0.0"
}

function Stop-ProcessLog {
    Write-Host "Stopping any running ProcessLog instances..." -ForegroundColor Cyan
    Get-Process ProcessLog -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    taskkill /F /IM ProcessLog.exe /T 2>$null

    if (Test-Path $DeployPath) {
        Write-Host "Waiting for file locks to release..." -ForegroundColor Gray
        $retry = 10
        while ($retry -gt 0) {
            try {
                $testStream = [System.IO.File]::Open($DeployPath, 'Open', 'Write', 'None')
                $testStream.Close()
                break
            } catch {
                Start-Sleep -Seconds 1
                $retry--
            }
        }
    }
}

if ($Build) {
    Write-Host "Building project (Warnings as Errors)..." -ForegroundColor Cyan
    dotnet build -c Release $RootDir /p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with errors or warnings."
        exit $LASTEXITCODE
    }
}

if ($Deploy) {
    Stop-ProcessLog

    Write-Host "Publishing single-file to $DeployPath..." -ForegroundColor Cyan
    dotnet publish $RootDir -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$PublishDir\single"
    $SingleExe = Join-Path "$PublishDir\single" "ProcessLog.exe"
    if (Test-Path $SingleExe) {
        if (Test-Path $DeployPath) {
            try {
                $oldPath = $DeployPath + ".old"
                if (Test-Path $oldPath) { Remove-Item $oldPath -Force -ErrorAction SilentlyContinue }
                Rename-Item $DeployPath $oldPath -Force -ErrorAction SilentlyContinue
            } catch {
                Write-Warning "Could not rename $DeployPath. Attempting direct overwrite..."
            }
        }
        try {
            $DeployDir = Split-Path $DeployPath
            if (-not (Test-Path $DeployDir)) { New-Item -ItemType Directory -Path $DeployDir -Force | Out-Null }
            Copy-Item $SingleExe $DeployPath -Force -ErrorAction Stop
            Write-Host "Deployed to $DeployPath" -ForegroundColor Green
        } catch {
            Write-Error "CRITICAL: Failed to copy to $DeployPath. File is likely still locked.`n$($_.Exception.Message)"
            exit 1
        }
    }
}

if ($Publish) {
    $newVersion = Bump-Version
    Write-Host "Publishing Release $newVersion to GitHub..." -ForegroundColor Cyan

    git add .
    git commit -m "v$newVersion"
    git push

    dotnet publish $RootDir -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$PublishDir\release"
    $ReleaseExe = Join-Path "$PublishDir\release" "ProcessLog.exe"
    gh release create "v$newVersion" $ReleaseExe --title "Release v$newVersion" --notes "Automated release via update.ps1"
}

if ($Run) {
    Write-Host "Starting ProcessLog..." -ForegroundColor Cyan
    $TargetExe = if ($Deploy) { $DeployPath } else { Join-Path "$PublishDir\single" "ProcessLog.exe" }

    if (-not (Test-Path $TargetExe)) {
        # Fallback to dotnet run
        Write-Host "No published exe found, using dotnet run..." -ForegroundColor Gray
        Push-Location $RootDir
        dotnet run -c Release
        Pop-Location
    } else {
        sudo $TargetExe
    }
}

Write-Host "Done!" -ForegroundColor Green
