using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Qobuz;
using octo_fiesta.Services.Yandex;
using octo_fiesta.Services.YouTube;
using octo_fiesta.Services.JioSaavn;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Lyrics;
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
builder.Services.Configure<QobuzSettings>(
    builder.Configuration.GetSection("Qobuz"));
builder.Services.Configure<YandexSettings>(
    builder.Configuration.GetSection("Yandex"));
builder.Services.Configure<YouTubeSettings>(
    builder.Configuration.GetSection("YouTube"));
builder.Services.Configure<JioSaavnSettings>(
    builder.Configuration.GetSection("JioSaavn"));
builder.Services.Configure<LyricsSettings>(
    builder.Configuration.GetSection("Lyrics"));
builder.Services.AddSingleton<JioSaavnApiClient>();
builder.Services.AddSingleton<IYtDlpProcessRunner, YtDlpProcessRunner>();

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

// Lyrics lookup (LRCLIB). Always registered; gated at runtime by Lyrics:Enabled.
var lyricsSettings = new LyricsSettings();
builder.Configuration.GetSection("Lyrics").Bind(lyricsSettings);
builder.Services.AddSingleton<ILyricsService, LrclibLyricsService>();
builder.Services.AddHttpClient(LrclibLyricsService.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(lyricsSettings.TimeoutSeconds);
    // LRCLIB etiquette: identify the app with a contact/repo URL.
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "octo-fiesta (https://github.com/V1ck3s/octo-fiesta)");
});

// Register music service based on configuration
// IMPORTANT: Primary service MUST be registered LAST because ASP.NET Core DI
// will use the last registered implementation when injecting IMusicMetadataService/IDownloadService
if (musicService == MusicService.Qobuz)
{
    if (enableExternalPlaylists)
    {
        builder.Services.AddSingleton<PlaylistSyncService>();
    }

    // Qobuz services (primary) - registered LAST to be injected by default
    builder.Services.AddSingleton<QobuzBundleService>();
    builder.Services.AddSingleton<IMusicMetadataService, QobuzMetadataService>();
    builder.Services.AddSingleton<IDownloadService, QobuzDownloadService>();
}
else if (musicService == MusicService.Yandex)
{
    if (enableExternalPlaylists)
    {
        builder.Services.AddSingleton<PlaylistSyncService>();
    }
    builder.Services.AddSingleton<IMusicMetadataService, YandexMetadataService>();
    builder.Services.AddSingleton<IDownloadService, YandexDownloadService>();
}
else if (musicService == MusicService.YouTube)
{
    builder.Services.AddSingleton<IMusicMetadataService, YouTubeMetadataService>();
    builder.Services.AddSingleton<IDownloadService, YouTubeDownloadService>();
}
else if (musicService == MusicService.JioSaavn)
{
    builder.Services.AddSingleton<IMusicMetadataService, JioSaavnMetadataService>();
    builder.Services.AddSingleton<IDownloadService, JioSaavnDownloadService>();
}
else
{
    // Unreachable in practice (MusicService binds to one of the enum values above,
    // and its own default is JioSaavn) - kept as a safe fallback.
    builder.Services.AddSingleton<IMusicMetadataService, JioSaavnMetadataService>();
    builder.Services.AddSingleton<IDownloadService, JioSaavnDownloadService>();
}

// Startup validation - register validators
builder.Services.AddSingleton<IStartupValidator, SubsonicStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, QobuzStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, YandexStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, YouTubeStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, JioSaavnStartupValidator>();

// Configure custom HTTP clients for services
builder.Services.AddHttpClient("Yandex", YandexHttpClientConfiguration.ConfigureClient);

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

// CORS runs before authentication so preflight (OPTIONS) requests and browser
// media/XHR requests get Access-Control-* headers even when auth later rejects
// them - otherwise the browser only ever sees an opaque CORS failure.
app.UseCors();

// Validate Subsonic authentication BEFORE any endpoint processing
// This prevents unauthenticated access to external resources
app.UseSubsonicAuthentication();

app.UseExceptionHandler(_ => { }); // Global exception handler

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

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
