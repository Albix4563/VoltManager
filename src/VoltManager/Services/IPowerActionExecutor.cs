using VoltManager.Models;

namespace VoltManager.Services;

public interface IPowerActionExecutor
{
    void Execute(ScheduledPowerActionType action);
}
