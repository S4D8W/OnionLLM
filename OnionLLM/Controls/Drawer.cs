using OnionLLM.Design;
using OnionLLM.Navigation;
using OnionLLM.Resources;

namespace OnionLLM.Controls;

/// <summary>
/// Detail drawer host (DESIGN.md §4.6.1): sticky header (overline · title · subtitle · previous/next/more/close), scrolling
/// body, optional footer. Push on wide windows (the window host gives it a column), overlay with scrim elsewhere.
/// Slides in/out (M6); replacing the content while open crossfades.
/// </summary>
public sealed class Drawer : Grid
{
    private readonly Label _overline = Ui.Text("Text.Overline");
    private readonly Label _title = Ui.Text("Text.TitleSection");
    private readonly Label _subtitle = Ui.Text("Text.Caption");
    private readonly IconButton _previous = Button(Icons.ChevronUp, "Poprzedni (K)", "Drawer.Previous");
    private readonly IconButton _next = Button(Icons.ChevronDown, "Następny (J)", "Drawer.Next");
    private readonly IconButton _more = Button(Icons.Ellipsis, "Więcej", "Drawer.More");
    private readonly IconButton _close = Button(Icons.X, "Zamknij (Esc)", "Drawer.Close");
    private readonly ContentView _body = new();
    private readonly ContentView _footer = new();
    private readonly Border _panel = new();
    private DrawerRequest? _shown;

    public Drawer()
    {
        IsVisible = false;
        HorizontalOptions = LayoutOptions.End;
        WidthRequest = ShellViewModel.Instance.DrawerWidth;

        _title.MaxLines = 1;
        _title.LineBreakMode = LineBreakMode.TailTruncation;
        SemanticProperties.SetHeadingLevel(_title, SemanticHeadingLevel.Level2);
        var actions = new HorizontalStackLayout { Spacing = Ui.D("Space050"), Children = { _previous, _next, _more, _close } };
        var header = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = Ui.D("Space200")
        }.Dyn(PaddingProperty, "LayoutCardPaddingLg");
        header.Add(new VerticalStackLayout { Spacing = Ui.D("Space050"), Children = { _overline, _title, _subtitle } });
        header.Add(actions, 1);

        var scroll = new ScrollView { Content = _body };
        _body.Dyn(PaddingProperty, "LayoutCardPaddingLg");
        _footer.Dyn(PaddingProperty, "LayoutCardPaddingLg");

