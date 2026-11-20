using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PolyEdge.Web.Models;

namespace PolyEdge.Web.Services;

public sealed class MarketDataService(ILogger<MarketDataService> logger)
{
    private readonly ConcurrentDictionary<string, AssetPrice> _prices = new();
    private readonly ConcurrentDictionary<string, BookQuote> _books = new();
    public IReadOnlyDictionary<string, AssetPrice> Prices => _prices;
    public bool TryGetPrice(string asset, out AssetPrice price) => _prices.TryGetValue(asset, out price!);
    public bool TryGetBook(string tokenId, out BookQuote quote) => _books.TryGetValue(tokenId, out quote!);
    public void SetBook(string tokenId, BookQuote quote) => _books[tokenId] = quote;

    public async Task RunBinanceAsync(CancellationToken ct)
    {
        const string url = "wss://stream.binance.com:9443/stream?streams=btcusdt@bookTicker/ethusdt@bookTicker/solusdt@bookTicker";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(url), ct);
                logger.LogInformation("Connected to Binance market stream");
                var buffer = new byte[16 * 1024];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    using var doc = JsonDocument.Parse(ms.ToArray());
                    var data = doc.RootElement.GetProperty("data");
                    var symbol = data.GetProperty("s").GetString() ?? "";
                    var asset = symbol.StartsWith("BTC") ? "BTC" : symbol.StartsWith("ETH") ? "ETH" : symbol.StartsWith("SOL") ? "SOL" : "";
                    if (asset.Length == 0) continue;
                    var bid = decimal.Parse(data.GetProperty("b").GetString()!, CultureInfo.InvariantCulture);
                    var ask = decimal.Parse(data.GetProperty("a").GetString()!, CultureInfo.InvariantCulture);
                    var mid = (bid + ask) / 2m;
                    _prices.AddOrUpdate(asset, _ => new(mid, mid, DateTime.UtcNow), (_, old) => new(mid, old.Price, DateTime.UtcNow));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Binance stream disconnected; reconnecting"); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}

public sealed class BinanceWorker(MarketDataService data) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => data.RunBinanceAsync(stoppingToken);
}
