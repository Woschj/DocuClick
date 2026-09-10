<#
.SYNOPSIS
  Runs ONE task from tasks.json through a tool-calling agent loop against a local
  LM Studio model. The model reads/writes files and runs build/test commands itself
  via tools_sandbox.ps1 until it calls finish(), or until the attempt caps out.

  This script never marks a task "done" — it only reports what the model claims.
  orchestrator.ps1 independently re-verifies acceptanceCriteria before trusting it.

.PARAMETER TaskId
  Id of the task in tasks.json to run.

.PARAMETER TasksFile
  Path to tasks.json (default: tools/local-agent-loop/tasks.json).

.PARAMETER LMStudioUrl
  Base URL of the LM Studio OpenAI-compatible server (default: http://localhost:1234/v1).

.PARAMETER Model
  Model id as reported by LM Studio's /v1/models (default: env DOCUCLICK_CODER_MODEL or
  "qwen3.8-27b").

.PARAMETER MaxToolCalls
  Hard cap on tool calls for this attempt (default 20).

.PARAMETER MaxMinutes
  Hard wall-clock cap for this attempt (default 15).

.PARAMETER MaxContextChars
  Approximate character budget for the message history sent to the model.
  Default -1 = auto-detect from the loaded model's actual context length (see
  Get-ModelContextBudget) instead of guessing a single fixed number - "many
  models" loaded at very different context sizes (a small quantized model
  might only have 4K/8K loaded, not everyone's 32K) made one hardcoded default
  wrong for most of them. Pass an explicit value to override auto-detection.

.PARAMETER MaxCompletionTokens
  max_tokens sent in the chat-completion request (default 1024, plenty for a
  single JSON tool call). Left unset entirely, some OpenAI-compatible servers
  default to reserving a large/unbounded completion budget on top of the
  prompt - which can trip "context length too small" even when the prompt
  itself would have fit comfortably.
#>
param(
    [Parameter(Mandatory = $true)][string]$TaskId,
    [string]$TasksFile = "",
    [string]$LMStudioUrl = "http://localhost:1234/v1",
    [string]$Model = $(if ($env:DOCUCLICK_CODER_MODEL) { $env:DOCUCLICK_CODER_MODEL } else { "qwen/qwen3.8-27b" }),
    [int]$MaxToolCalls = 20,
    [int]$MaxMinutes = 15,
    [int]$MaxContextChars = -1,
    [int]$MaxCompletionTokens = 1024
)

# NOTE: $PSScriptRoot is unreliable as a param-default expression on scripts that
# also declare a Mandatory parameter (Windows PowerShell 5.1 quirk: defaults are
# bound before $PSScriptRoot is populated in that case) - resolved here instead.
if ([string]::IsNullOrEmpty($TasksFile)) { $TasksFile = Join-Path $PSScriptRoot "tasks.json" }

. (Join-Path $PSScriptRoot "tools_sandbox.ps1")

$ErrorActionPreference = "Stop"

# Chars/token for a conservative *estimate* only (no real tokenizer available
# here) - deliberately on the low (pessimistic) side: code/JSON tends to
# tokenize worse than prose (more punctuation, camelCase splits, etc.), and
# overestimating token count is the safe direction - it just trims/caps a bit
# earlier than strictly necessary, instead of still risking an overflow.
$script:CharsPerToken = 3

function Get-ModelContextBudget {
    # LM Studio's OpenAI-compatible /v1/models doesn't report context length at
    # all (that's not part of the OpenAI schema) - LM Studio's own native REST
    # API (/api/v0/models) does, for whichever models are actually loaded right
    # now. Falls back to a small, safe budget if that call fails for any reason
    # (older LM Studio version, model id mismatch, server not reachable yet) -
    # failing toward "too conservative" here is far better than failing toward
    # "still overflows a small model's context".
    param([string]$LMStudioUrl, [string]$Model, [int]$FallbackTokens = 3500)

    $native = ($LMStudioUrl -replace '/v1/?$', '') + "/api/v0/models"
    try {
        $info = Invoke-RestMethod -Uri $native -Method Get -TimeoutSec 5
        $match = $info.data | Where-Object { $_.id -eq $Model } | Select-Object -First 1
        $ctxTokens = $null
        if ($match) {
            if ($match.loaded_context_length) { $ctxTokens = [int]$match.loaded_context_length }
            elseif ($match.max_context_length) { $ctxTokens = [int]$match.max_context_length }
        }
        if ($ctxTokens) {
            Write-Host "[agent_loop] detected loaded context for '$Model': $ctxTokens tokens"
            return $ctxTokens
        }
        Write-Host "[agent_loop] model '$Model' not found in $native response; using fallback context budget"
    } catch {
        Write-Host "[agent_loop] could not query $native ($($_.Exception.Message)); using fallback context budget"
    }
    return $FallbackTokens
}

