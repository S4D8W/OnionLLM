using System.Diagnostics;
using System.ComponentModel;

namespace OnionLLM.Cli;

/// <summary>Wypisanie JSON na stdout, opcjonalnie przez <c>jq</c> (ccusage <c>--jq</c> / <c>-q</c>).</summary>
internal static class JsonOutput
{
    public static async Task<int> WriteAsync(string json, string? jqFilter)
    {
        if (jqFilter is null)
        {
            Console.WriteLine(json);
            return 0;
        }

        var start = new ProcessStartInfo("jq")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardInputEncoding = new System.Text.UTF8Encoding(false)
        };
        start.ArgumentList.Add(jqFilter);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception exception)
        {
            throw new ArgumentException($"Nie udało się uruchomić jq ({exception.Message}). Zainstaluj jq i dodaj go do PATH.");
        }

        if (process is null)
        {
            throw new ArgumentException("Nie udało się uruchomić jq.");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync(json);
            process.StandardInput.Close();
            await process.WaitForExitAsync();
            Console.Write(await output);
            var errorText = await error;
            if (errorText.Length > 0)
            {
                Console.Error.Write(errorText);
            }

            return process.ExitCode == 0 ? 0 : 1;
        }
    }
}
