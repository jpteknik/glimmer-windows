// Ported from Packages/GlimmerKit/Sources/GlimmerRender/MetalContext.swift
//
// The Mac's MetalContext (shared device, command queue and shader library) has no counterpart: everything renders on
// the CPU, and every renderer here is made with a plain constructor. What remains is the error type.

namespace Glimmer.Render;

/// <summary>Swift's <c>enum RenderError: Error</c>, as an exception.</summary>
public sealed class RenderError : Exception
{
    public enum ErrorKind
    {
        NoMetalDevice,
        LibraryUnavailable,
        ResourceCreationFailed,
    }

    public ErrorKind Kind { get; }

    /// <summary>The associated string (what could not be made or found); null for <see cref="ErrorKind.NoMetalDevice"/>.</summary>
    public string? Detail { get; }

    private RenderError(ErrorKind kind, string? detail)
        : base(detail is null ? Describe(kind) : $"{Describe(kind)}(\"{detail}\")")
    {
        Kind = kind;
        Detail = detail;
    }

    private static string Describe(ErrorKind kind) => kind switch
    {
        ErrorKind.NoMetalDevice => "noMetalDevice",
        ErrorKind.LibraryUnavailable => "libraryUnavailable",
        _ => "resourceCreationFailed",
    };

    public static RenderError NoMetalDevice() => new(ErrorKind.NoMetalDevice, null);
    public static RenderError LibraryUnavailable(string detail) => new(ErrorKind.LibraryUnavailable, detail);
    public static RenderError ResourceCreationFailed(string detail) => new(ErrorKind.ResourceCreationFailed, detail);
}
