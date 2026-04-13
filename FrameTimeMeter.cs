using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace REPOFidelity;

// per-patch frame time measurement — only active when debug overlay or benchmark is running.
// uses a continuously-running stopwatch with snapshot pairs so overlapping patches don't clobber each other.
internal static class FrameTimeMeter
{
    internal class Meter
    {
        public readonly string Name;
        public readonly string ShortName;

        private readonly float[] _samples;
        private int _index;
        private int _count;
        private float _sum;
        public float LastUs;

        public Meter(string name, string shortName, int windowSize = 120)
        {
            Name = name;
            ShortName = shortName;
            _samples = new float[windowSize];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Record(float microseconds)
        {
            LastUs = microseconds;
            _sum -= _samples[_index];
            _samples[_index] = microseconds;
            _sum += microseconds;
            _index = (_index + 1) % _samples.Length;
            if (_count < _samples.Length) _count++;
        }

        public float AverageUs => _count > 0 ? _sum / _count : 0f;
        public float AverageMs => AverageUs / 1000f;
    }

    // runs continuously — never restarted. Begin/End capture tick snapshots.
    private static readonly Stopwatch _sw = Stopwatch.StartNew();

    internal static bool Active => Settings.DebugOverlay || OptimizerBenchmark.Running;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Begin() => Active ? _sw.ElapsedTicks : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void End(Meter meter, long startTicks)
    {
        if (startTicks == 0) return;
        float us = (float)((_sw.ElapsedTicks - startTicks) / 10.0);
        meter.Record(us);
    }

    internal static readonly Meter EnemyDirector = new("EnemyDirector Throttle", "EnemyDir");
    internal static readonly Meter RoomVolumeCheck = new("RoomVolume NonAlloc", "RoomVol");
    internal static readonly Meter SemiFuncCache = new("SemiFunc Cache", "SemiFunc");
    internal static readonly Meter PhysGrabObjectFix = new("PhysGrabObject Fix", "PhysGrab");
    internal static readonly Meter LightManagerBatch = new("LightManager Batch", "LightMgr");

    internal static readonly Meter[] All = {
        EnemyDirector, RoomVolumeCheck, SemiFuncCache,
        PhysGrabObjectFix, LightManagerBatch
    };
}
