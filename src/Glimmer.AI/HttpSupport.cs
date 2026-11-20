// New in the port: no counterpart in the original. What URLRequest / URLSession gave the Swift
// code for free (a request value, a per-request timeout, `URL.appending(path:)`, lines of a
// byte stream) is here, so the adapters read like the originals.
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Glimmer.AI;

/// A request as a value (Swift's `URLRequest`): it can be sent again, which an `HttpRequestMessage` cannot.
internal sealed class RequestSpec
{
    public Uri Url { get; }
    public string Method { get; }
    public List<KeyValuePair<string, string>> Headers { get; } = new();
    public byte[]? Body { get; set; }
    public string? ContentType { get; set; }
    /// `timeoutInterval`.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    public RequestSpec(Uri url, string method = "GET")
    {
        Url = url;
        Method = method;
    }

    public void SetHeader(string name, string value)
    {
        Headers.RemoveAll(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));
        Headers.Add(new(name, value));
    }

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public HttpRequestMessage ToMessage()
    {
        var message = new HttpRequestMessage(new HttpMethod(Method), Url);
        foreach (var header in Headers) message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (Body is not null)
        {
            var content = new ByteArrayContent(Body);
            // Exactly "application/json": no charset parameter.
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType ?? "application/json");
            message.Content = content;
        }
        return message;
    }
}

/// What came back, whole.
internal sealed class HttpData
{
    public int Status { get; }
    public byte[] Body { get; }
    private readonly Func<string, string?> header;

    public HttpData(int status, byte[] body, Func<string, string?> header)
    {
        Status = status;
        Body = body;
        this.header = header;
    }

    public string? Header(string name) => header(name);
}

internal static class Http
{
    /// `url.appending(path:)`: one slash between the base and the path.
    public static Uri Append(Uri baseUri, string path)
    {
        var builder = new UriBuilder(baseUri);
        builder.Path = builder.Path.TrimEnd('/') + "/" + path.TrimStart('/');
        return builder.Uri;
    }

    /// `url.lastPathComponent`.
    public static string LastPathComponent(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// `url.deletingLastPathComponent()`.
    public static Uri DeletingLastPathComponent(Uri uri)
    {
        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        builder.Path = slash <= 0 ? "/" : path[..slash] + "/";
        return builder.Uri;
    }

    private static CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    /// `session.data(for: request)`: the whole answer. The timeout is `timeoutInterval`; a timeout
    /// surfaces as a `TimeoutException`, a cancel by the caller stays a cancel.
    public static async Task<HttpData> DataAsync(HttpClient client, RequestSpec spec, CancellationToken ct)
    {
        using var cts = Linked(ct, spec.Timeout);
        try
        {
            using var message = spec.ToMessage();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in response.Headers.NonValidated) headers[pair.Key] = pair.Value.ToString();
            return new HttpData((int)response.StatusCode, body, name => headers.TryGetValue(name, out var value) ? value : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The request timed out.");
        }
    }

    /// `session.bytes(for: request)`: the headers, and the body as a stream. The timeout covers the wait for the headers.
    public static async Task<HttpResponseMessage> StreamAsync(HttpClient client, RequestSpec spec, CancellationToken ct)
    {
        using var cts = Linked(ct, spec.Timeout);
        try
        {
            using var message = spec.ToMessage();
            return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The request timed out.");
        }
    }

    /// `bytes.lines`.
    /// <paramref name="idleTimeout"/> is the stream's own `timeoutInterval`: URLSession gives up on a response that sends no bytes
    /// for that long, however long the whole answer would have taken; a stalled stream surfaces as a <see cref="TimeoutException"/>.
    public static async IAsyncEnumerable<string> LinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct, TimeSpan? idleTimeout = null)
    {
        if (idleTimeout is { } idle) stream = new IdleTimeoutStream(stream, idle);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) yield break;
            yield return line;
        }
    }

    /// Whether `error` is a failure of the connection (not a cancel the caller asked for).
    public static bool IsConnectionFailure(Exception error, CancellationToken ct) =>
        !(error is OperationCanceledException && ct.IsCancellationRequested);

    /// The system's own description of a failure, as `localizedDescription` gave it.
    public static string Describe(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException s) return s.Message;
        }
        return error.Message;
    }

    /// The HTTP client every keyed call goes through when the caller gives no handler of its own.
    private static readonly Lazy<HttpMessageHandler> SharedInner = new(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    internal static HttpMessageHandler SharedHandler => SharedInner.Value;
}

/// <summary>
/// Reads from the inner stream, but gives up when no bytes arrive for <c>idle</c> (Swift's <c>timeoutInterval</c> of a
/// URLSession request, which counts from the last byte, not from the start). The timer starts again with every read.
/// </summary>
internal sealed class IdleTimeoutStream : Stream
{
    private readonly Stream inner;
    private readonly TimeSpan idle;

    public IdleTimeoutStream(Stream inner, TimeSpan idle)
    {
        this.inner = inner;
        this.idle = idle;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(idle);
        try
        {
            return await inner.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The request timed out.");
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
