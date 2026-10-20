// The CLI drives ReTweak.Core, which is Windows-only.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
// Native DLLs from System32 only, never planted next to the exe.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
