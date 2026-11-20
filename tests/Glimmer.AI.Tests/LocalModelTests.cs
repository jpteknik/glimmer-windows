// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/LocalModelTests.swift
// (the evaluation's cases, report and verdict, the figures check and the plan checks; the tests that run a plan through
// AgentPlanner, judge a plan, or call the image models come with the Agent and the image providers)
using Glimmer.AI;

namespace Glimmer.AI.Tests;

public class LocalModelEvaluationTests
{
    [Fact]
    public void TheCasesAreHalfSwedishAndUseOnlyTheShortList()
    {
        Assert.Equal(18, LocalModelEvaluation.Scored.Count);
        Assert.Equal(19, LocalModelEvaluation.Cases.Count);   // one known gap, not counted
        Assert.Equal(8, LocalModelEvaluation.Scored.Count(c => c.Language == "sv"));
        Assert.Equal(2, LocalModelEvaluation.Scored.Count(c => c.AnswerOnly));   // one in each language: no invented figures
        Assert.Equal(3, LocalModelEvaluation.Scored.Count(c => c.ChangesNothing));   // requests that name no edit
        foreach (var c in LocalModelEvaluation.Cases.Where(c => !c.AnswerOnly && !c.ChangesNothing))
            Assert.Contains(c.Op, AgentAPI.CoreOps);
        Assert.Equal(1, LocalModelEvaluation.Cases.Count(c => c.MustRunAtOnce));
        Assert.Equal(1, LocalModelEvaluation.Cases.Count(c => c.KnownGap));
        Assert.Equal(new[] { "1200" }, LocalModelEvaluation.Cases[9].Alternatives[0].Values);
        Assert.Equal(3, LocalModelEvaluation.RawCases.Count);
        Assert.Equal("develop_raw", LocalModelEvaluation.RawCases[2].MustNotUse);
    }

    [Fact]
    public void AModelThatAnswersRightPassesAndASlowOrWrongOneDoesNot()
    {
        var outcomes = LocalModelEvaluation.Cases.Select(c => new LocalModelEvaluation.Outcome(c.Goal, c.Language, 5, true, "")).ToList();
        Assert.True(new LocalModelEvaluation.Report("m", outcomes).Passes);
        var slow = outcomes.ToList();
        slow[0] = new LocalModelEvaluation.Outcome("g", "en", 61, true, "");
        Assert.False(new LocalModelEvaluation.Report("m", slow).Passes);
        var swedishWeak = outcomes.ToList();
        foreach (var i in new[] { 1, 3 }) swedishWeak[i] = new LocalModelEvaluation.Outcome("g", "sv", 5, false, "");
        Assert.False(new LocalModelEvaluation.Report("m", swedishWeak).Passes);   // 10 of 12, but only 4 of 6 in Swedish
        var report = new LocalModelEvaluation.Report("m", outcomes);
        var english = LocalModelEvaluation.Cases.Count(c => c.Language == "en");
        Assert.Equal((english, english), report.PassedIn("en"));
        Assert.Equal(5, report.Median);
        Assert.Equal(5, report.Slowest);
    }

    [Fact]
    public void AKnownGapIsShownButNotCounted()
    {
        var outcomes = Enumerable.Range(0, 12).Select(_ => new LocalModelEvaluation.Outcome("g", "en", 1, true, "")).ToList();
        outcomes.Add(new LocalModelEvaluation.Outcome("gap", "en", 1, false, "") { KnownGap = true });
        var report = new LocalModelEvaluation.Report("m", outcomes);
        Assert.True(report.Passed == 12 && report.Scored.Count == 12);
        Assert.Contains("0 of 1", report.GapLine);
        Assert.Equal(" Known gaps (not counted): 0 of 1 held.", report.GapLine);
    }

