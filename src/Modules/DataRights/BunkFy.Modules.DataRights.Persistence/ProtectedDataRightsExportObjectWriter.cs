namespace BunkFy.Modules.DataRights.Persistence;

using System.Globalization;
using System.Security.Cryptography;
using BunkFy.Modules.DataRights.Application.Models;
using Gma.Framework.FileManagement;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Options;

internal sealed class ProtectedDataRightsExportObjectWriter(
    IDataRightsExportEnvelopeProtector protector,
    IFileStorage storage,
    IOptions<DataRightsExportArtifactOptions> options,
    ISystemClock clock)
{
    private readonly DataRightsExportArtifactOptions options = options.Value;

    public async Task<DataRightsProtectedExportObject> WriteAsync(
        Stream plaintext,
        long plaintextLength,
        byte[] plaintextHash,
        FileStorageObjectKey storageKey,
        DataRightsExportProtectionContext protectionContext,
        string formatMetadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(plaintextHash);
        ArgumentNullException.ThrowIfNull(protectionContext);
        if (!plaintext.CanRead ||
            !plaintext.CanSeek ||
            plaintextLength is <= 0 ||
            plaintextLength > this.options.MaximumPlaintextBytes ||
            plaintextHash.Length != SHA256.HashSizeInBytes ||
            string.IsNullOrWhiteSpace(storageKey.Value) ||
            !IsStableFormat(formatMetadata))
        {
            throw new DataRightsExportGenerationException(
                "protected-object-invalid");
        }

        DateTimeOffset protectionStartedAtUtc = clock.UtcNow;
        if (protectionContext.ExpiresAtUtc <= protectionStartedAtUtc)
        {
            throw new DataRightsExportGenerationException("artifact-expired");
        }

        plaintext.Position = 0;
        await using FileStream encrypted = DataRightsExportTemporaryFile.Create();
        DataRightsExportProtectionResult protectedResult =
            await protector.ProtectAsync(
                plaintext,
                plaintextLength,
                encrypted,
                protectionContext,
                cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await encrypted.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (encrypted.Length != protectedResult.EncryptedLength)
        {
            throw new DataRightsExportGenerationException(
                "encrypted-length-mismatch");
        }

        bool writeAttempted = false;
        bool putCompleted = false;
        try
        {
            encrypted.Position = 0;
            writeAttempted = true;
            FileStorageObjectProperties stored = await storage.PutAsync(
                new FileStorageWriteRequest(
                    storageKey,
                    encrypted,
                    encrypted.Length,
                    "application/octet-stream",
                    fileName: null,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["module"] =
                            Contracts.DataRightsModuleMetadata.Name,
                        ["format"] = formatMetadata,
                        ["key-version"] =
                            protectedResult.KeyVersion.ToString(
                                CultureInfo.InvariantCulture)
                    }),
                cancellationToken).ConfigureAwait(false);
            putCompleted = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (stored.ContentLength != protectedResult.EncryptedLength)
            {
                throw new DataRightsExportGenerationException(
                    "stored-length-mismatch",
                    DataRightsExportFailureDisposition.Retryable);
            }

            await this.VerifyStoredAsync(
                storageKey,
                protectionContext,
                protectedResult,
                plaintextHash,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset availableAtUtc = clock.UtcNow;
            if (availableAtUtc < protectionStartedAtUtc ||
                protectionContext.ExpiresAtUtc <= availableAtUtc)
            {
                throw new DataRightsExportGenerationException(
                    "artifact-expired");
            }

            return new DataRightsProtectedExportObject(
                storageKey.Value,
                protectedResult.EncryptedLength,
                Convert.ToHexStringLower(plaintextHash),
                protectedResult.KeyVersion,
                protectedResult.FormatVersion,
                availableAtUtc,
                protectionContext.ExpiresAtUtc);
        }
        catch (Exception exception) when (writeAttempted)
        {
            await this.DeleteUnverifiedAsync(
                storageKey,
                putCompleted,
                exception).ConfigureAwait(false);
            throw;
        }
    }

    private async Task VerifyStoredAsync(
        FileStorageObjectKey storageKey,
        DataRightsExportProtectionContext protectionContext,
        DataRightsExportProtectionResult expected,
        byte[] expectedPlaintextHash,
        CancellationToken cancellationToken)
    {
        FileStorageReadResult? stored = await storage.OpenReadAsync(
            storageKey,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (stored is null ||
            stored.Properties.ContentLength != expected.EncryptedLength)
        {
            throw new DataRightsExportGenerationException(
                "stored-artifact-unavailable",
                DataRightsExportFailureDisposition.Retryable);
        }

        long maximumEncryptedBytes = checked(
            this.options.MaximumPlaintextBytes +
            DataRightsExportArtifactOptions.MaximumEncryptionOverheadBytes);
        await using FileStream encrypted = DataRightsExportTemporaryFile.Create();
        BoundedWriteStream boundedEncrypted = new(
            encrypted,
            maximumEncryptedBytes);
        try
        {
            await stored.CopyToAsync(
                boundedEncrypted,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await boundedEncrypted.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DataRightsExportBufferLimitException exception)
        {
            throw new DataRightsExportGenerationException(
                "stored-artifact-limit-exceeded",
                exception,
                DataRightsExportFailureDisposition.Retryable);
        }

        if (encrypted.Length != expected.EncryptedLength)
        {
            throw new DataRightsExportGenerationException(
                "stored-length-mismatch",
                DataRightsExportFailureDisposition.Retryable);
        }

        encrypted.Position = 0;
        using HashingNullWriteStream verifiedPlaintext = new();
        DataRightsExportProtectionResult verified =
            await protector.UnprotectAsync(
                encrypted,
                verifiedPlaintext,
                protectionContext,
                cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] actualHash = verifiedPlaintext.CompleteHash();
        try
        {
            if (verified.FormatVersion != expected.FormatVersion ||
                verified.KeyVersion != expected.KeyVersion ||
                verified.PlaintextLength != expected.PlaintextLength ||
                verified.EncryptedLength != expected.EncryptedLength ||
                verifiedPlaintext.Length != expected.PlaintextLength ||
                !CryptographicOperations.FixedTimeEquals(
                    actualHash,
                    expectedPlaintextHash))
            {
                throw new DataRightsExportGenerationException(
                    "stored-artifact-verification-failed",
                    DataRightsExportFailureDisposition.Retryable);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualHash);
        }
    }

    private async Task DeleteUnverifiedAsync(
        FileStorageObjectKey storageKey,
        bool putCompleted,
        Exception generationFailure)
    {
        try
        {
            bool deleted = await storage.DeleteAsync(
                storageKey,
                CancellationToken.None).ConfigureAwait(false);
            if (putCompleted && !deleted)
            {
                throw new IOException(
                    "The unverified protected export object was not deleted.");
            }
        }
        catch (Exception cleanupFailure)
        {
            throw new DataRightsExportGenerationException(
                "unverified-artifact-cleanup-failed",
                new AggregateException(generationFailure, cleanupFailure),
                DataRightsExportFailureDisposition.Retryable);
        }
    }

    private static bool IsStableFormat(string value) =>
        value.Length is > 0 and <= 32 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '-');
}
