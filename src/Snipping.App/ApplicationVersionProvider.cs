using System.Reflection;
using Windows.ApplicationModel;

namespace Snipping.App;

internal static class ApplicationVersionProvider
{
    internal static string GetDisplayVersion()
    {
        // The package identity is the version users actually installed. It is
        // authoritative for MSIX launches and is independent of stale build
        // metadata in an older executable.
        try
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch (Exception)
        {
            // F5/unpackaged runs do not have Package.Current. Fall back to the
            // assembly version, which the MSIX build passes through explicitly.
            var version = typeof(ApplicationVersionProvider).Assembly.GetName().Version
                ?? Assembly.GetEntryAssembly()?.GetName().Version;
            return version?.ToString(4) ?? "0.0.0.0";
        }
    }
}
