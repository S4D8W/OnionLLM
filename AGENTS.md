# AGENTS.md — OnionLLM

Instrukcje dla agentów AI pracujących w tym repozytorium.

## Projekt

OnionLLM to lokalny monitor tokenów, kosztów i limitów 20 narzędzi AI do programowania (Claude Code, Codex, GitHub Copilot, Gemini CLI, …).

| Projekt | Ścieżka | Rola |
| --- | --- | --- |
| Aplikacja (.NET MAUI, Windows + macOS) | `OnionLLM/` | UI — powierzchnia podstawowa |
| Rdzeń | `OnionLLM.Core/` | źródła, cennik, raporty, bloki, limity, `UsageEngine` |
| CLI | `OnionLLM.Cli/` | raporty zgodne z ccusage, statusline Claude Code |
| Testy | `OnionLLM.Core.Tests/` | testy rdzenia |

Plan prac: [PLAN_IMPLEMENTACJI.md](PLAN_IMPLEMENTACJI.md).

## Zasada UI-first

Aplikacja jest powierzchnią podstawową, CLI — drugorzędną. **Każda funkcja dodana w `OnionLLM.Core` MUSI mieć swoje miejsce w UI** (ekran, panel szczegółów, ustawienie lub polecenie palety). Miejsce to opisuje [DESIGN.md §6](DESIGN.md#6-ekrany).

## Praca nad UI — obowiązkowo

1. **Przed każdą zmianą w UI przeczytaj [DESIGN.md](DESIGN.md)** — co najmniej §1, §2, §3, §10, a dla ekranów/komponentów odpowiednie rozdziały §4–§9 (kolejność w [§0.1](DESIGN.md#01-kolejność-czytania)). DESIGN.md opisuje stan **docelowy**; jeśli kod jest z nim niezgodny, poprawiamy kod.
2. **Żadnych literałów wizualnych** (hex, `FontSize`, `Margin`, `Padding`, `CornerRadius`, czasy animacji) poza `OnionLLM/Resources/Styles/Tokens.*.xaml`. Tylko tokeny i style ([DESIGN.md §3](DESIGN.md#3-implementacja-tokenów-w-maui)).
3. **Tylko komponenty z rejestru** ([DESIGN.md §11.3](DESIGN.md#113-rejestr-komponentów)). Nowy komponent = najpierw specyfikacja w DESIGN.md §4.
4. **Aktualizuj DESIGN.md w tym samym PR**, gdy zmieniasz tokeny, komponenty, wzorce, ekrany, etykiety, skróty lub status funkcji.
5. **Wypełnij checklistę PR** z [DESIGN.md §10.3](DESIGN.md#103-checklista-pr-ui) (oba motywy, obie gęstości, trzy breakpointy, klawiatura, czytnik ekranu, stany, zrzuty ekranu).
6. **Teksty UI po polsku**, zgodnie z [DESIGN.md §8](DESIGN.md#8-treści-i-ux-writing) (ton, odmiana liczebników, słownik etykiet). Kod, identyfikatory i komunikaty CLI — po angielsku.
7. **Nie udawaj danych.** Funkcje oznaczone w DESIGN.md jako `Wymaga danych` wymagają najpierw warstwy danych w Core ([§11.4](DESIGN.md#114-funkcje-wymagające-nowych-danych)).
