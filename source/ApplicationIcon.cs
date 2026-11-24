using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    public sealed class ApplicationIcon : Image
    {
        Task<BitmapSource> wanted;
        internal uint DrawingVersion { get; private set; }

        public ApplicationIcon()
        {
            Width = Height = 32;
            Loaded += (s, e) => Request(VisualTreeHelper.GetDpi(this));
            Unloaded += (s, e) => wanted = null;
            DataContextChanged += (s, e) => Request(VisualTreeHelper.GetDpi(this));
        }

        protected override void OnDpiChanged(DpiScale previous, DpiScale current)
        {
            base.OnDpiChanged(previous, current);
            Request(current);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            ++DrawingVersion;
        }

        async void Request(DpiScale scale)
        {
            var row = DataContext as AppRow;
            if (!IsLoaded || row == null) { wanted = null; return; }
            int pixels = (int)Math.Ceiling(Math.Max(Width * scale.DpiScaleX, Height * scale.DpiScaleY) * ArtworkResolution.Ratio);
            var request = row.IconAt(pixels);
            if (wanted == request) return;
            wanted = request;
            var image = await request;
            if (wanted == request && IsLoaded && ReferenceEquals(DataContext, row)) row.ShowIcon(image);
        }
    }

    internal static class ShellIcons
    {
        static readonly TaskScheduler workers = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 2).ConcurrentScheduler;
        const uint BiggerSizeOk = 1, IconOnly = 4;

        [StructLayout(LayoutKind.Sequential)]
        struct PixelSize { internal int Width, Height; }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(PixelSize size, uint flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);

        internal static Task<BitmapSource> LoadAsync(string path, int pixels)
        {
            return Task.Factory.StartNew(() => Load(path, pixels), CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, workers);
        }

        static BitmapSource Load(string path, int pixels)
        {
            if (!File.Exists(path)) return null;
            var image = FromShell(path, pixels);
            int available = image == null ? 0 : Math.Max(image.PixelWidth, image.PixelHeight);
            if (available < pixels && pixels < 256)
            {
                // A cold Shell cache can answer a 96px request with the 48px system image list.
                var jumbo = FromShell(path, 256);
                if (jumbo != null && Math.Max(jumbo.PixelWidth, jumbo.PixelHeight) > available)
                {
                    image = jumbo;
                    available = Math.Max(jumbo.PixelWidth, jumbo.PixelHeight);
                }
            }
            if (image != null)
            {
                if (available <= pixels) return image;
                double factor = (double)pixels / available;
                var sized = new TransformedBitmap(image, new ScaleTransform(factor, factor));
                sized.Freeze();
                return sized;
            }
            try
            {
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (icon == null) return null;
                    image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    image.Freeze();
                    return image;
                }
            }
            catch (Exception ex) when (ImageError(ex)) { return null; }
        }

        static bool ImageError(Exception ex)
        {
            return ex is ExternalException || ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException ||
                ex is FormatException;
        }

        static BitmapSource FromShell(string path, int pixels)
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            IShellItemImageFactory factory = null;
            IntPtr bitmap = IntPtr.Zero;
            try
            {
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) < 0 || factory == null) return null;
                var size = new PixelSize { Width = pixels, Height = pixels };
                if (factory.GetImage(size, IconOnly | BiggerSizeOk, out bitmap) < 0 || bitmap == IntPtr.Zero) return null;
                var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ImageError(ex)) { return null; }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }
    }
}
