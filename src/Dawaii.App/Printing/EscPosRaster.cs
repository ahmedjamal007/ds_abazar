using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dawaii.App.Printing
{
    /// <summary>A 1-bit-per-pixel bitmap packed MSB-first, bit 1 = black (printed).</summary>
    public sealed class MonoBitmap
    {
        public MonoBitmap(int width, int height)
        {
            Width = width;
            Height = height;
            BytesPerRow = (width + 7) / 8;
            Rows = new byte[BytesPerRow * height];
        }

        public int Width { get; }
        public int Height { get; }
        public int BytesPerRow { get; }
        public byte[] Rows { get; }

        public void SetBlack(int x, int y)
        {
            var index = y * BytesPerRow + (x >> 3);
            Rows[index] |= (byte)(0x80 >> (x & 7));
        }
    }

    /// <summary>
    /// Converts a rendered receipt bitmap to a 1-bpp image and emits it as an
    /// ESC/POS raster (GS v 0), wrapped with init/align/feed/cut. This is the
    /// native equivalent of the current "render to image, send as raster" path,
    /// which is the reliable way to print connected Arabic on thermal heads.
    /// </summary>
    public static class EscPosRaster
    {
        // ESC/POS control bytes.
        private static readonly byte[] InitPrinter = { 0x1B, 0x40 };          // ESC @

        /// <summary>Threshold/dither a BitmapSource into a packed 1-bpp MonoBitmap.</summary>
        public static MonoBitmap ToMono(BitmapSource source, int threshold = 128, bool dither = false)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            // Normalize to straight BGRA32 so pixel reads are predictable.
            var bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            int stride = width * 4;
            var pixels = new byte[stride * height];
            bgra.CopyPixels(pixels, stride, 0);

            // Compute grayscale (composited over white for any transparency).
            var gray = new float[width * height];
            for (int y = 0; y < height; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int p = rowBase + x * 4;
                    float b = pixels[p];
                    float g = pixels[p + 1];
                    float r = pixels[p + 2];
                    float a = pixels[p + 3] / 255f;
                    // Composite over white background.
                    r = r * a + 255f * (1 - a);
                    g = g * a + 255f * (1 - a);
                    b = b * a + 255f * (1 - a);
                    gray[y * width + x] = 0.299f * r + 0.587f * g + 0.114f * b;
                }
            }

            var mono = new MonoBitmap(width, height);
            if (dither)
            {
                FloydSteinberg(gray, width, height, mono);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (gray[y * width + x] < threshold)
                        {
                            mono.SetBlack(x, y);
                        }
                    }
                }
            }
            return mono;
        }

        private static void FloydSteinberg(float[] gray, int width, int height, MonoBitmap mono)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    float old = gray[i];
                    float newVal = old < 128 ? 0 : 255;
                    if (newVal == 0) mono.SetBlack(x, y);
                    float err = old - newVal;
                    if (x + 1 < width) gray[i + 1] += err * 7f / 16f;
                    if (y + 1 < height)
                    {
                        if (x > 0) gray[i + width - 1] += err * 3f / 16f;
                        gray[i + width] += err * 5f / 16f;
                        if (x + 1 < width) gray[i + width + 1] += err * 1f / 16f;
                    }
                }
            }
        }

        /// <summary>Emits GS v 0 raster bands only (no init/cut), for round-trip testing.</summary>
        public static byte[] EncodeBands(MonoBitmap mono, int bandHeight)
        {
            if (bandHeight <= 0) bandHeight = mono.Height;
            var buffer = new List<byte>(mono.Rows.Length + 64);
            int xL = mono.BytesPerRow & 0xFF;
            int xH = (mono.BytesPerRow >> 8) & 0xFF;

            for (int top = 0; top < mono.Height; top += bandHeight)
            {
                int rows = Math.Min(bandHeight, mono.Height - top);
                // GS v 0 m xL xH yL yH  d1..dk
                buffer.Add(0x1D);
                buffer.Add(0x76);
                buffer.Add(0x30);
                buffer.Add(0x00); // m = 0 (normal)
                buffer.Add((byte)xL);
                buffer.Add((byte)xH);
                buffer.Add((byte)(rows & 0xFF));
                buffer.Add((byte)((rows >> 8) & 0xFF));
                int start = top * mono.BytesPerRow;
                int count = rows * mono.BytesPerRow;
                for (int i = 0; i < count; i++)
                {
                    buffer.Add(mono.Rows[start + i]);
                }
            }
            return buffer.ToArray();
        }

        /// <summary>
        /// Returns a copy of <paramref name="mono"/> padded with white so its content
        /// sits centered in <paramref name="targetWidth"/> dots. Returns the original
        /// when it is already at least that wide.
        /// </summary>
        public static MonoBitmap CenterPad(MonoBitmap mono, int targetWidth)
        {
            if (mono == null) throw new ArgumentNullException(nameof(mono));
            if (targetWidth <= mono.Width) return mono;

            // Shift by whole bytes so rows can be block-copied; thermal dots are
            // small enough that the <8-dot rounding is invisible on paper.
            int offsetBytes = ((targetWidth - mono.Width) / 2) / 8;
            var padded = new MonoBitmap(targetWidth, mono.Height);
            for (int y = 0; y < mono.Height; y++)
            {
                Array.Copy(
                    mono.Rows, y * mono.BytesPerRow,
                    padded.Rows, y * padded.BytesPerRow + offsetBytes,
                    mono.BytesPerRow);
            }
            return padded;
        }

        /// <summary>Builds the full print job: init, raster bands, feed, cut.</summary>
        public static byte[] BuildDocument(MonoBitmap mono, EscPosOptions options)
        {
            options = options ?? new EscPosOptions();
            var buffer = new List<byte>(mono.Rows.Length + 128);

            // Centering is done in the bitmap, not with ESC a: a Bixolon SRP-E300
            // drops the whole GS v 0 raster when center alignment is active, which
            // is why it printed blank receipts while Epson heads were unaffected.
            if (options.AlignCenter)
            {
                mono = CenterPad(mono, options.PrintWidthDots);
            }

            buffer.AddRange(InitPrinter);
            buffer.AddRange(EncodeBands(mono, options.BandHeight));

            // Feed some blank lines so the last content clears the cutter.
            for (int i = 0; i < options.FeedLinesBeforeCut; i++)
            {
                buffer.Add(0x0A); // LF
            }

            if (options.Cut)
            {
                if (options.PartialCut)
                {
                    // GS V 66 0 -> feed + partial cut
                    buffer.Add(0x1D);
                    buffer.Add(0x56);
                    buffer.Add(0x42);
                    buffer.Add(0x00);
                }
                else
                {
                    // GS V 0 -> full cut
                    buffer.Add(0x1D);
                    buffer.Add(0x56);
                    buffer.Add(0x00);
                }
            }

            return buffer.ToArray();
        }
    }
}
