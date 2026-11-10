using System.Net;
using System.Text.Json;

namespace Watchboard.Api.Services;

public class SourceException(string message) : Exception(message);
public sealed class SourceIdentityException(string message) : SourceException(message);
public sealed class SourceNotFoundException(string message) : SourceException(message);
public sealed class SourceRedirectException(string message) : SourceException(message);

public static class SourceFailures
{
    public static bool Is(Exception exception, CancellationToken token) => !token.IsCancellationRequested &&
        exception is SourceException or HttpRequestException or OperationCanceledException or JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException;
    public static string Message(Exception exception) => exception is SourceException ? exception.Message :
        exception is OperationCanceledException ? "La fuente tardó demasiado. Se conserva la última lectura." :
        "No se ha podido leer la fuente o su formato ha cambiado. Se conserva la última lectura.";
}

public sealed class SourceHttp(IHttpClientFactory factory, TimeProvider clock)
{
    private static readonly HashSet<string> Hosts = [
        "api.github.com", "registry.npmjs.org", "nodejs.org", "dotnetcli.blob.core.windows.net",
        "www.allkeyshop.com", "store.steampowered.com"
    ];
    private readonly SemaphoreSlim aksGate = new(1, 1);
    private DateTimeOffset nextAksRequest = DateTimeOffset.MinValue;

    public async Task<string> GetAsync(string url, CancellationToken token)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !Hosts.Contains(uri.Host))
            throw new SourceException("La fuente no está permitida.");
        if (uri.Host == "www.allkeyshop.com")
        {
            // robots.txt: only public /blog/ pages; at least five seconds between requests.
            if (!uri.AbsolutePath.StartsWith("/blog/buy-", StringComparison.Ordinal))
                throw new SourceException("Solo se pueden consultar páginas públicas de comparación.");
            await aksGate.WaitAsync(token);
            try
            {
                var delay = nextAksRequest - clock.GetUtcNow();
                if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, token);
                nextAksRequest = clock.GetUtcNow().AddSeconds(5);
                return await SendAsync(uri, token);
            }
            finally { aksGate.Release(); }
        }
        return await SendAsync(uri, token);
    }

    public async Task<JsonDocument> JsonAsync(string url, CancellationToken token) =>
        JsonDocument.Parse(await GetAsync(url, token), new JsonDocumentOptions { MaxDepth = 48 });

    private async Task<string> SendAsync(Uri uri, CancellationToken token)
    {
        // HttpClient.Timeout with ResponseHeadersRead stops covering the request after headers.
        // Keep the body download inside the same bounded cancellation budget as well.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var bounded = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await factory.CreateClient("sources").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            throw new SourceException("La fuente ha limitado o bloqueado la consulta. Se conserva la última lectura.");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new SourceNotFoundException("La publicación no existe o no tiene releases públicos. Revisa la fuente.");
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new SourceRedirectException("La fuente devuelve una redirección que no se seguirá. Revisa el enlace público canónico.");
        if (!response.IsSuccessStatusCode)
            throw new SourceException($"La fuente respondió con HTTP {(int)response.StatusCode}. Se conserva la última lectura.");
        const long maxBytes = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new SourceException("La respuesta de la fuente es demasiado grande.");
        await response.Content.LoadIntoBufferAsync(maxBytes, bounded);
        return await response.Content.ReadAsStringAsync(bounded);
    }
}
