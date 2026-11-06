namespace BunkFy.Modules.Reservations.Tests.Persistence;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Reservations.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationStayAmendmentPersistenceTests
{
    private static readonly Guid PropertyId = Guid.Parse(
        "10000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset RequestedAtUtc = new(
        2026,
        8,
        12,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Model_is_scoped_concurrency_guarded_and_owned_by_management_journal()
    {
        using ReservationsDbContext dbContext = CreateInMemoryContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType amendment = designModel.FindEntityType(
            typeof(ReservationStayAmendmentOperation))!;
        IEntityType management = designModel.FindEntityType(
            typeof(ReservationManagementOperation))!;

        Assert.Equal("stay_amendment_operations", amendment.GetTableName());
        Assert.Equal(
            [
                nameof(ReservationStayAmendmentOperation.ScopeId),
                nameof(ReservationStayAmendmentOperation.ReservationId),
                nameof(ReservationStayAmendmentOperation.Id)
            ],
            amendment.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.NotEmpty(amendment.GetDeclaredQueryFilters());
        Assert.True(amendment.FindProperty(
            nameof(ReservationStayAmendmentOperation.OperationVersion))!
            .IsConcurrencyToken);
        Assert.True(amendment.FindProperty(
            nameof(ReservationStayAmendmentOperation.RequestedBy))!
            .IsNullable);
        Assert.Equal(
            ReservationStayAmendmentOperation.RequestFingerprintLength,
            amendment.FindProperty(
                nameof(ReservationStayAmendmentOperation.RequestFingerprint))!
                .GetMaxLength());
        Assert.True(amendment.FindProperty(
            nameof(ReservationStayAmendmentOperation.RequestFingerprint))!
            .IsFixedLength());
        Assert.Equal(
            ReservationStayAmendmentOperation.TargetInventoryUnitIdsMaxLength,
            amendment.FindProperty(
                nameof(ReservationStayAmendmentOperation.TargetInventoryUnitIds))!
                .GetMaxLength());
        Assert.Equal(
            0,
            amendment.FindProperty(
                nameof(ReservationStayAmendmentOperation.TargetExpectedArrivalTime))!
                .GetPrecision());
        Assert.Equal(
            0,
            amendment.FindProperty(
                nameof(ReservationStayAmendmentOperation.TargetExpectedDepartureTime))!
                .GetPrecision());

        IForeignKey owner = Assert.Single(
            amendment.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType == management);
        Assert.True(owner.IsUnique);
        Assert.True(owner.IsRequired);
        Assert.Equal(DeleteBehavior.Cascade, owner.DeleteBehavior);
        Assert.Equal(
            [
                nameof(ReservationStayAmendmentOperation.ScopeId),
                nameof(ReservationStayAmendmentOperation.ReservationId),
                nameof(ReservationStayAmendmentOperation.Id)
            ],
            owner.Properties.Select(property => property.Name));
        Assert.Contains(
            amendment.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(ReservationStayAmendmentOperation.ScopeId),
                    nameof(ReservationStayAmendmentOperation.PropertyId),
                    nameof(ReservationStayAmendmentOperation.Outcome),
                    nameof(ReservationStayAmendmentOperation.UpdatedAtUtc),
                    nameof(ReservationStayAmendmentOperation.Id),
                    nameof(ReservationStayAmendmentOperation.ReservationId)
                ]));

        string[] constraints =
        [
            "CK_stay_amendment_operations_request",
            "CK_stay_amendment_operations_target",
            "CK_stay_amendment_operations_outcome",
            "CK_stay_amendment_operations_timestamps",
            "CK_stay_amendment_operations_reconciliation",
            "CK_stay_amendment_operations_version"
        ];
        Assert.All(
            constraints,
            constraint => Assert.Contains(
                amendment.GetCheckConstraints(),
                candidate => candidate.Name == constraint));
        IReadOnlyCheckConstraint requestConstraint = Assert.Single(
            amendment.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_stay_amendment_operations_request");
        IReadOnlyCheckConstraint targetConstraint = Assert.Single(
            amendment.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_stay_amendment_operations_target");
        Assert.Contains(
            "\"RequestedBy\" IS NULL",
            requestConstraint.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"TargetInventoryUnitIds\" IS NULL",
            targetConstraint.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"TargetInventoryUnitIds\" IS NOT NULL",
            targetConstraint.Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_tracks_operation_version_for_unit_of_work_concurrency()
    {
        InMemoryDatabaseRoot databaseRoot = new();
        string databaseName = $"stay-amendment-concurrency-{Guid.NewGuid():N}";
        Guid operationId = Guid.Parse(
            "20000000-0000-0000-0000-000000000001");
        Guid reservationId = Guid.Parse(
            "30000000-0000-0000-0000-000000000001");

        await using (ReservationsDbContext seed = CreateInMemoryContext(
                         databaseName,
                         databaseRoot))
        {
            ReservationStayAmendmentOperationRepository repository = new(seed);
            await repository.AddAsync(
                CreatePending(operationId, reservationId, RequestedAtUtc),
                CancellationToken.None);
            await seed.SaveChangesAsync();
        }

        await using ReservationsDbContext firstContext = CreateInMemoryContext(
            databaseName,
            databaseRoot);
        await using ReservationsDbContext competingContext =
            CreateInMemoryContext(databaseName, databaseRoot);
        ReservationStayAmendmentOperationRepository firstRepository =
            new(firstContext);
        ReservationStayAmendmentOperationRepository competingRepository =
            new(competingContext);
        ReservationStayAmendmentOperation first = Assert.IsType<
            ReservationStayAmendmentOperation>(
            await firstRepository.GetAsync(
                PropertyId,
                reservationId,
                operationId,
                CancellationToken.None));
        ReservationStayAmendmentOperation competing = Assert.IsType<
            ReservationStayAmendmentOperation>(
            await competingRepository.GetAsync(
                PropertyId,
                reservationId,
                operationId,
                CancellationToken.None));

        Assert.Equal(EntityState.Unchanged, firstContext.Entry(first).State);
        Assert.True(first.Reconcile(
            expectedOperationVersion: 1,
            "staff:first",
            RequestedAtUtc.AddMinutes(5)).IsSuccess);
        Assert.Equal(EntityState.Modified, firstContext.Entry(first).State);
        Assert.Equal(
            1L,
            firstContext.Entry(first).Property(
                operation => operation.OperationVersion).OriginalValue);
        Assert.Equal(2L, first.OperationVersion);
        await firstContext.SaveChangesAsync();

        Assert.True(competing.Reconcile(
            expectedOperationVersion: 1,
            "staff:competing",
            RequestedAtUtc.AddMinutes(6)).IsSuccess);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => competingContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Recovery_query_is_bounded_exact_and_excludes_terminal_rows()
    {
        await using ReservationsDbContext dbContext = CreateInMemoryContext();
        ReservationStayAmendmentOperationRepository repository = new(dbContext);
        Guid sharedOperationId = Guid.Parse(
            "40000000-0000-0000-0000-000000000001");
        Guid firstReservationId = Guid.Parse(
            "50000000-0000-0000-0000-000000000001");
        Guid secondReservationId = Guid.Parse(
            "50000000-0000-0000-0000-000000000002");
        Guid thirdReservationId = Guid.Parse(
            "50000000-0000-0000-0000-000000000003");
        ReservationStayAmendmentOperation[] expected =
        [
            CreatePending(
                sharedOperationId,
                firstReservationId,
                RequestedAtUtc),
            CreatePending(
                sharedOperationId,
                secondReservationId,
                RequestedAtUtc),
            CreatePending(
                Guid.Parse("40000000-0000-0000-0000-000000000002"),
                thirdReservationId,
                RequestedAtUtc),
            CreateOutcomeUnknown(
                Guid.Parse("40000000-0000-0000-0000-000000000003"),
                Guid.Parse("50000000-0000-0000-0000-000000000004"),
                RequestedAtUtc.AddHours(-2)),
            CreateOutcomeUnknown(
                Guid.Parse("40000000-0000-0000-0000-000000000004"),
                Guid.Parse("50000000-0000-0000-0000-000000000005"),
                RequestedAtUtc.AddHours(-1))
        ];
        foreach (ReservationStayAmendmentOperation operation in expected)
        {
            await repository.AddAsync(operation, CancellationToken.None);
        }

        ReservationStayAmendmentOperation applied = CreateApplied(
            Guid.Parse("40000000-0000-0000-0000-000000000005"),
            Guid.Parse("50000000-0000-0000-0000-000000000006"),
            RequestedAtUtc);
        ReservationStayAmendmentOperation otherProperty = CreatePending(
            Guid.Parse("40000000-0000-0000-0000-000000000006"),
            Guid.Parse("50000000-0000-0000-0000-000000000007"),
            RequestedAtUtc,
            propertyId: Guid.Parse(
                "10000000-0000-0000-0000-000000000002"));
        await repository.AddAsync(applied, CancellationToken.None);
        await repository.AddAsync(otherProperty, CancellationToken.None);
        foreach (ReservationStayAmendmentOperation operation in expected
                     .Append(applied)
                     .Append(otherProperty))
        {
            AddOrdinaryReservation(
                dbContext,
                operation.PropertyId,
                operation.ReservationId,
                operation.RequestedAtUtc);
        }

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        ReservationStayAmendmentRecoveryPageRecord firstPage =
            await repository.ListRecoveryAsync(
                PropertyId,
                cursor: null,
                pageSize: 2,
                CancellationToken.None);
        ReservationStayAmendmentRecoveryPageRecord secondPage =
            await repository.ListRecoveryAsync(
                PropertyId,
                firstPage.NextCursor,
                pageSize: 2,
                CancellationToken.None);
        ReservationStayAmendmentRecoveryPageRecord thirdPage =
            await repository.ListRecoveryAsync(
                PropertyId,
                secondPage.NextCursor,
                pageSize: 2,
                CancellationToken.None);

        Assert.Equal(
            expected.Select(operation => (operation.Id, operation.ReservationId)),
            firstPage.Operations
                .Concat(secondPage.Operations)
                .Concat(thirdPage.Operations)
                .Select(operation => (operation.Id, operation.ReservationId)));
        Assert.NotNull(firstPage.NextCursor);
        Assert.Equal(secondReservationId, firstPage.NextCursor!.ReservationId);
        Assert.NotNull(secondPage.NextCursor);
        Assert.Equal(
            ReservationStayAmendmentOperationOutcome.OutcomeUnknown,
            secondPage.NextCursor!.Outcome);
        Assert.Null(thirdPage.NextCursor);
        Assert.Empty(dbContext.ChangeTracker.Entries<
            ReservationStayAmendmentOperation>());
    }

    [Fact]
    public async Task Recovery_query_does_not_disclose_restricted_or_anonymised_reservations()
    {
        await using ReservationsDbContext dbContext = CreateInMemoryContext();
        ReservationStayAmendmentOperationRepository repository = new(dbContext);
        ReservationStayAmendmentOperation visible = CreatePending(
            Guid.Parse("90000000-0000-0000-0000-000000000001"),
            Guid.Parse("91000000-0000-0000-0000-000000000001"),
            RequestedAtUtc);
        ReservationStayAmendmentOperation restricted = CreatePending(
            Guid.Parse("90000000-0000-0000-0000-000000000002"),
            Guid.Parse("91000000-0000-0000-0000-000000000002"),
            RequestedAtUtc);
        ReservationStayAmendmentOperation anonymised = CreatePending(
            Guid.Parse("90000000-0000-0000-0000-000000000003"),
            Guid.Parse("91000000-0000-0000-0000-000000000003"),
            RequestedAtUtc);
        await repository.AddAsync(visible, CancellationToken.None);
        await repository.AddAsync(restricted, CancellationToken.None);
        await repository.AddAsync(anonymised, CancellationToken.None);

        AddOrdinaryReservation(
            dbContext,
            PropertyId,
            visible.ReservationId,
            RequestedAtUtc);
        (_, ReservationProcessingRestrictionProjection restriction) =
            AddOrdinaryReservation(
                dbContext,
                PropertyId,
                restricted.ReservationId,
                RequestedAtUtc);
        Assert.True(restriction.Apply(
            expectedRevision: 0,
            ReservationProcessingRestrictionContract.CurrentVersion,
            RequestedAtUtc.AddMinutes(1)).IsSuccess);
        (Reservation anonymisedReservation, _) = AddOrdinaryReservation(
            dbContext,
            PropertyId,
            anonymised.ReservationId,
            RequestedAtUtc);
        dbContext.Entry(anonymisedReservation)
            .Property(reservation => reservation.IsAnonymised)
            .CurrentValue = true;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        ReservationStayAmendmentRecoveryPageRecord page =
            await repository.ListRecoveryAsync(
                PropertyId,
                cursor: null,
                pageSize: 10,
                CancellationToken.None);

        Assert.Equal(visible.Id, Assert.Single(page.Operations).Id);
        Assert.Null(page.NextCursor);
    }

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    public void Exact_status_lookup_correlates_ordinary_visibility_in_one_query(
        string provider)
    {
        using ReservationsDbContext dbContext = CreateRelationalContext(provider);
        Guid reservationId = Guid.Parse(
            "92000000-0000-0000-0000-000000000001");
        Guid operationId = Guid.Parse(
            "93000000-0000-0000-0000-000000000001");

        string sql = ReservationStayAmendmentOperationRepository
            .BuildVisibleQuery(
                dbContext,
                PropertyId,
                reservationId,
                operationId)
            .ToQueryString();

        Assert.Contains(
            "stay_amendment_operations",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reservations", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "reservation_processing_restriction_state",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXISTS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IsAnonymised", sql, StringComparison.Ordinal);
        Assert.Contains("IsRestricted", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    public void Recovery_keyset_and_lookahead_translate_for_relational_providers(
        string provider)
    {
        using ReservationsDbContext dbContext =
            CreateRelationalContext(provider);
        ReservationStayAmendmentRecoveryCursorRecord cursor = new(
            ReservationStayAmendmentOperationOutcome.Pending,
            RequestedAtUtc,
            Guid.Parse("60000000-0000-0000-0000-000000000001"),
            Guid.Parse("70000000-0000-0000-0000-000000000001"));

        string sql = ReservationStayAmendmentOperationRepository
            .BuildRecoveryQuery(
                dbContext,
                PropertyId,
                cursor,
                pageSize: 100)
            .ToQueryString();

        Assert.Contains(
            "stay_amendment_operations",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ScopeId", sql, StringComparison.Ordinal);
        Assert.Contains("PropertyId", sql, StringComparison.Ordinal);
        Assert.Contains("Outcome", sql, StringComparison.Ordinal);
        Assert.Contains("UpdatedAtUtc", sql, StringComparison.Ordinal);
        Assert.Contains("ReservationId", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("TOP", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("FETCH", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            nameof(Reservation.PrimaryGuestName),
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            nameof(Reservation.Email),
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            nameof(Reservation.Phone),
            sql,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Recovery_repository_rejects_unbounded_page_sizes(
        int pageSize)
    {
        await using ReservationsDbContext dbContext = CreateInMemoryContext();
        ReservationStayAmendmentOperationRepository repository = new(dbContext);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListRecoveryAsync(
                PropertyId,
                cursor: null,
                pageSize,
                CancellationToken.None));
    }

    [Fact]
    public async Task Recovery_repository_rejects_incomplete_cursor()
    {
        await using ReservationsDbContext dbContext = CreateInMemoryContext();
        ReservationStayAmendmentOperationRepository repository = new(dbContext);
        ReservationStayAmendmentRecoveryCursorRecord cursor = new(
            ReservationStayAmendmentOperationOutcome.Pending,
            default,
            Guid.Empty,
            Guid.Empty);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListRecoveryAsync(
                PropertyId,
                cursor,
                pageSize: 10,
                CancellationToken.None));
    }

    private static ReservationStayAmendmentOperation CreatePending(
        Guid operationId,
        Guid reservationId,
        DateTimeOffset requestedAtUtc,
        Guid? propertyId = null) =>
        ReservationStayAmendmentOperation.CreatePending(
            operationId,
            "tenant-a",
            propertyId ?? PropertyId,
            reservationId,
            Guid.NewGuid(),
            ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
            new string('a', ReservationStayAmendmentOperation.RequestFingerprintLength),
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 3),
            new TimeOnly(15, 0),
            new TimeOnly(10, 0),
            [Guid.Parse("80000000-0000-0000-0000-000000000001")],
            expectedDetailsRevision: 1,
            actorId: "staff:test",
            requestedAtUtc).Value;

    private static ReservationStayAmendmentOperation CreateApplied(
        Guid operationId,
        Guid reservationId,
        DateTimeOffset requestedAtUtc) =>
        ReservationStayAmendmentOperation.CreateAppliedNoOp(
            operationId,
            "tenant-a",
            PropertyId,
            reservationId,
            ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
            new string('b', ReservationStayAmendmentOperation.RequestFingerprintLength),
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 3),
            new TimeOnly(15, 0),
            new TimeOnly(10, 0),
            [Guid.Parse("80000000-0000-0000-0000-000000000001")],
            expectedDetailsRevision: 1,
            "staff:test",
            resultingDetailsRevision: 1,
            resultingReservationVersion: 2,
            resultingAllocationVersion: 1,
            requestedAtUtc).Value;

    private static ReservationStayAmendmentOperation CreateOutcomeUnknown(
        Guid operationId,
        Guid reservationId,
        DateTimeOffset requestedAtUtc) =>
        ReservationStayAmendmentOperation.CreateOutcomeUnknown(
            operationId,
            "tenant-a",
            PropertyId,
            reservationId,
            ReservationStayAmendmentOperation.LegacyRequestSchemaVersion,
            new string('c', ReservationStayAmendmentOperation.RequestFingerprintLength),
            expectedDetailsRevision: 1,
            requestedAtUtc).Value;

    private static (
        Reservation Reservation,
        ReservationProcessingRestrictionProjection Restriction)
        AddOrdinaryReservation(
            ReservationsDbContext dbContext,
            Guid propertyId,
            Guid reservationId,
            DateTimeOffset createdAtUtc)
    {
        Reservation reservation = Reservation.Create(
            reservationId,
            "tenant-a",
            propertyId,
            Guid.NewGuid(),
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 3),
            [Guid.NewGuid()],
            "Visible guest",
            email: null,
            phone: null,
            guestCount: 1,
            ReservationSource.Direct,
            sourceSystem: null,
            sourceReference: null,
            notes: null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            "staff:test",
            initialAdapterConnectionId: null,
            initialExternalOperationId: null,
            Guid.NewGuid(),
            createdAtUtc).Value;
        ReservationProcessingRestrictionProjection restriction =
            ReservationProcessingRestrictionProjection.Create(
                "tenant-a",
                propertyId,
                reservationId,
                ReservationProcessingRestrictionContract.CurrentVersion,
                createdAtUtc).Value;
        dbContext.Reservations.Add(reservation);
        dbContext.ProcessingRestrictionProjections.Add(restriction);
        return (reservation, restriction);
    }

    private static ReservationsDbContext CreateInMemoryContext(
        string? databaseName = null,
        InMemoryDatabaseRoot? databaseRoot = null)
    {
        DbContextOptionsBuilder<ReservationsDbContext> builder = new();
        builder.UseInMemoryDatabase(
            databaseName ?? $"stay-amendments-{Guid.NewGuid():N}",
            databaseRoot ?? new InMemoryDatabaseRoot());
        return new(builder.Options, new TestScopeContext());
    }

    private static ReservationsDbContext CreateRelationalContext(string provider)
    {
        DbContextOptionsBuilder<ReservationsDbContext> builder = new();
        switch (provider)
        {
            case "PostgreSql":
                builder.UseNpgsql(
                    "Host=localhost;Database=bunkfy_query_shape;" +
                    "Username=query_shape;Password=not-used");
                break;
            case "SqlServer":
                builder.UseSqlServer(
                    "Server=localhost;Database=BunkFyQueryShape;" +
                    "User Id=query-shape;Password=not-used;" +
                    "TrustServerCertificate=True");
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(provider),
                    provider,
                    "Unsupported query-shape test provider.");
        }

        return new(builder.Options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
