using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Watchboard.Api.Contracts;
using Watchboard.Api.Data;
using Watchboard.Api.Models;

namespace Watchboard.Api.Services;

public static class UserAccounts
{
    public const string AdminPolicy = "administrator";
    public const string StampClaim = "watchboard:session-stamp";
    public const int PasswordIterations = 210_000; // Identity V3: PBKDF2-HMAC-SHA512, OWASP baseline.

    public static Guid UserId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static UserDto ToDto(this AppUser user) => new(user.Id, user.Username, user.DisplayName, user.IsAdmin, user.Enabled);
    public static WidgetPreferences Preferences(this AppUser user) => new(user.OnlyActionable, user.ShowVersions,
        user.ShowGames, user.ShowSales, user.ShowPriceDrops, user.ShowErrors, user.MaxItems,
        user.SoftwareLocationIdsJson is null ? null : JsonSerializer.Deserialize<Guid[]>(user.SoftwareLocationIdsJson), user.SoftwareUpdatesOnly);

    public static void SetPreferences(this AppUser user, WidgetPreferences preferences)
    {
        user.OnlyActionable = preferences.OnlyActionable;
        user.ShowVersions = preferences.ShowVersions;
        user.ShowGames = preferences.ShowGames;
        user.ShowSales = preferences.ShowSales;
        user.ShowPriceDrops = preferences.ShowPriceDrops;
        user.ShowErrors = preferences.ShowErrors;
        user.MaxItems = preferences.MaxItems;
        user.SoftwareLocationIdsJson = preferences.SoftwareLocationIds is null ? null : JsonSerializer.Serialize(preferences.SoftwareLocationIds);
        user.SoftwareUpdatesOnly = preferences.SoftwareUpdatesOnly;
    }

    public static bool PasswordValid(string? password) => password is { Length: >= 12 and <= 128 } &&
        !string.IsNullOrWhiteSpace(password) && !password.Any(char.IsControl);
    public static bool UsernameValid(string? username) => username is { Length: >= 3 and <= 40 } &&
        username.All(x => char.IsAsciiLetterOrDigit(x) || x is '.' or '_' or '-');

    public static Task SignInAsync(HttpContext context, AppUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.Username),
            new(StampClaim, user.SessionStamp)
        };
        if (user.IsAdmin) claims.Add(new(ClaimTypes.Role, "admin"));
        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
    }

    public static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        var validId = Guid.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id);
        var db = context.HttpContext.RequestServices.GetRequiredService<WatchboardDbContext>();
        var user = validId ? await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, context.HttpContext.RequestAborted) : null;
        if (user is not { Enabled: true, PasswordHash: not null } ||
            context.Principal!.FindFirstValue(StampClaim) != user.SessionStamp || context.Principal!.IsInRole("admin") != user.IsAdmin)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}

// Compute one unrelated dummy hash at startup to avoid a fast unknown-username password path.
// Neither the random dummy password nor any real password/hash is logged or returned.
public sealed class LoginPasswordVerifier(IPasswordHasher<AppUser> hasher)
{
    private readonly AppUser dummy = new();
    private readonly string dummyHash = hasher.HashPassword(new AppUser(), Guid.NewGuid().ToString("N"));

    public PasswordVerificationResult Verify(AppUser? user, string password) => hasher.VerifyHashedPassword(
        user ?? dummy, user?.PasswordHash ?? dummyHash, password);
}