$contextTokens = Get-ModelContextBudget -LMStudioUrl $LMStudioUrl -Model $Model
# Reserve room for the completion itself plus a flat safety margin (tool-def
# JSON schemas, chat-template overhead, and the pessimistic chars/token ratio
# above are all approximations, not exact accounting).
$safetyMarginTokens = 500
$promptBudgetTokens = [Math]::Max(512, $contextTokens - $MaxCompletionTokens - $safetyMarginTokens)
if ($MaxContextChars -lt 0) { $MaxContextChars = $promptBudgetTokens * $script:CharsPerToken }
# No single tool result should be able to consume the *whole* prompt budget by
# itself - split it so a couple of rounds can realistically coexist rather
# than one big read already forcing every earlier round out.
$ReadFileMaxChars = [Math]::Max(1000, [int]($MaxContextChars * 0.3))
$RunCommandMaxChars = [Math]::Max(1000, [int]($MaxContextChars * 0.2))
Write-Host "[agent_loop] context budget: $MaxContextChars chars total, $ReadFileMaxChars per read_file, $RunCommandMaxChars per run_command"

$tasks = Get-Content -Raw -LiteralPath $TasksFile | ConvertFrom-Json
$task = $tasks.tasks | Where-Object { $_.id -eq $TaskId }
if (-not $task) { throw "Task '$TaskId' not found in $TasksFile" }

$allowedPaths = @($task.allowedPaths)

$toolDefs = @(
    @{ type = "function"; function = @{
        name = "read_file"; description = "Read a text file's full content."
        parameters = @{ type = "object"; properties = @{ path = @{ type = "string"; description = "Repo-relative path" } }; required = @("path") }
    } },
    @{ type = "function"; function = @{
        name = "write_file"; description = "Overwrite (or create) a text file with the given content."
        parameters = @{ type = "object"; properties = @{ path = @{ type = "string" }; content = @{ type = "string" } }; required = @("path", "content") }
    } },
    @{ type = "function"; function = @{
        name = "list_dir"; description = "List entries of a directory (repo-relative path)."
        parameters = @{ type = "object"; properties = @{ path = @{ type = "string" } }; required = @("path") }
    } },
    @{ type = "function"; function = @{
        name = "run_command"; description = "Run an allow-listed shell command from the repo root (e.g. a dotnet build)."
        parameters = @{ type = "object"; properties = @{ command = @{ type = "string" } }; required = @("command") }
    } },
    @{ type = "function"; function = @{
        name = "finish"; description = "Call this when you believe the task is complete, or when you are stuck and cannot proceed."
        parameters = @{ type = "object"; properties = @{ success = @{ type = "boolean" }; summary = @{ type = "string" } }; required = @("success", "summary") }
    } }
)

$systemPrompt = @"
Du bist ein autonomer C#/.NET-Entwickler-Agent fuer das Repository DocuClick (WPF, net8.0-windows).
Du bearbeitest GENAU EINE Aufgabe. Du hast Zugriff auf: read_file, write_file, list_dir, run_command, finish.

Regeln:
- Du darfst beliebige Dateien im Repository LESEN (fuer Kontext, z.B. verwandte Services).
- Du darfst NUR Dateien innerhalb dieser Pfade SCHREIBEN/ERSTELLEN: $($allowedPaths -join ', ')
- run_command akzeptiert ausschliesslich vorab freigegebene Kommandos (z.B. "dotnet build ..."). Andere Kommandos werden abgelehnt.
- Nutze run_command, um deine Aenderung selbst zu verifizieren (z.B. dotnet build), BEVOR du finish aufrufst.
- Rufe finish(success=true, summary=...) NUR auf, wenn die acceptanceCriteria der Aufgabe nachweislich erfuellt sind.
- Rufe finish(success=false, summary=...) auf, wenn du nach mehreren Versuchen nicht weiterkommst - beschreibe genau, woran es scheitert.
- Schreibe idiomatischen, minimalen C#/PowerShell-Code passend zum bestehenden Stil. Keine unnoetigen Refactorings.
- Antworte ausschliesslich über Tool-Calls, bis du finish aufrufst.

