using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Watchboard.Api.Contracts;
using Watchboard.Api.Data;
using Watchboard.Api.Models;
using Watchboard.Api.Services;
using static Watchboard.Api.Endpoints.EndpointSupport;

namespace Watchboard.Api.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccounts(this WebApplication app)
    {
        app.MapGet("/api/auth/session", async (ClaimsPrincipal principal, WatchboardDbContext db, CancellationToken token) =>
        {
            var admin = await db.Users.AsNoTracking().SingleAsync(x => x.Id == DefaultAccount.AdminId, token);
            var user = principal.Identity?.IsAuthenticated == true
                ? await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == principal.UserId(), token) : null;
            return Results.Ok(new SessionDto(admin.PasswordHash is null, user?.ToDto()));
        });

        app.MapPost("/api/auth/setup", async (SetupRequest value, HttpContext context, WatchboardDbContext db,
            IPasswordHasher<AppUser> hasher, OperationGate gate, CancellationToken token) =>
        {
            if (!UserAccounts.PasswordValid(value.Password)) return Problem("Elige una contraseña de 12 a 128 caracteres, sin caracteres de control.");
            return await Mutate(gate, token, async () =>
            {
                var admin = await db.Users.AsNoTracking().SingleAsync(x => x.Id == DefaultAccount.AdminId, token);
                if (admin.PasswordHash is not null) return Problem("La configuración inicial ya está cerrada. Inicia sesión.", 409);
                admin.PasswordHash = hasher.HashPassword(admin, value.Password);
                admin.SessionStamp = Guid.NewGuid().ToString("N");
                // The conditional database write also closes bootstrap atomically across multiple API processes.
                var claimed = await db.Users.Where(x => x.Id == DefaultAccount.AdminId && x.PasswordHash == null && x.Enabled)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.PasswordHash, admin.PasswordHash)
                        .SetProperty(x => x.SessionStamp, admin.SessionStamp), token);
                if (claimed != 1) return Problem("La configuración inicial ya está cerrada. Inicia sesión.", 409);
                await UserAccounts.SignInAsync(context, admin);
                return Results.Ok(admin.ToDto());
            });
        }).RequireRateLimiting("auth");

        app.MapPost("/api/auth/login", async (LoginRequest value, HttpContext context, WatchboardDbContext db,
            LoginPasswordVerifier verifier, IPasswordHasher<AppUser> hasher, OperationGate gate, CancellationToken token) =>
        {
            if (!UserAccounts.UsernameValid(value.Username) || value.Password is null || value.Password.Length > 128)
                return Results.Problem(statusCode: 401, detail: "Usuario o contraseña no válidos.");
            var normalized = value.Username.ToUpperInvariant();
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedUsername == normalized, token);
            var verified = verifier.Verify(user, value.Password);
            if (user is not { Enabled: true, PasswordHash: not null } || verified == PasswordVerificationResult.Failed)
                return Results.Problem(statusCode: 401, detail: "Usuario o contraseña no válidos.");

            // Recheck the stamp/enabled state after password work, so concurrent disable cannot issue a valid session.
            return await Mutate(gate, token, async () =>
            {
                var fresh = await db.Users.SingleOrDefaultAsync(x => x.Id == user.Id && x.Enabled && x.SessionStamp == user.SessionStamp, token);
                if (fresh is null) return Results.Problem(statusCode: 401, detail: "Usuario o contraseña no válidos.");
                if (verified == PasswordVerificationResult.SuccessRehashNeeded)
                {
                    fresh.PasswordHash = hasher.HashPassword(fresh, value.Password);
                    await db.SaveChangesAsync(token);
                }
                await UserAccounts.SignInAsync(context, fresh);
                return Results.Ok(fresh.ToDto());
            });
        }).RequireRateLimiting("auth");

        app.MapPost("/api/auth/logout", async (HttpContext context, WatchboardDbContext db, OperationGate gate, CancellationToken token) =>
            await Mutate(gate, token, async () =>
            {
                // This local single-device phase revokes the user's existing sessions, not only the browser cookie.
                var user = await db.Users.SingleAsync(x => x.Id == context.User.UserId(), token);
                user.SessionStamp = Guid.NewGuid().ToString("N");
                await db.SaveChangesAsync(token);
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.NoContent();
            })).RequireAuthorization().RequireRateLimiting("writes");

        var users = app.MapGroup("/api/users").RequireAuthorization(UserAccounts.AdminPolicy);
        users.MapGet("", async (WatchboardDbContext db, CancellationToken token) => Results.Ok(
            (await db.Users.AsNoTracking().OrderBy(x => x.Username).ToListAsync(token)).Select(x => x.ToDto())));
        users.MapPost("", async (CreateUser value, WatchboardDbContext db, IPasswordHasher<AppUser> hasher,
            OperationGate gate, CancellationToken token) =>
        {
            if (!UserAccounts.UsernameValid(value.Username)) return Problem("El usuario necesita entre 3 y 40 letras ASCII, números, puntos, guiones o guiones bajos.");
            if (!WatchValidation.Text(value.DisplayName, 100)) return Problem("Indica un nombre visible de hasta 100 caracteres.");
            if (!UserAccounts.PasswordValid(value.Password)) return Problem("Elige una contraseña de 12 a 128 caracteres, sin caracteres de control.");
            return await Mutate(gate, token, async () =>
            {
                var normalized = value.Username.ToUpperInvariant();
                if (await db.Users.AnyAsync(x => x.NormalizedUsername == normalized, token)) return Problem("Ese nombre de usuario ya existe.", 409);
                var user = new AppUser { Username = value.Username, NormalizedUsername = normalized,
                    DisplayName = value.DisplayName.Trim(), IsAdmin = value.IsAdmin };
                user.PasswordHash = hasher.HashPassword(user, value.Password);
                db.Users.Add(user);
                await db.SaveChangesAsync(token);
                return Results.Created($"/api/users/{user.Id}", user.ToDto());
            });
        }).RequireRateLimiting("writes");
        users.MapPatch("/{id:guid}/enabled", async (Guid id, SetUserEnabled value, ClaimsPrincipal principal,
            WatchboardDbContext db, OperationGate gate, CancellationToken token) => await Mutate(gate, token, async () =>
        {
            var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id, token);
            if (user is null) return Results.NotFound();
            if (!value.Enabled && user.Id == principal.UserId()) return Problem("No puedes desactivar tu propia cuenta.");
            if (!value.Enabled && user.IsAdmin && user.Enabled && await db.Users.CountAsync(x => x.IsAdmin && x.Enabled, token) <= 1)
                return Problem("Debe quedar al menos un administrador activo.");
            if (user.Enabled != value.Enabled)
            {
                user.Enabled = value.Enabled;
                user.SessionStamp = Guid.NewGuid().ToString("N");
                await db.SaveChangesAsync(token);
            }
            return Results.NoContent();
        })).RequireRateLimiting("writes");
    }
}
