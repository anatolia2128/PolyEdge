using System.Text.Json;

namespace PolyEdge.Web.Models.Weather;

public sealed class GammaMarketDto
{
    public string? Id { get; set; }
    public string? Question { get; set; }
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public string? EndDate { get; set; }
    public string? EndDateIso { get; set; }
    public bool? Active { get; set; }
    public bool? Closed { get; set; }
    public JsonElement Events { get; set; }
}
