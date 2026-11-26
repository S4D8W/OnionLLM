namespace OnionLLM;

/// <summary>Developer trace (DEBUG builds, <c>ONIONLLM_DIAG_LOG=&lt;file&gt;</c>): start-up milestones and exceptions.</summary>
internal static class Diagnostics
{
    [System.Diagnostics.Conditional("DEBUG")]
    public static void Trace(string message)
    {
        if (Environment.GetEnvironmentVariable("ONIONLLM_DIAG_LOG") is not { Length: > 0 } path)
        {
            return;
        }

        try
        {
            File.AppendAllText(path, $"{DateTime.Now:O} TRACE {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
