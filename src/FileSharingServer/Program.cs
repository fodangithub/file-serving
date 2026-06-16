using FileSharingServer.Auth;
using FileSharingServer.Components;
using FileSharingServer.Configuration;
using FileSharingServer.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddYamlFile("appsettings.yaml", optional: false, reloadOnChange: true);
builder.Services.Configure<AppSettings>(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<FileService>();
builder.Services.AddSingleton<BanlistService>();

var tempConfig = builder.Configuration.Get<AppSettings>() ?? new AppSettings();

// USN-based file search engine (falls back to directory enumeration if unavailable)
if (tempConfig.Search.EnableUsnIndex)
{
    builder.Services.AddSingleton<UsnSearchService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<UsnSearchService>());
}

if (tempConfig.Https.Enabled)
{
    var certDir = Path.GetFullPath(
        Path.Combine(builder.Environment.ContentRootPath, tempConfig.Https.CertDirectory));
    CertificateGenerator.GenerateIfNeeded(certDir, tempConfig.Https.Domain);

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(tempConfig.Server.Port);

        var cert = CertificateGenerator.LoadServerCertificate(certDir);
        kestrel.ListenAnyIP(tempConfig.Https.Port, listen =>
        {
            listen.UseHttps(cert);
        });
    });
}
else
{
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(tempConfig.Server.Port);
    });
}

var app = builder.Build();

app.UseMiddleware<BasicAuthMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["X-XSS-Protection"] = "0";
    await next();
});

app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/api/download", (HttpContext context, FileService fileService, string path) =>
{
    if (path.Length > FileService.MaxRelativePathLength)
        return Results.BadRequest("Path exceeds maximum allowed length.");

    var result = fileService.GetFileForDownload(path);
    if (result == null)
        return Results.NotFound();

    var (fullPath, fileName) = result.Value;
    return Results.File(fullPath, "application/octet-stream", fileName);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program { }
