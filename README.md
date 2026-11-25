<div align="center">

# 🧅 OnionLLM

**Local token-usage and cost tracker for AI coding assistants: Claude Code, OpenAI Codex CLI, GitHub Copilot, Gemini CLI and 16 more.**

Desktop app · Windows widget · CLI · Claude Code statusline — all reading the logs your tools already write to disk.

![.NET 11](https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet&logoColor=white)
![.NET MAUI](https://img.shields.io/badge/UI-.NET%20MAUI-512BD4)
![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Android%20%7C%20iOS-lightgrey)
![Sources](https://img.shields.io/badge/supported%20tools-20-success)
![Privacy](https://img.shields.io/badge/data-stays%20local-brightgreen)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

🇬🇧 **English** · 🇵🇱 [Polski](README.pl.md)

</div>

---

## Why OnionLLM?

You use several AI coding agents, each with its own pricing, its own log format and its own idea of a "session". OnionLLM peels those layers apart and shows one answer: **how many tokens you used, what it cost, and how close you are to your limits** — per day, week, month, session, model, project and tool.

- 🔒 **Local-first.** Usage is parsed from files on your machine. Nothing is uploaded. The only network calls are an optional pricing refresh (LiteLLM / models.dev) and reading your Claude plan limits via your existing local Anthropic login.
- 🗃️ **History that survives log cleanup.** Every request lands in a local SQLite ledger with incremental sync, so your history stays even after Claude Code deletes transcripts older than 30 days.
- 💵 **Accurate costs.** Uses the cost recorded by the tool when available, otherwise computes it from LiteLLM pricing — including 5-minute and 1-hour prompt cache, long-context tiers (>200k / 272k) and fast / flex speed modes. Unpriced models are flagged, and you can override any price.
- 🧹 **No double counting.** Source-specific deduplication: `message.id` + `requestId` for Claude Code, replayed `token_count` events and forked sessions for Codex, the same session stored in two databases for Copilot.
- ⏱️ **5-hour billing blocks.** Active block with burn rate, end-of-block projection and token limit — the same model as Claude's rolling usage windows.
- 🔁 **ccusage-compatible.** Report semantics and `--json` output follow [ccusage](https://github.com/ryoppippi/ccusage), so existing scripts and dashboards keep working.

## Supported tools

OnionLLM auto-detects **20** AI coding assistants:

| | | | |
| --- | --- | --- | --- |
| Claude Code | OpenAI Codex CLI | GitHub Copilot (CLI & Visual Studio) | Gemini CLI |
| Qwen Code | OpenCode | Kilo Code | Cline |
| Amp | Factory Droid | Codebuff | Goose |
| Kimi | pi-agent | OpenClaw | Hermes Agent |
| Antigravity | Grok Build | ZCode | DeepSeek Harness |

Run `onionllm sources` to see which ones were found on your machine and where their data lives.

## What's inside

| Component | What it does |
| --- | --- |
| **OnionLLM** (app) | .NET MAUI desktop app with tabs: **Today · Reports · Sessions · Blocks · Settings**. Charts, per-model breakdowns, provider limits, JSON/CSV export, cost-mismatch report. |
| **OnionLLM.WidgetProvider** | Windows 11 Widget Board widget: today's cost and tokens, the active 5-hour block, active sources. |
| **OnionLLM.Cli** (`onionllm`) | Command-line reports, JSON/CSV export, `jq` filtering and a statusline for Claude Code. |
| **OnionLLM.Core** | The engine: source parsers, SQLite ledger, pricing catalog, cost calculation, reports, blocks, export. |
| **OnionLLM.Core.Tests** | xUnit test suite (360+ tests). |

> The app UI is currently in Polish; the CLI help, JSON output and configuration keys are in English.

## Getting started

**Requirements:** [.NET 11 SDK](https://dotnet.microsoft.com/download) and, for the desktop app, the .NET MAUI workload (`dotnet workload install maui`). The Widget Board widget requires Windows 11.

```powershell
git clone <this-repo-url>
cd OnionLLM

# Desktop app (Windows)
dotnet build OnionLLM -f net11.0-windows10.0.19041.0
# or open OnionLLM.slnx in Visual Studio and run the OnionLLM project

# CLI
dotnet run --project OnionLLM.Cli -- daily

# Tests
dotnet test
```

To install the CLI as a standalone executable:

```powershell
dotnet publish OnionLLM.Cli -c Release -o C:\Tools\onionllm
```

## CLI usage

```powershell
onionllm daily                                   # usage and cost per day
onionllm daily --last 7 --breakdown              # last 7 days, split by model
onionllm weekly --start-of-week sunday --csv     # CSV export
onionllm monthly --json > report.json            # ccusage-compatible JSON
onionllm monthly --json --jq '.totals.totalCost' # filter with jq
onionllm session --order desc                    # sessions, newest first
onionllm codex daily                             # a single source only
onionllm daily --by-agent --json                 # per-day split by tool
onionllm blocks --active                         # current 5h block: burn rate, projection, limit
onionllm sources                                 # detected tools and data directories
onionllm pricing info claude-sonnet-4-5          # model pricing lookup
onionllm daily --debug                           # recorded vs. calculated cost mismatches
```

Common options: `--since/--until`, `--last N`, `--timezone`, `--mode auto|calculate|display`, `--offline`, `--breakdown`, `--by-agent`, `--compact`, `--config`. Full list: `onionllm help`.

## Claude Code statusline

Show live session cost, daily cost, block burn rate and context usage directly in Claude Code. Add to `~/.claude/settings.json`:

```json
{
  "statusLine": {
    "type": "command",
    "command": "C:\\Tools\\onionllm\\onionllm.exe statusline --visual-burn-rate emoji",
    "padding": 0
  }
}
```

```text
🤖 Opus 4.1 (high) | 💰 $1.23 session / $4.56 today / $2.10 block (1h 20m left) | 🔥 $3.40/h ⚠️ | 🧠 85,000 (43%)
```

The statusline works offline and caches its result per session, so it stays fast even when several Claude Code windows are open.

## Configuration

Settings live in `%APPDATA%\OnionLLM\config.json` (editable from the app's Settings tab). A `.onionllm.json` in the current directory or `--config FILE` overrides individual fields. IDE autocompletion: [onionllm.schema.json](onionllm.schema.json).

```jsonc
{
  "$schema": "./onionllm.schema.json",
  "timezone": "Europe/Warsaw",
  "costMode": "auto",
  "startOfWeek": "monday",
  "sources": {
    "codex": { "enabled": true, "paths": ["D:\\archive\\.codex"] }
  },
  "pricingOverrides": {
    "my-local-model": { "inputCostPerToken": 0.000001, "outputCostPerToken": 0.000002 }
  }
}
```

Data directories are resolved from config, then environment variables (`CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `COPILOT_HOME`, …), then default locations. Set `ONIONLLM_DATA_DIR` for a portable setup.

More detail — JSON format, deduplication rules, every option — is in the [Polish README](README.pl.md) and [PLAN_IMPLEMENTACJI.md](PLAN_IMPLEMENTACJI.md).

## Acknowledgements

Report semantics and the JSON format are modelled on [ccusage](https://github.com/ryoppippi/ccusage) by ryoppippi. Model pricing comes from [LiteLLM](https://github.com/BerriAI/litellm) and [models.dev](https://models.dev).

## License

[MIT](LICENSE)
