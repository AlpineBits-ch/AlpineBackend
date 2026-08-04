using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Echo.E2E.Tests.Support;

/// <summary>
/// A third-party website, for the unfurler to fetch.
///
/// <para>Link previews are the one feature whose input is a <b>foreign origin</b>, so a test that
/// stubbed the fetch would be testing everything except the part that is actually new. This serves
/// real HTTP on a real socket: real Open Graph tags, a real PNG, real <c>Cache-Control</c> - so the
/// spawned Unfurl process runs its genuine path end to end, right down to decoding the image and
/// storing it in MinIO.</para>
///
/// <para>It also records what it was asked for, which is how the tests assert two things no
/// response body could show: that the crawler User-Agent is sent (several large sites serve OG tags
/// only to recognised crawlers, so this is functional, not cosmetic), and that a second message
/// quoting the same URL is served from Redis instead of hitting the origin again.</para>
/// </summary>
internal sealed class StubOriginServer : IAsyncDisposable
{
    public const string ArticleTitle = "A Genuinely Interesting Article";
    public const string ArticleDescription = "Everything you never wanted to know about link previews.";
    public const string SiteName = "Stub Origin";

    public const int ImageWidth = 96;
    public const int ImageHeight = 64;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private readonly List<string> _requestedPaths = [];
    private readonly List<string?> _userAgents = [];
    private readonly Lock _sync = new();

    public string BaseUrl { get; }

    private StubOriginServer(TcpListener listener, string baseUrl)
    {
        _listener = listener;
        BaseUrl = baseUrl;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public static StubOriginServer Start()
    {
        // 127.0.0.1, not "localhost", and that is not a style choice.
        //
        // Markdown autolink detection requires a dot in the host, so a bare "localhost" URL is
        // never recognised as a link at all - LinkExtractor returns nothing for it and the preview
        // flow correctly does nothing. An earlier version of this stub served on localhost and
        // every positive test in this suite failed for that reason alone, which looked exactly like
        // a product bug and was not one.
        //
        // A raw TcpListener rather than HttpListener because HttpListener on Windows binds a
        // "localhost" prefix without an elevated URL ACL but generally will not bind an IP-literal
        // one - which is precisely the prefix now required. Speaking HTTP/1.1 by hand is a few
        // dozen lines and removes the platform question entirely.
        var port = Hosts.SpawnedServiceProcess.ReserveFreeTcpPort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        return new StubOriginServer(listener, $"http://127.0.0.1:{port}");
    }

    /// <summary>A page with a complete set of Open Graph tags and an image.</summary>
    public string ArticleUrl => $"{BaseUrl}/article";

    /// <summary>A page with no OG or Twitter tags at all - exercises the bare-HTML fallback.</summary>
    public string PlainUrl => $"{BaseUrl}/plain";

    /// <summary>A URL that answers 404, so a failed unfurl can be asserted on.</summary>
    public string MissingUrl => $"{BaseUrl}/gone";

    public IReadOnlyList<string> RequestedPaths
    {
        get { lock (_sync) return [.. _requestedPaths]; }
    }

    public IReadOnlyList<string?> UserAgents
    {
        get { lock (_sync) return [.. _userAgents]; }
    }

    public int CountRequestsFor(string path)
    {
        lock (_sync) return _requestedPaths.Count(p => p == path);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            }
            catch (Exception) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();

                var (path, userAgent) = await ReadRequestAsync(stream);
                if (path is null) return;

                lock (_sync)
                {
                    _requestedPaths.Add(path);
                    _userAgents.Add(userAgent);
                }

                switch (path)
                {
                    case "/article":
                        await WriteAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes($"""
                            <!DOCTYPE html>
                            <html><head>
                              <title>Ignored, because Open Graph wins</title>
                              <meta property="og:title" content="{ArticleTitle}">
                              <meta property="og:description" content="{ArticleDescription}">
                              <meta property="og:site_name" content="{SiteName}">
                              <meta property="og:type" content="article">
                              <meta property="og:image" content="{BaseUrl}/image.png">
                              <meta name="twitter:card" content="summary_large_image">
                            </head><body><p>Body text nobody should read.</p></body></html>
                            """));
                        return;

                    case "/plain":
                        await WriteAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes("""
                            <!DOCTYPE html>
                            <html><head>
                              <title>Just A Title</title>
                              <meta name="description" content="Just a description.">
                            </head><body></body></html>
                            """));
                        return;

                    case "/image.png":
                        await WriteAsync(stream, 200, "image/png", TinyPng.Create(ImageWidth, ImageHeight));
                        return;

                    default:
                        await WriteAsync(stream, 404, "text/plain", "not found"u8.ToArray());
                        return;
                }
            }
            catch (Exception)
            {
                // A client that went away mid-exchange is not a test failure.
            }
        }
    }

    /// <summary>Reads the request line and headers; returns the path and User-Agent.</summary>
    private static async Task<(string? Path, string? UserAgent)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var received = new StringBuilder();

        // Headers end at the blank line. Bounded by the buffer so a malformed request cannot spin.
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) return (null, null);

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (received.Length > 32 * 1024) return (null, null);
        }

        var lines = received.ToString().Split("\r\n");
        var target = lines[0].Split(' ') is [_, var t, ..] ? t : null;
        if (target is null) return (null, null);

        var userAgent = lines
            .Skip(1)
            .FirstOrDefault(l => l.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))
            ?["User-Agent:".Length..].Trim();

        // Strip any query string - every route here is path-only.
        var queryStart = target.IndexOf('?');
        return (queryStart >= 0 ? target[..queryStart] : target, userAgent);
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        var reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Unknown" };

        var headers =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            // A short max-age the unfurler is expected to clamp UP to its floor
            // (UNFURL_MIN_CACHE_TTL_MINUTES). Without that clamp a page declaring one second would
            // be re-fetched on every single mention.
            "Cache-Control: public, max-age=1\r\n" +
            // Closing per response keeps this server to one exchange per socket, so there is no
            // keep-alive state machine to get wrong.
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        try { _listener.Stop(); } catch (Exception) { /* already stopped */ }
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { /* best effort */ }
        _shutdown.Dispose();
    }
}
