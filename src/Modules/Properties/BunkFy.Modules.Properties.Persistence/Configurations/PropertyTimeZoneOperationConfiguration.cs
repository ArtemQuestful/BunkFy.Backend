namespace BunkFy.Modules.Properties.Persistence.Configurations;

using BunkFy.Modules.Properties.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class PropertyTimeZoneOperationConfiguration
    : IEntityTypeConfiguration<PropertyTimeZoneOperation>
{
    public void Configure(EntityTypeBuilder<PropertyTimeZoneOperation> builder)
    {
        builder.ToTable("property_time_zone_operations", table =>
        {
            table.HasCheckConstraint(
                "CK_properties_property_time_zone_operation_ids",
                "\"RevisionId\" <> '00000000-0000-0000-0000-000000000000' AND " +
                "\"RevisionId\" <> \"OperationId\" AND " +
                "\"PropertyId\" <> '00000000-0000-0000-0000-000000000000' AND " +
                "\"OperationId\" <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint(
                "CK_properties_property_time_zone_operation_change",
                "(\"ChangeKind\" = 1 AND \"OperationId\" = \"PropertyId\" AND " +
                "\"PreviousTimeZoneId\" IS NULL AND " +
                "\"ExpectedVersion\" = 0 AND \"ResultVersion\" = 1) OR " +
                "(\"ChangeKind\" = 2 AND \"PreviousTimeZoneId\" IS NOT NULL AND " +
                "\"PreviousTimeZoneId\" = \"TimeZoneId\" AND " +
                "\"ExpectedVersion\" > 0 AND \"ResultVersion\" = \"ExpectedVersion\") OR " +
                "(\"ChangeKind\" IN (3, 4) AND \"PreviousTimeZoneId\" IS NOT NULL AND " +
                "\"PreviousTimeZoneId\" <> \"TimeZoneId\" AND " +
                "\"ExpectedVersion\" > 0 AND \"ResultVersion\" = \"ExpectedVersion\" + 1)");
            table.HasCheckConstraint(
                "CK_properties_property_time_zone_operation_text",
                "char_length(\"ScopeId\") > 0 AND btrim(\"ScopeId\") = \"ScopeId\" AND " +
                "char_length(\"RequestedTimeZoneId\") > 0 AND btrim(\"RequestedTimeZoneId\") = \"RequestedTimeZoneId\" AND " +
                "(\"PreviousTimeZoneId\" IS NULL OR (char_length(\"PreviousTimeZoneId\") > 0 AND btrim(\"PreviousTimeZoneId\") = \"PreviousTimeZoneId\")) AND " +
                "char_length(\"TimeZoneId\") > 0 AND btrim(\"TimeZoneId\") = \"TimeZoneId\" AND " +
                "char_length(\"CatalogVersion\") > 0 AND btrim(\"CatalogVersion\") = \"CatalogVersion\" AND " +
                "char_length(\"ActorId\") > 0 AND btrim(\"ActorId\") = \"ActorId\" AND " +
                "\"ScopeId\" !~ '[[:cntrl:]]' AND " +
                "\"RequestedTimeZoneId\" !~ '[[:cntrl:]]' AND " +
                "(\"PreviousTimeZoneId\" IS NULL OR \"PreviousTimeZoneId\" !~ '[[:cntrl:]]') AND " +
                "\"TimeZoneId\" !~ '[[:cntrl:]]' AND " +
                "\"CatalogVersion\" !~ '[[:cntrl:]]' AND " +
                "\"ActorId\" !~ '[[:cntrl:]]'");
        });

        builder.HasKey(operation => new
        {
            operation.ScopeId,
            operation.PropertyId,
            operation.OperationId
        });
        builder.Property(operation => operation.RevisionId)
            .ValueGeneratedNever()
            .IsRequired();
        builder.Property(operation => operation.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(operation => operation.PropertyId)
            .ValueGeneratedNever()
            .IsRequired();
        builder.Property(operation => operation.OperationId)
            .ValueGeneratedNever()
            .IsRequired();
        builder.Property(operation => operation.ChangeKind)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(operation => operation.RequestedTimeZoneId)
            .HasMaxLength(Property.TimeZoneIdMaxLength)
            .IsRequired();
        builder.Property(operation => operation.PreviousTimeZoneId)
            .HasMaxLength(Property.TimeZoneIdMaxLength);
        builder.Property(operation => operation.TimeZoneId)
            .HasMaxLength(Property.TimeZoneIdMaxLength)
            .IsRequired();
        builder.Property(operation => operation.CatalogVersion)
            .HasMaxLength(PropertyTimeZoneCatalogSeed.CatalogVersionMaxLength)
            .IsRequired();
        builder.Property(operation => operation.ActorId)
            .HasMaxLength(Property.ActorIdMaxLength)
            .IsRequired();
        builder.HasOne<Property>()
            .WithMany()
            .HasForeignKey(operation => new
            {
                operation.ScopeId,
                operation.PropertyId
            })
            .HasPrincipalKey(property => new
            {
                property.ScopeId,
                property.Id
            })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PropertyOperationLock>()
            .WithMany()
            .HasForeignKey(operation => new
            {
                operation.ScopeId,
                operation.PropertyId
            })
            .HasPrincipalKey(resourceLock => new
            {
                resourceLock.ScopeId,
                resourceLock.Id
            })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PropertyTimeZoneCatalogEntry>()
            .WithMany()
            .HasForeignKey(operation => new
            {
                operation.CatalogVersion,
                operation.TimeZoneId
            })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PropertyTimeZoneCatalogResolution>()
            .WithMany()
            .HasForeignKey(operation => new
            {
                operation.CatalogVersion,
                operation.RequestedTimeZoneId,
                operation.TimeZoneId
            })
            .HasPrincipalKey(resolution => new
            {
                resolution.CatalogVersion,
                resolution.RequestedTimeZoneId,
                resolution.CanonicalTimeZoneId
            })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(operation => new
        {
            operation.CatalogVersion,
            operation.TimeZoneId
        }).HasDatabaseName(
            "IX_property_time_zone_operations_catalog_time_zone");
        builder.HasIndex(operation => new
        {
            operation.ScopeId,
            operation.RevisionId
        })
            .HasDatabaseName(
                "IX_property_time_zone_operations_scope_revision")
            .IsUnique();
        builder.HasIndex(operation => new
        {
            operation.ScopeId,
            operation.OccurredAtUtc,
            operation.PropertyId,
            operation.OperationId
        }).HasDatabaseName(
            "IX_property_time_zone_operations_scope_occurred");
    }
}
