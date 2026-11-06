namespace BunkFy.Modules.Properties.Tests;

using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Handlers;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.Application.Events;
using Gma.Framework.Cqrs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertiesApplicationRegistrationTests
{
    [Fact]
    public void Properties_application_registration_is_idempotent()
    {
        ServiceCollection services = new();

        services.AddPropertiesApplication();
        services.AddPropertiesApplication();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ICommandHandler<CreatePropertyCommand, PropertyMutationReceiptDto>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ICommandHandler<UpdatePropertyCommand, PropertyMutationReceiptDto>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ICommandHandler<RetirePropertyCommand, PropertyMutationReceiptDto>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ICommandHandler<CreateRoomCommand, RoomMutationReceiptDto>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ICommandHandler<UpdateRoomCommand, RoomMutationReceiptDto>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ICommandHandler<RetireRoomCommand, Unit>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ICommandHandler<AddBedCommand, BedMutationReceiptDto>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ICommandHandler<AddBedsCommand, BedBatchMutationReceiptDto>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ICommandHandler<UpdateBedCommand, BedMutationReceiptDto>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ICommandHandler<RetireBedCommand, Unit>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PropertyDetailsUpdateCoordinator));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PropertyMutationOperationJournal));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(TimeZoneRuntimeCompatibilityProbe));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IQueryHandler<GetPropertyQuery, PropertyDto>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IQueryHandler<ListPropertiesQuery, PropertyListResponse>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IQueryHandler<ListVisiblePropertiesQuery, PropertyListResponse>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IQueryHandler<GetRoomQuery, RoomDto>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IQueryHandler<ListRoomsQuery, RoomListResponse>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IQueryHandler<ListBedsQuery, BedListResponse>));
        Assert.Equal(11, services.Count(descriptor => IsDomainEventHandler(descriptor.ServiceType)));
    }

    [Fact]
    public void Properties_application_registration_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => DependencyInjection.AddPropertiesApplication(null!));
    }

    private static bool IsDomainEventHandler(Type serviceType) =>
        serviceType.IsGenericType &&
        serviceType.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>);
}
