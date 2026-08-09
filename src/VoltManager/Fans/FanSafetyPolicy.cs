namespace VoltManager.Fans;

public sealed record FanSafetyDecision
{
    public bool Allowed { get; init; }
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public double? EffectiveControlPercent { get; init; }
    public bool SafetyOverrideActive { get; init; }
}

/// <summary>
/// Central gate for every fan write. It never disables firmware protections and
/// refuses software control when the backend cannot expose a non-zero minimum
/// control floor. Fan-stop therefore remains unsupported until a backend can
/// explicitly describe it rather than inferring it from a 0% numeric range.
/// </summary>
public sealed class FanSafetyPolicy
{
    public const double RampStartTemperature = 80;
    public const double StrongRampTemperature = 85;
    public const double EmergencyTemperature = 90;

    public FanSafetyDecision Validate(
        FanDevice fan,
        FanConfiguration configuration,
        IReadOnlyList<FanExternalSoftwareNotice>? conflicts = null,
        double? referenceTemperature = null)
    {
        ArgumentNullException.ThrowIfNull(fan);
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.Mode == FanMode.Automatic)
        {
            if (conflicts?.Any(x => x.BlocksControl) == true || fan.ControlState == FanControlState.ExternalControllerDetected)
                return Deny("external_controller", "Another fan/hardware utility is active; VoltManager will not write to this controller.");
            return fan.Capabilities.CanRestoreDefault && !string.IsNullOrWhiteSpace(fan.ControlIdentifier)
                ? Allow(null, false)
                : Deny("restore_unavailable", "The backend cannot restore this fan to its hardware/default control mode.");
        }

        if (conflicts?.Any(x => x.BlocksControl) == true || fan.ControlState == FanControlState.ExternalControllerDetected)
            return Deny("external_controller", "Another fan/hardware utility is active; software control is blocked to avoid controller conflicts.");

        if (fan.ControlState == FanControlState.SensorUnavailable)
            return Deny("sensor_unavailable", "Temperature telemetry is unavailable or stale; software fan control is suspended.");
        if (fan.ControlState == FanControlState.SafetyBlocked)
            return Deny("safety_blocked", fan.SafetyReason ?? "Fan control is blocked by the current safety policy.");
        if (fan.ControlState is FanControlState.PermissionDenied or FanControlState.DeviceBusy or FanControlState.Disconnected or FanControlState.Unsupported or FanControlState.MonitorOnly)
            return Deny("control_state_unavailable", "The fan is not in a writable control state.");

        if (fan.Role == FanRole.Pump)
            return Deny("pump_safety_block", "Pump/AIO control remains read-only unless a dedicated backend can provide verified pump limits.");

        if (!fan.Capabilities.ControlWritable || string.IsNullOrWhiteSpace(fan.ControlIdentifier))
            return Deny("control_unavailable", "No verified writable fan control channel is available.");

        if (!fan.Capabilities.MinimumControl.HasValue || !fan.Capabilities.MaximumControl.HasValue ||
            !double.IsFinite(fan.Capabilities.MinimumControl.Value) || !double.IsFinite(fan.Capabilities.MaximumControl.Value) ||
            fan.Capabilities.MaximumControl.Value <= fan.Capabilities.MinimumControl.Value)
            return Deny("limits_unknown", "The backend did not expose a valid writable control range.");

        double min = fan.Capabilities.MinimumControl.Value;
        double max = fan.Capabilities.MaximumControl.Value;

        if (min <= 0 && !fan.Capabilities.FanStopSupported)
            return Deny("minimum_not_verified", "The backend exposes a zero minimum without explicit Fan Stop semantics, so VoltManager cannot prove a safe non-zero floor.");

        if (fan.AvailableTemperatureSensors.Count == 0)
            return Deny("sensor_unavailable", "A readable temperature sensor is required while VoltManager owns fan control.");

