using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ModernYedek.Core.Security;

internal static class WindowsDpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI yalnizca Windows uzerinde desteklenir.");
        }

        return Transform(plain, protect: true);
    }

    public static byte[] Unprotect(byte[] cipher)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI yalnizca Windows uzerinde desteklenir.");
        }

        return Transform(cipher, protect: false);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = new DataBlob();
        var outputBlob = new DataBlob();

        try
        {
            inputBlob.cbData = input.Length;
            inputBlob.pbData = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, inputBlob.pbData, input.Length);

            var ok = protect
                ? CryptProtectData(ref inputBlob, "ModernYedek", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob);

            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var output = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, output, 0, output.Length);
            return output;
        }
        finally
        {
            if (inputBlob.pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputBlob.pbData);
            }

            if (outputBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outputBlob.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
