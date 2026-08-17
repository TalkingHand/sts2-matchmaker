<#
.SYNOPSIS
    Decompiles the sts2 game API surface this mod depends on and diffs it against the last saved snapshot, so a
    game update that renames/removes/restructures something we touch shows up immediately instead of waiting for
    a user's ReflectionTypeLoadException report.

.DESCRIPTION
    Runs `ilspycmd -t <type>` (https://github.com/icsharpcode/ILSpy, `dotnet tool install -g ilspycmd`) against a
    fixed list of watched sts2 types - the ones referenced from src/Helpers/*.cs (dynamically, via reflection) or
    directly by name elsewhere in src/ - and saves each type's decompiled source under
    reference/api-snapshots/<Label>/. Snapshots are gitignored (reference/ already is) since they contain
    MegaCrit's own decompiled source, not ours.

    On a second run with the same -Label, any changed/added/removed watched type is printed as a diff. Run once
    per branch after Steam switches (label it "general" or "beta") to build up history for that branch, or just
    use the default label to track whatever's currently installed.

    When a game update touches a type or member this mod relies on but ISN'T in the watch list yet, this script
    won't catch it - extend $WatchedTypes below whenever src/Helpers/ starts depending on something new.

.PARAMETER DllPath
    Path to sts2.dll. Defaults to the live Steam install.

.PARAMETER Label
    Snapshot subfolder name, e.g. "general" or "beta". Defaults to "current".

.EXAMPLE
    .\tools\check-game-api.ps1 -Label beta
    .\tools\check-game-api.ps1 -DllPath "C:\...\reference\sts2_general.dll" -Label general
#>
param(
    [string]$DllPath = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll",
    [string]$Label = "current"
)

# Deliberately NOT $ErrorActionPreference = "Stop": ilspycmd exiting non-zero for a type that's genuinely absent
# on this branch is an expected, handled case here (see $exists below), not a script failure. Stop would turn
# PowerShell's stderr-wrapping of that native-exe "error" into a terminating exception and abort the whole run.

# Extend this list whenever src/Helpers/ (or any Patches/UI/Matchmaking code referencing sts2 types directly)
# starts depending on a new sts2 type or a type whose member names it already relies on by exact name.
$WatchedTypes = @(
    "MegaCrit.Sts2.Core.Entities.Multiplayer.LobbyPlayer",          # general name (beta: StartRunLobbyPlayer)
    "MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer",  # beta name (general: LobbyPlayer)
    "MegaCrit.Sts2.Core.Entities.Multiplayer.NetError",
    "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby",
    "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.LoadRunLobby",
    "MegaCrit.Sts2.Core.Multiplayer.NetHostGameService",
    "MegaCrit.Sts2.Core.Multiplayer.PeerVersionInfo",
    "MegaCrit.Sts2.Core.Nodes.Multiplayer.NRemoteLobbyPlayerContainer"
)

if (-not (Test-Path $DllPath)) {
    Write-Error "sts2.dll not found at: $DllPath"
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$snapshotDir = Join-Path $repoRoot "reference\api-snapshots\$Label"
New-Item -ItemType Directory -Force -Path $snapshotDir | Out-Null

Write-Host "Checking $($WatchedTypes.Count) watched types against:`n  $DllPath`nSnapshot label: $Label`n"

$changedCount = 0
$missingCount = 0
$newBaselineCount = 0

foreach ($typeName in $WatchedTypes) {
    $snapshotFile = Join-Path $snapshotDir "$typeName.cs"
    $output = & ilspycmd -t $typeName $DllPath 2>&1
    $exitCode = $LASTEXITCODE
    $exists = $exitCode -eq 0

    if (-not $exists) {
        $current = "// MISSING - type not found in this assembly`n"
    } else {
        # Drop ilspycmd's own "you're not using the latest version" nag line, not a real diff signal.
        $current = ($output | Where-Object { $_ -notmatch "latest version|ICSharpCode.Decompiler used" }) -join "`n"
    }

    if (Test-Path $snapshotFile) {
        $previous = Get-Content $snapshotFile -Raw
        if ($previous.Trim() -ne $current.Trim()) {
            $changedCount++
            $wasMissing = $previous.Trim().StartsWith("// MISSING")
            $nowMissing = -not $exists
            if ($nowMissing -and -not $wasMissing) {
                Write-Host "[REMOVED]  $typeName" -ForegroundColor Red
            } elseif ($wasMissing -and -not $nowMissing) {
                Write-Host "[ADDED]    $typeName" -ForegroundColor Green
            } else {
                Write-Host "[CHANGED]  $typeName" -ForegroundColor Yellow
            }
        }
    } else {
        $newBaselineCount++
        $status = if ($exists) { "present" } else { "MISSING" }
        Write-Host "[BASELINE] $typeName ($status)" -ForegroundColor Cyan
    }

    if (-not $exists) { $missingCount++ }
    Set-Content -Path $snapshotFile -Value $current -NoNewline
}

Write-Host "`nDone. $changedCount changed, $missingCount currently missing, $newBaselineCount new baselines saved."
if ($changedCount -gt 0) {
    Write-Host "Diff a changed type with: git diff --no-index reference\api-snapshots\$Label\<OldCopy>.cs reference\api-snapshots\$Label\<Type>.cs"
    Write-Host "(or just re-run after 'git stash'/checkout of the previous snapshot commit if you track these - by default they're gitignored, so compare manually or copy the file aside before re-running.)"
}
