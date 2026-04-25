using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImportAssetToOptiDam.Services.Import;

/// <summary>
/// Runs the import once on startup, then signals the host to shut down. This replaces
/// the original <c>Main</c> body, removing the blocking <c>Console.ReadKey()</c> so the
/// tool is runnable from CI/CD, Docker, and Windows Task Scheduler.
/// </summary>
public sealed class ImportHostedService : BackgroundService
{
    private readonly AssetImporter _importer;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ImportHostedService> _logger;

    public ImportHostedService(
        AssetImporter importer,
        IHostApplicationLifetime lifetime,
        ILogger<ImportHostedService> logger)
    {
        _importer = importer;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until the host reports itself "Started" so Serilog/DI are fully initialised.
        await Task.Yield();

        var exitCode = 0;
        try
        {
            _logger.LogInformation("Import process started at {StartedAt:O}.", DateTimeOffset.Now);

            var result = await _importer.RunAsync(stoppingToken).ConfigureAwait(false);
            if (result.Failed > 0)
            {
                exitCode = 1;
                _logger.LogWarning("Import completed with {Failed} failed row(s).", result.Failed);
            }
            else
            {
                _logger.LogInformation("Import completed successfully. {Succeeded} row(s) imported.", result.Succeeded);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Import was cancelled before it could complete.");
            exitCode = 2;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Import failed with an unhandled exception.");
            exitCode = 1;
        }
        finally
        {
            Environment.ExitCode = exitCode;
            _lifetime.StopApplication();
        }
    }
}
