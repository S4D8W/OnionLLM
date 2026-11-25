# OnionLLM — plan implementacji funkcji z ccusage

> Dokument roboczy. Opisuje, co i jak zostanie zaimplementowane, oraz śledzi postęp.
> Statusy: `[ ]` do zrobienia · `[~]` w toku · `[x]` gotowe · `[-]` świadomie pominięte (z uzasadnieniem).
> Referencja: ccusage (`C:\Users\rs0097\Downloads\ccusage-main\ccusage-main`, Rust, `rust/adapters/*`, `rust/crates/*`, `docs/guide/*`).
>
> **Zasada nadrzędna:** OnionLLM to aplikacja z interfejsem (MAUI + widżet Windows), a nie narzędzie CLI. Każda funkcja przeniesiona z ccusage musi być dostępna w UI: ustawienie w zakładce Ustawienia, filtr lub kolumna w Raportach i Sesjach, znacznik w widoku albo element widżetu. Logika trafia do `OnionLLM.Core`, a `OnionLLM.Cli` (raporty, eksport, statusline dla Claude Code) jest dodatkiem. Sama opcja CLI nie oznacza, że funkcja jest gotowa.

---

## Spis treści

1. [Stan wyjściowy](#1-stan-wyjściowy)
2. [Architektura docelowa](#2-architektura-docelowa)
3. [Faza 0 — infrastruktura](#faza-0--infrastruktura)
4. [Faza 1 — model danych i poprawność liczenia](#faza-1--model-danych-i-poprawność-liczenia)
5. [Faza 2 — cennik i koszty](#faza-2--cennik-i-koszty)
6. [Faza 3 — agregacje i raporty](#faza-3--agregacje-i-raporty)
7. [Faza 4 — bloki 5-godzinne](#faza-4--bloki-5-godzinne)
8. [Faza 5 — konfiguracja](#faza-5--konfiguracja)
9. [Faza 6 — CLI, eksport, statusline](#faza-6--cli-eksport-statusline)
10. [Faza 7 — GUI (MAUI)](#faza-7--gui-maui)
11. [Faza 8 — widżet Windows](#faza-8--widżet-windows)
12. [Faza 9 — dodatkowe źródła danych](#faza-9--dodatkowe-źródła-danych)
13. [Faza 10 — opcjonalne / dokumentacja](#faza-10--opcjonalne--dokumentacja)
14. [Faza 11 — luki względem ccusage (audyt 2026-11-25)](#faza-11--luki-względem-ccusage-audyt-2026-11-25)
15. [Dziennik postępu](#dziennik-postępu)

---

## 1. Stan wyjściowy

| Obszar | Stan przed zmianami |
|---|---|
| Projekty | `OnionLLM` (MAUI, net11.0, Windows/Android/iOS/Mac), `OnionLLM.WidgetProvider` (COM, Widget Board) — współdzielą kod przez `<Compile Include=... Link=...>` |
| Źródła | Claude Code (`~/.claude/projects`), Codex (`~/.codex/sessions`), Copilot (`session-store.db` VS / `~/.copilot`) |
| Dane | Tylko dzisiejsze tokeny wejścia/wyjścia, liczba sesji aktywnych/zakończonych |
| Historia | Zrzuty „dzisiaj” zapisywane przy odświeżeniu (`DailyUsage`, `HourlyUsage`) → luki, utrwalanie błędów |
| Limity | 5h / 7d live z `api.anthropic.com/api/oauth/usage` (tego ccusage nie ma — zachowujemy) |
| Koszty | brak |
| Testy | brak |
| Git | katalog nie jest repozytorium |

**Znane błędy liczenia** (naprawiane w Fazie 1):

- B1 Claude — brak deduplikacji `message.id` + `requestId` → wielokrotne liczenie.
- B2 Claude — pominięte `cache_creation_input_tokens`, `cache_read_input_tokens`.
- B3 Codex — ostatni `total_token_usage` z pliku zmienionego dziś = cała sesja (także wczorajsza) liczona jako dzisiejsza.
- B4 Copilot — cache doliczany do wejścia, w Claude nie → niespójność.
- B5 Historia 7d/30d — tylko gdy aplikacja była uruchomiona danego dnia.
- B6 Ścieżki na sztywno — brak `CLAUDE_CONFIG_DIR`, `~/.config/claude`, `CODEX_HOME`, `COPILOT_HOME`.

---

## 2. Architektura docelowa

```
OnionLLM.slnx
├── OnionLLM.Core/                 (net11.0 class library — cała logika, bez UI)
│   ├── Models/                    UsageEntry, TokenCounts, ModelPricing, raporty, bloki
│   ├── Sources/                   IUsageSource + implementacje (Claude, Codex, Copilot, …)
│   ├── Storage/                   UsageLedger (SQLite: pliki + wpisy), migracje
│   ├── Pricing/                   PricingCatalog (wbudowany snapshot + online + override), CostCalculator
│   ├── Reports/                   ReportBuilder (daily/weekly/monthly/session/project), BlockBuilder
│   ├── Config/                    OnionConfig, ConfigLoader (plik + env), JSON schema
│   ├── Limits/                    ProviderLimitService (przeniesiony)
│   ├── Export/                    JsonExporter, CsvExporter
│   └── UsageEngine.cs             fasada: Sync() → Query(...) używana przez GUI, widżet i CLI
├── OnionLLM.Core.Tests/           (xUnit, fixtury JSONL/SQLite)
├── OnionLLM.Cli/                  (konsola: raporty, --json/--csv, statusline dla Claude Code)
├── OnionLLM/                      (MAUI — tylko UI + ViewModel-e, referencja do Core)
└── OnionLLM.WidgetProvider/       (widżet — referencja do Core zamiast linkowanych plików)
```

### Kluczowe decyzje

1. **Ledger zamiast zrzutów.** Każde źródło zamienia surowe logi na znormalizowane `UsageEntry` (pojedyncze wywołanie modelu z timestampem). Wpisy trafiają do SQLite `%LOCALAPPDATA%\OnionLLM\ledger.db`. Raporty liczone są zawsze z wpisów → brak luk (B5), a historia przetrwa usunięcie transkryptów przez Claude Code (`cleanupPeriodDays`, domyślnie 30 dni) — to przewaga nad ccusage.
2. **Synchronizacja przyrostowa.** Tabela `SourceFiles(path, source, size, mtime_utc, parsed_at)`. Plik jest parsowany ponownie tylko, gdy zmienił się rozmiar lub mtime; jego stare wpisy są wtedy zastępowane w jednej transakcji. Pliki, które zniknęły, **nie** usuwają wpisów.
3. **Deduplikacja przy zapytaniu.** Wpisy mają `dedupe_key`; podczas agregacji duplikaty są scalane z preferencją dla rekordu „pełniejszego” (fast > standard, z cache_creation, z costUSD) — jak w `claude/src/dedupe.rs`.
4. **Czas.** Wpisy przechowywane w UTC (ms). Grupowanie po dniu/tygodniu/miesiącu w strefie z konfiguracji (domyślnie systemowej).
5. **Koszt liczony przy zapytaniu** (nie zapisywany), bo zależy od trybu (`auto/calculate/display`) i cennika, który się zmienia.
6. **Jedna fasada `UsageEngine`** dla GUI, widżetu i CLI → zero duplikacji logiki.

### Model `UsageEntry`

| Pole | Typ | Opis |
|---|---|---|
| `Source` | string | `claude`, `codex`, `copilot`, `copilot-vs`, `gemini`, … |
| `TimestampMs` | long | UTC, ms |
| `SessionId` | string | ID rozmowy/sesji |
| `Project` | string? | nazwa projektu (Claude: katalog w `projects/`; inne: cwd) |
| `Model` | string? | surowa nazwa modelu |
| `InputTokens` | long | bez cache |
| `OutputTokens` | long | łącznie z reasoning, jeśli źródło tak raportuje |
| `CacheCreationTokens` | long | zapis do cache (suma 5m + 1h) |
| `CacheCreation1hTokens` | long | część z TTL 1h (droższa) |
| `CacheReadTokens` | long | odczyt z cache |
| `ReasoningTokens` | long | informacyjnie (Codex/Gemini) — **nie** doliczane drugi raz |
| `RecordedCostUsd` | double? | koszt zapisany przez narzędzie (`costUSD`) |
| `Speed` | enum | `Unknown/Standard/Fast/Flex` |
| `DedupeKey` | string? | klucz deduplikacji (null = brak deduplikacji) |
| `Credits` | double? | kredyty/premium requests (Copilot) |

`TotalTokens = Input + Output + CacheCreation + CacheRead` (zgodnie z ccusage).

---

## Faza 0 — infrastruktura

- [x] 0.1 Kopia zapasowa stanu wyjściowego (brak gita) — archiwum `OnionLLM-original.tgz` w scratchpadzie sesji (tymczasowe!) + rekomendacja `git init`.
- [x] 0.2 Projekt `OnionLLM.Core` (net11.0, `Nullable`, `ImplicitUsings`, `Microsoft.Data.Sqlite`). Dodany do `OnionLLM.slnx`.
- [x] 0.3 Przeniesienie `Models/*`, `Services/*` do Core; MAUI i WidgetProvider referują Core (`ProjectReference`) zamiast `<Compile Link>`.
- [x] 0.4 Projekt `OnionLLM.Core.Tests` (xUnit) + katalog `Fixtures/` z próbkami JSONL.
  - Fixtury są generowane w kodzie testów (`TestHelpers.ClaudeLine`, syntetyczne JSONL/SQLite w katalogu tymczasowym) zamiast plików w `Fixtures/`.
- [x] 0.5 Wszystkie projekty budują się (`dotnet build`), testy przechodzą (`dotnet test`).

**Kryterium ukończenia:** aplikacja działa jak przed zmianami, ale logika jest w Core.

---

## Faza 1 — model danych i poprawność liczenia

### 1.1 Abstrakcja źródła

```csharp
public interface IUsageSource
{
    string Id { get; }                 // "claude"
    string DisplayName { get; }        // "Claude Code"
    string Vendor { get; }             // "Anthropic"
    string AccentColor { get; }
    IReadOnlyList<string> ResolveRoots(OnionConfig config);   // env + config + domyślne
    bool IsInstalled(OnionConfig config);                     // katalog danych lub binarka w PATH
    IEnumerable<SourceFile> EnumerateFiles(OnionConfig config);
    IEnumerable<UsageEntry> Parse(SourceFile file, ParseDiagnostics diag, CancellationToken ct);  // synchronicznie, wołane z Task.Run
}
```

- [x] Zaimplementowane: `OnionLLM.Core/Sources/IUsageSource.cs` (+ `UsageSourceBase`: konfiguracja → env → domyślne; `ParserVersion` wymusza reparse).

`ParseDiagnostics` zlicza pominięte linie (zły JSON, brak usage, zły timestamp) per plik — do trybu debug (Faza 6.6).

### 1.2 Claude Code (`ClaudeSource`) — naprawia B1, B2, B6

- [x] Korzenie: `CLAUDE_CONFIG_DIR` (lista rozdzielona przecinkami; każdy element to katalog konfiguracyjny z `projects/` albo sam `projects/`), w przeciwnym razie `~/.config/claude` i `~/.claude` (oba, jeśli istnieją). Najgłębszy korzeń wygrywa przy przypisaniu pliku (ccusage #2374).
- [x] Pliki: `projects/**/*.jsonl`.
- [x] Linia → wpis, gdy: obiekt z `timestamp` (ISO) i `message.usage.input_tokens`/`output_tokens` liczbowe. Linie `type != "assistant"` bez usage — pomijane bez diagnostyki.
- [x] Pola: `message.usage.input_tokens`, `output_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `cache_creation.ephemeral_1h_input_tokens` (→ `CacheCreation1hTokens`), `message.usage.speed` (`"fast"`) → `Speed`, `message.model`, `costUSD`, `sessionId` (fallback: nazwa pliku), `requestId`, `message.id`.
- [x] Model `"<synthetic>"` — pomijany (wewnętrzne wiadomości Claude Code, zero tokenów).
- [x] `Project` = pierwszy segment ścieżki pod `projects/` (np. `D--Repo-OnionLLM`); do wyświetlania dekodowany na czytelną nazwę (ostatni człon).
- [x] `DedupeKey` = `message.id + ":" + requestId`; gdy brak `requestId` → `message.id + ":" + sessionId + ":" + timestamp` (ccusage #1799); gdy brak `message.id` → null.
- [x] Regułą scalania duplikatów: preferuj rekord z `Speed=Fast`, potem z `cache_creation`, potem z `costUSD` (ccusage #886).

### 1.3 Codex (`CodexSource`) — naprawia B3, B6

- [x] Korzenie: `CODEX_HOME` (lista po przecinku) lub `~/.codex`; pliki `sessions/**/*.jsonl` i `archived_sessions/*.jsonl`.
- [x] Format: linie `{"timestamp", "type":"event_msg", "payload":{"type":"token_count","info":{"last_token_usage":{…},"total_token_usage":{…}}}}`.
- [x] Na wpis: preferuj `last_token_usage` (zużycie tego wywołania). Gdy brak — różnica kolejnych `total_token_usage` (z ochroną przed spadkiem/resetem: delta < 0 → traktuj bieżący total jako nową bazę).
- [x] Pomijaj zdublowane `token_count` z identycznym `total_token_usage` (Codex emituje je wielokrotnie bez nowego wywołania).
- [x] Mapowanie tokenów: Codex `input_tokens` zawiera `cached_input_tokens` → `InputTokens = input - cached`, `CacheReadTokens = cached`; `OutputTokens = output_tokens` (zawiera reasoning), `ReasoningTokens = reasoning_output_tokens`.
- [x] Model: z `turn_context.payload.model` / `payload.info.model` / `session_meta`; ostatni znany model w pliku przypisywany do kolejnych wpisów; brak → `gpt-5` (fallback jak ccusage, oznaczony jako „szacowany”).
- [x] `SessionId` z `session_meta.payload.id` lub nazwy pliku; `Project` z `session_meta.payload.cwd` / `turn_context.payload.cwd`.
- [x] Speed: `service_tier` (`fast`/`flex`/`priority`) z `turn_context` lub `~/.codex/config.toml` (`service_tier`).
- [x] Forki (`session_meta.payload.forked_from` / replay): wpisy skopiowane z sesji-rodzica (ten sam timestamp + identyczne `last_token_usage`) deduplikowane kluczem `{timestamp}:{in}:{cached}:{out}:{reasoning}:{total}`.
- [x] Zdarzenia `token_usage_record` / kompakcja (ccusage `CodexCompactionUsageState`) — zaimplementowane (2026-11-24, druga tura), testy na scenariuszach z ccusage.

### 1.4 GitHub Copilot — naprawia B4

- [x] `session-store.db` (jedno źródło `copilot` obsługuje oba formaty): `%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli\session-store.db`, `~/.copilot/session-store.db` → tabela `assistant_usage_events` (jeden wiersz = wpis). Mapowanie: `input_tokens` → Input, `cache_read_tokens` → CacheRead, `cache_write_tokens` → CacheCreation, `output_tokens` → Output; model/sesja z kolumn, jeśli istnieją (sprawdzane przez `pragma_table_info`). Plik SQLite traktowany jako jeden `SourceFile` (mtime/size też dla `-wal`). **Uwaga:** `input_tokens` zawiera `cache_read`+`cache_write` → odejmowane (stary kod liczył cache podwójnie). `total_nano_aiu` × 1e-11 = koszt USD (zweryfikowane stawkami z `token_details_json`). Deduplikacja po treści (ta sama sesja bywa w bazie CLI i VS).
- [x] `session-state/*/events.jsonl` — ostatnie `session.shutdown` (`modelMetrics`) jako wpis zbiorczy sesji (`IsSessionSummary`), odrzucany, gdy baza ma szczegóły tej sesji. `cwd` z `session.start`.
- [x] Eksport OTel `${COPILOT_HOME}/otel/**/*.jsonl` — zaimplementowany (`CopilotOtel.cs`), testy syntetyczne (brak próbek na tej maszynie).
- [x] Wykrywanie instalacji jak dotychczas (rozszerzenia VS Code, katalogi).

### 1.5 Ledger (`UsageLedger`)

- [x] Schemat:
  ```sql
  CREATE TABLE SourceFiles(path TEXT PRIMARY KEY, source TEXT, size INTEGER, mtime_utc INTEGER, parsed_at INTEGER, skipped_lines INTEGER);
  CREATE TABLE Entries(id INTEGER PRIMARY KEY, file_path TEXT, source TEXT, ts INTEGER, session_id TEXT, project TEXT, model TEXT,
      input INTEGER, output INTEGER, cache_create INTEGER, cache_create_1h INTEGER, cache_read INTEGER, reasoning INTEGER,
      recorded_cost REAL, speed INTEGER, credits REAL, dedupe_key TEXT);
  CREATE INDEX IX_Entries_ts ON Entries(ts);
  CREATE INDEX IX_Entries_file ON Entries(file_path);
  CREATE TABLE Meta(key TEXT PRIMARY KEY, value TEXT);   -- wersja schematu, wersja parserów
  ```
- [x] `SyncAsync()` — dla każdego źródła i pliku: porównaj size/mtime → parsuj zmienione → `DELETE FROM Entries WHERE file_path=?` + `INSERT` w transakcji.
- [x] Wersja parsera w `Meta`: zmiana wersji parsera źródła wymusza pełny reparse (np. po naprawie błędu).
- [x] Odczyt plików z `FileShare.ReadWrite | Delete` (logi są otwarte przez narzędzia).
- [x] Migracja: stara baza `usage-history.db` zostaje nietknięta (nie importujemy zrzutów — były błędne, B3/B1).
- [x] Blokada międzyprocesowa (GUI + widżet + CLI) — `Mutex` nazwany `Global\OnionLLM.Ledger` na czas zapisu; WAL dla odczytów.

### 1.6 Testy Fazy 1

- [x] Claude: duplikaty w jednym pliku i między plikami, cache 5m/1h, fast, `<synthetic>`, brak requestId.
- [x] Codex: `last_token_usage`, fallback delta z `total_token_usage`, powtórzone token_count, sesja przez północ (wpisy przed północą nie liczą się „dzisiaj”), cached input.
- [x] Ledger: reparse po zmianie pliku, zachowanie wpisów po usunięciu pliku.

---

## Faza 2 — cennik i koszty

### 2.1 Katalog cen (`PricingCatalog`)

- [x] Wbudowany snapshot: `OnionLLM.Core/Pricing/pricing-snapshot.json` (EmbeddedResource) — przefiltrowany LiteLLM `model_prices_and_context_window.json` do prefiksów jak w ccusage `litellm-sources.json`: `claude-`, `anthropic.`, `gpt-`, `o3`, `o4-mini`, `openai/`, `gemini`, `deepseek`, `xai/`, `zai/`, `qwen`, `moonshot`, `grok`. Pola: `input_cost_per_token`, `output_cost_per_token`, `cache_creation_input_token_cost`, `cache_creation_input_token_cost_above_1hr`, `cache_read_input_token_cost`, `*_above_200k_tokens`, `max_input_tokens`, `max_tokens`.
- [x] Odświeżanie snapshotu: komenda `onionllm pricing update-snapshot [ścieżka]` (pobiera LiteLLM, filtruje tą samą logiką co tryb online, zapisuje) — zamiast osobnego skryptu PS.
- [x] Tryb online (domyślnie w GUI włączony, raz na 24h): pobranie LiteLLM do `%LOCALAPPDATA%\OnionLLM\pricing-cache.json`; błąd sieci → cache → snapshot. Tryb `offline` wyłącza sieć.
- [x] Dopasowanie modelu: dokładne → bez prefiksu dostawcy (`anthropic/`, `openai/`, `us.anthropic.`) → bez sufiksu daty (`-20250514`) → najdłuższy wspólny prefiks w obrębie rodziny. Wynik dopasowania keszowany.
- [x] `pricingOverrides` z konfiguracji (klucz: model lub `[source] model`) nadpisują pola.
- [x] Modele bez ceny: koszt 0 + flaga `Unpriced` (pokazywana w UI/JSON jak `unpricedModels` w ccusage).

### 2.2 Kalkulator (`CostCalculator`)

- [x] `cost = in*in_rate + out*out_rate + cc5m*cc_rate + cc1h*cc1h_rate(fallback 2×in_rate… wg LiteLLM) + cr*cr_rate`.
- [x] Progi > 200k tokenów (Claude Sonnet 4/4.5 1M, Gemini): gdy `input+cache_create+cache_read` wywołania > 200 000 → stawki `_above_200k_tokens`.
- [x] Fast mode: stawki `*_priority` (OpenAI), inaczej `provider_specific_entry.fast` z LiteLLM, inaczej tabela nadpisań z ccusage (opus-4-6/4-7: 6×, opus-4-8: 2×, gpt-5.5: 2,5×); brak danych → 1×. Fast nie płaci dopłaty za długi kontekst. Flex: `*_flex` lub 50%.
- [x] Długi kontekst: **całe żądanie** po stawce progu, gdy `input+cache` > próg (`_above_200k/272k/100k_tokens`). Świadoma różnica względem ccusage, które dla części modeli liczy krańcowo.
- [x] Tryby: `auto` (RecordedCostUsd jeśli jest, inaczej wylicz), `calculate` (zawsze wylicz), `display` (tylko zapisany, inaczej 0).
- [x] Testy na znanych przykładach (`docs/guide/cost-modes.md`).

---

## Faza 3 — agregacje i raporty

- [x] `ReportQuery { Since, Until, Last(N), Sources[], Projects[], Order(asc/desc), TimeZone, StartOfWeek, CostMode, Breakdown(bool), GroupByProject(bool) }`.
- [x] Walidacja dat (`YYYY-MM-DD` / `YYYYMMDD`, niepoprawne → błąd, `since > until` → błąd) jak w ccusage.
- [x] Raporty: `Daily`, `Weekly` (start tygodnia konfigurowalny, domyślnie poniedziałek), `Monthly`, `Session` (per sesja: pierwszy/ostatni timestamp, projekt, modele, tokeny, koszt), `SessionById`.
- [x] Wiersz raportu: okres, źródła, modele, Input, Output, CacheCreate, CacheRead, Total, Cost, `Unpriced`, opcjonalnie `ModelBreakdowns[]` i `SourceBreakdowns[]`.
- [x] Wiersz sumy (`totals`).
- [x] `--last N` = N ostatnich okresów danego grupowania (dzień/tydzień/miesiąc), łącznie z bieżącym.
- [x] Grupowanie po projekcie + aliasy projektów (`projectAliases: "a=Nazwa,b=Inna"`).
- [x] Testy: granice dni w strefie czasowej, tydzień od niedzieli/poniedziałku, sortowanie, `last`.

---

## Faza 4 — bloki 5-godzinne

Algorytm z `claude/src/blocks.rs`:

- [x] Wpisy Claude posortowane po czasie. Start bloku = timestamp pierwszego wpisu zaokrąglony w dół do pełnej godziny (UTC). Nowy blok, gdy `t - start > długość` **lub** `t - poprzedni > długość` (wtedy dodatkowo blok-przerwa „(inactive)” od `poprzedni + długość` do `t`).
- [x] Blok aktywny: `now - ostatni < długość && now < start + długość`.
- [x] Burn rate: `tokens/min = total / (ostatni - pierwszy)`, `tokens/min (bez cache)` do wskaźnika, `$/h = cost / min * 60`.
- [x] Projekcja: `remaining_min = round((koniec - now)/min)`; `projected_tokens = total + tpm*remaining`, `projected_cost = cost + $/h/60*remaining`.
- [x] Limit tokenów: liczba albo `max` (największy zakończony blok); domyślnie `max`. Status: OK / WARNING (>80%) / EXCEEDS.
- [x] Wskaźnik burn rate: < 2 000 tpm normalny, 2 000–5 000 umiarkowany, > 5 000 wysoki.
- [x] Długość sesji konfigurowalna (`sessionLength`, godziny, domyślnie 5).
- [x] `--active` (tylko aktywny blok, widok szczegółowy), `--recent` (ostatnie 3 dni).
- [x] Połączenie z limitem live z API (istniejąca funkcja): gdy dostępne `five_hour.resets_at`, użyj go jako końca aktywnego bloku (dokładniejsze niż szacunek).
- [x] Testy na fixturze z luką > 5h i blokiem aktywnym (stały `now`).

---

## Faza 5 — konfiguracja

- [x] `OnionConfig` (System.Text.Json, camelCase):
  ```json
  {
    "$schema": "./onionllm.schema.json",
    "timezone": "Europe/Warsaw",
    "costMode": "auto",
    "offline": false,
    "startOfWeek": "monday",
    "sessionLengthHours": 5,
    "tokenLimit": "max",
    "projectAliases": "D--Repo-OnionLLM=OnionLLM",
    "sources": { "claude": { "enabled": true, "paths": [] }, "codex": { "enabled": true, "paths": [] } },
    "pricingOverrides": { "my-model": { "inputCostPerToken": 0.000001, "outputCostPerToken": 0.000002 } },
    "refreshIntervalSeconds": 30
  }
  ```
- [x] Lokalizacje (priorytet malejący): argument `--config`, `./.onionllm.json`, `%APPDATA%\OnionLLM\config.json`. CLI > lokalny > użytkownika > domyślne.
- [x] Zmienne środowiskowe źródeł (`CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `COPILOT_HOME`, `GEMINI_DATA_DIR`, …) mają pierwszeństwo przed `sources.*.paths`? → **Nie**: kolejność `config.paths` > env > domyślne (jawna konfiguracja aplikacji wygrywa; udokumentowane).
- [x] JSON Schema `onionllm.schema.json` generowany/utrzymywany ręcznie, kopiowany obok configu.
- [x] Walidacja z czytelnymi błędami (nieznana strefa, zła data, nieznany tryb).
- [x] GUI zapisuje ustawienia do tego samego pliku (Faza 7.6).

---

## Faza 6 — CLI, eksport, statusline

Projekt `OnionLLM.Cli` (net11.0, exe `onionllm`), bez zewnętrznych zależności do parsowania argumentów (prosty parser własny — mało opcji, zero ryzyka).

- [x] 6.1 Komendy: `daily`, `weekly`, `monthly`, `session [--id X]`, `blocks [--active] [--recent] [--token-limit N|max] [--session-length H]`, `statusline`, `sources` (wykryte źródła + ścieżki), `sync` (wymuś synchronizację ledgera).
- [x] 6.2 Namespace źródła: `onionllm claude daily`, `onionllm codex session` (jak ccusage).
- [x] 6.3 Opcje globalne: `--since`, `--until`, `--last N`, `--order asc|desc`, `--timezone`, `--mode auto|calculate|display`, `--offline`, `--breakdown`, `--instances`, `--project X`, `--no-cost`, `--json`, `--csv`, `--config`, `--debug`, `--start-of-week`.
- [x] 6.4 Tabela w terminalu: kolumny Okres, Modele, Input, Output, Cache Create, Cache Read, Total, Koszt; responsywna (wąski terminal → kompaktowe kolumny); `NO_COLOR`/`FORCE_COLOR`.
- [x] 6.5 Eksport `--json` (struktura zgodna koncepcyjnie z ccusage: `{ daily: [...], totals: {...} }`, camelCase) i `--csv`.
- [x] 6.6 `--debug`: na stderr liczba pominiętych linii per źródło/plik i powód; ścieżki korzeni.
  - Uwaga (audyt 2026-11-25): w ccusage `--debug` znaczy co innego — porównuje koszt zapisany przez narzędzie z wyliczonym (`--debug-samples`). Odpowiednik: punkt 11.8.3.
- [x] 6.7 `statusline` dla Claude Code: czyta JSON z stdin (`session_id`, `transcript_path`, `model.display_name`, `cost.total_cost_usd`, `context_window.{total_input_tokens,context_window_size}`), wypisuje:
  `🤖 Opus 4.1 | 💰 $1.23 session / $4.56 today / $2.10 block (1h 20m left) | 🔥 $3.40/hr | 🧠 85,000 (43%)`
  - koszt sesji: `--cost-source auto|ccusage|cc|both` (`cc` = z hooka, `onion` = wyliczony),
  - kontekst: z `context_window` lub z ostatniego wpisu transkryptu (`input + cache_create + cache_read`) / `max_input_tokens` modelu; kolory < 50% / 50–80% / > 80%,
  - `--visual-burn-rate off|emoji|text|emoji-text`,
  - cache wyniku (plik w `%LOCALAPPDATA%\OnionLLM\statusline-cache.json`, ważny `--refresh-interval` s, domyślnie 1 s), domyślnie offline.
  - Instrukcja konfiguracji `~/.claude/settings.json` w README.
- [x] 6.8 Testy CLI (zob. „Pozostało”); wcześniej: statusline, bloki i raporty sprawdzone ręcznie na prawdziwych danych; brak automatycznych testów projektu CLI (logika jest w Core i ma testy).

---

## Faza 7 — GUI (MAUI)

Shell z zakładkami (`TabBar`): **Dziś · Raporty · Sesje · Bloki · Ustawienia**.

- [x] 7.1 **Dziś** (rozbudowa obecnego ekranu):
  - nagłówek: tokeny dziś **+ koszt dziś**;
  - karta źródła: Input / Output / **Cache create / Cache read**, **koszt**, **modele** (top 3), sesje aktywne/zakończone, 7d/30d (z ledgera — tokeny i koszt), limity live 5h/7d (bez zmian);
  - karta „Aktywny blok” (Claude): czas do końca, koszt bloku, burn rate z kolorem, projekcja, pasek % limitu;
  - lista źródeł dynamiczna (wszystkie wykryte; niewykryte zwinięte), zachowana kolejność przeciągania.
- [x] 7.2 **Raporty**: przełącznik Dziennie/Tygodniowo/Miesięcznie, zakres (DatePicker od–do + szybkie „7 dni / 30 dni / ten miesiąc”), filtr źródła/projektu, tabela (CollectionView) + prosty wykres słupkowy (GraphicsView) kosztu/tokenów, rozwijany podział na modele, suma.
- [x] 7.3 **Sesje**: lista sesji (sortowanie po koszcie/dacie), szczegóły sesji (modele, tokeny, czas trwania, projekt), wyszukiwanie po ID.
- [x] 7.4 **Bloki**: lista bloków z przerwami, aktywny wyróżniony, REMAINING/PROJECTED.
- [x] 7.5 **Eksport**: przycisk „Eksportuj JSON/CSV” w Raportach i Sesjach (FileSaver przez `Microsoft.Maui.Storage` / zapis do Dokumentów + powiadomienie).
  - Pliki trafiają do `Dokumenty\OnionLLM\`, ścieżka jest kopiowana do schowka i pokazywana w stopce (bez zależności od CommunityToolkit).
- [x] 7.6 **Ustawienia**: strefa czasowa, tryb kosztu, offline, start tygodnia, długość bloku, limit tokenów, interwał odświeżania, źródła (włącz/wyłącz + ścieżki), aliasy projektów, przycisk „Odśwież cennik”, „Przebuduj bazę” (pełny reparse), **Diagnostyka** (ścieżki korzeni, liczba plików, pominięte linie, ostatnia synchronizacja).
- [x] 7.7 Odświeżanie w tle bez blokowania UI (`Task.Run` dla `SyncAsync`), `CancellationToken`, zachowanie istniejącego „always on top”.

---

## Faza 8 — widżet Windows

- [x] 8.1 Referencja do Core; `BuildData()` używa `UsageEngine` (wspólny ledger z GUI).
- [x] 8.2 Dane: tokeny i **koszt dziś**, per źródło: tokeny, koszt, limit 5h/7d; aktywny blok (pozostały czas, burn rate).
- [x] 8.3 Rozmiar `large`: lista źródeł + blok; `medium`: suma + top 3 źródła.
  - Medium: 3 źródła, Large: do 6; sortowanie po koszcie dziś.
- [x] 8.4 Odświeżanie co 30 s tylko gdy aktywny (bez zmian), synchronizacja ledgera nie częściej niż co 30 s (współdzielona z GUI przez `Meta.last_sync`).
  - Widżet synchronizuje nie częściej niż co 25 s; synchronizacja między procesami (GUI, widżet, CLI) chroniona nazwanym muteksem `Local\OnionLLM.Ledger`.

---

## Faza 9 — dodatkowe źródła danych

Każde źródło: klasa w `Sources/`, test z fixturą, wpis w ustawieniach. Format opisany w ccusage `rust/adapters/<id>/src/*` i `docs/guide/<id>/index.md` — przed implementacją każdego czytam parser referencyjny.

| # | Źródło | ID | Lokalizacja domyślna (Windows: `~` = `%USERPROFILE%`) | Format | Priorytet |
|---|---|---|---|---|---|
| 9.1 ✅ | Gemini CLI | `gemini` | `${GEMINI_DATA_DIR:-~/.gemini/tmp}` | JSON/JSONL czatów z `tokens` | wysoki |
| 9.2 ✅ | OpenCode | `opencode` | `${OPENCODE_DATA_DIR:-~/.local/share/opencode}` | JSON wiadomości / SQLite | wysoki |
| 9.3 ✅ | Qwen Code | `qwen` | `${QWEN_DATA_DIR:-~/.qwen}` | JSONL (fork Gemini CLI) | średni |
| 9.4 ✅ | Cline | `cline` | `${CLINE_DATA_DIR}`, `~/.cline`, VS Code `globalStorage/saoudrizwan.claude-dev` | `ui_messages.json` (`api_req_started`) | średni |
| 9.5 ✅ | Kilo Code | `kilo` | `${KILO_DATA_DIR:-~/.local/share/kilo}` (tylko `kilo.db`, tak samo w ccusage) | SQLite jak OpenCode | średni |
| 9.6 ✅ | Amp | `amp` | `${AMP_DATA_DIR:-~/.local/share/amp}` | JSON wątków | średni |
| 9.7 ✅ | Droid (Factory) | `droid` | `${DROID_SESSIONS_DIR:-~/.factory/sessions}` | JSONL + settings | średni |
| 9.8 ✅ | Codebuff | `codebuff` | `${CODEBUFF_DATA_DIR:-~/.config/manicode}` | JSON | niski |
| 9.9 ✅ | Goose | `goose` | `GOOSE_PATH_ROOT` / standardowe | SQLite/JSONL | niski |
| 9.10 ✅ | Kimi | `kimi` | `${KIMI_DATA_DIR:-~/.kimi}`, `~/.kimi-code` | JSONL | niski |
| 9.11 ✅ | pi-agent | `pi` | `${PI_AGENT_DIR:-~/.pi/agent/sessions}` | JSONL | niski |
| 9.12 ✅ | OpenClaw | `openclaw` | `${OPENCLAW_DIR:-~/.openclaw}` | JSONL | niski |
| 9.13 ✅ | Hermes Agent | `hermes` | `${HERMES_HOME:-~/.hermes}/state.db` | SQLite | niski |
| 9.14 ✅ | Antigravity | `antigravity` | `${ANTIGRAVITY_DATA_DIR:-~/.gemini/antigravity*}` | — | niski |
| 9.15 ✅ | Grok Build CLI | `grok` | `${GROK_HOME:-~/.grok}` | JSONL | niski |
| 9.16 ✅ | ZCode | `zcode` | `${ZCODE_HOME:-~/.zcode}` | — | niski |
| 9.17 ✅ | DeepSeek Harness | `dsh` | `${DSH_HOME:-~/.dsh}` | — | niski |

Każde źródło jest wykrywane automatycznie; brak katalogu = źródło ukryte w „niewykrytych”.
**Status:** ✅ = zaimplementowane i zarejestrowane w `SourceRegistry`. Szczegóły mapowania pól: komentarze w `OnionLLM.Core/Sources/<Nazwa>Source.cs`. Tury OpenCode `claude-agent` liczone przez Claude Code są pomijane (`MirrorOf`, 11.5.1). Świadome odstępstwa od ccusage (11.6.11):
- tokeny „resztowe” (total > suma składników) są pomijane;
- koszt zapisany równy 0 oznacza brak kosztu, poza źródłami, w których ccusage traktuje go jako prawdziwy (claude, amp, cline, openclaw, pi — 11.1.12);
- projekt brany z `cwd` lub katalogu zamiast stałej nazwy źródła (Cline, Amp, Gemini, Grok, Hermes, ZCode, dsh, OpenClaw, Droid, Codebuff, Goose);
- Codebuff odejmuje tokeny cache od wejścia (format OpenAI);
- tokeny thinking wliczone do Output (Gemini, Qwen, Antigravity, Droid) — koszt i suma bez zmian;
- luźniejsze parsowanie liczb (tekst liczbowy) i dat;
- Gemini deduplikuje tę samą wiadomość także między plikami `.json` i `.jsonl`;
- Droid wyświetla nazwy modeli z kropkami (`gemini-2.5-pro`), bo cennik potrzebuje nazwy z logu;
- pi: fork liczony ponownie przy zmianie rodzica (odcisk pliku), a nie przy każdym odczycie.


---

## Faza 10 — opcjonalne / dokumentacja

- [x] 10.1 `README.md`: instalacja, CLI, statusline dla Claude Code, konfiguracja, lista źródeł.
- [-] 10.2 Statusline Codex przez MCP (`ccusage codex statusline --mcp`) — eksperymentalna funkcja prerelease Codexa; odłożone.
- [-] 10.3 Telemetria — świadomie pominięta (OnionLLM: „Lokalnie · prywatnie”).
- [x] 10.4 Odświeżanie snapshotu cen: `onionllm pricing update-snapshot` (zamiast skryptu PS), opisane w README.

---

## Faza 11 — luki względem ccusage (audyt 2026-11-25)

Plan oznaczał prawie wszystko jako gotowe, ale porównanie kodu z ccusage plik po pliku (adaptery, cennik, CLI, konfiguracja, JSON, statusline) znalazło luki. Część z nich zmienia liczbę tokenów i koszty. Odnośniki `plik.rs:linia` są względne do `rust/adapters/` lub `rust/crates/ccusage-core/src/` w kopii ccusage.

Priorytety: **W** — zmienia tokeny lub koszt popularnych modeli; **Ś** — przypisanie, nisze, wyświetlanie; **N** — przypadki brzegowe.
Każdy punkt ma wskazane miejsce w UI. Zmiana tylko w Core wystarcza, gdy wpływa wyłącznie na liczby we wszystkich widokach.

### 11.1 Cennik i koszty (Core → wszystkie widoki, widżet, eksport)

- [x] 11.1.1 **W** Wbudowane ceny modeli, których brak w LiteLLM. Dotyczy to starszych Claude: Sonnet 4, Opus 4 i 4.1, 3.7/3.5 Sonnet, 3.5 Haiku i 3.x. Przed poprawką kosztowały $0. `OnionLLM.Core/Pricing/BuiltinPricing.cs` przenosi `pricing/builtin_pricing.rs` (bez wierszy Qwen `-thinking`, zob. 11.1.9) oraz wiersze Claude, które ccusage bierze z models.dev. Wiersz jest tylko dodawany, gdy go brakuje; istniejącemu uzupełnia się brakujące stawki cache i limit kontekstu.
- [x] 11.1.2 **W** Wpisy łączące wiele żądań wyceniane bez progu długiego kontekstu (`cost.rs:148-154`, #2365). Nowa właściwość `UsageEntry.SpansMultipleRequests` obejmuje dwa rodzaje wpisów: wpisy zbiorcze sesji (Copilot `shutdown`, Droid, Goose, Hermes, sumy OpenCode) oraz `IsAggregate`, czyli tury Grok. `IsAggregate` ma kolumnę `aggregate` w ledgerze (migracja do schematu 3), a parser Grok ma wersję 2, co wymusza ponowny odczyt.
- [x] 11.1.3 **W** Kuratorskie progi i harmonogramy cen według daty (`pricing/data.rs`):
  - Sonnet 4/4.5 powyżej 200k: $6 / $22.50;
  - dopłata Opus/Sonnet 4.6 do 2026-03-13;
  - zmiany cen Gemini 2.5/3.x/4 od 2026-12-01 i 2027-01-01;
  - `grok-code-fast-1`;
  - DeepSeek V4: stawki w szczycie i poza nim (UTC, dni robocze) oraz zmiana stawek 2026-08-16.

  Wymaga wyceny zależnej od `TimestampMs` w `CostCalculator`.
- [x] 11.1.4 **Ś/W** models.dev jako drugie źródło cen (`pricing/models_dev.rs`, `fetch.rs`) oraz pełny plik LiteLLM w trybie online, bez filtra prefiksów. Obecnie brak kluczy `azure/`, `bedrock/`, `vertex_ai/`, `openrouter/` i MiniMax. UI: Ustawienia → sekcja „Cennik” (źródła, data aktualizacji, liczba modeli).
  - Core gotowe (snapshot models.dev wbudowany, cache online 24 h, pełny LiteLLM online); sekcja „Cennik” w Ustawieniach pokazuje LiteLLM i models.dev osobno (pochodzenie, data, liczba modeli); `onionllm pricing info` też.
- [x] 11.1.5 **Ś** Ściślejsze dopasowanie nazw modeli. Dłuższa nazwa może dopasować krótszy klucz tylko wtedy, gdy różnicą jest data, rewizja, słowo effort lub `thinking` danej rodziny; w przeciwnym razie model jest bez ceny (`pricing/data.rs:197-250`). Dziś np. `gpt-5.4-codex` dostaje cenę `gpt-5.4`. Zmiana dotyczy `PricingCatalog.Lookup`.
- [x] 11.1.6 **Ś** Domyślne stawki cache zależne od dostawcy, gdy wiersz ich nie podaje: OpenAI odczyt 0,1×, Qwen 0,2×, DeepSeek 0,02×, GPT-5.6+ zapis 1 h 2,5× (`pricing/data.rs:6-194`). Dziś cache liczony jest po pełnej stawce wejścia (`CostCalculator.cs`).
- [x] 11.1.7 **Ś** Dostawca z logów jako wskazówka cennika i drogi rozliczenia (`common/src/billing.rs`): nowe pole `UsageEntry.Provider` z kolumną w ledgerze. Dotyczy źródeł OpenCode, Kilo, Cline, Goose, Codebuff, Hermes (`billing_provider`), OpenClaw, dsh, Antigravity (Vertex) i ZCode (11.6).
- [x] 11.1.8 **Ś** Poziomy szybkości (`report/cost.rs`, `*-multiplier-overrides.json`):
  - Ultrafast 6× (nowy `SpeedTier.Ultrafast`; dziś filtr `ultrafast` w `PricingCatalog` go usuwa);
  - Flex tylko dla modeli z listy;
  - stawki `_priority` nie dla każdego modelu (np. `gpt-5.1`/`gpt-5.2` mają 1×);
  - fast pomija próg długiego kontekstu tylko dla modeli Anthropic z datowanym 1M.
- [x] 11.1.9 **N** Aliasy modeli:
  - konfigurowalne (`modelAliases` w configu; odpowiednik `CCUSAGE_MODEL_ALIASES`, `model_aliases.rs`);
  - wbudowane `gpt-reserve` → `gpt-5.6-luna` i `gpt-5.3-spark`;
  - wiersze Qwen `-thinking` (z rozpoznawaniem trybu thinking).

  UI: edytor aliasów w Ustawieniach.
  - Core gotowe (`modelAliases` w configu, aliasy wbudowane); edytor `nazwa=model` w Ustawieniach (walidacja przed zapisem, ostrzeżenie o aliasie bez ceny). Wiersze Qwen `-thinking` nadal bez rozpoznawania trybu thinking.
- [x] 11.1.10 **Ś** Ceny szacowane: darmowy model używany przez płatnego hosta dostaje cenę najbliższego płatnego wpisu i flagę `estimated` (`docs/guide/json-output.md` → „Estimated Prices”). UI: znacznik `~` przy koszcie i lista modeli szacowanych w stopce Raportów.
- [x] 11.1.11 **Ś** `pricingOverrides` zgodne z ccusage (`ccusage-config/src/config_schema.rs:291-305`):
  - przyjmowanie nazwy `cacheCreationInputTokenCostAbove1hr` (obok obecnej `…Above1h`, która dziś po cichu ignoruje configi z ccusage);
  - pola `*Above200kTokens`, `fastMultiplier`, `flexMultiplier`;
  - dopasowanie kluczy z prefiksem dostawcy i aliasów.

  UI: edytor nadpisań cen w Ustawieniach.
  - Core gotowe (obie nazwy pola 1 h, wszystkie pola ccusage, `PricingCatalog.HasOverride`); edytor w Ustawieniach: lista z polami w USD za 1M tokenów, sekcja „Zaawansowane” dla pozostałych pól, zapis bez utraty wartości. Etykiety modeli statusline (`modelLabelAliases`) też edytowalne.
- [x] 11.1.12 **N** Koszt zapisany równy 0 ccusage traktuje jako prawdziwy dla Claude, Amp, Cline i OpenClaw (OnionLLM jako brak). Dla pi nadpisanie ceny unieważnia koszt zapisany (`pi/src/parser.rs:463-472`). Darmowe wiersze LiteLLM z ceną 0/0 (Gemma, Lyria) ccusage pokazuje jako bez ceny. Do decyzji: przenieść albo opisać jako świadome odstępstwo.
  - Przeniesione: koszt 0 jest prawdziwy dla claude, amp, cline, openclaw i pi; dla pi nadpisanie unieważnia koszt zapisany w trybie auto; wiersze Google 0/0 są pomijane (model bez ceny).

### 11.2 Claude Code (`ClaudeSource`, `Deduplicator`)

- [x] 11.2.1 **W** Zużycie advisora: każdy element `usage.iterations[]` typu `advisor_message` jako osobny wpis z własnym modelem i kluczem `msgId:advisor:N` (`claude/src/advisor.rs:283-335`, `daily/read.rs:207-234`).
- [x] 11.2.2 **W** Rekordy AgentProgress, czyli zużycie subagentów zagnieżdżone w `data.message.message` (`daily/record.rs:182-241`, #977). Dziś są pomijane bez diagnostyki.
- [x] 11.2.3 **W** Łańcuch odpowiedzi dla wpisów bez `requestId`, przez `uuid`/`parentUuid`/tool_use: bloki jednej odpowiedzi liczone raz (`dedupe.rs:85-102`, `daily/response_chain.rs`, #2255). Dziś każdy blok o innym timestampie liczy się osobno.
- [x] 11.2.4 **W/Ś** Sidechain: odczyt `isSidechain`, dopasowanie replayów `/btw` (ten sam `message.id`, nowy `requestId`) i preferencja kopii spoza sidechain (`dedupe.rs:305-340, 462-467`, #913).
- [x] 11.2.5 **Ś** Kolejność wyboru przy duplikatach jak w ccusage: najpierw kopia spoza sidechain, potem więcej tokenów, potem fast, potem obecność cache_creation, potem costUSD; przy pełnym remisie decyduje ścieżka pliku (`dedupe.rs:462-475`).
- [x] 11.2.6 **N** Gdy jest podział `cache_creation` (5 min/1 h), suma zapisu do cache pochodzi z podziału, w tym z iteracji (`usage_wire.rs:74-85`, `iterations.rs`, `ccusage-core types.rs:18-25`).
- [x] 11.2.7 **N** Odrzucanie rekordów, które ccusage pomija (null w `speed`, polach cache, `model`, `id`, `costUSD` lub `requestId`; nieznana prędkość; wersja nie-semver; puste identyfikatory), z powodem w diagnostyce (`usage_wire.rs:92-127`, `record.rs:156-282`). UI: Ustawienia → Diagnostyka.
- [x] 11.2.8 **N** Rekordy `<synthetic>` zachowywane, tylko bez nazwy modelu do wyświetlenia (`daily.rs:171-173`); dziś są usuwane.
- [x] 11.2.9 **Ś** Wpisy fast jako osobny model `<model>-fast` w podziale na modele (`daily.rs:174-175`). UI: osobny wiersz w podziale Raportów i Sesji.
- [x] 11.2.10 **Ś** ID sesji brane ze ścieżki pliku, także z katalogów `subagents/`; `sessionId` z linii tylko do deduplikacji (`paths.rs:96-138`, `load.rs:423`).
- [x] 11.2.11 **N** Ścieżki: obsługa `XDG_CONFIG_HOME` (`home.rs:135`), podążanie za dowiązaniami symbolicznymi i błąd w Diagnostyce, gdy `CLAUDE_CONFIG_DIR` wskazuje nieistniejący katalog.

### 11.3 GitHub Copilot (`CopilotSource`, `CopilotOtel`)

- [x] 11.3.1 **W** Zdarzenia `session.usage_record` (zużycie każdego wywołania) w `events.jsonl`, deduplikowane między plikami po `(sourceSessionId, sequence)` (`copilot/src/parser/session_state.rs:176-240, 370-443`).
- [x] 11.3.2 **W** Koszt zapisany przez Copilota: `totalNanoAiu` z `modelMetrics` i `copilotUsage`, kredyty z `session.usage_checkpoint` oraz wpisy dla niewyjaśnionych kredytów (`session_state.rs:267-342`, `loader/credits.rs:86-174`).
- [x] 11.3.3 **Ś** Kolejne `session.shutdown` jako przedziały (różnica względem poprzedniej sumy), każdy datowany własnym timestampem. Dziś całe zużycie sesji trafia na dzień ostatniego zamknięcia (`loader/reconcile.rs:143-216`). Przedział z `request_count == 1` może używać progu długiego kontekstu (`loader.rs:184-189`).
- [x] 11.3.4 **Ś** Uzgadnianie OTel z danymi sesji jak w ccusage: zachowanie danych z `shutdown`, doliczenie wywołań OTel po ostatnim `shutdown`, zastąpienie przez OTel tylko wtedy, gdy ma więcej wywołań, dopasowanie po ID żądania i odpowiedzi (`reconcile.rs:109-141`, `loader.rs:74-125`).
- [x] 11.3.5 **Ś** Normalizacja ID modeli: usuwanie `-1m` i `-1m-internal`, przy wycenie najpierw surowa nazwa (`parser.rs:401-414`).
- [x] 11.3.6 **Ś** Zmienna `COPILOT_OTEL_FILE_EXPORTER_PATH` (`paths.rs:90-100`).
- [x] 11.3.7 **N** Wiersze z samą liczbą żądań, bez tokenów (`session_state.rs:302`).

### 11.4 Codex (`CodexSource`)

- [x] 11.4.1 **W** Logi `codex exec --json`: linie z `usage`, `data.usage`, `result.usage` albo `response.usage`; timestamp z `timestamp`, `created_at` lub `createdAt`, w ostateczności czas modyfikacji pliku. Katalog z `CODEX_HOME` bez `sessions/` i `archived_sessions/` jest skanowany bezpośrednio (`codex/src/parser.rs:282-295`, `parser/exec.rs`, `paths.rs`).
- [x] 11.4.2 **W** Poziom Ultrafast (6×). Nieznany poziom z `thread_settings_applied` ustawia stan „niesklasyfikowany”, czyli powrót do `config.toml`, a nie poprzednią wartość (`types.rs:54-64`, `parser.rs:506-512`). Powiązane z 11.1.8.
- [x] 11.4.3 **Ś** Nadpisanie poziomu szybkości `auto|standard|flex|fast` (`speed.rs:11-18`). UI: Ustawienia → Codex → „Poziom szybkości do wyceny”.
- [x] 11.4.4 **Ś** `config.toml` czytany jako TOML: wartości w apostrofach, `service_tier` z profilu wybranego przez `profile = "x"` (`speed.rs:26-37`). Dziś regex.
- [x] 11.4.5 **W/Ś** Rollout z `session_meta.history_base` albo rollout zastępczy (`rollout-…-<wątek>_<rollout>` z id równym `<wątek>`) nie jest forkiem (`replay.rs:452-461`). Dziś każdy `forked_from_id` pomija tury sesji.
- [x] 11.4.6 **Ś** Okno replay kończy się na pierwszym znaczniku własnego wątku (`thread_id` w `thread_settings_applied` / `token_usage_record`). Zdarzenia z obcych wątków są odrzucane; subagent kończy okno na pierwszym rekordzie bez `thread_id` (`parser/replay_filter.rs:37-121`).
- [x] 11.4.7 **Ś** Pola `cache_write_input_tokens` / `cache_creation_input_tokens` (zapis do cache) i aliasy `prompt_tokens`, `input`, `completion_tokens`, `output`, `reasoning_tokens`, `cached_tokens` (`types/wire.rs:459-497`).
- [x] 11.4.8 **N** Klucz sesji = względna ścieżka rolloutu (`parser.rs:704-716`); duplikaty preferują Standard (`types.rs:70-89`); `codex-auto-review` dostaje zmienioną nazwę i flagę modelu zastępczego (`parser/model.rs:117-119`).

### 11.5 OpenCode i Kilo

- [x] 11.5.1 **Ś** Pomijanie tur `claude-agent`, które Claude Code już liczy, także w widoku samego OpenCode. Okno szukania sesji Claude rozszerzone o 1 dzień po końcu zakresu; liczy się tylko główny wątek (`ccusage-adapter-all/src/loader.rs:161-176`). UI: Ustawienia → OpenCode → przełącznik „Licz tury claude-agent” (odpowiednik `--include-claude-agent`).
- [x] 11.5.2 **Ś** Sumy z `session`/`session_v2` tylko w raporcie sesji bez filtra dat; wtedy pomijane są wiersze kompakcji tej sesji (`opencode/src/loader/files.rs:27`, `loader.rs:370-459`). Dziś sumy wchodzą do raportów dziennych i miesięcznych.
- [x] 11.5.3 **Ś** Aliasy modeli: `k2p6` → `kimi-k2.6`, `gemini-3-pro-high` → `gemini-3-pro-preview`, `claude-opus-46` → `claude-opus-4-6` (`opencode/src/parser.rs:500-555`).
- [x] 11.5.4 **N** Kilo i OpenCode: kandydat cennika `dostawca/model` (zob. 11.1.7).

### 11.6 Pozostałe źródła

- [x] 11.6.1 **W** Grok: usuwanie sufiksu `-build` i kandydaci `xai/`, `x-ai/` (`grok/src/parser.rs:163-187`). Dziś `grok-4.5-build` nie ma ceny.
- [x] 11.6.2 **W** Droid: kolejność kandydatów cennika: najpierw z prefiksem dostawcy (`anthropic/`, `openai/`, `google/`, `vertex_ai/`, `xai/`, `openrouter/…`), potem surowa nazwa, potem znormalizowana (`droid/src/parser.rs` `price_droid`). Dziś `gemini-2.5-pro` → `gemini-2-5-pro` zostaje bez ceny.
- [x] 11.6.3 **W** ZCode: zapis do cache Z.ai po stawce wejścia (`zcode/src/parser.rs:84-97`). Dziś jest za $0, bo LiteLLM podaje 0. Dostawcy spoza Z.ai, z ID w postaci UUID, mają cenę tylko z nadpisań (`parser.rs:167-213`).
- [x] 11.6.4 **W** dsh: kandydaci `deepseek/<model>` i hosta, bezpośrednie rozliczenie DeepSeek z harmonogramem z 11.1.3 (`dsh/src/parser.rs:556-568`). Deduplikacja: więcej tokenów, potem wcześniejszy timestamp; `seq` może być tekstem (`parser.rs:475-499`).
- [x] 11.6.5 **Ś** Goose (`provider_name`), Cline (`providerId`/`provider`), Codebuff (`infer_provider`, trasa zewnętrzna), Hermes (`billing_provider`), OpenClaw (`provider`) i Antigravity (pole 7 `ModelInfo` / pole 6 `ModelUsage`, Vertex) — zależne od 11.1.7.
- [x] 11.6.6 **Ś** OpenClaw: wiersze SQLite mają pierwszeństwo przed zmigrowanym JSONL (poprawiony koszt); deduplikacja SQLite po `(sesja, id zdarzenia)` (`openclaw/src/loader.rs:161-185`).
- [x] 11.6.7 **Ś** pi:
  - `piAgentDir` (czyta `<katalog>/sessions`; `pi/src/paths.rs:9-20`);
  - nazwane magazyny `pi.stores` z etykietą `[nazwa]` i kontrolą nakładania się ścieżek (`ccusage-config/src/config/pi_stores.rs`);
  - fork szukany we wszystkich magazynach i liczony przy każdym odczycie (`pi/src/loader.rs:337-354`).

  UI: Ustawienia → pi → lista magazynów.
- [x] 11.6.8 **Ś** Kredyty Amp i Codebuff: są w ledgerze (`UsageEntry.Credits`), ale nic ich nie pokazuje. UI: kolumna „Kredyty” w Raportach i Sesjach (gdy niezerowe), suma w karcie źródła na zakładce Dziś.
- [x] 11.6.9 **N** Antigravity: scalanie zdarzeń z wielu baz według dowolnego wspólnego identyfikatora, z większą wartością każdego pola (`antigravity/src/loader.rs:38`). Antigravity i Kimi: najpierw dokładna surowa nazwa modelu.
- [x] 11.6.10 **N** Drobne: Qwen `candidatesTokenCount: null` = 0 (`qwen/src/parser.rs:65`); Droid przy duplikatach zostawia najnowszy snapshot (`droid/src/loader.rs:27-29`); `GROK_HOME` jako pojedyncza ścieżka.
- [x] 11.6.11 **N** Świadome odstępstwa opisane w „Fazie 9” (zachowanie OnionLLM jest sensowne, ale inne niż w ccusage):
  - projekt z `cwd` lub katalogu, zamiast stałej nazwy źródła (Cline, Amp, Gemini, Grok, Hermes, ZCode, dsh, OpenClaw, Droid, Codebuff, Goose);
  - Codebuff odejmuje cache od wejścia;
  - tokeny thinking wliczone do Output (Gemini, Qwen, Antigravity, Droid);
  - luźniejsze parsowanie liczb i dat;
  - deduplikacja Gemini między plikami `.json` i `.jsonl`.

### 11.7 UI — odpowiedniki funkcji ccusage

- [x] 11.7.1 Raporty: znaczniki modeli bez ceny (`*`) i szacowanych (`~`) w wierszach, podziale i sumie; lista tych modeli w stopce.
- [x] 11.7.2 Raporty: podział każdego okresu na źródła (odpowiednik `--by-agent`; `SourceBreakdowns` już istnieje w Core).
- [x] 11.7.3 Ustawienia → Diagnostyka: rozbieżności między kosztem zapisanym przez narzędzie a wyliczonym z cennika, z przykładami (odpowiednik `--debug` / `--debug-samples` w ccusage). W szczegółach wiersza: część kosztu zapisana i część wyliczona (`storedCost`).
- [x] 11.7.4 Szczegóły wiersza w Raportach i Sesjach: tokeny zapisu do cache 1 h osobno (`cacheCreation1hTokens`).
- [x] 11.7.5 Ustawienia per źródło: Codex — poziom szybkości (11.4.3); OpenCode — `claude-agent` (11.5.1); pi — `piAgentDir` i magazyny (11.6.7). Każde źródło: opcjonalny tryb offline cennika. Odpowiednik sekcji `<źródło>.defaults` w configu ccusage.
  - Tryb offline per źródło świadomie pominięty (jeden cennik dla całej aplikacji). Zapis ustawień nie gubi już ustawień źródeł; magazyny pi walidowane przed zapisem.
- [x] 11.7.6 Zapamiętywanie stanu widoków (okres, zakres, źródło, podział, sortowanie) między uruchomieniami. W aplikacji z UI zastępuje to sekcje `commands.<raport>` z configu ccusage.
- [x] 11.7.7 Widżet: znacznik `*`, gdy koszt dzisiejszy zawiera modele bez ceny.
- [ ] 11.7.8 Uruchomienie i przegląd GUI oraz widżetu na żywo (przeniesione z „Pozostało”).

### 11.8 CLI i eksport (drugorzędne)

- [x] 11.8.1 JSON zgodny z ccusage — dotyczy też eksportu z UI:
  - `totals.unpricedModels` zamiast pola w obiekcie głównym;
  - `missingPricing` zamiast `unpriced`;
  - `storedCost`, `cacheCreation1hTokens`, `estimatedModels` / `estimatedPricing`;
  - w raportach zbiorczych `period`, `agent`, `metadata.agents`.

  Do decyzji: pełna zgodność albo opisany format własny.
  - Wybrano zgodność z ccusage (CLI `--json` i eksport z aplikacji); dodatkowe pola OnionLLM: `requests`, `credits`, `reasoningOutputTokens`, `source`/`project` w sesjach.
- [x] 11.8.2 Brakujące opcje:
  - `--sections`, `--by-agent`;
  - `--speed` (Codex), `--include-claude-agent`, `--pi-agent-dir`;
  - `--color` / `--no-color`;
  - skróty `-w`, `-B`, `-n` (zamiast `-l`), `-v`.

  `--compact` jest parsowane, ale ignorowane.
  - Dodane wszystkie powyższe oraz `--pi-path`, `--jq`; `--compact` działa; `-l` zostaje jako stary alias `-n`.
- [x] 11.8.3 `--debug` ma inne znaczenie niż w ccusage (zob. 6.6). Teraz: diagnostyka parsowania to `--diagnostics`, a `--debug`/`--debug-samples N` pokazuje rozbieżności cen. Pozostawić diagnostykę parsowania pod inną nazwą, a `--debug` / `--debug-samples` przeznaczyć na rozbieżności cen (logika wspólna z 11.7.3).
- [x] 11.8.4 Statusline:
  - cache osobny dla każdej sesji, zamiast jednego pliku;
  - blokada `isUpdating` + PID przed równoległym przeliczaniem;
  - po błędzie wyświetlenie ostatniego wyniku;
  - opcja `--no-cache`;
  - `modelLabelAliases`;
  - `N/A` zamiast `$0.00`, gdy brak kosztu z Claude Code (`claude/src/commands/statusline.rs`, `statusline/cache.rs`).
- [x] 11.8.5 `codex statusline` w trybie podsumowania (`--id`, `--json`, `--jq`). Tryb MCP nadal pominięty (10.2). Działa dla każdego źródła: `onionllm <źródło> statusline`.

### 11.9 Po wdrożeniu — ograniczenia i drobne luki

Fala 1 była realizowana równolegle przez agentów w izolowanych kopiach; poniżej to, czego nie przeniesiono, oraz kompromisy.

- [ ] 11.9.1 **N** Codex: parowanie duplikatów według głębokości forka (ccusage #2208/#2366); kilka katalogów `CODEX_HOME` z różnymi poziomami w `config.toml` (wpis ma jeden poziom); linie JSON z escapowanymi kluczami.
- [x] 11.9.2 **N** Copilot OTel: model i kontekst sesji ze śladu (trace) tylko z tego samego pliku; reszta z `total_tokens` nierozdzielana.
  - Jak ccusage `read_otel_calls` + `reconcile_otel_calls` (`otel.rs:183-311`): parser zwraca każde wywołanie pliku, a w `SourceData` zapisuje rodzaj rekordu, trace id, kontekst trace swojego pliku (model, sesja z priorytetem — z każdego rekordu zużycia, także niebędącego wywołaniem) i resztę z `total_tokens`. `CopilotSource.Reconcile` (`ResolveOtelRows`) na wpisach ze wszystkich plików: łączy kontekst trace (model i sesja dla rekordów bez własnych), kopie jednej tożsamości (najwcześniejsza + identyfikatory żądania z kopii), ranking rodzajów per trace / response id (np. `invoke_agent` i span chat tego samego trace w różnych plikach nie są już liczone podwójnie) i powtórzone spany chat. Klucze wywołań jak `CallKey` ccusage (`span:`/`log:`/`agent-turn:`); wywołania bez trace/span id z różnych plików nie są już sklejane. Parser w wersji 6.
  - Reszta z `total_tokens` (#2492): w rekordzie bez `output_tokens` jest wyjściem tylko po potwierdzeniu przez kopię o tych samych licznikach (wcześniej kopia częściowa wygrywała z wyjściem 0, a ponowny eksport częściowy liczył się podwójnie); z reszty rozliczane są tokeny reasoning (dodane do `OutputTokens`, jak `loader.rs:173`); rekord z samym `total_tokens` jest wywołaniem. Nie przeniesiono: pozostała niesklasyfikowana reszta (ccusage `extra_total_tokens`, tylko w sumie tokenów raportu) — `UsageEntry` nie ma na nią pola. Przy sprzecznych modelach jednego trace w różnych plikach pierwszeństwo ma plik rekordu (ccusage: kolejność ścieżek); kontekst z rekordu, który nie jest wywołaniem, przechodzi do innych plików tylko przez wpisy tego samego trace z jego pliku. 17 testów (scenariusze ccusage w jednym pliku i rozbite na pliki).
- [ ] 11.9.3 **Ś** Claude: dopasowanie kopii między plikami (`Reconcile`) działa na wpisach z wczytanego zakresu dat — rodzic spoza zakresu nie jest dopasowany. Pełna historia (jak Copilot, `ReconcileNeedsFullHistory`) celowo wyłączona ze względu na wydajność; do zmierzenia.
- [ ] 11.9.4 **N** Cennik: przebiegi dopasowania z rozróżnianiem wielkości liter z ccusage nie są przeniesione (wyszukiwanie zawsze bez rozróżniania).
- [ ] 11.9.5 **N** Wiersze Qwen `-thinking` (wymagają rozpoznania trybu thinking w źródłach).
- [x] 11.9.6 Kontrakt Core: `DedupeKey` w części źródeł (Claude, Copilot, Antigravity, OpenClaw, Kimi) przenosi dane pomocnicze po separatorze U+001F — nie parsować ani nie wyświetlać poza źródłem. Rozważyć osobne pole zamiast kodowania w kluczu.
  - Nowe pole `UsageEntry.SourceData` (kolumna `source_data`, ledger schemat 5). `DedupeKey` to sama tożsamość; dane pomocnicze są w `SourceData`: Claude — flagi sidechain i podziału cache, Copilot — identyfikatory żądania, liczba żądań, surowy model, Antigravity — ranga czasu, numer API, nazwa z logu i identyfikatory, OpenClaw — koszt i id zdarzenia, Kimi — surowy model. `Deduplicator` łączy od razu tylko wpisy o równych `DedupeKey` i `SourceData` (jak dotąd pełny klucz), więc wyniki się nie zmieniły. Statusline nie parsuje już klucza (`ClaudeSource.IsAdvisor`). Wersje parserów tych pięciu źródeł podniesione. Sprawdzone na prawdziwych danych: raport miesięczny i 78 sesji identyczne jak przed zmianą.

---

## Pozostało / znane ograniczenia

- [x] Codex: `token_usage_record` / kompakcja, forki (pomijanie historii rodzica), rollouty `.jsonl.zst` — scenariusze przeniesione z testów ccusage.
- [x] Copilot: eksport OTel (span chat > log inferencji > tura agenta > invoke_agent; poziom szczegółowości 1 — ustępuje bazie).
- [x] Deduplikacja międzyźródłowa OpenCode `claude-agent` ↔ Claude Code (`UsageEntry.MirrorOf`, migracja kolumny `mirror_of` w ledgerze).
- [x] Aliasy cen zależne od daty (`kimi-for-coding` → K2.5/K2.6, `codex-auto-review`) + dopasowanie kluczy z prefiksem dostawcy (`kimi-k3` → `moonshot/kimi-k3`).
- [x] Testy CLI: parser argumentów + end-to-end (JSON/CSV/tabela/sesje/statusline) w izolowanym katalogu `ONIONLLM_DATA_DIR`.
- [ ] Uruchomienie i przegląd GUI/widżetu na żywo — wersja niespakowana zbudowana poprawnie; uruchomienie okna i zrzuty ekranu z sesji agenta zablokowane uprawnieniami → do sprawdzenia ręcznie (Visual Studio, F5). Śledzone jako 11.7.8.
- [ ] Luki względem ccusage znalezione w audycie 2026-11-25 — zob. [Faza 11](#faza-11--luki-względem-ccusage-audyt-2026-11-25).
- [-] Statusline Codex przez MCP, telemetria — świadomie pominięte (10.2, 10.3).

## Dziennik postępu

| Data | Faza / punkt | Co zrobiono | Uwagi |
|---|---|---|---|
| 2026-11-24 | — | Analiza ccusage i OnionLLM, utworzenie planu | Build bazowy OK (0 błędów) |
| 2026-11-24 | 0.1–0.2 | Kopia zapasowa, projekt `OnionLLM.Core` | Zapis do `.backup/` w repo zablokowany przez uprawnienia → archiwum w scratchpadzie sesji |
| 2026-11-24 | 1.1–1.5 | `UsageEntry`, `IUsageSource`, `ClaudeSource`, `CodexSource`, `CopilotSource`, `UsageLedger` (SQLite, przyrostowo), `Deduplicator` | Pełna synchronizacja 130 MB logów Claude + Copilot: ~3 s; kolejne: tylko zmienione pliki |
| 2026-11-24 | 2.1–2.2 | `PricingCatalog` (snapshot LiteLLM 541 modeli wbudowany, cache online 24 h, nadpisania), `CostCalculator` (tryby auto/calculate/display, długi kontekst, cache 1 h, fast/flex) | — |
| 2026-11-24 | 3, 4 | `ReportBuilder` (daily/weekly/monthly/session, projekty, aliasy, `--last`), `BlockBuilder` (bloki, przerwy, burn rate, projekcja, limit max) | — |
| 2026-11-24 | 6 (część) | `OnionLLM.Cli` (`onionllm`): daily/weekly/monthly/session/blocks/statusline/sources/sync/pricing, `--json/--csv`, tabele | — |
| 2026-11-24 | Weryfikacja | Claude dziś: OnionLLM vs niezależny skrypt Python (deduplikacja `message.id+requestId`) — **identyczne** tokeny (924 / 459 444 / 1 146 906 / 80 156 484). Copilot: miesiące z samą bazą zgodne co do tokena; sesje tylko z `events.jsonl` doliczone, sesja obecna w obu źródłach liczona raz | Stary OnionLLM pokazywał dziś 1 337 373 tokenów wyjścia Claude zamiast 459 444 (~2,9× za dużo, B1) |
| 2026-11-24 | 0.3–0.5, testy | `OnionLLM.Core.Tests` (xUnit): parsery, deduplikacja, koszty, raporty, bloki, ledger, konfiguracja, dashboard | 41 testów własnych, wszystkie zielone |
| 2026-11-24 | 5 | Plik konfiguracji warstwowy (`%APPDATA%\OnionLLM\config.json` → `.onionllm.json` → `--config`), komentarze/trailing commas, walidacja, schemat `onionllm.schema.json` | — |
| 2026-11-24 | 7 | GUI: zakładki Dziś (koszt, cache, modele, aktywny blok, burn rate, limity), Raporty (wykres, zakresy, podział, eksport JSON/CSV), Sesje (wyszukiwanie, sortowanie, szczegóły), Bloki, Ustawienia (+ diagnostyka, odświeżanie cennika, przebudowa bazy) | Build Windows bez ostrzeżeń; GUI nie było uruchamiane w tej sesji (pakiet MSIX — uruchom z Visual Studio) |
| 2026-11-24 | 8 | Widżet na `UsageEngine`: koszt dziś, aktywny blok + burn rate, źródła wg kosztu, rozmiar medium/large | — |
| 2026-11-24 | 9 (część) | Źródła Cline, Amp, Droid, Codebuff, Goose (+19 testów) — zaimplementowane równolegle przez agenta, zarejestrowane | 60/60 testów |
| 2026-11-24 | 10.1 | `README.md` (CLI, statusline, konfiguracja) | — |
| 2026-11-24 | 9 (część) | Źródła Gemini CLI, Qwen Code, OpenCode, Kilo Code (+31 testów), zarejestrowane. Kilo zweryfikowane na prawdziwej `kilo.db` (16 wpisów, 0 pominiętych) | 91/91 testów |
| 2026-11-24 | 9 | Źródła Kimi, pi-agent, OpenClaw, Hermes, Antigravity (SQLite + protobuf), Grok Build, ZCode, DeepSeek Harness (zstd) (+39 testów), zarejestrowane — **Faza 9 zakończona: 20 źródeł** | 130/130 testów; wszystkie projekty budują się bez ostrzeżeń. Źródła bez danych na tej maszynie sprawdzone tylko na syntetycznych fixturach |
| 2026-11-24 | Pozostało | Codex: rekordy odpowiedzi + kompakcja, filtr replay forków, `.jsonl.zst`; Copilot OTel; poziomy szczegółowości sesji (`SessionTier` 0/1/2); `MirrorOf` OpenCode↔Claude (migracja ledgera do schematu 2, sprawdzona na prawdziwej bazie); aliasy cen wg daty i klucze z prefiksem dostawcy; tryb przenośny `ONIONLLM_DATA_DIR`; testy CLI end-to-end | 152/152 testów. Wyniki Claude/Copilot na prawdziwych danych bez zmian po migracji |
| 2026-11-24 | GUI | Build niespakowany (`WindowsPackageType=None`) OK; uruchomienie okna + UI Automation/zrzuty zablokowane przez uprawnienia sesji | Do weryfikacji ręcznej |
| 2026-11-25 | Audyt | Porównanie z ccusage plik po pliku (20 adapterów, cennik, CLI, config, JSON, statusline) → Faza 11 | Plan zawyżał stopień ukończenia; luki W zmieniają tokeny i koszty |
| 2026-11-25 | 11 — fala 0 | Wspólny kontrakt Core: `UsageEntry.Provider/Preference`, `SpeedTier.Ultrafast`, hooki źródeł (`PricingCandidates`, `AdjustPricing`, `DuplicateComparer`, `Reconcile`, `ApplySettings`, `ReconcileNeedsFullHistory`, `ConfigurationProblems`, `IsSessionReportOnly`), `ProviderNames`, ledger schemat 4, lustra w widoku jednego źródła, testy rozdzielone na pliki per klasa | Kopia zapasowa przed zmianami w scratchpadzie sesji |
| 2026-11-25 | 11 — fala 1 | Siedem równoległych gałęzi (cennik, Claude, Copilot, Codex, dwie grupy źródeł, UI/eksport/CLI), scalone bez konfliktów | 510/510 testów; całe rozwiązanie z MAUI i widżetem się buduje |
| 2026-11-25 | Weryfikacja | Prawdziwe dane: październik Claude −35 mln tokenów (−$30) względem wersji sprzed zmian — 538 rekordów bez `requestId` (bloki jednej odpowiedzi) stara wersja liczyła wielokrotnie (11.2.3). Listopad bez zmian poza bieżącym użyciem; brak modeli bez ceny w Claude i Copilocie | Porównanie ze starą wersją zbudowaną z kopii zapasowej, osobny katalog danych |
| 2026-11-25 | 11 — fala 2 | UI cennika: sekcja „Cennik” (LiteLLM + models.dev), edytory aliasów modeli, nadpisań cen i etykiet statusline; logika w `Config/ConfigTextFormats.cs` | 531/531 testów; GUI nieoglądane na żywo (11.7.8) |
| 2026-11-25 | 11.1.1, 11.1.2 | `BuiltinPricing` (starsze Claude i inne modele bez wiersza w LiteLLM); `UsageEntry.SpansMultipleRequests` + kolumna `aggregate` (ledger schemat 3, Grok parser v2) — bez progu długiego kontekstu dla sum wielu żądań | 162/162 testów; całe rozwiązanie z MAUI i widżetem się buduje; migracja sprawdzona na prawdziwej bazie |
