namespace BunkFy.DataGovernance;

using System.Globalization;

public static class CountryPolicyPackValidator
{
    private const int MaximumCollectionItems = 512;
    private const int MaximumSourceItems = 32;
    private const int MaximumTimeZoneItems = 64;

    public static IReadOnlyList<string> Validate(
        CountryPolicyPackDocument pack) =>
        ValidateCore(pack, timeZoneRules: null);

    public static IReadOnlyList<string> Validate(
        CountryPolicyPackDocument pack,
        CountryPolicyTimeZoneRules timeZoneRules)
    {
        ArgumentNullException.ThrowIfNull(timeZoneRules);
        return ValidateCore(pack, timeZoneRules);
    }

    private static List<string> ValidateCore(
        CountryPolicyPackDocument pack,
        CountryPolicyTimeZoneRules? timeZoneRules)
    {
        ArgumentNullException.ThrowIfNull(pack);
        List<string> errors = [];

        if (pack.SchemaVersion is not 1 and not 2)
        {
            errors.Add("SchemaVersion must be 1 or 2.");
        }

        ValidateKey(pack.PolicyId, "PolicyId", errors);
        ValidatePositive(pack.PolicyVersion, "PolicyVersion", errors);
        ValidateCountryCode(pack.OperatingCountryCode, errors);
        ValidateEnum(pack.ApprovalState, "ApprovalState", errors);
        if (pack.EffectiveAtUtc == default)
        {
            errors.Add("EffectiveAtUtc is required.");
        }

        if (pack.ExpiresAtUtc == default)
        {
            errors.Add("ExpiresAtUtc is required.");
        }
        else if (pack.ExpiresAtUtc <= pack.EffectiveAtUtc)
        {
            errors.Add("ExpiresAtUtc must be later than EffectiveAtUtc.");
        }

        ValidateKeys(pack.AccommodationTypes, "AccommodationTypes", errors);
        HashSet<string> guestCategories = ValidateKeys(pack.GuestCategories, "GuestCategories", errors);
        HashSet<string> purposeCodes = ValidatePurposeRules(pack.PurposeRules, errors);
        ValidateFieldRules(pack.FieldRules, guestCategories, purposeCodes, errors);
        ValidateRetentionRules(pack.RetentionRules, errors);
        ValidateRightsRule(
            pack.RightsRule,
            pack.SchemaVersion,
            timeZoneRules,
            errors);
        ValidateRestrictions(pack.Restrictions, errors);
        ValidateKeys(pack.PermittedDataRegions, "PermittedDataRegions", errors);
        ValidateKeys(pack.PermittedTransferProfiles, "PermittedTransferProfiles", errors);
        ValidateAcknowledgements(pack.RequiredAcknowledgements, errors);
        ValidateApproval(pack.Approval, pack, errors);

        return errors;
    }

    public static void ValidateAndThrow(CountryPolicyPackDocument pack)
    {
        IReadOnlyList<string> errors = Validate(pack);
        if (errors.Count > 0)
        {
            throw new CountryPolicyPackValidationException(errors);
        }
    }

    public static void ValidateAndThrow(
        CountryPolicyPackDocument pack,
        CountryPolicyTimeZoneRules timeZoneRules)
    {
        IReadOnlyList<string> errors = Validate(pack, timeZoneRules);
        if (errors.Count > 0)
        {
            throw new CountryPolicyPackValidationException(errors);
        }
    }

    private static HashSet<string> ValidatePurposeRules(
        CountryPolicyPurposeRule[]? rules,
        List<string> errors)
    {
        if (!ValidateCollection(rules, "PurposeRules", errors))
        {
            return [];
        }

        HashSet<string> purposeCodes = new(StringComparer.Ordinal);
        for (int index = 0; index < rules!.Length; index++)
        {
            CountryPolicyPurposeRule? rule = rules[index];
            if (rule is null)
            {
                errors.Add($"PurposeRules[{index}] cannot be null.");
                continue;
            }

            string path = $"PurposeRules[{rule.PurposeCode}]";
            ValidateKey(rule.PurposeCode, $"{path}.PurposeCode", errors);
            if (!purposeCodes.Add(rule.PurposeCode ?? string.Empty))
            {
                errors.Add($"Duplicate purpose code '{rule.PurposeCode}'.");
            }

            ValidateKeys(rule.LegalRuleReferenceKeys, $"{path}.LegalRuleReferenceKeys", errors);
            ValidateEnumValues(rule.AllowedSurfaces, $"{path}.AllowedSurfaces", errors);
            ValidateKeys(rule.AllowedSourceProvenance, $"{path}.AllowedSourceProvenance", errors);
        }

        return purposeCodes;
    }

