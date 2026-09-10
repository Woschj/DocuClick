# Sandbox for the local agent loop's tool calls.
# Every read/write/command from the local LM Studio model passes through here.
# This is the only thing standing between an "uncensored" local model and the
# repo/filesystem, so validation here must fail closed, not open.

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

# Paths the agent loop may never touch, regardless of a task's allowedPaths,
# to prevent self-modification of the sandbox/loop and history tampering.
$script:HardDenyPrefixes = @(
    (Join-Path $RepoRoot ".git"),
    (Join-Path $RepoRoot "tools\local-agent-loop")
)

# Command prefixes the model may invoke via run_command. Exact prefix match only;
# anything with shell metacharacters is rejected before this list is even checked.
$script:CommandAllowlist = @(
    "dotnet build",
    "dotnet publish",
    "dotnet run --project tools/ai-test-loop/DocuClick.TestHarness",
    "powershell tools/ai-test-loop/orchestrator.ps1",
    "powershell tools/ai-test-loop/judge/judge.ps1"
)

function Resolve-SandboxPath {
    param([string]$RelativePath)

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        throw "Empty path"
    }
    if ($RelativePath -match '\.\.') {
        throw "Path traversal ('..') is not allowed: $RelativePath"
    }
    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Absolute paths are not allowed: $RelativePath"
    }

    $full = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $RelativePath))

    if (-not $full.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the repository: $RelativePath"
    }
    foreach ($deny in $script:HardDenyPrefixes) {
        if ($full.StartsWith($deny, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Path is in a hard-denied area (sandbox/history): $RelativePath"
        }
    }
    return $full
}

function Test-PathAllowedForTask {
    param([string]$RelativePath, [string[]]$AllowedPaths)

    $full = Resolve-SandboxPath -RelativePath $RelativePath
    $normalizedRel = $full.Substring($RepoRoot.Length).TrimStart('\', '/').Replace('\', '/')

    foreach ($pattern in $AllowedPaths) {
        $p = $pattern.TrimEnd('/').Replace('\', '/')
        if ($p.EndsWith('/**')) {
            $prefix = $p.Substring(0, $p.Length - 3)
            if ($normalizedRel -eq $prefix -or $normalizedRel.StartsWith("$prefix/")) { return $full }
        } elseif ($normalizedRel -eq $p) {
            return $full
        }
    }
    throw "Path '$RelativePath' is not within this task's allowedPaths ($($AllowedPaths -join ', '))"
}

function Invoke-SandboxReadFile {
    # Read access is repo-wide (minus the hard-denied areas) rather than limited to
    # the task's allowedPaths: tasks routinely need to read adjacent source for
    # context (e.g. AppConfig.cs) without being allowed to write there. Only
    # write_file/run_command enforce the narrow per-task allowlist.
    #
    # Content is capped at $MaxChars: the local model runs with a hard 32K-token
    # context ceiling (VRAM-limited), and agent_loop.ps1's own history-trimming can
    # only work with rounds that are already individually small - a single
    # multi-thousand-line file read back in full could blow the whole budget in one
    # tool call before trimming ever gets a chance to help.
    param([string]$Path, [string[]]$AllowedPaths, [int]$MaxChars = 12000)
    $full = Resolve-SandboxPath -RelativePath $Path
    if (-not (Test-Path $full -PathType Leaf)) {
        return @{ ok = $false; error = "File does not exist: $Path" }
    }
    $content = Get-Content -Raw -LiteralPath $full -ErrorAction Stop
    if ($content.Length -gt $MaxChars) {
        $shown = $content.Substring(0, $MaxChars)
        $content = "$shown`n`n[... truncated: file is $($content.Length) chars, showing first $MaxChars. Re-read with a narrower need, or ask to see a specific section, if you need more.]"
    }
    return @{ ok = $true; content = $content }
}

function Invoke-SandboxWriteFile {
    param([string]$Path, [string]$Content, [string[]]$AllowedPaths)
    $full = Test-PathAllowedForTask -RelativePath $Path -AllowedPaths $AllowedPaths
    $dir = Split-Path -Parent $full
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    Set-Content -LiteralPath $full -Value $Content -NoNewline -Encoding utf8
    return @{ ok = $true }
}

function Invoke-SandboxListDir {
    # Same read-wide/write-narrow split as Invoke-SandboxReadFile.
    param([string]$Path, [string[]]$AllowedPaths)
    $full = Resolve-SandboxPath -RelativePath $Path
    if (-not (Test-Path $full -PathType Container)) {
        return @{ ok = $false; error = "Directory does not exist: $Path" }
    }
    $entries = Get-ChildItem -LiteralPath $full | ForEach-Object {
        if ($_.PSIsContainer) { "$($_.Name)/" } else { $_.Name }
    }
    return @{ ok = $true; entries = $entries }
}

function Invoke-SandboxRunCommand {
    # First-time builds (NuGet restore etc.) legitimately take minutes, so this
    # can't be a tight cap - but it must still be bounded, since a hung/looping
    # command would otherwise block the agent loop's own MaxMinutes check, which
    # is only re-evaluated between tool calls, not during one.
    param([string]$Command, [int]$TimeoutSeconds = 600, [int]$MaxOutputChars = 8000)

    if ($Command -match '[;&|`]' -or $Command -match '\$\(' -or $Command -match "`n") {
        return @{ ok = $false; error = "Command rejected: shell metacharacters/chaining are not allowed" }
    }

    $allowed = $false
    foreach ($prefix in $script:CommandAllowlist) {
        if ($Command.StartsWith($prefix, [System.StringComparison]::Ordinal)) { $allowed = $true; break }
    }
    if (-not $allowed) {
        return @{ ok = $false; error = "Command rejected: not on the allowlist. Allowed prefixes: $($script:CommandAllowlist -join ' | ')" }
    }

    $outFile = [System.IO.Path]::GetTempFileName()
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = "cmd.exe"
        $psi.Arguments = "/c `"$Command`" > `"$outFile`" 2>&1"
        $psi.WorkingDirectory = $RepoRoot
        $psi.UseShellExecute = $false
        $proc = [System.Diagnostics.Process]::Start($psi)

        if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
            try { $proc.Kill($true) } catch {}
            $partial = if (Test-Path $outFile) { Get-Content -Raw -LiteralPath $outFile -ErrorAction SilentlyContinue } else { "" }
            return @{ ok = $false; error = "Command timed out after $TimeoutSeconds s and was killed."; output = $partial }
        }

        $output = if (Test-Path $outFile) { Get-Content -Raw -LiteralPath $outFile -ErrorAction SilentlyContinue } else { "" }
        if ($output -and $output.Length -gt $MaxOutputChars) {
            # Keep the tail, not the head: a build/restore failure's actual error
            # is almost always at the end of the log, not buried under NuGet's
            # verbose restore chatter from the start.
            $omitted = $output.Length - $MaxOutputChars
            $output = "[... $omitted earlier chars omitted ...]`n" + $output.Substring($output.Length - $MaxOutputChars)
        }
        return @{ ok = ($proc.ExitCode -eq 0); exitCode = $proc.ExitCode; output = $output }
    } finally {
        Remove-Item -LiteralPath $outFile -ErrorAction SilentlyContinue
    }
}
