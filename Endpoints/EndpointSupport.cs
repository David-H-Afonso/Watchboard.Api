using Watchboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace Watchboard.Api.Endpoints;

internal static class EndpointSupport
{
    public static IResult Problem(string detail, int status = 400) => Results.Problem(statusCode: status,
        title: status == 409 ? "Operación en conflicto" : "Revisa los datos", detail: detail);
    public static IResult Busy() => Problem("Hay otra escritura en curso. Vuelve a intentarlo en unos segundos; no hace falta esperar a las fuentes.", 409);
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static async Task<IResult> Mutate(OperationGate gate, CancellationToken token, Func<Task<IResult>> action)
    {
        using var lease = await gate.AcquireAsync(token);
        if (lease is null) return Busy();
        try { return await action(); }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        { return Problem("Ya existe un registro con esos datos o cambió mientras guardabas. Recarga e inténtalo de nuevo.", 409); }
    }
}