    [Fact]
    public void TheReportSaysWhenTheFiguresCheckChangedTheScore()
    {
        var outcomes = new[]
        {
            new LocalModelEvaluation.Outcome("a", "en", 1, true, "") { PassedAsWritten = true },
            new LocalModelEvaluation.Outcome("b", "en", 1, false, "") { PassedAsWritten = true },
        };
        var report = new LocalModelEvaluation.Report("m", outcomes);
        Assert.Equal(" As the model wrote them: 2 of 2; as the person gets them: 1 of 2.", report.BothWaysLine);
        Assert.Equal("", new LocalModelEvaluation.Report("m", new[] { outcomes[0] }).BothWaysLine);
    }
}

/// The Assistant states no figure it wasn't given.
public class AssistantNumbersTests
{
    private static readonly string[] Given = { "How big is it?", "2400 1600 1", "Photo" };

    [Fact]
    public void ReadsNumbersHoweverTheyAreWritten()
    {
        Assert.Equal(new double[] { 2400, 1600, 0.5, 12.5 }, AssistantNumbers.Numbers("It is 2,400 by 1 600 px, zoom 0,5 and 12.5 %"));
        Assert.Empty(AssistantNumbers.Numbers("No figures here."));
        Assert.Equal(new double[] { 1234567 }, AssistantNumbers.Numbers("1.234.567"));
    }

    [Fact]
    public void AGivenFigureStaysAndAnInventedOneGoesWithItsSentence()
    {
        var right = AssistantNumbers.Checked("The document is 2400 × 1600 pixels.", Given);
        Assert.Equal("The document is 2400 × 1600 pixels.", right.Text);
        Assert.Empty(right.Removed);
        var wrong = AssistantNumbers.Checked("Sure. The document is 3000 × 2000 pixels. Anything else?", Given);
        Assert.Equal("Sure. Anything else?", wrong.Text);
        Assert.Equal(new[] { "number 3000", "number 2000" }, wrong.Removed);
    }

    [Fact]
    public void APriceIsNeverTheModelsOwn()
    {
        var result = AssistantNumbers.Checked("Upscaling costs about $0.04 per image. It has 1 layer.", Given);
        Assert.Equal("It has 1 layer.", result.Text);
        Assert.Equal(new[] { "price 0.04" }, result.Removed);
    }

