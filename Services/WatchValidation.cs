using System.Text.RegularExpressions;
using Watchboard.Api.Contracts;

namespace Watchboard.Api.Services;

public static partial class WatchValidation
{
    public static string? Software(CreateSoftware value)
    {
        if (!Text(value.Name, 100)) return "Escribe un nombre de hasta 100 caracteres.";
        if (value.Group is not ("pc" or "server" or "project")) return "Elige equipo, servidor o proyecto.";
        if (value.Provider == "github" && RepositorySource(value.Source) is not null) { }
        else if (value.Provider == "npm" && Package().IsMatch(value.Source?.Trim() ?? "")) { }
        else return "Usa un repositorio owner/repo de GitHub o un nombre de paquete npm válido.";
        if (!Version(value.InstalledVersion)) return "La versión admite hasta 100 caracteres, sin saltos de línea.";
        return null;
    }

    public static string? Game(CreateGame value)
    {
        if (!string.IsNullOrEmpty(value.Name) && !Text(value.Name, 150)) return "Escribe un nombre de hasta 150 caracteres o deja el campo vacío para resolver el enlace.";
        if (GameUrl(value.SourceUrl) is null) return "Usa una página de comparación PC de AllKeyShop o una página /app/ de Steam (HTTPS).";
        if (!Text(value.Edition, 60)) return "Indica la edición (por ejemplo Standard).";
        if (value.Region is not ("eu-global" or "global")) return "Elige Europa + Global o solo Global.";
        if (value.Payment is not ("best" or "card" or "paypal")) return "Elige el mejor total publicado, tarjeta o PayPal para incluir las comisiones.";
        if (!Target(value.TargetCents)) return "El precio objetivo debe estar entre 0 y 10.000 €.";
        if (GameUrl(value.SourceUrl)!.Value.Provider == "steam" &&
            (!string.Equals(value.Edition.Trim(), "Standard", StringComparison.OrdinalIgnoreCase) || value.Payment is not ("card" or "best") || value.Region != "eu-global"))
            return "Steam consulta su edición base en España; utiliza Standard, Europa + Global y tarjeta.";
        return null;
    }

    public static (string Provider, string Url, string? AppId)? GameUrl(string? value)
    {
        if (value is not null && (value.Trim().StartsWith("www.allkeyshop.com/", StringComparison.OrdinalIgnoreCase) ||
            value.Trim().StartsWith("allkeyshop.com/", StringComparison.OrdinalIgnoreCase) || value.Trim().StartsWith("store.steampowered.com/", StringComparison.OrdinalIgnoreCase))) value = "https://" + value.Trim();
        if (value is null || value.Length > 1000 || value.Any(char.IsControl) || value.Contains('\\') ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            return null;
        if ((uri.Host is "www.allkeyshop.com" or "allkeyshop.com") && AksPath().IsMatch(uri.AbsolutePath))
            return ("allkeyshop", $"https://www.allkeyshop.com{uri.AbsolutePath.TrimEnd('/')}/", null);
        var match = SteamPath().Match(uri.AbsolutePath);
        if (uri.Host == "store.steampowered.com" && match.Success)
            return ("steam", $"https://store.steampowered.com/app/{match.Groups[1].Value}/", match.Groups[1].Value);
        return null;
    }

    public static string? RepositorySource(string? value)
    {
        if (value is null || value.Length > 500 || value.Any(char.IsControl) || value.Contains('\\')) return null;
        value = value.Trim();
        if (value.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)) value = "https://" + value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
            var path = uri.AbsolutePath.Trim('/').Split('/');
            if (path.Length < 2 || path.Length > 2 &&
                !(path[2] == "releases" && (path.Length == 3 || path.Length == 4 && path[3] == "latest" ||
                    path.Length >= 5 && path[3] == "tag" && path.Skip(4).All(x => x.Length > 0)))) return null;
            value = $"{path[0]}/{path[1]}";
        }
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        if (!Repository().IsMatch(value) || value.Split('/').Any(x => x is "." or "..")) return null;
        // Verified same repository (GitHub ID 1433671132), not a general redirect exception.
        if (value.Equals("mamesj/pokemon-ds-widescreen", StringComparison.OrdinalIgnoreCase)) return "mamesj/pokemon-fulldex";
        return value.ToLowerInvariant();
    }

    public static bool ConfigSource(SoftwareConfig value)
    {
        var sourceValid = value.Provider switch
        {
            "github" or "github-tags" => RepositorySource(value.Source) is not null,
            "npm" => Package().IsMatch(value.Source ?? ""),
            "node-lts" => value.Source == "lts",
            "dotnet" => value.Source == "10.0",
            _ => false
        };
        if (!sourceValid) return false;
        if (value.LocalTool is null) return true;
        var source = value.Provider is "github" or "github-tags" ? RepositorySource(value.Source) : value.Source;
        return (value.LocalTool, value.Provider, source) switch
        {
            ("node", "node-lts", "lts") or ("dotnet", "dotnet", "10.0") or
            ("git", "github", "git-for-windows/git") or ("git", "github-tags", "git/git") or
            ("opencode", "github", "anomalyco/opencode") => true,
            _ => false
        };
    }

    public static string SoftwareSource(string provider, string source) => provider is "github" or "github-tags"
        ? RepositorySource(source)! : source.Trim();

    public static bool Preferences(WidgetPreferences? value) => value is { MaxItems: >= 1 and <= 30 } &&
        (value.SoftwareLocationIds is null || value.SoftwareLocationIds.Count <= 100 &&
            value.SoftwareLocationIds.All(x => x != Guid.Empty) && value.SoftwareLocationIds.Distinct().Count() == value.SoftwareLocationIds.Count);

    public static bool Target(int? value) => value is null or >= 0 and <= 1_000_000;
    public static bool Version(string? value) => value is null || value.Length <= 100 && !value.Any(char.IsControl);
    public static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$")]
    private static partial Regex Repository();
    [GeneratedRegex(@"^(?:@[a-z0-9][a-z0-9._-]{0,99}/)?[a-z0-9][a-z0-9._-]{0,99}$")]
    private static partial Regex Package();
    [GeneratedRegex(@"^/blog/buy-[a-z0-9-]+-(?:cd-key|key)-compare-prices/?$")]
    private static partial Regex AksPath();
    [GeneratedRegex(@"^/app/([1-9][0-9]{0,9})(?:/[A-Za-z0-9_-]+)?/?$")]
    private static partial Regex SteamPath();
}
