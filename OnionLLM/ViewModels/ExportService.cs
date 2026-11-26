using OnionLLM.Navigation;

namespace OnionLLM.ViewModels;

/// <summary>
/// Export (DESIGN.md §5.6): files go to <c>Dokumenty/OnionLLM/</c> as <c>onionllm-&lt;ekran&gt;-&lt;okres&gt;-&lt;rrrrMMdd-HHmm&gt;.&lt;ext&gt;</c>;
/// the result is a toast with „Otwórz folder”. „Kopiuj jako tabelę” puts TSV on the clipboard.
/// </summary>
public static class ExportService
{
    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OnionLLM");

    public static async Task SaveAsync(string screen, string period, string extension, string content, int rows)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, $"onionllm-{screen}-{period}-{DateTime.Now:yyyyMMdd-HHmm}.{extension}");
            await File.WriteAllTextAsync(path, content);
            ToastService.Show($"Wyeksportowano {Fmt.Count(rows, "wiersz", "wiersze", "wierszy")}", ToastKind.Success, "Otwórz folder",
                () => _ = Launcher.Default.OpenAsync(new Uri(Directory)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ToastService.Show($"Nie udało się zapisać pliku. Sprawdź uprawnienia do folderu {Directory}.", ToastKind.Error);
        }
    }

    public static async Task CopyTableAsync(IEnumerable<IEnumerable<string>> rows)
    {
        await Clipboard.Default.SetTextAsync(string.Join(Environment.NewLine, rows.Select(row => string.Join('\t', row))));
        ToastService.Show("Skopiowano tabelę", ToastKind.Success);
    }
}
