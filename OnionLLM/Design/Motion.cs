namespace OnionLLM.Design;

/// <summary>Reusable choreography from DESIGN.md §2.7.2 built on <see cref="MotionTokens"/> and <see cref="OnionEasing"/>.</summary>
public static class Motion
{
    /// <summary>§2.7.1: stagger applies to at most 8 elements (240 ms).</summary>
    private const int MaxStaggered = 8;

    /// <summary>
    /// M1: elements enter in reading order — fade 0 → 1 + Y 8 → 0, each <c>stagger</c> later (slow, enter). Full motion
    /// only; in reduced/off mode the page fade alone is used. The end state is always reached.
    /// </summary>
    public static Task StaggerInAsync(IEnumerable<VisualElement> elements)
    {
        if (!MotionTokens.AllowsTransforms || MotionTokens.Stagger == 0)
        {
            return Task.CompletedTask;
        }

        var offset = DesignTokens.Double("Space200", 8);
        return Task.WhenAll(elements.Take(MaxStaggered).Select(async (element, index) =>
        {
            element.Opacity = 0;
            element.TranslationY = offset;
            await Task.Delay(TimeSpan.FromMilliseconds(index * MotionTokens.Stagger));
            await Task.WhenAll(
                element.FadeToAsync(1, MotionTokens.Slow, OnionEasing.Enter),
                element.TranslateToAsync(0, 0, MotionTokens.Slow, OnionEasing.Enter));
            element.Opacity = 1;
            element.TranslationY = 0;
        }));
    }

    /// <summary>
    /// M17: crossfade of the whole window on theme change — a snapshot of <paramref name="root"/> is laid over the
    /// window, the theme switches underneath and the snapshot fades out (base). Without a snapshot the theme switches
    /// immediately.
    /// </summary>
    public static async Task CrossfadeAsync(Grid root, Action apply)
    {
        if (MotionTokens.Base == 0 || root.Width <= 0)
        {
            apply();
            return;
        }

        byte[]? pixels = null;
        try
        {
            if (await root.CaptureAsync() is { } shot)
            {
                await using var stream = await shot.OpenReadAsync();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                pixels = memory.ToArray();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or NotImplementedException or System.Runtime.InteropServices.COMException)
        {
            global::OnionLLM.Diagnostics.Trace("M17 snapshot failed: " + exception.Message);
        }

        if (pixels is null)
        {
            apply();
            return;
        }

        var snapshot = new Image
        {
            Source = ImageSource.FromStream(() => new MemoryStream(pixels)),
            Aspect = Aspect.Fill,
            InputTransparent = true,
            ZIndex = int.MaxValue
        };
        Grid.SetRowSpan(snapshot, Math.Max(1, root.RowDefinitions.Count));
        Grid.SetColumnSpan(snapshot, Math.Max(1, root.ColumnDefinitions.Count));
        root.Children.Add(snapshot);
        var loaded = DateTime.UtcNow + TimeSpan.FromMilliseconds(MotionTokens.Base);
        while (snapshot.IsLoading && DateTime.UtcNow < loaded)
        {
            await Task.Delay(MotionTokens.PollInterval);
        }

        await Task.Delay(MotionTokens.PollInterval); // one frame for the image to render before the theme swaps
        apply();
        await snapshot.FadeToAsync(0, MotionTokens.Base, OnionEasing.Standard);
        root.Children.Remove(snapshot);
    }
}
