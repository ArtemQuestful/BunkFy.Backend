namespace BunkFy.Modules.Staff.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using BunkFy.Modules.Staff.Domain.Aggregates;

internal sealed class StaffMemberConfiguration : IEntityTypeConfiguration<StaffMember>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<StaffMember> builder)
    {
        builder.ToTable("staff_members", table =>
        {
            table.HasCheckConstraint(
                "CK_staff_members_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND length(trim(\"ScopeId\")) > 0");
            table.HasCheckConstraint("CK_staff_members_version", "\"Version\" >= 1");
            table.HasCheckConstraint("CK_staff_members_display_name", "length(trim(\"DisplayName\")) > 0");
            table.HasCheckConstraint("CK_staff_members_created_by", "length(trim(\"CreatedBy\")) > 0");
            table.HasCheckConstraint("CK_staff_members_last_changed_by", "length(trim(\"LastChangedBy\")) > 0");
            table.HasCheckConstraint(
                "CK_staff_members_search_shape",
                "length(trim(\"DisplayNameSearch\")) > 0 AND " +
                PairedOptionalText("LegalName", "LegalNameSearch") + " AND " +
                PairedOptionalText("WorkEmail", "WorkEmailSearch") + " AND " +
                PairedOptionalText("WorkPhone", "WorkPhoneSearch") + " AND " +
                PairedOptionalText("EmployeeNumber", "EmployeeNumberSearch"));
            table.HasCheckConstraint(
                "CK_staff_members_optional_text",
                OptionalText("JobTitle") + " AND " +
                OptionalText("Department") + " AND " +
                OptionalText("AuthSubjectId"));
            table.HasCheckConstraint("CK_staff_members_lifecycle",
                "(\"Status\" = 1 AND \"SuspendedAtUtc\" IS NULL AND \"DepartedAtUtc\" IS NULL AND \"DepartureEffectiveOn\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR " +
                "(\"Status\" = 2 AND \"SuspendedAtUtc\" IS NOT NULL AND \"DepartedAtUtc\" IS NULL AND \"DepartureEffectiveOn\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR " +
                "(\"Status\" = 3 AND \"SuspendedAtUtc\" IS NULL AND \"DepartedAtUtc\" IS NOT NULL AND \"DepartureEffectiveOn\" IS NOT NULL AND \"AnonymisedAtUtc\" IS NULL) OR " +
                "(\"Status\" = 4 AND \"SuspendedAtUtc\" IS NULL AND \"DepartedAtUtc\" IS NOT NULL AND \"DepartureEffectiveOn\" IS NOT NULL AND \"AnonymisedAtUtc\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_staff_members_timestamps",
                "\"LastChangedAtUtc\" >= \"CreatedAtUtc\" AND " +
                "(\"SuspendedAtUtc\" IS NULL OR (\"SuspendedAtUtc\" >= \"CreatedAtUtc\" AND \"SuspendedAtUtc\" <= \"LastChangedAtUtc\")) AND " +
                "(\"DepartedAtUtc\" IS NULL OR (\"DepartedAtUtc\" >= \"CreatedAtUtc\" AND \"DepartedAtUtc\" <= \"LastChangedAtUtc\")) AND " +
                "(\"AnonymisedAtUtc\" IS NULL OR (\"DepartedAtUtc\" IS NOT NULL AND \"AnonymisedAtUtc\" >= \"DepartedAtUtc\" AND \"AnonymisedAtUtc\" <= \"LastChangedAtUtc\"))");
            table.HasCheckConstraint(
                "CK_staff_members_anonymised_profile",
                $"\"Status\" <> 4 OR (\"DisplayName\" = '{StaffMember.AnonymisedDisplayName}' AND " +
                $"\"DisplayNameSearch\" = '{StaffMember.AnonymisedDisplayName.ToUpperInvariant()}' AND " +
                "\"LegalName\" IS NULL AND \"LegalNameSearch\" IS NULL AND " +
                "\"WorkEmail\" IS NULL AND \"WorkEmailSearch\" IS NULL AND " +
                "\"WorkPhone\" IS NULL AND \"WorkPhoneSearch\" IS NULL AND " +
                "\"EmployeeNumber\" IS NULL AND \"EmployeeNumberSearch\" IS NULL AND " +
                "\"JobTitle\" IS NULL AND \"Department\" IS NULL AND " +
                "\"AuthSubjectId\" IS NULL)");
        });
        builder.HasKey(member => member.Id);
        builder.HasAlternateKey(member => new { member.ScopeId, member.Id });
        builder.Property(member => member.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(member => member.DisplayName).HasMaxLength(StaffMember.DisplayNameMaxLength).IsRequired();
        builder.Property(member => member.DisplayNameSearch).HasMaxLength(StaffMember.DisplayNameMaxLength).IsRequired();
        builder.Property(member => member.LegalName).HasMaxLength(StaffMember.LegalNameMaxLength);
        builder.Property(member => member.LegalNameSearch).HasMaxLength(StaffMember.LegalNameMaxLength);
        builder.Property(member => member.WorkEmail).HasMaxLength(StaffMember.EmailMaxLength);
        builder.Property(member => member.WorkEmailSearch).HasMaxLength(StaffMember.EmailMaxLength);
        builder.Property(member => member.WorkPhone).HasMaxLength(StaffMember.PhoneMaxLength);
        builder.Property(member => member.WorkPhoneSearch).HasMaxLength(StaffMember.PhoneMaxLength);
        builder.Property(member => member.EmployeeNumber).HasMaxLength(StaffMember.EmployeeNumberMaxLength);
        builder.Property(member => member.EmployeeNumberSearch).HasMaxLength(StaffMember.EmployeeNumberMaxLength);
        builder.Property(member => member.JobTitle).HasMaxLength(StaffMember.JobTitleMaxLength);
        builder.Property(member => member.Department).HasMaxLength(StaffMember.DepartmentMaxLength);
        builder.Property(member => member.AuthSubjectId).HasMaxLength(StaffMember.AuthSubjectIdMaxLength);
        builder.Property(member => member.Status).HasConversion<int>().IsRequired();
        builder.Property(member => member.Version).IsConcurrencyToken().IsRequired();
        builder.Property(member => member.ProjectionOrdinal).ValueGeneratedOnAdd().IsRequired();
        builder.HasIndex(member => member.ProjectionOrdinal).IsUnique();
        builder.Property(member => member.CreatedBy).HasMaxLength(StaffMember.ActorIdMaxLength).IsRequired();
        builder.Property(member => member.LastChangedBy).HasMaxLength(StaffMember.ActorIdMaxLength).IsRequired();
        builder.HasIndex(member => new { member.ScopeId, member.EmployeeNumberSearch }).IsUnique();
        builder.HasIndex(member => new { member.ScopeId, member.AuthSubjectId }).IsUnique();
        builder.HasIndex(member => new { member.ScopeId, member.Status, member.DisplayNameSearch, member.Id });
        builder.HasIndex(member => new
        {
            member.ScopeId,
            member.Status,
            member.ProjectionOrdinal,
            member.Id
        });
        builder.HasMany(member => member.Assignments).WithOne().HasForeignKey(assignment =>
            new { assignment.ScopeId, assignment.StaffMemberId }).HasPrincipalKey(member =>
            new { member.ScopeId, member.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(member => member.Assignments).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(member => member.DomainEvents);
    }

    private static string PairedOptionalText(string value, string search) =>
        $"((\"{value}\" IS NULL AND \"{search}\" IS NULL) OR " +
        $"(\"{value}\" IS NOT NULL AND \"{search}\" IS NOT NULL AND " +
        $"length(trim(\"{value}\")) > 0 AND length(trim(\"{search}\")) > 0))";

    private static string OptionalText(string value) =>
        $"(\"{value}\" IS NULL OR length(trim(\"{value}\")) > 0)";
}
