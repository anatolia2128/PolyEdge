using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PolyEdge.Web.Data;
using PolyEdge.Web.Hubs;
using PolyEdge.Web.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace PolyEdge.Web.Services;

public sealed class TradingOptions
{
    public decimal StartingBalance { get; set; } = 100m;
    public decimal RiskPerTradeUsd { get; set; } = 5m;
    public int MaxOpenPositions { get; set; } = 3;
    public decimal MinMomentumPct { get; set; } = 0.00015m;
    public decimal MinEntryPrice { get; set; } = 0.25m;
    public decimal MaxEntryPrice { get; set; } = 0.72m;
    public decimal TakeProfitPct { get; set; } = 0.12m;
    public decimal StopLossPct { get; set; } = 0.18m;
    public int ScanIntervalSeconds { get; set; } = 5;
    public bool Enabled { get; set; } = true;
}

public sealed class PaperTradingWorker(IServiceScopeFactory scopeFactory, MarketDataService marketData,
    PolymarketClient polymarket, TradingOptions options, IHubContext<DashboardHub> hub,
    ILogger<PaperTradingWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, (decimal Price, DateTime Time)> _samples = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastTraded = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
                await hub.Clients.All.SendAsync("dashboardUpdated", cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Paper trading scan failed"); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, options.ScanIntervalSeconds)), stoppingToken);
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        var markets = await polymarket.GetFiveMinuteMarketsAsync(ct);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts.FirstAsync(ct);

        var open = await db.Positions.Where(x => x.Status == "OPEN").ToListAsync(ct);
        foreach (var position in open)
        {
            var quote = await polymarket.GetBookAsync(position.TokenId, ct);
            var expired = position.MarketEndUtc <= DateTime.UtcNow;
            if (quote is null)
            {
                if (expired) ClosePosition(db, account, position, position.CurrentPrice, "MARKET_END_LAST_MARK");
                continue;
            }
            position.CurrentPrice = quote.Bid;
            position.UnrealizedPnl = quote.Bid * position.Shares - position.InvestedUsd;
            var returnPct = position.EntryPrice > 0 ? (quote.Bid - position.EntryPrice) / position.EntryPrice : 0;
            if (returnPct >= options.TakeProfitPct || returnPct <= -options.StopLossPct || expired)
                ClosePosition(db, account, position, quote.Bid, expired ? "MARKET_END_MARK_TO_BID" : returnPct >= options.TakeProfitPct ? "TAKE_PROFIT" : "STOP_LOSS");
        }

        if (options.Enabled)
        {
            open = await db.Positions.Where(x => x.Status == "OPEN").ToListAsync(ct);
            var momentumByAsset = new Dictionary<string, decimal>();
            foreach (var asset in new[] { "BTC", "ETH", "SOL" })
            {
                if (!marketData.TryGetPrice(asset, out var spot)) continue;
                var sample = _samples.GetOrAdd(asset, _ => (spot.Price, DateTime.UtcNow));
                if (DateTime.UtcNow - sample.Time < TimeSpan.FromSeconds(12)) continue;
                momentumByAsset[asset] = sample.Price == 0 ? 0 : (spot.Price - sample.Price) / sample.Price;
                _samples[asset] = (spot.Price, DateTime.UtcNow);
            }
            foreach (var market in markets)
            {
                if (open.Count >= options.MaxOpenPositions || account.CashBalance < options.RiskPerTradeUsd) break;
                if (_lastTraded.TryGetValue(market.MarketId, out var last) && last > DateTime.UtcNow.AddMinutes(-5)) continue;
                if (open.Any(x => x.MarketId == market.MarketId)) continue;
                if (!momentumByAsset.TryGetValue(market.Asset, out var momentum) || Math.Abs(momentum) < options.MinMomentumPct) continue;
                var side = momentum > 0 ? "UP" : "DOWN";
                var token = side == "UP" ? market.UpTokenId : market.DownTokenId;
                var quote = await polymarket.GetBookAsync(token, ct);
                if (quote is null || quote.Ask < options.MinEntryPrice || quote.Ask > options.MaxEntryPrice || quote.Ask >= 0.99m) continue;
                var shares = decimal.Round(options.RiskPerTradeUsd / quote.Ask, 6);
                var cost = shares * quote.Ask;
                if (cost > account.CashBalance || shares <= 0) continue;
                account.CashBalance -= cost;
                account.UpdatedAtUtc = DateTime.UtcNow;
                db.Positions.Add(new PaperPosition
                {
                    MarketId = market.MarketId, ConditionId = market.ConditionId, TokenId = token,
                    Asset = market.Asset, Side = side, MarketQuestion = market.Question,
                    EntryPrice = quote.Ask, CurrentPrice = quote.Ask, Shares = shares,
                    InvestedUsd = cost, UnrealizedPnl = 0, OpenedAtUtc = DateTime.UtcNow,
                    MarketEndUtc = market.EndUtc, Status = "OPEN"
                });
                _lastTraded[market.MarketId] = DateTime.UtcNow;
                logger.LogInformation("Paper BUY {Asset} {Side} at {Price}; cost {Cost}", market.Asset, side, quote.Ask, cost);
                open.Add(new PaperPosition { MarketId = market.MarketId });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static void ClosePosition(AppDbContext db, AccountState account, PaperPosition p, decimal exitPrice, string reason)
    {
        var proceeds = exitPrice * p.Shares;
        var pnl = proceeds - p.InvestedUsd;
        account.CashBalance += proceeds;
        account.RealizedPnl += pnl;
        account.UpdatedAtUtc = DateTime.UtcNow;
        p.Status = "CLOSED";
        p.CurrentPrice = exitPrice;
        p.UnrealizedPnl = 0;
        db.Trades.Add(new PaperTrade
        {
            MarketId = p.MarketId, TokenId = p.TokenId, Asset = p.Asset, Side = p.Side,
            MarketQuestion = p.MarketQuestion, EntryPrice = p.EntryPrice, ExitPrice = exitPrice,
            Shares = p.Shares, InvestedUsd = p.InvestedUsd, PnlUsd = pnl,
            PnlPct = p.InvestedUsd == 0 ? 0 : pnl / p.InvestedUsd * 100,
            ExitReason = reason, OpenedAtUtc = p.OpenedAtUtc, ClosedAtUtc = DateTime.UtcNow
        });
    }
}

public sealed class WalletTrackingWorker(IServiceScopeFactory scopeFactory, PolymarketClient client,
    IConfiguration configuration, ILogger<WalletTrackingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wallets = configuration.GetSection("Wallets").Get<string[]>() ?? [];
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                foreach (var wallet in wallets)
                {
                    var fills = await client.GetRecentWalletTradesAsync(wallet, stoppingToken);
                    foreach (var fill in fills)
                    {
                        var tx = Read(fill, "transactionHash");
                        if (string.IsNullOrWhiteSpace(tx)) continue;
                        var ts = long.TryParse(Read(fill, "timestamp"), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.UtcNow;
                        var exists = !string.IsNullOrWhiteSpace(tx) && await db.WalletFills.AnyAsync(x => x.TransactionHash == tx && x.Wallet == wallet, stoppingToken);
                        if (exists) continue;
                        db.WalletFills.Add(new WalletFill
                        {
                            Wallet = wallet, TransactionHash = tx, Asset = DetectAsset(Read(fill, "title") + " " + Read(fill, "slug")),
                            Side = Read(fill, "side"), Outcome = Read(fill, "outcome"), Market = Read(fill, "title"),
                            Price = Decimal(fill, "price"), Size = Decimal(fill, "size"), TimestampUtc = ts
                        });
                    }
                }
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Wallet tracking cycle failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
    private static string Read(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "";
    private static decimal Decimal(JsonElement e, string name) => decimal.TryParse(Read(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    private static string DetectAsset(string s) { s = s.ToLowerInvariant(); return s.Contains("bitcoin") || s.Contains("btc") ? "BTC" : s.Contains("ethereum") || s.Contains("eth") ? "ETH" : s.Contains("solana") || s.Contains("sol") ? "SOL" : "OTHER"; }
}
