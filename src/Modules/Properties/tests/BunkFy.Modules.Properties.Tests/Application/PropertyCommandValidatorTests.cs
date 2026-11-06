namespace BunkFy.Modules.Properties.Tests;

using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Validation;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyCommandValidatorTests
{
    [Theory]
    [InlineData("Hostel\nOne", "hostel-one", "Etc/UTC")]
    [InlineData("Hostel One", "hostel\none", "Etc/UTC")]
    [InlineData("Hostel One", "hostel-one", "Etc/\nUTC")]
    public void Create_rejects_control_characters_before_dependencies(
        string name,
        string code,
        string timeZoneId)
    {
        var command = new CreatePropertyCommand(
            Guid.NewGuid(),
            name,
            code,
            timeZoneId,
            "operator:one");

        string[] errors = [.. new CreatePropertyCommandValidator()
            .Validate(command)];

        Assert.Contains(errors, error => error.Contains(
            "control characters",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Update_rejects_control_characters_in_an_optional_time_zone()
    {
        var command = new UpdatePropertyCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Hostel One",
            "hostel-one",
            "Etc/\nUTC",
            ExpectedVersion: 1);

        string[] errors = [.. new UpdatePropertyCommandValidator()
            .Validate(command)];

        Assert.Contains(
            "Time zone id cannot contain control characters.",
            errors);
    }

    [Fact]
    public void Update_accepts_an_omitted_time_zone()
    {
        var command = new UpdatePropertyCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Hostel One",
            "hostel-one",
            TimeZoneId: null,
            ExpectedVersion: 1);

        Assert.Empty(new UpdatePropertyCommandValidator().Validate(command));
    }
}
