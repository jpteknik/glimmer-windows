// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/StepParametersTests.swift (WorkedFiguresTests, FiguresInWordsTests, PlanClaimsTests)
using Glimmer.AI;

namespace Glimmer.AI.Tests;

/// <summary>
/// Figures a capable model works out from what it was given are not taken for invented ones
/// (the first real model, Berget, 2026-11-16: five cases), and an invented figure beside each still goes.
/// </summary>
public class WorkedFiguresTests
{
    static IReadOnlyList<string> Check(string text, string request = "Crop the image to 16:9")
    {
        var extra = AssistantNumbers.Worked(2400, 1600, request);
        extra.Operations = AgentPlanner.OperationFigures.ToList();
        return AssistantNumbers.Checked(text, [request, "2400 1600 1", "Photo"], steps: 1, extra: extra).Removed;
    }

    [Fact]
    public void TheOtherSideOfANamedAspectIsGiven()
    {
        Assert.Empty(Check("Cropped to 16:9, which is 2400 × 1350 pixels, centred."));
        // An invented size beside it goes, and it is a number, not a price ("centred" has "cent" in it).
        Assert.Equal(["number 1777"], Check("Cropped to 16:9, which is 2400 × 1350 pixels, centred. The result is 1777 pixels wide."));
        Assert.Equal(["number 1280"], Check("Cropped to 2400 × 1280 pixels, centred."));
    }

    [Fact]
    public void HalvesAndDoublesAreGiven()
    {
        var request = "Gör bilden hälften så stor";
        Assert.Empty(Check("Bilden blir 1200 × 800 pixlar.", request));
        Assert.Empty(Check("Dubbla storleken är 4800 × 3200.", request));
        Assert.Equal(["number 850"], Check("Bilden blir 1200 × 850 pixlar.", request));
    }

    [Fact]
    public void TheSizeAsARatioAndItsMegapixelsAreGiven()
    {
        var request = "How big is this document, in pixels?";
        Assert.Empty(Check("It is 2400 × 1600 pixels, a 3:2 image of 3.84 megapixels.", request));
        Assert.Empty(Check("It is about 3.8 megapixels.", request));
        // A wrong shape for the person's own document goes: neither the megapixels nor an operation's figures excuse "4:3".
        Assert.Equal(["number 4", "number 3"], Check("It is a 4:3 image.", request));
        Assert.Empty(Check("It is a 3:2 image.", request));
        Assert.Empty(Check("It has 4 megapixels.", request));         // 3.84, said round
        Assert.Equal(["number 4"], Check("It is 4 wide.", request));   // not megapixels, not a size
        Assert.Equal(["number 5.1"], Check("It is 5.1 megapixels.", request));
    }

    [Fact]
    public void TheOperationsOwnFiguresAreGiven()
    {
        var request = "Hur många lager har dokumentet och vad kostar det att förstora det?";
        // 2× and 4× are in the upscale operation's own description, which the model was sent.
        Assert.Empty(Check("Dokumentet har 1 lager. Du kan förstora det 2× eller 4×.", request));
        Assert.Equal(["number 7"], Check("Du kan förstora det 7×.", request));
    }

    [Fact]
    public void APercentageNamedInTheRequestIsGiven()
    {
        Assert.Empty(Check("At 25 % it is 600 × 400.", "Scale it to 25 %"));
        Assert.Equal(["number 640"], Check("At 25 % it is 640 × 400.", "Scale it to 25 %"));
    }

