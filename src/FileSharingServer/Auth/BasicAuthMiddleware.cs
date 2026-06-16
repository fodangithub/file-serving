using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FileSharingServer.Configuration;
using Microsoft.Extensions.Options;

namespace FileSharingServer.Auth;

public partial class BasicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptions<AppSettings> _settings;

    public BasicAuthMiddleware(RequestDelegate next, IOptions<AppSettings> settings)
    {
        _next = next;
        _settings = settings;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        if (IsExemptPath(path))
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var credentials = DecodeBasicAuth(authHeader);
            if (credentials != null && ValidateCredentials(credentials.Value.username, credentials.Value.password))
            {
                await _next(context);
                return;
            }
        }

        context.Response.StatusCode = 401;
        context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"File Server\"";
        await context.Response.WriteAsync("Unauthorized");
    }

    internal static bool IsExemptPath(string path)
    {
        return ExemptPathRegex().IsMatch(path);
    }

    internal static (string username, string password)? DecodeBasicAuth(string authHeader)
    {
        try
        {
            var base64 = authHeader["Basic ".Length..].Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var colonIndex = decoded.IndexOf(':');
            if (colonIndex < 0) return null;
            return (decoded[..colonIndex], decoded[(colonIndex + 1)..]);
        }
        catch
        {
            return null;
        }
    }

    internal bool ValidateCredentials(string username, string password)
    {
        var hash = ComputeHash(username, password);
        return _settings.Value.Auth.Users.Any(u =>
            string.Equals(u.Username, username, StringComparison.Ordinal) &&
            string.Equals(u.PasswordHash, hash, StringComparison.OrdinalIgnoreCase));
    }

    internal static string ComputeHash(string username, string password)
    {
        var input = username + password + "2026";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    [GeneratedRegex(@"^(/_blazor|/_framework|/css/|/js/|/lib/|/favicon|/app\.css)")]
    private static partial Regex ExemptPathRegex();
}
