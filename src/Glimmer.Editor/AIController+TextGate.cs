// Ported from App/AIController+TextGate.swift (the keys, the notice text and the price helpers are in AISetup.cs, which
// Settings › AI shares)
using Glimmer.AI;

namespace Glimmer.Editor;

/// <summary>What stands between a text request and the service it goes to: the first-use notice per cloud service, and
/// the refusal to send a picture to a model that takes none.</summary>
public sealed partial class AIController
{
    /// <summary>Runs <paramref name="request"/> if it may go out now. Otherwise it puts up the notice that explains why not
    /// (a model that takes no pictures) or asks first (the first request to a cloud service), and the request runs when the
    /// person says Send.</summary>
    internal void GateTextRequest(bool needsPicture, Action request)
    {
        var provider = setup.TextProvider;
        var chosen = setup.ChosenTextModel(provider);
        if (needsPicture && chosen.Vision == false)
        {
            session.TextNotice = new TextNotice(TextNoticeKind.CantSee, "This model can't look at pictures",
                new UiText("{0} lists {1} as a model without picture input, and this needs to see your document. Nothing was sent. Choose a model that takes pictures in Settings › AI.",
                           provider.Name, chosen.Id), null);
            return;
        }
        // Without a key or a model there is nothing to ask about yet: the request itself says what is missing.
        bool sendsPictures = chosen.Vision != false;   // the same rule as the request itself (AIController+Agent)
        // Then the price: known from the service's own list, or the person's once-per-service word that it is not.
        void Priced() => GateTextPrice(provider, chosen.Id, request);
        if (!HasTextModel || setup.HasBeenTold(provider, sendsPictures))
        {
            Priced();
            return;
        }
        session.TextNotice = new TextNotice(TextNoticeKind.FirstUse, new UiText("Send this to {0}?", provider.Name),
            AISetup.Sentences([AISetup.TextNoticeText(provider, sendsPictures), "You can read this again in Settings › AI."]),
            () =>
            {
                setup.MarkTold(provider, sendsPictures);
                Priced();
            });
    }

    private void GateTextPrice(AIProvider provider, string model, Action request)
    {
        if (!HasTextModel || setup.TextPriceSettled(provider))
        {
            request();
            return;
        }
        // After the alert before this one (if any) has gone: it clears the notice as it closes.
        Scheduler.Schedule(TimeSpan.FromMilliseconds(450), () =>
        {
            session.TextNotice = new TextNotice(TextNoticeKind.UnknownPrice, new UiText("{0} states no price in advance", provider.Name),
                new UiText("Glimmer has read no price for {0} from {1}'s list, so it cannot say what a request costs. {1} bills your key for each request. You are asked this once for {1}; it is kept in Settings › AI, where you can take it back.",
                           model, provider.Name),
                () =>
                {
                    session.Environment.Settings.Set(AISetup.TextUnknownPriceKey(provider.Slug), true);
                    request();
                });
        });
    }

    /// <summary>The line the Assistant shows about what a request costs; null when nothing is billed.</summary>
    public UiText? TextPriceLine => setup.TextPriceLine(setup.TextProvider);
}
