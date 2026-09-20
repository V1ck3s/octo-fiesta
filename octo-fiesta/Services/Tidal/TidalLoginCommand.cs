using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Tidal;

namespace octo_fiesta.Services.Tidal;

/// <summary>
/// Interactive <c>--tidal-login</c> helper. Runs the OAuth login, writes the tokens to the
/// token store and exits, so the server itself never has to prompt for anything.
/// </summary>
public static class TidalLoginCommand
{
    public const string Argument = "--tidal-login";

    public static bool IsRequested(string[] args)
        => args.Contains(Argument, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Process exit code: 0 on success, 1 on failure.
    /// </summary>
    public static async Task<int> RunAsync(IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var settings = new TidalSettings();
        configuration.GetSection("Tidal").Bind(settings);

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
        }).SetMinimumLevel(LogLevel.Warning));

        var services = new ServiceCollection();
        services.AddHttpClient(TidalHttpClientConfiguration.AuthClientName, TidalHttpClientConfiguration.ConfigureApiClient);
        using var provider = services.BuildServiceProvider();

        var options = Options.Create(settings);
        var tokenStore = new TidalTokenStore(options, loggerFactory.CreateLogger<TidalTokenStore>());
        var auth = new TidalAuthService(
            provider.GetRequiredService<IHttpClientFactory>(),
            options,
            tokenStore,
            loggerFactory.CreateLogger<TidalAuthService>());

        try
        {
            if (auth.UsesDeviceAuthorization)
            {
                await RunDeviceFlowAsync(auth, cancellationToken);
            }
            else
            {
                await RunPkceFlowAsync(auth, cancellationToken);
            }

            var session = await auth.GetSessionAsync(cancellationToken);
            var subscription = await auth.GetSubscriptionAsync(cancellationToken);

            Console.WriteLine();
            Console.WriteLine("Tidal login successful.");
            Console.WriteLine($"  User ID: {session?.UserId.ToString() ?? auth.UserId ?? "unknown"}");
            Console.WriteLine($"  Country: {session?.CountryCode ?? auth.CountryCode}");
            Console.WriteLine($"  Subscription: {subscription?.Subscription?.Type ?? "unknown"}");
            Console.WriteLine($"  Token store: {Path.GetFullPath(auth.TokenStorePath)}");
            Console.WriteLine();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"Tidal login failed: {ex.Message}");
            Console.Error.WriteLine("Run the helper again to start over with a fresh code.");
            Console.WriteLine();
            return 1;
        }
    }

    /// <summary>
    /// Login of a public client. Tidal's login page is behind a bot check, so the browser
    /// does that part. It then bounces from the redirect to the home page without leaving
    /// the code in the address bar, which is why the user is sent to the network log.
    /// </summary>
    private static async Task RunPkceFlowAsync(TidalAuthService auth, CancellationToken cancellationToken)
    {
        var challenge = TidalPkce.CreateChallenge();

        Console.WriteLine();
        Console.WriteLine("Open this address in your browser and log in with your Tidal account.");
        Console.WriteLine();
        Console.WriteLine(auth.BuildAuthorizationUrl(challenge));
        Console.WriteLine();
        Console.WriteLine("Tidal redirects once and then bounces straight to its home page, so the address");
        Console.WriteLine("bar does not keep what is needed. Open the browser developer tools on the Network");
        Console.WriteLine("tab before logging in, with \"Preserve log\" enabled, and look afterwards for the");
        Console.WriteLine("request whose address starts with:");
        Console.WriteLine();
        Console.WriteLine($"  {TidalPkce.RedirectUri}?code=");
        Console.WriteLine();
        Console.WriteLine("Paste that whole address here, or just the value of its code parameter.");
        Console.WriteLine();
        Console.Write("Address or code: ");

        var redirected = Console.ReadLine();
        Console.WriteLine();

        await auth.CompleteAuthorizationAsync(redirected, challenge, cancellationToken);
    }

    /// <summary>
    /// Login of a limited input device client, which is only reachable by configuring a
    /// client id and secret pair.
    /// </summary>
    private static async Task RunDeviceFlowAsync(TidalAuthService auth, CancellationToken cancellationToken)
    {
        var authorization = await auth.StartDeviceAuthorizationAsync(cancellationToken);
        var link = BuildVerificationLink(authorization);
        var expiresInMinutes = Math.Max(authorization.ExpiresIn / 60, 1);

        Console.WriteLine();
        Console.WriteLine($"Open {link} and log in with your Tidal account.");
        Console.WriteLine($"Waiting for approval... (expires in {expiresInMinutes} minutes)");

        await auth.WaitForDeviceApprovalAsync(authorization, cancellationToken);
    }

    private static string BuildVerificationLink(TidalDeviceAuthorizationResponse authorization)
    {
        var uri = authorization.VerificationUriComplete;
        if (string.IsNullOrWhiteSpace(uri))
        {
            uri = string.IsNullOrWhiteSpace(authorization.UserCode)
                ? authorization.VerificationUri
                : $"{authorization.VerificationUri}/{authorization.UserCode}";
        }

        if (string.IsNullOrWhiteSpace(uri))
        {
            return "https://link.tidal.com";
        }

        // Tidal returns the link without a scheme (link.tidal.com/ABC12).
        return uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? uri : $"https://{uri}";
    }
}
