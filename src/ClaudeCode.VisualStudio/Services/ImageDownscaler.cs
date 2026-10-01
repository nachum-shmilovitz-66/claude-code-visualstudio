using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Shrinks an attached image so its longest edge is at most <see cref="MaxEdge"/> pixels before
    /// it is sent. The API scales larger images down to about this size anyway, so the model sees
    /// the same detail - but once a conversation holds more than about 20 images, the API rejects
    /// every request that still carries an image over 2000 px on either edge ("exceed max allowed
    /// size for many-image requests"), and the CLI resends the whole conversation each turn, so one
    /// wide screenshot pasted early breaks every later turn. Images already small enough, and
    /// anything that cannot be decoded here (WebP, say), are sent unchanged.
    /// </summary>
    public static class ImageDownscaler
    {
        /// <summary>Longest edge sent, in pixels: the size the API itself resizes to.</summary>
        public const int MaxEdge = 1568;

        public static ImageInput Fit(ImageInput img)
        {
            if (img == null || string.IsNullOrEmpty(img.Data)) return img;
            try
            {
                var bytes = Convert.FromBase64String(img.Data);
                string outType;
                var fitted = Fit(bytes, img.MediaType, MaxEdge, out outType);
                if (ReferenceEquals(fitted, bytes)) return img;
                return new ImageInput { MediaType = outType, Data = Convert.ToBase64String(fitted) };
            }
            catch (Exception ex)
            {
                Log.Write("ImageDownscaler: sent as is (" + ex.Message + ")");
                return img;
            }
        }

        /// <summary>
        /// The image scaled to fit <paramref name="maxEdge"/>, or <paramref name="data"/> itself when
        /// it already fits. JPEG stays JPEG; everything else is re-encoded as PNG (lossless, and what
        /// a screenshot is anyway).
        /// </summary>
        internal static byte[] Fit(byte[] data, string mediaType, int maxEdge, out string outMediaType)
        {
            outMediaType = mediaType;
            using (var input = new MemoryStream(data))
            using (var src = Image.FromStream(input, false, false))
            {
                int w = src.Width, h = src.Height;
                if (w <= maxEdge && h <= maxEdge) return data;

                double scale = (double)maxEdge / Math.Max(w, h);
                int nw = Math.Max(1, (int)Math.Round(w * scale));
                int nh = Math.Max(1, (int)Math.Round(h * scale));
                using (var dst = new Bitmap(nw, nh, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(dst))
                    using (var attrs = new ImageAttributes())
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        attrs.SetWrapMode(WrapMode.TileFlipXY);   // no dark fringe along the edges
                        g.DrawImage(src, new Rectangle(0, 0, nw, nh), 0, 0, w, h, GraphicsUnit.Pixel, attrs);
                    }

                    using (var output = new MemoryStream())
                    {
                        bool jpeg = string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase);
                        if (jpeg)
                        {
                            var codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
                            using (var p = new EncoderParameters(1))
                            {
                                p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                                dst.Save(output, codec, p);
                            }
                            outMediaType = "image/jpeg";
                        }
                        else
                        {
                            dst.Save(output, ImageFormat.Png);
                            outMediaType = "image/png";
                        }
                        Log.Write("ImageDownscaler: " + w + "x" + h + " -> " + nw + "x" + nh + " (" + data.Length + " -> " + output.Length + " bytes)");
                        return output.ToArray();
                    }
                }
            }
        }
    }
}
