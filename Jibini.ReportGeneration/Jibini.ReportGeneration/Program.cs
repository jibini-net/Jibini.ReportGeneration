using Jibini.ReportGeneration.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var builder = WebApplication.CreateBuilder(args);
{
    builder.WebHost.UseUrls("http://*:0").UseKestrel();

    builder.Services.AddWindowsService((config) =>
    {
        config.ServiceName = "Jibini Report Generation";
    });
    builder.Services.AddRazorPages();
    builder.Services.AddControllers();
    builder.Services.AddMvcCore();
    builder.Services.AddServerSideBlazor();
    builder.Services.AddLogging((config) =>
    {
        config.SetMinimumLevel(LogLevel.Trace);
        config.AddSimpleConsole();
    });

    builder.Services.AddSingleton<ISharedBrowserService, SharedBrowserService>();
    builder.Services.AddHostedService((sp) => (sp.GetService<ISharedBrowserService>() as SharedBrowserService)!);
}

var app = builder.Build();
{
    app.UseStaticFiles();

    app.MapBlazorHub();
    app.MapControllers();
    app.MapRazorPages();
    app.MapFallbackToPage("/_Host");
}

await app.StartAsync();
{
    var services = app.Services;
    var logger = services.GetService<ILogger<Program>>()!;

    var address = services.GetService<IServer>()!.Features.Get<IServerAddressesFeature>()!.Addresses.First();
    var uri = new Uri(address);
    logger.LogInformation("Auxiliary web content bound to {}", uri.Port);
}

await app.WaitForShutdownAsync();