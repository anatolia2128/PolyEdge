namespace PolyEdge.Web.Options;

public sealed class WeatherOptions
{
    public bool Enabled { get; set; } = true;
    public int DiscoveryIntervalMinutes { get; set; } = 30;
    public int StationRefreshHours { get; set; } = 24;
    public int GammaPageSize { get; set; } = 500;
    public string GammaMarketsUrl { get; set; } = "https://gamma-api.polymarket.com/markets";
    public string StationCacheUrl { get; set; } = "https://aviationweather.gov/data/cache/stations.cache.json.gz";
}
