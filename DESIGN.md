# OnionLLM — Design System i specyfikacja UX/UI

> **Dla kogo:** agenci i ludzie, którzy projektują lub implementują UI aplikacji OnionLLM (MAUI, desktop Windows + macOS).
> **Status dokumentu:** obowiązujący, docelowy. Opisuje, jak aplikacja **ma** wyglądać i się zachowywać — nie jak wygląda dziś. Jeśli kod jest niezgodny z tym dokumentem, rację ma dokument, a kod jest do poprawy.
> **Wersja:** 1.7 · 2026-11-26

## Spis treści

- [0. Jak używać tego dokumentu](#0-jak-używać-tego-dokumentu)
- [1. Produkt i zasady projektowe](#1-produkt-i-zasady-projektowe)
- [2. Fundamenty — tokeny](#2-fundamenty--tokeny)
- [3. Implementacja tokenów w MAUI](#3-implementacja-tokenów-w-maui)
- [4. Komponenty](#4-komponenty)
- [5. Wzorce](#5-wzorce)
- [6. Ekrany](#6-ekrany)
- [7. Powierzchnie systemowe](#7-powierzchnie-systemowe)
- [8. Treści i UX writing](#8-treści-i-ux-writing)
- [9. Dostępność](#9-dostępność)
- [10. Zasady dla agentów i checklista PR](#10-zasady-dla-agentów-i-checklista-pr)
- [11. Załączniki](#11-załączniki)

---

## 0. Jak używać tego dokumentu

### 0.1 Kolejność czytania

| Zadanie | Przeczytaj obowiązkowo |
| --- | --- |
| Dowolna zmiana w UI | §1, §2, §3, §10 |
| Nowy lub zmieniany komponent | + §4 (cały rozdział danego komponentu), §9 |
| Nowy lub zmieniany ekran | + §5, §6 (dany ekran i [§6.0](#60-wspólna-macierz-stanów)), §8 |
| Tray, okno, powiadomienia, onboarding | + §7 |
| Teksty, etykiety, komunikaty | + §8 |

### 0.2 Słowa kluczowe

- **MUSI / NIE WOLNO** — wymaganie bezwzględne. Złamanie = błąd do poprawy przed scaleniem.
- **POWINIEN / NIE POWINIEN** — domyślna reguła; odstępstwo wymaga uzasadnienia w opisie PR.
- **MOŻE** — opcja dopuszczalna.

### 0.3 Statusy funkcji

Każda funkcja, ekran lub element ma jeden ze statusów:

| Status | Znaczenie |
| --- | --- |
| `Docelowe` | Dane istnieją w `OnionLLM.Core` / ViewModelach — do zbudowania tylko w UI. |
| `Wymaga danych` | Potrzebne są nowe dane, ustawienia lub logika w Core (lista w [§11.4](#114-funkcje-wymagające-nowych-danych)). Agent UI MUSI najpierw dostarczyć lub zlecić warstwę danych; NIE WOLNO udawać danych w UI. |
| `Wymaga platformy` | Potrzebny kod platformowy (Windows/macOS), np. tray, powiadomienia, skróty globalne. |

Element bez oznaczenia statusu ma status `Docelowe`.

### 0.4 Konwencje zapisu

- Wymiary w **jednostkach niezależnych od urządzenia (dp)** MAUI. „px” w tym dokumencie = dp.
- Kolory w formacie `#RRGGBB` lub `#AARRGGBB` (MAUI: alfa na początku).
- Tokeny zapisujemy notacją kropkową (`color.bg.canvas`); w XAML mają klucz PascalCase (`ColorBgCanvas`) — reguła w [§3.3](#33-nazewnictwo-kluczy).
- **Skrót kolorów:** w opisach komponentów i ekranów prefiks `color.` jest pomijany — `surface.1` = `color.surface.1`, `text.secondary` = `color.text.secondary`, `status.warning` = `color.status.warning`, `data.cost` = `color.data.cost` itd.
- Etykiety UI cytujemy dokładnie w cudzysłowach „…”. Taki tekst jest wiążący (słownik w [§8.6](#86-słownik-etykiet)).

---

## 1. Produkt i zasady projektowe

### 1.1 Czym jest OnionLLM

OnionLLM to lokalny monitor zużycia narzędzi AI do programowania. Odczytuje logi 20 narzędzi (Claude Code, Codex, GitHub Copilot, Gemini CLI, …), liczy tokeny i koszty, odtwarza 5-godzinne bloki rozliczeniowe Claude i pokazuje limity. Dane trafiają do lokalnej bazy (`ledger.db`), więc historia przetrwa czyszczenie logów przez narzędzia. Obok aplikacji istnieje CLI zgodne z ccusage — **aplikacja jest jednak powierzchnią podstawową**: każda funkcja produktu MUSI być dostępna w UI.

**Jedno pytanie, na które odpowiada produkt:**

> „Ile tokenów zużyłem, ile to kosztowało i jak blisko jestem limitów?” — dla dnia, tygodnia, miesiąca, sesji, modelu, projektu i narzędzia.

### 1.2 Użytkownik

**Persona główna — „Developer wieloagentowy”**

- Programista, który na co dzień używa 2–5 agentów AI jednocześnie (np. Claude Code + Codex + Copilot).
- Płaci abonamentami (limity 5 h / 7 dni) i/lub za API (koszt per token).
- Ma aplikację otwartą w tle cały dzień; zerka na nią między zadaniami (sekundy, nie minuty).
- Biegle korzysta z klawiatury, ceni gęstość informacji, nie toleruje „marketingowego” UI.
- Pracuje na Windows lub macOS, często na dwóch monitorach, z oknem aplikacji przy krawędzi ekranu.

**Główne zadania (Jobs to be done)**

| # | Zadanie | Częstotliwość | Gdzie |
| --- | --- | --- | --- |
| J1 | Sprawdzić, ile dziś wydałem i czy tempo jest normalne | wiele razy dziennie | Dziś, tray, mini-widok |
| J2 | Sprawdzić, ile zostało do limitu 5 h / 7 dni i kiedy reset | wiele razy dziennie | Dziś, Limity, mini-widok |
| J3 | Zrozumieć, które narzędzie/model/projekt generuje koszt | kilka razy w tygodniu | Raporty, Projekty, Modele |
| J4 | Znaleźć konkretną sesję i jej koszt | okazjonalnie | Sesje |
| J5 | Zostać ostrzeżonym, zanim przekroczę limit lub budżet | pasywnie | powiadomienia |
| J6 | Wyeksportować dane do rozliczenia | raz w miesiącu | Raporty, Sesje |
| J7 | Naprawić brak danych (źródło niewykryte, błędy parsowania) | rzadko | Źródła, Ustawienia → Diagnostyka |

### 1.3 Charakter

**Dashboard premium.** Punkt odniesienia: Linear, Vercel, Raycast. W praktyce oznacza to:

- **Liczby są bohaterami.** Duże, ostre cyfry tabelaryczne; etykiety drugoplanowe.
- **Precyzja zamiast dekoracji.** Cienkie obrysy 1 px, spokojne tła, jeden wyrazisty kolor marki.
- **Głębia przez warstwy, nie przez cienie.** Powierzchnie różnią się tonem; cień tylko tam, gdzie coś unosi się nad treścią.
- **Ruch jest bogaty, ale zawsze coś znaczy** (zmiana danych, hierarchia, przyczyna-skutek). Patrz [§2.7](#27-ruch).
- **Motyw marki — cebula.** Koncentryczne warstwy to sygnatura wizualna: logo, pierścienie limitów (komponent `OnionRings`), skeletony i stany puste.

### 1.4 Zasady projektowe

1. **Liczby najpierw.** Każdy ekran odpowiada na swoje główne pytanie w ciągu 2 sekund. Najważniejsza liczba jest największym elementem na ekranie.
2. **Uczciwość danych.** Nigdy nie ukrywamy niepewności. Koszt bez ceny (`*`), koszt szacowany (`~`), wartość przeliczona na inną walutę (`≈`), dane częściowe i nieaktualne MUSZĄ być widoczne przy liczbie, której dotyczą, z wyjaśnieniem w tooltipie.
3. **Spokój, ale bez ukrywania alarmów.** W stanie normalnym UI jest stonowany (neutralne tła, kolor tylko w danych). Kolor ostrzeżenia lub błędu pojawia się wyłącznie, gdy coś naprawdę wymaga uwagi — wtedy jest jednoznaczny i opisany słowem.
4. **Klawiatura na równi z myszą.** Każda akcja jest osiągalna klawiaturą; najczęstsze mają skróty; Ctrl+K (⌘K) prowadzi wszędzie.
5. **Jeden system.** Żadnych jednorazowych kolorów, rozmiarów ani komponentów. Wszystko pochodzi z tokenów (§2) i rejestru komponentów (§4). Nowa potrzeba = nowy token/komponent opisany w tym dokumencie.
6. **Lokalność widać.** Użytkownik zawsze wie, skąd są dane (lokalne logi), kiedy były odświeżone i czy coś opuszczono.

### 1.5 Antywzorce (NIE WOLNO)

- Tekst mniejszy niż 11 px.
- Kolor jako jedyny nośnik informacji (np. tylko czerwona liczba bez słowa/ikony).
- Okna `DisplayAlert` do pokazywania danych — szczegóły zawsze w Drawerze (§4.6.1).
- Wartości wpisane na sztywno w XAML/C# (hex, `FontSize`, `Margin`, `Padding`, `CornerRadius`) — tylko tokeny.
- Fonty lub ikony systemowe zależne od platformy (Segoe MDL2, Segoe UI Variable, Consolas, SF Symbols).
- Spinner na całym ekranie, gdy są już jakieś dane do pokazania.
- Emoji jako ikony (🔥, ●, ▸) — używamy ikon Lucide.
- Kolor marki na dużych powierzchniach tła (poza hero i logo).
- Modalne potwierdzenia dla akcji odwracalnych.

---

## 2. Fundamenty — tokeny

Tokeny są jedynym źródłem wartości wizualnych. Mają trzy warstwy:

1. **Prymitywy** (`palette.*`) — surowe skale kolorów. NIE WOLNO używać ich bezpośrednio w ekranach i komponentach.
2. **Semantyczne** (`color.*`, `type.*`, `space.*`, …) — znaczenie („tło karty”, „tekst drugorzędny”). Tego używamy.
3. **Komponentowe** (`comp.<komponent>.*`) — tylko gdy komponent potrzebuje wartości, której nie ma w warstwie semantycznej. Zawsze aliasują token semantyczny.

### 2.1 Kolor

#### 2.1.1 Skala marki — „czerwona cebula” (`palette.onion.*`)

Fiolet-magenta przekroju czerwonej cebuli. Kolor marki i akcji podstawowych.

| Token | Hex | Kontrast na `#FFFFFF` | Kontrast na `#16151C` | Zastosowanie |
| --- | --- | --- | --- | --- |
| `palette.onion.50` | `#FDF2F8` | 1.09 | 16.61 | tło zaznaczenia (light) |
| `palette.onion.100` | `#FBE4F0` | 1.20 | 15.07 | tło nav aktywnej (light), tinty |
| `palette.onion.200` | `#F5C6DF` | 1.50 | 12.11 | obrys zaznaczenia (light) |
| `palette.onion.300` | `#EB9BC6` | 2.09 | 8.67 | **tekst/link/koszt w dark**, focus w dark |
| `palette.onion.400` | `#DA6AA7` | 3.18 | 5.70 | **akcja primary w dark** |
| `palette.onion.500` | `#C0448A` | 4.73 | 3.83 | wykres — seria marki (light) |
| `palette.onion.600` | `#A23272` | 6.49 | 2.79 | **akcja primary w light**, koszt w light, focus w light |
| `palette.onion.700` | `#86295E` | 8.43 | 2.15 | hover akcji (light) |
| `palette.onion.800` | `#6A214B` | 10.87 | 1.67 | pressed akcji (light), gradient hero |
| `palette.onion.900` | `#4E1937` | 13.83 | 1.31 | tło nav aktywnej (dark, z alfą), gradient hero |
| `palette.onion.950` | `#2E0D20` | 17.58 | 1.03 | gradient hero (początek) |

#### 2.1.2 Akcent — „złota łuska” (`palette.husk.*`)

Ciepły akcent łuski cebuli. **Wyłącznie dekoracyjny i wyróżniający** (logo, rekord okresu, wyróżnienie „najwyższy koszt”, wskaźnik nowości). NIE WOLNO używać go do komunikowania ostrzeżeń — od tego jest `color.status.warning`.

| Token | Hex | Na `#FFFFFF` | Na `#16151C` |
| --- | --- | --- | --- |
| `palette.husk.100` | `#FDF3D7` | 1.11 | 16.39 |
| `palette.husk.300` | `#F7D98A` | 1.38 | 13.16 |
| `palette.husk.400` | `#F2C14E` | 1.68 | 10.80 |
| `palette.husk.500` | `#D9A21B` | 2.30 | 7.89 |
| `palette.husk.600` | `#A87A0C` | 3.85 | 4.71 |
| `palette.husk.700` | `#7D5A07` | 6.29 | 2.88 |

#### 2.1.3 Neutralne (wynikowe wartości semantyczne)

Neutralne mają lekki chłodno-śliwkowy odcień (nie czysta szarość), żeby harmonizowały z marką.

#### 2.1.4 Kolory semantyczne

Każdy token ma wartość dla motywu jasnego (L) i ciemnego (D). Wszystkie pary tekst/tło zostały zweryfikowane (WCAG 2.2: tekst ≥ 4.5:1, elementy graficzne ≥ 3:1) — tabela w [§2.1.9](#219-tabela-kontrastów).

**Tła i powierzchnie**

| Token | L | D | Zastosowanie |
| --- | --- | --- | --- |
| `color.bg.canvas` | `#F7F7F9` | `#0D0C11` | tło okna, obszar treści |
| `color.bg.sidebar` | `#F2F1F5` | `#121117` | tło sidebaru |
| `color.surface.1` | `#FFFFFF` | `#16151C` | karty, tabele, drawer |
| `color.surface.2` | `#F3F2F6` | `#1D1C24` | zagnieżdżone kafle, nagłówek tabeli, pola formularzy, hover wiersza |
| `color.surface.3` | `#EAE9EF` | `#26252E` | elementy na `surface.2`, pressed, tor przełącznika |
| `color.surface.overlay` | `#FFFFFF` | `#1D1C24` | menu, popover, paleta poleceń, tooltip, dialog |
| `color.surface.inverse` | `#16151B` | `#F3F2F7` | toast, tooltip (wariant inverse) |
| `color.scrim` | `#6616151B` | `#99000000` | przyciemnienie pod dialogiem i drawerem (overlay) |

**Obrysy**

| Token | L | D | Zastosowanie |
| --- | --- | --- | --- |
| `color.border.subtle` | `#ECEBF0` | `#24232B` | dzielniki wierszy, separatory wewnątrz kart |
| `color.border.default` | `#E3E2E8` | `#2D2C36` | obrys kart, tabel, sidebaru |
| `color.border.strong` | `#85828F` | `#6E6B7B` | obrys pól formularzy, checkboxów (≥ 3:1) |
| `color.border.focus` | `#A23272` | `#EB9BC6` | pierścień fokusu (2 px) |

**Tekst**

| Token | L | D | Zastosowanie |
| --- | --- | --- | --- |
| `color.text.primary` | `#16151B` | `#F3F2F7` | tekst główny, liczby |
| `color.text.secondary` | `#4F4D59` | `#B9B6C4` | etykiety, opisy |
| `color.text.tertiary` | `#6B6875` | `#918E9E` | podpisy, metadane, placeholder |
| `color.text.disabled` | `#A9A7B2` | `#5C5A68` | tekst nieaktywny (wyjątek od 4.5:1 dopuszczony przez WCAG) |
| `color.text.link` | `#A23272` | `#EB9BC6` | linki |
| `color.text.onAction` | `#FFFFFF` | `#1A0812` | tekst na przycisku primary |
| `color.text.onInverse` | `#F3F2F7` | `#16151B` | tekst na `surface.inverse` |

**Akcja (marka)**

| Token | L | D |
| --- | --- | --- |
| `color.action.primary` | `#A23272` | `#DA6AA7` |
| `color.action.primaryHover` | `#86295E` | `#EB9BC6` |
| `color.action.primaryPressed` | `#6A214B` | `#F5C6DF` |
| `color.action.subtle` (tło aktywnej nawigacji, zaznaczenia) | `#FBE4F0` | `#3A1429` |
| `color.action.subtleBorder` | `#F5C6DF` | `#6A214B` |
| `color.action.onSubtle` (ikona/tekst na `action.subtle`) | `#A23272` | `#EB9BC6` |

`text.primary` na `action.subtle`: 15.1 (L) / 14.4 (D); `action.onSubtle` na `action.subtle`: 5.4 (L) / 7.6 (D).

**Statusy**

| Token | L | D | Tło (`…Bg`) L | Tło (`…Bg`) D |
| --- | --- | --- | --- | --- |
| `color.status.success` | `#117A3C` | `#3DD68C` | `#E7F6EC` | `#10291C` |
| `color.status.warning` | `#A35A00` | `#F5B23D` | `#FDF1DE` | `#2E2210` |
| `color.status.danger` | `#C4262E` | `#FF6B6B` | `#FDECEC` | `#341517` |
| `color.status.info` | `#1F64D1` | `#6BA4FF` | `#E8F0FD` | `#13223B` |

Tekst statusu na tle statusu ma kontrast ≥ 4.5:1 w obu motywach (np. w Bannerze, Badge).

**Burn rate (tempo spalania)**

| Token | L | D | Etykieta |
| --- | --- | --- | --- |
| `color.burn.normal` | `#117A3C` | `#3DD68C` | „normalne” |
| `color.burn.moderate` | `#A35A00` | `#F5B23D` | „umiarkowane” |
| `color.burn.high` | `#C4262E` | `#FF6B6B` | „wysokie” |

**Metryki danych** — stałe kolory wielkości, spójne w całej aplikacji (karty, tabele, wykresy, legendy):

| Token | L | D | Znaczenie |
| --- | --- | --- | --- |
| `color.data.cost` | `#A23272` | `#EB9BC6` | koszt (zawsze kolor marki) |
| `color.data.tokens` | `#0F7C78` | `#4FD1C5` | tokeny łącznie |
| `color.data.input` | `#0F7C78` | `#4FD1C5` | tokeny wejścia |
| `color.data.output` | `#C2410C` | `#FF9A62` | tokeny wyjścia |
| `color.data.cacheWrite` | `#7A4FD6` | `#A98BFF` | zapis cache (5 min i 1 h) |
| `color.data.cacheRead` | `#2667C9` | `#6BA4FF` | odczyt cache |
| `color.data.track` | `#E9E8EE` | `#2A2932` | tor pasków postępu i pierścieni |
| `color.data.grid` | `#ECEBF0` | `#24232B` | linie siatki wykresów |
| `color.data.axis` | `#6B6875` | `#918E9E` | etykiety osi |
| `color.data.other` | `#8F8C9B` | `#6E6B7B` | seria „Pozostałe” |
| `color.data.highlight` | `#A87A0C` | `#F2C14E` | wyróżnienie (rekord, maksimum) — złota łuska |

Wszystkie kolory metryk mają ≥ 4.5:1 na `surface.1` (mogą być tekstem) i ≥ 3:1 na `surface.2` (grafika).

#### 2.1.5 Gradient hero (`color.hero.*`)

Jedyna duża powierzchnia w kolorze marki — karta hero na ekranie Dziś i nagłówek mini-widoku.

| Token | L | D |
| --- | --- | --- |
| `color.hero.start` (0,0) | `#2E0D20` | `#2E0D20` |
| `color.hero.mid` (0.55) | `#6A214B` | `#5A1C40` |
| `color.hero.end` (1,1) | `#86295E` | `#86295E` |
| `color.hero.text` | `#FFFFFF` | `#FFFFFF` |
| `color.hero.textSecondary` | `#F5C6DF` | `#F5C6DF` |
| `color.hero.border` | `#33FFFFFF` | `#26FFFFFF` |
| `color.hero.ringTrack` | `#33FFFFFF` | `#26FFFFFF` |

Gradient liniowy po przekątnej (StartPoint `0,0`, EndPoint `1,1`). Na gradiencie dozwolony jest tylko tekst `hero.text` i `hero.textSecondary`. Kontrast w najjaśniejszym punkcie gradientu (`#86295E`): biały 8.43, `#F5C6DF` 5.63; w najciemniejszym (`#2E0D20`): 17.58 / 11.74. Ikony i obrysy na hero używają `hero.text` lub `hero.border`. Dodatkowo w prawym-dolnym rogu hero MOŻE być dekoracyjny motyw koncentrycznych pierścieni (`#14FFFFFF`, obrys 1 px).

#### 2.1.6 Akcenty źródeł (`color.source.<id>.*`)

Każde z 20 źródeł zachowuje rozpoznawalny kolor marki, ale ma warianty dostrojone do kontrastu ≥ 3:1 względem `surface.1` i `surface.2` (light) oraz `surface.1` i `surface.3` (dark). Kolor źródła służy do: paska akcentu, awatara (SourceBadge), serii na wykresach i kropek legendy. **Nigdy** do tekstu dłuższego niż inicjał w awatarze.

| Id | Nazwa | Kolor marki | `light` | `dark` | `avatar` (tło inicjału) | Tekst inicjału |
| --- | --- | --- | --- | --- | --- | --- |
| `claude` | Claude Code | `#D97757` | `#D66D4B` | `#D97757` | = light/dark | `#16151B` |
| `codex` | Codex CLI | `#10A37F` | `#109E7B` | `#10A37F` | = light/dark | `#16151B` |
| `copilot` | GitHub Copilot | `#7C5CFC` | `#7C5CFC` | `#7C5CFC` | `#7857FC` | `#FFFFFF` |
| `amp` | Amp | `#E5484D` | `#E5484D` | `#E5484D` | = light/dark | `#16151B` |
| `antigravity` | Antigravity | `#34A853` | `#32A04F` | `#34A853` | = light/dark | `#16151B` |
| `cline` | Cline | `#2F80ED` | `#2F80ED` | `#2F80ED` | = light/dark | `#16151B` |
| `codebuff` | Codebuff | `#84CC16` | `#639A11` | `#84CC16` | = light/dark | `#16151B` |
| `droid` | Factory Droid | `#F2A93B` | `#C47B0D` | `#F2A93B` | = light/dark | `#16151B` |
| `dsh` | DeepSeek Harness | `#4D6BFE` | `#4D6BFE` | `#4D6BFE` | `#4867FE` | `#FFFFFF` |
| `gemini` | Gemini CLI | `#C2489B` | `#C2489B` | `#C2489B` | `#C14499` | `#FFFFFF` |
| `goose` | Goose | `#8B5E3C` | `#8B5E3C` | `#966541` | = light/dark | `#FFFFFF` |
| `grok` | Grok Build | `#5B5B66` | `#5B5B66` | `#6E6E7B` | = light/dark | `#FFFFFF` |
| `hermes` | Hermes Agent | `#8E6CEF` | `#8E6CEF` | `#8E6CEF` | = light/dark | `#16151B` |
| `kilo` | Kilo Code | `#D4B000` | `#A68A00` | `#D4B000` | = light/dark | `#16151B` |
| `kimi` | Kimi | `#1783FF` | `#1783FF` | `#1783FF` | = light/dark | `#16151B` |
| `openclaw` | OpenClaw | `#FF5A36` | `#FF4922` | `#FF5A36` | = light/dark | `#16151B` |
| `opencode` | OpenCode | `#56B6C2` | `#3A96A1` | `#56B6C2` | = light/dark | `#16151B` |
| `pi` | pi-agent | `#E05D44` | `#E05D44` | `#E05D44` | = light/dark | `#16151B` |
| `qwen` | Qwen Code | `#FF6A00` | `#EB6200` | `#FF6A00` | = light/dark | `#16151B` |
| `zcode` | ZCode | `#2D6BFF` | `#2D6BFF` | `#2D6BFF` | = light/dark | `#FFFFFF` |

Tekst inicjału ma kontrast ≥ 4.5:1 do tła awatara w obu motywach.

**Źródłem prawdy** dla kolorów marki jest `AccentColor` w `OnionLLM.Core/Sources/*Source.cs`. Warianty `light`/`dark`/`avatar` są tokenami UI (słownik `Tokens.Sources.xaml`, §3.2) — wartości z tej tabeli są wpisane w `tools/design/gen_tokens.py`, który przy generowaniu sprawdza ich kontrast i ostrzega o źródle z Core bez wiersza tutaj (do tego czasu klucz `ColorSourceUnknown`). Nowe źródło = nowy wiersz w tej tabeli z wariantami wyliczonymi tym samym algorytmem (zmiana jasności HSL krokiem 1% aż do osiągnięcia 3:1; patrz [§11.5](#115-algorytm-wariantów-kolorów)).

**Kolizje odcieni.** Grupy podobnych kolorów: czerwono-pomarańczowe (`claude`, `amp`, `openclaw`, `pi`, `qwen`), niebieskie (`cline`, `kimi`, `zcode`, `dsh`), fioletowe (`copilot`, `hermes`), zielone (`codex`, `antigravity`, `codebuff`). Gdy na jednym wykresie lub legendzie są ≥ 2 źródła z tej samej grupy, MUSI zadziałać drugie kodowanie ([§4.8.3](#483-rozróżnianie-serii)).

#### 2.1.7 Paleta kategoryczna wykresów (`color.chart.cat.1–8`)

Do serii, które nie są źródłami (modele, projekty, okresy). Kolejność jest stała — seria #1 zawsze dostaje kolor 1.

| Token | L | D |
| --- | --- | --- |
| `color.chart.cat.1` | `#2667C9` | `#6BA4FF` |
| `color.chart.cat.2` | `#0F7C78` | `#4FD1C5` |
| `color.chart.cat.3` | `#C2410C` | `#FF9A62` |
| `color.chart.cat.4` | `#7A4FD6` | `#A98BFF` |
| `color.chart.cat.5` | `#A87A0C` | `#F2C14E` |
| `color.chart.cat.6` | `#B8336A` | `#F08BB4` |
| `color.chart.cat.7` | `#4B7A1F` | `#A6D46A` |
| `color.chart.cat.8` | `#5F6B7A` | `#A3AEBF` |
| `color.chart.other` | `#8F8C9B` | `#6E6B7B` |

Maksymalnie 8 serii + „Pozostałe”. Więcej kategorii agregujemy do „Pozostałe” (reguła top-N w [§4.8.3](#483-rozróżnianie-serii)).

#### 2.1.8 Przezroczystości i stany interakcji

Stany interakcji tworzymy nakładką (overlay) w kolorze tekstu na powierzchni:

| Token | L | D | Zastosowanie |
| --- | --- | --- | --- |
| `color.state.hover` | `#0A16151B` (4%) | `#0FF3F2F7` (6%) | hover wiersza, przycisku ghost, nav |
| `color.state.pressed` | `#1416151B` (8%) | `#1AF3F2F7` (10%) | pressed |
| `color.state.selected` | = `color.action.subtle` | = `color.action.subtle` | wiersz zaznaczony, nav aktywna |
| `color.state.dragged` | `#1F16151B` (12%) | `#24F3F2F7` (14%) | element przeciągany |

Opacity elementu wyłączonego: `opacity.disabled = 0.45` (cały komponent), `opacity.muted = 0.6` (np. przerwa między blokami), `opacity.skeleton = 1` (skeleton używa własnych kolorów `surface.2`/`surface.3`).

#### 2.1.9 Tabela kontrastów

Zweryfikowane skryptem (WCAG 2.2, luminancja względna). Wymagania: tekst ≥ 4.5, grafika/obrys/fokus ≥ 3.0.

| Para | Light | Dark | Wymóg |
| --- | --- | --- | --- |
| `text.primary` / `bg.canvas` | 16.96 | 17.49 | 4.5 |
| `text.primary` / `bg.sidebar` | 16.14 | 16.86 | 4.5 |
| `text.primary` / `surface.1` | 18.15 | 16.28 | 4.5 |
| `text.primary` / `surface.3` | 15.04 | 13.60 | 4.5 |
| `text.primary` / `action.subtle` | 15.09 | 14.37 | 4.5 |
| `text.secondary` / `bg.canvas` | 7.73 | 9.79 | 4.5 |
| `text.secondary` / `surface.1` | 8.27 | 9.11 | 4.5 |
| `text.secondary` / `surface.3` | 6.86 | 7.61 | 4.5 |
| `text.tertiary` / `bg.canvas` | 5.08 | 6.09 | 4.5 |
| `text.tertiary` / `bg.sidebar` | 4.84 | 5.87 | 4.5 |
| `text.tertiary` / `surface.1` | 5.44 | 5.67 | 4.5 |
| `text.tertiary` / `surface.2` | 4.88 | 5.28 | 4.5 |
| `text.tertiary` / `surface.3` | 4.51 | 4.74 | 4.5 |
| `text.onAction` / `action.primary` | 6.49 | 6.07 | 4.5 |
| `text.onAction` / `action.primaryHover` | 8.43 | 9.23 | 4.5 |
| `text.link` / `surface.1` | 6.49 | 8.67 | 4.5 |
| `text.onInverse` / `surface.inverse` | 16.29 | 16.29 | 4.5 |
| `action.onSubtle` / `action.subtle` | 5.40 | 7.65 | 4.5 |
| `border.focus` / `surface.1` | 6.49 | 8.67 | 3.0 |
| `border.focus` / `bg.canvas` | 6.07 | 9.31 | 3.0 |
| `border.strong` / `surface.1` | 3.76 | 3.50 | 3.0 |
| `border.strong` / `surface.2` | 3.37 | 3.26 | 3.0 |
| `status.*` / `surface.1` (min.) | 5.22 | 6.53 | 4.5 |
| `status.*` / `status.*Bg` (min.) | 4.68 | 5.98 | 4.5 |
| `data.*` / `surface.1` — tekst (min.) | 5.03 | 6.76 | 4.5 |
| `data.*` / `surface.2` — grafika (min.) | 4.51 | 6.29 | 3.0 |
| `burn.*`, `data.highlight` / `surface.1` (min.) | 3.85 | 6.53 | 3.0 |
| `action.primary` / `data.track` (wypełnienie paska) | 5.33 | 4.52 | 3.0 |
| `chart.cat.*` / `surface.2` (min.) | 3.45 | 6.04 | 3.0 |
| `source.*.light` / `surface.1` (min.) | 3.34 | — | 3.0 |
| `source.*.dark` / `surface.1` (min.) | — | 3.61 | 3.0 |
| `hero.text` / gradient (min.) | 8.43 | 8.43 | 4.5 |
| `hero.textSecondary` / gradient (min.) | 5.63 | 5.63 | 4.5 |
| `text.disabled` / `surface.1` | 2.37 | 2.69 | wyjątek WCAG 1.4.3 (nieaktywne) |

Każda nowa para kolorów MUSI zostać dopisana tutaj z wynikiem pomiaru.

### 2.2 Typografia

#### 2.2.1 Kroje

| Rola | Krój | Pliki (w `Resources/Fonts/`) | Alias MAUI |
| --- | --- | --- | --- |
| Interfejs | **Inter** 4.x (statyczne) | `Inter-Regular.ttf`, `Inter-Medium.ttf`, `Inter-SemiBold.ttf`, `Inter-Bold.ttf` | `InterRegular`, `InterMedium`, `InterSemiBold`, `InterBold` |
| Liczby | **Inter Tabular** — Inter z „zamrożonymi” cechami `tnum` + `cv11` (pojedyncze „a”) i `ss01` (otwarte cyfry) | `InterTnum-Medium.ttf`, `InterTnum-SemiBold.ttf` | `NumMedium`, `NumSemiBold` |
| Dane techniczne | **JetBrains Mono** 2.x | `JetBrainsMono-Regular.ttf`, `JetBrainsMono-Medium.ttf` | `MonoRegular`, `MonoMedium` |
| Ikony | **Lucide** (font) | `lucide.ttf` | `Lucide` |

- MAUI nie obsługuje cech OpenType w `Label`, dlatego cyfry tabelaryczne uzyskujemy osobnym plikiem fontu. Wariant „Inter Tabular” generujemy narzędziem `pyftfeatfreeze -f "tnum,cv11,ss01" -S -U Tnum` (pakiet `opentype-feature-freezer`) z oficjalnych plików Inter. Wygenerowane pliki trafiają do repo; skrypt generujący: `tools/fonts/build_fonts.py` (kopiuje fonty, zamraża cechy, generuje `Icons.cs`), przypięte wersje źródeł: `tools/fonts/VERSION`.
- Licencje: Inter, JetBrains Mono — SIL OFL 1.1; Lucide — ISC. Pliki licencji MUSZĄ leżeć obok fontów (`Resources/Fonts/LICENSES/`).
- Rejestracja w `MauiProgram.cs` (`ConfigureFonts`) z aliasami jak w tabeli. Fonty szablonu (OpenSans) zostają usunięte.

**Kiedy który krój:**

| Treść | Krój |
| --- | --- |
| Wszystkie teksty, etykiety, przyciski | Inter |
| Każda liczba, która może się zmieniać lub jest porównywana w kolumnie (koszty, tokeny, procenty, czasy, daty w tabelach) | Inter Tabular (`Num*`) |
| Identyfikatory sesji, ścieżki, nazwy modeli w tabelach technicznych, klucze konfiguracji, edytory tekstu (aliasy, nadpisania), diagnostyka | JetBrains Mono |
| Ikony | Lucide |

#### 2.2.2 Skala typograficzna

Rozmiar/interlinia w dp. `LineHeight` w MAUI jest mnożnikiem — w tabeli podano oba zapisy.

| Token | Krój / grubość | Rozmiar | Interlinia (mnożnik) | Odstęp liter | Zastosowanie |
| --- | --- | --- | --- | --- | --- |
| `type.display` | NumSemiBold | 44 | 52 (1.18) | −1.0 | liczba hero (koszt dnia) |
| `type.metric.lg` | NumSemiBold | 28 | 34 (1.21) | −0.5 | KPI w kaflach, liczba w drawerze |
| `type.metric.md` | NumSemiBold | 20 | 26 (1.30) | −0.2 | liczby w kartach źródeł, w mini-widoku |
| `type.metric.sm` | NumMedium | 15 | 20 (1.33) | 0 | liczby w wierszu listy / sekcjach |
| `type.title.page` | InterSemiBold | 24 | 32 (1.33) | −0.4 | tytuł strony |
| `type.title.section` | InterSemiBold | 17 | 24 (1.41) | −0.2 | tytuł sekcji, tytuł drawera |
| `type.title.card` | InterSemiBold | 14 | 20 (1.43) | 0 | tytuł karty, nazwa źródła |
| `type.body` | InterRegular | 14 | 20 (1.43) | 0 | tekst podstawowy |
| `type.body.strong` | InterMedium | 14 | 20 (1.43) | 0 | wyróżniony tekst, wartości pól |
| `type.label` | InterMedium | 13 | 18 (1.38) | 0 | przyciski, pola, nagłówki kolumn, nav |
| `type.caption` | InterRegular | 12 | 16 (1.33) | 0 | podpisy, metadane, pomoc pod polem |
| `type.overline` | InterSemiBold | 11 | 16 (1.45) | 0.8 | NADTYTUŁY SEKCJI (wersaliki) |
| `type.table.num` | NumMedium | 13 | 18 (1.38) | 0 | liczby w tabelach |
| `type.mono` | MonoRegular | 13 | 20 (1.54) | 0 | ID, ścieżki, edytory |
| `type.mono.sm` | MonoRegular | 12 | 16 (1.33) | 0 | diagnostyka, breakdown |
| `type.kbd` | MonoMedium | 11 | 16 (1.45) | 0 | skróty klawiszowe w `Kbd` |

**Gęstość kompaktowa** (§2.8) zmniejsza wybrane tokeny: `type.body` → 13/18, `type.label` → 12/16, `type.table.num` → 12/16, `type.metric.md` → 18/24, `type.display` → 40/48. Pozostałe bez zmian. **Minimum bezwzględne: 11 dp.**

#### 2.2.3 Zasady typograficzne

- **Hierarchia przez rozmiar i grubość, nie kolor.** Kolor tekstu to tylko `primary` / `secondary` / `tertiary` (+ statusy i metryki dla liczb).
- **Waluta i jednostka mniejsze niż liczba.** W `type.display` i `type.metric.lg` symbol waluty i jednostki („$”, „zł”, „M”, „k”, „%”) renderujemy w osobnym `Span` o 60% rozmiaru liczby, `color.text.secondary` (na hero `hero.textSecondary`), wyrównane do linii bazowej. Przykład: „**12,34** $”, „**1,2** M tokenów”.
- **Wersaliki tylko w `type.overline`** (przez `TextTransform="Uppercase"`, tekst źródłowy zapisany normalnie).
- **Wyrównanie liczb do prawej** w tabelach i kolumnach; etykiety do lewej.
- **Długość linii** tekstu opisowego ≤ 72 znaki (`MaximumWidthRequest` ≈ 560 dla `type.body`).
- **Obcinanie:** nazwy (projekty, modele, ścieżki) `LineBreakMode="MiddleTruncation"` dla ścieżek i ID, `TailTruncation` dla nazw; pełna wartość w tooltipie.
- **Brak kursywy** poza cytowaniem wartości wpisanej przez użytkownika w komunikatach.

### 2.3 Odstępy

Siatka bazowa **4 dp**. Dozwolone wyłącznie wartości z tabeli.

| Token | Wartość | Typowe użycie |
| --- | --- | --- |
| `space.0` | 0 | — |
| `space.050` | 2 | odstęp ikona–liczba w badge, wewnętrzny w Kbd |
| `space.100` | 4 | między etykietą a wartością w pionie |
| `space.150` | 6 | ikona–tekst w małych przyciskach |
| `space.200` | 8 | ikona–tekst, odstęp chipów, padding pionowy wiersza (compact) |
| `space.300` | 12 | odstęp elementów w karcie, padding pionowy wiersza (comfortable) |
| `space.400` | 16 | padding karty, odstęp kart w siatce, padding poziomy wiersza |
| `space.500` | 20 | padding karty hero (compact) |
| `space.600` | 24 | padding strony, padding hero, odstęp sekcji w drawerze |
| `space.800` | 32 | odstęp między sekcjami strony |
| `space.1000` | 40 | margines stanów pustych |
| `space.1200` | 48 | — |
| `space.1600` | 64 | ilustracje stanów pustych |

**Tokeny układu** (zależne od gęstości, §2.8):

| Token | Comfortable | Compact |
| --- | --- | --- |
| `layout.page.padding` | `24,20,24,24` | `16,12,16,16` |
| `layout.section.gap` | 32 | 20 |
| `layout.grid.gap` | 16 | 12 |
| `layout.card.padding` | 16 | 12 |
| `layout.card.paddingLg` (hero, drawer) | 24 | 16 |
| `layout.content.maxWidth` | 1440 | 1600 |

### 2.4 Promienie

| Token | Wartość | Zastosowanie |
| --- | --- | --- |
| `radius.xs` | 4 | Kbd, ModelTag, małe badge, słupki wykresu (górne rogi) |
| `radius.sm` | 6 | przyciski i pola `size=sm`, chipy, tooltip |
| `radius.md` | 8 | przyciski, pola, segmented, NavItem, wiersz zaznaczony |
| `radius.lg` | 12 | karty, tabele, kafle, menu, toast |
| `radius.xl` | 16 | hero, dialog, paleta poleceń, mini-widok |
| `radius.full` | 9999 | awatary okrągłe, kropki statusu, przełącznik, pigułki |

Zasada zagnieżdżania: promień wewnętrzny = promień zewnętrzny − padding (minimum `radius.xs`). Karta `radius.lg` z paddingiem 8 → kafel wewnątrz `radius.xs`.

### 2.5 Obrysy i elewacja

**Obrysy:** zawsze **1 dp** (`stroke.default = 1`). Fokus: **2 dp** pierścień z odstępem 2 dp (`stroke.focus = 2`, `stroke.focusOffset = 2`). Zaznaczenie wiersza: pasek 2 dp `color.action.primary` przy lewej krawędzi + tło `state.selected`.

**Elewacja** — w jasnym motywie przez cień, w ciemnym przez ton powierzchni + obrys:

| Token | Light (Shadow: Brush, Offset, Radius, Opacity) | Dark | Zastosowanie |
| --- | --- | --- | --- |
| `elevation.0` | brak | brak, `surface.1` + obrys | tabele, karty w siatce |
| `elevation.1` | `#16151B`, `0,1`, 3, 0.06 | `surface.1` + obrys | karty (spoczynek) |
| `elevation.2` | `#16151B`, `0,4`, 12, 0.08 | `surface.2` + obrys | karta hover (uniesienie), dropdown, tooltip |
| `elevation.3` | `#16151B`, `0,12`, 32, 0.14 | `surface.overlay` + obrys + `#000000`, `0,12`, 32, 0.5 | drawer, dialog, paleta poleceń, toast |

Hero ma własny cień: `#2E0D20`, `0,8`, 24, 0.25 (light) / brak (dark).

### 2.6 Ikony

- **Zestaw:** Lucide (font `lucide.ttf` z pakietu `lucide-static`, wersja przypięta w `tools/fonts/VERSION` — obecnie 1.69.0). Nazwy w `Icons.cs`: PascalCase, sąsiednie segmenty liczbowe rozdzielone „_” (`arrow-down-0-1` → `ArrowDown0_1`). Kody glifów generujemy do klasy `OnionLLM/Resources/Icons.cs` (`public const string Gauge = "";` itd.) skryptem z `lucide-static/font/info.json`. NIE WOLNO wpisywać kodów glifów ręcznie w XAML.
- **Rozmiary:** `icon.sm = 16` (w polach, tabelach, przyciskach `sm`), `icon.md = 20` (przyciski, nav, nagłówki), `icon.lg = 24` (stany puste w kafelkach, onboarding), `icon.xl = 48` (stany puste ekranu).
- **Kolor:** dziedziczy z tekstu obok (`text.secondary` domyślnie, `text.primary` w stanie aktywnym). Ikona sama w sobie nie przenosi koloru statusu, chyba że stoi obok tekstu statusu.
- **Ikona nigdy nie występuje bez etykiety lub tooltipu** (przyciski ikonowe MUSZĄ mieć tooltip i `SemanticProperties.Description`).

Mapa ikon → [§11.2](#112-mapa-ikon-lucide).

### 2.7 Ruch

Aplikacja ma **bogaty ruch** — ale każda animacja komunikuje zmianę stanu, hierarchię albo przyczynę i skutek. Całość podlega ustawieniu „Animacje” i systemowemu „ogranicz ruch” (§2.7.4).

#### 2.7.1 Czasy i krzywe

| Token | Wartość | Zastosowanie |
| --- | --- | --- |
| `motion.duration.instant` | 0 ms | reduce motion |
| `motion.duration.fast` | 120 ms | hover, pressed, zmiana koloru, tooltip |
| `motion.duration.base` | 200 ms | rozwinięcie sekcji, przełącznik, segmented, zmiana zakładki |
| `motion.duration.slow` | 280 ms | drawer, dialog, przejście strony |
| `motion.duration.slower` | 480 ms | wejście wykresów, pierścienie |
| `motion.duration.counter` | 600 ms | animacja liczb (count-up) |
| `motion.duration.ambient` | 2400 ms | puls aktywnego bloku (pętla) |
| `motion.stagger` | 30 ms | opóźnienie kolejnych elementów listy/siatki (max 8 elementów = 240 ms) |

| Token | Krzywa (cubic-bezier) | MAUI | Zastosowanie |
| --- | --- | --- | --- |
| `motion.ease.standard` | (0.2, 0, 0, 1) | `OnionEasing.Standard` | większość przejść |
| `motion.ease.enter` | (0, 0, 0, 1) | `OnionEasing.Enter` (≈ `Easing.CubicOut`) | wejście elementów |
| `motion.ease.exit` | (0.3, 0, 1, 1) | `OnionEasing.Exit` (≈ `Easing.CubicIn`) | wyjście elementów |
| `motion.ease.spring` | sprężyna, przesterowanie 4% | `OnionEasing.Spring` | pierścienie, przełącznik, upuszczenie przy drag & drop |

`OnionEasing` to statyczna klasa z gotowymi `Easing` (np. `new Easing(t => …)` aproksymującym krzywą bezier). Wartości czasu bierzemy z `MotionTokens` (C#), nie z literałów.

#### 2.7.2 Choreografia (katalog animacji)

| Id | Zdarzenie | Animacja | Czas / krzywa |
| --- | --- | --- | --- |
| M1 | Wejście na stronę | Treść: fade 0→1 + przesunięcie Y 8→0. Karty/kafle z opóźnieniem `stagger` w kolejności czytania. | `slow`, `enter` |
| M2 | Zmiana strony | Stara treść fade-out 120 ms (bez przesunięcia), nowa jak M1. Sidebar się nie animuje. | `fast` + `slow` |
| M3 | Zmiana liczby (dane odświeżone) | Count-up od starej do nowej wartości (interpolacja wartości, formatowanie co klatkę). Jeśli wartość wzrosła o > 0, krótki „błysk” tła liczby `color.action.subtle` 0→1→0. | `counter`, `standard` |
| M4 | Pierwsze pojawienie się wykresu | Słupki rosną od osi (scaleY 0→1) z `stagger` 15 ms; linie rysowane od lewej (clip); donut/pierścienie obracają się od 12:00. | `slower`, `enter` |
| M5 | Zmiana danych wykresu | Morfowanie: wysokości/kąty interpolowane ze starych do nowych. Nowe serie fade-in. | `base`, `standard` |
| M6 | Drawer otwarcie / zamknięcie | Slide X (szerokość→0) + scrim fade; zamknięcie odwrotnie. Treść drawera z `stagger`. | `slow`, `enter` / `exit` |
| M7 | Dialog / paleta poleceń | Scale 0.96→1 + fade; scrim fade. | `base`, `enter` |
| M8 | Hover karty klikalnej | Uniesienie Y 0→−1, cień `elevation.1`→`elevation.2`, obrys → `border.strong` 50%. | `fast`, `standard` |
| M9 | Pressed | Scale 1→0.98. | `fast` |
| M10 | Aktywny blok (puls) | Kropka „Aktywny” pulsuje: pierścień wokół kropki skaluje 1→2.2 z fade 0.5→0, w pętli. | `ambient`, `standard` |
| M11 | Pierścienie limitów (`OnionRings`) | Wypełnienie łuku od 0 do wartości ze sprężyną; przy przekroczeniu 80% łuk płynnie zmienia kolor na `status.warning`, przy 100% na `status.danger`. | `slower`, `spring` |
| M12 | Rozwinięcie sekcji / szczegółów | Wysokość auto (animacja `HeightRequest`), zawartość fade-in po 60% czasu; chevron obrót 0→180°. | `base`, `standard` |
| M13 | Toast | Wejście od dołu Y 16→0 + fade; auto-zamknięcie po 5 s (błędy: bez auto-zamknięcia); wyjście fade. | `base` |
| M14 | Przeciąganie (kolejność źródeł) | Podniesiony element: scale 1.02, `elevation.3`, tło `state.dragged`; pozostałe rozsuwają się płynnie; upuszczenie ze sprężyną. | `base`, `spring` |
| M15 | Odświeżanie | Ikona odświeżania obraca się (360°/800 ms, liniowo) tylko w czasie trwania sync; po zakończeniu: zatrzymanie na pełnym obrocie. | liniowo |
| M16 | Skeleton | Shimmer: gradient `surface.2`→`surface.3`→`surface.2` przesuwany po X, 1400 ms, pętla. | liniowo |
| M17 | Przełączenie motywu | Crossfade całego okna (snapshot) 200 ms; bez animowania poszczególnych kolorów. | `base` |
| M18 | Zwinięcie sidebaru | Szerokość 240↔64, etykiety fade (pierwsze 40% czasu znikają / ostatnie 40% pojawiają się). | `base`, `standard` |
| M19 | Przekroczenie progu limitu/budżetu | Jednorazowe „potrząśnięcie” paska/pierścienia (X ±3, 3 cykle) + zmiana koloru. Nie powtarza się przy kolejnych odświeżeniach. | 360 ms |
| M20 | Nowy rekord (np. najwyższy dzienny koszt) | Złota iskra (`data.highlight`) — krótki błysk obrysu 1→0 wokół kafla. | 800 ms |

**Implementacja (1.5):** `Design/Motion.cs` — `StaggerInAsync` (M1: przy każdym wejściu na ekran widoczne potomki `Card` — w tym StatTile, ChartCard, SourceCard — maks. 8, w kolejności drzewa), `CrossfadeAsync` (M17: zrzut korzenia okna przez `CaptureAsync`, podmiana słowników pod spodem, zanik zrzutu `base`; bez zrzutu motyw zmienia się od razu; podpięte przez `ThemeService.Transition`, więc działa dla Ustawień, Ctrl+Shift+L, palety i onboardingu). M18 w `MainShellPage.AnimateSidebar`: animowana szerokość kolumny i panelu; etykiety przełączają się od razu, a cała zawartość panelu wraca z krycia `OpacityMuted` do 1 w ostatnich 40% czasu (uproszczenie: bez osobnego zaniku etykiet). M2 i M8 — bez zmian (`NavigationService`, `Card`). Pomiar 1.7: zrzut ≈ 0,4 s, podmiana słowników przy wszystkich zbuforowanych ekranach ≈ 1,3 s (zakryta zrzutem), zanik 200 ms — przejście trwa dłużej niż `base`, ale bez widocznego „przeskoku” kolorów.

#### 2.7.3 Zasady ruchu

- Animacja NIE MOŻE blokować interakcji — kliknięcia w trakcie animacji są obsługiwane od razu (animacja jest przerywana i kończy się w stanie docelowym).
- Count-up (M3) NIE działa przy pierwszym renderze strony z cache — liczby pojawiają się od razu; animujemy tylko zmianę po odświeżeniu.
- Maksymalnie **jedna animacja pętlowa** (M10 lub M15) widoczna jednocześnie w danym obszarze.
- Ruch nie zmienia układu sąsiednich elementów nagle — przesunięcia innych elementów też są animowane (M12, M14).
- Animacje wykresów i liczb mają gwarantowany stan końcowy przy zmianie danych w trakcie (anuluj + uruchom od bieżącej wartości).

#### 2.7.4 Ograniczony ruch

Ustawienie **Wygląd → „Animacje”**: „Pełne” (domyślne) · „Ograniczone” · „Wyłączone”. Wartość „Pełne” respektuje ustawienie systemu (Windows: `UISettings.AnimationsEnabled`; macOS: `UIAccessibility.IsReduceMotionEnabled`) — jeśli system ogranicza ruch, aplikacja działa jak „Ograniczone”.

| Tryb | Zachowanie |
| --- | --- |
| Pełne | Cały katalog §2.7.2. |
| Ograniczone | Tylko fade (bez przesunięć, skalowania, sprężyn); M3 bez count-up (zmiana natychmiastowa + błysk tła); M4/M5 bez wzrostu (fade 120 ms); M10, M16, M19, M20 wyłączone (puls → statyczny pierścień; shimmer → statyczny skeleton). |
| Wyłączone | Wszystkie czasy = 0. Stany zmieniają się natychmiast. |

### 2.8 Gęstość

Ustawienie **Wygląd → „Gęstość”**: „Komfortowa” (domyślna) · „Kompaktowa”. Przełączane w locie (zamiana słownika gęstości, §3.2).

| Token | Comfortable | Compact |
| --- | --- | --- |
| `size.control.height` (przyciski, pola, segmented) | 36 | 30 |
| `size.control.heightSm` | 28 | 24 |
| `size.control.heightLg` (primary w onboardingu, dialogach) | 44 | 36 |
| `size.row.height` (wiersz tabeli) | 44 | 32 |
| `size.row.heightDense` (lista w drawerze, breakdown) | 36 | 28 |
| `size.nav.item` | 36 | 32 |
| `size.header.height` (PageHeader) | 64 | 52 |
| `size.avatar.md` | 32 | 28 |
| `size.avatar.sm` | 20 | 18 |
| `size.progress.bar` | 6 | 4 |
| `size.ring.hero` (OnionRings na hero) | 168 | 136 |
| + tokeny `layout.*` z §2.3 i `type.*` z §2.2.2 | | |

Minimalny obszar kliknięcia **24×24 dp** obowiązuje także w trybie kompaktowym (WCAG 2.5.8).

### 2.9 Układ, breakpointy i okno

#### 2.9.1 Okno

- Rozmiar startowy: **1280 × 840**; minimalny: **680 × 560**. Ostatni rozmiar i pozycja są zapamiętywane (per monitor) i przywracane, jeśli mieszczą się na ekranie.
- Tytuł okna: „OnionLLM” (bez dopisków).
- „Zawsze na wierzchu”: opcja w Ustawieniach → Ogólne, **domyślnie wyłączona** (§7.1). `Wymaga platformy`.

#### 2.9.2 Breakpointy (szerokość okna)

| Nazwa | Szerokość okna | Sidebar | Siatka treści | Drawer | Tabele |
| --- | --- | --- | --- | --- | --- |
| `bp.wide` | ≥ 1280 | rozwinięty 240 (użytkownik MOŻE zwinąć do 64, Ctrl+B) | 12 kolumn, gap `layout.grid.gap` | **wypycha** treść (układ dzielony, treść min 640) | pełne kolumny |
| `bp.medium` | 900–1279 | zwinięty do szyny 64 (ikony + tooltip; Ctrl+B rozwija jako nakładkę) | 8 kolumn | **nakłada się** (overlay ze scrimem) | kolumny o priorytecie 3 ukryte |
| `bp.narrow` | < 900 | szyna 56 | 4 kolumny | overlay na pełną szerokość treści (okno − 56) | **zwinięte do kart** (§4.5.1) poniżej szerokości treści 760 |

- Treść ma `layout.content.maxWidth` i jest centrowana na bardzo szerokich oknach.
- Siatka kolumn: kafle KPI zajmują 3/12 (wide), 4/8 (medium), 2/4 (narrow). Wykres główny 12/12. Karty źródeł: auto-fill min 320, max 1fr.
- Zmiana breakpointu następuje przez zmianę rozmiaru okna (`MainShellPage.SizeChanged` — strona hosta zajmuje całe okno) → `ShellViewModel.Layout` (`LayoutState` wide/medium/narrow, progi z tokenów `BreakpointWide`/`BreakpointMedium`) konsumowany przez komponenty (wiązania lub `VisualStateManager`). NIE WOLNO liczyć szerokości w każdym ekranie osobno.

#### 2.9.3 Struktura okna

Okno ma trzy kolumny regionów (od lewej):

| Region | Zawartość (od góry) | Przewijanie |
| --- | --- | --- |
| **Sidebar** | Logo i nazwa → nawigacja główna (grupy) → odstęp elastyczny → Ustawienia → przełącznik mini-widoku → zwiń/rozwiń | brak (nawigacja mieści się zawsze; przy niskim oknie grupy zwijają się do ikon) |
| **Treść** | PageHeader (tytuł · FilterBar · akcje) → treść strony → StatusBar (przyklejony do dołu) | tylko treść strony |
| **Drawer** (opcjonalny) | DrawerHeader → treść → DrawerFooter (akcje) | tylko treść drawera |

PageHeader jest przyklejony (sticky) i przy przewinięciu treści zyskuje dolny obrys `border.subtle` oraz tło `bg.canvas` z 92% kryciem.

---

## 3. Implementacja tokenów w MAUI

### 3.1 Zasady

- Każda wartość wizualna w XAML pochodzi z `{DynamicResource …}` (kolory, pędzle, gęstość — zmieniają się w locie) albo `{StaticResource …}` (promienie, fonty, ikony — stałe).
- Komponenty i ekrany używają **stylów** (`Style="{StaticResource Text.Body}"`) zamiast ustawiać właściwości wyglądu ręcznie.
- `AppThemeBinding` NIE jest używany w ekranach i komponentach — motyw jest obsługiwany zamianą słowników (§3.4). Wyjątek: `ThemeService` sam w sobie.
- Kod C# (rysowanie wykresów, animacje) pobiera tokeny przez `DesignTokens` (§3.6), nigdy z literałów.

### 3.2 Struktura plików

```
OnionLLM/Resources/Styles/
├── Tokens.Primitives.xaml      # palette.onion.*, palette.husk.* (stałe, oba motywy)
├── Tokens.Light.xaml           # color.* dla motywu jasnego
├── Tokens.Dark.xaml            # color.* dla motywu ciemnego (te same klucze!)
├── Tokens.Sources.Light.xaml   # color.source.<id>.* (light)
├── Tokens.Sources.Dark.xaml    # color.source.<id>.* (dark)
├── Tokens.Comfortable.xaml     # size.*, layout.*, type.* zależne od gęstości
├── Tokens.Compact.xaml         # te same klucze, wartości kompaktowe
├── Tokens.Static.xaml          # radius.*, space.*, stroke.*, icon.*, fonty, type.* niezależne od gęstości
├── Styles.Text.xaml            # style Label (Text.*)
├── Styles.Controls.xaml        # style Button, Entry, Picker, Switch, CheckBox, …
├── Styles.Surfaces.xaml        # style Border (Card, Tile, Hero, Overlay, …)
└── Styles.Implicit.xaml        # style domyślne (Page, Label, Entry, … → tokeny)
OnionLLM/Resources/Icons.cs     # stałe glifów Lucide (generowane)
OnionLLM/Design/                # DesignTokens.cs, MotionTokens.cs, OnionEasing.cs, ThemeService.cs, DensityService.cs
OnionLLM/Navigation/            # usługi okna głównego: ShellViewModel, NavigationService, ShortcutService, Toast/Dialog
OnionLLM/Views/                 # ekrany z §6 jako ContentView (+ IScreenView), hostowane w MainShellPage
OnionLLM/Controls/              # komponenty z §4 (ContentView + kod)
OnionLLM/Controls/Charts/       # ChartDrawable i wykresy z §4.8
```

Słowniki `Tokens.*.xaml` (z `x:Class` i plikiem `.xaml.cs`) są **generowane** przez `tools/design/gen_tokens.py` — zmiana wartości tokenu = zmiana w skrypcie + ponowne uruchomienie (ręczna edycja słowników jest zabroniona). Gradient hero jest tokenem `BrushHero` w `Tokens.Light/Dark`, a cienie elewacji — zasobami `ShadowE1`…`ShadowE3`, `ShadowHero` w tych samych słownikach.

`App.xaml` scala na stałe: `Tokens.Primitives`, `Tokens.Static`, `Styles.*`. Słowniki zależne od motywu i gęstości są dokładane i podmieniane przez `ThemeService` / `DensityService` (zawsze na tych samych pozycjach w `MergedDictionaries`).

Pliki szablonu `Colors.xaml` i `Styles.xaml` zostają usunięte wraz z ich kluczami (`Primary`, `Gray100`, …).

### 3.3 Nazewnictwo kluczy

| Token | Klucz XAML | Typ |
| --- | --- | --- |
| `color.bg.canvas` | `ColorBgCanvas` | `Color` |
| (pędzel do tego samego) | `ColorBgCanvasBrush` | `SolidColorBrush` |
| `color.source.claude.light` | `ColorSourceClaude` (w słowniku Light/Dark) | `Color` |
| `color.source.claude.avatar` | `ColorSourceClaudeAvatar` | `Color` |
| `space.400` | `Space400` | `x:Double` |
| `layout.card.padding` | `LayoutCardPadding` | `Thickness` |
| `radius.lg` | `RadiusLg` | `x:Double` (lub `CornerRadius` jako `RadiusLgCorner`) |
| `type.body` | styl `Text.Body` + klucze `TypeBodySize`, `TypeBodyLineHeight` | `Style` / `x:Double` |
| `size.row.height` | `SizeRowHeight` | `x:Double` |
| `motion.*` | tylko C# (`MotionTokens`) | `uint` / `Easing` |

Reguła: segmenty tokenu w PascalCase, sklejone, bez kropek. Pędzel = klucz koloru + `Brush`. Style mają nazwy z kropką (`Text.Body`, `Button.Primary`, `Card.Default`).

### 3.4 Motyw: jasny / ciemny / systemowy

Ustawienie **Wygląd → „Motyw”**: „Systemowy” (domyślny) · „Jasny” · „Ciemny”.

`ThemeService`:

1. Przy starcie odczytuje ustawienie (`appearance.theme`; `AppearanceService` stosuje też `appearance.density` i `appearance.motion` — przy starcie i po każdej zmianie konfiguracji).
2. Ustala efektywny motyw: dla „Systemowy” = `Application.Current.RequestedTheme`.
3. Ustawia `Application.Current.UserAppTheme` (aby kontrolki natywne — scroll, picker, kalendarz — dopasowały się do motywu).
4. Podmienia `Tokens.Light/Dark.xaml` i `Tokens.Sources.Light/Dark.xaml` w `MergedDictionaries`.
5. Nasłuchuje `Application.RequestedThemeChanged` (tylko w trybie „Systemowy”).
6. Emituje zdarzenie `ThemeChanged` — wykresy (`GraphicsView`) wywołują `Invalidate()`.
7. Na Windows aktualizuje kolor paska tytułu (`AppWindow.TitleBar`), na macOS — `NSAppearance` okna, tak by pasek tytułu pasował do motywu.

Zmiana motywu działa bez restartu i jest animowana crossfade (M17).

### 3.5 Style domyślne (implicit)

`Styles.Implicit.xaml` ustawia dla typów bazowych wyłącznie tokeny, aby kontrolka „bez stylu” wyglądała poprawnie:

| Typ | Ustawienia |
| --- | --- |
| `Page` / `ContentPage` | `BackgroundColor = ColorBgCanvas`, `Shell.NavBarIsVisible = False` |
| `Label` | `FontFamily = InterRegular`, `FontSize = TypeBodySize`, `LineHeight = TypeBodyLineHeight`, `TextColor = ColorTextPrimary` |
| `Entry`, `Editor`, `Picker`, `DatePicker`, `SearchBar` | jak `Input.Default` (§4.2) |
| `Button` | jak `Button.Secondary` (§4.1) |
| `CheckBox` | `Color = ColorActionPrimary` |
| `Switch` | `OnColor = ColorActionPrimary`, `ThumbColor = #FFFFFF` |
| `ActivityIndicator` | `Color = ColorActionPrimary`, rozmiar 16 |
| `ProgressBar` | zastąpiony komponentem `ProgressBar` z §4.4 (wbudowany NIE MOŻE być używany) |
| `Border` | `Stroke = ColorBorderDefault`, `StrokeThickness = 1`, `StrokeShape = RoundRectangle RadiusLg`, `BackgroundColor = ColorSurface1` |
| `BoxView` (dzielnik) | `Color = ColorBorderSubtle`, `HeightRequest = 1` |
| `ScrollView` | brak zmian (paski przewijania natywne) |

### 3.6 Tokeny w C#

`DesignTokens` (statyczna fasada) udostępnia bieżące wartości z aktywnych słowników:

```csharp
// Przykład użycia w ChartDrawable
var grid = DesignTokens.Color("ColorDataGrid");
var axisFont = DesignTokens.Font("NumMedium");
var radius = DesignTokens.Double("RadiusXs");
var series = DesignTokens.SourceColor("claude"); // wariant dla bieżącego motywu
var cat = DesignTokens.ChartCategorical(index);   // color.chart.cat.(index % 8 + 1)
await view.FadeTo(1, MotionTokens.Slow, OnionEasing.Enter);
```

`MotionTokens` zwraca czasy **już przeliczone** przez tryb animacji (§2.7.4) — kod nie sprawdza trybu sam.

### 3.7 Przykład słownika

```xml
<!-- Tokens.Light.xaml (fragment) -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
    <Color x:Key="ColorBgCanvas">#F7F7F9</Color>
    <Color x:Key="ColorSurface1">#FFFFFF</Color>
    <Color x:Key="ColorSurface2">#F3F2F6</Color>
    <Color x:Key="ColorBorderDefault">#E3E2E8</Color>
    <Color x:Key="ColorTextPrimary">#16151B</Color>
    <Color x:Key="ColorTextSecondary">#4F4D59</Color>
    <Color x:Key="ColorActionPrimary">#A23272</Color>
    <Color x:Key="ColorDataCost">#A23272</Color>
    <SolidColorBrush x:Key="ColorBgCanvasBrush" Color="#F7F7F9" />
    <!-- … pełna lista w §11.1 -->
</ResourceDictionary>
```

```xml
<!-- Styles.Text.xaml (fragment) -->
<Style x:Key="Text.Body" TargetType="Label">
    <Setter Property="FontFamily" Value="InterRegular" />
    <Setter Property="FontSize" Value="{DynamicResource TypeBodySize}" />
    <Setter Property="LineHeight" Value="{DynamicResource TypeBodyLineHeight}" />
    <Setter Property="TextColor" Value="{DynamicResource ColorTextPrimary}" />
</Style>
<Style x:Key="Text.Secondary" TargetType="Label" BasedOn="{StaticResource Text.Body}">
    <Setter Property="TextColor" Value="{DynamicResource ColorTextSecondary}" />
</Style>
<Style x:Key="Text.Overline" TargetType="Label">
    <Setter Property="FontFamily" Value="InterSemiBold" />
    <Setter Property="FontSize" Value="{StaticResource TypeOverlineSize}" />
    <Setter Property="CharacterSpacing" Value="{StaticResource TypeOverlineTracking}" />
    <Setter Property="TextTransform" Value="Uppercase" />
    <Setter Property="TextColor" Value="{DynamicResource ColorTextTertiary}" />
</Style>
<Style x:Key="Text.Display" TargetType="Label">
    <Setter Property="FontFamily" Value="NumSemiBold" />
    <Setter Property="FontSize" Value="{DynamicResource TypeDisplaySize}" />
    <Setter Property="CharacterSpacing" Value="{StaticResource TypeDisplayTracking}" />
    <Setter Property="TextColor" Value="{DynamicResource ColorHeroText}" />
</Style>
```

> W stylach `Setter Value="{DynamicResource …}"` jest poprawny w MAUI. Literały (hex, liczby) są dozwolone **wyłącznie** w słownikach `Tokens.*.xaml`. Style, komponenty i ekrany odwołują się tylko do kluczy. Każdy token typograficzny ma klucze `Type<Nazwa>Size`, `Type<Nazwa>LineHeight`, `Type<Nazwa>Tracking` oraz styl `Text.<Nazwa>`.

---

## 4. Komponenty

### 4.0 Zasady ogólne

- Każdy komponent jest kontrolką w `OnionLLM/Controls/` (`ContentView` z `BindableProperty`) albo stylem w `Styles.*.xaml`, jeśli wystarcza styl kontrolki wbudowanej. Ekrany NIE budują tych wzorców ręcznie z `Border`/`Grid`.
- Każdy komponent obsługuje: oba motywy, obie gęstości, stany z tabeli „Stany”, klawiaturę i czytnik ekranu.
- Wspólne stany interakcji (o ile komponent jest interaktywny):

| Stan | Wygląd | Wyzwalacz |
| --- | --- | --- |
| `Normal` | wartości domyślne | — |
| `PointerOver` | nakładka `color.state.hover`; kursor „ręka” na Windows | `PointerGestureRecognizer` (PointerEntered/Exited) |
| `Pressed` | nakładka `color.state.pressed`, M9 | — |
| `Focused` | pierścień `border.focus` 2 dp z odstępem 2 dp (wyłącznie przy fokusie z klawiatury) | `Focused` + flaga „ostatnie wejście = klawiatura” |
| `Disabled` | `opacity.disabled`, brak hover, `IsEnabled=False` | — |
| `Selected` / `Checked` | zależnie od komponentu | — |
| `Loading` | zależnie od komponentu (spinner w miejscu ikony, skeleton) | — |

Stany realizujemy przez `VisualStateManager` w stylach (grupy `CommonStates`, `FocusStates`, `SelectionStates`).

Rejestr wszystkich komponentów ze statusem → [§11.3](#113-rejestr-komponentów).

---

### 4.1 Przyciski

#### 4.1.1 Button

**Cel:** wywołanie akcji.

**Anatomia:** [ikona wiodąca 16/20] · etykieta · [ikona końcowa / Kbd]. Ikona i etykieta odstęp `space.150` (sm) / `space.200` (md, lg).

**Warianty:**

| Wariant | Tło | Tekst | Obrys | Użycie |
| --- | --- | --- | --- | --- |
| `Primary` | `action.primary` | `text.onAction` | brak | jedna główna akcja w widoku/dialogu/kroku („Kontynuuj”, „Eksportuj”) |
| `Secondary` | `surface.1` | `text.primary` | `border.default` | akcje drugorzędne („Odśwież cennik”, „Filtry”) |
| `Ghost` | przezroczyste | `text.secondary` → `text.primary` przy hover | brak | akcje w paskach narzędzi, w wierszach, w nagłówkach kart |
| `Danger` | `status.danger` | `#FFFFFF` (L) / `text.onAction` (D) | brak | akcje nieodwracalne („Przebuduj bazę”, „Usuń nadpisanie”) |
| `DangerGhost` | przezroczyste | `status.danger` | brak | usuwanie w wierszach |
| `Link` | przezroczyste | `text.link`, podkreślenie przy hover | brak | nawigacja w tekście („Pokaż w Diagnostyce”) |

**Rozmiary:**

| Rozmiar | Wysokość | Padding poziomy | Typografia | Promień |
| --- | --- | --- | --- | --- |
| `sm` | `size.control.heightSm` | 10 | `type.label` | `radius.sm` |
| `md` (domyślny) | `size.control.height` | 14 | `type.label` | `radius.md` |
| `lg` | `size.control.heightLg` | 20 | `type.body.strong` | `radius.md` |

**Stany:** wspólne (§4.0). `Primary`: hover → `action.primaryHover`, pressed → `action.primaryPressed`. `Loading`: etykieta zostaje, ikona wiodąca zamienia się na spinner 16, przycisk nieaktywny, szerokość nie zmienia się.

**Zachowanie:**

- Akcje asynchroniczne (eksport, odświeżenie cennika, przebudowa) pokazują `Loading` na przycisku i blokują ponowne wywołanie.
- W jednym obszarze maksymalnie **jeden** `Primary`.
- Kolejność w grupach: od lewej akcje mniej ważne, `Primary` na końcu (prawa strona). W dialogach: „Anuluj” (Secondary) · „Akcja” (Primary).

**A11y:** `SemanticProperties.Description` = etykieta (+ kontekst, jeśli etykieta jest krótka, np. „Eksportuj raport do CSV”); Enter/Space aktywuje.

**Do / Don't:**

- ✅ Etykieta czasownikiem w bezokoliczniku: „Eksportuj CSV”.
- ❌ „OK”, „Tak” jako etykiety akcji.
- ❌ Dwa `Primary` obok siebie.

```xml
<Button Style="{StaticResource Button.Primary}"
        Text="Eksportuj CSV"
        ImageSource="{FontImageSource FontFamily=Lucide, Glyph={x:Static res:Icons.Download}, Size=16, Color={DynamicResource ColorTextOnAction}}"
        Command="{Binding ExportCsvCommand}"
        SemanticProperties.Description="Eksportuj raport do pliku CSV" />
```

#### 4.1.2 IconButton

**Cel:** akcja reprezentowana tylko ikoną (odśwież, zamknij, więcej, zwiń sidebar).

**Anatomia:** kwadrat `size.control.height` (md) / `size.control.heightSm` (sm), ikona `icon.md` / `icon.sm` w środku.

**Warianty:** `Ghost` (domyślny, bez tła), `Secondary` (z obrysem `border.default` i tłem `surface.1`), `OnHero` (ikona `hero.text`, hover `#1AFFFFFF`).

**Wymagania:** tooltip (§4.6.5) z nazwą akcji i skrótem, np. „Odśwież (F5)”; `SemanticProperties.Description`. Minimalny obszar kliknięcia 24×24 nawet w `sm`.

**Stan specjalny `Spinning`** (odświeżanie): ikona obraca się (M15).

#### 4.1.3 ButtonGroup / SplitButton

- **ButtonGroup:** przyciski `Secondary` sklejone (wspólny obrys, wewnętrzne dzielniki `border.default`, promień tylko na zewnętrznych rogach). Użycie: „JSON | CSV” w eksporcie.
- **SplitButton:** część główna + strzałka menu (`chevron-down`). Użycie: „Eksportuj ▾” z opcjami CSV / JSON / Kopiuj jako tabelę. Preferowany nad ButtonGroup, gdy opcji > 2.

---

### 4.2 Formularze i filtry

#### 4.2.0 Wspólne dla pól

| Właściwość | Wartość |
| --- | --- |
| Wysokość | `size.control.height` |
| Tło | `surface.2` (L) / `surface.2` (D) |
| Obrys | 1 dp `border.strong`; hover `text.tertiary`; fokus `border.focus` 2 dp (bez dodatkowego pierścienia) |
| Promień | `radius.md` |
| Padding | 10 poziomo |
| Tekst | `type.body` `text.primary`; placeholder `text.tertiary` |
| Błąd | obrys `status.danger`, ikona `circle-alert` w polu po prawej, komunikat pod polem `type.caption` `status.danger` |
| Wyłączone | `opacity.disabled` |

**FormRow** (opakowanie pola): etykieta (`type.label`, `text.secondary`) nad polem, odstęp `space.100`; opcjonalny opis pod polem (`type.caption`, `text.tertiary`); komunikat błędu zastępuje opis. W Ustawieniach na `bp.wide` FormRow układa się poziomo: etykieta + opis po lewej (kolumna 280), pole po prawej (max 400).

**Walidacja:** inline, po opuszczeniu pola (`Unfocused`) i przy zmianie wartości po pierwszym błędzie. Komunikat mówi, co zrobić: „Podaj liczbę od 5 do 3600”. Przy autozapisie (§5.10) wartość niepoprawna NIE jest zapisywana; poprzednia poprawna obowiązuje do czasu poprawy.

#### 4.2.1 TextField

`Entry` ze stylem `Input.Default`. Opcjonalnie: ikona wiodąca, przycisk czyszczenia (`x`, pojawia się przy niepustej wartości), sufiks jednostki (`type.body` `text.tertiary`, np. „s”, „min”, „h”, „$ / 1M”).

**Warianty:** `Default`, `Mono` (font `MonoRegular` — ścieżki, klucze, ID), `Number` (`Keyboard=Numeric`, wyrównanie do prawej, font `NumMedium`, sufiks jednostki).

#### 4.2.2 SearchField

TextField z ikoną `search`, placeholderem opisującym zakres („Szukaj sesji: ID, projekt, model, źródło”), przyciskiem czyszczenia i skrótem `Kbd` „/” po prawej, gdy pole jest puste i nieaktywne. Filtrowanie z opóźnieniem 150 ms (debounce). `Esc` czyści, drugie `Esc` opuszcza pole.

#### 4.2.3 Select

Zastępuje `Picker` w wyglądzie: pole z wartością i ikoną `chevron-down`. Otwiera menu (§4.6.6) z opcjami; opcja wybrana ma ikonę `check`. Dla list > 8 pozycji menu ma pole wyszukiwania na górze (np. wybór źródła — 20 pozycji). Opcje źródeł zawierają `SourceBadge` `xs`.

**Klawiatura:** Enter/Space/Alt+↓ otwiera; ↑/↓ wybór; litera — skok do pozycji; Enter zatwierdza; Esc zamyka.

> Implementacja: własna kontrolka (pole + `Popup`/warstwa overlay w oknie). Natywny `Picker` MOŻE być użyty jako tymczasowe rozwiązanie wyłącznie ze stylem `Input.Default`.

#### 4.2.4 DateRangePicker

**Cel:** wybór zakresu dat raportu.

**Anatomia:** przycisk-pole z ikoną `calendar` i tekstem zakresu („1–26 lis 2026”) → popover z: listą presetów po lewej („Dziś”, „Wczoraj”, „Ostatnie 7 dni”, „Ostatnie 30 dni”, „Ten miesiąc”, „Poprzedni miesiąc”, „Ostatnie 90 dni”, „Ten rok”, „Cała historia”) oraz kalendarzem dwumiesięcznym po prawej (`bp.wide`) lub jednomiesięcznym (`bp.medium`/`narrow`). Dolny pasek: pola „Od” / „Do” (TextField z formatem `dd.MM.yyyy`) i „Zastosuj”.

**Zasady:** pierwszy dzień tygodnia zgodny z ustawieniem `StartOfWeek`; dni bez danych w kolorze `text.tertiary`; dzisiaj z obrysem `border.focus`; zakres z tłem `action.subtle`, krańce `action.primary` + `text.onAction`. Preset wybrany = zastosowany od razu (bez „Zastosuj”).

#### 4.2.5 SegmentedControl

**Cel:** wybór 2–5 wzajemnie wykluczających się opcji widocznych naraz (okres: „Dzień | Tydzień | Miesiąc”; metryka wykresu: „Koszt | Tokeny | Cache”).

**Wygląd:** kontener `surface.2`, promień `radius.md`, padding 2; segment aktywny `surface.1` + `elevation.1` (L) / `surface.3` (D), tekst `text.primary` `type.label`; nieaktywne `text.secondary`. Przesunięcie wskaźnika animowane (`base`, `standard`).

**Klawiatura:** ←/→ zmienia wybór (pętla nie zawija). **A11y:** każdy segment ma rolę przycisku-radio, opis „Okres: Tydzień, wybrane, 2 z 3”.

#### 4.2.6 Toggle (przełącznik)

`Switch` ze stylem: tor 32×18 (`surface.3` wył. / `action.primary` wł.), kciuk 14 biały z cieniem `elevation.1`. Etykieta zawsze po lewej (w FormRow), stan opisany słownie dla czytnika: „Uruchamiaj przy starcie systemu, włączone”. Zmiana działa natychmiast (autozapis).

#### 4.2.7 Checkbox

18×18, promień `radius.xs`, obrys `border.strong`; zaznaczony: tło `action.primary`, ikona `check` 14 `text.onAction`. Stan pośredni (`indeterminate`, np. „zaznacz wszystkie źródła”): ikona `minus`. Etykieta po prawej, klikalna. Używany tylko na listach wielokrotnego wyboru — dla pojedynczych opcji włącz/wyłącz używamy Toggle.

#### 4.2.8 FilterChip

**Cel:** szybkie włączanie wymiarów i filtrów (np. „Podział: Modele”, „Źródła”, „Projekty”; aktywne filtry w FilterBar).

**Warianty:**

- `Toggle` — wł./wył.; wyłączony: `surface.1` + obrys `border.default`, `text.secondary`; włączony: `action.subtle` + obrys `action.subtleBorder` + `action.onSubtle`, ikona `check` 14 z przodu.
- `Removable` — aktywny filtr z wartością („Źródło: Claude Code ✕”); ✕ usuwa filtr; Backspace przy fokusie usuwa.
- `Menu` — chip otwierający Select („Źródło: Wszystkie ▾”).

Wysokość `size.control.heightSm`, promień `radius.full`, padding 10, `type.label`.

#### 4.2.9 CodeEditor

`Editor` z fontem `MonoRegular` `type.mono`, tłem `surface.2`, obrysem jak pola, numeracją linii (opcjonalnie), min. 6 linii, autowysokość do 16 linii, potem przewijanie. Użycie: aliasy modeli, etykiety statusline, aliasy projektów (tryb zaawansowany). Pod edytorem opis formatu z przykładem (`type.caption`) i walidacja linii („Linia 3: brak znaku „=””).

#### 4.2.10 KeyValueEditor

Edytowalna lista par klucz → wartość (aliasy projektów, aliasy modeli w trybie podstawowym). Wiersz: TextField(klucz, Mono) · ikona `arrow-right` · TextField(wartość) · IconButton `trash-2` (DangerGhost). Pod listą „+ Dodaj alias” (Button Ghost). Przełącznik „Edytuj jako tekst” zamienia widok na CodeEditor (te same dane).

---

### 4.3 Kontenery i metryki

#### 4.3.1 Card

**Cel:** grupowanie treści w siatce.

**Anatomia:** [CardHeader: overline/tytuł · akcje (IconButton Ghost, Link „Zobacz wszystko →”)] · treść · [CardFooter: caption/akcje].

**Wygląd:** `surface.1`, obrys `border.default`, `radius.lg`, padding `layout.card.padding`, `elevation.1`. Nagłówek: tytuł `type.title.card` lub `type.overline`; odstęp nagłówek–treść `space.300`.

**Warianty:**

- `Default`.
- `Interactive` — cała karta klikalna (otwiera drawer lub stronę): hover M8, kursor ręki, fokus z klawiatury; NIE zawiera innych przycisków poza menu „więcej”.
- `Accent` — pasek 3 dp po lewej w kolorze źródła/serii (np. karta źródła). Pasek przycięty do promienia karty.
- `Inset` — bez obrysu, tło `surface.2`, `radius.md` (kafel wewnątrz karty).

```xml
<controls:Card Title="Koszt wg źródła" Variant="Default">
    <controls:Card.Actions>
        <controls:IconButton Glyph="{x:Static res:Icons.Ellipsis}" ToolTipProperties.Text="Więcej" />
    </controls:Card.Actions>
    <charts:DonutChart Series="{Binding SourceShares}" />
</controls:Card>
```

#### 4.3.2 StatTile (KPI)

**Cel:** jedna liczba z kontekstem.

**Anatomia (od góry):** overline (np. „KOSZT · 30 DNI”) · liczba `type.metric.lg` (kolor `text.primary`; waluta/jednostka mniejszym Spanem) · wiersz kontekstu: `TrendDelta` + porównanie („vs poprzednie 30 dni”) · [opcjonalnie Sparkline 32 wysokości na dole, pełna szerokość].

**Wygląd:** Card `Default`, min. wysokość 112 (comfortable) / 96 (compact). Klikalny wariant `Interactive` prowadzi do szczegółowego widoku.

**Stany:** `Loading` — skeleton (overline + prostokąt liczby 60% szer. + linia kontekstu); `NoData` — liczba „—” w `text.tertiary` i opis „Brak danych w okresie”.

#### 4.3.3 HeroMetric

**Cel:** główna liczba ekranu „Dziś” (koszt dnia) — największy element w aplikacji.

**Anatomia:** overline z kropką statusu („● DZIŚ” — kropka `status.success` gdy dane aktualne, `status.warning` gdy sync nieaktualny > 2× interwał) · liczba `type.display` (`hero.text`) z `PricingMarker` · podpis „1,24 M tokenów · 12 sesji” (`type.body`, `hero.textSecondary`) · `TrendDelta` w wariancie `OnHero` („▲ 18% vs wczoraj” / „▼ 6% vs średnia 7 dni”) · [Budżet dnia: ProgressBar `OnHero` + „8,20 $ z 15,00 $ budżetu”] (`Wymaga danych`).

**Wygląd:** na karcie Hero (gradient §2.1.5, `radius.xl`, padding `layout.card.paddingLg`).

**Zachowanie:** M3 przy zmianie wartości; tooltip na liczbie z rozbiciem: „Zapisany przez narzędzia: X · wyliczony z cennika: Y”.

#### 4.3.4 TrendDelta

Strzałka (`trending-up` / `trending-down` / `minus` 14) + procent `type.label` `NumMedium`. **Kolor zależy od znaczenia, nie od kierunku:** dla kosztów i tokenów wzrost = `status.warning`, spadek = `status.success`; dla udziału cache wzrost = `status.success`. Gdy zmiana < 2% → neutralny `text.tertiary` i „bez zmian”. Gdy brak bazy porównania → nie renderujemy (nie pokazujemy „∞%”). Tekst dla czytnika: „wzrost o 18 procent względem wczoraj”.

Wariant `OnHero`: tekst `hero.text`, tło pigułki `#1FFFFFFF`, kolor znaczenia tylko na ikonie (jasne warianty: `#FFB4B4` wzrost kosztu, `#9FF0C4` spadek).

#### 4.3.5 Section

Nagłówek sekcji strony: `type.title.section` + opcjonalny opis `type.caption` `text.tertiary` + akcje po prawej. Odstęp od poprzedniej sekcji `layout.section.gap`. Sekcja MOŻE być zwijana (`Collapsible`): chevron po lewej, stan zapamiętywany per ekran (§5.4).

#### 4.3.6 Divider

`BoxView` 1 dp `border.subtle`. Pionowy w paskach narzędzi: wysokość 20, `border.default`.

#### 4.3.7 KeyValueList

Lista „etykieta — wartość” w drawerach i kartach szczegółów. Etykieta `type.body` `text.secondary` (lewa, 40%), wartość `type.body.strong` lub `type.table.num` (prawa, wyrównanie do prawej). Wiersze `size.row.heightDense`, dzielnik `border.subtle`. Wartości kopiowalne (ID, ścieżki) mają IconButton `copy` `sm` widoczny przy hover/fokusie.

---

### 4.4 Wskaźniki postępu i limitów

#### 4.4.1 ProgressBar

**Anatomia:** tor (`data.track`, wysokość `size.progress.bar`, `radius.full`) + wypełnienie (`radius.full`) + [znaczniki progów: pionowe kreski 1×(wysokość+4) w `text.tertiary` przy 80% i 100% — opcjonalnie].

**Kolor wypełnienia:** domyślnie `action.primary` lub kolor metryki/źródła. **Tryb progowy** (limity, budżety): < 80% kolor bazowy, 80–99% `status.warning`, ≥ 100% `status.danger` (wypełnienie pełne + ukośne paski 45° `#33FFFFFF` dla rozróżnienia bez koloru).

**Etykiety:** obok paska zawsze liczba („62%”) — pasek nigdy nie jest jedynym nośnikiem wartości. Wartości > 100% → pasek pełny, etykieta „112%”.

Wariant `OnHero`: tor `hero.ringTrack`, wypełnienie `hero.text` (progowo: `#FFD27A` / `#FF9A9A`).

#### 4.4.2 LimitGauge

**Cel:** pełna informacja o jednym limicie (5 h, 7 dni, blok, budżet).

**Anatomia:** wiersz 1: nazwa limitu (`type.label`) · `StatusBadge` (OK / „Blisko limitu” / „Przekroczono”) · procent (`type.metric.sm`, prawa) · wiersz 2: ProgressBar (tryb progowy) · wiersz 3: „Reset za 2 h 14 min · 16:30” (`type.caption`, `text.tertiary`) · [prognoza: „Przy obecnym tempie: 94% przed resetem” — `Docelowe` dla bloku, `Wymaga danych` dla 5 h/7 d].

**Stany:** `Live` (dane na żywo — ikona `radio` 12 + tooltip „Dane na żywo z API Anthropic, odświeżone 14:02”), `Estimated` (z historii — „~” przy procencie), `Unavailable` („Zaloguj się ponownie w Claude Code, aby zobaczyć limity” + Link „Jak to naprawić”), `NotApplicable` (źródło bez limitów — komponent się nie renderuje).

#### 4.4.3 OnionRings — sygnatura marki

**Cel:** koncentryczne pierścienie limitów — jednym rzutem oka „jak blisko jestem limitów”. Nawiązanie do warstw cebuli z logo.

**Anatomia:** do 3 koncentrycznych pierścieni (od zewnątrz): **7 dni** → **5 godzin** → **Aktywny blok** (lub **Budżet dnia**, gdy brak bloku i ustawiony budżet). Grubość pierścienia 10 (comfortable) / 8 (compact), odstęp między pierścieniami 4. Każdy pierścień: tor (`data.track`, na hero `hero.ringTrack`) + łuk wartości od godz. 12 zgodnie z ruchem wskazówek, zakończenia zaokrąglone. W środku: procent najbardziej krytycznego limitu (`type.metric.md`) i jego nazwa (`type.caption`).

**Legenda:** obok (wide) lub pod (narrow) — dla każdego pierścienia: kropka koloru, nazwa, procent, „reset za …”. Legenda jest obowiązkowa (dostępność, kolory).

**Kolory łuków:** 7 d `color.chart.cat.1`, 5 h `color.chart.cat.4`, blok `color.source.claude`; progowo jak ProgressBar (80% warning, 100% danger + przerywany łuk). Na hero: łuki `hero.text` z kryciem 100% / 75% / 55% (od zewnątrz), progi `#FFD27A` / `#FF9A9A`.

**Rozmiar:** `size.ring.hero` na hero; 120 w kartach; 40 (bez tekstu w środku) w mini-widoku i trayu.

**Ruch:** M11; przy przekroczeniu progu M19.

**A11y:** cały komponent jednym elementem semantycznym z opisem: „Limity: 7 dni 41 procent, reset w piątek 09:00; 5 godzin 62 procent, reset za 2 godziny 14 minut; aktywny blok 48 procent.” Klik → ekran Limity.

```xml
<controls:OnionRings Size="{DynamicResource SizeRingHero}"
                     OnHero="True"
                     Rings="{Binding LimitRings}"
                     SemanticProperties.Description="{Binding LimitRingsDescription}" />
```

#### 4.4.4 BurnRateBadge

**Cel:** tempo spalania aktywnego bloku.

**Anatomia:** pigułka: ikona `flame` 14 · „4,20 $/h” (`NumMedium`) · separator · „1,8 k tok/min” · słowo poziomu („normalne” / „umiarkowane” / „wysokie”). Tło `status.*Bg` odpowiednie do poziomu, tekst `color.burn.*`. Tooltip: „Tempo spalania: średni koszt na godzinę w aktywnym bloku. Próg umiarkowany: X, wysoki: Y.”

#### 4.4.5 StatusBadge

Mała pigułka statusu: kropka 6 + tekst `type.caption` `SemiBold`; tło `status.*Bg`, tekst `status.*`. Warianty: `Success`, `Warning`, `Danger`, `Info`, `Neutral` (`surface.2` / `text.secondary`), `Live` (kropka z pulsem M10). Wysokość 20, padding 8, `radius.full`. Zawsze ze słowem — nigdy sama kropka.

#### 4.4.6 Spinner

`ActivityIndicator` 16 (`action.primary`) tylko w kontekście (przycisk, StatusBar, wiersz). Nigdy na środku pustego ekranu, jeśli można pokazać skeleton.

---

### 4.5 Dane

#### 4.5.1 DataTable

**Wiersze-separatory (1.6):** wiersz implementujący `ISeparatorRow` (`IsSeparator`, `SeparatorText`) jest rysowany jako cienka linia z podpisem (ikona `pause`, `type.caption`), w wysokości `size.row.heightDense`; nie da się go zaznaczyć ani kliknąć, klawiatura go pomija. Użycie: przerwy między blokami (§6.5).

**Cel:** podstawowy sposób prezentacji list danych (raporty, sesje, projekty, modele, bloki, źródła).

**Anatomia:**

- **Nagłówek** (sticky): tło `surface.2`, wysokość `size.row.heightDense`, tekst `type.label` `text.secondary`; kolumny sortowalne mają ikonę `arrow-up-down` 14 (nieaktywna, widoczna przy hover) / `arrow-up` / `arrow-down` (aktywna, `text.primary`).
- **Wiersze:** wysokość `size.row.height`, padding poziomy `space.400`, dzielnik `border.subtle`. Hover: `state.hover`. Zaznaczony (otwarty w drawerze): `state.selected` + pasek 2 dp `action.primary` przy lewej krawędzi.
- **Kolumny liczbowe:** wyrównane do prawej, `type.table.num`; kolumna kosztu w `data.cost`, pozostałe liczby `text.primary`.
- **Kolumna tożsamości** (pierwsza): SourceBadge / ikona + nazwa (`type.body.strong`) + podtytuł (`type.caption`, `text.tertiary`), obcinanie z tooltipem.
- **Wiersz podsumowania** (opcjonalny, sticky na dole): „Razem”, tło `surface.2`, `type.body.strong`.
- **Inline bar** (opcjonalnie w kolumnie kosztu/tokenów): poziomy pasek udziału 4 dp pod liczbą, w kolorze metryki z kryciem 35%, długość proporcjonalna do maksimum w tabeli.
- **Rozwijany wiersz** (breakdown): chevron w pierwszej kolumnie; podwiersze z wcięciem 24, `type.body` `text.secondary`, tło `surface.2` 50%. Zastępuje tekstowe „▸ źródło / └ model”.

**Definicja kolumny** (w ViewModelu ekranu): `Key`, `Header`, `Align` (Start/End), `Width` (stała / `*` / auto, min.), `Priority` (1 = zawsze, 2 = ukryj w `bp.narrow`, 3 = ukryj w `bp.medium`), `Sortable`, `Format` (Cost/Tokens/Percent/Date/Duration/Text/Mono).

**Zachowanie:**

| Akcja | Mysz | Klawiatura |
| --- | --- | --- |
| Sortowanie | klik w nagłówek (rosnąco → malejąco → domyślne) | fokus na nagłówku + Enter |
| Otwarcie szczegółów | klik w wiersz → Drawer | Enter |
| Ruch między wierszami | — | ↑/↓ (gdy drawer otwarty — aktualizuje drawer), PgUp/PgDn, Home/End |
| Rozwinięcie wiersza | klik w chevron | → / ← |
| Menu kontekstowe | prawy przycisk | Shift+F10 / klawisz menu |
| Kopiowanie | menu kontekstowe → „Kopiuj wiersz” / „Kopiuj ID” | Ctrl+C (kopiuje wiersz jako TSV) |

- Sortowanie i jego kierunek są zapamiętywane per ekran (§5.4).
- Wirtualizacja: `CollectionView` z `ItemsLayout` liniowym; > 300 wierszy → paginacja „Pokaż kolejne 300” na końcu (zamiast twardego limitu bez informacji). Licznik „Pokazano 300 z 1 284”.
- **Zwijanie do kart** (szerokość treści < 760): każdy wiersz staje się kartą: linia 1 — tożsamość + koszt (prawa); linia 2 — kolumny priorytetu 1–2 jako „etykieta wartość · etykieta wartość”; nagłówek tabeli zamienia się na Select „Sortuj: …”.

**Stany:** `Loading` (5 wierszy skeleton o szerokościach z kolumn), `Empty` (EmptyState w miejscu wierszy, nagłówek zostaje), `NoResults` (po filtrze/wyszukiwaniu: „Brak wyników dla „xyz”” + „Wyczyść filtry”), `Error` (ErrorState w miejscu wierszy).

**A11y:** każdy wiersz jest jednym elementem z opisem złożonym z kolumn priorytetu 1–2: „Sesja onion-app, Claude Code, 1,2 miliona tokenów, koszt 3,40 dolara, 26 listopada 14:02”. Nagłówki sortowalne ogłaszają stan sortowania.

```xml
<controls:DataTable ItemsSource="{Binding Rows}"
                    Columns="{Binding Columns}"
                    SortKey="{Binding SortKey, Mode=TwoWay}"
                    SortDescending="{Binding SortDescending, Mode=TwoWay}"
                    SelectedItem="{Binding SelectedRow, Mode=TwoWay}"
                    RowActivatedCommand="{Binding OpenDetailsCommand}"
                    State="{Binding TableState}"
                    TotalRow="{Binding TotalRow}" />
```

#### 4.5.2 ListItem

Prosty wiersz listy poza tabelą (lista źródeł w Ustawieniach, wyniki w palecie poleceń, top modele w karcie). Anatomia: [leading: SourceBadge/ikona] · tytuł + podtytuł · [trailing: liczba / Toggle / chevron]. Wysokość `size.row.height`, hover jak w tabeli.

#### 4.5.3 SourceBadge

**Cel:** tożsamość źródła (narzędzia).

**Warianty rozmiaru:** `xs` 16 (w menu, chipach), `sm` `size.avatar.sm` (tabele), `md` `size.avatar.md` (karty), `lg` 48 (drawer, strona źródła).

**Wygląd:** kwadrat z zaokrągleniem `radius.sm` (xs/sm) / `radius.md` (md/lg), tło `color.source.<id>.avatar`, inicjał (1–2 litery) `InterSemiBold` w kolorze z tabeli §2.1.6 (rozmiar 45% boku). Docelowo MOŻE być zastąpiony logotypem narzędzia (SVG monochromatyczny na tym samym tle), jeśli licencja na to pozwala.

**Z etykietą:** `SourceBadge` + nazwa (`type.body.strong`) + opcjonalnie dostawca (`type.caption`). Stan `Disabled` (źródło wyłączone): odbarwiony (tło `surface.3`, inicjał `text.tertiary`) + StatusBadge „Wyłączone”. Stan `NotDetected`: obrys przerywany `border.strong`, tło przezroczyste.

#### 4.5.4 ModelTag

Pigułka z nazwą modelu: `type.mono.sm`, tło `surface.2`, obrys `border.subtle`, `radius.xs`, wysokość 20, padding 6. Nazwa skrócona przez `Fmt.Model()` (bez prefiksu `claude-` i daty); pełna w tooltipie. Opcjonalnie poziom szybkości jako sufiks („fast”, „flex”) w `text.tertiary`. Model bez ceny ma `PricingMarker` wewnątrz tagu.

#### 4.5.5 PricingMarker

**Cel:** uczciwość danych — oznaczenie kosztu niepełnego lub szacowanego.

| Znak | Znaczenie | Wygląd | Tooltip |
| --- | --- | --- | --- |
| `*` | część modeli bez ceny — koszt zaniżony | indeks górny przy liczbie, `status.warning`, `InterSemiBold` | „Koszt niepełny: brak ceny dla modeli: X, Y. Dodaj cenę w Ustawienia → Cennik.” + Link |
| `~` | część cen szacowana | indeks górny, `status.info` | „Koszt szacowany: ceny modeli X, Y pochodzą z przybliżenia.” |
| `≈` | wartość przeliczona na walutę wyświetlania | prefiks przed liczbą, `text.tertiary` | „Przeliczono z 12,34 $ po kursie 4,02 zł (ręczny, 20.11.2026).” |

Marker jest elementem fokusowalnym (Tab), tooltip pokazuje się też przy fokusie. Na ekranach z listami w stopce jest **PricingNote** (§4.7.6) zbierająca wszystkie modele bez ceny/szacowane w widoku.

#### 4.5.6 Money i Quantity (formatowanie)

Kontrolki tekstowe `MoneyLabel` i `TokensLabel` (pochodne `Label`) formatują wartości wg §5.1, renderują jednostkę mniejszym `Span` (w rozmiarach ≥ `type.metric.md`), dodają markery i wykonują M3. Ekrany NIE formatują liczb samodzielnie w XAML (`StringFormat`) — formatowanie pochodzi z `Fmt` / tych kontrolek.

#### 4.5.7 TokenBreakdown

**Cel:** rozkład tokenów na typy.

**Anatomia:** pasek skumulowany (wysokość 8, `radius.full`) z segmentami: wejście (`data.input`), wyjście (`data.output`), zapis cache (`data.cacheWrite`), odczyt cache (`data.cacheRead`) + legenda pod spodem w siatce 2×2 (wide: 4×1): kropka · nazwa · liczba (`type.table.num`) · procent (`text.tertiary`). Rozumowanie (reasoning) i zapis 1 h pokazywane jako podpozycje („w tym reasoning 120 k”, „w tym 1 h: 300 k”). Udział cache = (zapis + odczyt) / suma — wyróżniony pod paskiem: „62% z cache”.

---

### 4.6 Nakładki

#### 4.6.1 Drawer (panel szczegółów)

**Cel:** szczegóły elementu listy (okres, sesja, blok, projekt, model, źródło) bez opuszczania listy. Zastępuje wszystkie `DisplayAlert` z danymi.

**Wymiary:** szerokość 420 (wide/medium), `min(420, szerokość treści)` w narrow; użytkownik MOŻE zmienić szerokość przeciągając lewą krawędź (360–640, zapamiętywane).

**Tryby:** `Push` (bp.wide — treść zwęża się, tabela pozostaje interaktywna) i `Overlay` (bp.medium/narrow — nad treścią, ze scrimem `color.scrim`; klik w scrim zamyka).

**Anatomia:**

1. **DrawerHeader** (sticky): overline z typem („SESJA”, „OKRES”, „BLOK”, …) · tytuł `type.title.section` (obcięty, pełny w tooltipie) · podtytuł (`type.caption`) · akcje: IconButton „Poprzedni” (`chevron-up`, ↑/K), „Następny” (`chevron-down`, ↓/J), „Więcej” (menu: Kopiuj ID, Kopiuj jako JSON, Otwórz folder projektu), „Zamknij” (`x`, Esc).
2. **Podsumowanie:** 2–3 StatTile w wersji kompaktowej (koszt, tokeny, czas trwania/wywołania).
3. **Sekcje** (Section, odstęp `space.600`): TokenBreakdown · „Modele” (mini-tabela: model, tokeny, koszt, udział) · „Źródła” · kontekst specyficzny (np. dla sesji: oś czasu aktywności sparkline; dla bloku: wykres skumulowanego kosztu w bloku z prognozą) · „Szczegóły” (KeyValueList: ID, ścieżka projektu, pierwsze/ostatnie zdarzenie, koszt zapisany vs wyliczony, liczba wywołań).
4. **DrawerFooter** (opcjonalny, sticky): akcje kontekstowe („Pokaż sesje z tego dnia”, „Filtruj po tym projekcie”).

**Zachowanie:** otwarcie M6; zmiana elementu przy otwartym drawerze — treść crossfade 120 ms (bez ponownego wjazdu); fokus przy otwarciu przechodzi na tytuł drawera; przy zamknięciu wraca do wiersza źródłowego. Stan otwarcia (id elementu) NIE jest zapamiętywany między uruchomieniami.

**A11y:** rola dialogu niemodalnego (Push) / modalnego (Overlay, z pułapką fokusu); tytuł ogłaszany przy otwarciu.

```xml
<controls:Drawer IsOpen="{Binding IsDetailsOpen}"
                 Mode="{Binding LayoutState, Converter={StaticResource LayoutToDrawerMode}}"
                 Overline="SESJA"
                 Title="{Binding Selected.Title}"
                 Subtitle="{Binding Selected.Subtitle}"
                 PreviousCommand="{Binding SelectPreviousCommand}"
                 NextCommand="{Binding SelectNextCommand}"
                 CloseCommand="{Binding CloseDetailsCommand}">
    <views:SessionDetailsView BindingContext="{Binding Selected}" />
</controls:Drawer>
```

#### 4.6.2 Dialog

**Cel:** wyłącznie potwierdzenie akcji nieodwracalnych lub kosztownych („Przebuduj bazę”, „Usuń wszystkie nadpisania”) oraz krótkie formularze (dodanie nadpisania ceny).

**Wygląd:** szerokość 440 (sm) / 560 (md), `surface.overlay`, `radius.xl`, `elevation.3`, padding 24; tytuł `type.title.section`; treść `type.body` `text.secondary`; stopka z przyciskami po prawej („Anuluj” Secondary, akcja Primary lub Danger). Scrim `color.scrim`. Esc = Anuluj; Enter = akcja główna (z wyjątkiem Danger — wymaga Tab + Enter lub kliknięcia).

**Wariant informacyjny** (1.5): `DialogService.InfoAsync` — jeden przycisk „Zamknij” (Secondary), bez „Anuluj”; Enter i Esc zamykają. Używany tylko przez „Skróty klawiszowe” (§9.6).

**Treść potwierdzenia mówi o skutku:** „Baza zostanie odtworzona z logów. Wpisy z logów, które narzędzia już usunęły, zostaną utracone. Trwa to zwykle 10–60 s.”

#### 4.6.3 Toast

**Cel:** krótkie potwierdzenie wyniku akcji („Zapisano”, „Wyeksportowano: onionllm-daily-…csv”).

**Wygląd:** prawy dolny róg treści (nad StatusBar), szerokość 360, `surface.inverse`, tekst `text.onInverse`, `radius.lg`, `elevation.3`, ikona statusu (`circle-check` / `circle-alert` / `info`) w jaśniejszym wariancie koloru statusu, opcjonalnie akcja (Link na inverse: „Otwórz folder”, „Cofnij”) i przycisk zamknięcia. Maks. 3 toasty w stosie (najnowszy na dole). Auto-zamknięcie 5 s (pauza przy hover/fokusie); błędy nie zamykają się same.

**A11y:** ogłaszany przez `SemanticScreenReader.Announce` (polite; błędy — assertive).

#### 4.6.4 Banner

**Cel:** trwały komunikat o stanie, który wymaga uwagi lub działania (dane częściowe, brak logowania Claude, cennik nieaktualny, przekroczony budżet).

**Wygląd:** pełna szerokość treści pod PageHeader, tło `status.*Bg`, obrys 1 dp `status.*` z kryciem 30%, `radius.md`, padding `12,16`, ikona statusu 20, tytuł `type.body.strong`, opis `type.body`, akcje (Button `sm` Secondary/Link) po prawej, opcjonalny przycisk zamknięcia (zamknięcie zapamiętane do zmiany stanu). Maks. **1** banner na ekranie — gdy wiele, priorytet: danger > warning > info, reszta zwinięta w „+2 inne komunikaty”.

#### 4.6.5 Tooltip

`ToolTipProperties.Text` dla prostych tekstów. Rozszerzony tooltip (`RichTooltip`, popover bez fokusu) dla wykresów i markerów: `surface.overlay`, obrys `border.default`, `radius.sm`, `elevation.2`, padding 8–10, maks. szer. 280, `type.caption`. Opóźnienie pokazania 400 ms, ukrycia 100 ms. Pokazuje się także przy fokusie klawiatury.

#### 4.6.6 Menu i menu kontekstowe

`surface.overlay`, obrys `border.default`, `radius.lg`, `elevation.2`, padding 4; pozycje wysokości `size.row.heightDense`, `radius.sm`, ikona 16 + etykieta + skrót (`Kbd`) po prawej; separator `border.subtle`; pozycja niebezpieczna w `status.danger`. Na desktopie używamy `MenuFlyout` (`FlyoutBase.ContextFlyout`) dla menu kontekstowych tam, gdzie to wystarcza.

#### 4.6.7 CommandPalette

**Cel:** jedno miejsce dostępu do wszystkiego — nawigacja, akcje, wyszukiwanie encji. Otwierana `Ctrl+K` (⌘K na macOS) z dowolnego miejsca.

**Wygląd:** wyśrodkowana w górnej 1/5 okna, szerokość 640 (max 90% okna), `surface.overlay`, `radius.xl`, `elevation.3`, scrim `color.scrim` z kryciem 50%.

**Anatomia:** pole wyszukiwania (bez obrysu, `type.body` 16, ikona `search`, placeholder „Wpisz polecenie lub szukaj…”) · dzielnik · lista wyników pogrupowanych (`type.overline` nad grupą) · stopka z podpowiedziami klawiszy („↑↓ wybierz · ↵ otwórz · Esc zamknij”).

**Grupy wyników (kolejność):**

1. **Ostatnie** (puste zapytanie): 5 ostatnio użytych poleceń/encji.
2. **Przejdź do** — wszystkie ekrany i podstrony Ustawień („Ustawienia → Cennik”).
3. **Akcje** — „Odśwież dane” (F5), „Eksportuj raport CSV”, „Eksportuj raport JSON”, „Przełącz motyw”, „Przełącz gęstość”, „Tryb kompaktowy okna”, „Zawsze na wierzchu”, „Odśwież cennik”, „Sprawdź rozbieżności kosztu”, „Otwórz folder danych”, „Przebuduj bazę…”.
4. **Sesje** — dopasowania po ID/projekcie/modelu (maks. 5, „Pokaż wszystkie w Sesjach →”).
5. **Projekty**, **Modele**, **Źródła** — dopasowania nazw (maks. 3 każda).

**Dopasowanie:** rozmyte (fuzzy), bez rozróżniania wielkości liter i polskich znaków („zrodla” → „Źródła”); dopasowane znaki pogrubione. Prefiksy zakresu: `>` tylko akcje, `#` sesje, `@` projekty.

**Zachowanie:** Enter wykonuje/otwiera (encje otwierają ekran z otwartym drawerem); Esc zamyka i przywraca fokus. Każde polecenie z palety ma też swoje miejsce w UI (paleta to skrót, nie jedyna droga).

**Implementacja (1.5):** `Controls/CommandPalette.cs` (widok, M7, podświetlenie dopasowań SemiBold w `action.onSubtle`) + `Navigation/CommandPalette.cs` (`CommandPaletteService`: grupy, prefiksy, „Ostatnie” w `ViewState`, indeks encji) + `ViewModels/FuzzyMatch.cs` (dopasowanie, testy w `FuzzyMatchTests`). Klawisze palety (↑↓, PgUp/PgDn, Enter, Esc, Tab — pułapka fokusu) obsługuje `ShortcutService` przed innymi skrótami. Encje: sesje, projekty i modele z ostatnich 30 dni (indeks budowany w tle przy otwarciu, unieważniany po synchronizacji), źródła — wszystkie 20; ekran docelowy szuka encji w swoim zakresie, a gdy jej tam nie ma — przełącza na „Cała historia” (bez zapamiętania). Akcje „Eksportuj raport CSV/JSON” eksportują bieżący widok Raportów, „Odśwież cennik”, „Sprawdź rozbieżności kosztu” i „Przebuduj bazę…” otwierają właściwą podstronę Ustawień i uruchamiają tam polecenie. Dodatkowa akcja „Skróty klawiszowe” (?). **Nierenderowane:** „Tryb kompaktowy okna” (`Wymaga platformy`).

---

### 4.7 Nawigacja i struktura

#### 4.7.1 Sidebar

**Anatomia (od góry):**

1. **Nagłówek:** logo (§7.7, 28) + „OnionLLM” (`type.title.card`), wysokość `size.header.height`. W stanie zwiniętym tylko logo.
2. **Grupy nawigacji** (nagłówek grupy `type.overline`, ukryty w stanie zwiniętym — zastąpiony dzielnikiem):
   - **Przegląd:** Dziś · Limity
   - **Analiza:** Raporty · Sesje · Bloki · Projekty · Modele
   - **System:** Źródła
3. **Odstęp elastyczny.**
4. **Dół:** Ustawienia · przycisk „Tryb kompaktowy” (`picture-in-picture-2`) · przycisk „Zwiń panel” (`panel-left-close` / `panel-left-open`, Ctrl+B).

**Wygląd:** tło `bg.sidebar`, prawy obrys `border.default`, szerokość 240 / 64 (zwinięty) / 56 (narrow), padding 8.

**Stan aktywności źródeł** w Sidebarze: przy „Źródła” licznik problemów (StatusBadge `Warning` z liczbą), gdy któreś wykryte źródło ma błędy parsowania. Przy „Limity” kropka `status.warning`/`danger`, gdy któryś limit ≥ 80% / ≥ 100%.

#### 4.7.2 NavItem

Wysokość `size.nav.item`, `radius.md`, padding 10, ikona `icon.md` + etykieta `type.label`; opcjonalny badge po prawej. Nieaktywny: ikona i tekst `text.secondary`; hover `state.hover`; **aktywny:** tło `action.subtle`, ikona i tekst `action.onSubtle`, `InterSemiBold`. Zwinięty: tylko ikona, tooltip z nazwą i skrótem („Raporty · Ctrl+3”). Fokus: pierścień.

#### 4.7.3 PageHeader

**Anatomia:** [przycisk rozwinięcia sidebaru — tylko narrow] · tytuł strony `type.title.page` · opcjonalny podtytuł/zakres (`type.caption`, `text.tertiary`, np. „1–26 lis 2026 · 4 źródła”) · odstęp · **akcje strony** po prawej (IconButton „Odśwież” ze `Spinning`, SplitButton „Eksportuj”, inne). Pod spodem (lub w tym samym wierszu na wide, jeśli się mieści) **FilterBar**.

Wysokość `size.header.height`; sticky z obrysem przy przewinięciu (§2.9.3).

**Implementacja:** `Controls/PageHeader` umieszczany przez ekran na górze swojego widoku (`Title`, `Subtitle`, akcje ekranu jako zawartość). Przycisk „Odśwież” i przycisk panelu (narrow) są wspólne — wiązane z `ShellViewModel`. Kontrolka ma `[ContentProperty(Actions)]`, dlatego jej własny XAML MUSI używać jawnego `<ContentView.Content>` (inaczej szablon trafiłby do `Actions` i utworzył cykl w drzewie).

#### 4.7.4 FilterBar

**Cel:** wszystkie filtry ekranu w jednym miejscu.

**Anatomia (od lewej):** SegmentedControl okresu/trybu (jeśli dotyczy) · DateRangePicker · FilterChip `Menu` „Źródło” · inne chipy (projekty, modele) · SearchField (jeśli dotyczy, rozciągliwy) · odstęp · Link „Wyczyść filtry” (widoczny tylko, gdy filtry ≠ domyślne).

**Zachowanie:** zmiana filtra = natychmiastowe przeliczenie (bez „Zastosuj”), wynik z M5. Na narrow FilterBar zawija się do dwóch linii; jeśli nadal brakuje miejsca — chipy zwijają się do przycisku „Filtry (3)” otwierającego popover.

#### 4.7.5 StatusBar (SyncIndicator)

**Cel:** zawsze widoczna informacja o świeżości i kompletności danych.

**Anatomia:** pasek wysokości 28 na dole obszaru treści, tło `bg.canvas`, górny obrys `border.subtle`, `type.caption` `text.tertiary`, elementy rozdzielone „·”:

1. **Sync:** ikona `refresh-cw` 12 (obraca się w trakcie) + „Zaktualizowano 14:02” / „Aktualizowanie…” / „Błąd aktualizacji — Szczegóły” (`status.danger`, Link) / „Błąd konfiguracji — Szczegóły” (`status.danger`; plik konfiguracji nieczytelny — użyto wartości domyślnych).
2. **Źródła:** „4 z 20 aktywnych” (klik → Źródła) — aktywne = włączone i wykryte (ostatnia synchronizacja znalazła pliki).
3. **Kompletność:** gdy są nieczytelne pliki: „3 pliki pominięte” (`status.warning`, klik → Ustawienia → Diagnostyka).
4. **Cennik:** „Cennik: LiteLLM, 26.11” / „Cennik: wbudowany, 20.11” (snapshot w aplikacji, gdy nie pobrano cennika) / „Cennik offline” (`status.warning`) / „Cennik nieaktualny (8 dni)”.
5. Po prawej: ikona `hard-drive` + „Dane lokalne” (tooltip: „Dane czytane wyłącznie z dysku tego komputera. Sieć: tylko cennik i limity Claude.”).

#### 4.7.6 PricingNote

Stopka list i raportów: ikona `info` 14 + „Koszt niepełny: brak ceny dla 2 modeli (gpt-x, foo-1)” · „Szacowany: 1 model” · Link „Dodaj ceny”. `type.caption`, `status.warning` dla `*`, `text.tertiary` dla `~`. Renderowana tylko, gdy niepusta.

#### 4.7.7 Tabs

Zakładki poziome (wewnątrz drawera, strony źródła, podstron): etykiety `type.label`, aktywna `text.primary` + podkreślenie 2 dp `action.primary` (animowane przesunięcie), nieaktywne `text.secondary`; dolny obrys kontenera `border.subtle`. ←/→ zmienia zakładkę.

**SettingsNav** — pionowa lista podstron Ustawień (jak NavItem bez ikon, szerokość 220) — na narrow zamienia się w Select nad treścią.

#### 4.7.8 Kbd

Skrót klawiszowy: `type.kbd`, tło `surface.2`, obrys `border.default` z dolnym obrysem 2 dp, `radius.xs`, padding `2,6`. Klawisze modyfikujące per platforma: „Ctrl” (Windows) / „⌘” (macOS); kombinacje jako sąsiadujące Kbd bez „+” („Ctrl” „K”).

#### 4.7.9 EmptyState

**Anatomia:** ilustracja (koncentryczne pierścienie cebuli z ikoną Lucide w środku, 64–96, kolory `surface.3`/`border.default`/`text.tertiary`) · tytuł `type.title.card` · opis `type.body` `text.secondary` (maks. 2 zdania: co oznacza brak danych + co zrobić) · akcja (Button Secondary lub Primary, jeśli jest jedna oczywista).

**Warianty:** `Page` (wyśrodkowany w treści, margines `space.1000`), `Inline` (wewnątrz karty/tabeli, bez ilustracji lub z ikoną 24).

Teksty stanów pustych → §8.5.

#### 4.7.10 Skeleton

Prostokąty `surface.2` z animacją shimmer (M16), `radius.xs` dla tekstu (wysokość = rozmiar fontu), `radius.lg` dla kart. Skeleton odwzorowuje docelowy układ (te same wymiary), aby po załadowaniu nic nie skakało. Pokazywany wyłącznie, gdy **brak jakichkolwiek danych** do wyświetlenia; przy odświeżaniu istniejących danych — tylko M15 w PageHeader/StatusBar.

#### 4.7.11 ErrorState

Jak EmptyState, ilustracja z ikoną `triangle-alert` w `status.danger`; tytuł mówi, co się nie udało („Nie udało się wczytać sesji”), opis — dlaczego (jeśli wiadomo) i co zrobić; akcje: „Spróbuj ponownie” (Primary) + „Szczegóły błędu” (Link → rozwija `type.mono.sm` z komunikatem i przyciskiem „Kopiuj”).

---

### 4.8 Wykresy

#### 4.8.1 Fundament: `ChartDrawable`

Wszystkie wykresy to `GraphicsView` + klasa dziedzicząca po `ChartDrawable : IDrawable` w `OnionLLM/Controls/Charts/`. Bez zewnętrznych bibliotek.

`ChartDrawable` zapewnia:

- **Model danych:** `ChartData` = lista serii (`Id`, `Label`, `ColorKey`, `Points`, `IsVisible`) + oś X (kategorie lub czas) + `Unit` (Cost/Tokens/Percent).
- **Obszar rysowania:** marginesy wewnętrzne (lewy — szerokość etykiet osi Y liczona dynamicznie; dół 20; góra 8; prawy 8).
- **Oś Y:** 4–5 „ładnych” podziałek (1/2/2,5/5 × 10ⁿ), etykiety `type.caption` `NumMedium` `data.axis`, wyrównane do prawej; linie siatki poziome 1 dp `data.grid`; linia zera 1 dp `border.default`.
- **Oś X:** etykiety `type.caption` `data.axis`; automatyczne przerzedzanie, aby etykiety nie nachodziły (min. odstęp 8); formaty dat wg §5.2 („pn 24”, „24.11”, „lis”, „Tydz. 47”).
- **Interakcja hover/fokus:** `GraphicsView.StartHoverInteraction/MoveHoverInteraction/EndHoverInteraction` → indeks najbliższej kategorii; **crosshair** (pionowa linia 1 dp `border.strong`) + podświetlenie (pozostałe kategorie z kryciem 40%) + **RichTooltip** z: nagłówkiem (data/okres), listą serii (kropka · nazwa · wartość) posortowaną malejąco, sumą. Klawiatura: fokus na wykresie → ←/→ przesuwa crosshair, Enter = klik.
- **Klik w kategorię:** zdarzenie `CategoryActivated` (np. otwarcie drawera okresu).
- **Animacje:** M4 / M5 przez interpolację wartości `t ∈ [0,1]` z `MotionTokens` (Animation + `Invalidate()` na klatkę).
- **Motyw:** subskrypcja `ThemeService.ThemeChanged` → `Invalidate()`; kolory zawsze z `DesignTokens`.
- **Stany:** `Loading` (skeleton: 6 szarych słupków `surface.2` z shimmer), `Empty` (oś + tekst „Brak danych w wybranym okresie” wyśrodkowany, `text.tertiary`), `Error`.
- **Fonty na płótnie:** Win2D przyjmuje URI fontu (`ms-appx:///…#Inter Tnum`) tylko w aplikacji spakowanej; build niespakowany dostaje samą nazwę rodziny (`DesignTokens.Font`). Tekst o niedodatnich wymiarach pole jest pomijany (Win2D rzuca wyjątek).
- **A11y:** `SemanticProperties.Description` generowany z danych („Wykres słupkowy kosztu dziennego, 1–26 listopada. Maksimum 24 listopada: 18,20 dolara. Suma: 212,40 dolara.”) + przycisk „Pokaż dane w tabeli” (`table-2`) w nagłówku karty wykresu, przełączający wykres na DataTable z tymi samymi danymi.

#### 4.8.2 Typy wykresów

**A. Słupkowy (`BarChart`)** — jedna seria w czasie.

- Szerokość słupka: `min(28, 0.64 × szerokość kategorii)`; zaokrąglenie górnych rogów `radius.xs`; kolor metryki (`data.cost`, `data.tokens`…); ostatni (bieżący, niezakończony) okres z kryciem 100% i **wzorem ukośnych pasków** + etykietą „w toku” w tooltipie; poprzednie z kryciem 85%.
- Opcjonalna linia średniej: przerywana 1 dp `text.tertiary` + etykieta „śr. 8,40 $” przy prawej krawędzi.
- Maksimum okresu: mała kropka `data.highlight` nad słupkiem (M20 przy nowym rekordzie).

**B. Skumulowany słupkowy (`StackedBarChart`)** — koszt/tokeny w czasie z podziałem na źródła lub modele.

- Kolejność warstw: od największej sumy w okresie (na dole) do najmniejszej; „Pozostałe” zawsze na górze.
- Segmenty rozdzielone przerwą 1 dp w kolorze tła karty (lepsza czytelność, kodowanie bez koloru).
- **Legenda interaktywna** (nad wykresem, po lewej): chip na serię (kropka · nazwa · suma w okresie); klik ukrywa/pokazuje serię (oś Y przelicza się z M5); Alt+klik = „pokaż tylko tę”; stan zapamiętywany per ekran.
- Przełącznik **„Wartości | Udział %”** (SegmentedControl `sm`) — tryb 100%.

**C. Liniowy / warstwowy (`LineChart`, `AreaChart`)** — trendy (np. skumulowany koszt miesiąca vs budżet, koszt w bloku z prognozą).

- Linia 2 dp, złącza zaokrąglone; obszar pod linią gradientem kolor → przezroczysty (krycie 20% → 0%).
- **Prognoza:** kontynuacja linią przerywaną (4/4) od bieżącego punktu do końca okresu + zacieniony stożek niepewności (krycie 8%).
- **Linia progu/budżetu:** pozioma przerywana `status.warning` / `status.danger` z etykietą po prawej („Budżet 300 $”, „Limit bloku”).
- Punkty widoczne tylko przy hover (kropka 6 z obrysem 2 dp tła).

**D. Sparkline (`Sparkline`)** — mały trend w kafelku/wierszu.

- Wysokość 24–32, bez osi i etykiet, linia 1.5 dp w kolorze metryki, ostatni punkt kropką 4; opcjonalnie obszar 12%. Tooltip tylko w wersji w kafelku (wartość + data przy hover).

**E. Donut (`DonutChart`)** — udział źródeł/modeli w okresie.

- Grubość 18% promienia, przerwy między segmentami 2°, zaokrąglone końce wyłączone. Maks. 6 segmentów + „Pozostałe”.
- Środek: suma (`type.metric.md`) + etykieta („Koszt · 30 dni”); przy hover segmentu — wartość i udział segmentu.
- Legenda obok (wide) / pod (narrow): kropka · nazwa · wartość · %. Kliknięcie segmentu lub pozycji legendy = filtr ekranu po tym elemencie (z FilterChip `Removable`).

**F. Heatmapa (`Heatmap`)** — aktywność w czasie.

- Dwa tryby: **„Dni × godziny”** (7 wierszy × 24 kolumny, ostatnie N tygodni zsumowane) i **„Kalendarz”** (tygodnie × dni tygodnia, ostatnie 12 miesięcy, jak wykres kontrybucji). `Wymaga danych` dla rozkładu godzinowego (agregacja godzinowa w Core).
- Komórki kwadratowe 12–16 z przerwą 2, `radius.xs`; skala 5-stopniowa od `surface.2` (zero) przez 4 odcienie `palette.onion` (200 → 400 → 600 → 800 w light; 900 → 700 → 400 → 300 w dark) — kwantyle, nie liniowo.
- Legenda skali („Mniej ▢▢▢▢▢ Więcej”) i metryka (koszt / tokeny / sesje) wybierana SegmentedControl.
- Tooltip: „wt 24 lis, 14:00–15:00 · 2,40 $ · 180 k tokenów · 3 sesje”.

**G. Pierścienie limitów** — zob. `OnionRings` (§4.4.3); technicznie `ChartDrawable`.

**H. Wykres bloku (`BlockTimeline`)** — Bloki i drawer bloku.

- Pozioma oś czasu 5 h: tło `data.track`, wypełnienie skumulowanego kosztu jako AreaChart w kolorze `source.claude`, pionowa linia „teraz” (`text.primary`, etykieta „teraz”), prognoza przerywana do końca bloku, linia limitu tokenów (jeśli ustawiony).
- W liście bloków wersja mini (wysokość 24) jako kolumna tabeli.

#### 4.8.3 Rozróżnianie serii

- **Top-N:** maks. 6 serii na wykresach skumulowanych i donut (8 dla kategorycznych) + „Pozostałe” (`chart.other`). N wybieramy po sumie w zakresie.
- **Kolory:** źródła → `color.source.*`; modele/projekty → `color.chart.cat.1–8` w kolejności malejącej sumy; kolory są **stabilne w obrębie sesji aplikacji** (mapa id → kolor), aby seria nie zmieniała koloru po zmianie filtra.
- **Drugie kodowanie** (obowiązkowe, gdy ≥ 2 serie z tej samej grupy odcieni, §2.1.6): (1) kolejność warstw i legendy jest identyczna; (2) etykiety bezpośrednio na segmentach, gdy segment ≥ 24 dp wysokości; (3) wzór wypełnienia dla co drugiej serii z grupy (ukośne paski 45°, 2 dp, krycie 25% tła); (4) tooltip zawsze z nazwami.
- Kolor nigdy nie jest jedynym sposobem identyfikacji serii — legenda i tooltip MUSZĄ podawać nazwy.

#### 4.8.4 Karta wykresu (ChartCard)

Card z: nagłówkiem (tytuł `type.title.card` + opis zakresu `type.caption`), kontrolkami po prawej (SegmentedControl metryki: „Koszt | Tokeny | Cache %”, przełącznik „Wartości | %”, IconButton „Pokaż dane w tabeli”), legendą pod nagłówkiem i wykresem. Wysokość obszaru wykresu: 240 (comfortable) / 200 (compact) dla wykresów głównych, 160 dla pomocniczych.

---

## 5. Wzorce

### 5.1 Liczby, waluty, jednostki

Kultura formatowania: **pl-PL** (przecinek dziesiętny, spacja niełamliwa U+00A0 jako separator tysięcy). Formatowanie wyłącznie przez `Fmt` (`OnionLLM/ViewModels/Fmt.cs`) — ten moduł MUSI zostać dostosowany do poniższych reguł.

**Tokeny**

| Zakres | Format zwarty (karty, wykresy, tabele) | Format pełny (tooltip, drawer, eksport) |
| --- | --- | --- |
| < 1 000 | „842” | „842” |
| 1 000 – 999 999 | „12,4 k” (1 miejsce; ≥ 100 k bez miejsc: „842 k”) | „12 412” |
| 1 M – 999 M | „1,24 M” (2 miejsca) | „1 243 512” |
| ≥ 1 B | „2,05 B” | „2 051 330 120” |

Między liczbą a sufiksem spacja niełamliwa. Słowo „tokenów” dodajemy w zdaniach i podpisach z odmianą (§8.3): „1 token”, „2 tokeny”, „5 tokenów”, „1,2 M tokenów” (ułamki → dopełniacz l.mn.).

**Koszt (USD — waluta bazowa)**

| Zakres | Format | Przykład |
| --- | --- | --- |
| 0 | „0,00 $” | „0,00 $” |
| 0 < x < 0,995 | do 4 miejsc, bez zer końcowych poza 2 | „0,0042 $”, „0,38 $” |
| 0,995 ≤ x < 1 000 | 2 miejsca | „12,34 $” |
| ≥ 1 000 | zwarty: bez miejsc; pełny: 2 miejsca | „1 234 $” / „1 234,50 $” |

Symbol waluty **po** liczbie, oddzielony spacją niełamliwą. Wartość ujemna (korekta) — znak minus U+2212: „−0,12 $”.

**Waluta wyświetlania** (Ustawienia → Ogólne → „Waluta wyświetlania”: „USD” (domyślnie) · „PLN” · „EUR”). `Wymaga danych`.

- Gdy ≠ USD: wszystkie koszty pokazujemy w walucie wyświetlania z prefiksem „≈ ” (`PricingMarker` `≈`): „≈ 49,60 zł”, „≈ 11,40 €”.
- Tooltip każdej przeliczonej wartości podaje kwotę w USD i kurs z datą.
- Kurs: „Ręczny” (pole liczbowe) lub „Automatyczny (NBP, dziennie)”. Przy kursie starszym niż 7 dni — Banner info na ekranie Ustawień i oznaczenie w StatusBar.
- **Eksport i CLI zawsze w USD** (zgodność z ccusage); w nagłówku eksportu CSV dodatkowa kolumna w walucie wyświetlania tylko, gdy wybrano ją w opcjach eksportu.
- Ceny modeli (cennik, nadpisania) zawsze w USD za 1 M tokenów („3,00 $ / 1 M”).

**Stan wdrożenia (1.7):** `Fmt.Currency` przelicza każdy koszt UI (z „≈”), `Fmt.CostUsd` / `PricePerMillion` zostają w USD; podpowiedź z kwotą w USD i kursem — w `MetricLabel` (hero, karty, bloki); `CurrencyService` pobiera kurs NBP raz dziennie (nie w trybie offline) i zapisuje go w konfiguracji; kurs starszy niż 7 dni → Banner info w Ustawieniach i dopisek w StatusBar. Kwoty wpisywane (budżety, nadpisania cen) są w USD.

**Procenty:** „62%” (bez spacji), < 10% z jednym miejscem: „4,2%”, < 0,1% → „< 0,1%”. **Kredyty:** „12,5 kredytu / kredytów” (odmiana). **Tempo:** „4,20 $/h”, „1,8 k tok/min”. **Mnożniki:** „×1,5”.

**Dokładność a jednostka:** w jednej kolumnie tabeli wszystkie wartości mają tę samą liczbę miejsc dziesiętnych przy tym samym sufiksie.

### 5.2 Daty i czas

Wszystkie czasy w strefie z ustawienia „Strefa czasowa” (domyślnie systemowa). Formatowanie przez `Fmt.LocalTime`.

| Kontekst | Format | Przykład |
| --- | --- | --- |
| Godzina | `HH:mm` | „14:02” |
| Data krótka (tabele) | `d MMM` / `d MMM yyyy` (inny rok) | „26 lis”, „3 gru 2025” |
| Data z dniem tygodnia (osie, okresy dzienne) | `ddd d MMM` | „wt 24 lis” |
| Data i godzina | `d MMM, HH:mm` | „26 lis, 14:02” |
| Tydzień | „Tydz. 47 · 17–23 lis” | — |
| Miesiąc | `LLLL yyyy` | „listopad 2026” |
| Zakres | „1–26 lis 2026”, „28 paź – 3 lis 2026” | — |
| Czas trwania | „2 h 14 min”, „45 min”, „< 1 min” | — |
| Odliczanie | „za 2 h 14 min” (+ „o 16:30” w tooltipie lub obok) | — |
| Czas względny (StatusBar, ostatnia aktywność) | „przed chwilą” (< 1 min), „5 min temu”, „o 14:02” (dziś), „wczoraj, 18:40”, dalej data | — |
| Pole daty (wpisywanie) | `dd.MM.yyyy` | „26.11.2026” |

Pierwszy dzień tygodnia z ustawienia „Początek tygodnia”. Nazwy dni i miesięcy po polsku, małą literą.

### 5.3 Uczciwość danych

| Sytuacja | Gdzie widać | Jak |
| --- | --- | --- |
| Modele bez ceny | przy każdej liczbie kosztu, w stopce | `PricingMarker *`, `PricingNote`, Banner warning na Dziś, jeśli dotyczy > 5% tokenów dnia |
| Ceny szacowane | przy liczbie, w stopce | `PricingMarker ~`, `PricingNote` |
| Koszt zapisany vs wyliczony | drawer, tooltip hero | KeyValueList: „Zapisany przez narzędzie” / „Wyliczony z cennika” / „Tryb kosztu: auto” |
| Waluta przeliczona | przy liczbie | `≈` |
| Pliki nieczytelne / pominięte linie | StatusBar, Banner (Dziś, Źródła) | „3 pliki pominięte” → Diagnostyka |
| Dane nieaktualne | HeroMetric (kropka), StatusBar | „Zaktualizowano 42 min temu” w `status.warning` |
| Limit szacowany zamiast live | LimitGauge | „~” + tooltip „Szacowane z historii; dane na żywo niedostępne” |
| Okres niezakończony | wykresy, tabele | słupek „w toku” (wzór), wiersz z etykietą „w toku” |
| Prognoza | wykresy, blok | zawsze linia przerywana + słowo „prognoza” |

### 5.4 Zapamiętywanie stanu widoków

Przez `ViewState` (MAUI Preferences), per ekran: zakres dat (preset lub własny), okres, filtry (źródło, chipy), wyszukiwanie (NIE — czyścimy przy wyjściu z ekranu), sortowanie, widoczność serii w legendzie, metryka wykresu, zwinięte sekcje, szerokość drawera, stan sidebaru, kolejność kart źródeł (`provider_order`). Każdy ekran ma Link „Wyczyść filtry” przywracający wartości domyślne z tabel w §6.

### 5.5 Filtrowanie, zakresy, drill-down

- Filtry działają natychmiast; w trakcie przeliczania stare dane pozostają widoczne (bez skeletonu), PageHeader pokazuje M15.
- **Drill-down:** każdy agregat prowadzi do szczegółu z zachowaniem kontekstu, tworząc filtr `Removable`:
  - wiersz okresu w Raportach → drawer okresu → „Pokaż sesje z tego okresu” → Sesje z zakresem = okres;
  - segment źródła na wykresie/donucie → filtr „Źródło: X” na bieżącym ekranie;
  - projekt w sesji → Projekty z otwartym drawerem projektu;
  - model w drawerze → Modele z otwartym drawerem modelu;
  - karta źródła na Dziś → Źródła z otwartym drawerem źródła;
  - pierścień/limit → Limity.
- Przycisk Wstecz (Alt+←, ⌘[) wraca do poprzedniego ekranu z jego stanem (historia nawigacji w obrębie sesji aplikacji, max 20 wpisów).

### 5.6 Eksport

- Dostępny na: Raporty, Sesje, Projekty, Modele, Bloki (`Docelowe` dla Raportów i Sesji; pozostałe — rozszerzenie VM).
- SplitButton „Eksportuj” → „CSV”, „JSON (format ccusage)”, „Kopiuj jako tabelę” (TSV do schowka).
- Eksport obejmuje **dokładnie to, co widać** po filtrach (zakres, źródło, podziały), w walucie USD.
- Domyślna lokalizacja: `Dokumenty/OnionLLM/`, nazwa `onionllm-<ekran>-<okres>-<rrrrMMdd-HHmm>.<ext>`. Pierwszy eksport otwiera systemowe okno zapisu (zapamiętuje folder); kolejne zapisują od razu.
- Wynik: Toast „Wyeksportowano 26 wierszy” + akcje „Otwórz folder” / „Kopiuj ścieżkę”.

### 5.7 Odświeżanie i synchronizacja

- **Automatyczne:** co `RefreshIntervalSeconds` (domyślnie 30 s, min. 5 s), gdy okno jest widoczne **lub** gdy włączone są tray/powiadomienia (wtedy w tle z interwałem ≥ 60 s).
- **Ręczne:** F5 / ⌘R / IconButton „Odśwież” w PageHeader / polecenie w palecie / menu traya.
- Sync czyta tylko zmienione pliki; w trakcie: M15 w PageHeader i StatusBar, dane bez zmian do końca. Po zakończeniu: M3 dla zmienionych liczb.
- Błąd sync: StatusBar w `status.danger` + Toast (raz na serię błędów) „Nie udało się odświeżyć danych” + „Szczegóły”. Dane z poprzedniego sync pozostają widoczne z oznaczeniem nieaktualności.
- Limity Claude odświeżane maks. co 5 min (ograniczenie API) — w LimitGauge widoczna godzina ostatniego odczytu.

### 5.8 Alerty i eskalacja

**Progi** (Ustawienia → Powiadomienia i budżety): domyślnie **80%** (ostrzeżenie) i **100%** (przekroczenie) dla: limit 5 h, limit 7 dni, limit tokenów bloku, budżet dzienny, budżet miesięczny; oraz **burn rate „wysokie”**. Każdy próg można zmienić (50–100%) lub wyłączyć.

**Kanały wg poziomu:**

| Poziom | W aplikacji | Tray / pasek menu | Powiadomienie systemowe |
| --- | --- | --- | --- |
| Info (np. reset limitu) | StatusBadge | — | opcjonalnie (domyślnie wył.) |
| Ostrzeżenie (≥ 80%, tempo wysokie) | kolor progowy, kropka w Sidebar, Banner warning na Dziś/Limity | ikona z kropką `warning` | **tak** |
| Przekroczenie (≥ 100%) | kolor danger, Banner danger, M19 | ikona z kropką `danger` | **tak** |

**Zasady:** każde powiadomienie systemowe dotyczy jednego progu jednego limitu i jest wysyłane **raz na okno limitu** (5 h / 7 d / blok / dzień / miesiąc). Ciche godziny (opcjonalnie, np. 22:00–8:00) i „Wycisz na 1 h / do jutra” z menu traya. Treści → §8.4.

### 5.9 Budżety `Wymaga danych`

- Budżet **dzienny** i **miesięczny** w USD (wyświetlany w walucie wyświetlania z `≈`), globalny oraz opcjonalnie per źródło.
- Widoczność: HeroMetric (budżet dnia — ProgressBar `OnHero`), OnionRings (pierścień budżetu, gdy brak aktywnego bloku), ekran Limity (sekcja „Budżety” z LimitGauge i prognozą końca miesiąca: „Prognoza na koniec listopada: 312 $ (104% budżetu)”), Raporty (linia budżetu na wykresie dziennym/miesięcznym), mini-widok.
- Prognoza miesiąca: średnia dzienna z ostatnich 7 dni × pozostałe dni + koszt dotychczasowy; pokazywana od 3. dnia miesiąca.

**Stan wdrożenia (1.7):** konfiguracja `budgets.*` (Ustawienia → „Budżety”, z tabelą budżetów per źródło i Dialogiem), `AppState.Budgets` przeliczany po każdym odświeżeniu; Dziś — pasek budżetu dnia w HeroMetric, pierścień „Budżet dnia” zamiast bloku, Banner warning ≥ 80% / danger ≥ 100%; Limity — LimitGauge dzienny i miesięczny z prognozą, DataTable per źródło, EmptyState „Nie ustawiono budżetów”; Raporty — linia budżetu na wykresie kosztu (dzień → dzienny, miesiąc → miesięczny); kropka przy „Limity” w Sidebarze uwzględnia budżety. Mini-widok — `Wymaga platformy`.

### 5.10 Autozapis ustawień

- Każda zmiana w Ustawieniach zapisuje się natychmiast po walidacji (przełączniki, Select — od razu; pola tekstowe — po 600 ms bezczynności lub opuszczeniu pola).
- Potwierdzenie: mały wskaźnik przy polu („Zapisano” + `check`, `status.success`, znika po 1,5 s) — bez Toastów dla pojedynczych pól.
- Ustawienia wymagające przeliczenia (strefa czasowa, tryb kosztu, długość bloku, aliasy) — po zapisie automatyczne odświeżenie danych (StatusBar „Aktualizowanie…”).
- Akcje nieodwracalne (przebudowa bazy, usunięcie wszystkich nadpisań) — Dialog potwierdzenia.
- Gdy plik konfiguracji zmienił się z zewnątrz (CLI, edytor) — Banner info „Konfiguracja zmieniona poza aplikacją. Wczytano nowe wartości.”
- Jeśli działa nadpisanie `.onionllm.json` / `--config` — Banner info na każdej podstronie Ustawień: „Aktywny plik konfiguracji: <ścieżka>. Zmiany są zapisywane do tego pliku.”

### 5.11 Przeciągnij i upuść

Kolejność kart źródeł na ekranie Dziś. Uchwyt (`grip-vertical` 16, widoczny przy hover/fokusie), M14. **Alternatywa klawiaturowa (obowiązkowa):** fokus na karcie → Alt+↑ / Alt+↓ przesuwa; ogłoszenie „Claude Code przeniesiono na pozycję 2 z 4”. Menu karty: „Przesuń na początek”, „Przesuń na koniec”, „Przywróć domyślną kolejność”.

### 5.12 Kopiowanie

Każdy identyfikator (ID sesji, ścieżka, nazwa modelu, klucz konfiguracji) jest kopiowalny: IconButton `copy` przy hover/fokusie lub Ctrl+C na zaznaczonym wierszu. Potwierdzenie: ikona zmienia się na `check` na 1,5 s + ogłoszenie „Skopiowano”. Bez Toastu.

---

## 6. Ekrany

### 6.0 Wspólna macierz stanów

Każdy ekran MUSI obsłużyć stany z tabeli. Kolumna „Domyślne zachowanie” obowiązuje, o ile sekcja ekranu nie mówi inaczej.

| Kod | Stan | Warunek | Domyślne zachowanie |
| --- | --- | --- | --- |
| `S-FIRST` | Pierwsze uruchomienie | brak ukończonego onboardingu | Onboarding (§7.5) zamiast ekranów |
| `S-NOSRC` | Brak źródeł | onboarding ukończony, 0 wykrytych źródeł | EmptyState `Page`: „Nie znaleziono danych żadnego narzędzia” + „Skonfiguruj źródła” (→ Źródła) + Link „Jakie narzędzia są obsługiwane?” |
| `S-IMPORT` | Import historii | pierwszy pełny sync w toku | Pasek postępu importu w Banner info (pliki X z Y, źródło), treść ekranu wypełnia się przyrostowo |
| `S-LOAD` | Ładowanie | brak danych w pamięci, sync/raport w toku | Skeleton układu ekranu |
| `S-REFRESH` | Odświeżanie | dane są, trwa sync | Dane widoczne, M15 w PageHeader i StatusBar |
| `S-EMPTY` | Pusto w zakresie | 0 wpisów dla filtrów/zakresu | EmptyState `Inline` w miejscu danych z propozycją szerszego zakresu („Pokaż ostatnie 30 dni”) |
| `S-NORES` | Brak wyników | wyszukiwanie/filtr bez wyników | „Brak wyników dla „…”” + „Wyczyść filtry” |
| `S-SYNCERR` | Błąd synchronizacji | wyjątek sync/raportu | Ostatnie dane (jeśli są) + StatusBar danger + Toast; bez danych → ErrorState |
| `S-PARTIAL` | Dane częściowe | pominięte pliki/linie | StatusBar warning; Banner warning na Dziś i Źródła |
| `S-STALE` | Dane nieaktualne | ostatni udany sync > 2× interwał | Kropka hero `warning`, StatusBar „Zaktualizowano 42 min temu” warning |
| `S-PRICEOFF` | Cennik offline/nieaktualny | tryb offline lub cennik > 7 dni | StatusBar warning; Banner info w Modele i Ustawienia → Cennik |
| `S-UNPRICED` | Brak cen | modele bez ceny/szacowane w widoku | `PricingMarker` + `PricingNote` |
| `S-NOLIMITS` | Limity niedostępne | brak logowania Claude / brak sieci / błąd API | LimitGauge `Unavailable`; limity bloku (lokalne) nadal działają |
| `S-SRCOFF` | Źródło wyłączone/niewykryte | dotyczy widoków jednego źródła | SourceBadge `Disabled`/`NotDetected` + akcja „Włącz” / „Ustaw ścieżkę” |

### 6.1 Dziś

**Status:** `Docelowe` (elementy oznaczone osobno). **Skrót:** Ctrl+1. **Ikona:** `layout-dashboard`.

**Cel:** w 2 sekundy odpowiedzieć: ile dziś wydałem, czy tempo jest normalne, jak blisko limitów.

**Pytania użytkownika:** Ile dziś? Więcej czy mniej niż zwykle? Ile zostało do limitu 5 h / 7 dni i kiedy reset? Czy trwa blok i jakie ma tempo? Które narzędzie dziś najwięcej kosztuje?

**Regiony (kolejność czytania):**

1. **PageHeader:** tytuł „Dziś”, podtytuł z datą („czwartek, 26 listopada”), akcje: IconButton „Odśwież” (F5).
2. **Banner** (slot, §4.6.4): `S-PARTIAL`, `S-NOLIMITS` (jeśli Claude aktywne), przekroczony budżet/limit.
3. **Wiersz hero** (wide: 8/12 + 4/12; medium/narrow: jeden pod drugim):
   - **Karta Hero** (gradient): HeroMetric — koszt dnia (`TodayCost` + markery), „`TodayTokens` tokenów · N sesji”, TrendDelta „vs wczoraj” i „vs średnia 7 dni” (`Wymaga danych`: koszt wczoraj i średnia dzienna w `DashboardSnapshot`), budżet dnia (`Wymaga danych`). W prawej części karty (wide) — **OnionRings** `OnHero` z legendą.
   - **Karta „Limity”** (gdy OnionRings nie mieści się w hero — medium/narrow): OnionRings 120 + legenda + Link „Szczegóły limitów →”.
4. **Wiersz KPI** (4 × StatTile, wide 3/12 każdy; medium 2×2; narrow 2×2):
   - „7 DNI” — koszt (`WeekSummary` rozbite na koszt/tokeny), sparkline 7 dni (`Wymaga danych`: seria dzienna).
   - „30 DNI” — koszt (`MonthSummary`), sparkline 30 dni.
   - „TOKENY DZIŚ” — suma + mini TokenBreakdown (pasek) + „62% z cache”.
   - „SESJE” — aktywne (StatusBadge `Live` z liczbą) / zakończone dziś; Link → Sesje (zakres Dziś).
5. **Karta „Aktywny blok Claude”** (tylko `HasActiveBlock`): nagłówek z StatusBadge `Live` „Aktywny” + „Pozostało `BlockRemaining` · do `BlockEnds`”; BlockTimeline (koszt skumulowany, „teraz”, prognoza `Projection`); po prawej: koszt bloku (`BlockCost`, `type.metric.md`), tokeny (`BlockTokens`), BurnRateBadge (`BurnRate`, `BurnLevel`), LimitGauge bloku (`BlockLimitProgress`, `BlockLimitDisplay`). Klik w nagłówek → Bloki z otwartym drawerem bloku.
6. **Sekcja „Źródła dziś”**: nagłówek Section z akcjami: Link „Pokaż niewykryte (`HiddenCount`)” / „Ukryj niewykryte” (`ToggleUndetectedCommand`), menu „Przywróć domyślną kolejność”. Siatka **SourceCard** (auto-fill, min 320) z kolejnością użytkownika (§5.11).
7. **StatusBar.**

**SourceCard** (komponent złożony, specyficzny dla ekranu — `Controls/SourceCard`):

- Card `Accent` (pasek w `color.source.<id>`), `Interactive` w części nagłówka.
- **Nagłówek:** SourceBadge `md` (inicjał `Initial`, kolor z tokenu `color.source.<id>` — NIE z `AccentColor` bezpośrednio) · nazwa (`Name`) + dostawca (`Description`) · StatusBadge (`Status`: „Aktywne” `Live` / „Wykryte · brak użycia dziś” `Neutral` / „Nie wykryto” / „Wyłączone”) · uchwyt przeciągania · menu „więcej”.
- **Liczby:** koszt dnia (`CostDisplay`, `type.metric.md`, `data.cost`) + tokeny (`TotalTokensDisplay`, `type.metric.sm`).
- **TokenBreakdown** (pasek + legenda zwinięta do 1 linii: „we `InputDisplay` · wy `OutputDisplay` · cache `CacheWriteDisplay`/`CacheReadDisplay`”) i „`CacheShareDisplay` z cache” (wartość liczbowa `CacheShare` steruje szerokością segmentów cache).
- **Sesje:** „● `ActiveSessions` aktywne · `CompletedSessions` zakończone · aktywny < `ActiveWindowDisplay`”.
- **Rozwinięcie** („Więcej szczegółów” / „Mniej szczegółów” — `DetailsToggleText`, `ToggleDetailsCommand`, stan `IsDetailsExpanded`, M12): top modele (`ModelsDisplay` jako ModelTagi z kosztem), kredyty (`HasCredits`, `CreditsDisplay`), limity (`ShowLimitsBox`: LimitGauge 5 h — `FiveHourProgress`/`FiveHourDisplay`/`FiveHourReset`, LimitGauge 7 d — `SevenDayProgress`/`SevenDayDisplay`/`SevenDayReset`, `LimitStatus`), 7 dni / 30 dni (`Last7Display`, `Last30Display`), Link „Otwórz w Źródłach →”.
- Karta niewykrytego/wyłączonego źródła: zwarta (tylko nagłówek + akcja „Skonfiguruj”), krycie `opacity.muted`.

**Responsywność:** wide — hero z pierścieniami w jednej karcie; medium — hero pełna szerokość, karta Limity + karta bloku obok siebie (6/6 z 8 kolumn → 4/4); narrow — wszystko w jednej kolumnie, KPI 2×2, SourceCard w jednej kolumnie.

**Interakcje i skróty:** F5 odśwież; klik hero → Raporty (zakres Dziś, podział źródła); klik pierścieni → Limity; klik KPI → odpowiedni ekran; Alt+↑/↓ na SourceCard — kolejność.

**Mapowanie ViewModelu (`MainViewModel`):** `TodayCost`, `TodayTokens`, `ActiveProviders` (StatusBar/„N z M aktywnych”), `WeekSummary`, `MonthSummary`, `LastUpdated` (StatusBar), `IsRefreshing` (M15), `RefreshCommand`, `Providers` (SourceCard), `ShowUndetected`, `UndetectedToggleText`, `HiddenCount`, `ToggleUndetectedCommand`, `HasActiveBlock`, `BlockRemaining`, `BlockEnds`, `BlockCost`, `BlockTokens`, `BurnRate`, `BurnLevel`, `BurnColor` (→ zastąpić tokenem `color.burn.*` wybieranym po `BurnLevel`, a nie hexem z `Fmt.BurnColor`), `Projection`, `BlockLimitProgress`, `BlockLimitDisplay`. `WeekSummary`/`MonthSummary` POWINNY zostać rozbite na osobne właściwości kosztu i tokenów.

**Stany specyficzne:**

| Stan | Zachowanie |
| --- | --- |
| `S-LOAD` | Skeleton: hero (prostokąt liczby + pierścienie), 4 kafle, 2 karty źródeł |
| `S-EMPTY` (brak aktywności dziś) | Hero: „0,00 $” + podpis „Brak aktywności dziś” + TrendDelta ukryty; KPI 7/30 dni normalnie; SourceCard ze statusem „Wykryte · brak użycia dziś” |
| Brak aktywnego bloku | Karta bloku niewidoczna; w OnionRings pierścień bloku zastąpiony budżetem (jeśli ustawiony) albo pominięty |
| `S-NOLIMITS` | Pierścienie 5 h/7 d jako tory bez wartości + „—”; legenda z tekstem „Limity niedostępne — zaloguj się w Claude Code”; Banner info (zamykalny) |
| Brak Claude wśród źródeł | OnionRings pokazuje tylko budżet (jeśli ustawiony); bez budżetu karta Limity zastąpiona kartą „Ustaw budżet dzienny” (EmptyState `Inline` + „Ustaw budżet”) |

### 6.2 Limity

**Status:** `Docelowe` (limity live Claude, limit bloku) + `Wymaga danych` (historia wykorzystania, budżety, prognozy 5 h/7 d). **Skrót:** Ctrl+2. **Ikona:** `gauge`.

**Cel:** jedno miejsce na pytanie „ile mi zostało i kiedy reset?” dla wszystkich limitów i budżetów.

**Regiony:**

1. **PageHeader:** „Limity”, podtytuł „Ostatni odczyt limitów: 14:02 · co 5 min”, akcje: „Odśwież limity”.
2. **Banner:** `S-NOLIMITS` z instrukcją („Zaloguj się ponownie w Claude Code (`claude /login`), aby odczytać limity konta.”).
3. **Sekcja „Claude”** (dla każdego źródła z limitami; dziś tylko Claude): duży OnionRings (168) po lewej + 3 × LimitGauge po prawej: „Limit 5-godzinny”, „Limit 7-dniowy”, „Aktywny blok” (z BurnRateBadge i prognozą). Pod spodem — **Historia wykorzystania** (`Wymaga danych`: zapis odczytów limitów w ledgerze): AreaChart % w czasie (7 dni) z liniami progów 80/100% i zaznaczonymi resetami.
4. **Sekcja „Budżety”** (`Wymaga danych`): LimitGauge „Budżet dzienny”, „Budżet miesięczny” (+ prognoza końca miesiąca), lista budżetów per źródło (DataTable: źródło, budżet, wykorzystanie, prognoza). Pusta → EmptyState `Inline` „Nie ustawiono budżetów” + „Ustaw budżet” (→ Ustawienia → Powiadomienia i budżety).
5. **Sekcja „Inne narzędzia”:** lista źródeł aktywnych bez informacji o limitach („Brak danych o limitach dla tego narzędzia”) — tylko informacyjnie, zwinięta domyślnie.
6. **Sekcja „Powiadomienia”** (podsumowanie): aktualne progi i stan („Ostrzeżenie przy 80%, przekroczenie przy 100% · powiadomienia systemowe włączone”) + Link „Zmień”.

**Mapowanie:** `SourceCardViewModel` (`HasLimits`, `FiveHourProgress`, `SevenDayProgress`, `FiveHourDisplay`, `SevenDayDisplay`, `FiveHourReset`, `SevenDayReset`, `LimitStatus`) i `MainViewModel` (blok) — do wydzielenia w `LimitsViewModel`.

### 6.3 Raporty

**Status:** `Docelowe` (heatmapa godzinowa: `Wymaga danych`). **Skrót:** Ctrl+3. **Ikona:** `chart-column`.

**Cel:** zrozumieć koszty i tokeny w czasie, z podziałem na źródła, modele i projekty.

**Filtry (FilterBar) i wartości domyślne:**

| Filtr | Kontrolka | Wartości | Domyślnie | VM |
| --- | --- | --- | --- | --- |
| Okres | SegmentedControl | „Dzień” · „Tydzień” · „Miesiąc” | Dzień | `PeriodIndex` / `Periods` |
| Zakres | DateRangePicker | presety §4.2.4 + własny | Ostatnie 30 dni | `RangeIndex`, `IsCustomRange`, `Since`, `Until` |
| Źródło | FilterChip `Menu` | „Wszystkie źródła” + 20 | Wszystkie | `SelectedSource` / `Sources` |
| Podział | FilterChip `Toggle` ×3 | „Modele” · „Źródła” (tylko przy wszystkich źródłach) · „Projekty” | wył. | `Breakdown`, `BySource` (`CanSplitBySource`), `GroupByProject` |

**Regiony:**

1. **PageHeader:** „Raporty”, podtytuł = `DateRangeSummary`, akcje: „Odśwież”, SplitButton „Eksportuj” (`ExportCsvCommand`, `ExportJsonCommand`).
2. **FilterBar.**
3. **Wiersz KPI** (4 × StatTile): „KOSZT ŁĄCZNIE” (`TotalCost` + TrendDelta vs poprzedni zakres tej samej długości — `Wymaga danych`), „TOKENY” (`TotalTokens`), „ŚREDNIO / <OKRES>” (koszt/okres), „UDZIAŁ CACHE” (z `TotalDetails`).
4. **ChartCard „W czasie”:** StackedBarChart (seria = źródła przy „Wszystkie źródła”, inaczej modele) z przełącznikiem metryki „Koszt | Tokeny | Cache %” i „Wartości | %”, legenda interaktywna, linia budżetu (jeśli ustawiony i okres = Dzień/Miesiąc), opis `ChartDescription` jako `SemanticProperties.Description`. Klik w słupek → drawer okresu. Zastępuje `BarChartDrawable` i właściwość `Chart` (VM wystawia `ChartData` z seriami zamiast gotowego `IDrawable`).
5. **Wiersz analiz** (wide 6/12 + 6/12; poniżej jeden pod drugim):
   - **ChartCard „Udział”**: DonutChart z SegmentedControl „Źródła | Modele | Projekty”.
   - **ChartCard „Aktywność”**: Heatmap z przełącznikiem „Dni × godziny | Kalendarz” i metryką.
6. **DataTable okresów** (`Rows` → `ReportRowViewModel`):

| Kolumna | Źródło danych | Wyrównanie | Priorytet |
| --- | --- | --- | --- |
| Okres (+ projekt przy grupowaniu) | `Period` | lewo | 1 |
| Modele | `Subtitle` → ModelTagi (maks. 3 + „+2”) | lewo | 3 |
| Wejście / Wyjście / Cache zapis / Cache odczyt | z `Details` (rozbić na pola) | prawo | 3 |
| Tokeny | `Tokens` (+ inline bar) | prawo | 2 |
| Koszt | `Cost` + PricingMarker (+ inline bar) | prawo | 1 |

   - Wiersz rozwijany, gdy `HasBreakdown`: podwiersze źródeł/modeli z `Breakdown` (zamiast tekstu monospace).
   - Wiersz podsumowania „Razem”: `TotalTokens`, `TotalCost`.
   - Klik/Enter → **Drawer okresu** (zastępuje `ShowDetailsCommand` → alert): KPI, TokenBreakdown, tabela modeli, tabela źródeł, koszt zapisany vs wyliczony, akcje „Pokaż sesje z tego okresu”, „Kopiuj jako JSON”.
7. **PricingNote** (`PricingNote`, `HasPricingNote`).
8. **StatusBar** (`Status`, `IsBusy`).

**Stany specyficzne:** `S-EMPTY` — KPI z „—”, wykresy w stanie Empty, tabela EmptyState „Brak danych w zakresie 1–26 lis” + „Pokaż całą historię”. Okres bieżący oznaczony „w toku”.

### 6.4 Sesje

**Status:** `Docelowe`. **Skrót:** Ctrl+4. **Ikona:** `square-terminal`.

**Cel:** znaleźć sesję i zobaczyć jej koszt, modele i przebieg.

**Filtry:**

| Filtr | Kontrolka | Wartości | Domyślnie | VM |
| --- | --- | --- | --- | --- |
| Zakres | DateRangePicker (presety: Dziś, 7 dni, 30 dni, Cała historia + własny) | | 7 dni | `RangeIndex` / `Ranges` |
| Źródło | FilterChip `Menu` | | Wszystkie | `SelectedSource` |
| Status | FilterChip `Toggle` „Tylko aktywne” | | wył. | `Wymaga danych` (flaga aktywności w wierszu) |
| Szukaj | SearchField („Szukaj: ID sesji, projekt, model, źródło”) | | — | `Search` |
| Sortowanie | nagłówki tabeli (+ Select w trybie kart) | Najnowsze · Najdroższe · Najwięcej tokenów | Najnowsze | `SortIndex` / `Sorts` |

**Regiony:** PageHeader („Sesje”, podtytuł `Total` — „128 sesji · 14,2 M tokenów · 84,20 $”; akcje: Odśwież, Eksportuj — `ExportCommand` (CSV), `ExportJsonCommand`) → FilterBar → DataTable → PricingNote → StatusBar.

**DataTable** (`Sessions` → `SessionItemViewModel`):

| Kolumna | Dane | Wyr. | Prio |
| --- | --- | --- | --- |
| Sesja | `Title` (projekt) + `SessionShort` (Mono, `text.tertiary`) + StatusBadge `Live` „Aktywna”, jeśli trwa | lewo | 1 |
| Źródło | SourceBadge `sm` + nazwa (`Source`) | lewo | 2 |
| Start | z `When` (rozbić na `Start`, `End`, `Duration`) | lewo | 2 |
| Czas trwania | `Duration` | prawo | 3 |
| Modele | `Models` → ModelTagi | lewo | 3 |
| Wywołania | z `Details` (rozbić) | prawo | 3 |
| Tokeny | `Tokens` | prawo | 2 |
| Koszt | `Cost` + PricingMarker | prawo | 1 |

**Drawer sesji** (zastępuje `ShowDetailsCommand` → alert z „Kopiuj ID”): DrawerHeader („SESJA”, tytuł projektu, podtytuł „Claude Code · 26 lis, 13:02 → 14:40 (1 h 38 min)”); KPI (koszt, tokeny, wywołania); TokenBreakdown; „Modele” (mini-tabela); „Przebieg” (Sparkline/AreaChart kosztu w czasie sesji — `Wymaga danych`); KeyValueList (pełne ID z kopiowaniem, ścieżka projektu z „Otwórz folder”, źródło, start, koniec, koszt zapisany vs wyliczony); akcje: „Pokaż projekt”, „Kopiuj ID”.

**Limit listy:** zamiast cichego limitu 300 — paginacja „Pokaż kolejne 300” + licznik „Pokazano 300 z 1 284”.

### 6.5 Bloki

**Status:** `Docelowe`. **Skrót:** Ctrl+5. **Ikona:** `timer`.

**Cel:** zrozumieć 5-godzinne okna rozliczeniowe Claude — bieżące tempo, prognozę i historię.

**Filtry:** Zakres — SegmentedControl „3 dni · 7 dni · 30 dni” (`RangeIndex` / `Ranges`), domyślnie 7 dni.

**Regiony:**

1. **PageHeader:** „Bloki 5-godzinne”, podtytuł „Okna rozliczeniowe Claude odtworzone z historii”, akcje: Odśwież.
2. **Karta aktywnego bloku** (jak §6.1 pkt 5, w wersji rozszerzonej: BlockTimeline wysokości 120, LimitGauge, BurnRateBadge, prognoza z ostrzeżeniem „Przy obecnym tempie blok przekroczy limit ok. 15:40”). Brak aktywnego bloku → karta „Brak aktywnego bloku” z „Ostatni blok zakończył się o 11:20”.
3. **Wiersz KPI** (z `Summary`): „BLOKI” (liczba), „ŚREDNIO / BLOK” (koszt), „LIMIT TOKENÓW” (wartość lub „bez limitu”), „NAJDROŻSZY BLOK”.
4. **DataTable bloków** (`Blocks` → `BlockItemViewModel`): kolumny: Blok (start–koniec, StatusBadge „Aktywny” dla aktywnego), Oś (BlockTimeline mini), Modele, % limitu (ProgressBar progowy + liczba), Tokeny, Koszt. **Przerwy** (`Opacity` < 1 w VM) jako wiersze-separatory: pełna szerokość, `type.caption` `text.tertiary`, ikona `pause`, „Przerwa 3 h 20 min”, wysokość `size.row.heightDense`, bez interakcji.
5. **Drawer bloku:** BlockTimeline duży, KPI, BurnRate (dla aktywnego), modele, sesje w bloku (`Wymaga danych`: powiązanie bloków z sesjami).

**Mapowanie:** `Title`, `Subtitle`, `Tokens`, `Cost`, `Extra`/`HasExtra` (→ rozbić na `Remaining`, `BurnRate`, `Projection`, `Warning`), `Accent` (→ token: aktywny `status.success`, zakończony `source.claude`), `Opacity` (→ flaga `IsGap`), `Summary`, `IsBusy`.

**Stany:** źródło Claude niewykryte/wyłączone → EmptyState `Page` „Bloki dotyczą tylko Claude Code” + „Skonfiguruj Claude Code”.

### 6.6 Projekty

**Status:** `Docelowe` (agregacja z raportów z grupowaniem po projekcie i z sesji — nowy `ProjectsViewModel`); trend per projekt `Wymaga danych` (seria dzienna per projekt). **Skrót:** Ctrl+6. **Ikona:** `folder-kanban`.

**Cel:** które projekty (repozytoria) generują koszt.

**Filtry:** Zakres (domyślnie 30 dni), Źródło, Szukaj („Szukaj projektu lub ścieżki”).

**Regiony:** PageHeader (podtytuł „24 projekty · 30 dni”) → FilterBar → ChartCard „Top projekty” (poziomy BarChart top 10 wg kosztu, z udziałem %) → DataTable → PricingNote → StatusBar.

**DataTable:** Projekt (nazwa z aliasu lub katalogu + ścieżka Mono `text.tertiary`) · Źródła (stos SourceBadge `xs`, maks. 4 + „+2”) · Sesje · Ostatnia aktywność (czas względny) · Trend (Sparkline 30 dni) · Tokeny · Koszt.

**Drawer projektu:** KPI (koszt, tokeny, sesje), AreaChart kosztu w zakresie, Donut źródeł, tabela modeli, lista ostatnich sesji (5 + „Wszystkie sesje projektu →” → Sesje z filtrem), KeyValueList (ścieżka z „Otwórz folder”, alias z „Zmień alias” → Ustawienia → Projekty).

**Stany:** sesje bez projektu grupowane jako „(bez projektu)” — zawsze na końcu listy, `text.tertiary`.

### 6.7 Modele

**Status:** `Docelowe` (dane użycia z breakdownów modeli, ceny z serwisu cennika — odpowiednik `pricing info` z CLI). **Skrót:** Ctrl+7. **Ikona:** `cpu`.

**Cel:** które modele kosztują najwięcej, ile kosztuje 1 M tokenów i czy cena jest znana.

**Filtry:** Zakres (30 dni), Źródło, Szukaj, FilterChip `Toggle` „Tylko bez ceny”.

**Regiony:** PageHeader → Banner (`S-PRICEOFF`, `S-UNPRICED` z liczbą modeli bez ceny + „Dodaj ceny”) → FilterBar → DataTable → StatusBar.

**DataTable:** Model (ModelTag + dostawca cennika `text.tertiary`) · Źródła · Tokeny · Udział cache · Cena wejście / wyjście (za 1 M, Mono; „brak ceny” w `status.warning` + `*`; „nadpisana” StatusBadge `Info`) · Koszt (+ inline bar).

**Drawer modelu:** karta **Cennik** (KeyValueList: wejście, wyjście, zapis cache 5 min / 1 h, odczyt cache, stawki > 200 k, maks. tokenów wejścia, mnożniki fast/flex; źródło ceny: „LiteLLM · 26.11.2026” / „models.dev” / „Nadpisanie użytkownika”) + akcja „Nadpisz cenę” (Dialog z formularzem nadpisania, §6.9) / „Usuń nadpisanie”; AreaChart użycia w czasie; TokenBreakdown; źródła używające modelu; aliasy wskazujące na model.

### 6.8 Źródła

**Status:** `Docelowe`. **Skrót:** Ctrl+8. **Ikona:** `plug`.

**Cel:** zobaczyć stan wszystkich 20 integracji, skonfigurować je i naprawić problemy z danymi. **To jedyne miejsce konfiguracji źródeł** (Ustawienia odsyłają tutaj).

**Filtry:** SegmentedControl „Wszystkie · Aktywne · Wykryte · Niewykryte · Z problemami” (domyślnie „Wszystkie” z sortowaniem: aktywne → wykryte → z problemami → niewykryte → wyłączone), Szukaj.

**Regiony:** PageHeader (podtytuł „4 aktywne · 2 wykryte · 14 niewykrytych”; akcje: „Wykryj ponownie”, „Przebuduj bazę…”) → Banner (`S-PARTIAL`) → DataTable → StatusBar.

**DataTable:** Źródło (SourceBadge + nazwa + dostawca) · Stan (StatusBadge: „Aktywne” `Success`, „Wykryte” `Neutral`, „Nie wykryto” `Neutral` przerywany, „Wyłączone” `Neutral`, „Błędy” `Warning`) · Ostatnia aktywność · Wpisy (liczba w ledgerze) · Pliki · Pominięte (liczba, `status.warning` gdy > 0) · Włączone (Toggle w wierszu — zmiana natychmiastowa, autozapis).

**Drawer źródła** — zakładki (Tabs):

1. **Przegląd:** KPI 7/30 dni, Sparkline, top modele, limity (jeśli `HasLimits`).
2. **Konfiguracja:** Toggle „Włączone” (`Enabled`); „Ścieżki danych” — lista wykrytych folderów (Mono, z „Otwórz folder”) + TextField `Mono` „Własne ścieżki” (`Paths`, separator „;”, opis: „Puste = zmienne środowiskowe, potem domyślne foldery”); opcje specyficzne: Codex — Select „Poziom szybkości do wyceny” (`IsCodex`, `Speeds`, `SpeedIndex`); OpenCode — Toggle „Licz tury claude-agent” (`IsOpenCode`, `IncludeClaudeAgent`); pi — KeyValueEditor „Katalogi agentów” (`IsPi`, `AgentDirs`) i „Magazyny sesji” (`Stores`: nazwa → ścieżka).
3. **Diagnostyka:** pliki (liczba, rozmiar), pominięte linie, ostatnie błędy parsowania (lista z `type.mono.sm`, „Kopiuj”), ostatni sync.

**Mapowanie:** `SettingsViewModel.Sources` → `SourceSettingViewModel` (`Id`, `Name`, `State`, `Enabled`, `Paths`, `IsCodex`, `IsOpenCode`, `IsPi`, `Speeds`, `SpeedIndex`, `IncludeClaudeAgent`, `AgentDirs`, `Stores`) + statystyki z `Diagnostics` (rozbić na strukturę per źródło).

### 6.9 Ustawienia

**Status:** `Docelowe` (pozycje oznaczone osobno). **Skrót:** Ctrl+, (⌘, na macOS). **Ikona:** `settings`.

**Układ:** PageHeader „Ustawienia” + SearchField („Szukaj ustawienia”, filtruje pozycje na wszystkich podstronach) → dwie kolumny: **SettingsNav** (220) | treść podstrony (FormRow poziome na wide, max 760).

**SettingsNav (kolejność):** Ogólne · Wygląd · Okno i zasobnik · Powiadomienia i budżety · Obliczenia · Projekty · Cennik · Statusline · Zaawansowane · Diagnostyka · O aplikacji · separator · „Źródła danych” (ikona `external-link` — przechodzi do ekranu Źródła, §6.8; konfiguracji źródeł nie duplikujemy w Ustawieniach). Autozapis (§5.10) — brak przycisku „Zapisz”. Pod tytułem każdej podstrony `type.caption`: „Zmiany zapisują się automatycznie · Plik: `ConfigPath`”.

**Podstrony i pola:**

**6.9.1 Ogólne**

| Pole | Kontrolka | Wartości / walidacja | VM / config |
| --- | --- | --- | --- |
| „Strefa czasowa” | Select z wyszukiwaniem (lista IANA + „Systemowa (Europe/Warsaw)”) | pusta = systemowa | `Timezone` |
| „Początek tygodnia” | Select | dni tygodnia | `StartOfWeekIndex` / `Days` |
| „Waluta wyświetlania” | SegmentedControl „USD · PLN · EUR” + kurs (Number „1 USD = … zł”, Toggle „Automatycznie (NBP)”) | kurs > 0 | `Wymaga danych` |
| „Uruchamiaj przy starcie systemu” | Toggle + opis stanu (`StartupStatus`) | widoczne tylko, gdy dostępne | `IsStartupSettingAvailable`, `RunAtStartup` |
| „Język” | — | tylko polski (pozycja informacyjna NIE jest pokazywana) | — |

**6.9.2 Wygląd**

| Pole | Kontrolka | Wartości | Config |
| --- | --- | --- | --- |
| „Motyw” | SegmentedControl z ikonami (`monitor`, `sun`, `moon`) | Systemowy · Jasny · Ciemny | `appearance.theme` — `Wymaga danych` |
| „Gęstość” | SegmentedControl | Komfortowa · Kompaktowa | `appearance.density` — `Wymaga danych` |
| „Animacje” | SegmentedControl | Pełne · Ograniczone · Wyłączone | `appearance.motion` — `Wymaga danych` |

Podgląd na żywo: mała karta przykładowa (StatTile + wiersz tabeli) pod polami.

**6.9.3 Okno i zasobnik** (`Wymaga platformy`)

| Pole | Kontrolka | Domyślnie |
| --- | --- | --- |
| „Zawsze na wierzchu” | Toggle | wył. |
| „Pokazuj ikonę w zasobniku” (Windows) / „Pokazuj w pasku menu” (macOS) | Toggle | wł. |
| „Zamknięcie okna minimalizuje do zasobnika” | Toggle | wł. |
| „Koszt dnia obok ikony w pasku menu” (macOS) | Toggle | wł. |
| „Mini-widok: moduły” | lista Checkbox z przeciąganiem kolejności: Koszt i tokeny dnia · Limity 5 h / 7 dni · Aktywny blok i tempo · Top 3 źródła dnia | wszystkie wł. |
| „Mini-widok: zawsze na wierzchu” | Toggle | wł. |

**6.9.4 Powiadomienia i budżety** (`Wymaga danych` + `Wymaga platformy`)

- Toggle „Powiadomienia systemowe” (główny) + Link „Ustawienia powiadomień systemu”.
- Tabela progów: wiersz na limit (5 h, 7 dni, blok, budżet dzienny, budżet miesięczny) × kolumny „Ostrzeżenie” (Number %, domyślnie 80) · „Przekroczenie” (100) · Toggle „Powiadamiaj”.
- Toggle „Wysokie tempo spalania”.
- „Ciche godziny”: Toggle + zakres godzin.
- **Budżety:** Number „Budżet dzienny ($)”, Number „Budżet miesięczny ($)” (puste = brak) + DataTable „Budżety per źródło” (+ „Dodaj budżet źródła”).
- Przycisk „Wyślij powiadomienie testowe” (Secondary).

**6.9.5 Obliczenia**

| Pole | Kontrolka | Walidacja | VM |
| --- | --- | --- | --- |
| „Tryb kosztu” | SegmentedControl „Automatyczny · Wyliczany · Zapisany” + opis każdego trybu | — | `CostModeIndex` / `CostModes` |
| „Długość bloku” | Number + „h” | 1–24 | `SessionLength` |
| „Limit tokenów bloku” | SegmentedControl „Bez limitu · Maksimum z historii · Własny” + Number | > 0 | `TokenLimit` („max” / liczba) |
| „Odświeżanie danych” | Number + „s” | 5–3600 | `RefreshInterval` |
| „Sesja aktywna, jeśli ostatnie zdarzenie młodsze niż” | Number + „min” | 1–240 | `ActiveMinutes` |

**6.9.6 Projekty**

- „Aliasy projektów” — KeyValueEditor (klucz katalogu → nazwa wyświetlana), tryb tekstowy `key=Nazwa,key2=Nazwa2`. VM: `ProjectAliases`.

**6.9.7 Cennik**

- Karta statusu: „LiteLLM” (`LiteLlmStatus`) i „models.dev” (`ModelsDevStatus`) — każdy: StatusBadge (aktualny/nieaktualny/offline), data, liczba modeli; akcja „Odśwież cennik” (`RefreshPricingCommand`, Loading).
- Toggle „Cennik offline (bez pobierania)” (`Offline`) z opisem skutków.
- „Aliasy modeli do wyceny” — KeyValueEditor / CodeEditor (`ModelAliases`).
- **„Nadpisania cen”** — DataTable (`PricingOverrides` → `PricingOverrideViewModel`): Model (`Key`, Mono) · Wejście (`Input`) · Wyjście (`Output`) · Cache 5 min (`CacheWrite`) · Cache 1 h (`CacheWrite1h`) · Odczyt (`CacheRead`) (USD / 1 M) · menu (Edytuj, Usuń — `RemoveCommand`). „Dodaj nadpisanie” (`AddOverrideCommand`) otwiera **Dialog** z formularzem: Model (TextField Mono z podpowiedziami z listy modeli), pola cen (Number z sufiksem „$ / 1 M”), Collapsible „Zaawansowane” (`ShowAdvanced`: stawki > 200 k — `InputAbove200k`, `OutputAbove200k`, `CacheWriteAbove200k`, `CacheReadAbove200k`; `MaxInputTokens`; `FastMultiplier`, `FlexMultiplier`).
- Link „Sprawdź rozbieżności kosztu” → Zaawansowane.

**6.9.8 Statusline**

- „Etykiety modeli” — KeyValueEditor (`ModelLabelAliases`) + podgląd na żywo linii statusu (Mono, tło `surface.inverse`, przykład z bieżącymi danymi).
- Instrukcja instalacji statusline w Claude Code (krok po kroku, z przyciskiem „Kopiuj polecenie”) — `Docelowe` (polecenie CLI `statusline`).

**6.9.9 Zaawansowane**

- „Plik konfiguracji”: ścieżka (`ConfigPath`, Mono) + „Otwórz plik” + „Otwórz folder”.
- „Folder danych” (`OpenFolderCommand`).
- **„Rozbieżności kosztu”**: SegmentedControl zakresu (`MismatchRanges`, `MismatchRangeIndex`) + „Sprawdź” (`CheckMismatchesCommand`) → wynik jako DataTable (model, koszt zapisany, wyliczony, różnica, %) zamiast tekstu (`PricingMismatches`, `HasPricingMismatches`).
- **„Przebuduj bazę”** (Button Danger → Dialog potwierdzenia; `RebuildCommand`).

**6.9.10 Diagnostyka**

- KeyValueList: ścieżka bazy, liczba plików, liczba wpisów, ostatni sync, wersja cennika.
- DataTable per źródło: pliki, wpisy, pominięte linie, błędy (klik → drawer źródła, zakładka Diagnostyka).
- „Kopiuj raport diagnostyczny” (cały `Diagnostics` jako tekst do schowka).

**6.9.11 O aplikacji**

- Logo, wersja, „Sprawdź aktualizacje” (jeśli dotyczy), licencje (fonty, ikony), link do repozytorium, informacja o prywatności („Dane nie opuszczają komputera. Połączenia sieciowe: cennik (LiteLLM, models.dev), limity Claude (api.anthropic.com).”).

**Mapowanie zbiorcze:** `SettingsViewModel.Status` / `IsBusy` → wskaźniki zapisu przy polach i StatusBar; `SaveCommand` → wywoływany przez autozapis (bez przycisku).

### 6.10 Stan wdrożenia ekranów (Faza 12.3)

Wszystkie ekrany §6.1–6.9 są zbudowane z komponentów §4 (pliki `Views/*View`, ViewModele w `ViewModels/`). Poniżej elementy, które **świadomie nie są renderowane** albo są uproszczone — każdy z powodem; reszta specyfikacji ekranu obowiązuje bez zmian.

| Ekran | Nierenderowane / uproszczone | Powód |
| --- | --- | --- |
| Dziś (§6.1) | TrendDelta i sparkline dla „30 dni” (snapshot ma tylko 30 dni, bez poprzednich 30) | Wymaga danych (seria 60 dni) |
| Limity (§6.2) | „Powiadomienia” (podsumowanie progów); resety nie są zaznaczane na wykresie historii | Wymaga platformy (powiadomienia systemowe) |
| Raporty (§6.3) | — (pełny zakres) | — |
| Sesje (§6.4) | — (pełny zakres) | — |
| Bloki (§6.5) | — (pełny zakres) | — |
| Projekty (§6.6) | — (pełny zakres; §6.6 nie przewiduje eksportu — jest „Kopiuj jako tabelę”) | — |
| Modele (§6.7) | — (pełny zakres) | — |
| Źródła (§6.8) | — (pełny zakres) | — |
| Ustawienia (§6.9) | Z „Powiadomienia i budżety” tylko budżety (podstrona „Budżety”); pola „Okno i zasobnik” poza „Zawsze na wierzchu”; opcje eksportu z kolumną w walucie wyświetlania | Wymaga platformy (powiadomienia, tray, mini-widok); eksport zawsze w USD (§5.1) |

---

## 7. Powierzchnie systemowe

### 7.1 Okno główne

| Właściwość | Wartość |
| --- | --- |
| Tytuł | „OnionLLM” |
| Rozmiar startowy / minimalny | 1280 × 840 / 680 × 560 (§2.9.1) |
| Pasek tytułu | dopasowany do motywu (§3.4); na Windows tło paska = `bg.sidebar`, na macOS standardowy z ukrytym tytułem i przyciskami okna nad sidebarem |
| Zawsze na wierzchu | opcja, domyślnie **wyłączona** (Ustawienia → Okno i zasobnik; paleta poleceń; menu traya). Stan widoczny w StatusBar ikoną `pin` |
| Zamknięcie (X) | gdy tray włączony i „Zamknięcie minimalizuje do zasobnika” — chowa okno; pierwszy raz pokazuje powiadomienie „OnionLLM działa w tle. Kliknij ikonę w zasobniku, aby otworzyć.” Inaczej — zamyka aplikację |

### 7.2 Tryb kompaktowy (mini-widok) `Wymaga platformy`

**Cel:** stały, mały podgląd najważniejszych liczb — następca usuniętego widżetu Windows.

**Wejście/wyjście:** przycisk w Sidebarze (`picture-in-picture-2`), Ctrl+Shift+M, polecenie w palecie, menu traya, klik ikony w pasku menu (macOS — jako popover). Powrót: przycisk „Rozwiń” (`maximize-2`), dwuklik w nagłówek, Ctrl+Shift+M.

**Okno:** osobne okno bez ramki systemowej (własny nagłówek do przeciągania), szerokość 360, wysokość dopasowana do modułów (min 140, max 520), `radius.xl` (gdzie platforma pozwala), `surface.1`, obrys `border.default`, `elevation.3`. Pozycja zapamiętywana; domyślnie prawy górny róg ekranu z marginesem 16. Domyślnie zawsze na wierzchu (opcja).

**Anatomia:**

1. **Nagłówek** (36, gradient hero): logo 16 · „Dziś” · godzina aktualizacji (`hero.textSecondary`) · IconButton `OnHero`: odśwież, rozwiń, zamknij mini-widok.
2. **Moduły** (kolejność i widoczność z Ustawień, każdy oddzielony `border.subtle`, padding 12):
   - **Koszt i tokeny dnia:** koszt `type.metric.lg` + markery, tokeny `type.caption`, TrendDelta vs wczoraj.
   - **Limity 5 h / 7 dni:** OnionRings 40 + dwa LimitGauge w wersji jednoliniowej („5 h ▬▬▬▭ 62% · za 2 h 14 min”).
   - **Aktywny blok i tempo:** „Pozostało 2 h 14 min” · BurnRateBadge · prognoza jednym zdaniem; brak bloku → moduł ukryty automatycznie.
   - **Top 3 źródła dnia:** ListItem ×3 (SourceBadge `xs`, nazwa, koszt, inline bar).
3. Klik w moduł → otwiera okno główne na odpowiednim ekranie.

**Konfiguracja:** prawy klik w mini-widok → menu z checkboxami modułów („Pokaż: ✓ Koszt i tokeny · ✓ Limity · ✓ Blok · ✓ Top źródła”) i „Zawsze na wierzchu” — te same ustawienia co §6.9.3.

**Stany:** każdy moduł obsługuje `S-LOAD` (skeleton 1–2 linii), `S-NOLIMITS` („Limity niedostępne”), `S-EMPTY` („Brak aktywności dziś”). Wszystkie moduły wyłączone → komunikat „Wybierz moduły (prawy przycisk)”.

### 7.3 Zasobnik systemowy (Windows) `Wymaga platformy`

- **Ikona:** znak cebuli (§7.7) w wariancie monochromatycznym zgodnym z motywem paska zadań; nakładka-kropka statusu (prawy dolny róg): brak (OK), `status.warning`, `status.danger` — wg najwyższego aktywnego alertu (§5.8).
- **Tooltip:** „OnionLLM · dziś 12,34 $ · 5 h: 62% · 7 dni: 41%”.
- **Klik lewym:** pokazuje/ukrywa mini-widok przy ikonie (popover). **Dwuklik:** okno główne.
- **Menu (prawy klik):** „Otwórz OnionLLM” · „Mini-widok” · separator · „Odśwież teraz” · „Wycisz powiadomienia ▸ (1 godzina / Do jutra / Wyłącz wyciszenie)” · „Zawsze na wierzchu” (✓) · separator · „Ustawienia” · „Zakończ”.
- Implementacja: Win32 `Shell_NotifyIcon` lub biblioteka (np. H.NotifyIcon) — decyzja techniczna poza tym dokumentem; wygląd i zachowanie jak wyżej.

### 7.4 Pasek menu (macOS) `Wymaga platformy`

- **NSStatusItem:** ikona szablonowa (template image) cebuli 18 pt + opcjonalnie koszt dnia obok („12,34 $”, font systemowy monospaced-digit — wyjątek od reguły fontów, bo pasek menu wymaga fontu systemowego).
- Kolor tekstu wg stanu: domyślny; przy ostrzeżeniu dodatkowa kropka `status.warning`.
- **Klik:** popover z mini-widokiem (§7.2, te same moduły) + stopka z „Otwórz OnionLLM”, „Ustawienia…”, „Zakończ”.
- Implementacja w Mac Catalyst wymaga mostka AppKit (bundle plugin) — status `Wymaga platformy`.

### 7.5 Onboarding (pierwsze uruchomienie)

Pełnoekranowy kreator w oknie głównym (bez sidebaru), wyśrodkowana karta 640, `radius.xl`, kroki z wskaźnikiem postępu („Krok 1 z 3”, pasek kropek). Zawsze możliwe „Pomiń” (ustawienia domyślne) i „Wstecz”.

| Krok | Tytuł | Treść | Akcja główna |
| --- | --- | --- | --- |
| 1 | „Wykryte narzędzia” | Lista wykrytych źródeł (ListItem: SourceBadge, nazwa, wykryty folder, Toggle — domyślnie włączone wszystkie wykryte); niewykryte zwinięte („+14 niewykrytych — pokaż”). Brak wykrytych → EmptyState z opisem i „Dodaj ścieżkę ręcznie”. | „Dalej” |
| 2 | „Wygląd i waluta” | Motyw (3 kafle z podglądem), Waluta wyświetlania, Gęstość. | „Dalej” |
| 3 | „Praca w tle” | Toggle „Uruchamiaj przy starcie systemu”, „Ikona w zasobniku / pasku menu”, „Powiadomienia o limitach” (z prośbą o zgodę systemu przy włączeniu), opcjonalny budżet dzienny. | „Zacznij” (Primary `lg`) |

**Zmiany 1.7:** krok 2 to „Wygląd i waluta” (USD · PLN · EUR, kurs NBP); import pokazuje OnionRings z postępem plików trzech pierwszych narzędzi i listę „Pliki X z Y · N wpisów” (zdarzenia `SyncProgress`); „Kontynuuj w tle” zostawia Banner `S-IMPORT` z bieżącym źródłem i liczbą wpisów nad treścią ekranów.

**Stan wdrożenia (1.5, `Views/OnboardingView.cs`):** `S-FIRST` = brak znacznika ukończenia w `ViewState`; instalacje, których baza była już synchronizowana, są oznaczane jako ukończone bez kreatora. Pętla odświeżania startuje dopiero po kreatorze (pierwszy sync uwzględnia wybór narzędzi). Krok 2 nosi tytuł „Wygląd” — waluta `Wymaga danych`; motyw i gęstość działają na żywo (kafle z miniaturą okna rysowaną ze słownika tokenów danego motywu), „Pomiń” cofa podgląd. Krok 3 istnieje tylko na Windows i zawiera wyłącznie „Uruchamiaj przy starcie systemu” (zasobnik, powiadomienia i budżet — `Wymaga platformy` / `Wymaga danych`); na macOS kreator ma 2 kroki. Import: Spinner + lista śledzonych narzędzi zamiast pierścieni z postępem (postęp „pliki X z Y” wymaga zdarzeń postępu synchronizacji w Core — §11.4); Toast z liczbą wpisów i narzędzi po zakończeniu; „Kontynuuj w tle” zamyka kreator (Banner `S-IMPORT` nierenderowany z tego samego powodu). „Dodaj ścieżkę ręcznie” kończy kreator i otwiera Źródła.

Po „Zacznij”: **import historii** — ekran z OnionRings animowanym jako wskaźnik postępu (pierścienie = źródła), lista źródeł z postępem (pliki X z Y, wpisy), tekst „Wczytywanie historii z dysku. Nic nie jest wysyłane do internetu.” Po zakończeniu — przejście do „Dziś” z Toastem „Wczytano 12 480 wpisów z 4 narzędzi”. Import można zminimalizować („Kontynuuj w tle”) — wtedy `S-IMPORT` na ekranach.

### 7.6 Powiadomienia systemowe `Wymaga platformy`

- Windows: App Notifications (Windows App SDK); macOS: `UNUserNotificationCenter`.
- Struktura: tytuł (stan) · treść (liczby + czas) · akcje: „Otwórz” (domyślna), „Wycisz na 1 h”.
- Ikona aplikacji; bez dźwięku dla ostrzeżeń, domyślny dźwięk systemowy dla przekroczeń.
- Treści → §8.4. Deduplikacja i ciche godziny → §5.8.

### 7.7 Logo, ikona aplikacji, splash

**Znak:** przekrój cebuli — **3 koncentryczne łuki** (otwarte u góry o ~40°, jak pierścienie limitów) + mała pełna kropka w środku. Grubość łuków jednakowa (≈ 9% średnicy), odstępy równe grubości. Kolory: łuki `palette.onion.700` → `palette.onion.500` → `palette.onion.300` (od zewnątrz), kropka `palette.husk.400`. Na ciemnym tle: `onion.400` → `onion.300` → `onion.200`, kropka `husk.400`.

**Warianty:**

| Wariant | Użycie | Specyfikacja |
| --- | --- | --- |
| Pełny kolor | sidebar, onboarding, O aplikacji | SVG, siatka 24×24, min. 16 |
| Monochromatyczny | tray Windows (biały/czarny wg paska zadań), macOS template | jeden kolor, łuki bez kropki przy 16 px |
| Ikona aplikacji | `appicon.svg` (tło) + `appiconfg.svg` (znak) | tło: kwadrat z zaokrągleniem, gradient `onion.950` → `onion.700`; znak w kolorach dla ciemnego tła, 60% powierzchni |
| Splash | `splash.svg` | znak 128 na `color.bg.canvas` (wg motywu systemu), bez napisów |

**Logotyp:** „OnionLLM” — Inter SemiBold, „LLM” w `text.secondary` (dwutonowo). Nie stosujemy wersalików ani pochylenia.

Pliki szablonu (`dotnet_bot.png`, `appicon.svg`/`splash.svg` z „.NET”, kolor `#512BD4` w csproj) zostały zastąpione plikami z `tools/design/gen_logo.py` (siatka 24: obrys 2, promienie 11/7/3, kropka 1; `Resources/Images/onion_logo.svg` i `onion_logo_dark.svg`, `AppIcon/*`, `Splash/splash.svg`). Splash ma tło `bg.canvas` motywu jasnego (`MauiSplashScreen` przyjmuje jeden kolor).

---

## 8. Treści i UX writing

### 8.1 Ton

- **Bezosobowo i rzeczowo.** Nie zwracamy się do użytkownika per „Ty” ani „Pan/Pani”: „Brak aktywności dziś”, „Limit blisko wyczerpania”, „Zaloguj się ponownie w Claude Code” (tryb rozkazujący dopuszczalny w instrukcjach naprawy).
- **Krótko.** Etykieta ≤ 3 słowa; opis ≤ 2 zdania; komunikat błędu: co się stało + co zrobić.
- **Liczby zamiast przymiotników:** „62% limitu 5 h” zamiast „spore zużycie”.
- **Spokojnie przy alarmach:** bez wykrzykników, bez wersalików (poza `overline`), bez emoji.

### 8.2 Zasady pisowni

- **Przyciski i polecenia:** bezokolicznik — „Odśwież”, „Eksportuj CSV”, „Dodaj nadpisanie”, „Przebuduj bazę”.
- **Wielka litera** tylko na początku etykiety (nie „Title Case”): „Limit tokenów bloku”, nie „Limit Tokenów Bloku”.
- **Nazwy własne narzędzi i modeli** — oryginalna pisownia: „Claude Code”, „GitHub Copilot”, „pi-agent”, „gpt-5-codex”.
- **Wielokropek „…”** na końcu etykiety akcji, która otwiera dialog z dalszymi wyborami: „Przebuduj bazę…”.
- **Cudzysłowy** polskie „…” w tekstach; w wartościach technicznych bez cudzysłowów, fontem Mono.
- **Jednostki:** „h”, „min”, „s” ze spacją niełamliwą: „5 h”, „15 min”.
- **Pauza** „—” (półpauza „–” w zakresach: „1–26 lis”).

### 8.3 Odmiana liczebników

Każdy tekst z liczbą korzysta z helpera `Fmt.Plural(n, "token", "tokeny", "tokenów")`:

| n | Forma | Przykład |
| --- | --- | --- |
| 1 | mianownik l.poj. | „1 token”, „1 sesja”, „1 plik” |
| 2–4, 22–24, 32–34… (bez 12–14) | mianownik l.mn. | „3 tokeny”, „2 sesje”, „4 pliki” |
| 0, 5–21, 25–31…, 12–14 | dopełniacz l.mn. | „5 tokenów”, „12 sesji”, „0 plików” |
| ułamki i skróty (1,2 M) | dopełniacz l.poj. dla ułamków („1,5 tokena” — unikamy) → przy skrótach zawsze „tokenów” | „1,2 M tokenów” |

Słownik form: token/tokeny/tokenów · sesja/sesje/sesji · plik/pliki/plików · źródło/źródła/źródeł · model/modele/modeli · wiersz/wiersze/wierszy · blok/bloki/bloków · wywołanie/wywołania/wywołań · projekt/projekty/projektów · wpis/wpisy/wpisów · dzień/dni/dni · kredyt/kredyty/kredytów · minuta/minuty/minut.

### 8.4 Komunikaty

**Powiadomienia systemowe:**

| Zdarzenie | Tytuł | Treść |
| --- | --- | --- |
| Limit 5 h ≥ 80% | „Limit 5-godzinny: 80%” | „Claude · reset za 1 h 40 min (o 16:30).” |
| Limit 5 h ≥ 100% | „Limit 5-godzinny wyczerpany” | „Claude · reset o 16:30.” |
| Limit 7 dni ≥ 80% | „Limit 7-dniowy: 80%” | „Claude · reset w piątek o 09:00.” |
| Blok — prognoza przekroczenia | „Blok przekroczy limit tokenów” | „Przy obecnym tempie ok. 15:40. Tempo: 4,20 $/h.” |
| Tempo wysokie | „Wysokie tempo spalania” | „Aktywny blok: 9,80 $/h · 4,1 k tok/min.” |
| Budżet dzienny ≥ 80% | „Budżet dzienny: 80%” | „12,10 $ z 15,00 $.” |
| Budżet miesięczny prognoza > 100% | „Prognoza przekracza budżet miesiąca” | „Prognoza: 312 $ z 300 $ (104%).” |

**Błędy (wzorzec: co + dlaczego + co zrobić):**

| Sytuacja | Tekst |
| --- | --- |
| Błąd sync | „Nie udało się odświeżyć danych. Poprzednie dane są nadal widoczne.” + „Spróbuj ponownie” / „Szczegóły” |
| Brak logowania Claude | „Limity Claude są niedostępne. Zaloguj się ponownie w Claude Code (`claude /login`).” |
| Brak sieci (limity/cennik) | „Brak połączenia — limity i cennik zostaną odświeżone automatycznie.” |
| Nieczytelne pliki | „Pominięto 3 pliki, których nie udało się odczytać. Koszt może być zaniżony.” + „Pokaż pliki” |
| Eksport nieudany | „Nie udało się zapisać pliku. Sprawdź uprawnienia do folderu <ścieżka>.” |
| Walidacja liczby | „Podaj liczbę od 5 do 3600.” |
| Niepoprawna strefa | „Nieznana strefa czasowa. Wybierz z listy.” |
| Przebudowa bazy — potwierdzenie | Tytuł: „Przebudować bazę?” Treść: „Baza zostanie odtworzona z logów na dysku. Wpisy z logów, które narzędzia już usunęły, zostaną utracone. Zwykle trwa to 10–60 s.” Przyciski: „Anuluj” / „Przebuduj” (Danger) |

**Toasty sukcesu:** „Wyeksportowano 26 wierszy”, „Cennik zaktualizowany: 1 204 modele”, „Baza przebudowana: 12 480 wpisów”, „Skopiowano ID sesji” (tylko gdy kopiowanie nie ma lokalnego potwierdzenia).

### 8.5 Stany puste

| Miejsce | Tytuł | Opis | Akcja |
| --- | --- | --- | --- |
| Brak źródeł (`S-NOSRC`) | „Nie znaleziono danych żadnego narzędzia” | „OnionLLM czyta lokalne logi 20 narzędzi AI. Jeśli dane są w niestandardowym folderze, wskaż go ręcznie.” | „Skonfiguruj źródła” |
| Dziś — brak aktywności | „Brak aktywności dziś” | „Ostatnia aktywność: wczoraj, 18:40 (Claude Code).” | — |
| Raporty — pusty zakres | „Brak danych w tym zakresie” | „Zmień zakres lub źródło.” | „Pokaż ostatnie 30 dni” |
| Sesje — brak wyników | „Brak sesji pasujących do „xyz”” | — | „Wyczyść filtry” |
| Bloki — brak Claude | „Bloki dotyczą tylko Claude Code” | „Nie wykryto danych Claude Code na tym komputerze.” | „Skonfiguruj Claude Code” |
| Limity — brak budżetów | „Nie ustawiono budżetów” | „Budżet dzienny lub miesięczny pozwoli dostać ostrzeżenie, zanim koszty przekroczą plan.” | „Ustaw budżet” |
| Modele — wszystkie wycenione (filtr „Tylko bez ceny”) | „Wszystkie modele mają ceny” | — | „Pokaż wszystkie modele” |
| Nadpisania cen — puste | „Brak nadpisań cen” | „Nadpisanie zastępuje cenę z LiteLLM lub models.dev dla jednego modelu.” | „Dodaj nadpisanie” |

### 8.6 Słownik etykiet

Etykiety wiążące (kanoniczne). Nowa etykieta = wpis tutaj.

| Obszar | Etykiety |
| --- | --- |
| Nawigacja | „Dziś”, „Limity”, „Raporty”, „Sesje”, „Bloki”, „Projekty”, „Modele”, „Źródła”, „Ustawienia”; grupy: „Przegląd”, „Analiza”, „System” |
| Akcje globalne | „Odśwież”, „Eksportuj”, „CSV”, „JSON (format ccusage)”, „Kopiuj jako tabelę”, „Wyczyść filtry”, „Pokaż dane w tabeli”, „Tryb kompaktowy”, „Zwiń panel”, „Rozwiń panel” |
| Metryki | „Koszt”, „Koszt łącznie”, „Tokeny”, „Wejście”, „Wyjście”, „Zapis cache”, „Zapis cache 1 h”, „Odczyt cache”, „Rozumowanie”, „Udział cache”, „Wywołania”, „Kredyty”, „Sesje”, „Czas trwania” |
| Okresy | „Dzień”, „Tydzień”, „Miesiąc”, „Dziś”, „Wczoraj”, „Ostatnie 7 dni”, „Ostatnie 30 dni”, „Ten miesiąc”, „Poprzedni miesiąc”, „Ostatnie 90 dni”, „Ten rok”, „Cała historia”, „Własny zakres”, „w toku” |
| Limity i bloki | „Limit 5-godzinny” (krótko „5 h”), „Limit 7-dniowy” („7 dni”), „Aktywny blok”, „Przerwa”, „Pozostało”, „Reset za …”, „Prognoza”, „Tempo spalania”, „normalne”, „umiarkowane”, „wysokie”, „Blisko limitu”, „Przekroczono”, „Bez limitu”, „Budżet dzienny”, „Budżet miesięczny” |
| Stany źródeł | „Aktywne”, „Wykryte”, „Nie wykryto”, „Wyłączone”, „Błędy”, „Dane lokalne” |
| Sortowanie | „Najnowsze”, „Najdroższe”, „Najwięcej tokenów”, „Sortuj:” |
| Markery | „Koszt niepełny”, „Koszt szacowany”, „Przeliczono”, „brak ceny”, „nadpisana” |
| Ustawienia | podstrony: „Ogólne”, „Wygląd”, „Okno i zasobnik”, „Powiadomienia i budżety”, „Obliczenia”, „Projekty”, „Cennik”, „Statusline”, „Zaawansowane”, „Diagnostyka”, „O aplikacji”, „Źródła danych” (→ ekran Źródła) |

### 8.7 Glosariusz PL ↔ EN

UI używa wyłącznie terminu polskiego; angielski pojawia się w tooltipie przy pierwszym wystąpieniu na ekranie (np. „Tempo spalania (burn rate)”) oraz w eksportach/CLI.

| PL (UI) | EN (ccusage / CLI / API) | Definicja |
| --- | --- | --- |
| Tokeny wejścia | input tokens | tokeny wysłane do modelu (bez cache) |
| Tokeny wyjścia | output tokens | tokeny wygenerowane przez model |
| Zapis cache | cache creation / cache write tokens | tokeny zapisane do cache promptu (5 min) |
| Zapis cache 1 h | 1h cache write | zapis do cache o czasie życia 1 h (droższy) |
| Odczyt cache | cache read tokens | tokeny odczytane z cache (tańsze) |
| Rozumowanie | reasoning tokens | tokeny „myślenia” modelu (część wyjścia) |
| Udział cache | cache share / hit ratio | (zapis + odczyt cache) / wszystkie tokeny |
| Koszt zapisany | stored cost | koszt podany przez narzędzie w logu |
| Koszt wyliczony | calculated cost | koszt z cennika × tokeny |
| Tryb kosztu | cost mode (auto / calculate / display) | automatyczny / wyliczany / zapisany |
| Blok (5-godzinny) | (5-hour) billing block, session block | okno rozliczeniowe Claude |
| Przerwa | gap | czas bez aktywności między blokami |
| Tempo spalania | burn rate | koszt lub tokeny na jednostkę czasu w bloku |
| Prognoza | projection | szacowany koszt/tokeny do końca bloku/okresu |
| Limit 5-godzinny / 7-dniowy | 5-hour / 7-day (weekly) usage limit | limity konta z API Anthropic |
| Źródło | source / agent / tool | narzędzie AI, z którego logów czytamy |
| Sesja | session | jedna rozmowa/uruchomienie agenta |
| Wywołanie | request / API call | jedno zapytanie do modelu |
| Kredyty | credits | jednostka rozliczeń niektórych narzędzi (Amp, Codebuff, Copilot) |
| Cennik | pricing (LiteLLM, models.dev) | ceny modeli za token |
| Nadpisanie ceny | pricing override | cena ustawiona ręcznie |
| Alias modelu | model alias | mapowanie nazwy modelu na wpis cennika |
| Poziom szybkości | speed tier (standard/fast/flex) | wariant cenowy modelu |
| Statusline | statusline | linia statusu Claude Code generowana przez CLI |
| Baza | ledger (`ledger.db`) | lokalna baza wpisów zużycia |
| Budżet | budget | limit kosztów ustawiony przez użytkownika |

---

## 9. Dostępność

Standard: **WCAG 2.2 poziom AA**. Wymagania obowiązują każdy komponent i ekran.

### 9.1 Kontrast i kolor

- Tekst ≥ 4.5:1, duży tekst (≥ 18,66 dp SemiBold / 24 dp Regular) i elementy graficzne/obrysy ≥ 3:1 — wartości w §2.1.9; nowe pary MUSZĄ być zmierzone.
- **Kolor nigdy nie jest jedynym nośnikiem informacji:** statusy zawsze ze słowem; progi z liczbą procent i wzorem przy przekroczeniu; serie wykresów z legendą i tooltipem; TrendDelta ze strzałką i słowem dla czytnika.
- Tryb wysokiego kontrastu systemu: aplikacja NIE MUSI mieć osobnego motywu, ale NIE MOŻE się psuć (obrysy pozostają widoczne, tekst czytelny).

### 9.2 Klawiatura

- Każdy element interaktywny jest osiągalny klawiszem Tab w logicznej kolejności: Sidebar → PageHeader → FilterBar → treść (od lewej-góry) → StatusBar → Drawer (jeśli otwarty, fokus przechodzi do niego).
- **Widoczny fokus** (pierścień 2 dp `border.focus` + odstęp 2 dp) przy nawigacji klawiaturą; przy kliknięciu myszą pierścień nie jest pokazywany.
- Brak pułapek fokusu poza modalnymi Dialog/CommandPalette/Drawer w trybie Overlay (tam pułapka + Esc zamyka i zwraca fokus).
- Skróty → [§9.6](#96-skróty-klawiszowe).

### 9.3 Czytniki ekranu (Narrator, VoiceOver)

- `SemanticProperties.Description` dla: przycisków ikonowych, kafli KPI (pełne zdanie: „Koszt 30 dni: 212,40 dolara, wzrost o 8 procent”), wierszy tabel (§4.5.1), wykresów (§4.8.1), OnionRings (§4.4.3), markerów cen.
- `SemanticProperties.HeadingLevel`: tytuł strony = Level1, Section = Level2, nagłówki kart = Level3.
- Liczby czytane w pełnej formie („1,2 miliona tokenów”, „12,34 dolara”) — `Fmt` udostępnia warianty `*Spoken`.
- Zmiany dynamiczne ogłaszane przez `SemanticScreenReader.Announce` oszczędnie: wynik akcji (eksport, zapis), przekroczenie progu, błąd sync. NIE ogłaszamy każdego automatycznego odświeżenia.
- `AutomationId` na każdym elemencie interaktywnym i kluczowej liczbie (konwencja: `<Ekran>.<Element>`, np. `Today.HeroCost`, `Reports.ExportButton`) — wymagane także dla testów UI.

### 9.4 Rozmiar i skalowanie

- Minimalny rozmiar tekstu 11 dp; minimalny cel kliknięcia 24×24 dp (WCAG 2.5.8), zalecany 32×32.
- Układ działa przy skalowaniu tekstu systemu do 200% (Windows „Rozmiar tekstu”, macOS Dynamic Type — w Mac Catalyst): tekst się zawija lub obcina z tooltipem, nic nie nachodzi na siebie; tabele przechodzą w tryb kart wcześniej.
- Zoom okna przez skalowanie DPI 100–300% bez rozmytych ikon (font ikon, SVG).

### 9.5 Ruch

- Ustawienie „Animacje” + systemowe ograniczenie ruchu (§2.7.4).
- Brak migotania > 3 razy na sekundę. Pętle animacji (puls, shimmer) mają amplitudę ≤ 50% krycia.

### 9.6 Skróty klawiszowe

Na macOS Ctrl → ⌘, Alt → ⌥. Skróty są pokazywane w tooltipach, menu i palecie poleceń.

| Skrót | Akcja | Zakres |
| --- | --- | --- |
| Ctrl+K | Paleta poleceń | globalny |
| Ctrl+1 … Ctrl+8 | Dziś, Limity, Raporty, Sesje, Bloki, Projekty, Modele, Źródła | globalny |
| Ctrl+, | Ustawienia | globalny |
| F5 / Ctrl+R | Odśwież dane | globalny |
| Ctrl+B | Zwiń / rozwiń sidebar | globalny |
| Ctrl+Shift+M | Tryb kompaktowy (mini-widok) | globalny |
| Ctrl+Shift+T | Zawsze na wierzchu | globalny |
| Ctrl+Shift+L | Przełącz motyw (Systemowy → Jasny → Ciemny) | globalny |
| Alt+← / Alt+→ | Wstecz / Dalej (historia nawigacji) | globalny |
| / lub Ctrl+F | Fokus na wyszukiwaniu ekranu | ekrany z SearchField |
| Ctrl+E | Eksportuj (otwiera menu eksportu) | ekrany z eksportem |
| ↑ / ↓, J / K | Poprzedni / następny wiersz (i element w drawerze) | tabele, drawer |
| Enter | Otwórz szczegóły | tabele |
| → / ← | Rozwiń / zwiń wiersz | tabele z breakdown |
| Esc | Zamknij drawer / dialog / paletę; wyczyść wyszukiwanie | kontekstowy |
| Ctrl+C | Kopiuj wiersz (TSV) / wartość | tabele, KeyValueList |
| Alt+↑ / Alt+↓ | Przesuń kartę źródła | Dziś |
| ? (Shift+/) | Lista skrótów (dialog) | globalny |

**Stan wdrożenia (1.5):** działają wszystkie skróty z tabeli poza Ctrl+Shift+M (mini-widok — `Wymaga platformy`). Alt+↑/↓ (1.6) przesuwa kartę źródła, która ostatnio dostała kliknięcie lub fokus swojego przycisku „Więcej” (Tab); fokus wraca na kartę po przesunięciu. Ctrl+F i „/” ustawiają fokus w pierwszym widocznym SearchField ekranu, Ctrl+E otwiera menu pierwszego widocznego SplitButton („Eksportuj”); na ekranach bez nich skrót nic nie robi. Ctrl+Shift+L, Ctrl+Shift+T i „Przełącz gęstość” z palety zapisują konfigurację (`ViewModels/WindowActions.cs`) i pokazują Toast z nowym stanem; Ustawienia odświeżają te pola, żeby autozapis nie przywrócił starej wartości. Lista skrótów: `Navigation/ShortcutHelp.cs`; na macOS ⇧⌘/.

Implementacja: `Navigation/ShortcutService` z implementacjami platformowymi (Windows: `PreviewKeyDown` na korzeniu okna WinUI; macOS: menu „Widok” w pasku menu — `MenuBarItems` z `KeyboardAccelerator`, natywnie odkrywalne i zamieniane na `UIKeyCommand`; Wstecz/Dalej na macOS: ⌘[ / ⌘]). Skróty nie działają, gdy fokus jest w polu tekstowym, z wyjątkiem Ctrl+K, Esc, F5 i Ctrl+1–8.

---

## 10. Zasady dla agentów i checklista PR

### 10.1 Zasady bezwzględne

1. **Zakaz literałów wizualnych** w ekranach, komponentach i kodzie C#: kolory (hex, `Colors.*`), `FontSize`, `FontFamily`, `Margin`, `Padding`, `Spacing`, `CornerRadius`, `HeightRequest`/`WidthRequest` elementów stylowanych, czasy animacji. Wyjątki: `Tokens.*.xaml` (definicje) oraz `0`/`*`/`Auto` w definicjach siatki. Kontrola: `python tools/design/lint.py` (hex, `FontSize`/`Margin`/`Padding`/`CornerRadius`/`Spacing`/`Width|HeightRequest` z liczbą, `AppThemeBinding`, fonty platformowe, `DisplayAlert`, wbudowany `ProgressBar`) poza `Resources/Styles/Tokens.*` MUSI dać 0 wyników; `--summary` daje zestawienie do opisu PR.
2. **Tylko komponenty z rejestru** (§11.3). Brak potrzebnego komponentu → najpierw specyfikacja w §4 (w tym samym PR), potem implementacja w `Controls/`.
3. **Aktualizacja DESIGN.md w tym samym PR**, jeśli zmiana: dodaje/zmienia token, komponent, wzorzec, ekran, etykietę (§8.6), skrót (§9.6) lub status funkcji (§11.4). Zmiana wersji dokumentu w nagłówku (semver: poprawka / nowy element / zmiana łamiąca).
4. **UI-first:** każda nowa funkcja w Core MUSI mieć miejsce w UI (ekran, drawer, ustawienie lub polecenie palety) — dopisane do §6.
5. **Bez `DisplayAlert`** do prezentacji danych; dozwolony wyłącznie jako awaryjny komunikat błędu krytycznego przed załadowaniem UI.
6. **Formatowanie tylko przez `Fmt`** / `MoneyLabel` / `TokensLabel` (§4.5.6, §5.1–5.2).
7. **Nie udawać danych:** funkcja `Wymaga danych` bez warstwy danych NIE jest renderowana (ani z wartościami przykładowymi, ani pustymi kontrolkami).

### 10.2 Gdzie co dodawać

| Potrzeba | Miejsce |
| --- | --- |
| Nowy kolor semantyczny | `tools/design/gen_tokens.py` (`SEMANTIC`, wartość light i dark) → `Tokens.Light/Dark.xaml` + §2.1.4 + §2.1.9 (kontrast) + §11.1 |
| Nowe źródło danych | warianty z `python tools/design/palette.py` (§11.5) → wiersz w §2.1.6 i w `SOURCES` w `gen_tokens.py` |
| Nowy rozmiar/odstęp | najpierw sprawdź, czy istniejący token wystarcza; jeśli nie — `gen_tokens.py` (`STATIC_*` lub `DENSITY*`) + §2 + §11.1 |
| Nowy styl tekstu | `Styles.Text.xaml` + §2.2.2 |
| Nowy komponent | `OnionLLM/Controls/<Nazwa>.xaml(.cs)` + §4 + §11.3 |
| Nowy wykres | `OnionLLM/Controls/Charts/<Nazwa>Drawable.cs` dziedziczący `ChartDrawable` + §4.8.2 |
| Nowy ekran | `OnionLLM/Pages/<Nazwa>Page.xaml` + `ViewModels/<Nazwa>ViewModel.cs` + §6 (z macierzą stanów) + Sidebar (§4.7.1) + paleta + skrót |
| Nowa ikona | `Icons.cs` (zawiera cały zestaw Lucide z przypiętej wersji; nowa wersja → `tools/fonts/build_fonts.py`) + §11.2 |
| Nowa etykieta / komunikat | §8.4–8.6 |

### 10.3 Checklista PR (UI)

Każdy PR zmieniający UI zawiera w opisie wypełnioną listę:

- [ ] Brak literałów wizualnych poza `Tokens.*.xaml` (wynik wyszukiwania w opisie).
- [ ] Sprawdzone **oba motywy** (jasny, ciemny) — zrzuty ekranu.
- [ ] Sprawdzone **obie gęstości** (komfortowa, kompaktowa).
- [ ] Sprawdzone **trzy breakpointy** (≥ 1280, 900–1279, < 900 — w tym minimalne okno 680×560) — zrzuty.
- [ ] **Klawiatura:** pełna obsługa Tab/Enter/Esc/strzałki, widoczny fokus, skróty działają.
- [ ] **Czytnik ekranu:** opisy semantyczne kluczowych elementów (sprawdzone Narratorem lub VoiceOver).
- [ ] **Kontrast** nowych par kolorów zmierzony i dopisany do §2.1.9.
- [ ] **Stany** z §6.0 dotyczące ekranu: ładowanie, pusto, brak wyników, błąd, dane częściowe, brak cen — zrzuty przynajmniej `S-LOAD`, `S-EMPTY`, `S-SYNCERR`.
- [ ] **Ruch:** animacje zgodne z §2.7; sprawdzone tryby „Ograniczone” i „Wyłączone”.
- [ ] **Windows i macOS** (lub uzasadnienie, że zmiana nie zależy od platformy).
- [ ] **Teksty** zgodne z §8 (ton, odmiana liczebników, słownik etykiet).
- [ ] `AutomationId` na nowych elementach interaktywnych.
- [ ] DESIGN.md zaktualizowany (lub „nie dotyczy” z uzasadnieniem).
- [ ] Zmieniony ekran przechodzi test dymny (`ONIONLLM_START_SCREEN=<Ekran> ONIONLLM_SCREEN_SMOKE=1 ONIONLLM_DIAG_LOG=<plik>` → „smoke done”); zmiany w nawigacji, skrótach lub palecie — `ONIONLLM_PALETTE_SELFTEST=1` („palette selftest done: 0 failures”); w onboardingu — `ONIONLLM_ONBOARDING=1 ONIONLLM_ONBOARDING_SELFTEST=1` („onboarding selftest done”); wskaźniki paska bocznego, drawer, obrys nagłówka, filtry na narrow, klawiatura wykresów i tabel, Alt+↑/↓ — `ONIONLLM_UI_SELFTEST=1` („ui selftest done: 0 failures”).
- [ ] Nowy lub zmieniony komponent ma przykład w Galerii komponentów (DEBUG); autotest galerii przechodzi (`ONIONLLM_START_SCREEN=Gallery ONIONLLM_GALLERY_SELFTEST=1 ONIONLLM_DIAG_LOG=<plik>` — w logu każdy krok „selftest ok”, na końcu „selftest done”).

---

## 11. Załączniki

### 11.1 Tabela tokenów (klucze XAML)

**Kolory semantyczne** (`Tokens.Light.xaml` / `Tokens.Dark.xaml`; każdy klucz ma też wariant `…Brush`):

| Klucz | Light | Dark |
| --- | --- | --- |
| `ColorBgCanvas` | `#F7F7F9` | `#0D0C11` |
| `ColorBgSidebar` | `#F2F1F5` | `#121117` |
| `ColorSurface1` | `#FFFFFF` | `#16151C` |
| `ColorSurface2` | `#F3F2F6` | `#1D1C24` |
| `ColorSurface3` | `#EAE9EF` | `#26252E` |
| `ColorSurfaceOverlay` | `#FFFFFF` | `#1D1C24` |
| `ColorSurfaceInverse` | `#16151B` | `#F3F2F7` |
| `ColorScrim` | `#6616151B` | `#99000000` |
| `ColorBorderSubtle` | `#ECEBF0` | `#24232B` |
| `ColorBorderDefault` | `#E3E2E8` | `#2D2C36` |
| `ColorBorderStrong` | `#85828F` | `#6E6B7B` |
| `ColorBorderFocus` | `#A23272` | `#EB9BC6` |
| `ColorTextPrimary` | `#16151B` | `#F3F2F7` |
| `ColorTextSecondary` | `#4F4D59` | `#B9B6C4` |
| `ColorTextTertiary` | `#6B6875` | `#918E9E` |
| `ColorTextDisabled` | `#A9A7B2` | `#5C5A68` |
| `ColorTextLink` | `#A23272` | `#EB9BC6` |
| `ColorTextOnAction` | `#FFFFFF` | `#1A0812` |
| `ColorTextOnInverse` | `#F3F2F7` | `#16151B` |
| `ColorTextOnDanger` (tekst na Button `Danger`) | `#FFFFFF` | `#1A0812` |
| `ColorActionPrimary` | `#A23272` | `#DA6AA7` |
| `ColorActionPrimaryHover` | `#86295E` | `#EB9BC6` |
| `ColorActionPrimaryPressed` | `#6A214B` | `#F5C6DF` |
| `ColorActionSubtle` | `#FBE4F0` | `#3A1429` |
| `ColorActionSubtleBorder` | `#F5C6DF` | `#6A214B` |
| `ColorActionOnSubtle` | `#A23272` | `#EB9BC6` |
| `ColorStatusSuccess` / `…Bg` | `#117A3C` / `#E7F6EC` | `#3DD68C` / `#10291C` |
| `ColorStatusWarning` / `…Bg` | `#A35A00` / `#FDF1DE` | `#F5B23D` / `#2E2210` |
| `ColorStatusDanger` / `…Bg` | `#C4262E` / `#FDECEC` | `#FF6B6B` / `#341517` |
| `ColorStatusInfo` / `…Bg` | `#1F64D1` / `#E8F0FD` | `#6BA4FF` / `#13223B` |
| `ColorBurnNormal` | `#117A3C` | `#3DD68C` |
| `ColorBurnModerate` | `#A35A00` | `#F5B23D` |
| `ColorBurnHigh` | `#C4262E` | `#FF6B6B` |
| `ColorDataCost` | `#A23272` | `#EB9BC6` |
| `ColorDataTokens` | `#0F7C78` | `#4FD1C5` |
| `ColorDataInput` | `#0F7C78` | `#4FD1C5` |
| `ColorDataOutput` | `#C2410C` | `#FF9A62` |
| `ColorDataCacheWrite` | `#7A4FD6` | `#A98BFF` |
| `ColorDataCacheRead` | `#2667C9` | `#6BA4FF` |
| `ColorDataTrack` | `#E9E8EE` | `#2A2932` |
| `ColorDataGrid` | `#ECEBF0` | `#24232B` |
| `ColorDataAxis` | `#6B6875` | `#918E9E` |
| `ColorDataOther` | `#8F8C9B` | `#6E6B7B` |
| `ColorDataHighlight` | `#A87A0C` | `#F2C14E` |
| `ColorChartCat1` … `ColorChartCat8` | §2.1.7 | §2.1.7 |
| `ColorHeroStart` / `Mid` / `End` | `#2E0D20` / `#6A214B` / `#86295E` | `#2E0D20` / `#5A1C40` / `#86295E` |
| `ColorHeroText` / `TextSecondary` | `#FFFFFF` / `#F5C6DF` | `#FFFFFF` / `#F5C6DF` |
| `ColorHeroBorder` / `RingTrack` | `#33FFFFFF` / `#33FFFFFF` | `#26FFFFFF` / `#26FFFFFF` |
| `ColorStateHover` | `#0A16151B` | `#0FF3F2F7` |
| `ColorStatePressed` | `#1416151B` | `#1AF3F2F7` |
| `ColorStateSelected` (= `action.subtle`) | `#FBE4F0` | `#3A1429` |
| `ColorStateDragged` | `#1F16151B` | `#24F3F2F7` |
| `ColorHeroDecoration` (pierścienie dekoracyjne hero, §2.1.5) | `#14FFFFFF` | `#14FFFFFF` |
| `ColorSwitchThumb` | `#FFFFFF` | `#FFFFFF` |
| `ColorHeroHover` (hover IconButton `OnHero`, §4.1.2) | `#1AFFFFFF` | `#1AFFFFFF` |
| `ColorHeroPillBg` (pigułka TrendDelta `OnHero`, §4.3.4) | `#1FFFFFFF` | `#1FFFFFFF` |
| `ColorHeroProgressWarning` / `…Danger` (progi na hero, §4.4.1, §4.4.3) | `#FFD27A` / `#FF9A9A` | jak light |
| `ColorStripe` (ukośne paski: przekroczenie, okres „w toku”, drugie kodowanie serii) | `#33FFFFFF` | `#33FFFFFF` |
| `ColorSegmentActive` (aktywny segment SegmentedControl, §4.2.5) | `#FFFFFF` | `#26252E` |
| `ColorStatus<Rodzaj>Border` (obrys Bannera — status z kryciem 30%, §4.6.4) | `#4D` + status light | `#4D` + status dark |
| `ColorHeat0` … `ColorHeat4` (skala heatmapy, §4.8.2 F) | `surface.2`, onion 200/400/600/800 | `surface.2`, onion 900/700/400/300 |
| `ImageLogo` (`FileImageSource`, logo wg motywu, §7.7) | `onion_logo.png` | `onion_logo_dark.png` |
| `ColorTransparent` | `#00FFFFFF` | `#00000000` |
| `BrushHero` (`LinearGradientBrush` 0,0 → 1,1 z `ColorHeroStart/Mid/End`) | §2.1.5 | §2.1.5 |
| `ColorSourceUnknown` / `…Avatar` / `…OnAvatar` (źródło bez wiersza w §2.1.6) | `#8F8C9B` | `#6E6B7B` |
| `ColorSource<Id>` / `ColorSource<Id>Avatar` / `ColorSource<Id>OnAvatar` | §2.1.6 | §2.1.6 |

**Prymitywy** (`Tokens.Primitives.xaml`): `PaletteOnion50` … `PaletteOnion950`, `PaletteHusk100` … `PaletteHusk700` (§2.1.1–2.1.2).

**Statyczne** (`Tokens.Static.xaml`): `Space0`, `Space050`, `Space100`, `Space150`, `Space200`, `Space300`, `Space400`, `Space500`, `Space600`, `Space800`, `Space1000`, `Space1200`, `Space1600`; `RadiusXs` (4), `RadiusSm` (6), `RadiusMd` (8), `RadiusLg` (12), `RadiusXl` (16), `RadiusFull` (9999) + warianty `Radius<Nazwa>Corner` (`CornerRadius`, dla `RoundRectangle`) i `RadiusSmInt`/`RadiusMdInt`/`RadiusLgInt` (`Button.CornerRadius` jest typu `int`); `StrokeDefault` (1), `StrokeFocus` (2), `StrokeFocusOffset` (2); `IconSm` (16), `IconMd` (20), `IconLg` (24), `IconXl` (48); `OpacityDisabled` (0.45), `OpacityMuted` (0.6), `ScalePressed` (0.98, M9), `ScaleDialogEnter` (0.96, M7), `ScalePulse` (2.2) i `OpacityPulse` (0.5) dla M10, `TypeUnitScale` (0.6, jednostka przy liczbie), `TypeInitialScale` (0.45, inicjał SourceBadge), `OpacityInlineBar` (0.35), `OpacityHover` (0.4, wygaszenie kategorii przy najechaniu), `OpacityGhost` (0.85, słupki zakończonych okresów), `StrokeDash` (2); `IconXs` (12), `IconTable` (14); `Type<Nazwa>Size/LineHeight` dla tokenów niezależnych od gęstości i `Type<Nazwa>Tracking` dla wszystkich (`TypePaletteInput` 16 — pole palety poleceń). Wymiary komponentów (`comp.*`, aliasy wartości z §4): `SizeMinTarget` (24), `SizeStatusBarHeight` (28), `SizeLogoSidebar` (28), `SizeSidebarWide/Rail/Narrow` (240/64/56), `SizeDrawerWidth/Min/Max` (420/360/640), `SizeDialogSm/Md` (440/560), `SizeToastWidth` (360), `SizePaletteWidth` (640), `SizeSettingsNav` (220), `SizeFormMaxWidth` (760), `SizeTextMaxWidth` (560), `SizeOnboardingCard` (640), `SizeOnboardingMinHeight` (480), `SizeThemeTile` / `SizeThemePreview` (160/72 — kafel motywu w onboardingu), `SizePaletteListMax` (400 — lista wyników palety), `OpacityScrimPalette` (0.5), `RatioPaletteTop` (0.2) / `RatioPaletteMaxWidth` (0.9) — położenie i szerokość palety względem okna, `SizeTokenBreakdownBar` (8), `SizeInlineBar` (4), `SizeModelTag` (20), `SizeBadge` (20), `SizeCheckbox` (18), `SizeStatusDot` (8), `SizeAvatarXs/Lg` (16/48), `SizeStatTileMin` (112), `SizeChartAux` (160), `SizeSparkline` (28), `SizeChartBarMax` (28), `SizeDotSm` (6), `SizeAccentBar` (3), `SizeTabUnderline` (2), `SizeIllustration` / `SizeIllustrationInline` (80/24), `SizeHeroDot` (6), `SizeFormLabel` / `SizeFormField` (280/400), `SizeIndent` (24), `SizePopoverMaxHeight` (360), `SizeSelectMinWidth` (160), `SizeCalendarCell` (32), `SizeEditorMinHeight` (120), `BreakpointWide/Medium/Cards` (1280/900/760). Grubości (`Thickness`): `PaddingButtonSm/Button/ButtonLg` (10/14/20 poziomo), `PaddingInput` (10,0), `PaddingChip` (10,0), `PaddingBadge` (8,0), `PaddingBanner` (16,12), `PaddingKbd` (6,2), `PaddingTooltip` (10,8), `PaddingModelTag` (6,0), `PaddingSidebar` (8), `PaddingNavItem` (10,0), `PaddingDialog` (24), `PaddingMenu` (4), `PaddingSegmented` (2), `PaddingStatusBar` (16,0), `PaddingToastHost` (16,16,16,40 — stos toastów nad StatusBar), `PaddingSidebarHeader` (6,0), `PaddingRow` (16,0), `ThicknessZero`. Style powierzchni: `Divider.Edge` (pionowa krawędź pełnej wysokości — prawy obrys sidebaru).

**Gęstość** (`Tokens.Comfortable.xaml` / `Tokens.Compact.xaml`): `SizeControlHeight` (36/30), `SizeControlHeightSm` (28/24), `SizeControlHeightLg` (44/36), `SizeRowHeight` (44/32), `SizeRowHeightDense` (36/28), `SizeNavItem` (36/32), `SizeHeaderHeight` (64/52), `SizeAvatarMd` (32/28), `SizeAvatarSm` (20/18), `SizeProgressBar` (6/4), `SizeRingHero` (168/136), `SizeRingThickness` (10/8), `LayoutPagePadding`, `LayoutSectionGap` (32/20), `LayoutGridGap` (16/12), `LayoutCardPadding` (16/12), `LayoutCardPaddingLg` (24/16), `LayoutContentMaxWidth` (1440/1600), `LayoutCardGap` (12/8), `LayoutRowPadding` (`16,12`/`16,8`), `SizeChartMain` (240/200, §4.8.4), `TypeBodySize` (14/13), `TypeBodyLineHeight`, `TypeLabelSize` (13/12), `TypeTableNumSize` (13/12), `TypeMetricMdSize` (20/18), `TypeDisplaySize` (44/40).

**Ruch** (C#, `MotionTokens`): `Instant` 0, `Fast` 120, `Base` 200, `Slow` 280, `Slower` 480, `Counter` 600, `Ambient` 2400, `Stagger` 30, `Shimmer` 1400, `Spin` 800 (ms); czasy interakcji: `SearchDebounce` 150 ms, `AutosaveDelay` 600 ms, `FeedbackVisible` 1,5 s, `ToastVisible` 5 s; `OnionEasing.Standard/Enter/Exit/Spring`.

**Elewacja** (style `Shadow.E1`, `Shadow.E2`, `Shadow.E3`, `Shadow.Hero` w `Styles.Surfaces.xaml`, wartości §2.5; w motywie ciemnym `Shadow.E1`/`E2` mają `Opacity=0`).

### 11.2 Mapa ikon (Lucide)

Nazwy wg lucide.dev (wersja przypięta). W `Icons.cs` jako PascalCase.

| Funkcja | Ikona |
| --- | --- |
| Dziś | `layout-dashboard` |
| Limity | `gauge` |
| Raporty | `chart-column` |
| Sesje | `square-terminal` |
| Bloki | `timer` |
| Projekty | `folder-kanban` |
| Modele | `cpu` |
| Źródła | `plug` |
| Ustawienia | `settings` |
| Odśwież | `refresh-cw` |
| Eksport | `download` |
| Kopiuj / skopiowano | `copy` / `check` |
| Szukaj | `search` |
| Filtry | `sliders-horizontal` |
| Kalendarz / zakres | `calendar` / `calendar-range` |
| Zamknij | `x` |
| Więcej | `ellipsis` |
| Rozwiń / zwiń | `chevron-down` / `chevron-up`; wiersz: `chevron-right` |
| Poprzedni / następny (drawer) | `chevron-up` / `chevron-down` |
| Sortowanie | `arrow-up-down`, `arrow-up`, `arrow-down` |
| Trend | `trending-up`, `trending-down`, `minus` |
| Tempo spalania | `flame` |
| Aktywny / live | `radio` |
| Przerwa | `pause` |
| Czas / reset | `clock`, `rotate-ccw` |
| Koszt | `circle-dollar-sign` |
| Tokeny | `coins` |
| Cache | `database-zap` |
| Budżet | `piggy-bank` |
| Powiadomienia / wyciszone | `bell` / `bell-off` |
| Dane lokalne | `hard-drive` |
| Folder | `folder-open` |
| Plik | `file-text` |
| Ostrzeżenie | `triangle-alert` |
| Błąd | `circle-alert` |
| Informacja | `info` |
| Sukces | `circle-check` |
| Przeciąganie | `grip-vertical` |
| Mini-widok / rozwiń okno | `picture-in-picture-2` / `maximize-2` |
| Zawsze na wierzchu | `pin` / `pin-off` |
| Sidebar zwiń / rozwiń | `panel-left-close` / `panel-left-open` |
| Motyw: systemowy / jasny / ciemny | `monitor` / `sun` / `moon` |
| Tabela danych (zamiast wykresu) | `table-2` |
| Dodaj / usuń | `plus` / `trash-2` |
| Edytuj | `pencil` |
| Link zewnętrzny | `external-link` |
| Klawiatura / skróty | `keyboard` |
| Cennik | `tag` |
| Diagnostyka | `stethoscope` |
| Statusline | `terminal` |
| Przebuduj bazę | `database` |

### 11.3 Rejestr komponentów

Komponenty są pisane w C# (kontrolki budowane w kodzie przez pomocnik `Controls/Ui.cs` — wyłącznie tokeny i klucze stylów) i pogrupowane w plikach; kolumna „Plik” wskazuje faktyczne miejsce. Każdy komponent ma przykład w **Galerii komponentów** (ekran tylko w buildzie DEBUG, `Views/GalleryView.cs`).

| Komponent | Sekcja | Plik | Status |
| --- | --- | --- | --- |
| Button (Primary/Secondary/Ghost/Danger/DangerGhost/Link) | 4.1.1 | `Styles.Controls.xaml` | Docelowe — wdrożone (1.3) |
| IconButton | 4.1.2 | `Controls/GlyphButton.cs` (`IconButton`), style `IconButton.*` | Docelowe — wdrożone (1.2) |
| SplitButton | 4.1.3 | `Controls/Inputs.cs` | Docelowe — wdrożone (1.3); ButtonGroup niepotrzebny (SplitButton preferowany, §4.1.3) |
| FormRow | 4.2.0 | `Controls/Inputs.cs` | Docelowe — wdrożone (1.3) |
| TextField (Default/Mono/Number) | 4.2.1 | `Controls/Inputs.cs` (`TextField`, `BareEntry`) | Docelowe — wdrożone (1.3) |
| SearchField | 4.2.2 | `Controls/Inputs.cs` | Docelowe — wdrożone (1.3) |
| Select | 4.2.3 | `Controls/Inputs.cs` (menu w `PopoverHost`) | Docelowe — wdrożone (1.3) |
| DateRangePicker | 4.2.4 | `Controls/Inputs.cs` (`DateRangePicker`, `RangeCalendar`) | Docelowe — wdrożone (1.3; dwa miesiące na `bp.wide` i tydzień od `StartOfWeek` — 1.6) (dni bez danych w `text.tertiary` — nie: kalendarz nie zna danych ekranu) |
| SegmentedControl | 4.2.5 | `Controls/Inputs.cs` | Docelowe — wdrożone (1.3) |
| Toggle | 4.2.6 | `Styles.Controls.xaml` | Docelowe — wdrożone (1.3) |
| Checkbox | 4.2.7 | `Styles.Controls.xaml` | Docelowe — wdrożone (1.3) |
| FilterChip | 4.2.8 | `Controls/Inputs.cs` | Docelowe — wdrożone (1.3) |
| CodeEditor | 4.2.9 | `Controls/Editors.cs` | Docelowe — wdrożone (1.3) |
| KeyValueEditor | 4.2.10 | `Controls/Editors.cs` | Docelowe — wdrożone (1.3) |
| Card (Default/Interactive/Accent/Inset) | 4.3.1 | `Controls/Containers.cs` | Docelowe — wdrożone (1.3) |
| StatTile | 4.3.2 | `Controls/Metrics.cs` | Docelowe — wdrożone (1.3) |
| HeroMetric | 4.3.3 | `Controls/Metrics.cs` | Docelowe — wdrożone (1.3) |
| TrendDelta | 4.3.4 | `Controls/Metrics.cs` (`TrendDelta`; w `StatTile` przez `TrendCurrent`/`TrendBaseline`/`TrendLabel`, w `HeroMetric` przez `Yesterday`/`WeekAverage`) | Docelowe — wdrożone (1.7) |
| Section / Collapsible | 4.3.5 | `Controls/Containers.cs` | Docelowe — wdrożone (1.3) |
| Divider | 4.3.6 | `Styles.Surfaces.xaml` (`Divider`, `Divider.Vertical`, `Divider.Edge`) | Docelowe — wdrożone (1.3) |
| KeyValueList | 4.3.7 | `Controls/Containers.cs` | Docelowe — wdrożone (1.3) |
| ProgressBar | 4.4.1 | `Controls/Metrics.cs` (rysowany na płótnie) | Docelowe — wdrożone (1.3) |
| LimitGauge | 4.4.2 | `Controls/Metrics.cs` | Docelowe — wdrożone (1.3) |
| OnionRings | 4.4.3 | `Controls/Charts/Charts.cs` (+ `RingLegend` w `ChartCard.cs`) | Docelowe — wdrożone (1.3) |
| BurnRateBadge | 4.4.4 | `Controls/Badges.cs` | Docelowe — wdrożone (1.3) |
| StatusBadge | 4.4.5 | `Controls/Badges.cs` | Docelowe — wdrożone (1.3) |
| Spinner | 4.4.6 | `Styles.Controls.xaml` | Docelowe — wdrożone (1.3) |
| DataTable | 4.5.1 | `Controls/DataTable.cs` | Docelowe — wdrożone (1.3) |
| ListItem | 4.5.2 | `Controls/Containers.cs` | Docelowe — wdrożone (1.3) |
| SourceBadge | 4.5.3 | `Controls/Badges.cs` | Docelowe — wdrożone (1.3) |
| ModelTag | 4.5.4 | `Controls/Badges.cs` | Docelowe — wdrożone (1.3) |
| PricingMarker | 4.5.5 | `Controls/Badges.cs` | Docelowe — wdrożone (1.3) (`≈`: Wymaga danych) |
| MoneyLabel, TokensLabel | 4.5.6 | `Controls/Metrics.cs` (`MetricLabel` z `Format` = Cost/Tokens/Number/Percent) | Docelowe — wdrożone (1.3) |
| TokenBreakdown | 4.5.7 | `Controls/Metrics.cs` | Docelowe — wdrożone (1.3) |
| Drawer | 4.6.1 | `Controls/Drawer.cs` + `Navigation/Keyboard.cs` (`DrawerService`) | Docelowe — wdrożone (1.3; zmiana szerokości przeciąganiem lewej krawędzi, 360–640, zapamiętywana — 1.6) |
| Dialog | 4.6.2 | `Controls/DialogHost` + `Navigation/Overlays.cs` (`DialogService.ConfirmAsync` / `FormAsync` / `InfoAsync`) | Docelowe — wdrożone (1.2, potwierdzenia; formularze — 12.3; informacyjny — 1.5) |
| Toast | 4.6.3 | `Controls/ToastHost` + `Navigation/Overlays.cs` (`ToastService`) | Docelowe — wdrożone (1.2) |
| Banner | 4.6.4 | `Controls/Containers.cs` | Docelowe — wdrożone (1.3) |
| Tooltip / RichTooltip | 4.6.5 | `ToolTipProperties` (teksty); podpowiedź wykresów rysowana na płótnie (`ChartView.DrawTooltip`) | Docelowe — wdrożone (1.3); osobny popover RichTooltip niepotrzebny |
| Menu | 4.6.6 | `Controls/Popover.cs` (`Menu`, `PopoverService`, `PopoverHost`); menu kontekstowe wierszy — `MenuFlyout` | Docelowe — wdrożone (1.3) |
| CommandPalette | 4.6.7 | `Controls/CommandPalette.cs` + `Navigation/CommandPalette.cs` | Docelowe — wdrożone (1.5) |
| Onboarding | 7.5 | `Views/OnboardingView.cs` | Docelowe — wdrożone (1.5, zakres w §7.5) |
| Sidebar, NavItem | 4.7.1–4.7.2 | `Controls/Sidebar`, `Controls/GlyphButton.cs` (`NavItem`) | Docelowe — wdrożone (1.2; wskaźniki stanu — 1.6: „Źródła” = liczba włączonych źródeł z pominiętymi plikami, „Limity” = kropka przy limicie 5 h / 7 dni ≥ 80% / ≥ 100%; na szynie licznik staje się kropką) |
| PageHeader | 4.7.3 | `Controls/PageHeader` | Docelowe — wdrożone (1.2; dolny obrys po przewinięciu treści — 1.6; nagłówek leży poza obszarem przewijania, więc tło 92% nie jest potrzebne) |
| FilterBar | 4.7.4 | `Controls/Inputs.cs` | Docelowe — wdrożone (1.3; na narrow ≥ 2 FilterChip zwijają się do „Filtry (N)” z popoverem, N = chipy różne od domyślnych — 1.6) |
| StatusBar | 4.7.5 | `Controls/StatusBar` | Docelowe — wdrożone (1.2) |
| PricingNote | 4.7.6 | `Controls/Containers.cs` | Docelowe — wdrożone (1.3) |
| Tabs, SettingsNav | 4.7.7 | `Controls/Inputs.cs` | Docelowe — wdrożone (1.3) |
| Kbd | 4.7.8 | `Controls/Badges.cs` | Docelowe — wdrożone (1.3) |
| EmptyState | 4.7.9 | `Controls/Containers.cs` | Docelowe — wdrożone (1.3) |
| Skeleton | 4.7.10 | `Controls/Containers.cs` | Docelowe — wdrożone (1.3) (shimmer jako pulsowanie tła) |
| ErrorState | 4.7.11 | `Controls/Containers.cs` (`EmptyState` z `IsError` i `ErrorDetails`) | Docelowe — wdrożone (1.3) |
| ChartView (baza), ChartCard, ChartLegend | 4.8.1, 4.8.4 | `Controls/Charts/ChartView.cs`, `Controls/Charts/ChartCard.cs` | Docelowe — wdrożone (1.3; klawiatura — 1.6: wykres jest przystankiem Tab, ←/→/Home/End przesuwają wskaźnik i ogłaszają wartości, Enter = klik) |
| BarChart (+ skumulowany, tryb %), poziomy BarChart (`HorizontalBarChart`), LineChart/AreaChart, Sparkline, DonutChart | 4.8.2 | `Controls/Charts/Charts.cs` | Docelowe — wdrożone (1.3; poziomy — 1.6: wiersz = nazwa · słupek · wartość i udział, bez dymka) |
| Heatmap, HourHeatmap | 4.8.2 F | `Controls/Charts/Charts.cs` | Docelowe — wdrożone (kalendarz 1.3; „Dni × godziny” — `HourHeatmap`, 1.7) |
| BlockTimeline | 4.8.2 H | `Controls/Charts/Charts.cs` | Docelowe — wdrożone (1.3; koszt narastający z wpisów bloku, próbkowany równomiernie — 1.7) |
| SourceCard | 6.1 | `Controls/SourceCard.cs` | Docelowe — wdrożone (1.4) |
| MiniView | 7.2 | `Windows/MiniViewWindow` | Wymaga platformy |

### 11.4 Funkcje wymagające nowych danych

| Funkcja | Potrzebne | Proponowane klucze konfiguracji / API |
| --- | --- | --- |
| Motyw, gęstość, animacje — **dostarczone** (1.1, `OnionConfig.Appearance`) | ustawienia wyglądu | `appearance.theme` (`system`/`light`/`dark`), `appearance.density` (`comfortable`/`compact`), `appearance.motion` (`full`/`reduced`/`off`) |
| Waluta wyświetlania — **dostarczone** (1.7: `OnionConfig.Currency`, `ExchangeRates` NBP, `Fmt.Currency`, `CurrencyService`) | waluta + kurs | `currency.display` (`USD`/`PLN`/`EUR`), `currency.rate`, `currency.rateSource` (`manual`/`nbp`), `currency.rateDate` |
| Budżety — **dostarczone** (1.7: `OnionConfig.Budgets`, `UsageEngine.BuildBudgets`, `BudgetCalculator`) | kwoty | `budgets.dailyUsd`, `budgets.monthlyUsd`, `budgets.perSource.<id>.{dailyUsd,monthlyUsd}` |
| Limit tokenów „Bez limitu” — **dostarczone** (1.7: `tokenLimit: "none"`, `OnionConfig.IsTokenLimitDisabled`) | tryb bez limitu | `tokenLimit` |
| Podgląd statusline — **dostarczone** (1.7: `Core/Text/StatuslineComposer` wspólny dla CLI i Ustawień) | renderer linii statusu | — |
| Powiadomienia | progi, kanały | `notifications.enabled`, `notifications.thresholds.<limit>.{warn,exceed,enabled}`, `notifications.burnHigh`, `notifications.quietHours.{from,to}` |
| Okno, tray, mini-widok | zachowanie okna | `window.alwaysOnTop` (**dostarczone** w 1.1, `OnionConfig.Window`), `window.closeToTray`, `tray.enabled`, `tray.showCost` (macOS), `miniView.modules[]`, `miniView.alwaysOnTop` |
| Trend vs wczoraj / średnia 7 dni — **dostarczone** (1.7: `DashboardSnapshot.DailyCost`, `YesterdayCost`, `Average7DaysCost`) | koszt wczoraj, średnia dzienna | — |
| Porównanie z poprzednim zakresem (Raporty) — **dostarczone** (1.7: drugi raport dla zakresu tej samej długości) | suma poprzedniego zakresu | — |
| Historia limitów — **dostarczone** (1.7: tabela `LimitSnapshots`, `UsageLedger.RecordLimits` / `LimitHistory`, 90 dni) | zapisywanie odczytów limitów | — |
| Prognoza 5 h / 7 d — **dostarczone** (1.7: `LimitForecasting.Project`, regresja liniowa w bieżącym oknie) | z historii odczytów | — |
| Heatmapa dni × godziny — **dostarczone** (1.7: `UsageEngine.BuildHourly`, `HourlyReport`) | agregacja godzinowa | — |
| Trend per projekt — **dostarczone** (1.4, z raportu dziennego grupowanego po projekcie) | — | — |
| Przebieg sesji, sesje w bloku, skumulowany koszt w bloku — **dostarczone** (1.7: `BuildSessionTimeline`, `BuildBlockDetails`, `BuildBlocksDetails`, `TimeSeries.Resample`) | serie czasowe per encja | — |
| Postęp importu historii (onboarding §7.5, `S-IMPORT`) — **dostarczone** (1.7: `IProgress<SyncProgress>` w `UsageLedger.Sync` / `UsageEngine.SyncAsync`) | zdarzenia postępu synchronizacji | — |
| Ostatnia aktywność i liczba wpisów per źródło — **dostarczone** (1.7: `UsageLedger.SourceStatistics`) | statystyki ledgera per źródło | — |
| Flaga aktywności sesji w wierszu — **dostarczone** (1.7: `SessionRow.IsActive`, próg `activeSessionMinutes`) | `IsActive` w wierszu sesji | — |
| Rozbicie pól w VM — **dostarczone** (1.4) | `WeekSummary`, `MonthSummary`, `Details`, `When`, `Extra`, `Diagnostics` jako struktury | refaktor ViewModeli (bez zmian w Core) |

### 11.5 Algorytm wariantów kolorów

Dla koloru marki źródła `c`:

1. **Wariant light:** dopóki kontrast(`c`, `#FFFFFF`) < 3.0 **lub** kontrast(`c`, `#F3F2F6`) < 3.0 — zmniejszaj jasność HSL o 0,01.
2. **Wariant dark:** dopóki kontrast(`c`, `#16151C`) < 3.0 **lub** kontrast(`c`, `#26252E`) < 3.0 — zwiększaj jasność HSL o 0,01.
3. **Tekst inicjału:** wybierz `#FFFFFF` lub `#16151B` — ten z wyższym kontrastem do wariantu.
4. **Awatar:** jeśli kontrast tekstu inicjału < 4.5 — przesuwaj jasność tła awatara (ciemniej dla białego tekstu, jaśniej dla ciemnego) do osiągnięcia 4.5.

Kontrast liczony wg WCAG 2.2 (luminancja względna sRGB). Skrypt referencyjny: `tools/design/palette.py` (bez argumentów wypisuje warianty wszystkich źródeł z Core). Wyniki różnią się od tabeli §2.1.6 o pojedyncze jednostki kanału (zaokrąglenia HSL); wiążąca jest tabela.

### 11.6 Historia zmian dokumentu

| Wersja | Data | Zmiana |
| --- | --- | --- |
| 1.0 | 2026-11-26 | Pierwsza wersja docelowego design systemu. |
| 1.7 | 2026-11-26 | Faza 12.5 (warstwa danych): aktywne sesje, statystyki źródeł, `tokenLimit: none`, postęp importu i `S-IMPORT`, serie czasowe (Dziś: trendy i sparkline, bloki: oś i sesje, sesje: przebieg, Raporty: dni × godziny, udział projektów, porównanie zakresów), budżety (§5.9), waluta wyświetlania (§5.1), historia i prognozy limitów, podgląd statusline; TrendDelta i HourHeatmap wdrożone; §6.10 i §11.4 zaktualizowane; nowe tokeny `ColorHeroTrendWorse` / `ColorHeroTrendBetter`. |
| 1.6 | 2026-11-26 | Faza 12.5 (domknięcie UI): wskaźniki stanu w pasku bocznym, zmiana szerokości drawera, obrys PageHeader po przewinięciu, „Filtry (N)” na narrow, klawiatura wykresów, kalendarz dwumiesięczny, Alt+↑/↓ na kartach źródeł, wiersze-separatory DataTable (przerwy w Blokach), ModelTagi w Raportach, poziomy BarChart „Top projekty”, skrót Ctrl+E w podpowiedzi SplitButton; §6.10 zaktualizowane. |
| 1.5 | 2026-11-26 | Faza 12.4 (powierzchnie globalne): CommandPalette wdrożona (§4.6.7 — stan wdrożenia), Dialog informacyjny, lista skrótów „?”, skróty Ctrl+K, Ctrl+F, /, Ctrl+E, Ctrl+Shift+L, Ctrl+Shift+T (§9.6 — stan wdrożenia), onboarding (§7.5 — stan wdrożenia), animacje M1 (stagger kart), M17 (crossfade zrzutu okna), M18 (szerokość panelu); nowe tokeny (§11.1); §11.4 — postęp importu; §10.3 — testy dymne i autotesty DEBUG. |
| 1.4 | 2026-11-26 | Faza 12.3 (ekrany): wszystkie ekrany §6.1–6.9 przebudowane na komponenty; nowe §6.10 (stan wdrożenia i świadome uproszczenia); SourceCard wdrożony; §11.4 — trend per projekt i rozbicie pól VM dostarczone, nowa pozycja „statystyki ledgera per źródło”. Wzorce runtime: widok nie może trafić do nowego rodzica, będąc w starym; nie przełączać `Shadow` na żywym przycisku WinUI. |
| 1.3 | 2026-11-26 | Faza 12.2 (biblioteka komponentów): wdrożone komponenty §4 poza CommandPalette, TrendDelta i SourceCard (rejestr §11.3 z faktycznymi plikami), popovery (`PopoverService`), Drawer (`DrawerService`), routing klawiatury list (`KeyboardRouter`), wykresy na `ChartView`; Galeria komponentów i autotest (DEBUG) w checkliście §10.3; nowe tokeny (§11.1). |
| 1.2 | 2026-11-26 | Faza 12.1 (struktura okna): host `MainShellPage` zamiast `Shell`, `Navigation/` (`ShellViewModel`, `NavigationService` z historią i M2, `ShortcutService`, `ToastService`, `DialogService`), ekrany w `Views/`; wdrożone Sidebar/NavItem, PageHeader, StatusBar, IconButton, Toast, Dialog; nowe tokeny `ColorHeroHover`, `ImageLogo`, `ScaleDialogEnter`, `PaddingToastHost`, `PaddingSidebarHeader`, styl `Divider.Edge`; etykiety StatusBar „Cennik: wbudowany, …”, „Błąd konfiguracji — Szczegóły”. |
| 1.1 | 2026-11-26 | Faza 12.0 (fundament): generatory `tools/design/gen_tokens.py`, `palette.py`, `gen_logo.py`, `lint.py` i `tools/fonts/build_fonts.py`; nowe tokeny w §11.1 (`ColorTextOnDanger`, `ColorStateSelected`, `ColorHeroDecoration`, `ColorSwitchThumb`, `ColorTransparent`, `BrushHero`, `ColorSourceUnknown`, warianty promieni, `ScalePressed`, wymiary komponentów, `LayoutCardGap`, `LayoutRowPadding`, `SizeChartMain`); `appearance.*` i `window.alwaysOnTop` mają warstwę danych w Core (§11.4). |
