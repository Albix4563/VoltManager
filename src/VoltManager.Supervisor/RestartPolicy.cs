namespace VoltManager.Supervisor;

public sealed class RestartPolicy
{
    private readonly RestartPolicyOptions _options;

    public RestartPolicy(RestartPolicyOptions options) => _options = options;

    public RestartDecision RegisterFailure(
        SupervisorState state,
        DateTimeOffset now,
        TimeSpan uptime,
        IJitterSource jitter)
    {
        bool stableReset = false;
        if (uptime >= _options.StablePeriod)
        {
            state.CrashTimesUtc.Clear();
            state.BlockedUntilUtc = null;
            stableReset = true;
        }

        DateTimeOffset cutoff = now - _options.AttemptWindow;
        state.CrashTimesUtc.RemoveAll(timestamp => timestamp < cutoff);
        state.CrashTimesUtc.Add(now);

        int failureNumber = state.CrashTimesUtc.Count;
        if (failureNumber > _options.MaximumRestarts)
        {
            DateTimeOffset blockedUntil = state.CrashTimesUtc[0] + _options.AttemptWindow;
            if (blockedUntil <= now)
                blockedUntil = now + _options.InitialDelay;

            state.BlockedUntilUtc = blockedUntil;
            return new RestartDecision(false, failureNumber, TimeSpan.Zero, stableReset, blockedUntil);
        }

        int exponent = Math.Min(failureNumber - 1, 30);
        double nominalMs = _options.InitialDelay.TotalMilliseconds * Math.Pow(2, exponent);
        nominalMs = Math.Min(nominalMs, _options.MaximumDelay.TotalMilliseconds);

        double unit = Math.Clamp(jitter.NextUnit(), 0.0, 1.0);
        double factor = 1.0 + ((unit * 2.0) - 1.0) * _options.JitterRatio;
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, nominalMs * factor));

        state.BlockedUntilUtc = null;
        return new RestartDecision(true, failureNumber, delay, stableReset, null);
    }

    public bool IsBlocked(SupervisorState state, DateTimeOffset now)
    {
        if (state.BlockedUntilUtc == null)
            return false;

        if (state.BlockedUntilUtc > now)
            return true;

        state.BlockedUntilUtc = null;
        state.CrashTimesUtc.RemoveAll(timestamp => timestamp < now - _options.AttemptWindow);
        return false;
    }
}
