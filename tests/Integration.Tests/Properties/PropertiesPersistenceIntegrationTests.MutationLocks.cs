namespace Integration.Tests;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Workspaces.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class PropertiesPersistenceIntegrationTests
{
    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Operation_locks_serialize_aggregate_and_unique_coordinate_writers()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_properties_operation_lock_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        using ServiceProvider provider = CreatePersistenceProvider(
            postgreSql.GetConnectionString());

        Property property;
        Room room;
        using (IServiceScope seedScope = provider.CreateScope())
        {
            WorkspacesDbContext workspaces = seedScope.ServiceProvider
                .GetRequiredService<WorkspacesDbContext>();
            PropertiesDbContext propertiesDb = seedScope.ServiceProvider
                .GetRequiredService<PropertiesDbContext>();
            await workspaces.Database.MigrateAsync().ConfigureAwait(false);
            await propertiesDb.Database.MigrateAsync().ConfigureAwait(false);
            property = CreateProperty("lock-hostel", "Lock Hostel");
            room = Room.Create(
                Guid.NewGuid(),
                TenantA,
                property.Id,
                "4A",
                null,
                null,
                Guid.NewGuid(),
                ExportNowUtc).Value;
            await seedScope.ServiceProvider
                .GetRequiredService<IPropertyRepository>()
                .AddAsync(property, CancellationToken.None)
                .ConfigureAwait(false);
            await seedScope.ServiceProvider
                .GetRequiredService<IRoomRepository>()
                .AddAsync(room, CancellationToken.None)
                .ConfigureAwait(false);
            await AppendCreatedTimeZoneOperationAsync(
                    seedScope.ServiceProvider,
                    property,
                    "system:properties-operation-lock-test")
                .ConfigureAwait(false);
            await propertiesDb.SaveChangesAsync().ConfigureAwait(false);
        }

        using IServiceScope firstScope = provider.CreateScope();
        using IServiceScope secondScope = provider.CreateScope();
        PropertiesDbContext firstDb = firstScope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        PropertiesDbContext secondDb = secondScope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        IPropertiesOperationLock firstLock = firstScope.ServiceProvider
            .GetRequiredService<IPropertiesOperationLock>();
        IPropertiesOperationLock secondLock = secondScope.ServiceProvider
            .GetRequiredService<IPropertiesOperationLock>();
        await using IDbContextTransaction firstTransaction =
            await firstDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);
        await using IDbContextTransaction secondTransaction =
            await secondDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);

        Assert.True(await firstLock.TryAcquireRoomAsync(
            TenantA,
            room.Id,
            CancellationToken.None).ConfigureAwait(false));
        Task<bool> secondAcquisition = secondLock.TryAcquireRoomAsync(
            TenantA,
            room.Id,
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(secondAcquisition.IsCompleted);

        await firstTransaction.CommitAsync().ConfigureAwait(false);
        Assert.True(await secondAcquisition
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false));
        await secondTransaction.CommitAsync().ConfigureAwait(false);

        using (IServiceScope verificationScope = provider.CreateScope())
        {
            PropertiesDbContext verification = verificationScope
                .ServiceProvider
                .GetRequiredService<PropertiesDbContext>();
            long revision = await verification.Database.SqlQuery<long>($"""
                    SELECT "Revision" AS "Value"
                    FROM properties.room_operation_locks
                    WHERE "RoomId" = {room.Id}
                    """)
                .SingleAsync()
                .ConfigureAwait(false);
            Assert.Equal(3, revision);
        }

        using IServiceScope firstCoordinateScope = provider.CreateScope();
        using IServiceScope secondCoordinateScope = provider.CreateScope();
        PropertiesDbContext firstCoordinateDb = firstCoordinateScope
            .ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        PropertiesDbContext secondCoordinateDb = secondCoordinateScope
            .ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        IPropertiesUniqueCoordinateLock firstCoordinate =
            firstCoordinateScope.ServiceProvider
                .GetRequiredService<IPropertiesUniqueCoordinateLock>();
        IPropertiesUniqueCoordinateLock secondCoordinate =
            secondCoordinateScope.ServiceProvider
                .GetRequiredService<IPropertiesUniqueCoordinateLock>();
        await using IDbContextTransaction firstCoordinateTransaction =
            await firstCoordinateDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);
        await using IDbContextTransaction secondCoordinateTransaction =
            await secondCoordinateDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false);

        await firstCoordinate.AcquirePropertyCodeAsync(
            TenantA,
            property.Code.Value,
            CancellationToken.None).ConfigureAwait(false);
        Task secondCoordinateAcquisition =
            secondCoordinate.AcquirePropertyCodeAsync(
                TenantA,
                property.Code.Value,
                CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(secondCoordinateAcquisition.IsCompleted);

        await firstCoordinateTransaction.CommitAsync().ConfigureAwait(false);
        await secondCoordinateAcquisition
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        await secondCoordinateTransaction.CommitAsync().ConfigureAwait(false);
    }
}
