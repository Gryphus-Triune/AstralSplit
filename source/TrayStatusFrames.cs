using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace AstralSplit
{
    // Native-size tiles: idle star, orbit, then the interiors of the two transitions.
    // Their endpoints share the existing star and disc tiles.
    internal sealed class TrayStatusFrames : IDisposable
    {
        internal const int FrameCount = 288, CycleMilliseconds = 4800;
        internal const int OpenSteps = 36, CloseSteps = 30;
        internal const int OpenFirst = FrameCount + 1, CloseFirst = OpenFirst + OpenSteps - 1;
        const int TileCount = CloseFirst + CloseSteps - 1;
        const int Columns = 17;
        static readonly int[] Sizes = { 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 80 };
        Bitmap sheet;
        Stream source;
        int tileSize;
        readonly byte[][] encoded = new byte[TileCount][];
        int cachedPixels;
        bool cachedLight;

        void Load(int pixels)
        {
            int n = Array.Find(Sizes, candidate => candidate >= pixels);
            if (n == 0) n = Sizes[Sizes.Length - 1];
            if (n == tileSize && sheet != null) return;
            var next = typeof(TrayStatusFrames).Assembly.GetManifestResourceStream("AstralSplit.StellarArt.notification-" + n + ".png");
            if (next == null) throw new InvalidDataException("未找到托盘图标资源。");
            Bitmap bitmap;
            try { bitmap = new Bitmap(next); }
            catch { next.Dispose(); throw; }
            int rows = (TileCount + Columns - 1) / Columns;
            if (bitmap.Width != 2 * Columns * n || bitmap.Height != rows * n) {
                bitmap.Dispose(); next.Dispose();
                throw new InvalidDataException("托盘图标资源尺寸不正确。");
            }
            Dispose();
            source = next; sheet = bitmap; tileSize = n;
        }

        internal Icon Create(int tile, int pixels, int glint, bool lightTaskbar, int orbitTile = 0, int orbitQuarters = 0)
        {
            if (cachedPixels != pixels || cachedLight != lightTaskbar) {
                Load(pixels);
                Array.Clear(encoded, 0, encoded.Length);
                cachedPixels = pixels; cachedLight = lightTaskbar;
            }
            bool cacheable = glint == 0 && orbitQuarters == 0;
            byte[] data = cacheable ? encoded[tile] : null;
            if (data == null) {
                data = EncodeTile(tile, pixels, glint, lightTaskbar, orbitTile, orbitQuarters);
                if (cacheable) encoded[tile] = data;
            }
            // Icon owns its native handle. Only the currently displayed HICON is retained.
            using (var stream = new MemoryStream(data, false)) return new Icon(stream, pixels, pixels);
        }

        Bitmap Cut(int tile, bool lightTaskbar)
        {
            int column = tile % Columns + (lightTaskbar ? Columns : 0);
            var region = new Rectangle(column * tileSize, tile / Columns * tileSize, tileSize, tileSize);
            return sheet.Clone(region, PixelFormat.Format32bppArgb);
        }

        static void MixOrbit(Bitmap cut, Bitmap orbit, int quarters)
        {
            // Interpolate premultiplied color and coverage together on transparent tray edges.
            var bounds = new Rectangle(Point.Empty, cut.Size);
            var dst = cut.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try {
                var src = orbit.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try {
                    var row = new byte[cut.Width * 4];
                    var previous = new byte[row.Length];
                    for (int y = 0; y < cut.Height; y++) {
                        var target = IntPtr.Add(dst.Scan0, y * dst.Stride);
                        Marshal.Copy(target, row, 0, row.Length);
                        Marshal.Copy(IntPtr.Add(src.Scan0, y * src.Stride), previous, 0, row.Length);
                        for (int x = 0; x < row.Length; x++)
                            row[x] = (byte)(((4 - quarters) * row[x] + quarters * previous[x] + 2) / 4);
                        Marshal.Copy(row, 0, target, row.Length);
                    }
                } finally { orbit.UnlockBits(src); }
            } finally { cut.UnlockBits(dst); }
        }

        byte[] EncodeTile(int tile, int pixels, int glint, bool lightTaskbar, int orbitTile, int orbitQuarters)
        {
            using (var cut = Cut(tile, lightTaskbar)) {
                if (orbitQuarters != 0) {
                    using (var orbit = Cut(orbitTile, lightTaskbar)) MixOrbit(cut, orbit, orbitQuarters);
                }
                if (pixels == tileSize && glint == 0) return EncodeIcon(cut);
                using (var scaled = new Bitmap(pixels, pixels, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(scaled))
                using (var attributes = new ImageAttributes()) {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    if (glint > 0) {
                        float gain = 1 + glint * .025f;
                        attributes.SetColorMatrix(new ColorMatrix { Matrix00 = gain, Matrix11 = gain, Matrix22 = gain });
                    }
                    g.DrawImage(cut, new Rectangle(0, 0, pixels, pixels), 0, 0, tileSize, tileSize, GraphicsUnit.Pixel, attributes);
                    return EncodeIcon(scaled);
                }
            }
        }

        static byte[] EncodeIcon(Bitmap image)
        {
            using (var png = new MemoryStream())
            using (var data = new MemoryStream())
            using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true)) {
                image.Save(png, ImageFormat.Png);
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
                writer.Write((byte)image.Width); writer.Write((byte)image.Height);
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write((int)png.Length); writer.Write(22);
                png.WriteTo(data);
                return data.ToArray();
            }
        }

        public void Dispose()
        {
            sheet?.Dispose(); sheet = null;
            source?.Dispose(); source = null;
            cachedPixels = 0;
            Array.Clear(encoded, 0, encoded.Length);
        }
    }
}