    private static void ValidateFieldRules(
        CountryPolicyFieldRule[]? rules,
        HashSet<string> guestCategories,
        HashSet<string> purposeCodes,
        List<string> errors)
    {
        if (!ValidateCollection(rules, "FieldRules", errors))
        {
            return;
        }

        HashSet<string> identities = new(StringComparer.Ordinal);
        for (int index = 0; index < rules!.Length; index++)
        {
            CountryPolicyFieldRule? rule = rules[index];
            if (rule is null)
            {
                errors.Add($"FieldRules[{index}] cannot be null.");
                continue;
            }

            string path = $"FieldRules[{rule.FieldPolicyKey}:{rule.GuestCategory}]";
            ValidateKey(rule.FieldPolicyKey, $"{path}.FieldPolicyKey", errors);
            ValidateKey(rule.GuestCategory, $"{path}.GuestCategory", errors);
            ValidateEnum(rule.Requirement, $"{path}.Requirement", errors);
            if (!guestCategories.Contains(rule.GuestCategory ?? string.Empty))
            {
                errors.Add($"{path}.GuestCategory references an unknown guest category.");
            }

            string identity = $"{rule.FieldPolicyKey}|{rule.GuestCategory}";
            if (!identities.Add(identity))
            {
                errors.Add($"Duplicate field rule '{identity}'.");
            }

            HashSet<string> referencedPurposes = ValidateKeys(rule.PurposeCodes, $"{path}.PurposeCodes", errors);
            foreach (string purpose in referencedPurposes.Where(value => !purposeCodes.Contains(value)))
            {
                errors.Add($"{path}.PurposeCodes references unknown purpose '{purpose}'.");
            }
        }
    }

    private static void ValidateRetentionRules(CountryPolicyRetentionRule[]? rules, List<string> errors)
    {
        if (!ValidateCollection(rules, "RetentionRules", errors))
        {
            return;
        }

        HashSet<string> identities = new(StringComparer.Ordinal);
        for (int index = 0; index < rules!.Length; index++)
        {
            CountryPolicyRetentionRule? rule = rules[index];
            if (rule is null)
            {
                errors.Add($"RetentionRules[{index}] cannot be null.");
                continue;
            }

            string path = $"RetentionRules[{rule.RetentionPolicyId}:{rule.RetentionPolicyVersion}]";
            ValidateKey(rule.RetentionPolicyId, $"{path}.RetentionPolicyId", errors);
            ValidatePositive(rule.RetentionPolicyVersion, $"{path}.RetentionPolicyVersion", errors);
            ValidateKey(rule.DataClass, $"{path}.DataClass", errors);
            ValidateKey(rule.Trigger, $"{path}.Trigger", errors);
            if (!TimeSpan.TryParse(rule.Period, CultureInfo.InvariantCulture, out TimeSpan period) ||
                period <= TimeSpan.Zero)
            {
                errors.Add($"{path}.Period must be a positive invariant TimeSpan value.");
            }

            string identity =
                $"{rule.RetentionPolicyId}|{rule.RetentionPolicyVersion}|" +
                $"{rule.DataClass}|{rule.Trigger}";
            if (!identities.Add(identity))
            {
                errors.Add($"Duplicate retention rule '{identity}'.");
            }
        }
    }

