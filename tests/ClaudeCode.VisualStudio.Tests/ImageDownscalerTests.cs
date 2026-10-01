using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class ImageDownscalerTests
    {
        private static byte[] Make(int w, int h, ImageFormat format)
        {
            using (var bmp = new Bitmap(w, h))
            using (var g = Graphics.FromImage(bmp))
            using (var ms = new MemoryStream())
            {
                g.Clear(Color.White);
                g.FillRectangle(Brushes.Red, 0, 0, w / 2, h / 2);
                bmp.Save(ms, format);
                return ms.ToArray();
            }
        }

        private static Size SizeOf(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var img = Image.FromStream(ms)) return img.Size;
        }

        [TestMethod]
        public void SmallImage_IsSentUntouched()
        {
            var png = Make(1424, 198, ImageFormat.Png);   // the status-bar screenshot that "failed"
            string type;
            var got = ImageDownscaler.Fit(png, "image/png", ImageDownscaler.MaxEdge, out type);
            Assert.AreSame(png, got);
            Assert.AreEqual("image/png", type);

            var input = new ImageInput { MediaType = "image/png", Data = Convert.ToBase64String(png) };
            Assert.AreSame(input, ImageDownscaler.Fit(input));
        }

        [TestMethod]
        public void WideScreenshot_IsFittedUnderTheManyImageLimit_KeepingItsShape()
        {
            // The 2000 x 734 screenshot that tripped the API's many-image rule in a real session.
            string type;
            var got = ImageDownscaler.Fit(Make(2000, 734, ImageFormat.Png), "image/png", ImageDownscaler.MaxEdge, out type);
            var size = SizeOf(got);
            Assert.AreEqual(1568, size.Width);
            Assert.AreEqual(575, size.Height);   // 734 * 1568 / 2000, rounded
            Assert.AreEqual("image/png", type);
        }

        [TestMethod]
        public void TallImage_FitsOnItsLongEdge()
        {
            string type;
            var size = SizeOf(ImageDownscaler.Fit(Make(900, 3000, ImageFormat.Png), "image/png", 1568, out type));
            Assert.AreEqual(1568, size.Height);
            Assert.AreEqual(470, size.Width);
        }

        [TestMethod]
        public void Jpeg_StaysJpeg_OtherFormatsBecomePng()
        {
            string type;
            var jpg = ImageDownscaler.Fit(Make(2400, 1200, ImageFormat.Jpeg), "image/jpeg", 1568, out type);
            Assert.AreEqual("image/jpeg", type);
            Assert.AreEqual(0xFF, jpg[0]); Assert.AreEqual(0xD8, jpg[1]);

            var bmp = ImageDownscaler.Fit(Make(2400, 1200, ImageFormat.Bmp), "image/bmp", 1568, out type);
            Assert.AreEqual("image/png", type);
            Assert.AreEqual((byte)'P', bmp[1]);
        }

        [TestMethod]
        public void UndecodableData_IsSentAsIs()
        {
            var junk = new ImageInput { MediaType = "image/webp", Data = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }) };
            Assert.AreSame(junk, ImageDownscaler.Fit(junk));
            var notBase64 = new ImageInput { MediaType = "image/png", Data = "%%%" };
            Assert.AreSame(notBase64, ImageDownscaler.Fit(notBase64));
            Assert.IsNull(ImageDownscaler.Fit(null));
        }
    }
}
