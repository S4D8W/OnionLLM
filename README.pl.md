# OnionLLM

🇬🇧 [English](README.md) · 🇵🇱 **Polski**

Lokalny monitor zużycia tokenów i kosztów narzędzi AI do programowania: Claude Code, Codex CLI, GitHub Copilot i kolejnych.
Dane pochodzą z logów, które te narzędzia zapisują na dysku. Nic nie jest wysyłane na zewnątrz. Jedyne wyjątki to opcjonalne pobranie cennika (LiteLLM) i odczyt limitów Claude z API Anthropic przy użyciu Twojego lokalnego logowania.

Funkcjonalnie wzorowane na [ccusage](https://github.com/ryoppippi/ccusage); różnice opisuje [PLAN_IMPLEMENTACJI.md](PLAN_IMPLEMENTACJI.md).

## Składniki

| Projekt | Opis |
| --- | --- |
| `OnionLLM.Core` | Cała logika: parsery źródeł, baza (ledger SQLite), cennik, koszty, raporty, bloki 5 h, eksport |
| `OnionLLM` | Aplikacja .NET MAUI z zakładkami: **Dziś · Raporty · Sesje · Bloki · Ustawienia** |
| `OnionLLM.Cli` | Narzędzie konsolowe `onionllm`: raporty, eksport JSON/CSV, statusline dla Claude Code |
| `OnionLLM.Core.Tests` | Testy xUnit (`dotnet test`) |

## Jak liczone są dane

- Każde wywołanie modelu staje się wpisem z tokenami **wejścia**, **wyjścia**, **zapisu do cache** i **odczytu z cache**. Te liczniki się nie nakładają: wejście nie zawiera cache.
- Wpisy są zapisywane w `%LOCALAPPDATA%\OnionLLM\ledger.db`. Synchronizacja jest przyrostowa: ponownie czytane są tylko pliki, które się zmieniły.
  Historia zostaje w bazie także wtedy, gdy narzędzie usunie stare logi. Claude Code domyślnie kasuje transkrypty po 30 dniach.
- Duplikaty są usuwane. W Claude Code klucz to `message.id` + `requestId`, w Codexie powtórzone `token_count` i kopie w forkach, w Copilocie ta sama sesja zapisana w dwóch bazach.
- Koszt: tryb `auto` (domyślny) bierze koszt zapisany przez narzędzie, a gdy go brak, liczy z cennika LiteLLM. Uwzględnia cache 5 min i 1 h, długi kontekst (>200k/272k) oraz tryby fast i flex.
  Modele bez ceny są oznaczone gwiazdką `*`. Własne ceny ustawisz w `pricingOverrides`.

## CLI

```powershell
dotnet run --project OnionLLM.Cli -- daily              # lub: onionllm daily
onionllm daily --last 7 --breakdown                     # 7 ostatnich dni z podziałem na modele
onionllm monthly --json > raport.json                   # eksport JSON
onionllm weekly --start-of-week sunday --csv            # eksport CSV
onionllm session --order desc                           # sesje, najnowsze pierwsze
onionllm session --id <ID> --json                       # jedna sesja
onionllm codex daily                                    # tylko jedno źródło
onionllm claude daily --instances --project-aliases "D--Repo-OnionLLM=OnionLLM"
onionllm blocks --active                                # aktywny blok 5 h: burn rate, prognoza, limit
onionllm blocks --recent --token-limit 500000
onionllm sources                                        # wykryte źródła i katalogi
onionllm daily --by-agent --json                        # podział każdego dnia na źródła (tablica agents)
onionllm daily --sections daily,weekly,monthly,session --json   # kilka sekcji z jednego odczytu
onionllm monthly --json --jq '.totals.totalCost'        # JSON przez jq (jq w PATH)
onionllm daily --debug --debug-samples 10               # rozbieżności kosztu zapisanego i wyliczonego (stderr)
onionllm sync --diagnostics                             # synchronizacja + diagnostyka parsowania
onionllm codex statusline                               # podsumowanie źródła: dziś / --since..--until / --id SESJA, --json
onionllm pricing info claude-sonnet-4-5                 # cena modelu
onionllm pricing update-snapshot                        # (dev) odświeża wbudowany snapshot cen
```

Najważniejsze opcje: `--since/--until` (`YYYY-MM-DD` lub `YYYYMMDD`), `--last N`, `--order asc|desc`, `--timezone`, `--mode auto|calculate|display`, `--offline`, `--breakdown`, `--by-agent`, `--sections`, `--no-cost`, `--compact`, `--color/--no-color`, `--config`; ustawienia źródeł: `--speed auto|standard|flex|fast` (Codex), `--include-claude-agent` (OpenCode), `--pi-agent-dir`, `--pi-path` (pi). Skróty jak w ccusage: `-w` (start tygodnia), `-n` (długość bloku), `-B` (wizualny burn rate), `-q` (jq), `-v` (wersja). Pełna lista: `onionllm help`.

`--debug` pokazuje rozbieżności między kosztem zapisanym przez narzędzie (np. `costUSD` Claude Code) a wyliczonym z cennika, jak w ccusage; diagnostyka parsowania logów jest pod `--diagnostics`. Ten sam raport rozbieżności jest w aplikacji: Ustawienia → „Rozbieżności kosztu”.

### Format JSON

`--json` (i eksport JSON w aplikacji) jest zgodny z ccusage (`docs/guide/json-output.md`):

- raport zbiorczy: tablica `daily`/`weekly`/`monthly` z wierszami `period`, `agent: "all"`, `metadata.agents`; sesje: tablica `session` z `agent` (źródło), `period` (ID sesji) i `metadata.lastActivity`;
- raport jednego źródła (`onionllm claude daily`): `date`/`week`/`month`, sesje w tablicy `sessions` (`sessionId`, `projectPath`, `firstActivity`, `lastActivity`);
- `--instances`: obiekt `projects` z wierszami każdego projektu;
- `modelBreakdowns` zawsze (`cost`, a gdy dotyczy: `storedCost`, `cacheCreation1hTokens`, `missingPricing`, `estimatedPricing`);
- `totals.unpricedModels` i `totals.estimatedModels` tylko, gdy niepuste.

Pola dodatkowe OnionLLM: `requests`, `reasoningOutputTokens`, `credits` w wierszach oraz `source`/`project` w sesjach.

## Statusline dla Claude Code

Zbuduj CLI (`dotnet publish OnionLLM.Cli -c Release -o C:\Tools\onionllm`) i dodaj do `~/.claude/settings.json`:

```json
{
  "statusLine": {
    "type": "command",
    "command": "C:\\Tools\\onionllm\\onionllm.exe statusline --visual-burn-rate emoji",
    "padding": 0
  }
}
```

Wynik:

```text
🤖 Opus 4.1 (high) | 💰 $1.23 sesja / $4.56 dziś / $2.10 blok (1h 20m do końca) | 🔥 $3.40/h ⚠️ | 🧠 85,000 (43%)
```

Opcje: `--cost-source auto|onion|cc|both`, `--visual-burn-rate off|emoji|text|emoji-text`, `--refresh-interval` (ważność zapamiętanego wyniku w sekundach), `--no-cache`, `--model-label-aliases "id=Etykieta,id2=Inna"` (np. długie ARN-y Bedrock), `--context-low-threshold`, `--context-medium-threshold`. Statusline domyślnie działa offline.

Wynik jest zapamiętywany osobno dla każdej sesji (`<katalog danych>\statusline\<sesja>.json`). Gdy inny proces właśnie przelicza linię, pokazywany jest ostatni wynik; po błędzie również ostatni wynik. Gdy Claude Code nie poda kosztu sesji, w trybach `cc` i `both` widać `N/A` zamiast `$0.00`.

## Konfiguracja

Plik `%APPDATA%\OnionLLM\config.json`. Aplikacja zapisuje go z zakładki Ustawienia, możesz też edytować go ręcznie. Plik `.onionllm.json` w bieżącym katalogu oraz `--config PLIK` nadpisują poszczególne pola.
Schemat do podpowiedzi w IDE: [onionllm.schema.json](onionllm.schema.json).

```jsonc
{
  "$schema": "./onionllm.schema.json",
  "timezone": "Europe/Warsaw",
  "costMode": "auto",
  "offline": false,
  "startOfWeek": "monday",
  "sessionLengthHours": 5,
  "tokenLimit": "max",
  "projectAliases": "D--Repo-OnionLLM=OnionLLM",
  "sources": {
    "claude": { "enabled": true, "paths": [] },
    "codex": { "enabled": true, "paths": ["D:\\archiwum\\.codex"] }
  },
  "pricingOverrides": {
    "my-local-model": { "inputCostPerToken": 0.000001, "outputCostPerToken": 0.000002 },
    "[codex] gpt-5": { "inputCostPerToken": 0.00000125, "outputCostPerToken": 0.00001 }
  }
}
```

Tryb przenośny: zmienna `ONIONLLM_DATA_DIR` przenosi bazę, cache i plik `config.json` do wskazanego katalogu.

Katalog danych źródła wybierany jest w kolejności: `sources.<id>.paths` z konfiguracji, potem zmienna środowiskowa (np. `CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `COPILOT_HOME`; kilka ścieżek oddzielasz `,` lub `;`), na końcu lokalizacje domyślne.

## Źródła

`onionllm sources` pokazuje, które źródła zostały wykryte. Obsługiwane (20): Claude Code, Codex CLI, GitHub Copilot (CLI i Visual Studio), Gemini CLI, Qwen Code, OpenCode, Kilo Code, Cline, Amp, Factory Droid, Codebuff, Goose, Kimi, pi-agent, OpenClaw, Hermes Agent, Antigravity, Grok Build, ZCode, DeepSeek Harness. Domyślne katalogi i zmienne środowiskowe opisuje [PLAN_IMPLEMENTACJI.md](PLAN_IMPLEMENTACJI.md#faza-9--dodatkowe-źródła-danych); `onionllm sources` pokazuje stan na Twojej maszynie.

## Licencja

[MIT](LICENSE)
