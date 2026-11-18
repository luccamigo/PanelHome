using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PanelHost.Security.Wifi;

// Native DPAPI CurrentUser. UI is forbidden; no machine scope or fallback.
internal static class WifiDpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }

    public static unsafe byte[] Transform(byte[] input, bool protect)
    {
        fixed (byte* pointer = input)
        {
            var source = new Blob { Length = input.Length, Data = (IntPtr)pointer };
            Blob result;
            var ok = protect
                ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var output = new byte[result.Length];
                Marshal.Copy(result.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                if (result.Data != IntPtr.Zero)
                {
                    CryptographicOperations.ZeroMemory(new Span<byte>((void*)result.Data, result.Length));
                    LocalFree(result.Data);
                }
            }
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
