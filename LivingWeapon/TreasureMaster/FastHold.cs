using System.Threading;

namespace LivingWeapon;

/// <summary>
/// Runs a dedicated background thread that re-stamps the armed map's tile addresses
/// at a high frequency (~8 ms) so the 0x80 mark bit is set on every render frame.
///
/// Problem addressed: on running-water maps the engine animates terrain render-flag
/// bytes at ~60 fps (~16 ms), clearing the held 0x80 between the 33 ms loop's
/// re-stamps and causing visible flicker. Re-stamping at ~8 ms (≈2× per animation
/// frame) out-paces the wipe so the bit is set whenever a frame renders.
///
/// Concurrency: TileHolder is stateless and OR-only (it never clears bits, only
/// OR-sets 0x80 on Resting bytes via RPM/WPM). The fast thread and the 33 ms loop
/// calling Hold on the same addresses is therefore safe: concurrent OR-sets to the
/// same byte are idempotent and there is no clear path in this module.
///
/// The thread is not started in the ctor so that unit tests can drive
/// <see cref="HoldOnce"/> directly without spawning OS threads.
/// </summary>
internal sealed class FastHold
{
    private readonly TileHolder _holder;
    private readonly int        _intervalMs;

    private volatile TreasureMap? _map;
    private volatile bool         _stop;
    private Thread?               _thread;

    public FastHold(TileHolder holder, int intervalMs)
    {
        _holder     = holder;
        _intervalMs = intervalMs;
    }

    /// <summary>
    /// Publishes the map the fast thread should hold.  Pass null to stop holding
    /// (the next HoldOnce / thread iteration becomes a no-op).
    /// </summary>
    public void Publish(TreasureMap? map) => _map = map;

    /// <summary>
    /// Executes one hold pass over the currently published map.
    /// Safe to call from any thread; a null map is a no-op.
    /// Exposed as internal so tests can exercise the hold logic without a thread.
    /// </summary>
    internal void HoldOnce()
    {
        var m = _map;
        if (m != null) _holder.Hold(m);
    }

    /// <summary>
    /// Starts the background thread.  Idempotent: calling Start a second time is a no-op
    /// if the thread is already running.
    /// </summary>
    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name         = "treasure-fasthold",
        };
        _thread.Start();
    }

    /// <summary>Signals the background thread to stop on its next iteration.</summary>
    public void Stop() => _stop = true;

    private void Run()
    {
        while (!_stop)
        {
            try { HoldOnce(); } catch { }
            Thread.Sleep(_intervalMs);
        }
    }
}
