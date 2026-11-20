// Ported from Packages/GlimmerKit/Sources/GlimmerAI/KeyStaysHome.swift
using System.Net;

namespace Glimmer.AI;

/// A request that carries an API key goes to the service's own host and nowhere else. If the
/// service answers with a redirect to another host, it is not followed: the redirect itself
/// becomes the answer ("<Service> answered HTTP 302"), and the key never leaves for an
/// address that came in a response. Used by every call that sends a key.
///
/// In Swift this is a URLSession task delegate; here it is a delegating handler that sits
/// between the HttpClient and the real handler (which must not follow redirects itself).
public sealed class KeyStaysHome : DelegatingHandler
{
    /// The most redirects one request follows, on the same host.
    private const int MaxRedirects = 10;

    public KeyStaysHome(HttpMessageHandler inner) : base(inner) { }

    /// An HttpClient whose redirects obey the rule. `handler` is the real transport (tests pass a
    /// fake one); without it the shared socket handler is used. The handler is never disposed here.
    public static HttpClient CreateClient(HttpMessageHandler? handler = null) =>
        new(new KeyStaysHome(handler ?? Http.SharedHandler), disposeHandler: false)
        {
            // Each request has its own timeout (`timeoutInterval`).
            Timeout = Timeout.InfiniteTimeSpan,
        };

    /// Whether a redirect from `original` to `next` stays on the same host over https (or stays on this machine).
    public static bool MayFollow(Uri? original, Uri? next)
    {
        if (original is null || next is null) return false;
        if (!original.IsAbsoluteUri || !next.IsAbsoluteUri) return false;
        var from = original.Host.ToLowerInvariant();
        var to = next.Host.ToLowerInvariant();
        if (from.Length == 0 || to.Length == 0 || from != to) return false;
        return string.Equals(next.Scheme, original.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var original = request.RequestUri;
        var current = request;
        byte[]? body = null;
        var bodyHeaders = new List<KeyValuePair<string, IEnumerable<string>>>();
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            bodyHeaders.AddRange(request.Content.Headers);
        }
        for (var hops = 0; ; hops++)
        {
            var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null || hops >= MaxRedirects)
                return response;
            var location = response.Headers.Location;
            var next = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (!MayFollow(original, next)) return response;   // the redirect itself is the answer
            response.Dispose();

            // As URLSession does: 301, 302 and 303 turn a POST into a GET; 307 and 308 keep method and body.
            var keepMethod = response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                || current.Method == HttpMethod.Get || current.Method == HttpMethod.Head;
            var followed = new HttpRequestMessage(keepMethod ? current.Method : HttpMethod.Get, next);
            foreach (var header in request.Headers) followed.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (keepMethod && body is not null)
            {
                var content = new ByteArrayContent(body);
                foreach (var header in bodyHeaders) content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                followed.Content = content;
            }
            if (!ReferenceEquals(current, request)) current.Dispose();
            current = followed;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// A link that came in a service's answer and is fetched without the key (a signed
    /// download): https with a host, nothing else. Never a file, never plain http.
    public static Uri? Fetchable(string? text)
    {
        if (text is null || !Uri.TryCreate(text, UriKind.Absolute, out var url)) return null;
        if (!string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase) || url.Host.Length == 0) return null;
        return url;
    }

    /// An identifier from a service's answer that goes into a path of a request with the key:
    /// letters, digits, "-" and "_" only, so it can't change where the request goes.
    public static string? PathSafe(string? id)
    {
        if (id is null || id.Length == 0 || id.Length > 200) return null;
        foreach (var c in id)
        {
            var ok = (c is >= 'a' and <= 'z') || (c is >= 'A' and <= 'Z') || (c is >= '0' and <= '9') || c == '-' || c == '_';
            if (!ok) return null;
        }
        return id;
    }
}
