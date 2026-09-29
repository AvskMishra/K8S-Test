using System.Runtime.InteropServices;

var stateLock = new object();
var shutdownRequested = false;
var patchAt = Environment.GetEnvironmentVariable("PATCH_AT");
var patchTimeZone = Environment.GetEnvironmentVariable("PATCH_TIME_ZONE");

void Log(string message) => Console.WriteLine($"{DateTimeOffset.UtcNow:O} {message}");

void RequestShutdown(string signal)
{
    lock (stateLock)
    {
        if (shutdownRequested)
        {
            return;
        }

        shutdownRequested = true;
        var schedule = !string.IsNullOrWhiteSpace(patchAt) || !string.IsNullOrWhiteSpace(patchTimeZone)
            ? $"; scheduled patch: {patchAt ?? "unspecified time"} ({patchTimeZone ?? "unspecified time zone"})"
            : string.Empty;
        Log($"Termination signal received: {signal}{schedule}");
    }
}

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    RequestShutdown("SIGINT");
};

using var sigtermRegistration = OperatingSystem.IsWindows()
    ? null
    : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        RequestShutdown("SIGTERM");
    });

Log("Workload started");

while (true)
{
    lock (stateLock)
    {
        if (shutdownRequested)
        {
            break;
        }

        Log("START");
    }

    Log("Sleeping for 30 seconds");
    await Task.Delay(TimeSpan.FromSeconds(30));
    Log("Done processing");
}

Log("Quitting the application");
Log("Done processing (final shutdown entry)");
Log("Workload stopped");
