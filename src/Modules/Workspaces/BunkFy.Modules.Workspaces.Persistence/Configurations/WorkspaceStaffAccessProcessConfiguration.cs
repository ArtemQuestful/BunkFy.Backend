namespace BunkFy.Modules.Workspaces.Persistence.Configurations;

using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class WorkspaceStaffAccessProcessConfiguration
    : IEntityTypeConfiguration<WorkspaceStaffAccessProcess>
{
    public void Configure(EntityTypeBuilder<WorkspaceStaffAccessProcess> builder)
    {
        builder.ToTable("staff_access_processes", table =>
        {
            table.HasCheckConstraint("CK_staff_access_process_version", "\"Version\" >= 1");
            table.HasCheckConstraint("CK_staff_access_process_target", "\"TargetState\" BETWEEN 1 AND 3");
            table.HasCheckConstraint(
                "CK_staff_access_process_restoration_disposition",
                "(\"TargetState\" = 1 AND \"RestorationDisposition\" IN (2, 3)) OR " +
                "(\"TargetState\" IN (2, 3) AND \"RestorationDisposition\" = 1)");
            table.HasCheckConstraint("CK_staff_access_process_state", "\"State\" BETWEEN 1 AND 4");
            table.HasCheckConstraint("CK_staff_access_process_staff_version", "\"TargetStaffVersion\" >= 2");
        });
        builder.HasKey(process => process.Id);
        builder.HasAlternateKey(process => new { process.ScopeId, process.Id });
        builder.Property(process => process.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(process => process.SubjectId)
            .HasMaxLength(WorkspaceStaffAccessProcess.SubjectIdMaxLength).IsRequired();
        builder.Property(process => process.TargetState).HasConversion<int>().IsRequired();
        builder.Property(process => process.RestorationDisposition)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(process => process.RequestedBy)
            .HasMaxLength(WorkspaceStaffAccessProcess.ActorIdMaxLength).IsRequired();
        builder.Property(process => process.State).HasConversion<int>().IsRequired();
        builder.Property(process => process.FailureCode)
            .HasMaxLength(WorkspaceStaffAccessProcess.FailureCodeMaxLength);
        builder.Property(process => process.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(process => new
        {
            process.ScopeId,
            process.StaffMemberId,
            process.TargetStaffVersion
        }).IsUnique();
        builder.HasIndex(process => new
        {
            process.ScopeId,
            process.StaffMemberId,
            process.State,
            process.CreatedAtUtc
        });
        builder.HasIndex(process => new
        {
            process.ScopeId,
            process.SubjectId,
            process.State,
            process.Id
        });
        builder.HasIndex(process => new
        {
            process.ScopeId,
            process.RequestedBy,
            process.Id
        });
        builder.OwnsMany(process => process.ProfileSnapshots, snapshots =>
        {
            snapshots.ToTable("staff_access_profile_snapshots");
            snapshots.WithOwner().HasForeignKey("ProcessId");
            snapshots.Property<Guid>("ProcessId");
            snapshots.HasKey(
                "ProcessId",
                nameof(WorkspaceStaffAccessProfileSnapshot.ProfileId),
                nameof(WorkspaceStaffAccessProfileSnapshot.AssignmentScope));
            snapshots.Property(snapshot => snapshot.ProfileId).ValueGeneratedNever();
            snapshots.Property(snapshot => snapshot.AssignmentScope)
                .HasMaxLength(WorkspaceStaffAccessProfileSnapshot.AssignmentScopeMaxLength)
                .IsRequired();
        });
        builder.Ignore(process => process.DomainEvents);
    }
}
