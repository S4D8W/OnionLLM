using OnionLLM.Controls;

namespace OnionLLM.Navigation;

/// <summary>
/// „Skróty klawiszowe” dialog (DESIGN.md §9.6, „?”): the implemented shortcuts, grouped by scope. Platform modifiers
/// come from <see cref="ShortcutService.Label"/> (Ctrl on Windows, ⌘ on macOS).
/// </summary>
public static class ShortcutHelp
{
    public static IReadOnlyList<(string Group, IReadOnlyList<(string[] Keys, string Action)> Items)> Catalog { get; } =
    [
        ("Globalne",
        [
            ([ShortcutService.Label("K")], "Paleta poleceń"),
            ([ShortcutService.Label("1"), ShortcutService.Label("8")], "Dziś … Źródła"),
            ([ShortcutService.SettingsShortcutLabel], "Ustawienia"),
            (["F5", ShortcutService.Label("R")], "Odśwież dane"),
            ([ShortcutService.Label("B")], "Zwiń / rozwiń panel boczny"),
            ([ShortcutService.Label("Shift+T")], "Zawsze na wierzchu"),
            ([ShortcutService.Label("Shift+L")], "Przełącz motyw"),
            ([ShortcutService.BackLabel, ShortcutService.ForwardLabel], "Wstecz / Dalej"),
            (["?"], "Lista skrótów")
        ]),
        ("Ekran",
        [
            (["/", ShortcutService.Label("F")], "Szukaj na ekranie"),
            ([ShortcutService.Label("E")], "Eksportuj"),
            (["Esc"], "Zamknij panel, dialog lub paletę; wyczyść wyszukiwanie")
        ]),
        ("Tabele i panel szczegółów",
        [
            (["↑", "↓"], "Poprzedni / następny wiersz"),
            (["J", "K"], "Następny / poprzedni element"),
            (["Enter"], "Otwórz szczegóły"),
            (["→", "←"], "Rozwiń / zwiń wiersz"),
            ([ShortcutService.Label("C")], "Kopiuj wiersz")
        ])
    ];

    public static Task ShowAsync() => DialogService.InfoAsync("Skróty klawiszowe", Build());

    private static View Build()
    {
        var stack = new VerticalStackLayout { Spacing = Ui.D("Space400") };
        foreach (var (group, items) in Catalog)
        {
            var section = new VerticalStackLayout { Spacing = Ui.D("Space200") };
            section.Children.Add(Ui.Text("Text.Overline", group.ToUpperInvariant()));
            foreach (var (keys, action) in items)
            {
                var row = new Grid
                {
                    ColumnDefinitions = [new(new GridLength(Ui.D("SizeFormLabel") / 2)), new(GridLength.Star)],
                    ColumnSpacing = Ui.D("Space300")
                };
                var chips = new HorizontalStackLayout { Spacing = Ui.D("Space100"), VerticalOptions = LayoutOptions.Center };
                foreach (var key in keys)
                {
                    chips.Children.Add(new Kbd { Text = key });
                }

                var label = Ui.Text("Text.Body", action);
                label.VerticalOptions = LayoutOptions.Center;
                row.Add(chips);
                row.Add(label, 1);
                SemanticProperties.SetDescription(row, $"{string.Join(" lub ", keys)}: {action}");
                section.Children.Add(row);
            }

            stack.Children.Add(section);
        }

        return stack;
    }
}
