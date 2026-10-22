namespace ReTweak.Core;

/// <summary>
/// The hail mary: when the fans are already at full speed and a sensor stays at its failsafe anyway, the only
/// lever left is less heat. After <see cref="TriggerAfter"/> of that, cut the power limit to the driver's minimum;
/// after <see cref="ReleaseAfter"/> with no failsafe, give the user's limit back. The card's firmware still throttles
/// on its own near its limit; this acts earlier, at the user's failsafe temperatures. Feed it once per fan tick.
/// </summary>
public sealed class PowerEmergency
{
    public static readonly TimeSpan TriggerAfter = TimeSpan.FromSeconds(10), ReleaseAfter = TimeSpan.FromSeconds(60);
    public enum Action { None, Cut, Restore }

    private TimeSpan? _hotSince, _coolSince;
    public bool Active { get; private set; }

    public Action Update(bool failsafe, bool fansAtMax, TimeSpan now)
    {
        if (!Active)
        {
            _hotSince = failsafe && fansAtMax ? _hotSince ?? now : null;
            if (_hotSince is not TimeSpan hot || now - hot < TriggerAfter) return Action.None;
            (Active, _hotSince, _coolSince) = (true, null, null);
            return Action.Cut;
        }
        _coolSince = failsafe ? null : _coolSince ?? now;
        if (_coolSince is not TimeSpan cool || now - cool < ReleaseAfter) return Action.None;
        (Active, _coolSince) = (false, null);
        return Action.Restore;
    }
}
