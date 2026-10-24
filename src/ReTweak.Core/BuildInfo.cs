using System.Reflection;

// ReTweak uses Windows APIs throughout (NVAPI, PawnIO, registry, ACLs): say so, so the platform analyzer is satisfied.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
// Native DLLs (nvml.dll, kernel32, user32...) come from System32 only, never from the app's own folder or the
// current directory, so a DLL planted next to an elevated ReTweak can't be loaded in its place.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace ReTweak.Core;

/// <summary>
/// Which build this is, from the informational version Directory.Build.props stamps in at build time:
/// "&lt;version&gt;+&lt;commits&gt;.&lt;hash&gt;" (version + commit count + short hash), with ".dirty" for uncommitted changes.
/// </summary>
public static class BuildInfo
{
    private static readonly string Raw =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>VersionPrefix from Directory.Build.props, e.g. "1.2.3".</summary>
    public static string Version { get; } = Raw.Split('+')[0];

    /// <summary>Commits in the history at build time; 0 when built without git.</summary>
    public static int Build { get; } = Raw.Split('+') is [_, var meta] && int.TryParse(meta.Split('.')[0], out int n) ? n : 0;

    /// <summary>Short commit hash, or "unknown".</summary>
    public static string Commit { get; } = Raw.Split('+') is [_, var meta] && meta.Split('.') is { Length: >= 2 } p ? p[1] : "unknown";

    /// <summary>Built with uncommitted changes.</summary>
    public static bool Dirty { get; } = Raw.EndsWith(".dirty", StringComparison.Ordinal);

    /// <summary>"&lt;version&gt; (build &lt;commits&gt;, &lt;hash&gt;)", with ", modified" for a dirty build.</summary>
    public static string Display { get; } = $"{Version} (build {Build}, {Commit}{(Dirty ? ", modified" : "")})";

    /// <summary>The full string, for logs: "&lt;version&gt;+&lt;commits&gt;.&lt;hash&gt;".</summary>
    public static string Full => Raw;
}
