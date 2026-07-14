using CryptoHft.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CryptoHft.Infrastructure.Persistence;

public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Signal> Signals => Set<Signal>();
    public DbSet<WalletSnapshot> Wallet => Set<WalletSnapshot>();
    public DbSet<RiskManagementSetting> RiskManagement => Set<RiskManagementSetting>();
    public DbSet<PersistedTradingSettings> TradingSettings => Set<PersistedTradingSettings>();
    public DbSet<AiUsageLog> AiUsage => Set<AiUsageLog>();
    public DbSet<NewsItem> News => Set<NewsItem>();
    public DbSet<NewsSentiment> NewsSentiment => Set<NewsSentiment>();
    public DbSet<AuditLog> Logs => Set<AuditLog>();
    public DbSet<AiDecisionLog> AiDecisionLogs => Set<AiDecisionLog>();
    public DbSet<FactorStat> FactorStats => Set<FactorStat>();
    public DbSet<ExecutionStat> ExecutionStats => Set<ExecutionStat>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("trading");
        modelBuilder.Entity<AiDecisionLog>().ToTable("AiDecisionLogs");
        modelBuilder.Entity<AiDecisionLog>().HasIndex(x => new { x.Evaluated, x.CreatedAt });
        modelBuilder.Entity<AiDecisionLog>().HasIndex(x => x.MatchedPositionId);
        modelBuilder.Entity<FactorStat>().ToTable("FactorStats");
        modelBuilder.Entity<FactorStat>().HasIndex(x => new { x.Regime, x.Factor }).IsUnique();
        modelBuilder.Entity<ExecutionStat>().ToTable("ExecutionStats");
        modelBuilder.Entity<ExecutionStat>().HasIndex(x => x.Regime).IsUnique();
        modelBuilder.Entity<User>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<PushDevice>().HasIndex(x => x.DeviceToken).IsUnique();
        modelBuilder.Entity<Order>().HasIndex(x => new { x.Symbol, x.CreatedAt });
        modelBuilder.Entity<Position>().HasIndex(x => new { x.Symbol, x.OpenedAt });
        modelBuilder.Entity<Signal>().HasIndex(x => new { x.Symbol, x.CreatedAt });
        modelBuilder.Entity<WalletSnapshot>().HasIndex(x => x.Timestamp);
        modelBuilder.Entity<AuditLog>().HasIndex(x => x.CreatedAt);
        modelBuilder.Entity<PersistedTradingSettings>().ToTable("TradingSettings");
        modelBuilder.Entity<PersistedTradingSettings>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<AiUsageLog>().ToTable("AiUsage");
        modelBuilder.Entity<AiUsageLog>().HasIndex(x => x.CreatedAt);
    }
}