        var layout = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)] };
        layout.Add(header, 0, 0);
        layout.Add(new BoxView().Styled("Divider"), 0, 1);
        layout.Add(scroll, 0, 2);
        layout.Add(new BoxView().Styled("Divider"), 0, 3);
        layout.Add(_footer, 0, 4);
        _panel.StrokeThickness = 0;
        _panel.Content = new Grid { Children = { layout, new BoxView { HorizontalOptions = LayoutOptions.Start }.Styled("Divider.Edge") } };
        Children.Add(_panel);
        Children.Add(ResizeGrip());

        _previous.Clicked += (_, _) => _shown?.PreviousCommand?.Execute(null);
        _next.Clicked += (_, _) => _shown?.NextCommand?.Execute(null);
        _more.Clicked += (_, _) => { if (_shown is { MoreEntries.Count: > 0 } request) { Menu.Show(_more, request.MoreEntries, Ui.D("SizeSelectMinWidth")); } };
        _close.Clicked += (_, _) => DrawerService.Close();

        DrawerService.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await UpdateAsync());
        ShellViewModel.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Layout))
            {
                UpdateMode();
            }
            else if (e.PropertyName == nameof(ShellViewModel.DrawerWidth))
            {
                WidthRequest = ShellViewModel.Instance.DrawerWidth;
            }
        };
        UpdateMode();
    }

    /// <summary>Left-edge grip (§4.6.1): drag to resize 360–640; the width is remembered. Hover highlights the edge.</summary>
    private View ResizeGrip()
    {
        var line = new BoxView { HorizontalOptions = LayoutOptions.Start, InputTransparent = true }.Styled("Divider.Edge");
        line.Opacity = 0;
        var grip = new Grid
        {
            WidthRequest = Ui.D("Space200"),
            HorizontalOptions = LayoutOptions.Start,
            BackgroundColor = Ui.Res<Color>("ColorTransparent"),
            Children = { line },
            AutomationId = "Drawer.Resize"
        };
        line.Dyn(BoxView.ColorProperty, "ColorBorderFocus");
        ToolTipProperties.SetText(grip, "Przeciągnij, aby zmienić szerokość");
        SemanticProperties.SetDescription(grip, "Zmień szerokość panelu szczegółów");
        var hover = new PointerGestureRecognizer();
        hover.PointerEntered += (_, _) => line.Opacity = 1;
        hover.PointerExited += (_, _) => line.Opacity = 0;
        grip.GestureRecognizers.Add(hover);

        var start = 0d;
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (_, e) =>
        {
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    start = Width;
                    line.Opacity = 1;
                    break;
                case GestureStatus.Running:
                    ShellViewModel.Instance.ResizeDrawer(start - e.TotalX, save: false);
                    break;
                default:
                    ShellViewModel.Instance.ResizeDrawer(ShellViewModel.Instance.DrawerWidth, save: true);
                    line.Opacity = 0;
                    break;
            }
        };
        grip.GestureRecognizers.Add(pan);
        return grip;
    }

    private static IconButton Button(string glyph, string tooltip, string automationId)
    {
        var button = new IconButton { Glyph = glyph, AutomationId = automationId }.Styled("IconButton.Ghost.Sm");
        ToolTipProperties.SetText(button, tooltip);
        SemanticProperties.SetDescription(button, tooltip);
        return button;
    }

    private void UpdateMode()
    {
        var overlay = ShellViewModel.Instance.Layout != LayoutState.Wide;
        _panel.Dyn(BackgroundColorProperty, overlay ? "ColorSurfaceOverlay" : "ColorSurface1");
        _panel.Dyn(Border.ShadowProperty, overlay ? "ShadowE3" : "ShadowE1");
    }

    private async Task UpdateAsync()
    {
        var request = DrawerService.Current;
        if (request is null)
        {
            if (IsVisible)
            {
                // M6 close: slide out (exit).
                await this.TranslateToAsync(MotionTokens.AllowsTransforms ? Width : 0, 0, MotionTokens.Slow, OnionEasing.Exit);
                if (DrawerService.Current is null)
                {
                    IsVisible = false;
                    _body.Content = null;
                    _shown = null;
                }
            }

            return;
        }

        var wasOpen = IsVisible && _shown is not null;
        if (wasOpen && MotionTokens.Fast > 0)
        {
            await _panel.FadeToAsync(0, MotionTokens.Fast, OnionEasing.Exit);
        }

        Apply(request);
        if (!wasOpen)
        {
            IsVisible = true;
            TranslationX = MotionTokens.AllowsTransforms ? WidthRequest : 0;
            _panel.Opacity = 1;
            await this.TranslateToAsync(0, 0, MotionTokens.Slow, OnionEasing.Enter);
        }
        else
        {
            await _panel.FadeToAsync(1, MotionTokens.Fast, OnionEasing.Enter);
        }

        SemanticScreenReader.Announce($"{request.Overline}: {request.Title}");
    }

    private void Apply(DrawerRequest request)
    {
        _shown = request;
        _overline.Text = request.Overline;
        _title.Text = request.Title;
        ToolTipProperties.SetText(_title, request.Title);
        _subtitle.Text = request.Subtitle;
        _subtitle.IsVisible = !string.IsNullOrEmpty(request.Subtitle);
        _previous.IsVisible = request.PreviousCommand is not null;
        _next.IsVisible = request.NextCommand is not null;
        _more.IsVisible = request.MoreEntries.Count > 0;
        _body.Content = request.Content;
        _footer.Content = request.Footer;
        _footer.IsVisible = request.Footer is not null;
        SemanticProperties.SetDescription(this, $"Panel szczegółów: {request.Title}");
    }
}
