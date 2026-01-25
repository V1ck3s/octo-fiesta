using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Deezer;
using octo_fiesta.Services.Qobuz;
using octo_fiesta.Services.SquidWTF;
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
builder.Services.AddHttpContextAccessor();

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
builder.Services.Configure<SquidWTFSettings>(
    builder.Configuration.GetSection("SquidWTF"));

// Get the configured music service from bound settings (to respect default values)
var subsonicSettings = new SubsonicSettings();
builder.Configuration.GetSection("Subsonic").Bind(subsonicSettings);
var musicService = subsonicSettings.MusicService;
var enableExternalPlaylists = subsonicSettings.EnableExternalPlaylists;

// Business services
// Registered as Singleton to share state (mappings cache, scan debounce, download tracking, rate limiting)
builder.Services.AddSingleton<ILocalLibraryService, LocalLibraryService>();

// Subsonic services
builder.Services.AddSingleton<SubsonicRequestParser>();
builder.Services.AddSingleton<SubsonicResponseBuilder>();
builder.Services.AddSingleton<SubsonicModelMapper>();
builder.Services.AddScoped<SubsonicProxyService>();

// Register music service based on configuration
// IMPORTANT: Primary service MUST be registered LAST because ASP.NET Core DI
// will use the last registered implementation when injecting IMusicMetadataService/IDownloadService
if (musicService == MusicService.Qobuz)
{
    // If playlists enabled, register Deezer FIRST (secondary provider)
    if (enableExternalPlaylists)
    {
        builder.Services.AddSingleton<IMusicMetadataService, DeezerMetadataService>();
        builder.Services.AddSingleton<IDownloadService, DeezerDownloadService>();
        builder.Services.AddSingleton<PlaylistSyncService>();
    }
    
    // Qobuz services (primary) - registered LAST to be injected by default
    builder.Services.AddSingleton<QobuzBundleService>();
    builder.Services.AddSingleton<IMusicMetadataService, QobuzMetadataService>();
    builder.Services.AddSingleton<IDownloadService, QobuzDownloadService>();
}
else if (musicService == MusicService.SquidWTF)
{
    var squidWtfSource = builder.Configuration.GetValue<string>("SquidWTF:Source") ?? "Qobuz";
    var isTidalSource = squidWtfSource.Equals("Tidal", StringComparison.OrdinalIgnoreCase);
    
    // Only enable playlists for Tidal source (Qobuz doesn't support playlists via SquidWTF)
    if (enableExternalPlaylists && isTidalSource)
    {
        builder.Services.AddSingleton<PlaylistSyncService>();
    }
    
    // SquidWTF services (primary) - registered LAST to be injected by default
    builder.Services.AddSingleton<IMusicMetadataService, SquidWTFMetadataService>();
    builder.Services.AddSingleton<IDownloadService, SquidWTFDownloadService>();
}
else
{
    // If playlists enabled, register Qobuz FIRST (secondary provider)
    if (enableExternalPlaylists)
    {
        builder.Services.AddSingleton<QobuzBundleService>();
        builder.Services.AddSingleton<IMusicMetadataService, QobuzMetadataService>();
        builder.Services.AddSingleton<IDownloadService, QobuzDownloadService>();
        builder.Services.AddSingleton<PlaylistSyncService>();
    }
    
    // Deezer services (primary, default) - registered LAST to be injected by default
    builder.Services.AddSingleton<IMusicMetadataService, DeezerMetadataService>();
    builder.Services.AddSingleton<IDownloadService, DeezerDownloadService>();
}

// Startup validation - register validators
builder.Services.AddSingleton<IStartupValidator, SubsonicStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, DeezerStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, QobuzStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, SquidWTFStartupValidator>();

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
// Enable request body buffering FIRST to allow multiple reads (for proxy forwarding)
app.UseRequestBodyBuffering();

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

// Start the application
app.Start();

// Display listening URL after startup
foreach (var url in app.Urls)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("✓ ");
    Console.ResetColor();
    Console.Write("Listening on: ");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(url);
    Console.ResetColor();
}

Console.WriteLine();

// Wait for shutdown
app.WaitForShutdown();