    [Fact]
    public void StepNumbersAndSharesAreAllowed()
    {
        var result = AssistantNumbers.Checked("Step 2 sets the layer to 50 % opacity.", Given.Append(@"{""opacity"":0.5}").ToArray(), steps: 3);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void WhenEverythingGoesNothingIsLeft()
    {
        var result = AssistantNumbers.Checked("It is 4000 by 3000 pixels.", Given);
        Assert.Null(result.Text);
        Assert.Equal(new[] { "number 4000", "number 3000" }, result.Removed);
    }

    [Fact]
    public void AnAnswerThatLostASentenceSaysSoOnce()
    {
        Assert.Equal("Sure. Glimmer left out a figure it could not verify.", AssistantNumbers.Noted("Sure.", removed: true));
        Assert.Equal(AssistantNumbers.Notice, AssistantNumbers.Noted(null, removed: true));
        Assert.Equal("Sure.", AssistantNumbers.Noted("Sure.", removed: false));
    }

    [Fact]
    public void AProposalKeepsItsFiguresButNeverAPrice()
    {
        // `pricesOnly`: a suggestion's numbers are the proposal itself.
        var crop = AssistantNumbers.Checked("Crop to 4:5 around the face.", new[] { "x" }, pricesOnly: true);
        Assert.Equal("Crop to 4:5 around the face.", crop.Text);
        var priced = AssistantNumbers.Checked("Upscale it for only $0.05.", new[] { "x" }, pricesOnly: true);
        Assert.Null(priced.Text);
        Assert.Equal(new[] { "price 0.05" }, priced.Removed);
    }

    [Fact]
    public void ACostClaimWithoutAFigureIsTheKnownGap()
    {
        Assert.True(AssistantNumbers.ClaimsACost("yes, generating is free."));
        Assert.True(AssistantNumbers.ClaimsACost("it is billig"));
        Assert.False(AssistantNumbers.ClaimsACost("it costs money"));
        // Nothing is removed for it: the check sees figures, not claims.
        Assert.Equal("Yes, generating is free.", AssistantNumbers.Checked("Yes, generating is free.", Given).Text);
    }

    [Fact]
    public void AnInventedFigureNeverShowsWhileTheAnswerArrives()
    {
        const string answer = "Sure, here it is. The document is 3000 × 2000 pixels. It costs $0.04 to enlarge. It has 1 layer and is 2400 wide. Anything else?";
        var everShown = new List<string>();
        for (var end = 1; end <= answer.Length; end++) everShown.Add(AssistantNumbers.ShownWhileArriving(answer[..end], Given));
        foreach (var shown in everShown)
        {
            Assert.False(shown.Contains('3') || shown.Contains("2000") || shown.Contains("0.04") || shown.Contains('$'), shown);
        }
        // Text without figures flows as it arrives, and a given figure shows once its sentence is finished.
        Assert.Contains("Sure, he", everShown);
        Assert.Equal("Sure, here it is. It has 1 layer and is 2400 wide. Anything else?", everShown[^1]);
        Assert.DoesNotContain(everShown, s => s.EndsWith("is 24"));   // never half a number
    }

    [Fact]
    public void AFigureGivenInWordsBacksTheSameFigureInDigits()
    {
        Assert.Equal(new[] { 1.5 }, AssistantNumbers.NumbersInWords("one and a half"));
        Assert.Equal(new[] { 25.0 }, AssistantNumbers.NumbersInWords("twenty five"));
        Assert.Equal(new[] { 25.0 }, AssistantNumbers.NumbersInWords("tjugofem"));
        Assert.Equal(new[] { 250.0 }, AssistantNumbers.NumbersInWords("two hundred and fifty"));
        Assert.Equal(new[] { 200.0 }, AssistantNumbers.NumbersInWords("tvåhundra"));
        Assert.Equal(new[] { 3000.0 }, AssistantNumbers.NumbersInWords("three thousand"));
        Assert.Equal(new[] { 1.5 }, AssistantNumbers.NumbersInWords("en och en halv"));
        Assert.Equal(new[] { 0.5, 0.25, 2.0 }, AssistantNumbers.NumbersInWords("half a quarter double"));
        Assert.Equal(new[] { 18.0 }, AssistantNumbers.NumbersInWords("minus arton"));
        Assert.Empty(AssistantNumbers.NumbersInWords("nothing to see"));
        var result = AssistantNumbers.Checked("It takes 1.5 seconds.", new[] { "Make it take one and a half seconds" });
        Assert.Empty(result.Removed);
        var shares = AssistantNumbers.Checked("Set it to 50 percent.", new[] { "half" });
        Assert.Empty(shares.Removed);
    }

    [Fact]
    public void FiguresTheAppWorksOutFromTheDocumentCountWhereTheyBelong()
    {
        var extra = AssistantNumbers.Worked(2400, 1600, "crop to 16:9 and make it half as large");
        // "2400 × 1350" beside a crop to 16:9, and "1200 × 800" beside "half as large".
        Assert.Empty(AssistantNumbers.Checked("The crop is 2400 × 1350.", new[] { "crop to 16:9", "2400 1600" }, extra: extra).Removed);
        Assert.Empty(AssistantNumbers.Checked("Half is 1200 × 800.", new[] { "2400 1600" }, extra: extra).Removed);
        // The document's shape counts only written as that ratio, and its megapixels only beside the word.
        Assert.Equal((3, 2), extra.Ratio);
        Assert.Empty(AssistantNumbers.Checked("It is a 3:2 image of 3.84 megapixels.", new[] { "2400 1600" }, extra: extra).Removed);
        Assert.NotEmpty(AssistantNumbers.Checked("It is a 4:3 image.", new[] { "2400 1600" }, extra: extra).Removed);
        Assert.NotEmpty(AssistantNumbers.Checked("It has 3.84 layers.", new[] { "2400 1600" }, extra: extra).Removed);
        // A sentence about money, time or a service gets none of it.
        Assert.NotEmpty(AssistantNumbers.Checked("That takes 1200 seconds.", new[] { "2400 1600" }, extra: extra).Removed);
        Assert.NotEmpty(AssistantNumbers.Checked("OpenRouter has 1200 models.", new[] { "2400 1600" }, extra: extra).Removed);
        // An operation's own figures count where the sentence names it.
        var withOp = AssistantNumbers.Worked(2400, 1600, "");
        withOp.Operations.Add((new[] { "heal_selection", "heal" }, new[] { 120.0 }));
        Assert.Empty(AssistantNumbers.Checked("Heal suits things up to 120 px.", new[] { "x" }, extra: withOp).Removed);
        Assert.NotEmpty(AssistantNumbers.Checked("Crop suits things up to 120 px.", new[] { "x" }, extra: withOp).Removed);
        Assert.Empty(AssistantNumbers.Worked(0, 0, "").Sizes);
    }
}

/// A plan runs at once only when the request names every step that changes the document.
public class PlanCheckTests
{
    private static TestStep Step(string op, params (string, string)[] p) => new(op, p);

