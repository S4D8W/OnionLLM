using System.Collections.ObjectModel;
using OnionLLM.Core.Reports;

namespace OnionLLM.ViewModels;

public sealed class BlockItemViewModel
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string Tokens { get; init; } = "";
    public string Cost { get; init; } = "";
    public string Extra { get; init; } = "";
    public bool HasExtra => Extra.Length > 0;
    public string Accent { get; init; } = "#8B909C";
    public double Opacity { get; init; } = 1;
}

public sealed class BlocksViewModel : ObservableObject
{
    private int _rangeIndex = ViewState.GetInt("blocks.range", 0, 2);
    private string _summary = "";
    private bool _isBusy;

    public string[] Ranges { get; } = ["Ostatnie 3 dni", "7 dni", "30 dni"];
    public ObservableCollection<BlockItemViewModel> Blocks { get; } = [];
    public int RangeIndex { get => _rangeIndex; set { if (Set(ref _rangeIndex, value)) { ViewState.Set("blocks.range", value); _ = RefreshAsync(); } } }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await AppState.RefreshDataAsync();
            var since = DateOnly.FromDateTime(DateTime.Today).AddDays(RangeIndex switch { 0 => -2, 1 => -6, _ => -29 });
            DateTimeOffset? resetsAt = AppState.Limits is { } limits && limits.TryGetValue("claude", out var claude) && claude.IsAvailable
                ? claude.FiveHourReset
                : null;
            var now = DateTimeOffset.Now;
            var report = await Task.Run(() => AppState.Engine.BuildBlocks(since, now: now, activeResetsAt: resetsAt));

            Blocks.Clear();
            foreach (var block in report.Blocks.Reverse())
            {
                if (block.IsGap)
                {
                    Blocks.Add(new BlockItemViewModel
                    {
                        Title = "Przerwa",
                        Subtitle = $"{Fmt.LocalTime(block.Start)} – {Fmt.LocalTime(block.End)} ({Fmt.Duration(block.End - block.Start)})",
                        Opacity = 0.55
                    });
                    continue;
                }

                var percent = report.TokenLimit is { } limit and > 0 ? $" · {block.Totals.TotalTokens * 100.0 / limit:0}% limitu" : "";
                var extra = "";
                if (block.IsActive)
                {
                    extra = $"Zostało {Fmt.Duration(block.Remaining(now))} (do {Fmt.LocalTime(block.End, "HH:mm")})";
                    if (block.BurnRate is { } burn)
                    {
                        extra += $"\n🔥 {Fmt.Cost(burn.CostPerHour)}/h · {Fmt.Number((long)burn.TokensPerMinute)} tok/min ({Fmt.BurnLevel(burn.Level)})";
                    }

                    if (block.Projection is { } projection)
                    {
                        var status = BlocksReport.Status(projection.TotalTokens, report.TokenLimit) switch
                        {
                            LimitStatus.Exceeds => " — PRZEKROCZY LIMIT",
                            LimitStatus.Warning => " — blisko limitu",
                            _ => ""
                        };
                        extra += $"\nPrognoza: {Fmt.Tokens(projection.TotalTokens)} · {Fmt.Cost(projection.TotalCostUsd)}{status}";
                    }
                }

                Blocks.Add(new BlockItemViewModel
                {
                    Title = (block.IsActive ? "● AKTYWNY  " : "") + Fmt.LocalTime(block.Start),
                    Subtitle = string.Join(", ", block.Models.Select(Fmt.Model)) + percent,
                    Tokens = Fmt.Tokens(block.Totals.TotalTokens),
                    Cost = Fmt.Cost(block.Totals.CostUsd),
                    Extra = extra,
                    Accent = block.IsActive ? "#23C483" : "#D97757"
                });
            }

            var real = report.Blocks.Where(block => !block.IsGap).ToList();
            Summary = real.Count == 0
                ? "Brak danych Claude Code w tym zakresie."
                : $"{real.Count} bloków · średnio {Fmt.Cost(real.Average(block => block.Totals.CostUsd))} / blok · limit: " +
                  (report.TokenLimit is { } tokenLimit ? Fmt.Tokens(tokenLimit) + (report.TokenLimitIsMax ? " (max)" : "") : "brak");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
