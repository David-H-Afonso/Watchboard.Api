using System.Security.Claims;
using System.Text.Json;
using Watchboard.Api.Contracts;
using Watchboard.Api.Services;
using static Watchboard.Api.Endpoints.EndpointSupport;

namespace Watchboard.Api.Endpoints;

public static class ConfigurationEndpoints
{
    public static void MapConfiguration(this WebApplication app)
    {
        var group = app.MapGroup("/api/configuration").RequireAuthorization();
        group.MapGet("", async (ClaimsPrincipal user, ConfigurationService config, CancellationToken token) =>
            Results.Ok(await config.ExportAsync(user.UserId(), token)));
        group.MapPost("/preview", async (HttpRequest request, ClaimsPrincipal user, ConfigurationService config, CancellationToken token) =>
        {
            var (document, error) = await ReadDocument(request, token);
            if (error is not null) return error;
            try { return Results.Ok(await config.PreviewAsync(user.UserId(), user.IsInRole("admin"), document!, token)); }
            catch (ConfigValidationException exception) { return Problem(exception.Message); }
        }).RequireRateLimiting("writes");
        group.MapPost("/import", async (HttpRequest request, ClaimsPrincipal user, ConfigurationService config,
            OperationGate gate, CancellationToken token) =>
        {
            var (document, error) = await ReadDocument(request, token);
            if (error is not null) return error;
            return await Mutate(gate, token, async () =>
            {
                try { return Results.Ok(await config.ImportAsync(user.UserId(), user.IsInRole("admin"), document!, token)); }
                catch (ConfigValidationException exception) { return Problem(exception.Message); }
            });
        }).RequireRateLimiting("writes");
    }

    private static async Task<(ConfigDocument? Document, IResult? Error)> ReadDocument(HttpRequest request, CancellationToken token)
    {
        if (!request.HasJsonContentType()) return (null, Problem("Usa un documento application/json.", 415));
        try
        {
            var document = await request.ReadFromJsonAsync<ConfigDocument>(token);
            return document is null ? (null, Problem("El documento JSON no puede ser null.")) : (document, null);
        }
        catch (JsonException)
        {
            return (null, Problem("JSON de configuración inválido. Revisa tipos/formato y elimina campos extra: no se admiten IDs, propietarios, credenciales ni observaciones."));
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        { return (null, Problem("El documento supera el límite de 256 KiB.", 413)); }
    }
}
