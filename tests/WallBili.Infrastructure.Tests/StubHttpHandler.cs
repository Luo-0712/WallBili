using System.Net;
using System.Text;

namespace WallBili.Infrastructure.Tests;

internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, bool initializeVisitor = true) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RequestUri!);
        if (initializeVisitor && request.RequestUri!.AbsolutePath == "/x/frontend/finger/spi")
            return Task.FromResult(Json("""{"code":0,"data":{"b_3":"visitor-three","b_4":"visitor-four"}}"""));
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    public static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
            pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty);
}
