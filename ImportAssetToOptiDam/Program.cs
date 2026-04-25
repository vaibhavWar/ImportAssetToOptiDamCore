using ImportAssetToOptiDam.Configuration;
using ImportAssetToOptiDam.Infrastructure.Http;
using ImportAssetToOptiDam.Services.Authentication;
using ImportAssetToOptiDam.Services.Dam;
using ImportAssetToOptiDam.Services.Excel;
using ImportAssetToOptiDam.Services.Import;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Serilog;

// ---------------------------------------------------------------------------
// Bootstrap logger: used only while the host is being built. Replaced by the
// configuration-driven logger once the host is up.
// ---------------------------------------------------------------------------
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Import host.");

    var builder = Host.CreateApplicationBuilder(args);

    // Load config: appsettings.json, environment-specific, env vars, user secrets (dev),
    // and command-line args. User secrets and env vars take precedence over json files,
    // which is where ClientId / ClientSecret should live.
    builder.Configuration
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json",
                     optional: true, reloadOnChange: true)
        .AddUserSecrets<Program>(optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args);

    // Logging: route everything through Serilog, driven by configuration.
    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // Log which provider supplied the credential-bearing settings (without logging the
    // values). This is the fastest way to diagnose "the app isn't seeing my User Secrets"
    // or "env vars aren't making it through" issues at startup.
    LogConfigurationSources(builder.Configuration);

    // --- Options ---
    builder.Services
        .AddOptions<OptimizelyCmpOptions>()
        .Bind(builder.Configuration.GetSection(OptimizelyCmpOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    // Custom validator catches the placeholder / whitespace cases that DataAnnotations
    // can't express — registered as a singleton so it runs alongside ValidateDataAnnotations.
    builder.Services.AddSingleton<IValidateOptions<OptimizelyCmpOptions>, OptimizelyCmpOptionsValidator>();

    builder.Services
        .AddOptions<ImportOptions>()
        .Bind(builder.Configuration.GetSection(ImportOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    // --- HTTP clients ---

    // 1. Token client: NO bearer handler (it's what gets the token). Retries on transient
    //    5xx / network errors so startup doesn't die on a blip.
    builder.Services
        .AddHttpClient(CachingTokenProvider.HttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<OptimizelyCmpOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(new("application/json"));
            // BaseAddress intentionally left unset — TokenUrl is absolute.
            _ = opts; // (kept in lambda scope for future per-client defaults)
        })
        .AddStandardResilienceHandler();

    // 2. Storage upload client: talks to a pre-signed URL. No auth header, generous timeout
    //    for large assets, light retry policy because uploads are not always idempotent.
    builder.Services
        .AddHttpClient(OptimizelyDamClient.StorageHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
        });

    // 3. Typed DAM client: bearer token injected by DelegatingHandler, standard resilience,
    //    BaseAddress set from configuration so the client code can use relative paths.
    builder.Services.AddTransient<BearerTokenHandler>();
    builder.Services
        .AddHttpClient<IOptimizelyDamClient, OptimizelyDamClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<OptimizelyCmpOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(100);
            client.DefaultRequestHeaders.Accept.Add(new("application/json"));
        })
        .AddHttpMessageHandler<BearerTokenHandler>()
        .AddStandardResilienceHandler(options =>
        {
            // Allow uploads with large assets; the default 30s total timeout is too tight.
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);
            options.AttemptTimeout.Timeout      = TimeSpan.FromMinutes(1);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(2);
        });

    // --- Application services ---
    builder.Services.AddSingleton<ITokenProvider, CachingTokenProvider>();
    builder.Services.AddSingleton<IAssetImportReader, OpenXmlAssetImportReader>();
    builder.Services.AddSingleton<IUploadReportWriter, XlsxUploadReportWriter>();
    builder.Services.AddSingleton<AssetImporter>();

    // Hosted service: runs the import once and shuts down.
    builder.Services.AddHostedService<ImportHostedService>();

    var host = builder.Build();
    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly.");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

// ---------------------------------------------------------------------------
// Startup helpers
// ---------------------------------------------------------------------------

// Reports, for a handful of interesting keys, which configuration provider actually
// supplied the effective value. Values themselves are never logged — only the provider
// name and whether the value is populated / empty / missing. Intended to unblock the
// "my User Secrets don't seem to load" / "env var isn't making it through" diagnostic.
static void LogConfigurationSources(IConfigurationManager configuration)
{
    string[] keysToInspect =
    {
        "OptimizelyCmp:ClientId",
        "OptimizelyCmp:ClientSecret",
        "OptimizelyCmp:TokenUrl",
        "OptimizelyCmp:BaseUrl",
    };

    foreach (var key in keysToInspect)
    {
        var value = configuration[key];

        // Walk providers in reverse to find which one had the last word on this key.
        string sourceName = "<not set>";
        foreach (var provider in ((IConfigurationRoot)configuration).Providers.Reverse())
        {
            if (provider.TryGet(key, out _))
            {
                sourceName = provider.GetType().Name;
                break;
            }
        }

        // Mask the value — report only whether it has content.
        var presence =
            value is null        ? "missing"      :
            value.Length == 0    ? "empty"        :
            value != value.Trim() ? "has whitespace"  // a very common cause of 401s
                                  : $"set (len={value.Length})";

        Log.Information("Config source: {Key} = {Presence} (from {Source})",
            key, presence, sourceName);
    }
}

/// <summary>Marker type used by <c>AddUserSecrets&lt;Program&gt;()</c>.</summary>
public partial class Program { }