    /// <summary>Held whatever the arithmetic: money, time, sizes on disk, quality, a service.</summary>
    [Fact]
    public void MoneyTimeFileSizeQualityAndServicesKeepOnlyWhatWasGiven()
    {
        var request = "Make it half as large and tell me the cost";
        // 2 is an operation's own figure and 1200 a worked one; neither backs a sentence about money, time or a file's size.
        Assert.Equal(["price 2"], Check("That costs 2 credits.", request));
        Assert.Equal(["number 4"], Check("It will take 4 seconds.", request));
        Assert.Equal(["number 1200"], Check("The file will be about 1200 kB.", request));
        Assert.Equal(["number 2"], Check("Berget charges 2 per request.", request));
        Assert.Equal(["number 4"], Check("Quality 4 is best.", request));
        // The same figures in a sentence about the picture stay.
        Assert.Empty(Check("It becomes 1200 × 800 pixels.", request));
        // A price is called a price only with a currency or the word.
        Assert.Equal(["number 999"], Check("The centre is at 999.", request));
        Assert.Equal(["price 3"], Check("It is €3.", request));
    }

    /// <summary>An operation's own figures count where the sentence names the operation or its subject, and nowhere else.</summary>
    [Fact]
    public void AnOperationsFiguresCountOnlyWhereItIsNamed()
    {
        var request = "Make it better";
        Assert.Empty(Check("I can upscale it 2× or 4×.", request));
        Assert.Empty(Check("Du kan förstora det 4×.", request));
        Assert.Empty(Check("An exposure of +3 would brighten it.", request));        // within the exposure adjustment's own range
        Assert.Equal(["number 3"], Check("The crop moves it by +3.", request));      // crop has no such figure
        Assert.Equal(["number 4"], Check("There are 4 of them.", request));          // names nothing
        // Free as before: 0, 1 and the count of the plan's steps, as in "1 step" or a numbered list.
        Assert.Empty(Check("1. Crop it. Then 1 more thing.", request));
        Assert.Empty(AssistantNumbers.Checked("Step 1: crop. Step 2: blur. That is 2 steps.", [request], steps: 2).Removed);
        Assert.Equal(["number 3"], AssistantNumbers.Checked("Step 3 comes later.", [request], steps: 2).Removed);
    }

    [Fact]
    public void TheSetIsClosed()
    {
        var worked = AssistantNumbers.Worked(2400, 1600, "Crop to 16:9 and to a square");
        Assert.True(worked.Sizes.ToHashSet().IsSupersetOf([1200, 800, 4800, 3200, 9600, 6400, 1350, 1600]));
        Assert.True(worked.Megapixels.SequenceEqual([4, 3.8, 3.84]) && worked.Ratio is (3, 2));
        Assert.True(!worked.Sizes.Contains(1777) && !worked.Sizes.Contains(1280) && worked.Sizes.Count < 20);
        Assert.Empty(AssistantNumbers.Worked(0, 0, "x").Sizes);
    }
}

/// <summary>A figure the request gives in words backs the same figure in digits in the answer.</summary>
public class FiguresInWordsTests
{
    [Fact]
    public void WordsAreReadInEnglishAndSwedish()
    {
        Assert.Equal([1.5], AssistantNumbers.NumbersInWords("one and a half seconds"));
        Assert.Equal([18.0], AssistantNumbers.NumbersInWords("minus arton"));
        Assert.Equal([25.0, 250.0], AssistantNumbers.NumbersInWords("twenty-five percent, then two hundred and fifty"));
        Assert.Equal([25.0, 200.0], AssistantNumbers.NumbersInWords("tjugofem procent och tvåhundra pixlar"));
        Assert.Equal([1.5, 3000.0], AssistantNumbers.NumbersInWords("en och en halv gång, tre tusen"));
        Assert.Equal([0.5, 0.25, 2.0], AssistantNumbers.NumbersInWords("half as large, a quarter, double it"));
        Assert.Empty(AssistantNumbers.NumbersInWords("Remove the background"));
    }

