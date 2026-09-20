using Microsoft.Web.WebView2.Wpf;
using VoltManager.Bridge;
using VoltManager.Localization;
using VoltManager.Performance;

namespace VoltManager.Services;

internal sealed record WidgetRuntimeContext(
    HardwareInfoService Hardware,
    PowerPlanService Power,
    SettingsService Settings,
    UpdateService Updates,
    StartupService AutoStart,
    MonitorService Monitor,
    ThemeService Theme,
    LocalizationService Loc,
    PowerAwakeService Awake,
    ProtectedFullscreenCoverageService FullscreenCoverage,
    PowerRequestCoordinator PowerRequests,
    Func<ResourcePressureState> ResourcePressureState,
    Action<bool> RefreshSamplingDemand,
    Func<WebView2, bool, HostBridge> CreateBridge);
