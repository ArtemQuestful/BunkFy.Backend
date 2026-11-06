namespace BunkFy.Modules.Properties.Tests;

using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertiesListQueryHandlerTests
{
    [Fact]
    public async Task List_rooms_returns_not_found_when_parent_property_is_missing()
    {
        FakePropertiesReadRepository repository = new();
        IQueryHandler<ListRoomsQuery, RoomListResponse> handler = CreateHandler<ListRoomsQuery, RoomListResponse>(repository);

        Result<RoomListResponse> result = await handler.HandleAsync(new ListRoomsQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(PropertiesApplicationErrors.PropertyNotFound, result.Error);
        Assert.False(repository.ListRoomsCalled);
    }

    [Fact]
    public async Task List_beds_returns_not_found_when_parent_room_is_missing()
    {
        FakePropertiesReadRepository repository = new();
        IQueryHandler<ListBedsQuery, BedListResponse> handler = CreateHandler<ListBedsQuery, BedListResponse>(repository);

        Result<BedListResponse> result = await handler.HandleAsync(
            new ListBedsQuery(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(PropertiesApplicationErrors.RoomNotFound, result.Error);
        Assert.False(repository.ListBedsCalled);
    }

    private static IQueryHandler<TQuery, TResponse> CreateHandler<TQuery, TResponse>(FakePropertiesReadRepository repository)
        where TQuery : IQuery<TResponse>
    {
        ServiceCollection services = new();
        services.AddSingleton<IPropertiesReadRepository>(repository);
        services.AddPropertiesApplication();

        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IQueryHandler<TQuery, TResponse>>();
    }

    private sealed class FakePropertiesReadRepository : IPropertiesReadRepository
    {
        public bool ListRoomsCalled { get; private set; }
        public bool ListBedsCalled { get; private set; }

        public Task<Property?> GetPropertyAsync(Guid propertyId, CancellationToken cancellationToken) =>
            Task.FromResult<Property?>(null);

        public Task<PropertyReadPage> ListPropertiesAsync(PageRequest pageRequest, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PropertyReadPage> ListVisiblePropertiesAsync(
            PageRequest pageRequest,
            PropertiesVisibilityScope visibility,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RoomDto?> GetRoomAsync(Guid propertyId, Guid roomId, CancellationToken cancellationToken) =>
            Task.FromResult<RoomDto?>(null);

        public Task<RoomListResponse> ListRoomsAsync(
            Guid propertyId,
            PageRequest pageRequest,
            CancellationToken cancellationToken)
        {
            this.ListRoomsCalled = true;
            return Task.FromResult(new RoomListResponse([], pageRequest.Page, pageRequest.PageSize, false));
        }

        public Task<BedListResponse> ListBedsAsync(
            Guid propertyId,
            Guid roomId,
            PageRequest pageRequest,
            CancellationToken cancellationToken)
        {
            this.ListBedsCalled = true;
            return Task.FromResult(new BedListResponse([], pageRequest.Page, pageRequest.PageSize, false));
        }
    }
}