    private static bool RunsAtOnce(string request, params TestStep[] steps) => PlanCheck.RunsAtOnce(steps, request);

    [Fact]
    public void WhatWasAskedForStillRunsAtOnce()
    {
        Assert.True(RunsAtOnce("Make it black and white", Step("add_adjustment", ("type", "black_white"))));
        Assert.True(RunsAtOnce("Gör bilden svartvit", Step("add_adjustment", ("type", "black_white"))));
        Assert.True(RunsAtOnce("Crop the image to 16:9", Step("crop", ("aspect", "16:9"))));
        Assert.True(RunsAtOnce("Add the text Hello at the top", Step("add_text", ("text", "Hello"), ("position", "top"))));
        Assert.True(RunsAtOnce("Improve the photo automatically", Step("auto_enhance")));
        Assert.True(RunsAtOnce("Ta bort bakgrunden", Step("remove_background")));
        Assert.True(RunsAtOnce("Gör bilden hälften så stor", Step("resize_image")));
        Assert.True(RunsAtOnce("Make the layer half transparent", Step("set_layer")));
        // Selecting changes nothing in the document, and several named steps are fine together.
        Assert.True(RunsAtOnce("Markera motivet", Step("select_subject")));
        Assert.True(RunsAtOnce("black and white with a vignette", Step("add_adjustment", ("type", "black_white")), Step("apply_filter", ("type", "vignette"))));
    }

    [Fact]
    public void WhatWasNotAskedForWaits()
    {
        Assert.False(RunsAtOnce("Write a poem about the sea", Step("add_text", ("text", "The sea is wide and deep"))));
        Assert.False(RunsAtOnce("Berätta ett skämt", Step("add_adjustment", ("type", "invert"))));
        Assert.False(RunsAtOnce("Thanks, that looks good", Step("crop", ("aspect", "1:1"))));
        // One step the request names and one it doesn't: the plan waits, and the unnamed one is told apart.
        var mixed = new[] { Step("add_adjustment", ("type", "black_white")), Step("auto_enhance") };
        Assert.False(PlanCheck.RunsAtOnce(mixed, "Make it black and white"));
        Assert.Equal(new[] { "auto_enhance" }, PlanCheck.Unnamed(mixed, "Make it black and white").Select(s => s.Op));
        // An operation Glimmer has no words for, and a step with nothing to go on: it asks.
        Assert.False(RunsAtOnce("do the new thing", Step("some_new_op")));
        Assert.False(RunsAtOnce("Make it black and white", Step("add_adjustment")));
        // A paid step waits anyway (this check adds nothing for it), and so does one with a stray parameter.
        Assert.True(RunsAtOnce("whatever", Step("generate_layer")));
        Assert.False(PlanCheck.RunsAtOnce(new[] { Step("crop", ("aspect", "1:1")) }, "crop it", planHasStrayParameters: true));
        Assert.False(PlanCheck.RunsAtOnce(new[] { Step("crop", ("aspect", "1:1")) }, "crop it", stepHasStray: _ => true));
    }

