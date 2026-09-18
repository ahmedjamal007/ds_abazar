using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dawaii.App.Printing
{
    /// <summary>
    /// Sends a raw byte buffer straight to a Windows print spooler queue using
    /// winspool.drv (pDataType = "RAW"), bypassing the driver's rendering. This
    /// is the compiled, native equivalent of electron/printing/WinSpoolRaw.ps1 —
    /// no PowerShell spawn and no runtime compile/cache, which removes the biggest
    /// source of print latency in the current app.
    /// </summary>
    public static class RawPrinterHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private class DOCINFOA
        {
            [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
        }

        [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern bool OpenPrinter(string szPrinter, out IntPtr hPrinter, IntPtr pd);

        [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOA di);

        [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        public static bool SendBytesToPrinter(string printerName, byte[] bytes)
        {
            if (string.IsNullOrEmpty(printerName)) throw new ArgumentException("Printer name is required", nameof(printerName));
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            IntPtr hPrinter;
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenPrinter failed");
            }

            try
            {
                var docInfo = new DOCINFOA
                {
                    pDocName = "Dawaii Receipt",
                    pOutputFile = null,
                    pDataType = "RAW",
                };

                if (!StartDocPrinter(hPrinter, 1, docInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter failed");
                }

                try
                {
                    if (!StartPagePrinter(hPrinter))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter failed");
                    }

                    try
                    {
                        IntPtr unmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
                        try
                        {
                            Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);
                            int written;
                            if (!WritePrinter(hPrinter, unmanagedBytes, bytes.Length, out written))
                            {
                                throw new Win32Exception(Marshal.GetLastWin32Error(), "WritePrinter failed");
                            }
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(unmanagedBytes);
                        }
                    }
                    finally
                    {
                        EndPagePrinter(hPrinter);
                    }
                }
                finally
                {
                    EndDocPrinter(hPrinter);
                }
            }
            finally
            {
                ClosePrinter(hPrinter);
            }

            return true;
        }
    }
}
