namespace BunkFy.Modules.Properties.Persistence.Configurations;

using BunkFy.Modules.Properties.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class PropertyTimeZoneCatalogEntryConfiguration
    : IEntityTypeConfiguration<PropertyTimeZoneCatalogEntry>
{
    public void Configure(
        EntityTypeBuilder<PropertyTimeZoneCatalogEntry> builder)
    {
        builder.ToTable("property_time_zone_catalog_entries", table =>
        {
            table.HasCheckConstraint(
                "CK_properties_property_time_zone_catalog_entry_ordinal",
                "\"Ordinal\" > 0");
            table.HasCheckConstraint(
                "CK_properties_property_time_zone_catalog_entry_text",
                "char_length(\"CatalogVersion\") > 0 AND " +
                "btrim(\"CatalogVersion\") = \"CatalogVersion\" AND " +
                "char_length(\"TimeZoneId\") > 0 AND " +
                "btrim(\"TimeZoneId\") = \"TimeZoneId\" AND " +
                "\"CatalogVersion\" !~ '[[:cntrl:]]' AND " +
                "\"TimeZoneId\" !~ '[[:cntrl:]]'");
        });
        builder.HasKey(entry => new
        {
            entry.CatalogVersion,
            entry.TimeZoneId
        });
        builder.Property(entry => entry.CatalogVersion)
            .HasMaxLength(PropertyTimeZoneCatalogSeed.CatalogVersionMaxLength)
            .IsRequired();
        builder.Property(entry => entry.TimeZoneId)
            .HasMaxLength(Property.TimeZoneIdMaxLength)
            .IsRequired();
        builder.Property(entry => entry.Ordinal)
            .ValueGeneratedNever()
            .IsRequired();
        builder.HasIndex(entry => new
        {
            entry.CatalogVersion,
            entry.Ordinal
        })
            .HasDatabaseName(
                "IX_property_time_zone_catalog_entries_version_ordinal")
            .IsUnique();
        builder.HasData(
            PropertyTimeZoneCatalogSeed.AllEntries.Select(entry => new
            {
                entry.CatalogVersion,
                entry.TimeZoneId,
                entry.Ordinal
            }));
    }
}