    [Fact]
    public void ASingleCommandIsNamedByItsTitleOrSearchWords()
    {
        Assert.True(PlanCheck.NamesCommand("flip the canvas horizontally", "Flip Canvas Horizontal", Array.Empty<string>()));
        Assert.True(PlanCheck.NamesCommand("fix the colors", "Auto Enhance", new[] { "fix", "improve" }));
        Assert.False(PlanCheck.NamesCommand("write a poem about the sea", "Flatten Image", new[] { "merge" }));
        Assert.False(PlanCheck.NamesCommand("tack, det ser bra ut", "Invert", Array.Empty<string>()));
        Assert.True(PlanCheck.Names("flip the canvas", Step("run_command", ("id", "image.flip")), id => id == "image.flip" ? "Flip Canvas Horizontal" : null));
        Assert.False(PlanCheck.Names("flip the canvas", Step("run_command", ("id", "other")), _ => null));
    }

    private static readonly string[] Adjustments = { "black_white", "brightness_contrast", "exposure", "levels", "curves", "hue_saturation", "vibrance", "color_balance", "photo_filter", "channel_mixer", "invert", "posterize", "threshold", "gradient_map" };
    private static readonly string[] Filters = { "gaussian_blur", "lens_blur", "sharpen", "vignette", "grain" };

    private static string? Complete(string op, string request, params (string, string)[] p)
    {
        var (steps, _) = PlanCheck.Completed<TestStep>(new[] { Step(op, p) }, request, Adjustments, Filters, s => s.StringParam("type")!, (s, kind) => Step(s.Op, ("type", kind)));
        return steps[0].StringParam("type");
    }

    [Fact]
    public void AMissingKindIsFilledInWhenTheRequestNamesExactlyOne()
    {
        Assert.Equal("black_white", Complete("add_adjustment", "Make it black and white"));
        Assert.Equal("black_white", Complete("add_adjustment", "Gör bilden svartvit"));
        Assert.Equal("black_white", Complete("add_adjustment", "Make it black and white", ("type", "monochrome")));   // a kind that doesn't exist
        Assert.Null(Complete("add_adjustment", "invert the colours"));        // "colour" names two more: nothing is guessed
        Assert.Equal("invert", Complete("add_adjustment", "invert it"));
        Assert.Equal("vignette", Complete("apply_filter", "add a vignette"));
        var (steps, filled) = PlanCheck.Completed<TestStep>(new[] { Step("add_adjustment") }, "Make it black and white", Adjustments, Filters, s => "", (s, kind) => Step(s.Op, ("type", kind)));
        Assert.Equal(1, filled);
        Assert.Equal("type taken from your request", PlanCheck.FilledNote);
        // The filled-in step is then one the request names, so the plan runs at once.
        Assert.True(PlanCheck.RunsAtOnce(steps, "Make it black and white"));
    }

    [Fact]
    public void NothingIsFilledInWhenTheRequestFitsSeveralKindsOrNone()
    {
        Assert.Null(Complete("add_adjustment", "make it brighter"));          // Brightness/Contrast and Exposure
        Assert.Null(Complete("add_adjustment", "make it warmer"));            // Color Balance and Photo Filter
        Assert.Null(Complete("apply_filter", "blur it"));                     // two kinds of blur
        Assert.Null(Complete("add_adjustment", "write a poem about the sea"));
        // A kind the model gave, and that exists, is never changed.
        Assert.Equal("levels", Complete("add_adjustment", "Make it black and white", ("type", "levels")));
        Assert.Null(Complete("crop", "Make it black and white"));
    }

