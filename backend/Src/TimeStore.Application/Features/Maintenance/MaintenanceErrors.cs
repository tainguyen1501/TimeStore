using TimeStore.Primitives;

namespace TimeStore.Application.Features.Maintenance;

public static class MaintenanceErrors
{
    public static class Codes // stable: never change a value once released
    {
        public const string SweepRunning = "MAINTENANCE_SWEEP_RUNNING";
    }

    public static readonly AppError SweepRunning =
        AppError.Conflict(Codes.SweepRunning, "An orphan-blob sweep is already running; try again when it has finished.");
}
