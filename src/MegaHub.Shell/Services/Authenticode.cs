using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using MegaHub.Core.Library;
using MegaHub.Core.Platform;

namespace MegaHub.Shell.Services;

/// <summary>
/// The Authenticode signature of a program file that is held open. WinVerifyTrust is given the open handle (so the signature is judged on the
/// very file that is hashed and started, not on a path that could be swapped), and the signer is read from the verification state it keeps,
/// not from the file again. The chain is then built a second time by Mega Hub with only the machine's own roots as trust anchors
/// (LocalMachine\Root and LocalMachine\AuthRoot: a root a user added to the user store does not count), the Code Signing usage required, and the
/// signer's publisher must be one of the known ones (<see cref="WandSetup.SignerMatches(System.Security.Cryptography.X509Certificates.X500DistinguishedName)"/>).
/// Anything missing means "not verified".
/// </summary>
public static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const int StateVerify = 1, StateClose = 2;

    // CRYPT_PROVIDER_SGNR and CRYPT_PROVIDER_CERT of wintrust.h (the array stride needs the whole certificate struct).
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderSigner
    {
        public int cbStruct;
        public System.Runtime.InteropServices.ComTypes.FILETIME sftVerifyAsOf;
        public int csCertChain;
        public IntPtr pasCertChain;
        public int dwSignerType;
        public IntPtr psSigner;
        public int dwError;
        public int csCounterSigners;
        public IntPtr pasCounterSigners;
        public IntPtr pChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCert
    {
        public int cbStruct;
        public IntPtr pCert;
        public int fCommercial, fTrustedRoot, fSelfSigned, fTestCert;
        public int dwRevokedReason, dwConfidence, dwError;
        public IntPtr pTrustListContext;
        public int fTrustListSignerCert;
        public IntPtr pCtlContext;
        public int dwCtlError, fIsCyclic;
        public IntPtr pChainElement;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, int signerIndex, int counterSigner, int counterSignerIndex);

    /// <summary>The machine's own trust anchors: LocalMachine\Root and LocalMachine\AuthRoot, read only. Never the current user's store.</summary>
    public static X509Certificate2Collection MachineRoots()
    {
        var roots = new X509Certificate2Collection();
        foreach (var name in new[] { StoreName.Root, StoreName.AuthRoot })
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            roots.AddRange(store.Certificates);
        }
        return roots;
    }

    /// <summary>
    /// True when the held file carries a valid Authenticode signature (WinVerifyTrust on the open handle, no network lookup), the signer chains
    /// to a machine root with the Code Signing usage, and the signer is a known publisher of Wand. The file is not closed.
    /// </summary>
    public static bool VerifyPublisher(HeldProgram held)
    {
        X509Certificate2Collection? roots = null;
        try
        {
            roots = MachineRoots();
            using var signer = Verify(held, roots);
            return signer is not null && WandSetup.SignerMatches(signer.SubjectName);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (roots is not null) foreach (var root in roots) root.Dispose();
        }
    }

    /// <summary>The signer's certificate, or null when the signature or the chain does not hold. Only the chain, not the publisher name, is judged here.</summary>
    public static X509Certificate2? Verify(HeldProgram held, X509Certificate2Collection trustedRoots)
    {
        if (!OperatingSystem.IsWindows()) return null;
        held.Stream.Position = 0;
        var fileInfo = new WintrustFileInfo
        {
            cbStruct = Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = held.Path,
            hFile = held.Stream.SafeFileHandle.DangerousGetHandle()
        };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, filePtr, false);
        var data = new WintrustData
        {
            cbStruct = Marshal.SizeOf<WintrustData>(),
            dwUIChoice = 2,                 // WTD_UI_NONE
            fdwRevocationChecks = 0,        // WTD_REVOKE_NONE
            dwUnionChoice = 1,              // WTD_CHOICE_FILE
            pFile = filePtr,
            dwStateAction = StateVerify,
            dwProvFlags = 0x10 | 0x1000     // no revocation check, cache-only URL retrieval: nothing goes over the network
        };
        var action = GenericVerifyV2;
        var opened = false;
        try
        {
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0)
            {
                opened = data.hWVTStateData != IntPtr.Zero;
                return null;
            }
            opened = true;
            var provider = WTHelperProvDataFromStateData(data.hWVTStateData);
            if (provider == IntPtr.Zero) return null;
            var signerPtr = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
            if (signerPtr == IntPtr.Zero) return null;
            var signer = Marshal.PtrToStructure<ProviderSigner>(signerPtr);
            if (signer.csCertChain <= 0 || signer.csCertChain > 16 || signer.pasCertChain == IntPtr.Zero) return null;
            var stride = Marshal.SizeOf<ProviderCert>();
            var chain = new List<X509Certificate2>();
            for (var i = 0; i < signer.csCertChain; i++)
            {
                var entry = Marshal.PtrToStructure<ProviderCert>(signer.pasCertChain + i * stride);
                if (entry.pCert == IntPtr.Zero)
                {
                    foreach (var made in chain) made.Dispose();
                    return null;
                }
                chain.Add(new X509Certificate2(entry.pCert));
            }
            var trusted = false;
            try
            {
                trusted = CodeSigning.ChainTrusted(chain[0], chain.Skip(1), trustedRoots);
            }
            finally
            {
                // The certificates of the chain other than the signer are not kept; the signer only when the caller gets it.
                foreach (var other in chain.Skip(1)) other.Dispose();
                if (!trusted) chain[0].Dispose();
            }
            return trusted ? chain[0] : null;
        }
        finally
        {
            if (opened)
            {
                data.dwStateAction = StateClose;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            Marshal.DestroyStructure<WintrustFileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }
}
