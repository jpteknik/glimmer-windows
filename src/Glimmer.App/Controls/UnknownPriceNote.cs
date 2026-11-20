// Ported from App/UnknownPriceNote.swift (UnknownPriceNote, UnknownPriceTick, NeedsUnknownPriceTick). The line every
// paid plan and sheet carries while the picture service states no price in advance, and under it, until it has been
// made, the once-per-service tick. The tick is kept in Settings › AI, where it can be taken back.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Glimmer.AI;
using Glimmer.App.Localization;
using Glimmer.Core;

namespace Glimmer.App.Controls;

/// <summary>The AI settings keys the views share with the editor's AIController (same names as the Mac's).</summary>
public static class AIPreferences
{
    public const string TextProviderKey = "ai.textProvider";
    public const string ImageProviderKey = "ai.imageProvider";
    public const string AutoRunKey = "ai.autoRunPlans";
    public const string OnDeviceRoutingKey = "ai.routeOnDevice";
    public const string ShowImageKey = "ai.assistantSeesImage";
    public const string ReviewKey = "ai.reviewResults";
    public const string UpscaleNeuralKey = "ai.upscaleNeural";

    public static string UnknownPriceKey(string slug) => $"ai.unknownPriceAgreed.{slug}";
    /// <summary>The text service's own tick (AIController.textUnknownPriceKey).</summary>
    public static string TextUnknownPriceKey(string slug) => $"ai.unknownPriceAgreed.text.{slug}";
    /// <summary>Set once what is sent to a picture service has been shown and agreed to.</summary>
    public static string ImageToldKey(string slug) => $"ai.imageNoticeShown.{slug}";
    public const string EndpointKey = "ai.endpoint";
    /// <summary>OpenRouter's models (AIDefaults.imageModelKey / textModelKey); other services have their own keys.</summary>
    public const string ImageModelKey = "ai.imageModel";
    public const string TextModelKey = "ai.textModel";
    public static string TextModelKeyFor(string slug) => slug == "openrouter" ? TextModelKey : $"ai.textModel.{slug}";
    public static string ImageModelKeyFor(string slug) => $"ai.imageModel.{slug}";
    public static string ImageEditModelKeyFor(string slug) => $"ai.imageEditModel.{slug}";

    /// <summary>The picture service in use: the chosen one when it offers pictures, else OpenRouter.</summary>
    public static AIProvider ImageProvider
    {
        get
        {
            var slug = AppServices.Settings.Get(ImageProviderKey, "openrouter");
            return AIProvider.All.FirstOrDefault(p => p.Slug == slug && p.Offers(AIProvider.Capability.Image))
                   ?? AIProvider.All.First(p => p.Slug == "openrouter");
        }
    }

    /// <summary>Whether a paid picture job may start: the price is known, or the person ticked that it isn't.</summary>
    public static bool PriceAccepted =>
        PaidPrice.KnownInAdvance(ImageProvider.Slug) || AppServices.Settings.Get(UnknownPriceKey(ImageProvider.Slug), false);
}

public sealed class UnknownPriceNote : StackPanel
{
    public UnknownPriceNote()
    {
        Margin = new Thickness(0, 0, 0, 8);
        Rebuild();
        Loaded += (_, _) => AppServices.Settings.Changed += OnSettings;
        Unloaded += (_, _) => AppServices.Settings.Changed -= OnSettings;
    }

    private void OnSettings(object? sender, string key)
    {
        if (key == AIPreferences.ImageProviderKey) Dispatcher.BeginInvoke(Rebuild);
    }

    private void Rebuild()
    {
        Children.Clear();
        var provider = AIPreferences.ImageProvider;
        Visibility = PaidPrice.KnownInAdvance(provider.Slug) ? Visibility.Collapsed : Visibility.Visible;
        Children.Add(new StatusLine { Kind = StatusKind.Notice, TextKey = "Price not known in advance", FontSize = 11 });
        // Made here, it stays in sight (ticked) until the view goes: a tick that vanishes when clicked looks like a fault.
        if (!AppServices.Settings.Get(AIPreferences.UnknownPriceKey(provider.Slug), false)) Children.Add(Tick(provider, small: true));
    }

    /// <summary>"&lt;service&gt; does not state a price in advance; I know": one tick per service (UnknownPriceTick).</summary>
    public static CheckBox Tick(AIProvider provider, bool small = false, string? key = null)
    {
        key ??= AIPreferences.UnknownPriceKey(provider.Slug);
        var box = new CheckBox
        {
            Content = new TextBlock { Text = Loc.F("{0} does not state a price in advance; I know", provider.Name), TextWrapping = TextWrapping.Wrap },
            IsChecked = AppServices.Settings.Get(key, false), Style = (Style)Application.Current.FindResource("Glimmer.Compact.CheckBox"),
            FontSize = small ? 11 : 13, Margin = new Thickness(0, 4, 0, 0),
        };
        AutomationProperties.SetName(box, Loc.F("{0} does not state a price in advance; I know", provider.Name));
        var k = key;
        box.Click += (_, _) => AppServices.Settings.Set(k, box.IsChecked == true);
        return box;
    }
}

/// <summary>NeedsUnknownPriceTick: greys a button that starts a paid picture job until the tick for the chosen service is
/// made (or the price is known).</summary>
public static class PriceGate
{
    public static T Gate<T>(T button, Func<bool>? applies = null) where T : ButtonBase
    {
        void Update() => button.IsEnabled = !(applies?.Invoke() ?? true) || AIPreferences.PriceAccepted;
        void OnSettings(object? sender, string key)
        {
            if (key == AIPreferences.ImageProviderKey || key.StartsWith("ai.unknownPriceAgreed.", StringComparison.Ordinal)) button.Dispatcher.BeginInvoke(Update);
        }
        button.Loaded += (_, _) => { AppServices.Settings.Changed += OnSettings; Update(); };
        button.Unloaded += (_, _) => AppServices.Settings.Changed -= OnSettings;
        Update();
        return button;
    }
}
