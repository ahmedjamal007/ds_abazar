namespace Dawaii.App.Printing
{
    /// <summary>Tunable ESC/POS emission options (mirrors the receiptline profile flags).</summary>
    public sealed class EscPosOptions
    {
        /// <summary>Send GS V cut at the end.</summary>
        public bool Cut { get; set; } = true;

        /// <summary>Use partial cut (GS V 66 n) rather than full cut (GS V 0).</summary>
        public bool PartialCut { get; set; } = true;

        /// <summary>Blank lines fed before the cut so the blade clears the last text.</summary>
        public int FeedLinesBeforeCut { get; set; } = 3;

        /// <summary>
        /// Center the raster on the paper. Applied by padding the bitmap, never by
        /// ESC a — Bixolon heads (SRP-E300) silently discard a GS v 0 raster while
        /// center alignment is active, which ejects a blank receipt. Needs
        /// <see cref="PrintWidthDots"/> to know what to center against; a raster
        /// already as wide as the head is full-bleed and needs no padding.
        /// </summary>
        public bool AlignCenter { get; set; } = true;

        /// <summary>
        /// Head width in dots to center a narrower raster against (576 = 80 mm,
        /// 384 = 58 mm). 0 means "the raster already spans the head", so no padding.
        /// </summary>
        public int PrintWidthDots { get; set; }

        /// <summary>
        /// GS v 0 band height. Splitting the image into horizontal bands keeps each
        /// raster command within the image buffer of cheaper thermal heads.
        /// </summary>
        public int BandHeight { get; set; } = 128;

        /// <summary>Luminance threshold (0..255) below which a pixel prints black.</summary>
        public int Threshold { get; set; } = 128;

        /// <summary>Apply Floyd–Steinberg dithering (for photos; off for crisp text).</summary>
        public bool Dither { get; set; } = false;
    }
}
