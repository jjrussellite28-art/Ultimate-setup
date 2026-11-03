using System.Runtime.InteropServices;

// Native libraries come from System32 only, never from the application folder or the search path (a planted DLL next to the exe would otherwise load).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
