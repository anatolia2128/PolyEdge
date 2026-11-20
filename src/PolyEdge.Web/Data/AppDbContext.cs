using Microsoft.EntityFrameworkCore;
using PolyEdge.Web.Models;

namespace PolyEdge.Web.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AccountState> Accounts => Set<AccountState>();
    public DbSet<PaperPosition> Positions => Set<PaperPosition>();
    public DbSet<PaperTrade> Trades => Set<PaperTrade>();
    public DbSet<WalletFill> WalletFills => Set<WalletFill>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountState>().HasKey(x => x.Id);
        modelBuilder.Entity<AccountState>().Property(x => x.CashBalance).HasPrecision(18, 8);
        foreach (var entity in new[] { typeof(PaperPosition), typeof(PaperTrade), typeof(WalletFill) })
        {
            foreach (var property in entity.GetProperties().Where(p => p.PropertyType == typeof(decimal)))
                modelBuilder.Entity(entity).Property(property.Name).HasPrecision(18, 8);
        }
        modelBuilder.Entity<PaperPosition>().HasIndex(x => new { x.MarketId, x.Status });
        modelBuilder.Entity<PaperTrade>().HasIndex(x => x.ClosedAtUtc);
        modelBuilder.Entity<WalletFill>().HasIndex(x => new { x.Wallet, x.TimestampUtc });
    }
}
