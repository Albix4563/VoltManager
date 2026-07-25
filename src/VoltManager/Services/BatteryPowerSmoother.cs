using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Stabilizes noisy firmware charge/discharge rates with an EMA and enriches
/// the power-flow state with a more reliable runtime estimate plus energy used
/// in the current on-battery session (Wh) from the history ring buffer.
/// </summary>
public sealed class BatteryPowerSmoother
{
    /// <summary>EMA responsiveness: higher = tracks spikes faster (0–1).</summary>
    public const double DefaultAlpha = 0.28;

    /// <summary>How far back (seconds) to look for a history median rate.</summary>
    public const int HistoryWindowSeconds = 15 * 60;

    /// <summary>Minimum history samples required before trusting the median blend.</summary>
    public const int MinHistorySamples = 3;

    private readonly object _lock = new();
    private readonly double _alpha;
    private double? _emaAbsWatts;
    private string? _emaStatus;

    public BatteryPowerSmoother(double alpha = DefaultAlpha)
    {
        _alpha = Math.Clamp(alpha, 0.05, 0.95);
    }

    /// <summary>
    /// Apply smoothing + session energy. Thread-safe; safe to call from the host bridge.
    /// </summary>
    public BatteryPowerState Apply(
        BatteryPowerState raw,
        IReadOnlyList<BatteryHistorySample>? history = null,
        DateTime? nowUtc = null)
    {
        if (raw is not { Available: true })
            return raw;

        lock (_lock)
        {
            return Enrich(raw, history, nowUtc ?? DateTime.UtcNow, ref _emaAbsWatts, ref _emaStatus, _alpha);
        }
    }