    [Fact]
    public void AFigureGivenInWordsBacksTheDigits()
    {
        static IReadOnlyList<string> Check(string text, string request) => AssistantNumbers.Checked(text, [request, "2400 1600 1"], steps: 1).Removed;
        Assert.Empty(Check("Blurred with a radius of 18.", "Blur it by eighteen"));
        Assert.Empty(Check("Oskärpa med radie 18.", "Gör den suddig, radie arton"));
        Assert.Empty(Check("Rotated 1.5 degrees.", "Rotate it one and a half degrees"));
        Assert.Empty(Check("Scaled to 25 %.", "Scale it to a quarter"));        // a share may be said as a percentage
        // A figure nobody gave still goes, in words or not.
        Assert.Equal(["number 19"], Check("Blurred with a radius of 19.", "Blur it by eighteen"));
        Assert.Equal(["number 2.5"], Check("Rotated 2.5 degrees.", "Rotate it one and a half degrees"));
    }
}

/// <summary>A sentence saying something was done must not stand when it was not.</summary>
public class PlanClaimsOverStepsTests
{
    static AgentStep Step(string op, params (string, AgentValue)[] p) => new(op, p);
    static IReadOnlyList<IPlanStep> Steps(params AgentStep[] s) => s;

    [Fact]
    public void ASentenceStandsOnlyAboveTheStepsItNames()
    {
        var crop = Step("crop", ("aspect", "16:9"));
        Assert.Equal("Cropped to 16:9.", PlanClaims.Shown("Cropped to 16:9.", Steps(crop), madeUpSteps: false));
        Assert.NotNull(PlanClaims.Shown("Beskär bilden till 16:9.", Steps(crop), madeUpSteps: false));
        // It names something no step does.
        Assert.Null(PlanClaims.Shown("Cropped to 16:9 and removed the background.", Steps(crop), madeUpSteps: false));
        Assert.Null(PlanClaims.Shown("Beskuren, och bakgrunden är borttagen.", Steps(crop), madeUpSteps: false));
        Assert.Null(PlanClaims.Shown("Cropped and sharpened.", Steps(crop), madeUpSteps: false));
        // With both steps it stands.
        var sharpen = Step("apply_filter", ("type", "sharpen"));
        Assert.NotNull(PlanClaims.Shown("Cropped and sharpened.", Steps(crop, sharpen), madeUpSteps: false));
        // A kind of adjustment other than the one planned.
        var bw = Step("add_adjustment", ("type", "black_white"));
        Assert.NotNull(PlanClaims.Shown("Made it black and white.", Steps(bw), madeUpSteps: false));
        Assert.Null(PlanClaims.Shown("Made it black and white with a vignette.", Steps(bw), madeUpSteps: false));
        // Words too common to mean an operation don't take a sentence down.
        Assert.NotNull(PlanClaims.Shown("The background is removed; the layer keeps its size.", Steps(Step("remove_background")), madeUpSteps: false));
    }

    [Fact]
    public void ASentenceThatSaysItIsFinishedDoesNotStandAboveSteps()
    {
        var crop = Step("crop", ("aspect", "16:9"));
        foreach (var said in new[] { "All done!", "Done. Cropped to 16:9.", "I have cropped it to 16:9.", "I've cropped it.", "Klart!", "Jag har beskurit bilden.", "Beskärningen är färdig.", "Finished cropping." })
            Assert.Null(PlanClaims.Shown(said, Steps(crop), madeUpSteps: false));
        // Not completion words inside other words, and a plain statement of the plan stands.
        foreach (var said in new[] { "Cropping to 16:9.", "Beskär till 16:9, klarare blir det inte.", "Abandoned edges are cropped away." })
            Assert.Equal(said, PlanClaims.Shown(said, Steps(crop), madeUpSteps: false));
    }

    [Fact]
    public void NothingStandsAboveNoStepsOrMadeUpSteps()
    {
        Assert.Null(PlanClaims.Shown("VCA Voices removed.", Steps(), madeUpSteps: false));
        Assert.Null(PlanClaims.Shown("Cropped to 16:9.", Steps(Step("crop")), madeUpSteps: true));
        Assert.Null(PlanClaims.Shown(null, Steps(Step("crop")), madeUpSteps: false));
    }
}
