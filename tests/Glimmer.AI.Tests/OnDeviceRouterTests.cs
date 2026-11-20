// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/OnDeviceRouterTests.swift
// (not ported: LiveOnDeviceRouterTests, which run Apple's on-device model; Windows has none)
using Glimmer.AI;

namespace Glimmer.AI.Tests;

public class OnDeviceRouterTests
{
    private static readonly OnDeviceRouter.Candidate[] Candidates =
    {
        new("adjustment.invert", "Invert", "Adjustment"),
        new("image.flipHorizontal", "Flip Canvas Horizontal", "Image"),
        new("layer.new", "New Layer", "Layer"),
    };

    [Fact]
    public void CompoundRequestsAreNeverASingleCommand()
    {
        foreach (var text in new[] { "make it a moody black and white with a vignette", "invert, then flip it", "warmer and sharper", "gör den svartvit med vinjett", "ljusare och varmare" })
        {
            Assert.True(OnDeviceRouter.IsCompound(text), text);
            var route = new OnDeviceRouter.Route(OnDeviceRouter.RouteAction.Run, "adjustment.invert", "");
            Assert.Equal(new OnDeviceRouter.Decision.Assistant(), OnDeviceRouter.Decide(route, text, Candidates));
        }
        foreach (var text in new[] { "invert the colors", "make it black and white", "new layer", "gör den svart och vit" })
            Assert.False(OnDeviceRouter.IsCompound(text), text);
        Assert.True(OnDeviceRouter.IsCompound("one two three four five six seven eight nine"));
    }

    [Fact]
    public void UnknownIdsFallBackToTheAssistant()
    {
        var route = new OnDeviceRouter.Route(OnDeviceRouter.RouteAction.Run, "image.explode", "");
        Assert.Equal(new OnDeviceRouter.Decision.Assistant(), OnDeviceRouter.Decide(route, "explode", Candidates));
        var ok = new OnDeviceRouter.Route(OnDeviceRouter.RouteAction.Run, " layer.new ", "");
        Assert.Equal(new OnDeviceRouter.Decision.Run("layer.new"), OnDeviceRouter.Decide(ok, "new layer", Candidates));
        var gen = new OnDeviceRouter.Route(OnDeviceRouter.RouteAction.Generate, "", "");
        Assert.Equal(new OnDeviceRouter.Decision.Generate("a red fox"), OnDeviceRouter.Decide(gen, "a red fox", Candidates));
        var described = new OnDeviceRouter.Route(OnDeviceRouter.RouteAction.Generate, "", "  a red fox, studio light ");
        Assert.Equal(new OnDeviceRouter.Decision.Generate("a red fox, studio light"), OnDeviceRouter.Decide(described, "a red fox", Candidates));
        Assert.Equal(new OnDeviceRouter.Decision.Assistant(), OnDeviceRouter.Decide(new OnDeviceRouter.Route(OnDeviceRouter.RouteAction.Assistant, "", ""), "x", Candidates));
    }

    /// Windows has no Apple Intelligence: the router says it isn't available, and asking it anyway
    /// is an error the caller turns into "use the assistant", as on a Mac without it.
    [Fact]
    public async Task WithoutAnOnDeviceModelTheRouterIsNotAvailable()
    {
        Assert.False(OnDeviceRouter.IsAvailable);
        await Assert.ThrowsAsync<OnDeviceRouter.UnavailableException>(() => OnDeviceRouter.RouteAsync("flip the canvas horizontally", Candidates));
    }

    private sealed class Scripted : OnDeviceRouter.IModel
    {
        public string Action = "run", OptionID = "", Prompt = "";
        public string? SeenPrompt;
        public bool IsAvailable => true;

        public Task<(string Action, string OptionID, string Prompt)> RespondAsync(string instructions, string prompt, CancellationToken ct)
        {
            SeenPrompt = prompt;
            return Task.FromResult((Action, OptionID, Prompt));
        }
    }

    [Fact]
    public async Task AModelThatIsPluggedInIsAskedWithTheOptionsAndItsAnswerIsChecked()
    {
        var before = OnDeviceRouter.Model;
        try
        {
            var model = new Scripted { Action = "run", OptionID = "image.flipHorizontal" };
            OnDeviceRouter.Model = model;
            Assert.True(OnDeviceRouter.IsAvailable);
            var decision = await OnDeviceRouter.RouteAsync("flip the canvas horizontally", Candidates);
            Assert.Equal(new OnDeviceRouter.Decision.Run("image.flipHorizontal"), decision);
            Assert.Equal("OPTIONS:\n- id: adjustment.invert — Invert (Adjustment)\n- id: image.flipHorizontal — Flip Canvas Horizontal (Image)\n- id: layer.new — New Layer (Layer)\n\nREQUEST: flip the canvas horizontally", model.SeenPrompt);
            // An id that isn't in the list, or a request that asks for several things, goes to the assistant.
            model.OptionID = "image.explode";
            Assert.Equal(new OnDeviceRouter.Decision.Assistant(), await OnDeviceRouter.RouteAsync("explode", Candidates));
            model.OptionID = "layer.new";
            Assert.Equal(new OnDeviceRouter.Decision.Assistant(), await OnDeviceRouter.RouteAsync("new layer, then invert", Candidates));
            // An action it doesn't know is the assistant; no options are said so.
            model.Action = "dance";
            Assert.Equal(new OnDeviceRouter.Decision.Assistant(), await OnDeviceRouter.RouteAsync("x", Array.Empty<OnDeviceRouter.Candidate>()));
            Assert.EndsWith("OPTIONS:\n(none)\n\nREQUEST: x", model.SeenPrompt);
        }
        finally
        {
            OnDeviceRouter.Model = before;
        }
    }

    [Fact]
    public void TheInstructionsAreTheOriginals()
    {
        Assert.StartsWith("You route requests typed into the command bar of a photo editor. Decide one action:\n- run:", OnDeviceRouter.Instructions);
        Assert.EndsWith("When in doubt, answer assistant.", OnDeviceRouter.Instructions);
        // The router is not sent the operations: fixed instructions and the palette's matches only.
        Assert.DoesNotContain("add_adjustment", OnDeviceRouter.Instructions);
    }
}
