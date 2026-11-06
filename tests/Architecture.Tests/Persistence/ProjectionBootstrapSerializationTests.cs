namespace Architecture.Tests.Persistence;

using Architecture.Tests.Support;
using Xunit;

[Trait("Category", "Architecture")]
public sealed class ProjectionBootstrapSerializationTests
{
    [Theory]
    [InlineData(
        "src/Modules/Reservations/BunkFy.Modules.Reservations.Persistence/Repositories/ReservationArrivalReminderRepository.cs",
        "await this.AcquirePropertyProjectionLockAsync(",
        2,
        "bunkfy:reservations:property-projection:")]
    [InlineData(
        "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Persistence/Repositories/IngestionPropertyProjectionRepository.cs",
        "await this.AcquirePropertyProjectionLockAsync(",
        3,
        "bunkfy:ingestion:property-projection:")]
    [InlineData(
        "src/Modules/DataRights/BunkFy.Modules.DataRights.Persistence/Repositories/DataRightsPropertyProjectionRepository.cs",
        "await this.AcquirePropertyProjectionLockAsync(",
        2,
        "bunkfy:data-rights:property-projection:")]
    [InlineData(
        "src/Modules/Staff/BunkFy.Modules.Staff.Persistence/Repositories/StaffPropertyProjectionRepository.cs",
        "await this.AcquirePropertyProjectionLockAsync(",
        1,
        "bunkfy:staff:property-projection:")]
    [InlineData(
        "src/Modules/Workspaces/BunkFy.Modules.Workspaces.Persistence/Repositories/WorkspacePropertyProjectionRepository.cs",
        "await this.AcquirePropertyProjectionLockAsync(",
        1,
        "bunkfy:workspaces:property-projection:")]
    public void Property_projection_writers_lock_before_first_write(
        string relativePath,
        string invocation,
        int invocationCount,
        string resourcePrefix)
    {
        string source = RepositoryPaths.Read(relativePath.Split('/'));

        Assert.Equal(invocationCount, Count(source, invocation));
        Assert.Contains(resourcePrefix, source, StringComparison.Ordinal);
        Assert.Contains("EfTransactionKeyLock.AcquireAsync(", source, StringComparison.Ordinal);
        Assert.Contains("Database.CurrentTransaction is null", source, StringComparison.Ordinal);
        Assert.Contains("!dbContext.Database.IsRelational()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_topology_writers_use_the_fixed_property_room_bed_lock_order()
    {
        string source = RepositoryPaths.Read(
            "src", "Modules", "Inventory",
            "BunkFy.Modules.Inventory.Persistence", "Repositories",
            "InventoryTopologyRepository.cs");

        Assert.Equal(3, Count(source, "await this.AcquireTopologyLocksAsync("));
        int property = source.IndexOf("PropertyLockPrefix +", StringComparison.Ordinal);
        int room = source.IndexOf("RoomLockPrefix +", StringComparison.Ordinal);
        int bed = source.IndexOf("BedLockPrefix +", StringComparison.Ordinal);
        Assert.True(property >= 0 && room > property && bed > room);
        Assert.Equal(3, Count(source, "EfTransactionKeyLock.AcquireAsync("));
        Assert.Contains("Database.CurrentTransaction is null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Guest_durable_operation_lock_serializes_its_first_row_creation()
    {
        string source = RepositoryPaths.Read(
            "src", "Modules", "Guests",
            "BunkFy.Modules.Guests.Persistence", "Repositories",
            "GuestOperationLockRepository.cs");

        Assert.Contains("bunkfy:guests:operation-lock:", source, StringComparison.Ordinal);
        Assert.Contains("EfTransactionKeyLock.AcquireAsync(", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("EfTransactionKeyLock.AcquireAsync(", StringComparison.Ordinal) <
            source.IndexOf("GuestOperationLock[] existing", StringComparison.Ordinal));
    }

    [Fact]
    public void Retention_keeps_its_existing_property_projection_coordinator()
    {
        string source = RepositoryPaths.Read(
            "src", "Modules", "Retention",
            "BunkFy.Modules.Retention.Application", "Handlers",
            "RetentionScopeMutationCoordinator.cs");

        Assert.Equal(2, Count(source, "AcquirePropertyTargetWriteAsync("));
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
