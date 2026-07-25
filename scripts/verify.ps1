<#
.SYNOPSIS
    Full per-subsystem verification of AutoRim against a running colony.

.DESCRIPTION
    Exercises every subsystem with a read and, where applicable, a write. Checks that
    destructive commands refuse without confirm, and proves a mutation survives serialization
    by writing a unique marker, saving, and finding it in the save XML.

    Run this ONLY against the AutoRim-testbed save. It mutates colony state.

.EXAMPLE
    .\scripts\verify.ps1
    .\scripts\verify.ps1 -SkipWrites     # reads and safety checks only
#>
[CmdletBinding()]
param(
    [int]$Port = 7789,
    [switch]$SkipWrites
)

$ErrorActionPreference = 'Stop'

$tokenFile = Join-Path $env:LOCALAPPDATA '..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\AutoRim\bridge.token'
$savesDir  = Join-Path $env:LOCALAPPDATA '..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Saves'
$token     = (Get-Content $tokenFile -Raw).Trim()

Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(120)
$client.DefaultRequestHeaders.Add('X-AutoRim-Token', $token)

$script:pass = 0
$script:fail = 0
$script:sizes = @()

function Rpc($command, $arguments, $timeoutMs = 30000) {
    if ($null -eq $arguments) { $arguments = @{} }
    $payload = @{ command = $command; args = $arguments; timeoutMs = $timeoutMs } |
        ConvertTo-Json -Depth 12 -Compress
    $content = [System.Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, 'application/json')
    $response = $client.PostAsync("http://127.0.0.1:$Port/rpc", $content).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $script:sizes += [pscustomobject]@{ Command = $command; Bytes = [Text.Encoding]::UTF8.GetByteCount($text) }
    return $text | ConvertFrom-Json
}

function Check($label, $condition, $detail) {
    if ($condition) {
        $script:pass++
        Write-Host "  [PASS] $label" -ForegroundColor Green
    } else {
        $script:fail++
        Write-Host "  [FAIL] $label" -ForegroundColor Red
    }
    if ($detail) { Write-Host "         $detail" -ForegroundColor DarkGray }
}

function CheckOk($label, $command, $arguments, $extra) {
    $r = Rpc $command $arguments
    $ok = $r.ok -eq $true
    $detail = if ($ok) { if ($extra) { & $extra $r } else { '' } } else { "$($r.error.code): $($r.error.message)" }
    Check $label $ok $detail
    return $r
}

function Section($name) {
    Write-Host ""
    Write-Host "== $name ==" -ForegroundColor Cyan
}

Write-Host "`nAutoRim verification" -ForegroundColor Cyan

# --- preflight ------------------------------------------------------------------------------
Section "Preflight"
$health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 5
Check "bridge reachable" ($health.ok -eq $true) "version=$($health.data.version) commands=$($health.data.commandCount)"
Check "a game is loaded" ($health.data.gameLoaded -eq $true)
Check "all commands registered" ($health.data.commandCount -ge 100) "$($health.data.commandCount) commands"

$list = Rpc 'meta.list_commands' @{}
$destructive = @($list.data.commands | Where-Object { $_.tier -eq 'destructive' })
Check "destructive commands tiered" ($destructive.Count -ge 10) "$($destructive.Count) marked destructive"