    private static void ValidateRightsRule(
        CountryPolicyRightsRule? rule,
        int schemaVersion,
        CountryPolicyTimeZoneRules? timeZoneRules,
        List<string> errors)
    {
        if (rule is null)
        {
            errors.Add("RightsRule is required.");
            return;
        }

        ValidateKey(rule.Registration, "RightsRule.Registration", errors);
        ValidateKey(rule.Export, "RightsRule.Export", errors);
        ValidateKey(rule.Correction, "RightsRule.Correction", errors);
        ValidateKey(rule.Restriction, "RightsRule.Restriction", errors);
        ValidateKey(rule.Erasure, "RightsRule.Erasure", errors);

        if (schemaVersion == 1)
        {
            if (rule.ResponseRules is { Length: > 0 })
            {
                errors.Add("RightsRule.ResponseRules requires SchemaVersion 2.");
            }

            return;
        }

        if (!ValidateCollection(
                rule.ResponseRules,
                "RightsRule.ResponseRules",
                errors))
        {
            return;
        }

        HashSet<CountryPolicyRight> rights = [];
        for (int index = 0; index < rule.ResponseRules!.Length; index++)
        {
            CountryPolicyRightsResponseRule? responseRule =
                rule.ResponseRules[index];
            if (responseRule is null)
            {
                errors.Add($"RightsRule.ResponseRules[{index}] cannot be null.");
                continue;
            }

            string path =
                $"RightsRule.ResponseRules[{responseRule.Right}]";
            ValidateEnum(responseRule.Right, $"{path}.Right", errors);
            if (!rights.Add(responseRule.Right))
            {
                errors.Add(
                    $"Duplicate rights response rule '{responseRule.Right}'.");
            }

            if (!CountryPolicyCalendarDeadlineCalculator.IsValid(
                    responseRule.Period))
            {
                errors.Add(
                    $"{path}.Period must contain a positive bounded calendar period.");
            }

            ValidateTimeZoneIds(
                responseRule.AllowedTimeZoneIds,
                $"{path}.AllowedTimeZoneIds",
                timeZoneRules,
                errors);
        }

        CountryPolicyRight[] required =
        [
            CountryPolicyRight.Export,
            CountryPolicyRight.Correction,
            CountryPolicyRight.Restriction,
            CountryPolicyRight.Erasure
        ];
        foreach (CountryPolicyRight right in required.Where(value =>
                     !rights.Contains(value)))
        {
            errors.Add(
                $"RightsRule.ResponseRules must contain one '{right}' rule.");
        }
    }

    private static void ValidateTimeZoneIds(
        string[]? values,
        string path,
        CountryPolicyTimeZoneRules? timeZoneRules,
        List<string> errors)
    {
        if (!ValidateCollection(
                values,
                path,
                errors,
                MaximumTimeZoneItems))
        {
            return;
        }

        HashSet<string> unique = new(StringComparer.Ordinal);
        HashSet<string> canonicalUnique = new(StringComparer.Ordinal);
        foreach (string? value in values!)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > 128 ||
                value.Any(char.IsControl) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                errors.Add(
                    $"{path} must contain bounded time-zone identifiers.");
                continue;
            }

            if (!unique.Add(value))
            {
                errors.Add($"{path} contains duplicate value '{value}'.");
                continue;
            }

            if (timeZoneRules is null)
            {
                continue;
            }

            if (!timeZoneRules.TryResolve(
                    value,
                    out CountryPolicyTimeZoneResolution resolution))
            {
                errors.Add(
                    $"{path} contains unavailable time zone '{value}'.");
                continue;
            }

            if (resolution.Kind !=
                CountryPolicyTimeZoneResolutionKind.Canonical)
            {
                errors.Add(
                    $"{path} must contain primary TZDB identifiers; '{value}' resolves to '{resolution.CanonicalTimeZoneId}'.");
            }

