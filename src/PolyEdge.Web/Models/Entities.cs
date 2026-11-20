namespace PolyEdge.Web.Models;

public sealed class AccountState
{
    public int Id { get; set; } = 1;
    public decimal StartingBalance { get; set; } = 100m;
    public decimal CashBalance { get; set; } = 100m;
    public decimal RealizedPnl { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PaperPosition
{
    public long Id { get; set; }
    public string MarketId { get; set; } = "";
    public string ConditionId { get; set; } = "";
    public string TokenId { get; set; } = "";
    public string Asset { get; set; } = "";
    public string Side { get; set; } = "";
    public string MarketQuestion { get; set; } = "";
    public decimal EntryPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal Shares { get; set; }
    public decimal InvestedUsd { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime MarketEndUtc { get; set; }
    public string Status { get; set; } = "OPEN";
}

public sealed class PaperTrade
{
    public long Id { get; set; }
    public string MarketId { get; set; } = "";
    public string TokenId { get; set; } = "";
    public string Asset { get; set; } = "";
    public string Side { get; set; } = "";
    public string MarketQuestion { get; set; } = "";
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal Shares { get; set; }
    public decimal InvestedUsd { get; set; }
    public decimal PnlUsd { get; set; }
    public decimal PnlPct { get; set; }
    public string ExitReason { get; set; } = "";
    public DateTime OpenedAtUtc { get; set; }
    public DateTime ClosedAtUtc { get; set; }
}

public sealed class WalletFill
{
    public long Id { get; set; }
    public string Wallet { get; set; } = "";
    public string Asset { get; set; } = "UNKNOWN";
    public string Side { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Market { get; set; } = "";
    public decimal Price { get; set; }
    public decimal Size { get; set; }
    public string TransactionHash { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
}

public sealed record MarketSnapshot(string MarketId, string ConditionId, string Question, string Asset,
    string UpTokenId, string DownTokenId, DateTime EndUtc, bool IsActive);
public sealed record BookQuote(decimal Bid, decimal Ask);
public sealed record AssetPrice(decimal Price, decimal PreviousPrice, DateTime UpdatedAtUtc);
