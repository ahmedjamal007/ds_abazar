using System;
using System.Runtime.InteropServices;

namespace Dawaii.Core.Printing
{
    /// <summary>
    /// Sends raw bytes straight to a Windows printer via the spooler (winspool.drv).
    /// Used for ESC/POS thermal printers that expect raw command bytes, not GDI rendering.
    /// </summary>
    internal static class RawPrinterHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DOCINFOW
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string pDataType;
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool OpenPrinterW(string src, out IntPtr hPrinter, IntPtr pd);
        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool StartDocPrinterW(IntPtr hPrinter, int level, ref DOCINFOW di);
        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);
        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);
        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);
        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, byte[] buf, int count, out int written);

        public static void SendBytes(string printerName, byte[] bytes)
        {
            if (string.IsNullOrEmpty(printerName)) throw new ArgumentException("Printer name required.");
            IntPtr hPrinter;
            if (!OpenPrinterW(printerName, out hPrinter, IntPtr.Zero))
                throw new InvalidOperationException("تعذّر فتح الطابعة: " + printerName);
            try
            {
                var di = new DOCINFOW { pDocName = "Dawaii Receipt", pDataType = "RAW" };
                if (!StartDocPrinterW(hPrinter, 1, ref di)) throw new InvalidOperationException("تعذّر بدء الطباعة.");
                try
                {
                    if (!StartPagePrinter(hPrinter)) throw new InvalidOperationException("تعذّر بدء الصفحة.");
                    WritePrinter(hPrinter, bytes, bytes.Length, out _);
                    EndPagePrinter(hPrinter);
                }
                finally { EndDocPrinter(hPrinter); }
            }
            finally { ClosePrinter(hPrinter); }
        }
    }
}
