namespace Integration.Tests;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;

internal static class ProviderTestContainers
{
    // Prepared by eng/build-provider-images.ps1; never fall back to an
    // unavailable floating upstream image or implicitly pull a local tag.
    private const string MinioImage = "bunkfy-provider-minio:9e49d5e7a648-go1.27.3";

    internal static ContainerBuilder Minio() => new ContainerBuilder(MinioImage)
        .WithImagePullPolicy(PullPolicy.Never)
        .WithLabel("bunkfy.test.fixture", "source-minio-9e49d5e7a648")
        .WithEnvironment("MINIO_UPDATE", "off")
        .WithCreateParameterModifier(parameters =>
        {
            var host = parameters.HostConfig ?? throw new InvalidOperationException("Fixture host configuration is missing.");
            var ports = host.PortBindings ?? throw new InvalidOperationException("Fixture port bindings are missing.");
            foreach (var bindings in ports.Values)
            {
                foreach (var binding in bindings)
                {
                    binding.HostIP = "127.0.0.1";
                }
            }

            host.Memory = 512L * 1024 * 1024;
            host.NanoCPUs = 1_000_000_000;
        });
}
