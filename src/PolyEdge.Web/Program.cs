using Microsoft.EntityFrameworkCore;
using PolyEdge.Web.Data;
using PolyEdge.Web.Hubs;
using PolyEdge.Web.Models;
using PolyEdge.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton<MarketDataService>();
builder.Services.AddHttpClient<PolymarketClient>(c => { c.Timeout = TimeSpan.FromSeconds(8); c.DefaultRequestHeaders.UserAgent.ParseAdd("PolyEdge/1.0"); });
builder.Services.Configure<TradingOptions>(builder.Configuration.GetSection("Trading"));
var trading = builder.Configuration.GetSection("Trading").Get<TradingOptions>() ?? new TradingOptions();
builder.Services.AddSingleton(trading);
builder.Services.AddHostedService<BinanceWorker>();
builder.Services.AddHostedService<PaperTradingWorker>();
builder.Services.AddHostedService<WalletTrackingWorker>();

var app = builder.Build();
var dbFile = app.Configuration.GetConnectionString("Default")!.Replace("Data Source=", "");
var dbDirectory = Path.GetDirectoryName(dbFile);
if (!string.IsNullOrWhiteSpace(dbDirectory)) Directory.CreateDirectory(dbDirectory);
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
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
