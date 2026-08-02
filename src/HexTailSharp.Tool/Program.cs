using System;
using System.Diagnostics;
using System.IO;
using HexTailSharp.Tool;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

if (!ToolOptions.TryParse(args, out var options, out var error))
{
    Console.Error.WriteLine(error);
    return 2;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
var settings = options!;
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(settings.Port));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"HexTail is running at {settings.Url}");
    if (!settings.NoBrowser)
        Process.Start(new ProcessStartInfo(settings.Url.ToString()) { UseShellExecute = true });
});

try
{
    await app.RunAsync();
    return 0;
}
catch (IOException exception)
{
    Console.Error.WriteLine($"Could not start HexTail at {settings.Url}: {exception.Message}");
    return 1;
}
