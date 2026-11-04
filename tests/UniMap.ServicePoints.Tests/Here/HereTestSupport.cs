using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using UniMap.ServicePoints.API.Here;

namespace UniMap.ServicePoints.Tests.Here;

public static class Fixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Here", "Fixtures", name));
}

public record RecordedRequest(HttpMethod Method, Uri Uri, string? Body)
{
    /// <summary>Decoded so that literal and percent-encoded brackets compare the same.</summary>
    public string DecodedQuery => Uri.UnescapeDataString(Uri.Query);
}

/// <summary>Fake HERE: answers each attempt (1-based) from the supplied function and records every request.</summary>
public sealed class FakeHereHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
        return respond(Requests.Count, request);
    }
}

public sealed class RecordingDelay : IHereDelay
{
    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}

/// <summary>A real ILoggerProvider that keeps every formatted message, exception text included.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get { lock (_lines) return _lines.ToArray(); }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = $"{category} {logLevel}: {formatter(state, exception)} {exception}";
            lock (owner._lines) owner._lines.Add(line);
        }
    }
}
