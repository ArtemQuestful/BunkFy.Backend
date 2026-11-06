namespace BunkFy.Modules.DataRights.Persistence;

using System.Security.Cryptography;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using Microsoft.Extensions.Options;

internal sealed class ProtectedDataRightsExportArtifactGenerator(
    IDataRightsExportAssembler assembler,
    ProtectedDataRightsExportObjectWriter writer,
    IOptions<DataRightsExportArtifactOptions> options)
    : IDataRightsExportArtifactGenerator
{
    private readonly DataRightsExportArtifactOptions options = options.Value;

    public async Task<DataRightsProtectedExportArtifact> GenerateAsync(
        DataRightsExportGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using FileStream plaintext = DataRightsExportTemporaryFile.Create();
        BoundedWriteStream boundedPlaintext = new(
            plaintext,
            this.options.MaximumPlaintextBytes);
        try
        {
            _ = await assembler.AssembleAsync(
                request,
                boundedPlaintext,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await boundedPlaintext.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DataRightsExportBufferLimitException exception)
        {
            throw new DataRightsExportGenerationException(
                "plaintext-limit-exceeded",
                exception);
        }

        long plaintextLength = plaintext.Length;
        if (plaintextLength is <= 0 ||
            plaintextLength > this.options.MaximumPlaintextBytes)
        {
            throw new DataRightsExportGenerationException(
                "plaintext-limit-exceeded");
        }

        plaintext.Position = 0;
        byte[] plaintextHash = await SHA256.HashDataAsync(
            plaintext,
            cancellationToken).ConfigureAwait(false);
        try
        {
            DataRightsProtectedExportObject protectedObject =
                await writer.WriteAsync(
                    plaintext,
                    plaintextLength,
                    plaintextHash,
                    DataRightsExportStorageKey.Create(
                        request.TenantId,
                        request.ArtifactId),
                    new DataRightsSubjectExportProtectionContext(
                        request.ArtifactId,
                        request.TenantId,
                        request.CaseId,
                        request.CaseType,
                        request.PropertyId,
                        request.DecisionRevision,
                        request.ExpiresAtUtc),
                    "bfdrx-1",
                    cancellationToken).ConfigureAwait(false);
            return new DataRightsProtectedExportArtifact(
                protectedObject.StorageKey,
                protectedObject.EncryptedByteLength,
                protectedObject.PlaintextSha256,
                protectedObject.EncryptionKeyVersion,
                protectedObject.FormatVersion,
                protectedObject.AvailableAtUtc,
                protectedObject.ExpiresAtUtc);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextHash);
        }
    }
}
