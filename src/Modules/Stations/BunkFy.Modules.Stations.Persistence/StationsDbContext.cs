namespace BunkFy.Modules.Stations.Persistence;

using BunkFy.Modules.Stations.Domain;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;

public sealed class StationsDbContext(DbContextOptions<StationsDbContext> options, IScopeContext scopeContext)
    : ScopeAwareDbContext<StationsDbContext>(options, scopeContext)
{
    public DbSet<Station> Stations => this.Set<Station>();
    public DbSet<StationBrowserSession> BrowserSessions => this.Set<StationBrowserSession>();
    public DbSet<StationStaffCredential> Credentials => this.Set<StationStaffCredential>();
    public DbSet<StationSetupGrant> SetupGrants => this.Set<StationSetupGrant>();
    public DbSet<StationStaffCheckInGrant> CheckInGrants => this.Set<StationStaffCheckInGrant>();
    public DbSet<StationStaffCheckOutGrant> CheckOutGrants => this.Set<StationStaffCheckOutGrant>();
    public DbSet<StationOperationReceipt> OperationReceipts => this.Set<StationOperationReceipt>();
    public DbSet<StationStaffRegistration> StaffRegistrations => this.Set<StationStaffRegistration>();
    internal DbSet<StationsTenantLifecycleState> Lifecycle => this.Set<StationsTenantLifecycleState>();
    public void RequirePostgreSql()
    {
        if (this.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            throw new NotSupportedException("Stations core requires PostgreSQL.");
        }
    }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(StationsMigrations.Schema);
        StationsConfigurations.Configure(modelBuilder);
        this.ApplyScopeConventions(modelBuilder);
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        this.ValidateWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        this.ValidateWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    private void ValidateWrites()
    {
        this.RequirePostgreSql();
        StationRules.Coordinates(this.CurrentScopeId);
        if (!this.ScopeFilterEnabled)
        {
            throw new InvalidOperationException("Stations require a current tenant scope.");
        }

        if (this.ChangeTracker.Entries<StationOperationReceipt>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Station receipts are append-only outside the future governed destruction owner.");
        }
    }
}
