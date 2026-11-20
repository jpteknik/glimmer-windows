// Ported from Packages/GlimmerKit/Sources/GlimmerAI/PaidPrice.swift
namespace Glimmer.AI;

/// What Glimmer knows about the price of a paid operation before it runs
/// (the-apps/SIBLINGS.md, "Okänt pris").
///
/// `allowPaid` does not mean "whatever it costs": a paid operation whose price is not known
/// is refused unless the call also says `allowUnknownPrice`, and in the window the person
/// ticks once per service that it states no price in advance.
///
/// The register is empty, and that is its content: no picture service Glimmer talks to states
/// the price of one picture job in a form Glimmer reads. OpenRouter's answer carries the charge
/// afterwards (`usage.cost`); the others' answers carry none. A price goes in here only with
/// its source and its date, never as an estimate.
public static class PaidPrice
{
    public sealed record Known(double Amount, string Currency, string Source, DateTimeOffset AsOf);

    /// The price of running `op` once on `service`, when the service has stated it. Null: not known.
    public static Known? KnownPrice(string op, string service) => null;

    /// Whether every paid operation on `service` has a stated price.
    public static bool KnownInAdvance(string service) => false;

    public const string Reason = "unknown_price";
    public const string ConsentParameter = "allowUnknownPrice";
}
