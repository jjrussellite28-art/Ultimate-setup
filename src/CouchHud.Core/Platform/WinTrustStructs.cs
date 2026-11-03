using System.Runtime.InteropServices;

namespace CouchHud.Core.Platform;

/// <summary>WINTRUST_FILE_INFO of wintrust.h.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WintrustFileInfo
{
    public int cbStruct;
    public string pcwszFilePath;
    public IntPtr hFile;
    public IntPtr pgKnownSubject;
}

/// <summary>
/// WINTRUST_DATA of wintrust.h including the trailing <c>pSignatureSettings</c> of Windows 8 and later (88 bytes on x64, 64 on x86). The size goes
/// into <c>cbStruct</c>: a struct that is too short for the size WinVerifyTrust is told would be read past its end.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WintrustData
{
    public int cbStruct;
    public IntPtr pPolicyCallbackData;
    public IntPtr pSIPClientData;
    public int dwUIChoice;
    public int fdwRevocationChecks;
    public int dwUnionChoice;
    public IntPtr pFile;
    public int dwStateAction;
    public IntPtr hWVTStateData;
    public IntPtr pwszURLReference;
    public int dwProvFlags;
    public int dwUIContext;
    public IntPtr pSignatureSettings;
}
