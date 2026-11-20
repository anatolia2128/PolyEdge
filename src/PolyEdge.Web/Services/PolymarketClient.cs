using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PolyEdge.Web.Models;

namespace PolyEdge.Web.Services;

public sealed class PolymarketClient(HttpClient http, ILogger<PolymarketClient> logger)
{
    public async Task<List<MarketSnapshot>> GetFiveMinuteMarketsAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("https://gamma-api.polymarket.com/markets?active=true&closed=false&limit=500", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var result = new List<MarketSnapshot>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            var question = GetString(m, "question") ?? "";
            var slug = GetString(m, "slug") ?? "";
            var text = (question + " " + slug).ToLowerInvariant();
            var asset = text.Contains("bitcoin") || Regex.IsMatch(text, @"\bbtc\b") ? "BTC" :
                text.Contains("ethereum") || Regex.IsMatch(text, @"\beth\b") ? "ETH" :
                text.Contains("solana") || Regex.IsMatch(text, @"\bsol\b") ? "SOL" : "";
            var isFiveMinute = Regex.IsMatch(text, @"(^|[-_\s])5m($|[-_\s])") || text.Contains("5-minute") || text.Contains("5 minute");
            if (asset == "" || !isFiveMinute || !(text.Contains("up or down") || text.Contains("higher or lower") || text.Contains("up/down"))) continue;
            var end = ParseDate(GetString(m, "endDate") ?? GetString(m, "endDateIso"));
            if (end == null || end < DateTime.UtcNow.AddMinutes(-1) || end > DateTime.UtcNow.AddMinutes(8)) continue;
            var tokens = ParseStringArray(m, "clobTokenIds");
            var outcomes = ParseStringArray(m, "outcomes");
            if (tokens.Count < 2) continue;
            var upIndex = outcomes.FindIndex(x => x.Equals("Up", StringComparison.OrdinalIgnoreCase) || x.Equals("Yes", StringComparison.OrdinalIgnoreCase));
            var downIndex = outcomes.FindIndex(x => x.Equals("Down", StringComparison.OrdinalIgnoreCase) || x.Equals("No", StringComparison.OrdinalIgnoreCase));
            if (upIndex < 0) upIndex = 0;
            if (downIndex < 0) downIndex = 1;
            if (upIndex >= tokens.Count || downIndex >= tokens.Count) continue;
            result.Add(new(GetString(m, "id") ?? GetString(m, "conditionId") ?? Guid.NewGuid().ToString("N"),
                GetString(m, "conditionId") ?? "", question, asset, tokens[upIndex], tokens[downIndex], end.Value, true));
        }
        return result;
    }

    public async Task<BookQuote?> GetBookAsync(string tokenId, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"https://clob.polymarket.com/book?token_id={Uri.EscapeDataString(tokenId)}", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var bids = doc.RootElement.GetProperty("bids");
            var asks = doc.RootElement.GetProperty("asks");
            var bid = BestPrice(bids, true);
            var ask = BestPrice(asks, false);
            return bid > 0 && ask > 0 ? new BookQuote(bid, ask) : null;
        }
        catch (Exception ex) { logger.LogDebug(ex, "Book unavailable for token {Token}", tokenId); return null; }
    }

    public async Task<List<JsonElement>> GetRecentWalletTradesAsync(string wallet, CancellationToken ct)
    {
        using var response = await http.GetAsync($"https://data-api.polymarket.com/trades?user={Uri.EscapeDataString(wallet)}&limit=500", ct);
        if (!response.IsSuccessStatusCode) return [];
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().Select(x => x.Clone()).ToList() : [];
    }

    private static decimal BestPrice(JsonElement levels, bool highest)
    {
        decimal best = 0;
        foreach (var level in levels.EnumerateArray())
        {
            if (!level.TryGetProperty("price", out var p)) continue;
            if (!decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price)) continue;
            best = best == 0 ? price : highest ? Math.Max(best, price) : Math.Min(best, price);
        }
        return best;
    }
    private static string? GetString(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
    private static DateTime? ParseDate(string? s) => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
    private static List<string> ParseStringArray(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return [];
        if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray().Select(x => x.ToString()).ToList();
        if (v.ValueKind == JsonValueKind.String)
        {
            try { using var d = JsonDocument.Parse(v.GetString()!); return d.RootElement.EnumerateArray().Select(x => x.ToString()).ToList(); } catch { }
        }
        return [];
    }
}
