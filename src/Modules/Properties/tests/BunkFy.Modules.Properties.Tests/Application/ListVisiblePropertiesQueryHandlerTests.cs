namespace BunkFy.Modules.Properties.Tests;

using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.TimeZones;
using Gma.Framework.AccessControl;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Permissions;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ListVisiblePropertiesQueryHandlerTests
{
    private static readonly Guid PropertyId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Tenant_grant_lists_all_properties()
    {
        FakeGrantScopeReader grants = new(
            new AccessGrantScope(AccessScope.Parse("tenant:tenant-a"), new AccessScopeMatchOptions()));
        FakePropertiesReadRepository repository = new();
        IQueryHandler<ListVisiblePropertiesQuery, PropertyListResponse> handler = CreateHandler(repository, grants);

        Result<PropertyListResponse> result = await handler.HandleAsync(
            new ListVisiblePropertiesQuery(AccessSubject.User("user-a")),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(repository.Visibility!.IncludesAllProperties);
        Assert.Equal(1, grants.CallCount);
    }

    [Fact]
    public async Task Property_grant_is_translated_to_a_restricted_repository_scope()
    {
        FakeGrantScopeReader grants = new(
            new AccessGrantScope(
                AccessScope.Parse($"tenant:tenant-a/property:{PropertyId:D}"),
                new AccessScopeMatchOptions(AllowAncestorScopeGrants: true)));
        FakePropertiesReadRepository repository = new();
        IQueryHandler<ListVisiblePropertiesQuery, PropertyListResponse> handler = CreateHandler(repository, grants);

        Result<PropertyListResponse> result = await handler.HandleAsync(
            new ListVisiblePropertiesQuery(AccessSubject.User("user-a")),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(repository.Visibility!.IncludesAllProperties);
        Assert.Equal([PropertyId], repository.Visibility.PropertyIds);
        Assert.Equal(1, grants.CallCount);
    }

    [Fact]
    public async Task Unrelated_scope_is_denied_without_querying_properties()
    {
        FakeGrantScopeReader grants = new(
            new AccessGrantScope(
                AccessScope.Parse($"tenant:tenant-b/property:{PropertyId:D}"),
                new AccessScopeMatchOptions(AllowAncestorScopeGrants: true)));
        FakePropertiesReadRepository repository = new();
        IQueryHandler<ListVisiblePropertiesQuery, PropertyListResponse> handler = CreateHandler(repository, grants);

        Result<PropertyListResponse> result = await handler.HandleAsync(
            new ListVisiblePropertiesQuery(AccessSubject.User("user-a")),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(PropertiesApplicationErrors.AccessDenied, result.Error);
        Assert.Null(repository.Visibility);
        Assert.Equal(1, grants.CallCount);
    }

    [Fact]
    public async Task Descendant_grant_does_not_widen_visibility_to_the_property()
    {
        FakeGrantScopeReader grants = new(
            new AccessGrantScope(
                AccessScope.Parse($"tenant:tenant-a/property:{PropertyId:D}/room:20000000-0000-0000-0000-000000000001"),
                new AccessScopeMatchOptions(AllowAncestorScopeGrants: true)));
        FakePropertiesReadRepository repository = new();
        IQueryHandler<ListVisiblePropertiesQuery, PropertyListResponse> handler = CreateHandler(repository, grants);

        Result<PropertyListResponse> result = await handler.HandleAsync(
            new ListVisiblePropertiesQuery(AccessSubject.User("user-a")),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(PropertiesApplicationErrors.AccessDenied, result.Error);
        Assert.Null(repository.Visibility);
        Assert.Equal(1, grants.CallCount);
    }

    [Fact]
    public async Task Visible_list_returns_typed_time_source_failure()
    {
        FakeGrantScopeReader grants = new(
            new AccessGrantScope(
                AccessScope.Parse("tenant:tenant-a"),
                new AccessScopeMatchOptions()));
        FakePropertiesReadRepository repository = new();
        IQueryHandler<ListVisiblePropertiesQuery, PropertyListResponse> handler =
            CreateHandler(repository, grants, new FixedClock(default));

        Result<PropertyListResponse> result = await handler.HandleAsync(
            new ListVisiblePropertiesQuery(AccessSubject.User("user-a")),
            CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.TimeSourceUnavailable,
            result.Error);
        Assert.NotNull(repository.Visibility);
    }

    private static IQueryHandler<ListVisiblePropertiesQuery, PropertyListResponse> CreateHandler(
        FakePropertiesReadRepository repository,
        FakeGrantScopeReader grants,
        ISystemClock? clock = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IPropertiesReadRepository>(repository);
        services.AddSingleton<IAccessGrantScopeReader>(grants);
        services.AddSingleton<IScopeContext>(new TestScopeContext());
        services.AddSingleton<ISystemClock>(clock ?? new TestClock());
        services.AddPropertiesApplication();

        ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IQueryHandler<ListVisiblePropertiesQuery, PropertyListResponse>>();
    }

    private sealed class FakeGrantScopeReader(params AccessGrantScope[] grants) : IAccessGrantScopeReader
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<AccessGrantScope>> ListGrantedScopesAsync(
            AccessSubject subject,
            PermissionCode permission,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            Assert.Equal(PropertiesAdminPermissionCodes.Read, permission.Value);
            return Task.FromResult<IReadOnlyList<AccessGrantScope>>(grants);
        }
    }

    private sealed class FakePropertiesReadRepository : IPropertiesReadRepository
    {
        public PropertiesVisibilityScope? Visibility { get; private set; }

        public Task<Property?> GetPropertyAsync(Guid propertyId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PropertyReadPage> ListPropertiesAsync(PageRequest pageRequest, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PropertyReadPage> ListVisiblePropertiesAsync(
            PageRequest pageRequest,
            PropertiesVisibilityScope visibility,
            CancellationToken cancellationToken)
        {
            this.Visibility = visibility;
            return Task.FromResult(new PropertyReadPage([], pageRequest.Page, pageRequest.PageSize, false));
        }

        public Task<RoomDto?> GetRoomAsync(Guid propertyId, Guid roomId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RoomListResponse> ListRoomsAsync(
            Guid propertyId,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BedListResponse> ListBedsAsync(
            Guid propertyId,
            Guid roomId,
            PageRequest pageRequest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } =
            new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }
}
