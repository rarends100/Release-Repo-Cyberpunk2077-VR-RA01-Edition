# RA01: M12-1 - builds everything with one command and gathers it in Cyberpunk2077-VR-RA01-Edition\ (M31-1; was Cyberpunk_VR_Definitive_Edition_RA01\,
# RA01: and Ship\ before M20-7), ready to run or zip:
# RA01:   RA01.dll (the mod), xinput9_1_0.dll (the doorman), RA01.Launcher.exe (M40: one self-contained exe with its window) + RA01_Guide.txt, and openxr_loader.dll.
# RA01: Run from the project folder in PowerShell:   .\build.ps1
# RA01: Close the game first - it locks RA01.dll while it runs. Your settings in that folder are never overwritten; on the first run it is copied over from the old
# RA01: launcher folder (Launcher\bin\Release\net8.0) if one is there.
# RA01: Cost - CPU: high while it runs (three compiles, a minute or two, NativeAOT is the slow part); GPU: none. Not part of the game at all.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# RA01: runs one dotnet command and stops the script if it failed, so a broken build never ships.
function Step($what, [string[]]$dotnetArgs) {
    Write-Host "RA01: $what..." -ForegroundColor Cyan
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "RA01: $what failed - see the errors above." }
}

Step 'building RA01.dll (the mod)'          @('publish', 'RA01.csproj', '-c', 'Release')
Step 'building xinput9_1_0.dll (the doorman)' @('publish', 'Launcher\Doorman\RA01.Doorman.csproj', '-c', 'Release')
# RA01: M31-2 - the launcher gets the RA01 icon (assets\RA01.ico) stamped into RA01.Launcher.exe instead of the default one. Without the icon file it builds as before.
$icon = Join-Path $PSScriptRoot 'assets\RA01.ico'
# RA01: M40 - the launcher is published as ONE self-contained exe (NativeAOT, its own window) - players need no .NET installed.
$launcherArgs = @('publish', 'Launcher\RA01.Launcher.csproj', '-c', 'Release')
if (Test-Path $icon) { $launcherArgs += "-p:ApplicationIcon=$icon" } else { Write-Host "RA01: assets\RA01.ico not found - the launcher keeps the default icon" -ForegroundColor Yellow }
Step 'building RA01.Launcher.exe'           $launcherArgs

$shipName = 'Cyberpunk2077-VR-RA01-Edition'
$ship = Join-Path $PSScriptRoot $shipName
# RA01: M31-1 - the ship folder's older names (newest first): the first build after a rename moves the old folder (with your Settings and Logs) to the new
# RA01: name - a rename (a copy if Windows refuses, M31-1b), nothing deleted. If the new one already exists, an old one is left alone with a warning (never merged by guesswork).
foreach ($oldName in @('Cyberpunk_VR_Definitive_Edition_RA01', 'Ship')) {
    $oldShip = Join-Path $PSScriptRoot $oldName
    if (-not (Test-Path $oldShip)) { continue }
    if (Test-Path $ship) { Write-Host "RA01: both $oldName\ and $shipName\ exist - $oldName\ left alone (move anything you need from it yourself)" -ForegroundColor Yellow; continue }
    # RA01: M31-1b - Rob 10:24: the rename was refused even after a restart (something - Explorer, the Claude app's folder watch, antivirus or the search
    # RA01: indexer - can hold the folder itself; that blocks a rename, not a copy). Then everything is COPIED to the new name instead and the old folder is
    # RA01: left as it was, for you to delete when you like. Windows' own reason is shown either way.
    try {
        Rename-Item $oldShip $shipName
        Write-Host "RA01: renamed $oldName\ to $shipName\ (your settings and logs came with it)" -ForegroundColor Cyan
    }
    catch {
        Write-Host "RA01: Windows refused to rename $oldName\ ($($_.Exception.Message)) - copying it to $shipName\ instead" -ForegroundColor Yellow
        try { Copy-Item $oldShip $ship -Recurse -Force }
        catch { throw "RA01: couldn't copy $oldName\ to $shipName\ either: $($_.Exception.Message)" }
        Write-Host "RA01: copied $oldName\ to $shipName\ (settings and logs included) - the old folder is untouched; delete it when you like" -ForegroundColor Cyan
    }
}
$launcherOut = 'Launcher\bin\Release\net8.0\win-x64\publish'   # RA01: M40 - the published launcher
$oldLauncherOut = 'Launcher\bin\Release\net8.0'                # RA01: the old launcher folder (only for the one-time settings copy below)
New-Item -ItemType Directory -Force $ship | Out-Null

# RA01: M40 - the launcher exe and its guide text (CONTROLS & FEATURES tab). The old launcher's .NET side files are removed from the ship folder - the new exe
# RA01: carries everything inside it (only these exact old names, nothing else is touched).
Copy-Item (Join-Path $launcherOut 'RA01.Launcher.exe') $ship -Force
Copy-Item 'Launcher\RA01_Guide.txt' $ship -Force
foreach ($old in @('RA01.Launcher.dll', 'RA01.Launcher.runtimeconfig.json', 'RA01.Launcher.deps.json', 'RA01.Launcher.pdb')) {
    $oldFile = Join-Path $ship $old
    if (Test-Path $oldFile) { Remove-Item $oldFile -Force; Write-Host "RA01: removed the old launcher file $old (the new launcher is one exe)" -ForegroundColor Cyan }
}
Copy-Item 'bin\Release\net8.0\win-x64\publish\RA01.dll' $ship -Force
Copy-Item 'Launcher\Doorman\bin\Release\net8.0\win-x64\publish\xinput9_1_0.dll' $ship -Force
Copy-Item 'Launcher\openxr_loader.dll' $ship -Force
# RA01: M52 - Mys own "guns ready" voice (his signature), played with the GUNS READY banner: assets\audio_guns_ready.wav -> Sounds\RA01_GunsReady.wav. Missing = no voice.
$voice = Join-Path $PSScriptRoot 'assets\audio_guns_ready.wav'
if (Test-Path $voice) { New-Item -ItemType Directory -Force (Join-Path $ship 'Sounds') | Out-Null; Copy-Item $voice (Join-Path $ship 'Sounds\RA01_GunsReady.wav') -Force } else { Write-Host "RA01: assets\audio_guns_ready.wav not found - no guns ready voice" -ForegroundColor Yellow }

$settings = Join-Path $oldLauncherOut 'RA01_Settings.ini'
# RA01: M20-4 - the mod keeps its settings in the ship folder's Settings\ now (it moves them there itself on its first run), so that counts as 'already there' too.
if (-not (Test-Path (Join-Path $ship 'RA01_Settings.ini')) -and -not (Test-Path (Join-Path $ship 'Settings\RA01_Settings.ini')) -and (Test-Path $settings)) {
    Copy-Item $settings $ship
    Write-Host "RA01: copied your existing RA01_Settings.ini into the ship folder" -ForegroundColor Cyan
}

Write-Host "RA01: done - run $shipName\RA01.Launcher.exe (Logs\ and Settings\ are made beside it)" -ForegroundColor Green
