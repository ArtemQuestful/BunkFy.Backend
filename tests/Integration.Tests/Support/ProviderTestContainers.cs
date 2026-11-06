namespace Integration.Tests;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;

internal static class ProviderTestContainers
{
    // Prepared by eng/build-provider-images.ps1; never fall back to an
    // unavailable floating upstream image or implicitly pull a local tag.
    private const string MinioImage = "bunkfy-provider-silo:c3ad836aed0d-nonroot-v1";

    internal static ContainerBuilder Minio() => new ContainerBuilder(MinioImage)
        .WithImagePullPolicy(PullPolicy.Never)
        .WithLabel("bunkfy.test.fixture", "source-silo-c3ad836aed0d")
        .WithEnvironment("MINIO_UPDATE", "off")
        .WithEnvironment("SILO_UPDATE", "off")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
            request.ForPath("/minio/health/ready").ForPort(9000)))
        .WithCreateParameterModifier(parameters =>
        {
            parameters.User = "10001:10001";
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
            host.MemorySwap = host.Memory;
            host.NanoCPUs = 1_000_000_000;
            host.PidsLimit = 128;
            host.CapDrop = ["ALL"];
            host.SecurityOpt = ["no-new-privileges:true"];
        });
}