# --- reads ----------------------------------------------------------------------------------
Section "Reads"
CheckOk "colony.snapshot"   'colony.snapshot'   @{} { param($r) "$($r.data.colonists.count) colonists, $($r.data.food.daysOfFood)d food, threat=$($r.data.threat.rating)" } | Out-Null
CheckOk "colony.alerts"     'colony.alerts'     @{} { param($r) "$($r.data.count) active" } | Out-Null
CheckOk "colony.letters"    'colony.letters'    @{} | Out-Null
CheckOk "colony.resources"  'colony.resources'  @{ limit = 10 } | Out-Null
CheckOk "colony.power"      'colony.power'      @{} | Out-Null
CheckOk "map.info"          'map.info'          @{} | Out-Null
CheckOk "map.things"        'map.things'        @{ category = 'item'; limit = 10 } | Out-Null
CheckOk "pawns.list"        'pawns.list'        @{} { param($r) "$($r.data.totalCount) colonists" } | Out-Null
CheckOk "work.list_types"   'work.list_types'   @{} | Out-Null
CheckOk "work.get_priorities" 'work.get_priorities' @{} | Out-Null
CheckOk "jobs.current"      'jobs.current'      @{} { param($r) "$($r.data.idleCount) idle" } | Out-Null
CheckOk "research.current"  'research.current'  @{} | Out-Null
CheckOk "research.list"     'research.list'     @{ limit = 10 } | Out-Null
CheckOk "research.suggest"  'research.suggest'  @{} { param($r) "top: $($r.data.suggestions[0].label)" } | Out-Null
$benches = CheckOk "bills.list_workbenches" 'bills.list_workbenches' @{} { param($r) "$($r.data.totalCount) benches" }
# Pawns and corpses implement IBillGiver too (that is how surgery is queued). A bench list
# containing a colonist means the filter regressed, and a bare count would not catch it.
$allPawnIds = @((Rpc 'pawns.list' @{ filter = 'all'; limit = 200 }).data.items | ForEach-Object { $_.id })
$pawnBenches = @($benches.data.items | Where-Object { $allPawnIds -contains $_.id })
Check "workbench list excludes pawns" ($pawnBenches.Count -eq 0) `
    $(if ($pawnBenches.Count -gt 0) { "leaked: $($pawnBenches.label -join ', ')" } else { "no pawns listed as benches" })
CheckOk "zones.list"        'zones.list'        @{} { param($r) "$($r.data.count) zones" } | Out-Null
CheckOk "areas.list"        'areas.list'        @{} | Out-Null
CheckOk "policies.list"     'policies.list'     @{} | Out-Null
CheckOk "designate.list"    'designate.list'    @{} | Out-Null
CheckOk "prisoners.list"    'prisoners.list'    @{} | Out-Null
CheckOk "trade.list_traders" 'trade.list_traders' @{} | Out-Null
CheckOk "caravan.list"      'caravan.list'      @{} | Out-Null
CheckOk "caravan.sendable"  'caravan.sendable'  @{} | Out-Null
CheckOk "world.factions"    'world.factions'    @{} { param($r) "$($r.data.count) factions" } | Out-Null
CheckOk "world.settlements" 'world.settlements' @{ limit = 10 } | Out-Null
CheckOk "world.quests"      'world.quests'      @{} | Out-Null
CheckOk "ideology.list"     'ideology.list'     @{} | Out-Null
CheckOk "query.search_defs" 'query.search_defs' @{ type = 'research'; query = 'electricity' } | Out-Null
CheckOk "query.thing_info"  'query.thing_info'  @{ thing = 'Wall' } | Out-Null
CheckOk "query.recipe_info" 'query.recipe_info' @{ recipe = 'Make_Pemmican' } | Out-Null
CheckOk "build.list_buildable" 'build.list_buildable' @{ limit = 10 } | Out-Null
CheckOk "analyze.idle_pawns" 'analyze.idle_pawns' @{} | Out-Null
CheckOk "analyze.bottlenecks" 'analyze.bottlenecks' @{} { param($r) "$($r.data.problemCount) problems" } | Out-Null
CheckOk "analyze.threats"   'analyze.threats'   @{} | Out-Null
$status = CheckOk "control.bridge_status" 'control.bridge_status' @{} `
    { param($r) "runsWhileUnfocused=$($r.data.runsWhileUnfocused)" }
