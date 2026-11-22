using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PolyEdge.Web.Data;
using PolyEdge.Web.Models.Weather;

namespace PolyEdge.Web.Services;

public sealed class WeatherStationMapper(AppDbContext db, ILogger<WeatherStationMapper> logger)
{
    private static readonly Regex IcaoPattern = new(@"\b[A-Z]{4}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task MapPendingAsync(CancellationToken ct)
    {
        var pending = await db.WeatherMarkets
            .Where(x => x.MappingStatus == "Pending" || x.MappingStatus == "Candidate")
            .OrderBy(x => x.Id).Take(500).ToListAsync(ct);
        if (pending.Count == 0) return;

        var codes = pending.SelectMany(m => IcaoPattern.Matches($"{m.Description} {m.ResolutionSource}")
                .Select(match => match.Value.ToUpperInvariant()))
            .Distinct().ToArray();
        if (codes.Length == 0) return;

        var stations = await db.WeatherStations.Where(s => codes.Contains(s.ICAO) && s.IsActive)
            .ToDictionaryAsync(s => s.ICAO, StringComparer.OrdinalIgnoreCase, ct);

        foreach (var market in pending)
        {
            var matches = IcaoPattern.Matches($"{market.Description} {market.ResolutionSource}")
                .Select(m => m.Value.ToUpperInvariant()).Distinct()
                .Where(stations.ContainsKey).Select(code => stations[code]).ToList();
            if (matches.Count == 0) continue;

            foreach (var station in matches)
            {
                var exists = await db.WeatherMarketStationMappings.AnyAsync(x =>
                    x.WeatherMarketId == market.Id && x.WeatherStationId == station.Id, ct);
                if (exists) continue;
                db.WeatherMarketStationMappings.Add(new WeatherMarketStationMapping
                {
                    WeatherMarketId = market.Id,
                    WeatherStationId = station.Id,
                    MatchMethod = "DescriptionCode",
                    Confidence = matches.Count == 1 ? 85m : 50m,
                    IsVerified = false
                });
            }

            market.MappingStatus = matches.Count == 1 ? "Candidate" : "Ambiguous";
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Weather station mapping pass completed for {Count} pending markets.", pending.Count);
    }
}
