using Microsoft.EntityFrameworkCore;
using PolyEdge.Web.Models;
using PolyEdge.Web.Models.Weather;

namespace PolyEdge.Web.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AccountState> Accounts => Set<AccountState>();
    public DbSet<PaperPosition> Positions => Set<PaperPosition>();
    public DbSet<PaperTrade> Trades => Set<PaperTrade>();
    public DbSet<WalletFill> WalletFills => Set<WalletFill>();
    public DbSet<WeatherMarket> WeatherMarkets => Set<WeatherMarket>();
    public DbSet<WeatherStation> WeatherStations => Set<WeatherStation>();
    public DbSet<WeatherMarketStationMapping> WeatherMarketStationMappings => Set<WeatherMarketStationMapping>();

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

        modelBuilder.Entity<WeatherMarket>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.GammaMarketId).IsUnique();
            entity.HasIndex(x => x.Slug);
            entity.HasIndex(x => new { x.IsActive, x.IsClosed });
            entity.HasIndex(x => x.MappingStatus);
            entity.Property(x => x.GammaMarketId).HasMaxLength(100);
            entity.Property(x => x.Slug).HasMaxLength(300);
            entity.Property(x => x.Question).HasMaxLength(1000);
            entity.Property(x => x.MappingStatus).HasMaxLength(30);
            entity.Property(x => x.BucketMin).HasPrecision(18, 4);
            entity.Property(x => x.BucketMax).HasPrecision(18, 4);
            entity.HasMany(x => x.StationMappings).WithOne(x => x.WeatherMarket)
                .HasForeignKey(x => x.WeatherMarketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WeatherStation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ICAO).IsUnique();
            entity.HasIndex(x => x.IATA);
            entity.Property(x => x.ICAO).HasMaxLength(4);
            entity.Property(x => x.IATA).HasMaxLength(3);
            entity.Property(x => x.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<WeatherMarketStationMapping>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WeatherMarketId, x.WeatherStationId }).IsUnique();
            entity.Property(x => x.MatchMethod).HasMaxLength(40);
            entity.Property(x => x.Confidence).HasPrecision(5, 2);
            entity.HasOne(x => x.WeatherStation).WithMany(x => x.MarketMappings)
                .HasForeignKey(x => x.WeatherStationId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
