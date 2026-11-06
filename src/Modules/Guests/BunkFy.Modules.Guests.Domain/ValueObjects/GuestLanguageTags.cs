namespace BunkFy.Modules.Guests.Domain.ValueObjects;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.Errors;
using Gma.Framework.Results;

public static class GuestLanguageTags
{
    public const int MaxCount = 12;

    public static string Identity(string value) => string.Create(
        value.Trim().Length,
        value.Trim(),
        static (destination, source) =>
        {
            for (int index = 0; index < source.Length; index++)
            {
                char character = source[index];
                destination[index] = character is >= 'A' and <= 'Z'
                    ? (char)(character + ('a' - 'A'))
                    : character;
            }
        });

    public static Result<string[]> Normalize(string[] requested, string? preferredLanguageTag)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (requested.Length > MaxCount || requested.Any(value =>
                string.IsNullOrWhiteSpace(value) ||
                value.Trim().Length > GuestProfile.LanguageTagMaxLength ||
                !value.Trim().All(character => character is (>= 'A' and <= 'Z') or
                    (>= 'a' and <= 'z') or '-' || char.IsDigit(character))))
        {
            return Result.Failure<string[]>(GuestsDomainErrors.LanguageTagsInvalid);
        }

        string[] identities = requested.Select(Identity).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (preferredLanguageTag is not null &&
            !identities.Contains(Identity(preferredLanguageTag), StringComparer.Ordinal))
        {
            return Result.Failure<string[]>(GuestsDomainErrors.LanguageTagsContradictPreferred);
        }

        return Result.Success(identities);
    }

    public static string[] ForCreation(string[]? requested, string? preferredLanguageTag) =>
        requested is not null ? [.. requested] :
        preferredLanguageTag is null ? [] : [preferredLanguageTag];

    public static Result<string[]> ResolveUpdate(
        string[] existing,
        string? previousPreferred,
        string? requestedPreferred,
        string[]? requested)
    {
        Dictionary<string, string> retained = existing.ToDictionary(Identity, StringComparer.Ordinal);
        if (requested is not null)
        {
            return Result.Success(requested.Select(key => retained.GetValueOrDefault(key, key)).ToArray());
        }

        if (requestedPreferred is not null &&
            !string.Equals(previousPreferred, requestedPreferred, StringComparison.Ordinal))
        {
            string key = Identity(requestedPreferred);
            retained.TryAdd(key, key);
        }

        return retained.Count > MaxCount
            ? Result.Failure<string[]>(GuestsDomainErrors.LanguageTagsInvalid)
            : Result.Success(retained.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value).ToArray());
    }

    public static bool Equivalent(string[] left, string[] right) =>
        left.Select(Identity).ToHashSet(StringComparer.Ordinal).SetEquals(right.Select(Identity));
}
