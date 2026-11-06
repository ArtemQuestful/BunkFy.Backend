namespace BunkFy.Modules.Retention.Persistence.Configurations;

using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Naming;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class OutboxMessageConfiguration
    : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder) =>
        builder.ConfigureOutboxMessage();
}
