namespace PolyEdge.Web.Models.Weather;

public sealed class WeatherMarket
{
    public long Id { get; set; }
    public string GammaMarketId { get; set; } = "";
    public string? EventId { get; set; }
    public string Slug { get; set; } = "";
    public string Question { get; set; } = "";
    public string? Description { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? Metric { get; set; }
    public decimal? BucketMin { get; set; }
    public decimal? BucketMax { get; set; }
    public string? Unit { get; set; }
    public string? ResolutionSource { get; set; }
    public DateTime? EndDateUtc { get; set; }
    public bool IsActive { get; set; }
    public bool IsClosed { get; set; }
    public string MappingStatus { get; set; } = "Pending";
    public DateTime FirstDiscoveredUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastDiscoveredUtc { get; set; } = DateTime.UtcNow;
    public string? RawJson { get; set; }
    public ICollection<WeatherMarketStationMapping> StationMappings { get; set; } = new List<WeatherMarketStationMapping>();
}

public sealed class WeatherStation
{
    public long Id { get; set; }
    public string ICAO { get; set; } = "";
    public string? IATA { get; set; }
    public string Name { get; set; } = "";
    public string? City { get; set; }
    public string? Country { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? ElevationMeters { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public ICollection<WeatherMarketStationMapping> MarketMappings { get; set; } = new List<WeatherMarketStationMapping>();
}

public sealed class WeatherMarketStationMapping
{
    public long Id { get; set; }
    public long WeatherMarketId { get; set; }
    public WeatherMarket WeatherMarket { get; set; } = null!;
    public long WeatherStationId { get; set; }
    public WeatherStation WeatherStation { get; set; } = null!;
    public string MatchMethod { get; set; } = "DescriptionCode";
    public decimal Confidence { get; set; }
    public bool IsVerified { get; set; }
    public string? VerifiedBy { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
