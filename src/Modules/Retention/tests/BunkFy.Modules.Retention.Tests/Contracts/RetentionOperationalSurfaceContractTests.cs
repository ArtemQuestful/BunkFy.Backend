namespace BunkFy.Modules.Retention.Tests.Contracts;

using BunkFy.Modules.Retention.Admin.Contracts;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Messaging;
using Xunit;

[Trait("Category", "Unit")]
public sealed class RetentionOperationalSurfaceContractTests
{
    [Fact]
    public void Health_and_retry_contracts_expose_bounded_operational_coordinates()
    {
        Assert.Equal(
            typeof(bool),
            typeof(RetentionScheduleHealthListResponse)
                .GetProperty(nameof(RetentionScheduleHealthListResponse.HasMore))!
                .PropertyType);
        Assert.Equal(
            typeof(RetentionScheduleHealthSummaryDto),
            typeof(RetentionScheduleHealthListResponse)
                .GetProperty(nameof(RetentionScheduleHealthListResponse.Summary))!
                .PropertyType);
        Assert.Equal(
            typeof(Guid?),
            typeof(RetentionScheduleHealthDto)
                .GetProperty(nameof(RetentionScheduleHealthDto.LastRunId))!
                .PropertyType);
        Assert.Equal(
            typeof(Guid),
            typeof(RetentionRunRetryReceiptDto)
                .GetProperty(nameof(RetentionRunRetryReceiptDto.RunId))!
                .PropertyType);
    }

    [Fact]
    public void Recovery_event_is_published_and_self_subscribed_without_owner_payload()
    {
        Assert.Contains(
            RetentionModuleMetadata.Descriptor.GetPublishedEvents(),
            published =>
                published.EventType ==
                    RetentionRunRetryRequestedIntegrationEvent.EventType &&
                published.Version ==
                    RetentionRunRetryRequestedIntegrationEvent.EventVersion);
        Assert.Contains(
            RetentionModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.ProducerModule ==
                    RetentionModuleMetadata.Name &&
                subscription.EventType ==
                    RetentionRunRetryRequestedIntegrationEvent.EventType &&
                subscription.HandlerName ==
                    RetentionModuleMetadata.RunRetryRequestedHandlerName);

        string[] payloadMembers = typeof(
                RetentionRunRetryRequestedIntegrationEvent)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.Contains(nameof(
            RetentionRunRetryRequestedIntegrationEvent.RequestId),
            payloadMembers);
        Assert.DoesNotContain("PropertyId", payloadMembers);
        Assert.DoesNotContain("OwnerKey", payloadMembers);
        Assert.DoesNotContain("DataClassKey", payloadMembers);
    }
}
