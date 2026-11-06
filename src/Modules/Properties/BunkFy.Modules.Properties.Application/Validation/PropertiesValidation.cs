namespace BunkFy.Modules.Properties.Application.Validation;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;

internal static class PropertiesValidation
{
    public static IEnumerable<string> ValidateExpectedVersion(long expectedVersion, string description)
    {
        if (expectedVersion < 1)
        {
            yield return $"Expected {description} version must be positive.";
        }
    }

    public static IEnumerable<string> ValidateActor(
        string? actorId,
        bool required)
    {
        string normalized = actorId?.Trim() ?? string.Empty;
        if (required && normalized.Length == 0)
        {
            yield return "Actor id is required.";
            yield break;
        }

        if (normalized.Length > PropertiesContractLimits.ActorIdMaxLength ||
            normalized.Any(char.IsControl))
        {
            yield return
                $"Actor id must be {PropertiesContractLimits.ActorIdMaxLength} characters or fewer and cannot contain control characters.";
        }
    }

    public static IEnumerable<string> ValidatePropertyWrite(string? name, string? code, string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            yield return "Property name is required.";
        }
        else if (name.Trim().Length > Property.PropertyNameMaxLength)
        {
            yield return $"Property name cannot exceed {Property.PropertyNameMaxLength} characters.";
        }
        else if (name.Any(char.IsControl))
        {
            yield return "Property name cannot contain control characters.";
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            yield return "Property code is required.";
        }
        else if (code.Trim().Length > Property.PropertyCodeMaxLength)
        {
            yield return $"Property code cannot exceed {Property.PropertyCodeMaxLength} characters.";
        }
        else if (code.Any(char.IsControl))
        {
            yield return "Property code cannot contain control characters.";
        }

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            yield return "Time zone id is required.";
        }
        else if (timeZoneId.Trim().Length > Property.TimeZoneIdMaxLength)
        {
            yield return $"Time zone id cannot exceed {Property.TimeZoneIdMaxLength} characters.";
        }
        else if (timeZoneId.Any(char.IsControl))
        {
            yield return "Time zone id cannot contain control characters.";
        }
    }

    public static IEnumerable<string> ValidateOptionalPropertyTimeZone(
        string? timeZoneId)
    {
        if (timeZoneId is null)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            yield return "Time zone id cannot be blank when supplied.";
        }
        else if (timeZoneId.Trim().Length > Property.TimeZoneIdMaxLength)
        {
            yield return
                $"Time zone id cannot exceed {Property.TimeZoneIdMaxLength} characters.";
        }
        else if (timeZoneId.Any(char.IsControl))
        {
            yield return "Time zone id cannot contain control characters.";
        }
    }

    public static IEnumerable<string> ValidateRoomWrite(string? name, string? buildingLabel, string? floorLabel)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            yield return "Room name is required.";
        }
        else if (name.Trim().Length > Room.RoomNameMaxLength)
        {
            yield return $"Room name cannot exceed {Room.RoomNameMaxLength} characters.";
        }

        if (!string.IsNullOrWhiteSpace(buildingLabel) && buildingLabel.Trim().Length > Room.PhysicalLabelMaxLength)
        {
            yield return $"Building label cannot exceed {Room.PhysicalLabelMaxLength} characters.";
        }

        if (!string.IsNullOrWhiteSpace(floorLabel) && floorLabel.Trim().Length > Room.PhysicalLabelMaxLength)
        {
            yield return $"Floor label cannot exceed {Room.PhysicalLabelMaxLength} characters.";
        }
    }

    public static IEnumerable<string> ValidateBedWrite(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            yield return "Bed label is required.";
        }
        else if (label.Trim().Length > Room.BedLabelMaxLength)
        {
            yield return $"Bed label cannot exceed {Room.BedLabelMaxLength} characters.";
        }
    }
}
