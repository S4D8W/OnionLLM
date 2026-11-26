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

    public PageHeader()
    {
        InitializeComponent();
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
