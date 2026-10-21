namespace Tack.Sync;

/// <summary>
/// Hybrid logical clock (port of web/src/model/hlc.ts). Every field change is stamped. Stamps keep
/// increasing on one device even if the wall clock jumps backwards, and after seeing another
/// device's stamp a device never issues an older one. Ties between devices are broken by device id,
/// so every device picks the same winner.
/// </summary>
internal sealed class Hlc
{
    /// <summary>
    /// Stamps further ahead than this (a year and a day, as for files: see Stamp.IsPlausible) can
    /// only be corrupt; they aren't adopted. Anything nearer is, even if another device's clock is
    /// wrong: whatever you do next must win over what you've seen. Files and clocks use the same
    /// window, so a device whose clock once ran far ahead isn't left stamping what no one accepts.
    /// </summary>
    private const long MaxAheadMs = Stamp.MaxAheadMs;

    private readonly Func<long> _wallClock;
    private long _ms;
    private long _counter;

    public Hlc(string node, Func<long>? wallClock = null)
    {
        Node = node;
        _wallClock = wallClock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>This device's id, the last part of every stamp it issues.</summary>
    public string Node { get; private set; }

    /// <summary>
    /// Starts stamping as a different device (sync found another device using this id, e.g. a
    /// copied data folder). The clock itself carries on, so new stamps still sort after old ones.
    /// </summary>
    public void Renew(string node) => Node = node;

    /// <summary>A new stamp, later than every stamp issued or observed before.</summary>
    public string Now()
    {
        long wall = _wallClock();
        if (wall > _ms)
        {
            _ms = wall;
            _counter = 0;
        }
        else if (_counter < Stamp.MaxCounter)
        {
            _counter++;
        }
        else
        {
            _ms++;
            _counter = 0;
        }
        return Stamp.Encode(_ms, _counter, Node);
    }

    /// <summary>Moves the clock past a stamp seen elsewhere (another device, or our own saved data).</summary>
    public void Observe(string stamp)
    {
        if (Stamp.Decode(stamp) is not { } decoded) return;
        var (ms, counter, _) = decoded;
        if (ms > _wallClock() + MaxAheadMs) return;
        if (ms > _ms || (ms == _ms && counter > _counter))
        {
            _ms = (long)ms;
            // Any counter past the maximum behaves the same (the next stamp moves to the next ms).
            _counter = (long)Math.Clamp(counter, long.MinValue / 2, Stamp.MaxCounter + 1);
        }
    }
}
