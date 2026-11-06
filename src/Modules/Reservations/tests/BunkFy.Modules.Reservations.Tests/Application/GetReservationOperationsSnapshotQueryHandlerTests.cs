namespace BunkFy.Modules.Reservations.Tests.Application;

using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GetReservationOperationsSnapshotQueryHandlerTests
{
    private static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 8, 13, 10, 15, 30, TimeSpan.Zero);

    [Fact]
    public async Task Success_captures_one_observation_and_forwards_the_property_date_and_limit()
    {
        Guid propertyId = Guid.NewGuid();
        DateOnly localDate = new(2026, 8, 14);
        ReservationOperationsSnapshotDto snapshot = Snapshot(propertyId, localDate);
        var reader = new RecordingReader(ReservationOperationsSnapshotReadResult.Found(snapshot));
        var handler = new GetReservationOperationsSnapshotQueryHandler(reader, new FixedClock());

        Result<ReservationOperationsSnapshotDto> result = await handler.HandleAsync(
            new GetReservationOperationsSnapshotQuery(propertyId, localDate, 0),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Same(snapshot, result.Value);
        ReadCall call = Assert.Single(reader.Calls);
        Assert.Equal(propertyId, call.PropertyId);
        Assert.Equal(localDate, call.ExplicitLocalDate);
        Assert.Equal(ObservedAtUtc, call.ObservedAtUtc);
        Assert.Equal(0, call.UpcomingLimit);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(51)]
    public async Task Invalid_limit_returns_the_stable_application_error_without_reading(int limit)
    {
        var reader = new RecordingReader(ReservationOperationsSnapshotReadResult.PropertyNotFound());
        var handler = new GetReservationOperationsSnapshotQueryHandler(reader, new FixedClock());

        Result<ReservationOperationsSnapshotDto> result = await handler.HandleAsync(
            new GetReservationOperationsSnapshotQuery(Guid.NewGuid(), null, limit),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ReservationsApplicationErrors.OperationsSnapshotLimitInvalid, result.Error);
        Assert.Empty(reader.Calls);
    }

    [Theory]
    [InlineData(ReservationOperationsSnapshotReadStatus.PropertyNotFound, "Reservations.PropertyNotFound")]
    [InlineData(ReservationOperationsSnapshotReadStatus.PropertyInactive, "Reservations.PropertyInactive")]
    [InlineData(
        ReservationOperationsSnapshotReadStatus.PropertyTimeZoneUnavailable,
        "Reservations.PropertyTimeZoneUnavailable")]
    public async Task Reader_failures_map_to_stable_application_errors(
        ReservationOperationsSnapshotReadStatus status,
        string expectedCode)
    {
        var reader = new RecordingReader(new ReservationOperationsSnapshotReadResult(status, null));
        var handler = new GetReservationOperationsSnapshotQueryHandler(reader, new FixedClock());

        Result<ReservationOperationsSnapshotDto> result = await handler.HandleAsync(
            new GetReservationOperationsSnapshotQuery(Guid.NewGuid(), null, 25),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
    }

    private static ReservationOperationsSnapshotDto Snapshot(Guid propertyId, DateOnly localDate)
    {
        var zero = new ReservationOperationsCountDto(0, 0);
        return new ReservationOperationsSnapshotDto(
            propertyId,
            localDate,
            "Europe/Riga",
            ReservationOperationsDateSource.Explicit,
            ObservedAtUtc,
            new ReservationOperationsCohortCountsDto(zero, zero, zero),
            new ReservationOperationsAttentionCountsDto(zero, zero, zero, zero, zero, zero, zero, zero),
            [],
            0,
            false);
    }

    private sealed class RecordingReader(ReservationOperationsSnapshotReadResult result)
        : IReservationOperationsSnapshotReader
    {
        public List<ReadCall> Calls { get; } = [];

        public Task<ReservationOperationsSnapshotReadResult> ReadAsync(
            Guid propertyId,
            DateOnly? explicitLocalDate,
            DateTimeOffset observedAtUtc,
            int upcomingLimit,
            CancellationToken cancellationToken)
        {
            this.Calls.Add(new ReadCall(propertyId, explicitLocalDate, observedAtUtc, upcomingLimit));
            return Task.FromResult(result);
        }
    }

    private sealed record ReadCall(
        Guid PropertyId,
        DateOnly? ExplicitLocalDate,
        DateTimeOffset ObservedAtUtc,
        int UpcomingLimit);

    private sealed class FixedClock : ISystemClock
    {
        public DateTimeOffset UtcNow => ObservedAtUtc;
    }
}