    /// <summary>Resets EMA state (e.g. after long sleep). Pure side-effect on this instance.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _emaAbsWatts = null;
            _emaStatus = null;
        }
    }

    /// <summary>
    /// Pure enrichment logic — no I/O. Mutable EMA refs keep state across ticks.
    /// </summary>
    public static BatteryPowerState Enrich(
        BatteryPowerState raw,
        IReadOnlyList<BatteryHistorySample>? history,
        DateTime nowUtc,
        ref double? emaAbsWatts,
        ref string? emaStatus,
        double alpha = DefaultAlpha)
    {
        if (raw is not { Available: true })
            return raw;

        alpha = Math.Clamp(alpha, 0.05, 0.95);
        string statusKey = raw.Status is "charging" or "discharging" ? raw.Status : "other";

        // Reset EMA when charge/discharge phase flips so idle→load does not lag forever.
        if (emaStatus != statusKey)
        {
            emaAbsWatts = null;
            emaStatus = statusKey;
        }

        double? instantAbs = raw.PowerWatts is double w && Math.Abs(w) > 0.05
            ? Math.Abs(w)
            : null;

        if (instantAbs is double sample)
        {
            emaAbsWatts = emaAbsWatts is double prev
                ? prev + alpha * (sample - prev)
                : sample;
        }

        // Optional history median of |W| on the matching polarity within the window.
        double? historyAbs = MedianAbsWatts(history, nowUtc, statusKey);
        double? blendedAbs = BlendRates(emaAbsWatts, historyAbs);

        double? displayWatts = null;
        int? minutes = raw.MinutesRemaining;
        bool stable = false;

        if (statusKey == "discharging" && blendedAbs is > 0.05)
        {
            displayWatts = -Math.Round(blendedAbs.Value, 1);
            minutes = EstimateMinutes(raw.RemainingCapacityMwh, blendedAbs.Value) ?? minutes;
            stable = true;
        }
        else if (statusKey == "charging" && blendedAbs is > 0.05)
        {
            displayWatts = Math.Round(blendedAbs.Value, 1);
            if (raw.RemainingCapacityMwh is int rem && raw.FullChargedCapacityMwh is int full && full > rem)
                minutes = EstimateMinutes(full - rem, blendedAbs.Value) ?? minutes;
            stable = true;
        }
        else
        {
            // Idle/full: keep firmware watts (usually ~0) and clear runtime.
            displayWatts = raw.PowerWatts;
            if (statusKey == "other")
                minutes = null;
        }

        double? sessionWh = ComputeSessionWh(history, nowUtc);

        return raw with
        {
            InstantPowerWatts = raw.PowerWatts,
            PowerWatts = displayWatts,
            MinutesRemaining = minutes,
            TimeKind = minutes is > 0
                ? (statusKey == "charging" ? "toFull" : statusKey == "discharging" ? "toEmpty" : raw.TimeKind)
                : (statusKey is "charging" or "discharging" ? raw.TimeKind : "none"),
            EstimateStable = stable,
            SessionWh = sessionWh,
        };
    }

    /// <summary>
    /// Trapezoidal integral of discharge power over the current unplugged streak.
    /// Returns null when there is no meaningful on-battery history.
    /// </summary>
    public static double? ComputeSessionWh(IReadOnlyList<BatteryHistorySample>? samples, DateTime nowUtc)
    {
        if (samples == null || samples.Count < 2)
            return null;

        long nowSec = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        // Walk backward until AC reappears — that marks the start of this DC session.
        int end = samples.Count - 1;
        int start = end;
        while (start > 0 && !samples[start].Ac)
            start--;
        if (samples[start].Ac)
            start++; // first sample after last AC

        if (start > end)
            return null;

        double wh = 0;
        for (int i = start; i < end; i++)
        {
            var a = samples[i];
            var b = samples[i + 1];
            if (a.Ac || b.Ac) continue;
            long dt = b.T - a.T;
            if (dt <= 0 || dt > HistoryWindowSeconds * 2) continue;

            // Only count discharge (negative W). Charging while "unplugged" is rare/noise.
            double wa = a.W is < 0 ? -a.W.Value : 0;
            double wb = b.W is < 0 ? -b.W.Value : 0;
            if (wa <= 0 && wb <= 0) continue;

            double avgW = (wa + wb) / 2.0;
            wh += avgW * (dt / 3600.0);
        }

        // Tail: last sample → now, if still on battery and recent.
        var last = samples[end];
        if (!last.Ac && last.W is < 0)
        {
            long dtTail = nowSec - last.T;
            if (dtTail > 0 && dtTail <= 120)
                wh += (-last.W.Value) * (dtTail / 3600.0);
        }

        if (wh < 0.05)
            return null;

        return Math.Round(wh, 2);
    }

    private static double? MedianAbsWatts(
        IReadOnlyList<BatteryHistorySample>? samples,
        DateTime nowUtc,
        string statusKey)
    {
        if (samples == null || samples.Count == 0 || statusKey is not ("charging" or "discharging"))
            return null;

        long nowSec = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        long cutoff = nowSec - HistoryWindowSeconds;
        var values = new List<double>(16);

        for (int i = samples.Count - 1; i >= 0; i--)
        {
            var s = samples[i];
            if (s.T < cutoff) break;
            if (s.W is not double w) continue;

            if (statusKey == "discharging")
            {
                if (s.Ac || w >= -0.05) continue;
                values.Add(-w);
            }
            else
            {
                // charging: prefer AC samples with positive power
                if (w <= 0.05) continue;
                values.Add(w);
            }
        }

        if (values.Count < MinHistorySamples)
            return null;

        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[mid - 1] + values[mid]) / 2.0
            : values[mid];
    }

    private static double? BlendRates(double? ema, double? historyMedian)
    {
        if (ema is double e && historyMedian is double h)
            return 0.55 * e + 0.45 * h; // slight preference for live EMA
        return ema ?? historyMedian;
    }

    private static int? EstimateMinutes(int? capacityMwh, double absWatts)
    {
        if (capacityMwh is not > 0 || absWatts <= 0.05)
            return null;
        // capacity mWh / (W * 1000) hours * 60 = minutes
        double minutes = capacityMwh.Value / (absWatts * 1000.0) * 60.0;
        if (double.IsNaN(minutes) || double.IsInfinity(minutes) || minutes < 0)
            return null;
        return (int)Math.Clamp(Math.Round(minutes), 0, 7 * 24 * 60); // cap at 7 days
    }
}
