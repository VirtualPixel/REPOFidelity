using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace REPOFidelity;

// Stopwatch-based accumulator for the mod's own hot paths. Unity strips
// user-defined ProfilerMarker spans in release builds, so the F9 probe can't
// read them back. Manual timing in a main-thread-only dictionary gives the
// probe something concrete to emit.
internal static class ModTiming
{
    internal class Acc { public long Ticks; public int Calls; }
    static readonly Dictionary<string, Acc> _accs = new();

    internal static void Reset()
    {
        foreach (var a in _accs.Values) { a.Ticks = 0; a.Calls = 0; }
    }

    internal static IEnumerable<(string name, long ticks, int calls)> Read()
    {
        foreach (var kv in _accs)
            yield return (kv.Key, kv.Value.Ticks, kv.Value.Calls);
    }

    internal struct Scope : IDisposable
    {
        readonly Acc _acc;
        readonly long _start;
        internal Scope(Acc acc) { _acc = acc; _start = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            if (_acc == null) return;
            _acc.Ticks += Stopwatch.GetTimestamp() - _start;
            _acc.Calls++;
        }
    }

    internal static Scope Begin(string name)
    {
        if (!_accs.TryGetValue(name, out var a))
        {
            a = new Acc();
            _accs[name] = a;
        }
        return new Scope(a);
    }
}