Acceptance-Kriterien fuer diese Aufgabe (werden nach deinem finish() unabhaengig erneut geprueft):
$($task.acceptanceCriteria -join "`n")
"@

$userPrompt = @"
Aufgabe: $($task.title)

$($task.description)
"@

# Keeps the request under the local model's context ceiling. The system+user
# messages are always kept in full. Everything after that is grouped into
# "rounds" - one assistant message (with its tool_calls) plus the tool-result
# messages answering it - and the OLDEST rounds are dropped first once the
# approximate size exceeds $MaxChars. Dropping (not summarizing) is safe here:
# every read_file/list_dir/run_command result just reflects repo/build state,
# which the model can always re-query if it still needs it - there's no
# irrecoverable information loss, just a re-read cost. The most recent round
# is always kept even if it alone exceeds the budget, so this can never wedge
# the loop into sending zero rounds at all.
function Get-TrimmedMessages {
    param([array]$Messages, [int]$MaxChars)

    if ($Messages.Count -le 2) { return $Messages }

    $head = $Messages[0..1]
    $rounds = [System.Collections.Generic.List[object]]::new()
    $current = $null
    for ($i = 2; $i -lt $Messages.Count; $i++) {
        $m = $Messages[$i]
        if ($m.role -eq "assistant") {
            if ($null -ne $current) { $rounds.Add($current) }
            $current = [System.Collections.Generic.List[object]]::new()
        }
        if ($null -eq $current) { $current = [System.Collections.Generic.List[object]]::new() }
        $current.Add($m)
    }
    if ($null -ne $current) { $rounds.Add($current) }

    function Get-ApproxSize($msgs) {
        $sum = 0
        foreach ($m in $msgs) {
            if ($m.content) { $sum += ([string]$m.content).Length }
            if ($m.tool_calls) { $sum += ($m.tool_calls | ConvertTo-Json -Depth 10 -Compress).Length }
        }
        return $sum
    }

    $size = Get-ApproxSize $head
    $keep = [System.Collections.Generic.List[object]]::new()
    # Walk newest-first so it's always the most recent rounds that get kept.
    for ($i = $rounds.Count - 1; $i -ge 0; $i--) {
        $roundSize = Get-ApproxSize $rounds[$i]
        if ($keep.Count -gt 0 -and ($size + $roundSize) -gt $MaxChars) { break }
        $keep.Insert(0, $rounds[$i])
        $size += $roundSize
    }

    $droppedCount = $rounds.Count - $keep.Count
    $result = [System.Collections.Generic.List[object]]::new()
    $result.AddRange($head)
    if ($droppedCount -gt 0) {
        Write-Host "[agent_loop]   context trim: dropped $droppedCount oldest round(s), kept ~$size chars"
        $result.Add(@{ role = "user"; content = "[$droppedCount aeltere Tool-Runde(n) wurden aus dem Kontext entfernt, um das Token-Budget einzuhalten. Lies betroffene Dateien bei Bedarf erneut.]" })
    }
    foreach ($round in $keep) { $result.AddRange($round) }
    return $result.ToArray()
}

$messages = @(
    @{ role = "system"; content = $systemPrompt },
    @{ role = "user"; content = $userPrompt }
)

$transcript = [System.Collections.Generic.List[object]]::new()
$transcript.Add(@{ role = "system"; content = $systemPrompt })
$transcript.Add(@{ role = "user"; content = $userPrompt })

$start = Get-Date
$toolCallCount = 0
$result = @{ success = $false; summary = "Attempt did not converge (max tool calls or time exceeded)."; toolCallCount = 0 }

