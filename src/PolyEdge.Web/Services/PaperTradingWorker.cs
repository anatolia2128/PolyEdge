using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PolyEdge.Web.Data;
using PolyEdge.Web.Hubs;
using PolyEdge.Web.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    private static readonly string[] Assets = ["BTC", "ETH", "SOL"];
    private static readonly TimeSpan MomentumLookback = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MaxPriceAge = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, (decimal Price, DateTime Time)> _samples = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastTraded = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Paper trading worker started. Enabled={Enabled}, Risk=${Risk}, MaxOpen={MaxOpen}, MomentumThreshold={Momentum:P5}, EntryRange={MinEntry}-{MaxEntry}, Scan={ScanSeconds}s",
            options.Enabled, options.RiskPerTradeUsd, options.MaxOpenPositions, options.MinMomentumPct,
            options.MinEntryPrice, options.MaxEntryPrice, options.ScanIntervalSeconds);

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
        var timer = Stopwatch.StartNew();
        var reasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Count(string reason) => reasons[reason] = reasons.GetValueOrDefault(reason) + 1;

        var markets = await polymarket.GetFiveMinuteMarketsAsync(ct);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts.FirstAsync(ct);
        var open = await db.Positions.Where(x => x.Status == "OPEN").ToListAsync(ct);
        var closedThisScan = 0;

        foreach (var position in open)
        {
            var quote = await polymarket.GetBookAsync(position.TokenId, ct);
            var expired = position.MarketEndUtc <= DateTime.UtcNow;
            if (quote is null)
            {
                Count("open_position_no_book");
                if (expired)
                {
                    ClosePosition(db, account, position, position.CurrentPrice, "MARKET_END_LAST_MARK");
                    closedThisScan++;
                }
                continue;
            }

            position.CurrentPrice = quote.Bid;
            position.UnrealizedPnl = quote.Bid * position.Shares - position.InvestedUsd;
            var returnPct = position.EntryPrice > 0 ? (quote.Bid - position.EntryPrice) / position.EntryPrice : 0;
            if (returnPct >= options.TakeProfitPct || returnPct <= -options.StopLossPct || expired)
            {
                var reason = expired ? "MARKET_END_MARK_TO_BID" : returnPct >= options.TakeProfitPct ? "TAKE_PROFIT" : "STOP_LOSS";
                ClosePosition(db, account, position, quote.Bid, reason);
                closedThisScan++;
                logger.LogInformation("Paper CLOSE {Asset} {Side}: entry={Entry:F4}, exit={Exit:F4}, return={Return:P2}, reason={Reason}",
                    position.Asset, position.Side, position.EntryPrice, quote.Bid, returnPct, reason);
            }
        }

        // Remove positions closed in this scan from the in-memory list.
        // They are still OPEN in SQLite until SaveChangesAsync below.
        open = open.Where(x => x.Status == "OPEN").ToList();
        var momentumByAsset = new Dictionary<string, decimal>();

        if (options.Enabled)
        {
            foreach (var asset in Assets)
            {
                if (!marketData.TryGetPrice(asset, out var spot))
                {
                    Count($"price_missing_{asset}");
                    continue;
                }
                var age = DateTime.UtcNow - spot.UpdatedAtUtc;
                if (age > MaxPriceAge)
                {
                    Count($"price_stale_{asset}");
                    logger.LogDebug("Skipping {Asset}: Binance price is stale ({AgeSeconds:F1}s)", asset, age.TotalSeconds);
                    continue;
                }

                var sample = _samples.GetOrAdd(asset, _ => (spot.Price, DateTime.UtcNow));
                if (DateTime.UtcNow - sample.Time < MomentumLookback)
                {
                    Count($"momentum_warmup_{asset}");
                    continue;
                }
                var momentum = sample.Price == 0 ? 0 : (spot.Price - sample.Price) / sample.Price;
                momentumByAsset[asset] = momentum;
                _samples[asset] = (spot.Price, DateTime.UtcNow);
                logger.LogDebug("Momentum {Asset}: start={StartPrice}, now={CurrentPrice}, change={Momentum:P5}, threshold={Threshold:P5}",
                    asset, sample.Price, spot.Price, momentum, options.MinMomentumPct);
            }
        }
        else Count("trading_disabled");

        var openCount = open.Count;
        var considered = 0;
        var entered = 0;

        if (options.Enabled)
        {
            foreach (var market in markets)
            {
                considered++;
                if (openCount >= options.MaxOpenPositions) { Count("blocked_max_open_positions"); break; }
                if (account.CashBalance < options.RiskPerTradeUsd) { Count("blocked_insufficient_cash"); break; }
                if (_lastTraded.TryGetValue(market.MarketId, out var last) && last > DateTime.UtcNow.AddMinutes(-5))
                { Count("blocked_market_cooldown"); continue; }
                if (open.Any(x => x.MarketId == market.MarketId)) { Count("blocked_already_open"); continue; }
                if (!momentumByAsset.TryGetValue(market.Asset, out var momentum))
                { Count($"blocked_no_momentum_{market.Asset}"); continue; }
                if (Math.Abs(momentum) < options.MinMomentumPct)
                { Count($"blocked_momentum_below_threshold_{market.Asset}"); continue; }

                var side = momentum > 0 ? "UP" : "DOWN";
                var token = side == "UP" ? market.UpTokenId : market.DownTokenId;
                var quote = await polymarket.GetBookAsync(token, ct);
                if (quote is null) { Count("blocked_no_valid_order_book"); continue; }
                if (quote.Ask < options.MinEntryPrice || quote.Ask > options.MaxEntryPrice || quote.Ask >= 0.99m || quote.Bid > quote.Ask)
                {
                    Count("blocked_entry_price_or_spread");
                    logger.LogDebug("Entry rejected {Asset} {Side}: bid={Bid}, ask={Ask}, allowed={Min}-{Max}",
                        market.Asset, side, quote.Bid, quote.Ask, options.MinEntryPrice, options.MaxEntryPrice);
                    continue;
                }

                var shares = decimal.Round(options.RiskPerTradeUsd / quote.Ask, 6);
                var cost = shares * quote.Ask;
                if (cost > account.CashBalance || shares <= 0) { Count("blocked_cost_or_shares"); continue; }

                account.CashBalance -= cost;
                account.UpdatedAtUtc = DateTime.UtcNow;
                var position = new PaperPosition
                {
                    MarketId = market.MarketId, ConditionId = market.ConditionId, TokenId = token,
                    Asset = market.Asset, Side = side, MarketQuestion = market.Question,
                    EntryPrice = quote.Ask, CurrentPrice = quote.Ask, Shares = shares,
                    InvestedUsd = cost, UnrealizedPnl = 0, OpenedAtUtc = DateTime.UtcNow,
                    MarketEndUtc = market.EndUtc, Status = "OPEN"
                };
                db.Positions.Add(position);
                _lastTraded[market.MarketId] = DateTime.UtcNow;
                open.Add(position);
                openCount++;
                entered++;
                logger.LogInformation("Paper BUY {Asset} {Side}: momentum={Momentum:P5}, bid={Bid:F4}, ask={Ask:F4}, shares={Shares}, cost=${Cost:F2}, marketEnds={EndUtc:O}, question={Question}",
                    market.Asset, side, momentum, quote.Bid, quote.Ask, shares, cost, market.EndUtc, market.Question);
            }
        }

        await db.SaveChangesAsync(ct);
        timer.Stop();
        var reasonSummary = reasons.Count == 0 ? "none" : string.Join(", ", reasons.OrderByDescending(x => x.Value).Select(x => $"{x.Key}={x.Value}"));
        logger.LogInformation("SCAN SUMMARY: markets={MarketCount}, considered={Considered}, open={OpenCount}, entered={Entered}, closed={Closed}, cash=${Cash:F2}, prices=[{Prices}], momentum=[{Momentum}], rejects=[{Rejects}], elapsed={ElapsedMs}ms",
            markets.Count, considered, openCount, entered, closedThisScan, account.CashBalance,
            FormatPrices(), FormatMomentum(momentumByAsset), reasonSummary, timer.ElapsedMilliseconds);
    }

    private string FormatPrices() => string.Join(", ", Assets.Select(asset =>
    {
        if (!marketData.TryGetPrice(asset, out var p)) return $"{asset}=MISSING";
        return $"{asset}={p.Price} (age={(DateTime.UtcNow - p.UpdatedAtUtc).TotalSeconds:F1}s)";
    }));

    private static string FormatMomentum(IReadOnlyDictionary<string, decimal> momentum) => momentum.Count == 0
        ? "warming-up/no-data" : string.Join(", ", momentum.Select(x => $"{x.Key}={x.Value:P5}"));

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
        logger.LogInformation("Wallet tracking worker started for {WalletCount} wallets", wallets.Length);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var received = 0;
                foreach (var wallet in wallets)
                {
                    var fills = await client.GetRecentWalletTradesAsync(wallet, stoppingToken);
                    received += fills.Count;
                    foreach (var fill in fills)
                    {
                        var tx = Read(fill, "transactionHash");
                        if (string.IsNullOrWhiteSpace(tx)) continue;
                        var ts = long.TryParse(Read(fill, "timestamp"), out var seconds)
                            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.UtcNow;
                        var exists = await db.WalletFills.AnyAsync(x => x.TransactionHash == tx && x.Wallet == wallet, stoppingToken);
                        if (exists) continue;
                        db.WalletFills.Add(new WalletFill
                        {
                            Wallet = wallet, TransactionHash = tx,
                            Asset = DetectAsset(Read(fill, "title") + " " + Read(fill, "slug")),
                            Side = Read(fill, "side"), Outcome = Read(fill, "outcome"), Market = Read(fill, "title"),
                            Price = Decimal(fill, "price"), Size = Decimal(fill, "size"), TimestampUtc = ts
                        });
                    }
                }
                await db.SaveChangesAsync(stoppingToken);
                logger.LogInformation("Wallet tracking cycle completed: wallets={WalletCount}, receivedFills={FillCount}", wallets.Length, received);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Wallet tracking cycle failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private static string Read(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "";
    private static decimal Decimal(JsonElement e, string name) => decimal.TryParse(Read(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    private static string DetectAsset(string s)
    {
        s = s.ToLowerInvariant();
        return s.Contains("bitcoin") || s.Contains("btc") ? "BTC" : s.Contains("ethereum") || s.Contains("eth") ? "ETH" : s.Contains("solana") || s.Contains("sol") ? "SOL" : "OTHER";
    }
}
