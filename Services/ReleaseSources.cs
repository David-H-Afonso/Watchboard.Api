using Watchboard.Api.Contracts;
using Watchboard.Api.Models;

namespace Watchboard.Api.Services;

public sealed class ReleaseSources(SourceHttp http)
{
    public async Task<ReleaseResult> ReadAsync(SoftwareWatch watch, CancellationToken token)
    {
        switch (watch.Provider)
        {
            case "github":
            {
                var source = WatchValidation.RepositorySource(watch.Source) ?? throw new SourceException("Repositorio de GitHub no permitido.");
                using var json = await http.JsonAsync($"https://api.github.com/repos/{source}/releases/latest", token);
                var root = json.RootElement;
                if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                    throw new SourceException("No se ha encontrado una publicación estable.");
                var version = root.GetProperty("tag_name").GetString();
                if (!WatchValidation.Text(version, 100)) throw new SourceException("La publicación no tiene una versión válida.");
                // Construct a known-origin link instead of trusting arbitrary provider-returned URLs.
                DateTimeOffset? publishedAt = root.TryGetProperty("published_at", out var date) && date.ValueKind == System.Text.Json.JsonValueKind.String &&
                    date.TryGetDateTimeOffset(out var published) ? published.ToUniversalTime() : null;
                return new(version!, $"https://github.com/{source}/releases/tag/{Uri.EscapeDataString(version!)}", publishedAt);
            }
            case "github-tags":
            {
                var source = WatchValidation.RepositorySource(watch.Source) ?? throw new SourceException("Repositorio de GitHub no permitido.");
                using var json = await http.JsonAsync($"https://api.github.com/repos/{source}/tags?per_page=30", token);
                var versions = json.RootElement.EnumerateArray().Select(x => x.GetProperty("name").GetString())
                    .OfType<string>().Where(x => !x.Contains('-') && VersionComparison.Compare(x, x) == "current").ToList();
                if (versions.Count == 0) throw new SourceException("No se han encontrado tags de versión estables.");
                var version = versions.Aggregate((a, b) => VersionComparison.Compare(a, b) == "update" ? b : a);
                return new(version, $"https://github.com/{source}/releases/tag/{Uri.EscapeDataString(version)}");
            }
            case "npm":
            {
                using var json = await http.JsonAsync($"https://registry.npmjs.org/{Uri.EscapeDataString(watch.Source)}/latest", token);
                var version = json.RootElement.GetProperty("version").GetString();
                if (!WatchValidation.Text(version, 100)) throw new SourceException("El paquete no tiene una versión válida.");
                return new(version!, $"https://www.npmjs.com/package/{watch.Source}");
            }
            case "node-lts":
            {
                using var json = await http.JsonAsync("https://nodejs.org/dist/index.json", token);
                var lts = json.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("lts").ValueKind == System.Text.Json.JsonValueKind.String);
                if (lts.ValueKind == System.Text.Json.JsonValueKind.Undefined) throw new SourceException("No se ha encontrado una versión LTS de Node.js.");
                return new(lts.GetProperty("version").GetString()!, "https://nodejs.org/en/download");
            }
            case "dotnet":
            {
                using var json = await http.JsonAsync("https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json", token);
                return new(json.RootElement.GetProperty("latest-sdk").GetString()!, "https://dotnet.microsoft.com/download/dotnet/10.0");
            }
            default: throw new SourceException("Proveedor de versiones no compatible.");
        }
    }

    public static string PublicUrl(SoftwareWatch watch) => watch.Provider switch
    {
        "github" or "github-tags" => $"https://github.com/{watch.Source}/releases",
        "npm" => $"https://www.npmjs.com/package/{watch.Source}",
        "node-lts" => "https://nodejs.org/en/download",
        "dotnet" => "https://dotnet.microsoft.com/download/dotnet/10.0",
        _ => ""
    };
}
