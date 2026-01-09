using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Deezer;
using octo_fiesta.Services.Qobuz;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Validation;
using octo_fiesta.Services.Subsonic;
using octo_fiesta.Services.Common;
using octo_fiesta.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Exception handling
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Configuration
builder.Services.Configure<SubsonicSettings>(
    builder.Configuration.GetSection("Subsonic"));
builder.Services.Configure<DeezerSettings>(
    builder.Configuration.GetSection("Deezer"));
builder.Services.Configure<QobuzSettings>(
    builder.Configuration.GetSection("Qobuz"));

// Get the configured music service
var musicService = builder.Configuration.GetValue<MusicService>("Subsonic:MusicService");

// Business services
// Registered as Singleton to share state (mappings cache, scan debounce, download tracking, rate limiting)
builder.Services.AddSingleton<ILocalLibraryService, LocalLibraryService>();

// Subsonic services
builder.Services.AddSingleton<SubsonicRequestParser>();
builder.Services.AddSingleton<SubsonicResponseBuilder>();
builder.Services.AddSingleton<SubsonicModelMapper>();
builder.Services.AddSingleton<SubsonicProxyService>();

// Register music service based on configuration
if (musicService == MusicService.Qobuz)
{
    // Qobuz services
    builder.Services.AddSingleton<QobuzBundleService>();
    builder.Services.AddSingleton<IMusicMetadataService, QobuzMetadataService>();
    builder.Services.AddSingleton<IDownloadService, QobuzDownloadService>();
}
else
{
    // Deezer services (default)
    builder.Services.AddSingleton<IMusicMetadataService, DeezerMetadataService>();
    builder.Services.AddSingleton<IDownloadService, DeezerDownloadService>();
}

// Startup validation - register validators
builder.Services.AddSingleton<IStartupValidator, SubsonicStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, DeezerStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, QobuzStartupValidator>();

// Register orchestrator as hosted service
builder.Services.AddHostedService<StartupValidationOrchestrator>();

// Register cache cleanup service (only runs when StorageMode is Cache)
builder.Services.AddHostedService<CacheCleanupService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders("X-Content-Duration", "X-Total-Count", "X-Nd-Authorization");
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler(_ => { }); // Global exception handler

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.UseCors();

app.MapControllers();

app.Run();