while ($true) {
    if ($toolCallCount -ge $MaxToolCalls) {
        $result.summary = "Stopped: reached MaxToolCalls ($MaxToolCalls)."
        break
    }
    if (((Get-Date) - $start).TotalMinutes -ge $MaxMinutes) {
        $result.summary = "Stopped: reached MaxMinutes ($MaxMinutes)."
        break
    }

    $trimmedMessages = Get-TrimmedMessages -Messages $messages -MaxChars $MaxContextChars
    $body = @{
        model       = $Model
        messages    = $trimmedMessages
        tools       = $toolDefs
        tool_choice = "auto"
        temperature = 0.2
        max_tokens  = $MaxCompletionTokens
    } | ConvertTo-Json -Depth 20

    Write-Host "[agent_loop] round $($toolCallCount)/$MaxToolCalls -> calling $Model ..."
    $callSw = [System.Diagnostics.Stopwatch]::StartNew()
    $response = Invoke-RestMethod -Uri "$LMStudioUrl/chat/completions" -Method Post -Body $body -ContentType "application/json"
    $callSw.Stop()
    Write-Host "[agent_loop] model responded after $([math]::Round($callSw.Elapsed.TotalSeconds, 1))s"
    $choice = $response.choices[0].message

    $messages += @{ role = "assistant"; content = $choice.content; tool_calls = $choice.tool_calls }
    $transcript.Add(@{ role = "assistant"; content = $choice.content; tool_calls = $choice.tool_calls })

    if (-not $choice.tool_calls -or $choice.tool_calls.Count -eq 0) {
        # Model responded with plain text instead of a tool call - nudge it back on track.
        $messages += @{ role = "user"; content = "Bitte antworte ausschliesslich per Tool-Call (read_file/write_file/list_dir/run_command/finish)." }
        continue
    }

    $finished = $false
    foreach ($call in $choice.tool_calls) {
        $toolCallCount++
        $fnName = $call.function.name
        $args = $null
        try { $args = $call.function.arguments | ConvertFrom-Json } catch { $args = [PSCustomObject]@{} }
        if ($null -eq $args) { $args = [PSCustomObject]@{} }

        Write-Host "[agent_loop]   tool call: $fnName($($call.function.arguments))"
        $toolSw = [System.Diagnostics.Stopwatch]::StartNew()
        $toolResult = switch ($fnName) {
            "read_file"    { Invoke-SandboxReadFile -Path $args.path -AllowedPaths $allowedPaths -MaxChars $ReadFileMaxChars }
            "write_file"   { Invoke-SandboxWriteFile -Path $args.path -Content $args.content -AllowedPaths $allowedPaths }
            "list_dir"     { Invoke-SandboxListDir -Path $args.path -AllowedPaths $allowedPaths }
            "run_command"  { Invoke-SandboxRunCommand -Command $args.command -MaxOutputChars $RunCommandMaxChars }
            "finish"       {
                $finished = $true
                $result = @{ success = [bool]$args.success; summary = [string]$args.summary; toolCallCount = $toolCallCount }
                @{ ok = $true }
            }
            default        { @{ ok = $false; error = "Unknown tool: $fnName" } }
        }
        $toolSw.Stop()
        Write-Host "[agent_loop]   -> ok=$($toolResult.ok) ($([math]::Round($toolSw.Elapsed.TotalSeconds, 1))s)"

        $toolResultJson = $toolResult | ConvertTo-Json -Depth 10 -Compress
        $messages += @{ role = "tool"; tool_call_id = $call.id; content = $toolResultJson }
        $transcript.Add(@{ role = "tool"; name = $fnName; arguments = $args; result = $toolResult })
    }

    if ($finished) { break }
}

$logsDir = Join-Path $PSScriptRoot "logs"
if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Force -Path $logsDir | Out-Null }
$attemptFiles = Get-ChildItem -Path $logsDir -Filter "$TaskId-attempt*.json" -ErrorAction SilentlyContinue
$attemptNum = 1
if ($attemptFiles) {
    $attemptNum = ($attemptFiles | ForEach-Object {
        if ($_.Name -match "attempt(\d+)\.json$") { [int]$Matches[1] } else { 0 }
    } | Measure-Object -Maximum).Maximum + 1
}
$logPath = Join-Path $logsDir "$TaskId-attempt$attemptNum.json"
@{ taskId = $TaskId; attempt = $attemptNum; startedAt = $start; finishedAt = (Get-Date); result = $result; transcript = $transcript } |
    ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $logPath -Encoding utf8

$result | ConvertTo-Json -Compress
