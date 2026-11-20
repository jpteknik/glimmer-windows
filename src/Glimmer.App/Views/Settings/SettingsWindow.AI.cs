// Ported from App/SettingsView.swift (AISettingsView, ProviderTextSettings, OpenRouterTextPrice, ProviderImageSettings,
// LocalTextSettings). What the AI library knows (keys, model lists, notices, prices) comes through IAISettingsService;
// the choices themselves are app settings under the Mac's keys. A key is held only in its PasswordBox and handed to the
// service when saved or tested; it is never shown, logged or kept anywhere else.
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Glimmer.AI;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.App.Views.Sheets;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Settings;

public sealed partial class SettingsWindow
{
    private FormPanel AI()
    {
        var s = AppServices.Settings;
        var form = new FormPanel();
        if (AppServices.AISettings is not { } ai) return form;

        // ---------------------------------------------------------------------------------------- OpenRouter's key
        var openRouter = form.Section(new Verbatim("OpenRouter"));
        KeyRows(openRouter, ai, "openrouter", "API key", testable: true);
        form.Children.Add(Footer(WithLinks(Loc.T("Glimmer calls OpenRouter directly with your own key. It is stored in your keychain and only sent to the service below (openrouter.ai unless you change it). [Get a key](https://openrouter.ai/keys)"))));

        // ---------------------------------------------------------------------------------------- endpoint
        var service = form.Section("Service", "Any service with OpenRouter's API (chat completions with image output) works. Leave empty for OpenRouter. Your key is sent only to this address.");
        var endpoint = new TextBox { Text = s.Get(AIPreferences.EndpointKey, ""), Width = 280, Style = Compact("TextBox"), Tag = ai.DefaultEndpoint };
        var invalid = new StatusLine { Kind = StatusKind.Warning, TextKey = "Not a valid http(s) address; Glimmer uses OpenRouter." };
        void CheckEndpoint() =>
            invalid.Visibility = endpoint.Text.Trim().Length > 0 && !ai.IsUsableEndpoint(endpoint.Text) ? Visibility.Visible : Visibility.Collapsed;
        endpoint.TextChanged += (_, _) => { s.Set(AIPreferences.EndpointKey, endpoint.Text.Trim()); CheckEndpoint(); };
        service.Row("Endpoint", endpoint);
        service.Add(invalid);
        CheckEndpoint();

        // ---------------------------------------------------------------------------------------- text
        var text = form.Section("Text (the Assistant)");
        var textBody = new StackPanel();
        text.Add(textBody);
        Dictionary<string, IReadOnlyList<LocalModel>> localModels = [];
        async void FindLocalServers()
        {
            foreach (var p in AIProvider.Offering(AIProvider.Capability.Text).Where(p => p.Kind == AIProvider.KindOf.LocalServer))
            {
                if (await ai.LocalModelsAsync(p, CancellationToken.None) is { } models) localModels[p.Slug] = models;
                else localModels.Remove(p.Slug);
            }
            BuildText();
        }
        void BuildText()
        {
            textBody.Children.Clear();
            var chosen = s.Get(AIPreferences.TextProviderKey, "openrouter");
            // A local server is listed only while it is running on this computer (or when it is the one chosen).
            var choices = AIProvider.Offering(AIProvider.Capability.Text)
                .Where(p => p.Kind == AIProvider.KindOf.Cloud || localModels.ContainsKey(p.Slug) || p.Slug == chosen)
                .Select(p => (p.Slug, p.Kind == AIProvider.KindOf.LocalServer ? Loc.F("{0} (on this Mac)", p.Name) : p.Name));
            var picker = Picker(choices, chosen, v => { s.Set(AIPreferences.TextProviderKey, v); BuildText(); }, "Service");
            textBody.Children.Add(new FormRow(Loc.T("Service"), picker, null) { Margin = new Thickness(0, 0, 0, 8) });
            if (AIProvider.All.FirstOrDefault(p => p.Slug == chosen && p.Offers(AIProvider.Capability.Text)) is not { } provider) return;
            if (provider.Slug == "ollama")
                textBody.Children.Add(LocalTextSettings(ai, provider, localModels.GetValueOrDefault(provider.Slug), FindLocalServers));
            else if (provider.Slug != "openrouter")
                textBody.Children.Add(ProviderTextSettings(ai, provider));
            // Asked once per service when its list states no price for the chosen model; kept here, taken back here.
            if (provider.Kind == AIProvider.KindOf.Cloud && ai.ListedTextPrice(provider) is null)
            {
                textBody.Children.Add(UnknownPriceNote.Tick(provider, key: AIPreferences.TextUnknownPriceKey(provider.Slug)));
                textBody.Children.Add(Caption(Loc.T("Glimmer has read no price for the chosen model from this service's list, so it cannot say what a request to the Assistant costs. Until this is ticked, the first request asks.")));
            }
            textBody.Children.Add(WhatIsSent(provider, ai.TextNotice(provider)));
        }
        BuildText();
        FindLocalServers();
        text.FooterText = Loc.T(s.Get(AIPreferences.ImageProviderKey, "openrouter") == "openrouter"
            ? "Which service answers when the Assistant plans, suggests edits or looks for distractions. Pictures are still made through OpenRouter."
            : "Which service answers when the Assistant plans, suggests edits or looks for distractions.");

        // ---------------------------------------------------------------------------------------- pictures
        var imageChoices = AIProvider.Offering(AIProvider.Capability.Image);
        if (imageChoices.Count > 1)
        {
            var pictures = form.Section("Pictures", "Which service makes and edits pictures: Generate, Edit with Prompt, Generative Fill, Expand and the others.");
            var picturesBody = new StackPanel();
            pictures.Add(picturesBody);
            void BuildPictures()
            {
                picturesBody.Children.Clear();
                var chosen = s.Get(AIPreferences.ImageProviderKey, "openrouter");
                var picker = Picker(imageChoices.Select(p => (p.Slug, p.Name)), chosen, slug =>
                {
                    // OpenRouter is chosen at once; another service only after what is sent to it has been shown and agreed to.
                    var p = imageChoices.First(c => c.Slug == slug);
                    if (slug != "openrouter" && !s.Get(AIPreferences.ImageToldKey(slug), false))
                    {
                        int answer = AlertWindow.Show(this, Loc.F("Send pictures to {0}?", p.Name), ai.ImageNotice(p),
                                                      [(Loc.F("Use {0}", p.Name), AlertRole.Default), ("Cancel", AlertRole.Cancel)]);
                        if (answer != 0) { BuildPictures(); return; }
                        s.Set(AIPreferences.ImageToldKey(slug), true);
                    }
                    s.Set(AIPreferences.ImageProviderKey, slug);
                    BuildPictures();
                    text.FooterText = Loc.T(slug == "openrouter"
                        ? "Which service answers when the Assistant plans, suggests edits or looks for distractions. Pictures are still made through OpenRouter."
                        : "Which service answers when the Assistant plans, suggests edits or looks for distractions.");
                }, "Service");
                picturesBody.Children.Add(new FormRow(Loc.T("Service"), picker, null) { Margin = new Thickness(0, 0, 0, 8) });
                if (chosen != "openrouter" && imageChoices.FirstOrDefault(p => p.Slug == chosen) is { } provider)
                {
                    picturesBody.Children.Add(ProviderImageSettings(ai, provider));
                    picturesBody.Children.Add(WhatIsSent(provider, ai.ImageNotice(provider)));
                }
                // Asked once per service, kept here, taken back here.
                if (!PaidPrice.KnownInAdvance(AIPreferences.ImageProvider.Slug))
                {
                    picturesBody.Children.Add(UnknownPriceNote.Tick(AIPreferences.ImageProvider));
                    picturesBody.Children.Add(Caption(Loc.T("Glimmer cannot say what a picture costs before it is made. Until this is ticked, the buttons that make pictures stay gray. Untick it to be asked again.")));
                }
            }
            BuildPictures();
        }

        // ---------------------------------------------------------------------------------------- models
        var models = form.Section("Models");
        var imageModel = new TextBox { Text = s.Get(AIPreferences.ImageModelKey, ai.DefaultImageModel), Width = 260, Style = Compact("TextBox") };
        var textModel = new TextBox { Text = s.Get(AIPreferences.TextModelKey, ai.DefaultTextModel), Width = 260, Style = Compact("TextBox") };
        bool textIsOpenRouter = s.Get(AIPreferences.TextProviderKey, "openrouter") == "openrouter";
        textModel.IsEnabled = textIsOpenRouter;
        if (!textIsOpenRouter) textModel.ToolTip = Loc.T("Used when the text service is OpenRouter");
        var imageRetired = new StatusLine { Kind = StatusKind.Warning };
        var textRetired = new StatusLine { Kind = StatusKind.Warning };
        var price = new StackPanel();
        var restore = SmallButton("Restore Defaults", () => { imageModel.Text = ai.DefaultImageModel; textModel.Text = ai.DefaultTextModel; });
        restore.HorizontalAlignment = HorizontalAlignment.Left;
        CancellationTokenSource? reading = null;
        void Update()
        {
            s.Set(AIPreferences.ImageModelKey, imageModel.Text.Trim());
            s.Set(AIPreferences.TextModelKey, textModel.Text.Trim());
            imageRetired.TextKey = ai.Retirement(imageModel.Text.Trim());
            imageRetired.Visibility = imageRetired.TextKey is null ? Visibility.Collapsed : Visibility.Visible;
            textRetired.TextKey = textIsOpenRouter ? ai.Retirement(textModel.Text.Trim()) : null;
            textRetired.Visibility = textRetired.TextKey is null ? Visibility.Collapsed : Visibility.Visible;
            restore.IsEnabled = imageModel.Text != ai.DefaultImageModel || textModel.Text != ai.DefaultTextModel;
        }
        // What OpenRouter's own list says the typed text model costs, read a moment after typing stops (OpenRouterTextPrice).
        async void ReadPrice()
        {
            if (!textIsOpenRouter) return;
            reading?.Cancel();
            var token = (reading = new CancellationTokenSource()).Token;
            price.Children.Clear();
            price.Children.Add(Caption(Loc.T("Reading OpenRouter's list…")));
            try
            {
                await Task.Delay(700, token);
                var id = textModel.Text.Trim();
                var openRouterProvider = AIProvider.All.First(p => p.Slug == "openrouter");
                var list = await ai.TextModelsAsync(openRouterProvider, null, token);
                var entry = list.FirstOrDefault(m => m.Id == id);
                ai.RememberTextPrice("openrouter", id, ai.PriceText(entry));
                price.Children.Clear();
                if (ai.PriceText(entry) is { } line && ai.ListedTextPrice(openRouterProvider) is { } listed)
                {
                    price.Children.Add(Caption(Loc.F("{0}'s list: {1}. {2} bills you for what you use.", openRouterProvider.Name, line, openRouterProvider.Name)));
                    price.Children.Add(Caption(Loc.F("Read {0}.", listed.Read.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))));
                }
                else
                {
                    price.Children.Add(Caption(Loc.T(entry is null ? "OpenRouter's list has no model with this id, so its price is not known." : "OpenRouter's list states no price for this model.")));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                price.Children.Clear();
                price.Children.Add(new StatusLine { Kind = StatusKind.Error, Verbatim = ex.Message });
            }
        }
        imageModel.TextChanged += (_, _) => Update();
        textModel.TextChanged += (_, _) => { Update(); ReadPrice(); };
        models.Row("Image model", imageModel);
        models.Add(imageRetired);
        models.Row("Text model", textModel);
        models.Add(textRetired);
        if (textIsOpenRouter) models.Add(price);
        models.Add(restore);
        Update();
        ReadPrice();
        return form;
    }

