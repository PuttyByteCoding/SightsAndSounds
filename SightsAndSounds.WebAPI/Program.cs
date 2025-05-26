using Microsoft.EntityFrameworkCore;
using SightsAndSounds.Shared.Models;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Sights and Sounds API", Version = "v1" });
});

// Register my SQL Database Context with DI so that it is accessible in the controllers
builder
    .Services.AddDbContext<SightsAndSoundsDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

app.UseHttpsRedirection();

// Configure Swagger and set it to the defaul page.
app.UseSwagger();
app.UseSwaggerUI();
app.MapGet("/", context =>
{
    context.Response.Redirect("/swagger");
    return Task.CompletedTask;
});

// Register the endpoints for the API
app.MapSongEndpoints();
app.MapConcertEndpoints();
app.MapVenueEndpoints();

app.Run();

