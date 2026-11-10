using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Watchboard.Api.Services;

public interface ILocalVersionDetector
{
    Task<(string? Version, string? Error)> DetectAsync(string tool, CancellationToken token);
}

public sealed partial class LocalVersionDetector : ILocalVersionDetector
{
    // Not user-configurable: no shell, arbitrary arguments, scripts, paths, or remote execution.
    private static readonly Dictionary<string, string> Commands = new()
    {
        ["node"] = "node", ["git"] = "git", ["dotnet"] = "dotnet", ["opencode"] = "opencode"
    };

    public async Task<(string? Version, string? Error)> DetectAsync(string tool, CancellationToken token)
    {
        if (!Commands.TryGetValue(tool, out var command)) return (null, "Herramienta no permitida.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? $"{command}.exe" : command,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = Path.GetTempPath()
        };
        start.ArgumentList.Add("--version");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["NO_COLOR"] = "1";
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await output;
            await error;
            if (process.ExitCode != 0) return (null, "La herramienta no ha devuelto una versión. Puedes registrarla manualmente.");
            var version = ExtractVersion(text);
            return version is not null ? (version, null) : (null, "No se reconoce el formato de la versión; regístrala manualmente.");
        }
        catch (Win32Exception) { return (null, "No se encontró el ejecutable en el PATH del panel. Puedes registrar la versión manualmente."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return (null, "La detección tardó demasiado y se detuvo.");
        }
        finally
        {
            // Also stop our own child on request cancellation; never leave probes running.
            try { if (process.Id > 0 && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    public static string? ExtractVersion(string text)
    {
        var match = ToolVersion().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"(?<![0-9A-Za-z.])v?(\d+\.\d+(?:\.\d+){0,2}(?:\.windows\.\d+)?(?:-[0-9A-Za-z.-]+)?)(?![0-9A-Za-z.])")]
    private static partial Regex ToolVersion();
}
