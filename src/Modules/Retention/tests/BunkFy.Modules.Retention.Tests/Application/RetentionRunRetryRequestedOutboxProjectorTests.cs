namespace BunkFy.Modules.Retention.Tests.Application;

using BunkFy.Modules.Retention.Application.Handlers;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Events;
using Gma.Framework.Messaging;
using Xunit;

[Trait("Category", "Unit")]
public sealed class RetentionRunRetryRequestedOutboxProjectorTests
{
    [Fact]
    public async Task Domain_event_projects_the_minimized_tenant_event()
    {
        Guid eventId = Guid.NewGuid();
        Guid requestId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        DateTimeOffset occurredAtUtc =
            new(2026, 8, 15, 15, 0, 0, TimeSpan.Zero);
        RecordingWriter writer = new();
        RetentionRunRetryRequestedOutboxProjector projector = new(
            new RecordingRegistry(writer));

        await projector.HandleAsync(
            new RetentionRunRetryRequestedDomainEvent(
                eventId,
                occurredAtUtc,
                "tenant-a",
                requestId,
                runId,
                attempt: 2),
            CancellationToken.None);

        RetentionRunRetryRequestedIntegrationEvent projected =
            Assert.IsType<RetentionRunRetryRequestedIntegrationEvent>(
                writer.Event);
        Assert.Equal(eventId, projected.EventId);
        Assert.Equal("tenant-a", projected.TenantId);
        Assert.Equal(requestId, projected.RequestId);
        Assert.Equal(runId, projected.RunId);
        Assert.Equal(2, projected.Attempt);
        Assert.Equal(occurredAtUtc, projected.OccurredAtUtc);
    }

    private sealed class RecordingRegistry(IOutboxWriter writer)
        : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName)
        {
            Assert.Equal(RetentionModuleMetadata.Name, moduleName);
            return writer;
        }
    }

    private sealed class RecordingWriter : IOutboxWriter
    {
        public string ModuleName => RetentionModuleMetadata.Name;
        public IIntegrationEvent? Event { get; private set; }

        public Task EnqueueAsync<TEvent>(
            TEvent integrationEvent,
            CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Event = integrationEvent;
            return Task.CompletedTask;
        }
    }
}
