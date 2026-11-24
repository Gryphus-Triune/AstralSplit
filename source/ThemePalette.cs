using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    public sealed class ThemePalette : ResourceDictionary
    {
        static readonly BitmapCache foregroundCache = (BitmapCache)new BitmapCache(ArtworkResolution.Ratio).GetAsFrozen();
        // One table keeps the light and dark appearances in sync.
        static readonly string[,] palette = {
            { "Ink", "#1D1D1F", "#F5F5F7" },
            { "Subtle", "#444B58", "#C1C4CE" },
            { "Muted", "#464D59", "#9EA5B2" },
            { "MetadataHalo", "#00000000", "#DC0A0E18" },
            { "ListValueInk", "#444B58", "#D2D8E4" },
            { "Accent", "#007AFF", "#0A84FF" },
            { "AccentText", "#004396", "#CFEAFF" },
            { "OnAccent", "#FFFFFF", "#FFFFFF" },
            { "MenuAccent", "#0065D5", "#0767C6" },
            { "AccentWash", "#1F007AFF", "#1F0A84FF" },
            { "FocusStroke", "#80007AFF", "#AA0A84FF" },
            { "Surface", "#FFFFFF", "#2C2C2E" },
            { "GlassTool", "#67F4F9FF", "#325A7198" },
            { "GlassKnob", "#C3FFFFFF", "#C7F5FAFF" },
            { "GlassSeam", "#505F7598", "#475F779B" },
            { "GlassWell", "#6595A9C3", "#55788DAA" },
            { "GlassTab", "#D8FFFFFF", "#AA354663" },
            { "GlassField", "#28A3B6D2", "#1B8595A9" },
            { "GlassMenu", "#F3F8FF", "#35425A" },
            { "MenuFilm", "#BCFFFFFF", "#B72C374B" },
            { "Recess", "#F2F3F6", "#38383C" },
            { "SegmentBed", "#EDEEF2", "#38383C" },
            { "SegmentFace", "#FFFFFF", "#505054" },
            { "Line", "#14000000", "#33EBEBF5" },
            { "Divider", "#12414652", "#22EBEBF5" },
            { "ButtonInk", "#28414652", "#50EBEBF5" },
            { "RowHover", "#0A000000", "#12EBEBF5" },
            { "SwitchOn", "#67CD67", "#67CD67" },
            { "SwitchOff", "#59595D", "#59595D" },
            { "SwitchOutline", "#0F000000", "#0CEBEBF5" },
            { "CardHighlight", "#FFFFFF", "#22EBEBF5" },
            { "CaptionInactive", "#D5D5DA", "#55555C" },
            { "CaptionInactiveEdge", "#BFC1C9", "#66666D" },
            { "CaptionRed", "#FF5F57", "#FF5F57" },
            { "CaptionRedEdge", "#E0443E", "#E0443E" },
            { "CaptionYellow", "#FEBC2E", "#FEBC2E" },
            { "CaptionYellowEdge", "#DEA123", "#DEA123" },
            { "CaptionGreen", "#28C840", "#28C840" },
            { "CaptionGreenEdge", "#1BA832", "#1BA832" },
            { "CaptionInk", "#3F2221", "#3F2221" },
            { "CaptionPressed", "#21000000", "#21000000" },
            { "StatusOn", "#208651", "#30D158" },
            { "StatusOff", "#64728B", "#A4AEC2" },
            { "StatusBusy", "#007AFF", "#0A84FF" },
            { "StatusWarning", "#BB7C21", "#FF9F0A" },
            { "SkyText", "#F5F5F7", "#F5F5F7" },
            { "SkyBodyText", "#C1C4CE", "#C1C4CE" },
            { "SkySecondary", "#BAFFFFFF", "#BAFFFFFF" },
            { "SkyScrim", "#C3090D22", "#C3090D22" },
            { "SkyGlass", "#14FFFFFF", "#14FFFFFF" },
            { "SkyEdge", "#29FFFFFF", "#29FFFFFF" },
            { "SkyHighlight", "#3DFFFFFF", "#3DFFFFFF" },
            { "SkyTrack", "#59595D", "#59595D" },
            { "SkyStatusOff", "#B9C8E4", "#B9C8E4" },
            { "SkyStatusOn", "#79E8CD", "#79E8CD" },
            { "SkyStatusBusy", "#86B9FF", "#86B9FF" },
            { "SkyStatusWarning", "#FFD098", "#FFD098" },
            { "NoticeFill", "#FFF3DD", "#403322" },
            { "NoticeInk", "#6C450E", "#FFD18A" },
            { "RestoreFill", "#FFE0A2", "#62471F" },
            { "ScrollGrip", "#9AA9BB", "#A5B4C8" },
            { "ScrollGripHover", "#B6C4D4", "#C4D0DE" },
            { "ScrollGripPressed", "#E6EDF6", "#E4EEFC" },
            { "SkeletonFill", "#EEF0F4", "#3B3B3F" },
            { "SkeletonGlint", "#BFFFFFFF", "#507B7B87" },
        };

        public ThemePalette() : this(false, false) { }

        internal ThemePalette(bool dark, bool contrast)
        {
            Add("ForegroundCache", foregroundCache);
            for (int i = 0; i < palette.GetLength(0); i++)
                Add(palette[i, 0], Brush((Color)ColorConverter.ConvertFromString(palette[i, dark ? 2 : 1])));

            if (contrast)
            {
                foreach (string key in new[] { "Ink", "Subtle", "Muted", "ListValueInk", "NoticeInk", "StatusOff", "CaptionInk", "CardHighlight", "Divider", "Line", "SwitchOutline" })
                    this[key] = SystemColors.WindowTextBrush;
                foreach (string key in new[] { "Surface", "Recess", "SegmentBed", "SegmentFace", "NoticeFill", "RestoreFill", "SwitchOff", "CaptionInactive" })
                    this[key] = SystemColors.WindowBrush;
                foreach (string key in new[] { "Accent", "AccentText", "FocusStroke", "MenuAccent", "StatusOn", "StatusBusy", "StatusWarning", "ScrollGrip", "ScrollGripHover", "ScrollGripPressed" })
                    this[key] = SystemColors.HighlightBrush;
                this["OnAccent"] = SystemColors.HighlightTextBrush;
                foreach (string key in new[] { "SkyText", "SkyBodyText", "SkySecondary", "SkyEdge", "SkyStatusOff" })
                    this[key] = SystemColors.WindowTextBrush;
                foreach (string key in new[] { "SkyStatusOn", "SkyStatusBusy", "SkyStatusWarning" })
                    this[key] = SystemColors.HighlightBrush;
                this["SkyGlass"] = this["SkyTrack"] = SystemColors.WindowBrush;
                this["SkyScrim"] = this["SkyHighlight"] = Brushes.Transparent;
                this["AccentWash"] = this["RowHover"] = this["ButtonInk"] = Brushes.Transparent;
                this["GlassTool"] = this["GlassWell"] = this["GlassField"] = this["GlassMenu"] = SystemColors.WindowBrush;
                this["MenuFilm"] = SystemColors.WindowBrush;
                this["GlassTab"] = SystemColors.HighlightBrush;
                this["GlassSeam"] = SystemColors.ControlDarkBrush;
                this["GlassKnob"] = SystemColors.WindowTextBrush;
                this["SwitchOn"] = SystemColors.HighlightBrush;
            }
            else
            {
                var rest = ((SolidColorBrush)this["ScrollGrip"]).Color;
                var hover = ((SolidColorBrush)this["ScrollGripHover"]).Color;
                var light = ((SolidColorBrush)this["ScrollGripPressed"]).Color;
                this["ScrollGrip"] = Ramp(hover, rest);
                this["ScrollGripHover"] = Ramp(light, hover);
            }

            Add("SelectionInk", contrast ? SystemColors.HighlightTextBrush : this["Ink"]);
            Color from = contrast ? SystemColors.WindowColor : dark ? Color.FromRgb(28,28,30) : Color.FromRgb(246,247,250);
            Color to = contrast ? from : dark ? Color.FromRgb(25,25,28) : Color.FromRgb(236,238,243);
            Add("WindowFill", Ramp(from, to));
            var surface = ((SolidColorBrush)this["Surface"]).Color;
            var invisible = Color.FromArgb(0, surface.R, surface.G, surface.B);
            Add("ListFadeTop", Ramp(surface, invisible));
            Add("ListFadeBottom", Ramp(invisible, surface));
            Add("DecorationOpacity", contrast ? 0.0 : 1.0);
            Add("GlassEnabled", !contrast);
            // Ink under the small glyphs provides local contrast on bright dust.
            Add("MetadataHaloVisibility",dark && !contrast ? Visibility.Visible : Visibility.Collapsed);
            Add("IdleAppIconOpacity", contrast ? 1.0 : .84);
            var cardCoat = new DrawingGroup();
            using (var dc = cardCoat.Open()) {
                var area = new Rect(0, 0, 1, 1);
                dc.DrawRectangle((Brush)this["SkyScrim"], null, area);
                dc.DrawRectangle((Brush)this["SkyGlass"], null, area);
            }
            var cardFilm = new DrawingBrush(cardCoat);
            cardFilm.Freeze();
            Add("StatusCardFilm", cardFilm);
            // Only white light is added to the frosted layer, with extra diffusion at the canopy join.
            var film = new LinearGradientBrush { StartPoint=new Point(0,0), EndPoint=new Point(0,1) };
            film.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)24 : (byte)250,255,255,255),0));
            film.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)23 : (byte)245,255,255,255),0.032));
            film.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)21 : (byte)122,255,255,255),0.095));
            film.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)24 : (byte)76,255,255,255),1));
            film.Freeze();
            Add("ListFilm", contrast ? this["Surface"] : (object)film);
        }

        static SolidColorBrush Brush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        static LinearGradientBrush Ramp(Color from, Color to)
        {
            var brush = new LinearGradientBrush(from, to, new Point(0,0), new Point(0,1));
            brush.Freeze();
            return brush;
        }
    }
}
