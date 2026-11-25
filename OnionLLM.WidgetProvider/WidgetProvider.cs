using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using OnionLLM.Core;
using OnionLLM.Core.Config;
using OnionLLM.Core.Limits;
using OnionLLM.Core.Models;
using OnionLLM.Core.Reports;

namespace OnionLLM.WidgetProvider;

internal sealed class WidgetProvider : IWidgetProvider
{
    private static readonly ConcurrentDictionary<string, WidgetInstance> RunningWidgets = new();
    private static readonly ManualResetEvent EmptyWidgetListEvent = new(false);
    private static readonly object RefreshLock = new();
    private static readonly Timer RefreshTimer = new(_ => RefreshAll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    private static readonly TimeSpan LimitsLifetime = TimeSpan.FromMinutes(5);

    private static UsageEngine? _engine;
    private static IReadOnlyDictionary<string, ProviderLimits>? _limits;
    private static DateTimeOffset _limitsReadAt = DateTimeOffset.MinValue;

    public WidgetProvider()
    {
        foreach (var widget in WidgetManager.GetDefault().GetWidgetInfos())
        {
            RunningWidgets.TryAdd(widget.WidgetContext.Id, new WidgetInstance
            {
                Id = widget.WidgetContext.Id,
                DefinitionId = widget.WidgetContext.DefinitionId,
                Size = widget.WidgetContext.Size
            });
        }
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        var widget = RunningWidgets.GetOrAdd(widgetContext.Id, id => new WidgetInstance
        {
            Id = id,
            DefinitionId = widgetContext.DefinitionId,
            Size = widgetContext.Size
        });
        EmptyWidgetListEvent.Reset();
        UpdateWidget(widget);
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        RunningWidgets.TryRemove(widgetId, out _);
        if (RunningWidgets.IsEmpty)
        {
            EmptyWidgetListEvent.Set();
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        if (actionInvokedArgs.Verb == "refresh" &&
            RunningWidgets.TryGetValue(actionInvokedArgs.WidgetContext.Id, out var widget))
        {
            _limitsReadAt = DateTimeOffset.MinValue;
            UpdateWidget(widget, force: true);
        }
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        if (RunningWidgets.TryGetValue(contextChangedArgs.WidgetContext.Id, out var widget))
        {
            widget.Size = contextChangedArgs.WidgetContext.Size;
            UpdateWidget(widget);
        }
    }

    public void Activate(WidgetContext widgetContext)
    {
        if (RunningWidgets.TryGetValue(widgetContext.Id, out var widget))
        {
            widget.IsActive = true;
            widget.Size = widgetContext.Size;
            UpdateWidget(widget);
        }
    }

    public void Deactivate(string widgetId)
    {
        if (RunningWidgets.TryGetValue(widgetId, out var widget))
        {
            widget.IsActive = false;
        }
    }

    public static ManualResetEvent GetEmptyWidgetListEvent() => EmptyWidgetListEvent;

    private static void RefreshAll()
    {
        foreach (var widget in RunningWidgets.Values.Where(item => item.IsActive))
        {
            UpdateWidget(widget);
        }
    }

    private static void UpdateWidget(WidgetInstance widget, bool force = false)
    {
        lock (RefreshLock)
        {
            var options = new WidgetUpdateRequestOptions(widget.Id)
            {
                Template = Template,
                Data = BuildData(widget.Size, force),
                CustomState = string.Empty,
                IsPlaceholderContent = false
            };
            WidgetManager.GetDefault().UpdateWidget(options);
        }
    }

    private static string BuildData(WidgetSize size, bool force)
    {
        try
        {
            var config = ConfigLoader.LoadOrDefault(out _);
            if (_engine is null)
            {
                _engine = new UsageEngine(config);
            }
            else
            {
                _engine.UpdateConfig(config);
            }

            _engine.LoadPricingAsync().GetAwaiter().GetResult();
            _engine.SyncAsync(force, TimeSpan.FromSeconds(25)).GetAwaiter().GetResult();
            if (DateTimeOffset.UtcNow - _limitsReadAt > LimitsLifetime)
            {
                _limits = _engine.ReadLimitsAsync().GetAwaiter().GetResult();
                _limitsReadAt = DateTimeOffset.UtcNow;
            }

            var dashboard = _engine.BuildDashboard(_limits);
            var maxRows = size == WidgetSize.Large ? 6 : 3;
            var rows = dashboard.Sources
                .Where(source => source.IsEnabled && (source.HasDataToday || source.Limits?.IsAvailable == true))
                .OrderByDescending(source => source.Today.CostUsd)
                .ThenByDescending(source => source.Today.TotalTokens)
                .Take(maxRows)
                .Select(source => new
                {
                    name = source.DisplayName,
                    total = Tokens(source.Today.TotalTokens),
                    cost = Cost(source.Today.CostUsd) + Marker(source.HasUnpricedUsage, source.HasEstimatedModel),
                    detail = $"we {Tokens(source.Today.InputTokens)} · wy {Tokens(source.Today.OutputTokens)} · cache {Tokens(source.Today.CacheReadTokens + source.Today.CacheCreationTokens)}" +
                             (source.Today.Credits != 0 ? $" · kredyty {source.Today.Credits.ToString("#,0.##", CultureInfo.InvariantCulture)}" : ""),
                    sessions = $"{source.ActiveSessions} aktywne · {source.CompletedSessions} zakończone",
                    limits = source.Limits is { IsAvailable: true } limits
                        ? $"5h {limits.FiveHourUtilization:P0} · 7d {limits.SevenDayUtilization:P0}"
                        : ""
                }).ToArray();

            var block = dashboard.ActiveBlock;
            // * — dzisiejszy koszt zawiera modele bez ceny (niedoszacowany), ~ — część cen szacowana (11.7.7).
            var unpriced = dashboard.Sources.Any(source => source.IsEnabled && source.HasUnpricedUsage);
            var estimated = dashboard.Sources.Any(source => source.IsEnabled && source.HasEstimatedModel);
            var notes = new List<string>();
            if (unpriced)
            {
                notes.Add("* część modeli bez ceny");
            }

            if (estimated)
            {
                notes.Add("~ ceny szacowane");
            }

            return JsonSerializer.Serialize(new
            {
                updated = $"Aktualizacja {DateTime.Now:HH:mm}",
                total = Tokens(dashboard.Today.TotalTokens),
                cost = Cost(dashboard.Today.CostUsd) + Marker(unpriced, estimated),
                hasPricingNote = notes.Count > 0,
                pricingNote = string.Join(" · ", notes),
                hasBlock = block is not null,
                block = block is null ? "" : $"Blok Claude: {Cost(block.Totals.CostUsd)} · zostało {Duration(block.Remaining(DateTimeOffset.Now))}",
                burn = block?.BurnRate is { } burn ? $"🔥 {Cost(burn.CostPerHour)}/h ({LevelText(burn.Level)})" : "",
                showRows = rows.Length > 0,
                rows
            });
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(exception.ToString());
            return JsonSerializer.Serialize(new
            {
                updated = "Dane chwilowo niedostępne",
                total = "—",
                cost = "",
                hasPricingNote = false,
                pricingNote = "",
                hasBlock = false,
                block = "",
                burn = "",
                showRows = false,
                rows = Array.Empty<object>()
            });
        }
    }

    private static string Tokens(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("N0", CultureInfo.InvariantCulture)
    };

    private static string Cost(double value) => "$" + value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Marker(bool unpriced, bool estimated) => (unpriced ? "*" : "") + (estimated ? "~" : "");

    private static string Duration(TimeSpan span) => span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m";

    private static string LevelText(BurnRateLevel level) => level switch
    {
        BurnRateLevel.High => "wysokie",
        BurnRateLevel.Moderate => "umiarkowane",
        _ => "normalne"
    };

    private sealed class WidgetInstance
    {
        public required string Id { get; init; }
        public required string DefinitionId { get; init; }
        public WidgetSize Size { get; set; } = WidgetSize.Medium;
        public bool IsActive { get; set; }
    }

    private const string Template = """
    {
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "type": "AdaptiveCard",
      "version": "1.5",
      "body": [
        {
          "type": "ColumnSet",
          "columns": [
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                { "type": "TextBlock", "text": "OnionLLM", "size": "Large", "weight": "Bolder" },
                { "type": "TextBlock", "text": "${updated}", "size": "Small", "isSubtle": true, "spacing": "None" }
              ]
            },
            {
              "type": "Column",
              "width": "auto",
              "items": [
                { "type": "TextBlock", "text": "${cost}", "size": "ExtraLarge", "weight": "Bolder", "horizontalAlignment": "Right" },
                { "type": "TextBlock", "text": "${total} tokenów dziś", "size": "Small", "isSubtle": true, "spacing": "None", "horizontalAlignment": "Right" }
              ]
            }
          ]
        },
        {
          "type": "TextBlock",
          "$when": "${hasPricingNote}",
          "text": "${pricingNote}",
          "size": "Small",
          "color": "Warning",
          "spacing": "None",
          "horizontalAlignment": "Right",
          "wrap": true
        },
        {
          "type": "Container",
          "$when": "${hasBlock}",
          "spacing": "Small",
          "items": [
            { "type": "TextBlock", "text": "${block}", "size": "Small", "weight": "Bolder", "wrap": true },
            { "type": "TextBlock", "text": "${burn}", "size": "Small", "isSubtle": true, "spacing": "None" }
          ]
        },
        {
          "type": "Container",
          "$data": "${rows}",
          "items": [
            {
              "type": "ColumnSet",
              "spacing": "Medium",
              "columns": [
                { "type": "Column", "width": "stretch", "items": [{ "type": "TextBlock", "text": "${name}", "weight": "Bolder" }] },
                { "type": "Column", "width": "auto", "items": [{ "type": "TextBlock", "text": "${total} · ${cost}", "weight": "Bolder", "horizontalAlignment": "Right" }] }
              ]
            },
            { "type": "TextBlock", "text": "${detail}", "size": "Small", "isSubtle": true, "spacing": "None" },
            {
              "type": "ColumnSet",
              "spacing": "None",
              "columns": [
                { "type": "Column", "width": "stretch", "items": [{ "type": "TextBlock", "text": "${sessions}", "size": "Small", "isSubtle": true, "wrap": true }] },
                { "type": "Column", "width": "auto", "items": [{ "type": "TextBlock", "text": "${limits}", "size": "Small", "weight": "Bolder", "horizontalAlignment": "Right" }] }
              ]
            }
          ]
        },
        {
          "type": "TextBlock",
          "$when": "${!showRows}",
          "text": "Brak aktywności dziś",
          "size": "Small",
          "isSubtle": true
        }
      ],
      "actions": [
        { "type": "Action.Execute", "title": "Odśwież", "verb": "refresh" }
      ]
    }
    """;
}
