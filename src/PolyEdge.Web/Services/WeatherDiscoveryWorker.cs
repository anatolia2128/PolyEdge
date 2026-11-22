using Microsoft.Extensions.Options;
using PolyEdge.Web.Data;
using PolyEdge.Web.Options;

namespace PolyEdge.Web.Services;

public sealed class WeatherDiscoveryWorker(
    IServiceScopeFactory scopeFactory,
    GammaWeatherMarketDiscovery discovery,
    WeatherStationCatalogService stationCatalog,
    IOptions<WeatherOptions> options,
    ILogger<WeatherDiscoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Weather discovery is disabled by configuration.");
            return;
        }

        var stationRefresh = TimeSpan.FromHours(Math.Clamp(options.Value.StationRefreshHours, 1, 168));
        var interval = TimeSpan.FromMinutes(Math.Clamp(options.Value.DiscoveryIntervalMinutes, 5, 1440));
        var lastStationRefresh = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastStationRefresh >= stationRefresh)
                {
                    try
                    {
                        await stationCatalog.RefreshAsync(stoppingToken);
                        lastStationRefresh = DateTime.UtcNow;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "AviationWeather station catalog refresh failed; discovery will continue.");
                    }
                }

                await discovery.DiscoverAsync(stoppingToken);
                using var scope = scopeFactory.CreateScope();
                var mapper = scope.ServiceProvider.GetRequiredService<WeatherStationMapper>();
                await mapper.MapPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Weather discovery cycle failed.");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
