namespace BunkFy.Modules.Properties.Persistence.Configurations;

using BunkFy.Modules.Properties.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class PropertyTimeZoneCatalogResolutionConfiguration
    : IEntityTypeConfiguration<PropertyTimeZoneCatalogResolution>
{
    public void Configure(
        EntityTypeBuilder<PropertyTimeZoneCatalogResolution> builder)
    {
        builder.ToTable("property_time_zone_catalog_resolutions", table =>
        {
            table.HasCheckConstraint(
                "CK_properties_property_time_zone_catalog_resolution_ordinal",
                "\"Ordinal\" > 0");
            table.HasCheckConstraint(
                "CK_properties_property_time_zone_catalog_resolution_text",
                "char_length(\"CatalogVersion\") > 0 AND " +
                "btrim(\"CatalogVersion\") = \"CatalogVersion\" AND " +
                "char_length(\"RequestedTimeZoneId\") > 0 AND " +
                "btrim(\"RequestedTimeZoneId\") = \"RequestedTimeZoneId\" AND " +
                "char_length(\"CanonicalTimeZoneId\") > 0 AND " +
                "btrim(\"CanonicalTimeZoneId\") = \"CanonicalTimeZoneId\" AND " +
                "\"CatalogVersion\" !~ '[[:cntrl:]]' AND " +
                "\"RequestedTimeZoneId\" !~ '[[:cntrl:]]' AND " +
                "\"CanonicalTimeZoneId\" !~ '[[:cntrl:]]'");
        });
        builder.HasKey(resolution => new
        {
            resolution.CatalogVersion,
            resolution.RequestedTimeZoneId
        });
        builder.HasAlternateKey(resolution => new
        {
            resolution.CatalogVersion,
            resolution.RequestedTimeZoneId,
            resolution.CanonicalTimeZoneId
        });
        builder.Property(resolution => resolution.CatalogVersion)
            .HasMaxLength(PropertyTimeZoneCatalogSeed.CatalogVersionMaxLength)
            .IsRequired();
        builder.Property(resolution => resolution.RequestedTimeZoneId)
            .HasMaxLength(Property.TimeZoneIdMaxLength)
            .IsRequired();
        builder.Property(resolution => resolution.CanonicalTimeZoneId)
            .HasMaxLength(Property.TimeZoneIdMaxLength)
            .IsRequired();
        builder.Property(resolution => resolution.Ordinal)
            .ValueGeneratedNever()
            .IsRequired();
        builder.HasOne<PropertyTimeZoneCatalogEntry>()
            .WithMany()
            .HasForeignKey(resolution => new
            {
                resolution.CatalogVersion,
                resolution.CanonicalTimeZoneId
            })
            .HasPrincipalKey(entry => new
            {
                entry.CatalogVersion,
                entry.TimeZoneId
            })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(resolution => new
        {
            resolution.CatalogVersion,
            resolution.Ordinal
        })
            .HasDatabaseName(
                "IX_property_time_zone_catalog_resolutions_version_ordinal")
            .IsUnique();
        builder.HasData(
            PropertyTimeZoneCatalogSeed.AllResolutions.Select(resolution =>
                new
                {
                    resolution.CatalogVersion,
                    resolution.RequestedTimeZoneId,
                    resolution.CanonicalTimeZoneId,
                    resolution.Ordinal
                }));
    }
}
