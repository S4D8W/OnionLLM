using OnionLLM.Core.Pricing;

namespace OnionLLM.Core.Models;

/// <summary>Sumowalne liczniki tokenów i kosztu.</summary>
public sealed class TokenCounts
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    /// <summary>Część <see cref="CacheCreationTokens"/> zapisana z TTL 1 h (ccusage <c>cacheCreation1hTokens</c>).</summary>
    public long CacheCreation1hTokens { get; set; }

    public long CacheReadTokens { get; set; }
    public long ReasoningTokens { get; set; }
    public double CostUsd { get; set; }

    /// <summary>Część <see cref="CostUsd"/> pochodząca z kosztu zapisanego przez narzędzie (ccusage <c>storedCost</c>).</summary>
    public double StoredCostUsd { get; set; }

    /// <summary>Liczba wpisów, których koszt pochodzi (choćby częściowo) z wartości zapisanej przez narzędzie.</summary>
    public int StoredCostEntries { get; set; }

    /// <summary>Kredyty / AIU zapisane przez narzędzie (Amp, Codebuff, Copilot); informacyjnie.</summary>
    public double Credits { get; set; }

    public int Requests { get; set; }

    public long TotalTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;

    /// <summary>Część kosztu wyliczona z cennika (<see cref="CostUsd"/> − <see cref="StoredCostUsd"/>).</summary>
    public double CalculatedCostUsd => CostUsd - StoredCostUsd;

    public bool HasStoredCost => StoredCostEntries > 0;

    public void Add(UsageEntry entry, double cost)
    {
        InputTokens += entry.InputTokens;
        OutputTokens += entry.OutputTokens;
        CacheCreationTokens += entry.CacheCreationTokens;
        CacheCreation1hTokens += Math.Min(entry.CacheCreation1hTokens, entry.CacheCreationTokens);
        CacheReadTokens += entry.CacheReadTokens;
        ReasoningTokens += entry.ReasoningTokens;
        Credits += entry.Credits ?? 0;
        CostUsd += cost;
        Requests++;
    }

    /// <summary>Dodaje wpis wraz z rozbiciem kosztu na zapisany i wyliczony.</summary>
    public void Add(UsageEntry entry, CostResult cost)
    {
        Add(entry, cost.CostUsd);
        if (cost.StoredCostUsd != 0)
        {
            StoredCostUsd += cost.StoredCostUsd;
            StoredCostEntries++;
        }
    }

    public void Add(TokenCounts other)
    {
        InputTokens += other.InputTokens;
        OutputTokens += other.OutputTokens;
        CacheCreationTokens += other.CacheCreationTokens;
        CacheCreation1hTokens += other.CacheCreation1hTokens;
        CacheReadTokens += other.CacheReadTokens;
        ReasoningTokens += other.ReasoningTokens;
        CostUsd += other.CostUsd;
        StoredCostUsd += other.StoredCostUsd;
        StoredCostEntries += other.StoredCostEntries;
        Credits += other.Credits;
        Requests += other.Requests;
    }
}
