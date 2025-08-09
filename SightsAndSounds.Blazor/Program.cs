using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SightsAndSounds.Blazor;
using SightsAndSounds.Blazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IConcertService, ConcertService>();
builder.Services.AddScoped<IVenueService, VenueService>();
builder.Services.AddScoped<ITrackService, TrackService>();
builder.Services.AddScoped<ISongService, SongService>();


await builder.Build().RunAsync();