    private static Border Footer(FrameworkElement content) => new() { Child = content, Margin = new Thickness(10, -4, 10, 6) };

    private static Expander WhatIsSent(AIProvider provider, string notice) => new()
    {
        Header = Loc.F("What is sent to {0}", provider.Name), Margin = new Thickness(0, 8, 0, 0),
        Content = new TextBlock { Text = notice, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Style = (Style)Application.Current.FindResource("Glimmer.Text.Secondary") },
    };

    /// <summary>A key field with Save and Remove (and Test Key for OpenRouter), and what happened, said as a status.</summary>
    private StackPanel KeyRows(FormSection? section, IAISettingsService ai, string slug, string label, bool testable, string? name = null)
    {
        var panel = new StackPanel();
        string saved;
        string? loadError = null;
        try { saved = ai.Key(slug) ?? ""; }
        catch (Exception ex) { saved = ""; loadError = ex.Message; }
        var field = new PasswordBox { Width = 280, Password = saved, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(field, name ?? Loc.T(label));
        var buttons = new WrapPanel();
        var status = new ContentControl { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        Button? save = null, test = null, remove = null;
        void Status(UIElement? content)
        {
            status.Content = content;
            // The result appears beside the buttons; say it too.
            if (content is StatusLine line && UIElementAutomationPeer.CreatePeerForElement(field) is { } peer)
                peer.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, line.ShownText, "glimmer.key");
        }
        void Refresh()
        {
            save!.IsEnabled = field.Password != saved;
            if (test is not null) test.IsEnabled = field.Password.Length > 0;
            remove!.Visibility = saved.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void Save()
        {
            var trimmed = field.Password.Trim();
            try
            {
                ai.SaveKey(slug, trimmed);
                field.Password = trimmed;
                saved = trimmed;
                Status(testable || saved.Length == 0 ? null : new StatusLine { Kind = StatusKind.Success, TextKey = "Saved in your keychain" });
            }
            catch (Exception ex)
            {
                Status(new StatusLine { Kind = StatusKind.Error, Verbatim = ex.Message });
            }
            Refresh();
        }
        save = SmallButton("Save", Save);
        remove = SmallButton("Remove", () => { field.Password = ""; Save(); });
        buttons.Children.Add(save);
        if (testable)
        {
            test = SmallButton("Test Key", async () =>
            {
                var key = field.Password.Trim();
                var spinner = new Spinner { Width = 14, Height = 14 };
                AutomationProperties.SetName(spinner, Loc.T("Checking the key"));
                Status(spinner);
                test!.IsEnabled = false;
                try
                {
                    var remaining = await ai.TestOpenRouterKeyAsync(key, CancellationToken.None);
                    var usd = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
                    usd.CurrencySymbol = "$";
                    Status(new StatusLine { Kind = StatusKind.Success, Verbatim = remaining is { } r ? Loc.F("Valid · {0} left", r.ToString("C", usd)) : Loc.T("Valid") });
                }
                catch (Exception ex)
                {
                    Status(new StatusLine { Kind = StatusKind.Error, Verbatim = ex.Message });
                }
                Refresh();
            });
            buttons.Children.Add(test);
        }
        buttons.Children.Add(remove);
        buttons.Children.Add(status);
        field.PasswordChanged += (_, _) => Refresh();
        field.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Save(); };
        panel.Children.Add(field);
        panel.Children.Add(buttons);
        if (loadError is not null) Status(new StatusLine { Kind = StatusKind.Error, Verbatim = loadError });
        else if (!testable && saved.Length > 0) Status(new StatusLine { Kind = StatusKind.Success, TextKey = "Saved in your keychain" });
        Refresh();
        if (section is not null) section.Row(label, panel);
        return panel;
    }

    /// <summary>What the register says about a service: its own note, whether it was tried, where the key goes, links.</summary>
    private static StackPanel ProviderFacts(AIProvider provider, bool documentationOnly)
    {
        var facts = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        if (provider.TermsNote is { } note) facts.Children.Add(Caption(Loc.Maybe(note)));
        if (documentationOnly)
            facts.Children.Add(Caption(Loc.F("Glimmer's support for {0} is built from its documentation and has not been tried with a real call yet. If it answers with an error, Glimmer shows it as it came.", provider.Name)));
        var host = Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : provider.BaseUrl;
        facts.Children.Add(Caption(Loc.F("Your key is stored in your keychain and sent only to {0}.", host)));
        facts.Children.Add(Links(provider));
        return facts;
    }

    /// <summary>Key, terms and model for a text service other than OpenRouter (ProviderTextSettings).</summary>
    private StackPanel ProviderTextSettings(IAISettingsService ai, AIProvider provider)
    {
        var s = AppServices.Settings;
        var panel = new StackPanel();
        if (provider.NeedsKey) panel.Children.Add(new FormRow(Loc.F("{0} API key", provider.Name), KeyRows(null, ai, provider.Slug, "API key", false, Loc.F("{0} API key", provider.Name)), null));
        panel.Children.Add(ProviderFacts(provider, provider.FromDocumentationOnly));
        var modelKey = AIPreferences.TextModelKeyFor(provider.Slug);
        string chosen = s.Get(modelKey, "");
        IReadOnlyList<ProviderModel> models = [];
        var picker = new ComboBox { Style = Compact("ComboBox"), MinWidth = 200, IsEnabled = false };
        AutomationProperties.SetName(picker, Loc.T("Model"));
        var loading = new Spinner { Width = 14, Height = 14, Margin = new Thickness(6, 0, 6, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(loading, Loc.T("Loading the model list"));
        var details = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        void FillPicker()
        {
            picker.Items.Clear();
            if (chosen.Length == 0) picker.Items.Add(new ComboBoxItem { Content = Loc.T("Choose a model"), Tag = "" });
            // A model chosen earlier that the service no longer lists is shown as such, not silently kept.
            if (chosen.Length > 0 && models.Count > 0 && models.All(m => m.Id != chosen)) picker.Items.Add(new ComboBoxItem { Content = Loc.F("{0} (no longer listed)", chosen), Tag = chosen });
            if (models.Count == 0 && chosen.Length > 0) picker.Items.Add(new ComboBoxItem { Content = chosen, Tag = chosen });
            foreach (var m in models) picker.Items.Add(new ComboBoxItem { Content = m.Id, Tag = m.Id });
            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == chosen);
            picker.IsEnabled = models.Count > 0;
        }
        void ShowDetails()
        {
            details.Children.Clear();
            if (models.FirstOrDefault(m => m.Id == chosen) is { } m)
            {
                if (m.Tools == false) details.Children.Add(new StatusLine { Kind = StatusKind.Warning, Verbatim = Loc.F("{0} lists this model without tool calls. The Assistant can't use it.", provider.Name) });
                if (m.Vision == false) details.Children.Add(Caption(Loc.T("This model can't take pictures, so the Assistant plans without looking at the document, and suggestions and Find Distractions won't work with it.")));
                if (ai.PriceText(m) is { } line)
                {
                    details.Children.Add(Caption(Loc.F("{0}'s list: {1}. {2} bills you for what you use.", provider.Name, line, provider.Name)));
                    if (ai.ListedTextPrice(provider) is { } listed) details.Children.Add(Caption(Loc.F("Read {0}.", listed.Read.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))));
                }
                else details.Children.Add(Caption(Loc.F("Billed by {0} at their rates.", provider.Name)));
            }
            else if (chosen.Length > 0 && models.Count > 0)
            {
                details.Children.Add(new StatusLine { Kind = StatusKind.Warning, Verbatim = Loc.F("{0} no longer lists {1}. Choose another model.", provider.Name, chosen) });
            }
        }
        // Remembers the model and what the list says it can do, for the Assistant to check before a request.
        void Choose(string id)
        {
            chosen = id;
            s.Set(modelKey, id);
            var m = models.FirstOrDefault(x => x.Id == id);
            s.Set(modelKey + ".tools", m?.Tools);
            s.Set(modelKey + ".vision", m?.Vision);
            if (models.Count > 0) ai.RememberTextPrice(provider.Slug, id, ai.PriceText(m));
            ShowDetails();
        }
        async void Load()
        {
            loading.Visibility = Visibility.Visible;
            try
            {
                models = await ai.TextModelsAsync(provider, ai.Key(provider.Slug), CancellationToken.None);
                FillPicker();
                if (chosen.Length > 0) Choose(chosen);
            }
            catch (Exception ex)
            {
                details.Children.Clear();
                details.Children.Add(new StatusLine { Kind = StatusKind.Error, Verbatim = ex.Message });
            }
            finally
            {
                loading.Visibility = Visibility.Collapsed;
            }
        }
        picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is ComboBoxItem { Tag: string id } && id.Length > 0 && id != chosen) Choose(id); };
        var refresh = SmallButton("Refresh", Load);
        panel.Children.Add(new FormRow(Loc.T("Model"), new StackPanel { Orientation = Orientation.Horizontal, Children = { picker, loading, refresh } }, null));
        panel.Children.Add(details);
        FillPicker();
        Load();
        return panel;
    }

    /// <summary>The key and model of an image service; the model is typed by its id (ProviderImageSettings).</summary>
    private StackPanel ProviderImageSettings(IAISettingsService ai, AIProvider provider)
    {
        var s = AppServices.Settings;
        var panel = new StackPanel();
        panel.Children.Add(new FormRow(Loc.F("{0} API key", provider.Name), KeyRows(null, ai, provider.Slug, "API key", false, Loc.F("{0} API key", provider.Name)), null));
        panel.Children.Add(ProviderFacts(provider, documentationOnly: true));
        var model = new TextBox { Text = s.Get(AIPreferences.ImageModelKeyFor(provider.Slug), ""), Width = 260, Style = Compact("TextBox"), Tag = Loc.F("The model's id, as {0} writes it", provider.Name) };
        model.TextChanged += (_, _) => s.Set(AIPreferences.ImageModelKeyFor(provider.Slug), model.Text.Trim());
        panel.Children.Add(new FormRow(Loc.T("Model"), model, null) { Margin = new Thickness(0, 0, 0, 8) });
        if (provider.Slug == "fal")
        {
            var edit = new TextBox { Text = s.Get(AIPreferences.ImageEditModelKeyFor(provider.Slug), ""), Width = 260, Style = Compact("TextBox"), Tag = Loc.T("Leave empty for the model's id followed by /edit") };
            edit.TextChanged += (_, _) => s.Set(AIPreferences.ImageEditModelKeyFor(provider.Slug), edit.Text.Trim().Length == 0 ? null : edit.Text.Trim());
            panel.Children.Add(new FormRow(Loc.T("Model for editing"), edit, null) { Margin = new Thickness(0, 0, 0, 8) });
        }
        panel.Children.Add(Caption(Loc.F("Billed by {0} at their rates. Glimmer shows no cost for these pictures, because {1}'s answers carry none.", provider.Name, provider.Name)));
        return panel;
    }

    /// <summary>The model of a local server (Ollama): only models that list tool calls can be chosen (LocalTextSettings).</summary>
    private StackPanel LocalTextSettings(IAISettingsService ai, AIProvider provider, IReadOnlyList<LocalModel>? models, Action refresh)
    {
        var s = AppServices.Settings;
        var panel = new StackPanel();
        var usable = (models ?? []).Where(m => m.Tools).ToList();
        if (models is null)
            panel.Children.Add(new StatusLine { Kind = StatusKind.Warning, Verbatim = Loc.F("{0} isn't running on this Mac. Start it, then click Refresh.", provider.Name) });
        else if (usable.Count == 0)
            panel.Children.Add(new StatusLine { Kind = StatusKind.Warning, Verbatim = Loc.F("None of the models installed in {0} lists tool calls, which the Assistant needs.", provider.Name) });
        var modelKey = AIPreferences.TextModelKeyFor(provider.Slug);
        string chosen = s.Get(modelKey, "");
        var picker = new ComboBox { Style = Compact("ComboBox"), MinWidth = 200, IsEnabled = usable.Count > 0 };
        AutomationProperties.SetName(picker, Loc.T("Model"));
        if (chosen.Length == 0) picker.Items.Add(new ComboBoxItem { Content = Loc.T("Choose a model"), Tag = "" });
        if (chosen.Length > 0 && usable.All(m => m.Name != chosen)) picker.Items.Add(new ComboBoxItem { Content = Loc.F("{0} (not installed)", chosen), Tag = chosen });
        foreach (var m in usable)
            picker.Items.Add(new ComboBoxItem { Content = provider.EvaluatedModels.Contains(m.Name) ? m.Name : Loc.F("{0} (not evaluated with Glimmer)", m.Name), Tag = m.Name });
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == chosen);
        var facts = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Show()
        {
            facts.Children.Clear();
            if (provider.TermsNote is { } note) facts.Children.Add(Caption(Loc.Maybe(note)));
            if (usable.FirstOrDefault(m => m.Name == chosen) is not { } m) return;
            if (m.Size is { } size) facts.Children.Add(Caption(Loc.F("{0} takes about {1} of memory while it runs.", m.Name, Readable.Bytes(size))));
            facts.Children.Add(Caption(provider.EvaluatedModels.Contains(m.Name)
                ? Loc.F("Glimmer has tried {0} with its own test of everyday requests. It gets most of them right; a Swedish request for an adjustment can come back incomplete, and the same request in English works.", m.Name)
                : Loc.F("Glimmer has not tried {0}. It may plan badly or call tools that don't exist; Glimmer drops those and tells you.", m.Name)));
            if (!m.Vision) facts.Children.Add(Caption(Loc.T("This model can't take pictures, so the Assistant plans without looking at the document, and Suggest Edits and Find Distractions won't work with it.")));
        }
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not ComboBoxItem { Tag: string name } || name.Length == 0) return;
            chosen = name;
            s.Set(modelKey, name);
            if (usable.FirstOrDefault(m => m.Name == name) is { } m)
            {
                s.Set(modelKey + ".tools", m.Tools);
                s.Set(modelKey + ".vision", m.Vision);
            }
            Show();
        };
        panel.Children.Add(new FormRow(Loc.T("Model"), new StackPanel { Orientation = Orientation.Horizontal, Children = { picker, SmallButton("Refresh", refresh) } }, null));
        panel.Children.Add(facts);
        Show();
        return panel;
    }
}
