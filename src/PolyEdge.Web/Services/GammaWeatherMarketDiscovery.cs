using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PolyEdge.Web.Data;
using PolyEdge.Web.Models.Weather;
using PolyEdge.Web.Options;

namespace PolyEdge.Web.Services;

public sealed class GammaWeatherMarketDiscovery(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    WeatherMarketClassifier classifier,
    IOptions<WeatherOptions> options,
    ILogger<GammaWeatherMarketDiscovery> logger)
{
    public async Task<int> DiscoverAsync(CancellationToken ct)
    {
        var pageSize = Math.Clamp(options.Value.GammaPageSize, 1, 500);
        var offset = 0;
        var discovered = 0;
        var client = httpClientFactory.CreateClient("gamma-weather");

        while (!ct.IsCancellationRequested)
        {
            var url = $"{options.Value.GammaMarketsUrl}?active=true&closed=false&limit={pageSize}&offset={offset}";
            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (document.RootElement.ValueKind != JsonValueKind.Array) break;

            var page = document.RootElement.EnumerateArray().ToList();
            if (page.Count == 0) break;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = page.Select(x => ReadString(x, "id")).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
            var known = await db.WeatherMarkets.Where(x => ids.Contains(x.GammaMarketId))
                .ToDictionaryAsync(x => x.GammaMarketId, StringComparer.Ordinal, ct);
            var now = DateTime.UtcNow;

            foreach (var item in page)
            {
                var question = ReadString(item, "question") ?? "";
                var description = ReadString(item, "description");
                if (!classifier.IsWeatherMarket(question, description)) continue;
                var gammaId = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(gammaId)) continue;

                if (!known.TryGetValue(gammaId, out var market))
                {
                    market = new WeatherMarket { GammaMarketId = gammaId, FirstDiscoveredUtc = now };
                    db.WeatherMarkets.Add(market);
                    known.Add(gammaId, market);
                }

                market.EventId = ExtractEventId(item);
                market.Slug = ReadString(item, "slug") ?? "";
                market.Question = question;
                market.Description = description;
                market.EndDateUtc = ParseDate(ReadString(item, "endDate") ?? ReadString(item, "endDateIso"));
                market.IsActive = ReadBool(item, "active") ?? true;
                market.IsClosed = ReadBool(item, "closed") ?? false;
                market.LastDiscoveredUtc = now;
                market.RawJson = item.GetRawText();
                classifier.Classify(market);
                if (market.MappingStatus == "") market.MappingStatus = "Pending";
                discovered++;
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Gamma weather discovery page processed. Offset={Offset}, Rows={Rows}, WeatherSeen={WeatherSeen}", offset, page.Count, discovered);
            offset += page.Count;
            if (page.Count < pageSize) break;
        }

        logger.LogInformation("Gamma weather discovery completed. Weather market rows processed: {Count}", discovered);
        return discovered;
    }

    private static string? ExtractEventId(JsonElement item)
    {
        if (!item.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array || events.GetArrayLength() == 0) return null;
        var first = events[0];
        return ReadString(first, "id");
    }

    private static string? ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v.ToString() : null;

    private static bool? ReadBool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        return bool.TryParse(v.ToString(), out var result) ? result : null;
    }

    private static DateTime? ParseDate(string? value) => DateTime.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null;
}
