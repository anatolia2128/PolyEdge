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
        var discovered = 0;
        var totalRows = 0;
        var pageNumber = 0;

        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        var client = httpClientFactory.CreateClient("gamma-weather");

        var baseUrl = options.Value.GammaMarketsUrl.TrimEnd('/');
        var keysetUrl = baseUrl.EndsWith("/keyset", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : $"{baseUrl}/keyset";

        while (!ct.IsCancellationRequested)
        {
            pageNumber++;

            var url = $"{keysetUrl}?active=true&closed=false&limit={pageSize}";

            if (!string.IsNullOrWhiteSpace(cursor))
                url += $"&after_cursor={Uri.EscapeDataString(cursor)}";

            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: ct);

            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("markets", out var marketsElement) ||
                marketsElement.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning(
                    "Gamma keyset response had unexpected shape. Page={Page}",
                    pageNumber);

                break;
            }

            var page = marketsElement.EnumerateArray().ToList();

            if (page.Count == 0)
                break;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var ids = page
                .Select(x => ReadString(x, "id"))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .ToArray();

            var known = await db.WeatherMarkets
                .Where(x => ids.Contains(x.GammaMarketId))
                .ToDictionaryAsync(
                    x => x.GammaMarketId,
                    StringComparer.Ordinal,
                    ct);

            var now = DateTime.UtcNow;

            foreach (var item in page)
            {
                var question = ReadString(item, "question") ?? "";
                var description = ReadString(item, "description");

                if (!classifier.IsWeatherMarket(question, description))
                    continue;

                var gammaId = ReadString(item, "id");

                if (string.IsNullOrWhiteSpace(gammaId))
                    continue;

                if (!known.TryGetValue(gammaId, out var market))
                {
                    market = new WeatherMarket
                    {
                        GammaMarketId = gammaId,
                        FirstDiscoveredUtc = now
                    };

                    db.WeatherMarkets.Add(market);
                    known.Add(gammaId, market);
                }

                market.EventId = ExtractEventId(item);
                market.Slug = ReadString(item, "slug") ?? "";
                market.Question = question;
                market.Description = description;

                market.EndDateUtc = ParseDate(
                    ReadString(item, "endDate") ??
                    ReadString(item, "endDateIso"));

                market.IsActive = ReadBool(item, "active") ?? true;
                market.IsClosed = ReadBool(item, "closed") ?? false;
                market.LastDiscoveredUtc = now;
                market.RawJson = item.GetRawText();

                classifier.Classify(market);

                if (market.MappingStatus == "")
                    market.MappingStatus = "Pending";

                discovered++;
            }

            await db.SaveChangesAsync(ct);

            totalRows += page.Count;

            var nextCursor =
                root.TryGetProperty("next_cursor", out var cursorElement) &&
                cursorElement.ValueKind == JsonValueKind.String
                    ? cursorElement.GetString()
                    : null;

            logger.LogInformation(
                "Gamma weather keyset page processed. Page={Page}, Rows={Rows}, TotalRows={TotalRows}, WeatherSeen={WeatherSeen}, HasNext={HasNext}",
                pageNumber,
                page.Count,
                totalRows,
                discovered,
                !string.IsNullOrWhiteSpace(nextCursor));

            if (string.IsNullOrWhiteSpace(nextCursor))
                break;

            if (!seenCursors.Add(nextCursor))
            {
                logger.LogWarning(
                    "Gamma returned a repeated pagination cursor. Stopping discovery.");

                break;
            }

            cursor = nextCursor;

            await Task.Delay(100, ct);
        }

        logger.LogInformation(
            "Gamma weather keyset discovery completed. Pages={Pages}, TotalRows={TotalRows}, Weather market rows processed={Count}",
            pageNumber,
            totalRows,
            discovered);

        return discovered;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static bool? ReadBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? ExtractEventId(JsonElement item)
    {
        if (!item.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Array ||
            events.GetArrayLength() == 0)
            return null;

        return ReadString(events[0], "id");
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal |
            DateTimeStyles.AdjustToUniversal,
            out var date)
                ? date
                : null;
    }
}