# Unity suspends the whole app when unfocused unless this is set, which stops the main-thread
# pump and times out every request the moment the player switches to the chat window.
Check "game runs while window is unfocused" ($status.data.runsWhileUnfocused -eq $true) `
    "Application.runInBackground and Prefs.RunInBackground both true"

# Pick a live colonist to work with.
$pawns = Rpc 'pawns.list' @{}
if ($pawns.data.items.Count -eq 0) { throw "No colonists in this save; cannot verify." }
$subject   = $pawns.data.items[0]
$subjectId = $subject.id
Write-Host "  Using pawn '$($subject.name)' (id $subjectId) as the subject." -ForegroundColor DarkGray

CheckOk "pawns.detail"      'pawns.detail'      @{ pawn = $subjectId } | Out-Null
CheckOk "schedule.get"      'schedule.get'      @{ pawn = $subjectId } | Out-Null
CheckOk "health.list_surgeries" 'health.list_surgeries' @{ pawn = $subjectId } | Out-Null
CheckOk "analyze.best_pawn_for" 'analyze.best_pawn_for' @{ work = 'Cooking' } { param($r) "recommends $($r.data.recommendation)" } | Out-Null

$snapshot = Rpc 'colony.snapshot' @{}
$mapSize  = (Rpc 'map.info' @{}).data.maps[0]
$centre   = @{ x = [int]($mapSize.sizeX / 2); z = [int]($mapSize.sizeZ / 2) }
CheckOk "map.region"        'map.region'        @{ center = $centre; radius = 8 } | Out-Null

# --- error handling -------------------------------------------------------------------------
Section "Error handling"
$e1 = Rpc 'pawns.detail' @{ pawn = 'ThisPawnDoesNotExist' }
Check "unknown pawn -> NOT_FOUND" ((-not $e1.ok) -and $e1.error.code -eq 'NOT_FOUND') $e1.error.message

$e2 = Rpc 'query.thing_info' @{ thing = 'zzzznothing' }
Check "unknown def -> NOT_FOUND" ((-not $e2.ok) -and $e2.error.code -eq 'NOT_FOUND') $e2.error.message

$e3 = Rpc 'work.set_priority' @{ pawn = $subjectId; work = 'Cooking'; priority = 99 }
Check "out-of-range priority -> BAD_ARGS" ((-not $e3.ok) -and $e3.error.code -eq 'BAD_ARGS') $e3.error.message

$e4 = Rpc 'nope.nothing' @{}
Check "unknown command -> UNKNOWN_COMMAND" ((-not $e4.ok) -and $e4.error.code -eq 'UNKNOWN_COMMAND') $e4.error.message

# --- safety gate ----------------------------------------------------------------------------
Section "Safety gate (no confirm -> must refuse)"
foreach ($case in @(
    @{ c = 'designate.slaughter'; a = @{ wholeMap = $true; defName = 'Muffalo' } },
    @{ c = 'prisoners.execute';   a = @{ pawn = $subjectId } },
    @{ c = 'trade.execute';       a = @{} },
    @{ c = 'caravan.form';        a = @{ pawns = @($subjectId); destinationTile = 1 } },
    @{ c = 'health.add_surgery';  a = @{ pawn = $subjectId; recipe = 'Anesthetize' } }
)) {
    $r = Rpc $case.c $case.a
    # Either it refuses for confirmation, or it fails earlier for a legitimate reason
    # (no prisoners, no trade open). What must never happen is ok:true.
    $refused = (-not $r.ok)
    $needsConfirm = $r.error.code -eq 'NEEDS_CONFIRM'
    Check "$($case.c) refused without confirm" $refused `
        "$($r.error.code)$(if ($needsConfirm) { ' (preview returned)' })"
}

