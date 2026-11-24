using System;

namespace AstralSplit
{
    internal struct TrayIconFrame
    {
        internal readonly int Tile, OrbitTile, OrbitQuarters;

        internal TrayIconFrame(int tile, int orbitTile = 0, int orbitQuarters = 0)
        {
            Tile = tile;
            OrbitTile = orbitTile;
            OrbitQuarters = orbitQuarters;
        }
    }

    // Time is the tray's monotonic clock, including while the main window is hidden.
    internal sealed class TrayIconMotion
    {
        enum Clip { None, Opening, Closing }
        const int OpeningMs = 600, ClosingMs = 500;
        const int ClosingOrbitPhase = 81; // Disk phase at the start of the approved closing artwork.
        Clip clip;
        bool connected;
        int offset;
        long changedAt, orbitOrigin;

        internal bool NeedsTicks { get { return connected || clip != Clip.None; } }
        int Duration { get { return clip == Clip.Opening ? OpeningMs : ClosingMs; } }
        bool Forward { get { return connected == (clip == Clip.Opening); } }

        int Position(long now)
        {
            long elapsed = now - changedAt;
            return (int)Math.Max(0, Math.Min(Duration, offset + (Forward ? elapsed : -elapsed)));
        }

        void FinishIfDue(long now)
        {
            if (clip != Clip.None && Position(now) == (Forward ? Duration : 0))
                clip = Clip.None;
        }

        internal void SetConnected(bool value, long now)
        {
            FinishIfDue(now);
            if (value == connected) return;
            // If interrupted, retrace the same artwork from its current sample.
            // A fresh transition from a resting pose gets its own choreography.
            offset = clip == Clip.None ? 0 : Position(now);
            if (clip == Clip.None) clip = value ? Clip.Opening : Clip.Closing;
            changedAt = now;
            connected = value;
            if (value && clip == Clip.Opening) orbitOrigin = now - offset;
        }

        internal TrayIconFrame Sample(long now, bool animate)
        {
            if (!animate) {
                clip = Clip.None;
                return new TrayIconFrame(connected ? 1 : 0);
            }
            FinishIfDue(now);
            int orbit = 1 + (int)((now - orbitOrigin) % TrayStatusFrames.CycleMilliseconds
                * TrayStatusFrames.FrameCount / TrayStatusFrames.CycleMilliseconds);
            if (clip == Clip.None) return new TrayIconFrame(connected ? orbit : 0);
            int position = Position(now);
            if (clip == Clip.Opening) {
                int step = position * TrayStatusFrames.OpenSteps / OpeningMs;
                if (step == 0) return new TrayIconFrame(0);
                if (step == TrayStatusFrames.OpenSteps) return new TrayIconFrame(1 + step);
                return new TrayIconFrame(TrayStatusFrames.OpenFirst + step - 1);
            }
            int closeStep = position * TrayStatusFrames.CloseSteps / ClosingMs;
            if (closeStep == 0) return new TrayIconFrame(orbit);
            if (closeStep == TrayStatusFrames.CloseSteps) return new TrayIconFrame(0);
            int tile = TrayStatusFrames.CloseFirst + closeStep - 1;
            // Match the moving disc at the moment the user stops. The blend only
            // spans the first three samples, before the two silhouettes diverge.
            if (closeStep < 4 && orbit != 1 + ClosingOrbitPhase + closeStep)
                return new TrayIconFrame(tile, orbit, 4 - closeStep);
            return new TrayIconFrame(tile);
        }
    }
}
