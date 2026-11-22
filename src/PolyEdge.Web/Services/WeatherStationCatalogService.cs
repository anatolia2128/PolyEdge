using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PolyEdge.Web.Data;
using PolyEdge.Web.Models.Weather;
using PolyEdge.Web.Options;
using Microsoft.Extensions.Options;

namespace PolyEdge.Web.Services;

public sealed class WeatherStationCatalogService(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<WeatherOptions> options,
    ILogger<WeatherStationCatalogService> logger)
{
    public async Task<int> RefreshAsync(CancellationToken ct)
    {
        using var response = await httpClientFactory.CreateClient("weather-stations")
            .GetAsync(options.Value.StationCacheUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var document = await JsonDocument.ParseAsync(gzip, cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AviationWeather station cache JSON root is not an array.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.WeatherStations.ToDictionaryAsync(x => x.ICAO, StringComparer.OrdinalIgnoreCase, ct);
        var now = DateTime.UtcNow;
        var changed = 0;

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var icao = ReadString(item, "icaoId", "icao", "ICAO", "id")?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(icao) || icao.Length != 4 || !icao.All(char.IsLetter)) continue;
            if (!existing.TryGetValue(icao, out var station))
            {
                station = new WeatherStation { ICAO = icao };
                db.WeatherStations.Add(station);
                existing.Add(icao, station);
            }

            station.IATA = ReadString(item, "iataId", "iata", "IATA");
            station.Name = ReadString(item, "name", "site", "stationName") ?? station.Name;
            station.City = ReadString(item, "city", "municipality") ?? station.City;
            station.Country = ReadString(item, "country", "countryCode") ?? station.Country;
            station.Latitude = ReadDouble(item, "lat", "latitude");
            station.Longitude = ReadDouble(item, "lon", "longitude", "lng");
            station.ElevationMeters = ReadDouble(item, "elev", "elevation", "elevationMeters");
            station.IsActive = true;
            station.LastUpdatedUtc = now;
            changed++;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("AviationWeather station catalog refreshed. Processed {Count} stations.", changed);
        return changed;
    }

    private static string? ReadString(JsonElement e, params string[] names)
    {
        foreach (var name in names)
            if (e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return v.ToString();
        return null;
    }

    private static double? ReadDouble(JsonElement e, params string[] names)
    {
        var value = ReadString(e, names);
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null;
    }
}