        double? requested = configuration.Mode switch
        {
            FanMode.Manual => configuration.FixedControlPercent,
            FanMode.Curve => referenceTemperature.HasValue
                ? FanCurveEngine.Interpolate(configuration.Curve, referenceTemperature.Value)
                : null,
            _ => null,
        };

        if (!requested.HasValue || !double.IsFinite(requested.Value))
            return Deny("invalid_configuration", "The requested configuration does not produce a valid fan control value.");

        if (requested.Value < min || requested.Value > max)
            return Deny("out_of_range", $"The requested fan control is outside the verified backend range {min:0.#}-{max:0.#}%.");

        if (configuration.Mode == FanMode.Curve)
        {
            if (string.IsNullOrWhiteSpace(configuration.SensorId) ||
                !fan.AvailableTemperatureSensors.Any(x => string.Equals(x.Id, configuration.SensorId, StringComparison.Ordinal)))
                return Deny("sensor_mapping_invalid", "The selected reference temperature sensor is not available for this fan.");

            FanSafetyDecision curveValidation = ValidateCurve(configuration.Curve, min, max);
            if (!curveValidation.Allowed) return curveValidation;
        }

        double effective = ApplyThermalGuard(requested.Value, referenceTemperature, max);
        return Allow(effective, effective > requested.Value + 0.001);
    }

    public FanSafetyDecision ValidateCurve(IReadOnlyList<FanCurvePoint>? curve, double min, double max)
    {
        if (curve == null || curve.Count is < 2 or > 32)
            return Deny("curve_points_invalid", "A fan curve must contain between 2 and 32 points.");

        double previousTemperature = double.NegativeInfinity;
        double previousControl = double.NegativeInfinity;
        foreach (FanCurvePoint? point in curve)
        {
            if (point == null || !double.IsFinite(point.Temperature) || !double.IsFinite(point.ControlPercent))
                return Deny("curve_value_invalid", "The fan curve contains a non-finite value.");
            if (point.Temperature < -20 || point.Temperature > 150)
                return Deny("curve_temperature_invalid", "A curve temperature is outside the supported validation range.");
            if (point.ControlPercent < min || point.ControlPercent > max)
                return Deny("curve_control_invalid", $"Every curve point must stay inside the verified backend range {min:0.#}-{max:0.#}%.");
            if (point.Temperature <= previousTemperature)
                return Deny("curve_order_invalid", "Fan curve temperatures must be strictly increasing.");
            if (point.ControlPercent < previousControl)
                return Deny("curve_not_monotonic", "Cooling cannot decrease as temperature rises.");
            previousTemperature = point.Temperature;
            previousControl = point.ControlPercent;
        }

        return Allow(null, false);
    }

    public static double ApplyThermalGuard(double requested, double? temperature, double maximum)
    {
        if (!temperature.HasValue || !double.IsFinite(temperature.Value)) return requested;
        double t = temperature.Value;
        if (t >= EmergencyTemperature) return maximum;

        double floor = 0;
        if (t >= StrongRampTemperature)
        {
            double fraction = Math.Clamp((t - StrongRampTemperature) /
                (EmergencyTemperature - StrongRampTemperature), 0, 1);
            floor = maximum * (0.85 + 0.15 * fraction);
        }
        else if (t >= RampStartTemperature)
        {
            double fraction = Math.Clamp((t - RampStartTemperature) /
                (StrongRampTemperature - RampStartTemperature), 0, 1);
            floor = maximum * (0.70 + 0.15 * fraction);
        }

        return Math.Min(maximum, Math.Max(requested, floor));
    }

    private static FanSafetyDecision Allow(double? effective, bool overridden) => new()
    {
        Allowed = true,
        Code = "ok",
        EffectiveControlPercent = effective,
        SafetyOverrideActive = overridden,
    };

    private static FanSafetyDecision Deny(string code, string message) => new()
    {
        Allowed = false,
        Code = code,
        Message = message,
    };
}