if ($SkipWrites) {
    Write-Host "`nSkipping writes (-SkipWrites)." -ForegroundColor Yellow
} else {
    # --- writes -----------------------------------------------------------------------------
    Section "Writes"

    $before = (Rpc 'work.get_priorities' @{ pawn = $subjectId }).data.priorities.Cooking
    $target = if ($before -eq 1) { 2 } else { 1 }
    $w = CheckOk "work.set_priority" 'work.set_priority' @{ pawn = $subjectId; work = 'Cooking'; priority = $target } `
        { param($r) $r.data.summary }
    $after = (Rpc 'work.get_priorities' @{ pawn = $subjectId }).data.priorities.Cooking
    Check "work priority actually changed" ($after -eq $target) "was $before, now $after"

    CheckOk "work.set_bulk" 'work.set_bulk' @{ assignments = @(
        @{ pawn = $subjectId; work = 'Hauling'; priority = 3 },
        @{ pawn = $subjectId; work = 'NotARealWorkType'; priority = 1 }
    ) } { param($r) "applied=$($r.data.appliedCount) rejected=$($r.data.failedCount)" } | Out-Null

    CheckOk "control.set_speed" 'control.set_speed' @{ speed = 'paused' } | Out-Null
    CheckOk "control.notify" 'control.notify' @{ text = 'AutoRim verification running' } | Out-Null
    CheckOk "schedule.set" 'schedule.set' @{ pawn = $subjectId; assignment = 'Work'; fromHour = 9; toHour = 11 } | Out-Null
    CheckOk "pawns.set_medical_care" 'pawns.set_medical_care' @{ pawn = $subjectId; care = 'best' } | Out-Null

    $research = Rpc 'research.list' @{ filter = 'available'; limit = 1 }
    if ($research.data.items.Count -gt 0) {
        $proj = $research.data.items[0].defName
        CheckOk "research.set_current" 'research.set_current' @{ project = $proj } { param($r) $r.data.summary } | Out-Null
    }

    $zoneBefore = (Rpc 'zones.list' @{}).data.count
    $zr = Rpc 'zones.create_stockpile' @{ area = @{ x1 = $centre.x; z1 = $centre.z; x2 = $centre.x + 2; z2 = $centre.z + 2 }; name = 'AutoRimVerify' }
    Check "zones.create_stockpile" ($zr.ok -eq $true) $(if ($zr.ok) { $zr.data.summary } else { "$($zr.error.code): $($zr.error.message)" })
    if ($zr.ok) {
        $zoneAfter = (Rpc 'zones.list' @{}).data.count
        Check "zone count increased" ($zoneAfter -gt $zoneBefore) "$zoneBefore -> $zoneAfter"
        CheckOk "zones.delete" 'zones.delete' @{ zone = 'AutoRimVerify' } | Out-Null
    }

    # Hunt a real animal rather than sweeping an empty corner, so this actually proves the
    # designation lands rather than passing on "considered 0".
    $wild = (Rpc 'pawns.list' @{ filter = 'wild'; limit = 3 }).data.items
    if ($wild.Count -gt 0) {
        $prey = $wild[0].id
        $h1 = CheckOk "designate.hunt (real animal)" 'designate.hunt' @{ things = @($prey) } `
            { param($r) $r.data.summary }

        $hunts = (Rpc 'designate.list' @{}).data.byKind | Where-Object { $_.defName -eq 'Hunt' }
        Check "hunt designation appears in designate.list" ($null -ne $hunts) "count=$($hunts.count)"

        # Re-issuing must explain itself rather than silently reporting 0 designated.
        $h2 = Rpc 'designate.hunt' @{ things = @($prey) }
        $reasons = @($h2.data.skipped)
        Check "repeat designation reports a reason" `
            (($h2.data.designated -eq 0) -and ($reasons.Count -gt 0)) `
            $(if ($reasons.Count -gt 0) { "reason: $($reasons[0].reason)" } else { "no reason given" })

        Rpc 'designate.cancel' @{ things = @($prey) } | Out-Null
    } else {
        Write-Host "  [skip] no wild animals on this map to hunt" -ForegroundColor DarkYellow
    }

    CheckOk "build.check" 'build.check' @{ thing = 'steel wall'; cell = $centre } `
        { param($r) "canPlace=$($r.data.canPlace) stuff=$($r.data.stuff)" } | Out-Null

    # --- equipment --------------------------------------------------------------------------
    Section "Equipment"

    $equippable = CheckOk "pawns.list_equippable" 'pawns.list_equippable' @{ pawn = $subjectId; kind = 'weapons' } `
        { param($r) "$($r.data.totalCount) weapons reachable" }

    $weapons = @($equippable.data.items | Where-Object { -not $_.unusable })
    if ($weapons.Count -gt 0) {
        $weapon = $weapons[0]
        $eq = CheckOk "pawns.equip" 'pawns.equip' @{ pawn = $subjectId; item = $weapon.id } `
            { param($r) $r.data.summary }
        # The pawn walks to the weapon, so the order is what we can assert synchronously.
        $job = (Rpc 'jobs.current' @{}).data.pawns | Where-Object { $_.pawn.id -eq $subjectId }
        Check "equip order accepted" ($eq.ok -eq $true) "current job: $($job.job)"

        # Wrong-type rejection must be clear rather than silently doing nothing.
        $wrong = Rpc 'pawns.wear' @{ pawn = $subjectId; item = $weapon.id }
        Check "wear rejects a weapon with a clear reason" `
            ((-not $wrong.ok) -and $wrong.error.message -match 'not apparel') $wrong.error.message
    } else {
        Write-Host "  [skip] no reachable weapons on this map" -ForegroundColor DarkYellow
    }

    $noSuch = Rpc 'pawns.equip' @{ pawn = $subjectId; item = 'definitely-not-a-weapon-xyz' }
    Check "equip unknown item -> NOT_FOUND" ((-not $noSuch.ok) -and $noSuch.error.code -eq 'NOT_FOUND') `
        $noSuch.error.message

    # --- persistence ------------------------------------------------------------------------
    Section "Persistence (save round-trip)"

    $marker = "AutoRimVerify$(Get-Random -Minimum 100000 -Maximum 999999)"
    $rn = CheckOk "pawns.rename (unique marker)" 'pawns.rename' @{ pawn = $subjectId; nick = $marker } `
        { param($r) $r.data.summary }

    if ($rn.ok) {
        $saveName = 'AutoRim-verify'
        $sv = CheckOk "control.save" 'control.save' @{ name = 'verify' } { param($r) $r.data.file } 60000
        Start-Sleep -Milliseconds 1500

        $savePath = Join-Path $savesDir "$saveName.rws"
        Check "save file written" (Test-Path $savePath) $savePath

        if (Test-Path $savePath) {
            $content = Get-Content $savePath -Raw
            Check "mutation present in save XML" ($content -match [regex]::Escape($marker)) `
                "marker '$marker' found in serialized save - the change survives save/reload"
            Check "save is well-formed XML" ($null -ne ([xml]$content)) "parsed without error"
        }

        # Put the name back so the testbed stays usable.
        Rpc 'pawns.rename' @{ pawn = $subjectId; nick = $subject.name } | Out-Null
    }

    # --- audit log --------------------------------------------------------------------------
    Section "Audit"
    $logPath = Join-Path $env:LOCALAPPDATA '..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\AutoRim\actions.log'
    Check "no destructive actions were executed" (-not (Test-Path $logPath)) `
        "actions.log absent, as expected - verification never confirms anything destructive"
}

# --- response sizes ---------------------------------------------------------------------------
Section "Response sizes (token budget)"
$worst = $script:sizes | Sort-Object Bytes -Descending | Select-Object -First 5
foreach ($s in $worst) {
    $tokens = [int]($s.Bytes / 4)
    $colour = if ($tokens -gt 2000) { 'Red' } elseif ($tokens -gt 1000) { 'Yellow' } else { 'DarkGray' }
    Write-Host ("  {0,-28} {1,7} bytes  ~{2} tokens" -f $s.Command, $s.Bytes, $tokens) -ForegroundColor $colour
}
$snapshotSize = ($script:sizes | Where-Object { $_.Command -eq 'colony.snapshot' } | Select-Object -First 1).Bytes
Check "colony.snapshot under 1.5k tokens" (($snapshotSize / 4) -lt 1500) "~$([int]($snapshotSize/4)) tokens"

# --- result -----------------------------------------------------------------------------------
$client.Dispose()
Write-Host ""
Write-Host ("{0} passed, {1} failed" -f $script:pass, $script:fail) `
    -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
Write-Host ""
if ($script:fail -gt 0) { exit 1 }
