using CryptoHft.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CryptoHft.Infrastructure.Persistence;

public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Signal> Signals => Set<Signal>();
    public DbSet<WalletSnapshot> Wallet => Set<WalletSnapshot>();
    public DbSet<RiskManagementSetting> RiskManagement => Set<RiskManagementSetting>();
    public DbSet<NewsItem> News => Set<NewsItem>();
    public DbSet<NewsSentiment> NewsSentiment => Set<NewsSentiment>();
    public DbSet<AuditLog> Logs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("trading");
        modelBuilder.Entity<User>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<Order>().HasIndex(x => new { x.Symbol, x.CreatedAt });
        modelBuilder.Entity<Position>().HasIndex(x => new { x.Symbol, x.OpenedAt });
        modelBuilder.Entity<Signal>().HasIndex(x => new { x.Symbol, x.CreatedAt });
        modelBuilder.Entity<WalletSnapshot>().HasIndex(x => x.Timestamp);
        modelBuilder.Entity<AuditLog>().HasIndex(x => x.CreatedAt);
    }
}
