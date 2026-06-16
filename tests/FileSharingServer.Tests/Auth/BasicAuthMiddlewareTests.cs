using System.Text;
using FileSharingServer.Auth;
using FileSharingServer.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FileSharingServer.Tests.Auth;

public class BasicAuthMiddlewareTests
{
    private BasicAuthMiddleware CreateMiddleware(AppSettings settings, RequestDelegate? next = null)
    {
        next ??= ctx => Task.CompletedTask;
        return new BasicAuthMiddleware(next, Options.Create(settings));
    }

    private static AppSettings CreateSettings(string username, string password)
    {
        var hash = BasicAuthMiddleware.ComputeHash(username, password);
        return new AppSettings
        {
            Auth = new AuthConfig
            {
                Users = new List<UserConfig>
                {
                    new() { Username = username, PasswordHash = hash }
                }
            }
        };
    }

    [Fact]
    public async Task ValidCredentials_PassesThrough()
    {
        var settings = CreateSettings("admin", "password123");
        var called = false;
        var middleware = CreateMiddleware(settings, _ => { called = true; return Task.CompletedTask; });

        var context = new DefaultHttpContext();
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:password123"));
        context.Request.Headers.Authorization = $"Basic {credentials}";

        await middleware.InvokeAsync(context);

        Assert.True(called);
        Assert.NotEqual(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task MissingAuthHeader_Returns401()
    {
        var settings = CreateSettings("admin", "password123");
        var middleware = CreateMiddleware(settings);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvalidCredentials_Returns401()
    {
        var settings = CreateSettings("admin", "password123");
        var middleware = CreateMiddleware(settings);
        var context = new DefaultHttpContext();
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:wrongpassword"));
        context.Request.Headers.Authorization = $"Basic {credentials}";

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task MalformedBase64_Returns401()
    {
        var settings = CreateSettings("admin", "password123");
        var middleware = CreateMiddleware(settings);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Basic not-valid-base64!!!";

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task ExemptPath_BypassesAuth()
    {
        var settings = CreateSettings("admin", "password123");
        var called = false;
        var middleware = CreateMiddleware(settings, _ => { called = true; return Task.CompletedTask; });

        var context = new DefaultHttpContext();
        context.Request.Path = "/css/app.css";

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    [Fact]
    public async Task BlazorPath_BypassesAuth()
    {
        var settings = CreateSettings("admin", "password123");
        var called = false;
        var middleware = CreateMiddleware(settings, _ => { called = true; return Task.CompletedTask; });

        var context = new DefaultHttpContext();
        context.Request.Path = "/_blazor";

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    [Fact]
    public void DecodeBasicAuth_ValidInput_ReturnsCredentials()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));
        var result = BasicAuthMiddleware.DecodeBasicAuth($"Basic {encoded}");

        Assert.NotNull(result);
        Assert.Equal("user", result!.Value.username);
        Assert.Equal("pass", result!.Value.password);
    }

    [Fact]
    public void DecodeBasicAuth_NoColon_ReturnsNull()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("nocolon"));
        var result = BasicAuthMiddleware.DecodeBasicAuth($"Basic {encoded}");

        Assert.Null(result);
    }

    [Fact]
    public void ComputeHash_IsDeterministic()
    {
        var hash1 = BasicAuthMiddleware.ComputeHash("admin", "pass");
        var hash2 = BasicAuthMiddleware.ComputeHash("admin", "pass");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_IncludesSalt()
    {
        var hash = BasicAuthMiddleware.ComputeHash("admin", "pass");
        var hashWithoutSalt = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("adminpass"));
        var hexWithoutSalt = Convert.ToHexString(hashWithoutSalt);
        Assert.NotEqual(hexWithoutSalt, hash);
    }

    [Fact]
    public void IsExemptPath_CssPath_ReturnsTrue()
    {
        Assert.True(BasicAuthMiddleware.IsExemptPath("/css/app.css"));
    }

    [Fact]
    public void IsExemptPath_ApiPath_ReturnsFalse()
    {
        Assert.False(BasicAuthMiddleware.IsExemptPath("/api/download"));
    }

    [Fact]
    public void IsExemptPath_RootPath_ReturnsFalse()
    {
        Assert.False(BasicAuthMiddleware.IsExemptPath("/"));
    }
}
