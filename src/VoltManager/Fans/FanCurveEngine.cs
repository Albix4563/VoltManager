namespace VoltManager.Fans;

public enum FanCurvePreset
{
    Silent,
    Balanced,
    Performance,
}

/// <summary>Pure curve math shared by preview and the runtime control loop.</summary>
public static class FanCurveEngine
{
    public static double? Interpolate(IReadOnlyList<FanCurvePoint>? curve, double temperature)
    {
        if (curve == null || curve.Count == 0 || !double.IsFinite(temperature)) return null;
        if (curve.Count == 1) return curve[0].ControlPercent;

        if (temperature <= curve[0].Temperature) return curve[0].ControlPercent;
        if (temperature >= curve[^1].Temperature) return curve[^1].ControlPercent;

        for (int i = 1; i < curve.Count; i++)
        {
            FanCurvePoint left = curve[i - 1];
            FanCurvePoint right = curve[i];
            if (temperature > right.Temperature) continue;

            double span = right.Temperature - left.Temperature;
            if (span <= 0) return null;
            double fraction = (temperature - left.Temperature) / span;
            return left.ControlPercent + ((right.ControlPercent - left.ControlPercent) * fraction);
        }

        return curve[^1].ControlPercent;
    }

    /// <summary>
    /// Cooling increases are immediate; decreases are rate-limited so a transient
    /// temperature dip cannot cause rapid fan hunting. The thermal guard runs after
    /// interpolation, so this limiter never delays an emergency increase.
    /// </summary>
    public static double ApplyDownwardRateLimit(double? previous, double target, double maxDropPerTick = 8)
    {
        if (!previous.HasValue || !double.IsFinite(previous.Value)) return target;
        if (target >= previous.Value) return target;
        return Math.Max(target, previous.Value - Math.Max(0, maxDropPerTick));
    }

    public static List<FanCurvePoint> CreatePreset(FanCurvePreset preset, double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            return new List<FanCurvePoint>();

        double At(double fraction) => Math.Round(minimum + ((maximum - minimum) * fraction), 1);
        List<(double Temp, double Fraction)> template = preset switch
        {
            FanCurvePreset.Silent => new()
            {
                (35, 0.00), (50, 0.12), (65, 0.35), (78, 0.65), (85, 0.85), (90, 1.00),
            },
            FanCurvePreset.Performance => new()
            {
                (30, 0.30), (45, 0.50), (60, 0.72), (72, 0.88), (82, 1.00), (90, 1.00),
            },
            _ => new()
            {
                (30, 0.12), (50, 0.30), (65, 0.55), (78, 0.75), (85, 0.88), (90, 1.00),
            },
        };

        return template.Select(x => new FanCurvePoint
        {
            Temperature = x.Temp,
            ControlPercent = At(x.Fraction),
        }).ToList();
    }
}
