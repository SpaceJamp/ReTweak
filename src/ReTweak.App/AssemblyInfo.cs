// Native DLLs (user32, shell32...) from System32 only, never planted next to an elevated ReTweak.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
