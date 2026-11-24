using System;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        Image themeGhost;
        TextBlock captionTitle;
        HwndSource themeSource;
        DispatcherOperation themeUpdate;
        bool darkTheme, contrastTheme;
        int themeRevision;

        void InitializeTheme()
        {
            themeGhost = Find<Image>("ThemeGhost");
            captionTitle = Find<TextBlock>("CaptionTitle");
            sort.ContextMenu.Resources.MergedDictionaries.Add(Window.Resources);
            ApplyTheme(ReadAppDarkMode(), SystemParameters.HighContrast, false);
            Window.SourceInitialized += (s, e) => {
                // PrepareWindowReveal creates the HWND before WPF attaches the window's visual tree.
                themeSource = HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
                themeSource?.AddHook(ThemeMessage);
                AttachTaskbarInput(themeSource);
                ApplyWindowBackdrop();
            };
            Window.Activated += (s, e) => {
                RefreshTheme();
                PaintCaptionTitle();
                UpdateAmbientMotion();
                UpdateLoadingMotion();
            };
            Window.Deactivated += (s, e) => {
                PaintCaptionTitle();
                UpdateAmbientMotion();
                UpdateLoadingMotion();
            };
            Window.Loaded += (s, e) => {
                PaintCaptionTitle();
                UpdateAmbientMotion();
                UpdateLoadingMotion();
            };
            surface.SizeChanged += (s, e) => { if (themeGhost.Source != null) DropThemeGhost(); };
            Window.Closed += (s, e) => {
                themeSource?.RemoveHook(ThemeMessage);
                DetachTaskbarInput(themeSource);
                themeUpdate?.Abort();
                DropThemeGhost();
            };
        }

        static bool ReadAppDarkMode()
        {
            const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            try { return Registry.GetValue(key, "AppsUseLightTheme", 1) is int light && light == 0; }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException) { return false; }
        }

        IntPtr ThemeMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int settingsChange = 0x001A, systemColorChange = 0x0015, themeChanged = 0x031A;
            MoveObservatory(message,lParam);
            if (message == settingsChange || message == systemColorChange || message == themeChanged) QueueThemeUpdate();
            if (message == 0x007E) { DisplayRefresh.Invalidate(); QueueDisplayChange(); } // WM_DISPLAYCHANGE
            return IntPtr.Zero;
        }

        void QueueThemeUpdate()
        {
            if (closed || themeUpdate != null || Window.Dispatcher.HasShutdownStarted) return;
            themeUpdate = Window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
                themeUpdate = null;
                if (!closed) RefreshTheme();
            }));
        }

        void RefreshTheme() { ApplyTheme(ReadAppDarkMode(), SystemParameters.HighContrast, true); }

        internal void ApplyTheme(bool dark, bool contrast, bool animate)
        {
            if (darkTheme == dark && contrastTheme == contrast && !contrast) return;
            var oldFrame = animate && !contrast && !contrastTheme && UiMotion.CanAnimate(surface) ? CaptureThemeFrame() : null;
            DropThemeGhost();
            darkTheme = dark;
            contrastTheme = contrast;
            if(observatoryButton!=null)observatoryButton.IsEnabled=zen || !contrast;
            Window.Resources.MergedDictionaries[0] = new ThemePalette(dark, contrast);
            sky.SetTheme(dark,contrast);
            if (themeSource != null) ApplyWindowBackdrop();
            UpdateSegmentText(false);
            PaintArtwork(artworkState ?? "StatusOff", false);
            SetColor(networkFill, Theme(network?.Ready == true ? "SkyStatusOn" : "SkyStatusOff"), 250, null, false);
            UpdateListEdges();
            PaintCaptionTitle();
            UpdateLoadingMotion();
            if (oldFrame == null) return;
            int revision = themeRevision;
            themeGhost.Source = oldFrame;
            themeGhost.Visibility = Visibility.Visible;
            themeGhost.Opacity = 1;
            UiMotion.Fade(themeGhost, 0, 250, () => { if (revision == themeRevision) DropThemeGhost(); });
        }

        BitmapSource CaptureThemeFrame()
        {
            double width = surface.ActualWidth, height = surface.ActualHeight;
            if (width < 1 || height < 1) return null;
            double scale=ArtworkResolution.PixelsPerDip(surface,width,height);
            var frame = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            frame.Render(surface);
            frame.Freeze();
            return frame;
        }

        void DropThemeGhost()
        {
            themeRevision++;
            themeGhost.Visibility = Visibility.Collapsed;
            themeGhost.Source = null;
            themeGhost.BeginAnimation(UIElement.OpacityProperty, null);
        }

        void PaintCaptionTitle() {
            UiMotion.Fade(captionTitle,Window.IsActive || contrastTheme || !Window.IsLoaded?1:.5,150);
            if(moonButtons!=null)ShowCaptionMarks(captionButtons.IsMouseOver);
        }

        void ApplyWindowBackdrop()
        {
            const int immersiveDark = 20, cornerPreference = 33, systemBackdrop = 38;
            IntPtr handle = themeSource?.Handle ?? new WindowInteropHelper(Window).Handle;
            if (handle == IntPtr.Zero) return;
            // System backdrops cover the rectangular DWM surface independently of its
            // Win32 window region. The observation aperture needs the desktop outside it.
            bool material = !zenPlaced && !contrastTheme;
            int round = zenPlaced ? 1 : 2, dark = darkTheme && !contrastTheme ? 1 : 0, mica = material ? 2 : 1;
            DwmSetWindowAttribute(handle, cornerPreference, ref round, sizeof(int));
            DwmSetWindowAttribute(handle, immersiveDark, ref dark, sizeof(int));
            bool supported = DwmSetWindowAttribute(handle, systemBackdrop, ref mica, sizeof(int)) == 0;
            var chrome = WindowChrome.GetWindowChrome(Window);
            if (chrome == null) return;
            if (supported && material)
            {
                chrome.GlassFrameThickness = new Thickness(-1);
                Window.Background = Brushes.Transparent;
            }
            else
            {
                chrome.GlassFrameThickness = zenPlaced ? new Thickness(0) : new Thickness(0, 0, 0, 1);
                Window.SetResourceReference(Control.BackgroundProperty, "WindowFill");
            }
        }
    }
}
