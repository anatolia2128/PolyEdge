using Microsoft.EntityFrameworkCore;
using PolyEdge.Web.Data;
using PolyEdge.Web.Hubs;
using PolyEdge.Web.Models;
using PolyEdge.Web.Options;
using PolyEdge.Web.Models.Weather;
using PolyEdge.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton<MarketDataService>();
builder.Services.AddHttpClient<PolymarketClient>(c => { c.Timeout = TimeSpan.FromSeconds(8); c.DefaultRequestHeaders.UserAgent.ParseAdd("PolyEdge/1.0"); });
builder.Services.Configure<TradingOptions>(builder.Configuration.GetSection("Trading"));
builder.Services.Configure<WeatherOptions>(builder.Configuration.GetSection("Weather"));
builder.Services.AddHttpClient("gamma-weather", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("PolyEdge-Weather/1.0");
});
builder.Services.AddHttpClient("weather-stations", c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("PolyEdge-Weather/1.0");
});
builder.Services.AddSingleton<WeatherMarketClassifier>();
builder.Services.AddSingleton<GammaWeatherMarketDiscovery>();
builder.Services.AddSingleton<WeatherStationCatalogService>();
builder.Services.AddScoped<WeatherStationMapper>();
var trading = builder.Configuration.GetSection("Trading").Get<TradingOptions>() ?? new TradingOptions();
builder.Services.AddSingleton(trading);
builder.Services.AddHostedService<BinanceWorker>();
builder.Services.AddHostedService<PaperTradingWorker>();
builder.Services.AddHostedService<WalletTrackingWorker>();
builder.Services.AddHostedService<WeatherDiscoveryWorker>();

var app = builder.Build();
var dbFile = app.Configuration.GetConnectionString("Default")!.Replace("Data Source=", "");
var dbDirectory = Path.GetDirectoryName(dbFile);
if (!string.IsNullOrWhiteSpace(dbDirectory)) Directory.CreateDirectory(dbDirectory);
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await WeatherSchemaInitializer.InitializeAsync(db);
    if (!await db.Accounts.AnyAsync())
    {
        var initial = trading.StartingBalance;
        db.Accounts.Add(new AccountState { StartingBalance = initial, CashBalance = initial });
        await db.SaveChangesAsync();
    }
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapHub<DashboardHub>("/hubs/dashboard");
app.MapGet("/health", () => Results.Ok(new { status = "healthy", utc = DateTime.UtcNow }));
app.MapGet("/api/dashboard", async (AppDbContext db, MarketDataService md) =>
{
    var account = await db.Accounts.FirstAsync();
    var positions = await db.Positions.Where(x => x.Status == "OPEN").OrderByDescending(x => x.OpenedAtUtc).ToListAsync();
    var trades = await db.Trades.OrderByDescending(x => x.ClosedAtUtc).Take(100).ToListAsync();
    var unrealized = positions.Sum(x => x.UnrealizedPnl);
    var wins = await db.Trades.CountAsync(x => x.PnlUsd > 0);
    var total = await db.Trades.CountAsync();
    return Results.Ok(new { startingBalance = account.StartingBalance, cashBalance = account.CashBalance,
        realizedPnl = account.RealizedPnl, unrealizedPnl = unrealized, equity = account.CashBalance + positions.Sum(x => x.InvestedUsd) + unrealized,
        openPositions = positions, recentTrades = trades, totalTrades = total, winRate = total == 0 ? 0 : (decimal)wins / total * 100,
        prices = md.Prices.ToDictionary(x => x.Key, x => x.Value.Price) });
});
app.MapGet("/api/wallet-fills", async (AppDbContext db) => Results.Ok(await db.WalletFills.OrderByDescending(x => x.TimestampUtc).Take(200).ToListAsync()));
app.MapGet("/api/weather/summary", async (AppDbContext db) =>
{
    var total = await db.WeatherMarkets.CountAsync();
    var active = await db.WeatherMarkets.CountAsync(x => x.IsActive && !x.IsClosed);
    var pending = await db.WeatherMarkets.CountAsync(x => x.MappingStatus == "Pending");
    var candidates = await db.WeatherMarkets.CountAsync(x => x.MappingStatus == "Candidate");
    var ambiguous = await db.WeatherMarkets.CountAsync(x => x.MappingStatus == "Ambiguous");
    var verified = await db.WeatherMarketStationMappings.CountAsync(x => x.IsVerified);
    var stations = await db.WeatherStations.CountAsync(x => x.IsActive);
    var recent = await db.WeatherMarkets.OrderByDescending(x => x.LastDiscoveredUtc).Take(30)
        .Select(x => new { x.Question, x.City, x.Country, x.Metric, x.Unit, x.EndDateUtc, x.MappingStatus, x.LastDiscoveredUtc })
        .ToListAsync();
    return Results.Ok(new { totalMarkets = total, activeMarkets = active, pendingMappings = pending,
        candidateMappings = candidates, ambiguousMappings = ambiguous, verifiedMappings = verified,
        activeStations = stations, recentMarkets = recent });
});
app.MapPost("/api/trading/toggle", (TradingOptions opts) => { opts.Enabled = !opts.Enabled; return Results.Ok(new { enabled = opts.Enabled }); });
app.MapPost("/api/trading/reset", async (AppDbContext db, TradingOptions opts) =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    db.Positions.RemoveRange(db.Positions);
    db.Trades.RemoveRange(db.Trades);
    db.WalletFills.RemoveRange(db.WalletFills);
    var account = await db.Accounts.FirstAsync();
    account.CashBalance = account.StartingBalance = opts.StartingBalance;
    account.RealizedPnl = 0;
    account.UpdatedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(); await tx.CommitAsync();
    return Results.Ok(new { reset = true, balance = account.CashBalance });
});
app.Run();
