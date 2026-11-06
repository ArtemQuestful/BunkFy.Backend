namespace BunkFy.Modules.DataRights.Tests.Api;

using BunkFy.Modules.DataRights.AdminCli;
using BunkFy.Modules.DataRights.Application.Models;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class TenantTerminationExportFileWriterTests
{
    [Fact]
    public async Task Verified_content_is_written_atomically_and_privately()
    {
        await using TemporaryDirectory directory = new();
        byte[] content = [1, 2, 3, 4, 5];
        TrackingStream source = new(content);
        string outputPath = Path.Combine(directory.Path, "tenant-export.zip");

        Result<TenantTerminationExportDownloadReport> result =
            await TenantTerminationExportFileWriter.WriteAsync(
                outputPath,
                overwrite: false,
                Download(source, content.Length),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(content, await File.ReadAllBytesAsync(outputPath));
        Assert.True(source.Disposed);
        Assert.Single(Directory.GetFiles(directory.Path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(outputPath));
        }
    }

    [Fact]
    public async Task Existing_output_is_preserved_without_overwrite()
    {
        await using TemporaryDirectory directory = new();
        string outputPath = Path.Combine(directory.Path, "tenant-export.zip");
        await File.WriteAllTextAsync(outputPath, "existing");
        TrackingStream source = new([1, 2, 3]);

        Result<TenantTerminationExportDownloadReport> result =
            await TenantTerminationExportFileWriter.WriteAsync(
                outputPath,
                overwrite: false,
                Download(source, source.Length),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            TenantTerminationExportFileWriter.OutputFileExists,
            result.Error);
        Assert.Equal("existing", await File.ReadAllTextAsync(outputPath));
        Assert.True(source.Disposed);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Truncated_content_leaves_no_output_or_temporary_file()
    {
        await using TemporaryDirectory directory = new();
        string outputPath = Path.Combine(directory.Path, "tenant-export.zip");
        TrackingStream source = new([1, 2, 3]);

        Result<TenantTerminationExportDownloadReport> result =
            await TenantTerminationExportFileWriter.WriteAsync(
                outputPath,
                overwrite: false,
                Download(source, source.Length + 1),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            TenantTerminationExportFileWriter.OutputWriteFailed,
            result.Error);
        Assert.False(File.Exists(outputPath));
        Assert.True(source.Disposed);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    private static DataRightsExportDownload Download(
        Stream content,
        long length) => new(content, length, "tenant-export.zip");

    private sealed class TrackingStream(byte[] content)
        : MemoryStream(content, writable: false)
    {
        public bool Disposed { get; private set; }

        public override ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            this.Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public TemporaryDirectory()
        {
            this.Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"bunkfy-tenant-export-{Guid.NewGuid():N}");
            Directory.CreateDirectory(this.Path);
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(this.Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
