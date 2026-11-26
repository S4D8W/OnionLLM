using OnionLLM.Resources;
using OnionLLM.ViewModels;

namespace OnionLLM.Controls;

/// <summary>Where a card should move (menu, Alt+↑/↓, drag &amp; drop — DESIGN.md §5.11).</summary>
public sealed record SourceCardMove(SourceCardViewModel Card, int Index);

/// <summary>
/// Source card of „Dziś” (§6.1): Accent card with SourceBadge · name · vendor · status · menu; cost and tokens; compact
/// TokenBreakdown; sessions; expandable details (models, credits, limits, 7/30 days); compact form for undetected or
/// disabled sources. Drag the card onto another card to reorder.
/// </summary>
public sealed class SourceCard : Card
{
    private SourceCardViewModel? _card;

    public SourceCard()
    {
        Variant = CardVariant.Accent;
        BindingContextChanged += (_, _) => Build();

        var drag = new DragGestureRecognizer();
        drag.DragStarting += (_, e) =>
        {
            e.Data.Properties["source"] = _card;
            Opacity = Ui.D("OpacityMuted");
        };
        drag.DropCompleted += (_, _) => Opacity = _card?.IsCompact == true ? Ui.D("OpacityMuted") : 1;
        GestureRecognizers.Add(drag);
        var drop = new DropGestureRecognizer();
        drop.Drop += (_, e) =>
        {
            if (e.Data.Properties.TryGetValue("source", out var value) && value is SourceCardViewModel dragged && _card is not null && dragged != _card)
            {
                MoveRequested?.Invoke(this, new SourceCardMove(dragged, IndexOf(_card)));
            }
        };
        GestureRecognizers.Add(drop);
    }

    /// <summary>Raised with the target index of the moved card.</summary>
    public event EventHandler<SourceCardMove>? MoveRequested;

    /// <summary>Opens the configuration of the source („Skonfiguruj”).</summary>
    public event EventHandler<SourceCardViewModel>? ConfigureRequested;

    /// <summary>Resolves the current position of a card (set by the screen).</summary>
    public Func<SourceCardViewModel, int> IndexOf { get; set; } = _ => 0;

    public int Count { get; set; }

    private void Build()
    {
        if (_card is not null)
        {
            _card.PropertyChanged -= OnCardChanged;
        }

        _card = BindingContext as SourceCardViewModel;
        if (_card is null)
        {
            return;
        }

        _card.PropertyChanged += OnCardChanged;
        AccentColorKey = _card.AccentKey;
        AutomationId = _card.AutomationId;
        Opacity = _card.IsCompact ? Ui.D("OpacityMuted") : 1;
        Body = BuildBody(_card);
        SemanticProperties.SetDescription(this,
            $"{_card.Name}, {_card.StatusText}" + (_card.IsCompact ? "" : $", koszt dziś {Fmt.CostSpoken(_card.CostValue)}, {Fmt.TokensSpoken((long)_card.TokensValue)}"));
    }

