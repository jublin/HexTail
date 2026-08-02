using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using HexTailSharp;
using HexTailSharp.Application;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddRadzenComponents();
builder.Services.AddScoped<TailerService>();
builder.Services.AddScoped<IAppPersistence, BrowserLocalStoragePersistence>();
builder.Services.AddScoped<AppState>();

await builder.Build().RunAsync();
