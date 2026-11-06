namespace BunkFy.Modules.Staff.Tests.Application;

using BunkFy.Modules.Staff.Application.Queries;
using BunkFy.Modules.Staff.Application.Validation;
using BunkFy.Modules.Staff.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ResolveStaffAccountDirectoryQueryValidatorTests
{
    [Fact]
    public void Query_requires_a_bounded_set_of_valid_subject_identifiers()
    {
        ResolveStaffAccountDirectoryQueryValidator validator = new();

        Assert.Empty(validator.Validate(new(["subject-a", "subject-b"])));
        Assert.NotEmpty(validator.Validate(new([])));
        Assert.NotEmpty(validator.Validate(new(["  "])));
        Assert.NotEmpty(validator.Validate(new([
            new string('s', StaffContractLimits.AuthSubjectIdMaxLength + 1)
        ])));
        Assert.NotEmpty(validator.Validate(new(
            Enumerable.Range(
                    1,
                    StaffContractLimits.AccountDirectoryResolveMaxCount + 1)
                .Select(index => $"subject-{index}")
                .ToArray())));
    }
}