            if (!canonicalUnique.Add(resolution.CanonicalTimeZoneId))
            {
                errors.Add(
                    $"{path} contains duplicate primary time zone '{resolution.CanonicalTimeZoneId}'.");
            }
        }
    }

    private static void ValidateRestrictions(CountryPolicyRestrictionRule? rule, List<string> errors)
    {
        if (rule is null)
        {
            errors.Add("Restrictions is required.");
            return;
        }

        ValidateKey(rule.Minors, "Restrictions.Minors", errors);
        ValidateKey(rule.Documents, "Restrictions.Documents", errors);
        ValidateKey(rule.SpecialCategoryData, "Restrictions.SpecialCategoryData", errors);
    }

    private static void ValidateAcknowledgements(
        CountryPolicyAcknowledgementRequirement[]? acknowledgements,
        List<string> errors)
    {
        if (acknowledgements is null)
        {
            errors.Add("RequiredAcknowledgements is required.");
            return;
        }

        if (acknowledgements.Length > MaximumCollectionItems)
        {
            errors.Add($"RequiredAcknowledgements cannot contain more than {MaximumCollectionItems} items.");
        }

        HashSet<string> identities = new(StringComparer.Ordinal);
        for (int index = 0; index < acknowledgements.Length; index++)
        {
            CountryPolicyAcknowledgementRequirement? acknowledgement = acknowledgements[index];
            if (acknowledgement is null)
            {
                errors.Add($"RequiredAcknowledgements[{index}] cannot be null.");
                continue;
            }

            string path = $"RequiredAcknowledgements[{acknowledgement.AcknowledgementId}]";
            ValidateKey(acknowledgement.AcknowledgementId, $"{path}.AcknowledgementId", errors);
            ValidatePositive(acknowledgement.AcknowledgementVersion, $"{path}.AcknowledgementVersion", errors);
            string identity = $"{acknowledgement.AcknowledgementId}|{acknowledgement.AcknowledgementVersion}";
            if (!identities.Add(identity))
            {
                errors.Add($"Duplicate acknowledgement '{identity}'.");
            }
        }
    }

    private static void ValidateApproval(
        CountryPolicyApprovalMetadata? approval,
        CountryPolicyPackDocument pack,
        List<string> errors)
    {
        if (approval is null)
        {
            errors.Add("Approval is required.");
            return;
        }

        ValidateText(approval.OwnerReference, "Approval.OwnerReference", errors);
        ValidateText(approval.ReviewerReference, "Approval.ReviewerReference", errors);
        if (approval.ReviewedAtUtc == default)
        {
            errors.Add("Approval.ReviewedAtUtc is required.");
        }
        else if (approval.ReviewedAtUtc > pack.EffectiveAtUtc)
        {
            errors.Add("Approval.ReviewedAtUtc cannot be later than EffectiveAtUtc.");
        }

        if (!ValidateCollection(approval.Sources, "Approval.Sources", errors, MaximumSourceItems))
        {
            return;
        }

        HashSet<string> sourceIds = new(StringComparer.Ordinal);
        for (int index = 0; index < approval.Sources.Length; index++)
        {
            CountryPolicySourceReference? source = approval.Sources[index];
            if (source is null)
            {
                errors.Add($"Approval.Sources[{index}] cannot be null.");
                continue;
            }

            ValidateKey(source.ReferenceId, "Approval.Sources.ReferenceId", errors);
            if (!sourceIds.Add(source.ReferenceId ?? string.Empty))
            {
                errors.Add($"Duplicate source reference '{source.ReferenceId}'.");
            }

            if (!System.Uri.TryCreate(source.Uri, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                source.Uri.Length > 2048)
            {
                errors.Add($"Source '{source.ReferenceId}' must contain a bounded absolute HTTPS URI.");
            }
        }

        if (approval.DetachedSignatureReference is not null)
        {
            ValidateText(approval.DetachedSignatureReference, "Approval.DetachedSignatureReference", errors);
        }
    }

    private static HashSet<string> ValidateKeys(string[]? values, string path, List<string> errors)
    {
        if (!ValidateCollection(values, path, errors))
        {
            return [];
        }

        HashSet<string> unique = new(StringComparer.Ordinal);
        foreach (string value in values!)
        {
            ValidateKey(value, path, errors);
            if (!unique.Add(value ?? string.Empty))
            {
                errors.Add($"{path} contains duplicate value '{value}'.");
            }
        }

        return unique;
    }

    private static bool ValidateCollection<T>(
        T[]? values,
        string path,
        List<string> errors,
        int maximum = MaximumCollectionItems)
    {
        if (values is null || values.Length == 0)
        {
            errors.Add($"{path} must contain at least one item.");
            return false;
        }

        if (values.Length > maximum)
        {
            errors.Add($"{path} cannot contain more than {maximum} items.");
            return false;
        }

        return true;
    }

    private static void ValidateEnumValues<T>(T[]? values, string path, List<string> errors)
        where T : struct, Enum
    {
        if (!ValidateCollection(values, path, errors))
        {
            return;
        }

        HashSet<T> unique = [];
        foreach (T value in values!)
        {
            ValidateEnum(value, path, errors);
            if (!unique.Add(value))
            {
                errors.Add($"{path} contains duplicate value '{value}'.");
            }
        }
    }

    private static void ValidateCountryCode(string? value, List<string> errors)
    {
        if (!Iso3166Alpha2CountryCodes.Contains(value))
        {
            errors.Add("OperatingCountryCode must be a recognized uppercase ISO 3166-1 alpha-2 code.");
        }
    }

    private static void ValidateKey(string? value, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value[0] is < 'a' or > 'z' ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z') and
                    not (>= '0' and <= '9') and
                    not '.' and not '-' and not '_'))
        {
            errors.Add($"{path} must be a lowercase ASCII policy key with at most 128 characters.");
        }
    }

    private static void ValidateText(string? value, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
        {
            errors.Add($"{path} must contain bounded text without control characters.");
        }
    }

    private static void ValidatePositive(int value, string path, List<string> errors)
    {
        if (value <= 0)
        {
            errors.Add($"{path} must be greater than zero.");
        }
    }

    private static void ValidateEnum<T>(T value, string path, List<string> errors)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value) || Convert.ToInt32(value, CultureInfo.InvariantCulture) == 0)
        {
            errors.Add($"{path} must be a known non-zero value.");
        }
    }
}

public sealed class CountryPolicyPackValidationException(IReadOnlyList<string> errors)
    : Exception("The country-policy pack is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors.ToArray();
}
