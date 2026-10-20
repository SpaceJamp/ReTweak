using System.Reflection;

// ReTweak uses Windows APIs throughout (NVAPI, PawnIO, registry, ACLs): say so, so the platform analyzer is satisfied.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace ReTweak.Core;

/// <summary>
/// Which build this is, from the informational version Directory.Build.props stamps in at build time:
/// "0.9.0+36.0855fce" (version + commit count + short hash), with ".dirty" for uncommitted changes.
/// </summary>
public static class BuildInfo
{
    private static readonly string Raw =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>"0.9.0".</summary>
    public static string Version { get; } = Raw.Split('+')[0];

    /// <summary>Commits in the history at build time; 0 when built without git.</summary>
    public static int Build { get; } = Raw.Split('+') is [_, var meta] && int.TryParse(meta.Split('.')[0], out int n) ? n : 0;

    /// <summary>Short commit hash, or "unknown".</summary>
    public static string Commit { get; } = Raw.Split('+') is [_, var meta] && meta.Split('.') is { Length: >= 2 } p ? p[1] : "unknown";

    /// <summary>Built with uncommitted changes.</summary>
    public static bool Dirty { get; } = Raw.EndsWith(".dirty", StringComparison.Ordinal);

    /// <summary>"0.9.0 (build 36, 0855fce)", with ", modified" for a dirty build.</summary>
    public static string Display { get; } = $"{Version} (build {Build}, {Commit}{(Dirty ? ", modified" : "")})";

    /// <summary>The full string, for logs: "0.9.0+36.0855fce".</summary>
    public static string Full => Raw;
}
