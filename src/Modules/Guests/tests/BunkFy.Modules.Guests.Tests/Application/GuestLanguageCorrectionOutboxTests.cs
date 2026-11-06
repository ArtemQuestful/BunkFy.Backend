namespace BunkFy.Modules.Guests.Tests.Application;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application.Handlers;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Events;
using BunkFy.Modules.Guests.Domain.Models;
using Gma.Framework.Messaging;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestLanguageCorrectionOutboxTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Completion_projection_keeps_the_owning_receipt_version_and_no_language_values(int version)
    {
        GuestProfileField field = version == 1 ? GuestProfileField.PreferredLanguageTag : GuestProfileField.LanguageTags;
        GuestDataRightsCorrectionAppliedDomainEvent domainEvent = new(Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero), "tenant-a", Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), 1, 2, [field], version);
        Outbox outbox = new();
        await new GuestDataRightsCorrectionAppliedOutboxProjector(outbox)
            .HandleAsync(domainEvent, CancellationToken.None);
        DataRightsCorrectionAppliedIntegrationEvent projected =
            Assert.IsType<DataRightsCorrectionAppliedIntegrationEvent>(Assert.Single(outbox.Events));
        Assert.Equal(version, projected.ReceiptContractVersion);
        Assert.Equal(domainEvent.ReceiptId, projected.ReceiptId);
        Assert.Equal(domainEvent.EventId, projected.EventId);
        Assert.DoesNotContain(projected.GetType().GetProperties(), property => property.Name == "LanguageTags");
    }

    private sealed class Outbox : IOutboxWriter, IOutboxWriterRegistry
    {
        public string ModuleName => GuestsModuleMetadata.Name;
        public List<IIntegrationEvent> Events { get; } = [];
        public IOutboxWriter GetRequired(string moduleName) => this;
        public Task EnqueueAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent
        {
            this.Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }
}
