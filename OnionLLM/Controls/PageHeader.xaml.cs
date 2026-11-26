namespace OnionLLM.Controls;

[ContentProperty(nameof(Actions))]
public partial class PageHeader : ContentView
{
    public static readonly BindableProperty TitleProperty = BindableProperty.Create(
        nameof(Title), typeof(string), typeof(PageHeader), string.Empty,
        propertyChanged: (bindable, _, value) => ((PageHeader)bindable).TitleLabel.Text = value as string);

    public static readonly BindableProperty SubtitleProperty = BindableProperty.Create(
        nameof(Subtitle), typeof(string), typeof(PageHeader), string.Empty,
        propertyChanged: (bindable, _, value) =>
        {
            var header = (PageHeader)bindable;
            header.SubtitleLabel.Text = value as string;
            header.SubtitleLabel.IsVisible = header.HasSubtitle;
        });

    public static readonly BindableProperty ActionsProperty = BindableProperty.Create(
        nameof(Actions), typeof(View), typeof(PageHeader), null,
        propertyChanged: (bindable, _, value) => ((PageHeader)bindable).ActionsHost.Content = value as View);

    private readonly HashSet<Element> _watched = [];

    public PageHeader()
    {
        InitializeComponent();
        Loaded += (_, _) => WatchScrolling();
    }

    /// <summary>True while the screen's content is scrolled away from the top (§2.9.3 — bottom border).</summary>
    public bool IsScrolled
    {
        get => ScrollEdge.IsVisible;
        private set => ScrollEdge.IsVisible = value;
    }

    /// <summary>Subscribes to the vertical scroll containers of the screen this header belongs to.</summary>
    private void WatchScrolling()
    {
        Element? screen = this;
        while (screen is not null and not OnionLLM.Navigation.IScreenView)
        {
            screen = screen.Parent;
        }

        if (screen is null)
        {
            return;
        }

        foreach (var scroll in OnionLLM.Navigation.ElementTree.Find<ScrollView>(screen).Where(scroll => scroll.Orientation != ScrollOrientation.Horizontal))
        {
            if (_watched.Add(scroll))
            {
                scroll.Scrolled += (_, e) => IsScrolled = e.ScrollY > 0;
            }
        }

        foreach (var list in OnionLLM.Navigation.ElementTree.Find<CollectionView>(screen))
        {
            if (_watched.Add(list))
            {
                list.Scrolled += (_, e) => IsScrolled = e.VerticalOffset > 0;
            }
        }
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Range or context, e.g. „1–26 lis 2026 · 4 źródła”.</summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    /// <summary>Screen actions placed left of the shared refresh button.</summary>
    public View? Actions
    {
        get => (View?)GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}
