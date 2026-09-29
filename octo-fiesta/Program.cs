using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Deezer;
using octo_fiesta.Services.Qobuz;
using octo_fiesta.Services.SquidWTF;
using octo_fiesta.Services.Tidal;
using octo_fiesta.Services.Yandex;
using octo_fiesta.Services.Composite;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Lyrics;
using octo_fiesta.Services.Validation;
using octo_fiesta.Services.Subsonic;
using octo_fiesta.Services.Common;
using octo_fiesta.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Interactive Tidal OAuth login. Runs the device authorization flow and exits without
// starting the server, so the tokens can be minted before the first real run.
if (TidalLoginCommand.IsRequested(args))
{
    return await TidalLoginCommand.RunAsync(builder.Configuration);
}

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
builder.Services.Configure<TidalSettings>(
    builder.Configuration.GetSection("Tidal"));
builder.Services.Configure<YandexSettings>(
    builder.Configuration.GetSection("Yandex"));
builder.Services.Configure<LyricsSettings>(
    builder.Configuration.GetSection("Lyrics"));

// Get the configured music service from bound settings (to respect default values)
var subsonicSettings = new SubsonicSettings();
builder.Configuration.GetSection("Subsonic").Bind(subsonicSettings);
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

// Register the configured music service(s). MUSIC_SERVICE may list several separated by "|";
// a provider without credentials is skipped with a warning (unless it is the only one requested).
var startupWarnings = new List<string>();
var requested = SubsonicSettings.ParseMusicServices(subsonicSettings.MusicServices, out var unknownServices);
foreach (var name in unknownServices) startupWarnings.Add($"Unknown MUSIC_SERVICE entry '{name}', skipping it");

bool Has(string? v) => !string.IsNullOrWhiteSpace(v);
string? MissingCredentials(MusicService svc)
{
    var cfg = builder.Configuration;
    switch (svc)
    {
        case MusicService.Deezer:
            return Has(cfg["Deezer:Arl"]) ? null : "Deezer__Arl is not set";
        case MusicService.Qobuz:
            return Has(cfg["Qobuz:UserId"]) && Has(cfg["Qobuz:UserAuthToken"]) ? null : "Qobuz__UserId / Qobuz__UserAuthToken is not set";
        case MusicService.Yandex:
            return Has(cfg["Yandex:OAuthToken"]) ? null : "Yandex__OAuthToken is not set";
        case MusicService.Tidal:
            var store = cfg["Tidal:TokenStore"] ?? "./tidal-tokens.json";
            return Has(cfg["Tidal:AccessToken"]) || Has(cfg["Tidal:RefreshToken"]) || File.Exists(store)
                ? null : "no Tidal tokens configured (run the Tidal login first)";
        default:
            return null; // SquidWTF needs no credentials
    }
}

var activeServices = requested;
if (requested.Count > 1)
{
    activeServices = [];
    foreach (var svc in requested)
    {
        var missing = MissingCredentials(svc);
        if (missing is null) activeServices.Add(svc);
        else startupWarnings.Add($"Skipping music service {svc}: {missing}");
    }
}
if (activeServices.Count == 0)
{
    throw new InvalidOperationException(
        "No usable MUSIC_SERVICE configured. " + string.Join("; ", startupWarnings));
}

var providers = new List<(string Key, Type Metadata, Type Download)>();
foreach (var svc in activeServices)
{
    switch (svc)
    {
        case MusicService.Qobuz:
            builder.Services.AddSingleton<QobuzBundleService>();
            providers.Add(("qobuz", typeof(QobuzMetadataService), typeof(QobuzDownloadService)));
            break;
        case MusicService.SquidWTF:
            builder.Services.AddSingleton<SquidWTFInstanceManager>();
            builder.Services.AddSingleton<SquidWTFCaptchaSolver>();
            providers.Add(("squidwtf", typeof(SquidWTFMetadataService), typeof(SquidWTFDownloadService)));
            break;
        case MusicService.Tidal:
            // Shared OAuth state: token renewal and the account's country code.
            builder.Services.AddSingleton<TidalTokenStore>();
            builder.Services.AddSingleton<TidalAuthService>();
            providers.Add(("tidal", typeof(TidalMetadataService), typeof(TidalDownloadService)));
            break;
        case MusicService.Yandex:
            providers.Add(("yandex", typeof(YandexMetadataService), typeof(YandexDownloadService)));
            break;
        default:
            providers.Add(("deezer", typeof(DeezerMetadataService), typeof(DeezerDownloadService)));
            break;
    }
}

// Single-provider setups keep their historical playlist-only secondary (Deezer <-> Qobuz).
if (enableExternalPlaylists && activeServices.Count == 1
    && activeServices[0] is MusicService.Deezer or MusicService.Qobuz)
{
    var qobuzPrimary = activeServices[0] == MusicService.Qobuz;
    if (!qobuzPrimary) builder.Services.AddSingleton<QobuzBundleService>();
    builder.Services.AddSingleton<IMusicMetadataService>(sp => qobuzPrimary
        ? sp.GetRequiredService<DeezerMetadataService>() : sp.GetRequiredService<QobuzMetadataService>());
    builder.Services.AddSingleton<IDownloadService>(sp => qobuzPrimary
        ? sp.GetRequiredService<DeezerDownloadService>() : sp.GetRequiredService<QobuzDownloadService>());
    builder.Services.AddSingleton(qobuzPrimary ? typeof(DeezerMetadataService) : typeof(QobuzMetadataService));
    builder.Services.AddSingleton(qobuzPrimary ? typeof(DeezerDownloadService) : typeof(QobuzDownloadService));
}

foreach (var p in providers)
{
    builder.Services.AddSingleton(p.Metadata);
    builder.Services.AddSingleton(p.Download);
}

// The composite is registered LAST so it is what gets injected for a single
// IMusicMetadataService / IDownloadService; playlist sync unwraps it.
builder.Services.AddSingleton<IMusicMetadataService>(sp => new CompositeMetadataService(
    providers.Select(p => (p.Key, (IMusicMetadataService)sp.GetRequiredService(p.Metadata))).ToList(),
    sp.GetRequiredService<ILogger<CompositeMetadataService>>()));
builder.Services.AddSingleton<IDownloadService>(sp => new CompositeDownloadService(
    providers.Select(p => (p.Key, (IDownloadService)sp.GetRequiredService(p.Download))).ToList()));

if (enableExternalPlaylists && activeServices.Any(s =>
        s != MusicService.SquidWTF ||
        (builder.Configuration.GetValue<string>("SquidWTF:Source") ?? "Qobuz").Equals("Tidal", StringComparison.OrdinalIgnoreCase)))
{
    builder.Services.AddSingleton<PlaylistSyncService>();
}

// Startup validation - register validators
builder.Services.AddSingleton<IStartupValidator, SubsonicStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, DeezerStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, QobuzStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, SquidWTFStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, TidalStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, YandexStartupValidator>();

// Configure custom HTTP clients for services
builder.Services.AddHttpClient(TidalHttpClientConfiguration.AuthClientName, TidalHttpClientConfiguration.ConfigureApiClient);
builder.Services.AddHttpClient(TidalHttpClientConfiguration.MediaClientName, TidalHttpClientConfiguration.ConfigureMediaClient);
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

foreach (var warning in startupWarnings) app.Logger.LogWarning("{Warning}", warning);

// Configure the HTTP request pipeline.
// Enable request body buffering FIRST to allow multiple reads (for proxy forwarding)
app.UseRequestBodyBuffering();

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

return 0;
