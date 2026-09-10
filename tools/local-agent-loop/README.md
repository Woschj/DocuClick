# Lokaler Agent-Loop

Lässt ein lokales LM-Studio-Modell die DocuClick-Testinfrastruktur (`tools/ai-test-loop/`)
selbst implementieren und gefundene Bugs selbst beheben — Claude greift nur bei
wiederholtem Scheitern oder bei Stichproben-Reviews ein. Details/Architektur: siehe
Plan-Datei bzw. `.claude-loop-prompt.md`.

## Voraussetzungen

1. **LM Studio** läuft lokal mit aktiviertem "Local Server" (Standard-Port `1234`),
   erreichbar unter `http://localhost:1234/v1`.
2. Ein Modell mit **Tool-Use/Function-Calling** ist geladen, z. B. `Qwen3.8 27B`
   (Default). Anderes Modell: Umgebungsvariable `DOCUCLICK_CODER_MODEL` setzen
   (Modell-Id exakt wie unter LM Studios `GET /v1/models`).
3. Für den Judge (`tools/ai-test-loop/judge/judge.ps1`, entsteht erst in Task 12):
   `DOCUCLICK_JUDGE_MODEL`, Default `mistral-small-3.2-24b-instruct-2506`.
4. .NET 8 SDK installiert (`dotnet build DocuClick.sln` muss lokal funktionieren).

## Manuell einen Durchlauf starten

```powershell
powershell -NoProfile -File tools\local-agent-loop\orchestrator.ps1
```

Exit-Codes: `0` = alle Tasks fertig, `1` = noch offene Tasks (nächster Durchlauf
arbeitet weiter), `2` = mind. eine Eskalation wartet unter `escalation/*.md`.

## Einzelne Task erneut/gezielt laufen lassen (Debugging)

```powershell
powershell -NoProfile -File tools\local-agent-loop\agent_loop.ps1 -TaskId task-01-config-datadir-override
```

## Unbeaufsichtigt laufen lassen

```
/loop 10m Folge tools/local-agent-loop/.claude-loop-prompt.md
```

## Sicherheits-Leitplanken (siehe `tools_sandbox.ps1`)

- Lesen: repo-weit erlaubt (für Kontext), außer `.git/` und `tools/local-agent-loop/`
  selbst (kein Selbstzugriff auf die eigene Sandbox/Historie).
- Schreiben: nur innerhalb der `allowedPaths` der jeweiligen Task in `tasks.json`.
- Befehle (`run_command`): nur exakte, fest hinterlegte Prefixes (`dotnet build`,
  `dotnet publish`, `dotnet run --project tools/ai-test-loop/DocuClick.TestHarness`,
  `powershell tools/ai-test-loop/orchestrator.ps1`, `powershell
  tools/ai-test-loop/judge/judge.ps1`). Shell-Verkettung (`;`, `&`, `|`, `` ` ``,
  `$(...)`) wird abgelehnt.
- Kein Commit-Tool — Commits bleiben ein manueller/Claude-geprüfter Schritt.
- Der Orchestrator vertraut nie der Selbstauskunft des Modells (`finish(success)`),
  sondern prüft `acceptanceCriteria` jeder Task unabhängig selbst nach.