    private void OnCardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SourceCardViewModel.IsDetailsExpanded) && _card is not null)
        {
            Body = BuildBody(_card);
        }
    }

    private View BuildBody(SourceCardViewModel card)
    {
        var header = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = Ui.D("Space300")
        };
        header.Add(new SourceBadge { SourceId = card.Id, Initial = card.Initial, Size = BadgeSize.Md, State = card.BadgeState });
        var name = Ui.Text("Text.TitleCard", card.Name);
        name.LineBreakMode = LineBreakMode.TailTruncation;
        header.Add(new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children = { name, Ui.Text("Text.Caption", card.Description) } }, 1);
        header.Add(new StatusBadge { Text = card.StatusText, Kind = card.StatusKind }, 2);
        var more = new IconButton { Glyph = Icons.Ellipsis, AutomationId = card.AutomationId + ".More" }.Styled("IconButton.Ghost.Sm");
        ToolTipProperties.SetText(more, "Więcej");
        SemanticProperties.SetDescription(more, "Więcej opcji: " + card.Name);
        more.Clicked += (_, _) => Menu.Show(more,
        [
            new MenuEntry("Przesuń na początek", () => MoveRequested?.Invoke(this, new SourceCardMove(card, 0)), Icons.ArrowUp),
            new MenuEntry("Przesuń w górę", () => MoveRequested?.Invoke(this, new SourceCardMove(card, IndexOf(card) - 1)), Shortcut: "Alt+↑"),
            new MenuEntry("Przesuń w dół", () => MoveRequested?.Invoke(this, new SourceCardMove(card, IndexOf(card) + 1)), Shortcut: "Alt+↓"),
            new MenuEntry("Przesuń na koniec", () => MoveRequested?.Invoke(this, new SourceCardMove(card, Count - 1)), Icons.ArrowDown)
        ], Ui.D("SizeDrawerMin"));
        header.Add(more, 3);

        var stack = new VerticalStackLayout { Spacing = Ui.D("Space300"), Children = { header } };
        if (card.IsCompact)
        {
            var configure = new Button { Text = "Skonfiguruj" }.Styled("Button.Secondary.Sm");
            configure.HorizontalOptions = LayoutOptions.Start;
            configure.Clicked += (_, _) => ConfigureRequested?.Invoke(this, card);
            stack.Children.Add(configure);
            return stack;
        }

        var numbers = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = Ui.D("Space400") };
        numbers.Add(new MetricLabel { Value = card.CostValue, Marker = card.Marker, Size = MetricSize.Md, Format = MetricFormat.Cost }
            .Dyn(Label.TextColorProperty, "ColorDataCost"));
        var tokens = new MetricLabel { Value = card.TokensValue, Size = MetricSize.Sm, Format = MetricFormat.Tokens, VerticalOptions = LayoutOptions.End };
        numbers.Add(new HorizontalStackLayout { Spacing = Ui.D("Space100"), VerticalOptions = LayoutOptions.End, Children = { tokens, Ui.Text("Text.Caption", "tokenów") } }, 1);
        stack.Children.Add(numbers);
        stack.Children.Add(new TokenBreakdown { Input = card.Input, Output = card.Output, CacheWrite = card.CacheWrite, CacheRead = card.CacheRead, IsCompact = true });

        var sessions = new HorizontalStackLayout { Spacing = Ui.D("Space150") };
        sessions.Children.Add(Ui.Dot(card.HasActiveSessions ? "ColorStatusSuccess" : "ColorTextTertiary"));
        sessions.Children.Add(Ui.Text("Text.Caption.Secondary", card.SessionsText));
        stack.Children.Add(sessions);

        var toggle = new Button { Text = card.DetailsToggleText, Command = card.ToggleDetailsCommand }.Styled("Button.Link");
        toggle.AutomationId = card.AutomationId + ".Details";
        stack.Children.Add(toggle);
        if (card.IsDetailsExpanded)
        {
            stack.Children.Add(Details(card));
        }

        return stack;
    }

    private static View Details(SourceCardViewModel card)
    {
        var details = new VerticalStackLayout { Spacing = Ui.D("Space300") };
        if (card.HasModels)
        {
            var models = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
            foreach (var model in card.Models)
            {
                models.Children.Add(new ModelTag { Model = model, Margin = new Thickness(0, 0, Ui.D("Space150"), Ui.D("Space150")) });
            }

            details.Children.Add(models);
        }

        if (card.HasCredits)
        {
            details.Children.Add(Ui.Text("Text.Caption.Secondary", card.CreditsText));
        }

        if (card.ShowLimits)
        {
            details.Children.Add(new LimitGauge { Title = "Limit 5-godzinny", Percent = card.FiveHourPercent, ResetText = card.FiveHourReset, State = card.LimitState, LiveTooltip = "Dane na żywo z API Anthropic" });
            details.Children.Add(new LimitGauge { Title = "Limit 7-dniowy", Percent = card.SevenDayPercent, ResetText = card.SevenDayReset, State = card.LimitState, LiveTooltip = "Dane na żywo z API Anthropic" });
        }

        details.Children.Add(Ui.Text("Text.Caption.Secondary", card.RecentText));
        return details;
    }
}
