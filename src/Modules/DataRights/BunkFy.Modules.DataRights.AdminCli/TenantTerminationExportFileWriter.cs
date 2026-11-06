namespace BunkFy.Modules.DataRights.AdminCli;

using BunkFy.Modules.DataRights.Application.Models;
using Gma.Framework.Results;

internal static class TenantTerminationExportFileWriter
{
    internal static readonly Error OutputPathInvalid = new(
        "DataRights.TenantTerminationExportOutputPathInvalid",
        "The export output path is invalid or unavailable.");
    internal static readonly Error OutputFileExists = new(
        "DataRights.TenantTerminationExportOutputFileExists",
        "The export output file already exists.");
    internal static readonly Error OutputWriteFailed = new(
        "DataRights.TenantTerminationExportOutputWriteFailed",
        "The verified export could not be written completely.");

    public static async Task<Result<TenantTerminationExportDownloadReport>>
        WriteAsync(
            string path,
            bool overwrite,
            DataRightsExportDownload download,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(download);
        string? temporaryPath = null;
        string? fullPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Result.Failure<
                    TenantTerminationExportDownloadReport>(
                        OutputPathInvalid);
            }

            fullPath = Path.GetFullPath(path.Trim());
            string? directory = Path.GetDirectoryName(fullPath);
            string fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(directory) ||
                string.IsNullOrWhiteSpace(fileName) ||
                !Directory.Exists(directory) ||
                Directory.Exists(fullPath))
            {
                return Result.Failure<
                    TenantTerminationExportDownloadReport>(
                        OutputPathInvalid);
            }

            if (!overwrite && File.Exists(fullPath))
            {
                return Result.Failure<
                    TenantTerminationExportDownloadReport>(
                        OutputFileExists);
            }

            temporaryPath = Path.Combine(
                directory,
                $".{fileName}.{Guid.NewGuid():N}.tmp");
            await using (FileStream destination = new(
                temporaryPath,
                CreateOptions()))
            {
                await download.Content.CopyToAsync(
                    destination,
                    cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (destination.Length != download.ContentLength)
                {
                    return Result.Failure<
                        TenantTerminationExportDownloadReport>(
                            OutputWriteFailed);
                }
            }

            File.Move(temporaryPath, fullPath, overwrite);
            temporaryPath = null;
            return Result.Success(new TenantTerminationExportDownloadReport(
                fullPath,
                download.ContentLength,
                download.FileName));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException or
                UnauthorizedAccessException)
        {
            return Result.Failure<TenantTerminationExportDownloadReport>(
                OutputPathInvalid);
        }
        catch (IOException) when (
            !overwrite &&
            fullPath is not null &&
            File.Exists(fullPath))
        {
            return Result.Failure<TenantTerminationExportDownloadReport>(
                OutputFileExists);
        }
        catch (IOException)
        {
            return Result.Failure<TenantTerminationExportDownloadReport>(
                OutputWriteFailed);
        }
        finally
        {
            await download.Content.DisposeAsync().ConfigureAwait(false);
            TryDeleteTemporary(temporaryPath);
        }
    }

    private static FileStreamOptions CreateOptions()
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead |
                UnixFileMode.UserWrite;
        }

        return options;
    }

    private static void TryDeleteTemporary(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Preserve the primary write result during best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the primary write result during best-effort cleanup.
        }
    }
}

internal sealed record TenantTerminationExportDownloadReport(
    string OutputPath,
    long ContentLength,
    string SourceFileName);
