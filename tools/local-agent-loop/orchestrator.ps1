<#
.SYNOPSIS
  Drives tasks.json: for each pending task, runs agent_loop.ps1 (the local model),
  then independently re-verifies the task's acceptanceCriteria itself (never trusts
  the model's own finish(success) claim). Escalates to Claude after MaxAttempts
  failures, and queues diffs for spot-review on a sample of successes.

.PARAMETER TasksFile
  Path to tasks.json (default: tools/local-agent-loop/tasks.json).

.PARAMETER SpotReviewEvery
  Queue every Nth successful task for spot-review (default 4). Tasks with
  alwaysSpotReview=true are queued regardless.
#>
param(
    [string]$TasksFile = (Join-Path $PSScriptRoot "tasks.json"),
    [int]$SpotReviewEvery = 4
)

. (Join-Path $PSScriptRoot "tools_sandbox.ps1")

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

$escalationDir = Join-Path $PSScriptRoot "escalation"
$spotReviewDir = Join-Path $PSScriptRoot "spot-review"
foreach ($d in @($escalationDir, $spotReviewDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
}

function Save-Tasks {
    param($TasksObj)
    $TasksObj | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $TasksFile -Encoding utf8
}

function Test-AcceptanceCriteria {
    param([string[]]$Criteria)
    foreach ($cmd in $Criteria) {
        $r = Invoke-SandboxRunCommand -Command $cmd
        if (-not $r.ok) {
            return @{ ok = $false; failedCommand = $cmd; output = $r.output }
        }
    }
    return @{ ok = $true }
}

$tasksObj = Get-Content -Raw -LiteralPath $TasksFile | ConvertFrom-Json
$successCount = 0
$processedAny = $false

foreach ($task in $tasksObj.tasks) {
    if ($task.status -ne "pending") { continue }
    $processedAny = $true

    Write-Host "=== Task $($task.id): $($task.title) ==="

    $attemptResultJson = & powershell -NoProfile -File (Join-Path $PSScriptRoot "agent_loop.ps1") -TaskId $task.id -TasksFile $TasksFile
    $attemptResult = $attemptResultJson | ConvertFrom-Json

    if (-not $task.attempts) { $task | Add-Member -NotePropertyName attempts -NotePropertyValue @() -Force }
    $task.attempts += @{
        at      = (Get-Date).ToString("o")
        claimed = $attemptResult.success
        summary = $attemptResult.summary
    }

    $check = Test-AcceptanceCriteria -Criteria $task.acceptanceCriteria

    if ($check.ok) {
        $task.status = "done"
        $successCount++
        Write-Host "  -> DONE (acceptance criteria verified independently)"

        $sample = ($task.alwaysSpotReview -eq $true) -or (($successCount % $SpotReviewEvery) -eq 0)
        if ($sample) {
            Push-Location $RepoRoot
            try {
                $paths = $task.allowedPaths -join ' '
                $diff = & cmd /c "git diff HEAD -- $paths 2>&1"
                $diffPath = Join-Path $spotReviewDir "$($task.id).diff"
                ($diff -join "`n") | Set-Content -LiteralPath $diffPath -Encoding utf8
                Write-Host "  -> queued for spot-review: $diffPath"
            } finally { Pop-Location }
        }
    } else {
        $maxAttempts = if ($task.maxAttempts) { $task.maxAttempts } else { 3 }
        if ($task.attempts.Count -ge $maxAttempts) {
            $task.status = "escalated"
            $handoff = @"
# Escalation: $($task.id)

## Titel
$($task.title)

## Beschreibung
$($task.description)

## Acceptance-Kriterien
$($task.acceptanceCriteria -join "`n")

## Letzter Fehler
Kommando: $($check.failedCommand)

$($check.output)

## Alle Attempts
$(($task.attempts | ForEach-Object { "- $($_.at): claimed=$($_.claimed) summary=$($_.summary)" }) -join "`n")

## Was Claude tun sollte
Das Problem selbst im Quellcode beheben ODER diese Aufgabe in tasks.json
vereinfachen/aufteilen und status wieder auf "pending" setzen, dann diese Datei loeschen.
"@
            Set-Content -LiteralPath (Join-Path $escalationDir "$($task.id).md") -Value $handoff -Encoding utf8
            Write-Host "  -> ESCALATED after $($task.attempts.Count) attempts, see escalation/$($task.id).md"
        } else {
            Write-Host "  -> FAILED acceptance check (attempt $($task.attempts.Count)/$maxAttempts), will retry next run"
        }
    }

    Save-Tasks -TasksObj $tasksObj
}

if (-not $processedAny) {
    Write-Host "No pending tasks."
}

# Second wave: turn FAILing ai-test-loop scenarios into new bugfix tasks.
# This is deliberately done here (trusted, Claude-authored code) rather than by
# tools/ai-test-loop/orchestrator.ps1 itself, so a locally-built script never needs
# write access to tasks.json.
$verdictsDir = Join-Path $RepoRoot "tools\ai-test-loop\test-results\verdicts"
if (Test-Path $verdictsDir) {
    $newBugfixCount = 0
    Get-ChildItem -Path $verdictsDir -Filter "*.json" | ForEach-Object {
        $scenarioId = $_.BaseName
        $verdict = Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json
        if ($verdict.verdict -ne "FAIL") { return }

        $baseId = "bugfix-$scenarioId"
        $existingOpen = $tasksObj.tasks | Where-Object { $_.id -like "$baseId*" -and $_.status -in @("pending", "escalated") }
        if ($existingOpen) { return }

        $suffix = ""
        $priorDone = ($tasksObj.tasks | Where-Object { $_.id -like "$baseId*" }).Count
        if ($priorDone -gt 0) { $suffix = "-r$($priorDone + 1)" }

        $rawPath = Join-Path $RepoRoot "tools\ai-test-loop\test-results\raw\$scenarioId.json"
        $evidenceExcerpt = ""
        if (Test-Path $rawPath) {
            $raw = Get-Content -Raw -LiteralPath $rawPath
            if ($raw.Length -gt 4000) { $raw = $raw.Substring(0, 4000) + "... (gekuerzt)" }
            $evidenceExcerpt = $raw
        }

        $newTask = [PSCustomObject]@{
            id                = "$baseId$suffix"
            title             = "Bugfix fuer Szenario $scenarioId"
            description       = "Das Test-Szenario '$scenarioId' ist FEHLGESCHLAGEN.`n`nJudge-Begruendung: $($verdict.reasoning)`n`nVermutete Ursache: $($verdict.suspectedRootCause)`n`nRohe Evidence (ScenarioResult):`n$evidenceExcerpt`n`nBehebe die zugrunde liegende Ursache im DocuClick-Quellcode (src/DocuClick/**). Verifiziere per 'dotnet build DocuClick.sln'; die eigentliche Szenario-Freigabe erfolgt im naechsten Testlauf durch den Judge, nicht durch dich."
            allowedPaths      = @("src/DocuClick/**")
            acceptanceCriteria = @("dotnet build DocuClick.sln")
            maxAttempts       = 3
            status            = "pending"
            alwaysSpotReview  = $true
            attempts          = @()
        }
        $tasksObj.tasks += $newTask
        $newBugfixCount++
        Write-Host "  -> queued new bugfix task: $($newTask.id)"
    }
    if ($newBugfixCount -gt 0) { Save-Tasks -TasksObj $tasksObj }
}

$open = ($tasksObj.tasks | Where-Object { $_.status -eq "pending" }).Count
$escalated = ($tasksObj.tasks | Where-Object { $_.status -eq "escalated" }).Count
$done = ($tasksObj.tasks | Where-Object { $_.status -eq "done" }).Count
Write-Host ""
Write-Host "Summary: $done done, $open pending, $escalated escalated (of $($tasksObj.tasks.Count) total)"

if ($escalated -gt 0) { exit 2 }
if ($open -gt 0) { exit 1 }
exit 0
