using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SightsAndSounds.Blazor;
using SightsAndSounds.Blazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Configuring HttpClient BassAddress for all services (VenueService, ConcertService, TrackService, SongService)
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri("http://localhost:5192/") });

builder.Services.AddScoped<IConcertService, ConcertService>();
builder.Services.AddScoped<IVenueService, VenueService>();
builder.Services.AddScoped<ITrackService, TrackService>();
builder.Services.AddScoped<ISongService, SongService>();


await builder.Build().RunAsync();
