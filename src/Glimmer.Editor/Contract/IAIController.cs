// View contract (see ViewContractTypes.cs for the rules every member follows).
// The parts of App/AIController*.swift that AIPanel, the omnibar, the selection bar, the options bar and the canvas
// read or call. Types from GlimmerAI are the ported Glimmer.AI types (AgentPlan, AgentPlanner.Suggestion, SavedAction,
// AIPipelines.*, MagicSelector.Point, OnDeviceModel, Preset); LensBlurParameters is Glimmer.Render's
// (DocumentEffects.LensBlurParameters, LensBlur.swift).
using System.ComponentModel;
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Render;

namespace Glimmer.Editor;

public interface IAIController : INotifyPropertyChanged
{
    /// <summary>An OpenRouter key is saved.</summary>
    bool HasAPIKey { get; }
    /// <summary>The picture service chosen in Settings has a key (OpenRouter unless another is chosen).</summary>
    bool HasImageKey { get; }
    /// <summary>A paid picture job may start from the window: the price is known, or the person ticked, once for the
    /// service, that it states none (the buttons are grey until then).</summary>
    bool UnknownPriceAgreed { get; }
    /// <summary>A text request can be made at all (a key where one is needed, and a model).</summary>
    bool HasTextModel { get; }

    // Assistant
    IReadOnlyList<ChatMessage> Messages { get; }
    IReadOnlyList<SavedAction> SavedActions { get; }
    IReadOnlyList<AgentPlanner.Suggestion> Suggestions { get; set; }
    bool IsSuggesting { get; }
    bool IsPlanning { get; }
    /// <summary>Run plans with paid AI steps without asking first (persisted, "ai.autoRunPlans").</summary>
    bool AutoRunAIPlans { get; set; }
    /// <summary>The line the Assistant shows about what a request costs; null when nothing is billed.</summary>
    UiText? TextPriceLine { get; }
    StepStatus StatusOfStep(Guid stepId);
    PlanStatus? StatusOfPlan(Guid messageId);
    bool IsCorrection(Guid messageId);
    void Ask(string text);
    void SuggestEdits();
    void RunPlan(Guid messageId);
    void CancelPlan(Guid messageId);
    void SaveAction(Guid messageId);
    void DismissPendingCommand(Guid messageId);
    void RunPendingCommand(Guid messageId);
    void Run(SavedAction action);
    void DeleteSavedAction(Guid id);

    // Jobs
    IReadOnlyList<AIJob> Jobs { get; }
    /// <summary>OpenRouter cost of this window's jobs, in US dollars.</summary>
    decimal TotalCost { get; }
    /// <summary>Finished paid jobs whose service's answer carried no cost, counted beside the sum.</summary>
    int JobsWithoutPrice { get; }
    bool IsBusy { get; }
    void ClearFinished();
    void Cancel(Guid jobId);

    // Paid picture tools
    void Generate(string prompt, string negative, int width, int height);
    /// <summary>Edit with Prompt: the document as the service changes it, as a new layer.</summary>
    void EditImage(string instruction);
    /// <summary>Generative Fill (mode fill) or Remove Object (mode remove); referencePath: an optional image file.</summary>
    void Fill(AIPipelines.InpaintMode mode, string prompt, string? referencePath);
    void Expand(AIPipelines.Margins margins, string prompt);
    void Relight(AIPipelines.LightDirection direction, SRGBColor color, double intensity, string environment);
    void RemoveReflections(double strength);
    void Harmonize(double strength);
    void Upscale(int scale, bool neural, bool enhance, double creativity);

    // On this computer
    void MatchColor(string referencePath, double strength);
    void AutoEnhance();
    void AutoColor();
    void AutoStraighten();
    void SuggestCrop();
    void RemoveBackground();
    void SelectSubject();
    void SelectPeople();
    void SelectSky();
    void StraightenDocument();
    void TextToLayers();
    void CopyImageText();
    void ApplyPreset(Preset preset);
    void HealSelection(bool allowLarge = false);

    // Clean Up
    IReadOnlyList<AIPipelines.Distraction> Distractions { get; }
    UiText? DistractionMessage { get; }
    void FindDistractions();
    void SelectDistractions(IReadOnlyList<AIPipelines.Distraction> items);
    void RemoveDistractions(IReadOnlyList<AIPipelines.Distraction> items);
    void DismissDistraction(string id);

    // Magic Select (the canvas draws the points; Return keeps, Esc puts the earlier selection back)
    IReadOnlyList<MagicSelector.Point> MagicPoints { get; }
    bool IsSegmenting { get; }
    void MagicSelect(Vec2 point, bool positive);
    void CancelMagicSelect();
    void ResetMagicSelect();

    // Lens Blur
    bool IsLensBlurLive { get; }
    bool IsPreparingLensBlur { get; }
    void BeginLensBlur(DocumentEffects.LensBlurParameters parameters);
    void UpdateLensBlur(DocumentEffects.LensBlurParameters parameters, bool showDepth);
    void EndLensBlur(bool apply, DocumentEffects.LensBlurParameters parameters);

    // Omnibar
    /// <summary>On-device analysis of this image (instant and free): commands worth suggesting.</summary>
    IReadOnlyList<CommandID> SmartSuggestions { get; }
    /// <summary>insights?.horizonDegrees: the tilt Auto Straighten would fix, when one was found.</summary>
    double? HorizonDegrees { get; }
    void RefreshInsights();

    /// <summary>A feature needs models that aren't on this computer: the download question while it is up.</summary>
    ModelDownloadRequest? ModelRequest { get; set; }
}

/// <summary>One message in the assistant's conversation (AIController.Message). Text the model wrote is verbatim;
/// the app's own sentences are templates. PendingCommand: the palette entry's id a request waits on.</summary>
public sealed record ChatMessage(Guid Id, bool FromUser, UiText Text, AgentPlan? Plan, string? ExactWords, string? PendingCommand);

public enum StepStatusKind { Pending, Running, Done, Failed, Skipped }

/// <summary>A plan step's state; Message is set when it failed (in the person's language: a template).</summary>
public readonly record struct StepStatus(StepStatusKind Kind, UiText? Message = null);

public enum PlanStatus { Proposed, Running, Finished, Failed, Cancelled }

public enum AIJobStatusKind { Running, Done, Failed, Cancelled }

/// <summary>A job in the Activity list (AIController.Job). Paid: a service bills for it.</summary>
public sealed record AIJob(Guid Id, UiText Title, AIJobStatusKind Status, UiText? FailureMessage, decimal? Cost, bool Paid, bool MayHaveBeenCharged);

/// <summary>Models a feature needs that aren't downloaded, and what to run once they are.</summary>
public sealed record ModelDownloadRequest(IReadOnlyList<OnDeviceModel> Models, Action? Retry);