    /// An unasked-for extra that stands by itself is held back, and what was asked runs.
    [Fact]
    public void AutoEnhanceBesideWhatWasAskedIsOfferedAndTheRestRuns()
    {
        var enhance = Step("auto_enhance");
        var padded = new[] { enhance, Step("add_adjustment", ("type", "black_white")) };
        var split = PlanCheck.WithoutExtras(padded, "Make it black and white")!.Value;
        Assert.Equal(new[] { "add_adjustment" }, split.Runs.Select(s => s.Op));
        Assert.Equal(new[] { "auto_enhance" }, split.Offered.Select(s => s.Op));
        Assert.True(PlanCheck.RunsAtOnce(split.Runs, "Make it black and white"));
    }

    [Fact]
    public void AnythingElseUnaskedKeepsTheWholePlanWaiting()
    {
        // Another unnamed step: no split.
        Assert.Null(PlanCheck.WithoutExtras(new[] { Step("crop", ("aspect", "1:1")), Step("add_adjustment", ("type", "black_white")) }, "Make it black and white"));
        // Only the extra, nothing that was asked: it waits as a plan, it is not run and not split.
        Assert.Null(PlanCheck.WithoutExtras(new[] { Step("auto_enhance") }, "Write a poem about the sea"));
        Assert.Null(PlanCheck.WithoutExtras(new[] { Step("auto_enhance"), Step("select_subject") }, "select the subject"));
        // A plan that is named throughout has nothing to split.
        Assert.Null(PlanCheck.WithoutExtras(new[] { Step("auto_enhance") }, "Improve the photo"));
    }
}

public class PlanClaimsTests
{
    private static IReadOnlyList<IPlanStep> Steps(params TestStep[] steps) => steps;

    [Fact]
    public void ASentenceThatNamesAnOperationBeyondTheStepsDoesNotStand()
    {
        var crop = Steps(new TestStep("crop", ("aspect", "16:9")));
        Assert.False(PlanClaims.ClaimsMore("Cropped to 16:9.", crop));
        Assert.True(PlanClaims.ClaimsMore("Cropped it and added a vignette.", crop));
        Assert.True(PlanClaims.ClaimsMore("Cropped it and removed the background.", crop));
        // Stems too common to mean an operation by themselves are ignored.
        Assert.False(PlanClaims.ClaimsMore("The layer is cropped and the image looks better.", crop));
        var bw = Steps(new TestStep("add_adjustment", ("type", "black_white")));
        Assert.False(PlanClaims.ClaimsMore("Made it black and white.", bw));
        Assert.True(PlanClaims.ClaimsMore("Made it black and white and sharpened it.", bw));
    }

    [Fact]
    public void ASentenceThatSaysTheWorkIsDoneDoesNotStandAboveStepsThatHaveNotRun()
    {
        Assert.True(PlanClaims.ClaimsCompletion("All done!"));
        Assert.True(PlanClaims.ClaimsCompletion("Klart!"));
        Assert.True(PlanClaims.ClaimsCompletion("I’ve cropped it."));
        Assert.True(PlanClaims.ClaimsCompletion("Jag har beskurit bilden."));
        Assert.False(PlanClaims.ClaimsCompletion("Cropped to 16:9."));
        Assert.False(PlanClaims.ClaimsCompletion("The undone steps are listed."));
    }

    [Fact]
    public void TheModelsSentenceStandsOnlyWhenItClaimsNothingBeyondTheSteps()
    {
        var crop = Steps(new TestStep("crop", ("aspect", "16:9")));
        Assert.Equal("Cropped to 16:9.", PlanClaims.Shown("Cropped to 16:9.", crop, madeUpSteps: false));
        Assert.Null(PlanClaims.Shown("All done!", crop, madeUpSteps: false));
        Assert.Null(PlanClaims.Shown("Cropped to 16:9.", Steps(), madeUpSteps: false));
        Assert.Null(PlanClaims.Shown("Cropped to 16:9.", crop, madeUpSteps: true));
        Assert.Null(PlanClaims.Shown(null, crop, madeUpSteps: false));
    }
}
