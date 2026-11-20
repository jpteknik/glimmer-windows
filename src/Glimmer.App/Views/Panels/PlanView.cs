// Ported from App/AIPanel.swift (PlanView). The assistant's plan under its message: one row per step with its state,
// what it does and why, then Run / Dismiss while proposed, Stop while running, and Save as Action when finished.
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Panels;

public sealed class PlanView : StackPanel
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(ChatMessage), typeof(PlanView),
        new PropertyMetadata(null, (d, _) => ((PlanView)d).Build()));

    /// <summary>The assistant's own remarks in a step's note (the rest is the model's words, shown as they are).</summary>
    private static readonly string[] OwnNotes =
    [
        "asks for a setting that doesn't exist",
        "Glimmer could not check these settings before running: the other app doesn't say which value chooses them, in",
        "type taken from your request",
    ];

    public PlanView()
    {
        Margin = new Thickness(0, 6, 0, 0);
        Loaded += (_, _) => Build();
    }

    public ChatMessage? Message { get => (ChatMessage?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    private IAIController? AI
    {
        get
        {
            for (DependencyObject? x = this; x is not null; x = VisualTreeHelper.GetParent(x))
                if (x is AIPanel panel) return panel.Controller;
            return null;
        }
    }

    private void Build()
    {
        Children.Clear();
        if (Message is not { Plan: { } plan } message || AI is not { } ai)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        foreach (var step in plan.Steps)
        {
            var status = ai.StatusOfStep(step.Id);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var icon = StatusIcon(status.Kind);
            icon.Width = 14;
            icon.Margin = new Thickness(0, 2, 6, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var text = new StackPanel();
            var what = Describe(step);
            text.Children.Add(new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
            // The step's own sign says that it failed; this is why. One sign, not two.
            string? note = status.Kind == StepStatusKind.Failed ? (status.Message is { } why ? Loc.T(why) : null) : step.Rationale is { } r ? Note(r) : null;
            if (note is not null) text.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Glimmer.Text.Caption") });
            row.Children.Add(text);
            // One stop per step: its status, what it does and why.
            AutomationProperties.SetName(row, string.Join(", ", new[] { StatusName(status.Kind), what, note }.Where(s => !string.IsNullOrEmpty(s))));
            row.Focusable = true;
            Children.Add(row);
        }
        var actions = new DockPanel { Margin = new Thickness(0, 4, 0, 0), LastChildFill = false };
        switch (ai.StatusOfPlan(message.Id))
        {
            case PlanStatus.Proposed:
                var run = PriceGate.Gate(Small("Run", accent: true), () => plan.UsesAI);
                run.Click += (_, _) => ai.RunPlan(message.Id);
                var dismiss = Small("Dismiss");
                dismiss.Click += (_, _) => ai.CancelPlan(message.Id);
                DockPanel.SetDock(run, Dock.Right);
                DockPanel.SetDock(dismiss, Dock.Right);
                actions.Children.Add(run);
                actions.Children.Add(dismiss);
                if (plan.UsesAI)
                {
                    var credits = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    credits.Children.Add(new Icon { Symbol = "sparkles", Size = 11, Margin = new Thickness(0, 0, 4, 0), Foreground = (Brush)FindResource("Glimmer.MutedBrush") });
                    credits.Children.Add(new TextBlock { Text = Loc.T("Uses AI credits"), Style = (Style)FindResource("Glimmer.Text.Caption") });
                    actions.Children.Add(credits);
                }
                Children.Add(actions);
                if (plan.UsesAI) Children.Add(new UnknownPriceNote());
                break;
            case PlanStatus.Running:
                var stop = Small("Stop");
                stop.Click += (_, _) => ai.CancelPlan(message.Id);
                DockPanel.SetDock(stop, Dock.Right);
                actions.Children.Add(stop);
                Children.Add(actions);
                break;
            case PlanStatus.Finished when !ai.IsCorrection(message.Id):
                var save = new Button
                {
                    Style = (Style)FindResource("Glimmer.IconButton"), ToolTip = Loc.T("Keep these steps to run again from ⌘K or the Actions menu"),
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Icon { Symbol = "bookmark", Size = 11, Margin = new Thickness(0, 0, 4, 0) }, new TextBlock { Text = Loc.T("Save as Action"), FontSize = 12 } } },
                };
                AutomationProperties.SetName(save, Loc.T("Save as Action"));
                save.Click += (_, _) => ai.SaveAction(message.Id);
                DockPanel.SetDock(save, Dock.Right);
                actions.Children.Add(save);
                Children.Add(actions);
                break;
        }
    }

    private Button Small(string title, bool accent = false) => new()
    {
        Content = Loc.T(title), Margin = new Thickness(6, 0, 0, 0), Style = (Style)FindResource(accent ? "Glimmer.Compact.AccentButton" : "Glimmer.Compact.Button"),
    };

    private FrameworkElement StatusIcon(StepStatusKind kind) => kind switch
    {
        StepStatusKind.Running => new Spinner { Height = 12 },
        StepStatusKind.Done => new StatusSymbol { Kind = StatusKind.Success },
        StepStatusKind.Failed => new StatusSymbol { Kind = StatusKind.Error },
        StepStatusKind.Skipped => new Icon { Symbol = "minus.circle", Size = 12, Foreground = (Brush)FindResource("Glimmer.TertiaryBrush") },
        _ => new Icon { Symbol = "circle", Size = 12, Foreground = (Brush)FindResource("Glimmer.TertiaryBrush") },
    };

    private static string StatusName(StepStatusKind kind) => Loc.T(kind switch
    {
        StepStatusKind.Running => "Running", StepStatusKind.Done => "Done", StepStatusKind.Failed => "Failed", StepStatusKind.Skipped => "Skipped", _ => "Waiting",
    });

    /// <summary>A step's note with Glimmer's own remarks in the person's language.</summary>
    private static string Note(string rationale)
    {
        foreach (var own in OwnNotes) rationale = rationale.Replace(own, Loc.T(own), StringComparison.Ordinal);
        return rationale;
    }

    /// <summary>A step's name in the plan, in the person's language: what the menus call it where the menus have it; what
    /// the model wrote (a text, a prompt) as it is.</summary>
    public static string Describe(AgentStep step)
    {
        static string Menu(string title) => Loc.Maybe(title).Replace("…", "");
        static string Plain(string? s) => Menu(CultureInfo.InvariantCulture.TextInfo.ToTitleCase((s ?? "").Replace('_', ' ')));
        var effects = new Dictionary<string, string>
        {
            ["dropShadow"] = "Drop Shadow", ["innerShadow"] = "Inner Shadow", ["outerGlow"] = "Outer Glow", ["stroke"] = "Stroke", ["colorOverlay"] = "Color Overlay",
        };
        var menuTitles = new Dictionary<string, string>
        {
            ["crop"] = "Crop", ["resize_image"] = "Image Size…", ["resize_canvas"] = "Canvas Size…", ["auto_enhance"] = "Auto Enhance", ["auto_color"] = "Auto Color",
            ["select_people"] = "Select People", ["select_sky"] = "Select Sky", ["select_subject"] = "Select Subject", ["remove_background"] = "Remove Background",
            ["transform_layer"] = "Free Transform", ["add_waveform"] = "Add Waveform", ["deselect"] = "Deselect",
        };
        // Features a release can be built without.
#if GLIMMER_HEAL
        menuTitles["heal_selection"] = "Heal Selection";
#endif
#if GLIMMER_RAW
        menuTitles["develop_raw"] = "Develop…";
#endif
        string S(string key) => step.String(key) ?? "";
        return step.Op switch
        {
            "add_adjustment" => AdjustmentKind.FromRawValue(S("type")) is { } a ? Menu(a.Title()) : Loc.T("Adjustment"),
            "apply_filter" => FilterKind.FromRawValue(S("type")) is { } f ? Menu(f.Title()) : Loc.T("Filter"),
            "add_layer_effect" => Loc.F("Layer style: {0}", effects.TryGetValue(S("type"), out var e) ? Menu(e) : Loc.T("Effect")),
            "apply_preset" => Loc.F("Preset: {0}", S("name")),
            "add_text" => Loc.F("Text “{0}”", S("text")),
            "style_text" => step.String("match") is { } m ? Loc.F("Style “{0}”", m) : Loc.T("Style text"),
            "image_edit_composite" => Loc.F("AI edit: {0}", S("instruction")),
            "generate_layer" => Loc.F("Generate: {0}", S("prompt")),
            "select_layer" => Loc.F("Select layer “{0}”", S("name")),
            "inpaint_selection" => S("mode") == "remove" ? Loc.T("Remove selected object") : Loc.F("Fill selection: {0}", S("prompt")),
            "upscale" => Loc.F("Upscale {0}×", (int)(step.Number("scale") ?? 2)),
            "fill" => Loc.F("Fill with {0}", S("color")),
            "set_foreground" => Loc.F("Foreground {0}", S("color")),
            "run_command" => WindowsCommands.Registry.Descriptor(new CommandID(S("id"))) is { } d ? Menu(d.Title) : Plain(step.String("id")),
            _ => menuTitles.TryGetValue(step.Op, out var t) ? Menu(t) : Plain(step.Op),
        };
    }
}
