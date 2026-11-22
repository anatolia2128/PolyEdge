using System.Globalization;
using System.Text.RegularExpressions;
using PolyEdge.Web.Models.Weather;

namespace PolyEdge.Web.Services;

public sealed class WeatherMarketClassifier
{
    private static readonly string[] WeatherTerms =
    ["weather", "temperature", "rainfall", "precipitation", "snowfall", "snow", "wind speed", "humidity", "high temperature", "low temperature", "hottest", "coldest"];

    public bool IsWeatherMarket(string? question, string? description)
    {
        var text = $"{question} {description}".ToLowerInvariant();
        return WeatherTerms.Any(term => text.Contains(term, StringComparison.Ordinal)) || Regex.IsMatch(text, @"\btemp\b", RegexOptions.IgnoreCase);
    }

    public void Classify(WeatherMarket market)
    {
        var text = $"{market.Question} {market.Description}";
        var lower = text.ToLowerInvariant();

        market.Metric = lower.Contains("rain") || lower.Contains("precipitation") ? "Precipitation" :
            lower.Contains("snow") ? "Snowfall" : lower.Contains("wind") ? "WindSpeed" :
            lower.Contains("humidity") ? "Humidity" : lower.Contains("temperature") || Regex.IsMatch(text, @"\btemp\b", RegexOptions.IgnoreCase) || lower.Contains("hottest") || lower.Contains("coldest") ? "Temperature" : null;

        market.Unit = lower.Contains("fahrenheit") || Regex.IsMatch(text, @"°\s*F|\bFahrenheit\b", RegexOptions.IgnoreCase) ? "F" :
            lower.Contains("celsius") || Regex.IsMatch(text, @"°\s*C|\bCelsius\b", RegexOptions.IgnoreCase) ? "C" :
            lower.Contains("millimeter") || lower.Contains(" mm") ? "mm" :
            lower.Contains("inch") ? "in" : lower.Contains("mph") ? "mph" : null;

        var city = Regex.Match(market.Question,
            @"\b(?:in|for|at)\s+([A-Z][A-Za-z .'-]{2,35}?)(?=\s+(?:be|reach|hit|exceed|above|below|over|under|on|by|this|tomorrow|today)|[?,]|$)");
        if (city.Success) market.City = city.Groups[1].Value.Trim().TrimEnd('.', ',');

        var source = Regex.Match(text,
            @"(?:measured|reported|recorded|observed)\s+(?:at|by|from)\s+([^.;]{3,180})",
            RegexOptions.IgnoreCase);
        if (source.Success) market.ResolutionSource = source.Groups[1].Value.Trim();

        var range = Regex.Match(market.Question, @"(?<min>-?\d+(?:\.\d+)?)\s*(?:to|-)\s*(?<max>-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (range.Success && decimal.TryParse(range.Groups["min"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var min) &&
            decimal.TryParse(range.Groups["max"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var max))
        {
            market.BucketMin = min;
            market.BucketMax = max;
        }

        // Resolution source is deliberately not inferred from city/title.
        market.ResolutionSource = null;
    }
}
