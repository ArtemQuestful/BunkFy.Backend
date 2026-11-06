namespace BunkFy.Host.Api;

using System.Security.Cryptography;
using BunkFy.Modules.Stations.Application;
using Microsoft.Extensions.Options;

/// <summary>Deployment-supplied versioned PIN keys; never generated from or stored in the database.</summary>
public sealed class ConfiguredStationPepperProvider : IStationPepperProvider, IDisposable
{
    private readonly Dictionary<string, byte[]> keys = new(StringComparer.Ordinal);

    public ConfiguredStationPepperProvider(IConfiguration configuration, IOptions<StationOptions> options)
    {
        try
        {
            foreach (IConfigurationSection entry in configuration.GetSection("Stations:PepperKeys").GetChildren())
            {
                if (this.keys.Count >= 3 || entry.Key.Length is < 1 or > 64 ||
                    !entry.Key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') ||
                    entry.Value is not { Length: > 0 and <= 128 } encoded)
                { throw new InvalidOperationException("Invalid station PIN key configuration."); }
                byte[] key = Convert.FromBase64String(encoded);
                if (key.Length is < 32 or > 64)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new InvalidOperationException("Invalid station PIN key configuration.");
                }
                this.keys.Add(entry.Key, key);
            }
            if (!this.keys.ContainsKey(options.Value.PepperVersion))
            { throw new InvalidOperationException("The active station PIN key is missing."); }
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            this.Dispose();
            // Configuration values and inner errors must not become startup diagnostics.
            throw new InvalidOperationException("Station PIN keys require valid deployment-owned configuration.");
        }
    }

    public bool TryGet(string version, out ReadOnlyMemory<byte> pepper)
    {
        bool found = this.keys.TryGetValue(version, out byte[]? key);
        pepper = found ? key : ReadOnlyMemory<byte>.Empty;
        return found;
    }

    public void Dispose()
    {
        foreach (byte[] key in this.keys.Values)
        { CryptographicOperations.ZeroMemory(key); }
        this.keys.Clear();
    }
}
