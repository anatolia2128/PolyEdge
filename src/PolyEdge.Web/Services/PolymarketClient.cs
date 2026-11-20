using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PolyEdge.Web.Models;

namespace PolyEdge.Web.Services;

public sealed class PolymarketClient(HttpClient http, ILogger<PolymarketClient> logger)
{
    public async Task<List<MarketSnapshot>> GetFiveMinuteMarketsAsync(CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        using var response = await http.GetAsync("https://gamma-api.polymarket.com/markets?active=true&closed=false&limit=500", ct);
        logger.LogInformation("Gamma markets response: HTTP {StatusCode} in {ElapsedMs}ms", (int)response.StatusCode, timer.ElapsedMilliseconds);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning("Gamma markets response root was {JsonKind}, expected array", doc.RootElement.ValueKind);
            return [];
        }

        var total = doc.RootElement.GetArrayLength();
        var assetMatched = 0;
        var fiveMinuteMatched = 0;
        var directionMatched = 0;
        var dateMatched = 0;
        var tokenMatched = 0;
        var result = new List<MarketSnapshot>();

        foreach (var m in doc.RootElement.EnumerateArray())
        {
            var question = GetString(m, "question") ?? "";
            var slug = GetString(m, "slug") ?? "";
            var text = (question + " " + slug).ToLowerInvariant();
            var asset = text.Contains("bitcoin") || Regex.IsMatch(text, @"\bbtc\b") ? "BTC" :
                text.Contains("ethereum") || Regex.IsMatch(text, @"\beth\b") ? "ETH" :
                text.Contains("solana") || Regex.IsMatch(text, @"\bsol\b") ? "SOL" : "";
            if (asset.Length == 0) continue;
            assetMatched++;

            var isFiveMinute = Regex.IsMatch(text, @"(^|[-_\s])5m($|[-_\s])") || text.Contains("5-minute") || text.Contains("5 minute");
            if (!isFiveMinute) continue;
            fiveMinuteMatched++;
            if (!(text.Contains("up or down") || text.Contains("higher or lower") || text.Contains("up/down"))) continue;
            directionMatched++;

            var end = ParseDate(GetString(m, "endDate") ?? GetString(m, "endDateIso"));
            if (end == null || end < DateTime.UtcNow.AddMinutes(-1) || end > DateTime.UtcNow.AddMinutes(8)) continue;
            dateMatched++;

            var tokens = ParseStringArray(m, "clobTokenIds");
            var outcomes = ParseStringArray(m, "outcomes");
            if (tokens.Count < 2) continue;
            var upIndex = outcomes.FindIndex(x => x.Equals("Up", StringComparison.OrdinalIgnoreCase) || x.Equals("Yes", StringComparison.OrdinalIgnoreCase));
            var downIndex = outcomes.FindIndex(x => x.Equals("Down", StringComparison.OrdinalIgnoreCase) || x.Equals("No", StringComparison.OrdinalIgnoreCase));
            if (upIndex < 0) upIndex = 0;
            if (downIndex < 0) downIndex = 1;
            if (upIndex >= tokens.Count || downIndex >= tokens.Count || upIndex == downIndex) continue;
            tokenMatched++;

            result.Add(new(GetString(m, "id") ?? GetString(m, "conditionId") ?? Guid.NewGuid().ToString("N"),
                GetString(m, "conditionId") ?? "", question, asset, tokens[upIndex], tokens[downIndex], end.Value, true));
        }

        logger.LogInformation("Market discovery summary: API={ApiCount}, asset={AssetCount}, 5m={FiveMinuteCount}, direction={DirectionCount}, dateWindow={DateCount}, validTokens={TokenCount}, selected={SelectedCount}",
            total, assetMatched, fiveMinuteMatched, directionMatched, dateMatched, tokenMatched, result.Count);
        foreach (var market in result)
            logger.LogDebug("Selected market: {Asset} | ends {EndUtc:O} | {Question}", market.Asset, market.EndUtc, market.Question);
        return result;
    }

    public async Task<BookQuote?> GetBookAsync(string tokenId, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"https://clob.polymarket.com/book?token_id={Uri.EscapeDataString(tokenId)}", ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("CLOB book request failed: HTTP {StatusCode}, token {TokenId}", (int)response.StatusCode, ShortToken(tokenId));
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("bids", out var bids) || !doc.RootElement.TryGetProperty("asks", out var asks) ||
                bids.ValueKind != JsonValueKind.Array || asks.ValueKind != JsonValueKind.Array)
            {
                logger.LogDebug("CLOB book response missing bids/asks arrays for token {TokenId}", ShortToken(tokenId));
                return null;
            }

            var bid = BestPrice(bids, true);
            var ask = BestPrice(asks, false);
            if (bid <= 0 || ask <= 0)
            {
                logger.LogDebug("CLOB book has no two-sided quote: bid={Bid}, ask={Ask}, token {TokenId}", bid, ask, ShortToken(tokenId));
                return null;
            }
            if (bid > ask) logger.LogDebug("CLOB crossed book detected: bid={Bid}, ask={Ask}, token {TokenId}", bid, ask, ShortToken(tokenId));
            return new BookQuote(bid, ask);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Book unavailable for token {TokenId}", ShortToken(tokenId));
            return null;
        }
    }

    public async Task<List<JsonElement>> GetRecentWalletTradesAsync(string wallet, CancellationToken ct)
    {
        using var response = await http.GetAsync($"https://data-api.polymarket.com/trades?user={Uri.EscapeDataString(wallet)}&limit=500", ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Wallet trades API returned HTTP {StatusCode} for wallet {Wallet}", (int)response.StatusCode, ShortWallet(wallet));
            return [];
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().Select(x => x.Clone()).ToList() : [];
    }

    private static decimal BestPrice(JsonElement levels, bool highest)
    {
        decimal best = 0;
        foreach (var level in levels.EnumerateArray())
        {
            if (!level.TryGetProperty("price", out var p)) continue;
            if (!decimal.TryParse(p.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price)) continue;
            best = best == 0 ? price : highest ? Math.Max(best, price) : Math.Min(best, price);
        }
        return best;
    }

    private static string ShortToken(string token) => token.Length <= 12 ? token : $"{token[..6]}...{token[^4..]}";
    private static string ShortWallet(string wallet) => wallet.Length <= 12 ? wallet : $"{wallet[..6]}...{wallet[^4..]}";
    private static string? GetString(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
    private static DateTime? ParseDate(string? s) => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    private static List<string> ParseStringArray(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return [];
        if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray().Select(x => x.ToString()).ToList();
        if (v.ValueKind == JsonValueKind.String)
        {
            try { using var d = JsonDocument.Parse(v.GetString()!); return d.RootElement.EnumerateArray().Select(x => x.ToString()).ToList(); }
            catch (JsonException) { }
        }
        return [];
    }
}
