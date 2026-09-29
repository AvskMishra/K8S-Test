using System.Globalization;
using System.Text.Json;
using K8sExplorer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PatchManager;

public sealed class PatchScheduleWorker(
    KubernetesService kubernetes,
    ClusterOperationsService operations,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<PatchScheduleWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CordoningWindow = TimeSpan.FromHours(2);
    private static readonly string[] LocalDateFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
    ];

    private readonly string _schedulePath = configuration["PATCH_SCHEDULE_PATH"] ?? "/etc/patch-manager/schedule.json";
    private readonly TimeSpan _pollInterval = TimeSpan.FromMinutes(ParsePositiveInt(configuration["PATCH_POLL_MINUTES"], 5));
    private readonly bool _dryRun = !bool.TryParse(configuration["PATCH_DRY_RUN"], out var dryRun) || dryRun;
    private readonly bool _runOnce = bool.TryParse(configuration["PATCH_RUN_ONCE"], out var runOnce) && runOnce;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Patch manager started. SchedulePath={SchedulePath} PollInterval={PollInterval} DryRun={DryRun}",
            _schedulePath,
            _pollInterval,
            _dryRun);

        do
        {
            await EvaluateScheduleAsync(stoppingToken);
            if (_runOnce)
            {
                lifetime.StopApplication();
                return;
            }

            await Task.Delay(_pollInterval, stoppingToken);
        } while (!stoppingToken.IsCancellationRequested);
    }

    private async Task EvaluateScheduleAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting patch schedule evaluation");

        PatchSchedule? schedule;
        try
        {
            await using var stream = File.OpenRead(_schedulePath);
            schedule = await JsonSerializer.DeserializeAsync<PatchSchedule>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(exception, "Could not read or parse patch schedule at {SchedulePath}; no changes will be made", _schedulePath);
            return;
        }

        if (schedule?.Patches is null)
        {
            logger.LogError("Patch schedule must contain a patches array; no changes will be made");
            return;
        }

        if (schedule.Patches.Any(entry => string.IsNullOrWhiteSpace(entry.NodeName)) ||
            schedule.Patches.GroupBy(entry => entry.NodeName, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            logger.LogError("Patch schedule contains a missing or duplicate nodeName; no changes will be made");
            return;
        }

        foreach (var entry in schedule.Patches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolvePatchTime(entry, out var patchUtc, out var localTime, out var zone, out var validationError))
            {
                logger.LogError("Invalid schedule entry for node {NodeName}: {ValidationError}", entry.NodeName, validationError);
                continue;
            }

            try
            {
                await EvaluateNodeAsync(entry.NodeName, patchUtc, localTime, zone, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not evaluate node {NodeName}; continuing with other schedule entries", entry.NodeName);
            }
        }
    }

    private async Task EvaluateNodeAsync(
        string nodeName,
        DateTimeOffset patchUtc,
        DateTime localPatchTime,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var timeUntilPatch = patchUtc - now;

        logger.LogInformation(
            "Evaluating node {NodeName} for patch at {LocalPatchTime} {TimeZoneId} ({PatchUtc}); starts in {TimeUntilPatch}",
            nodeName,
            localPatchTime,
            timeZone.Id,
            patchUtc,
            timeUntilPatch);

        if (timeUntilPatch <= TimeSpan.Zero)
        {
            logger.LogWarning("Patch time for node {NodeName} has passed; automatic action skipped", nodeName);
            return;
        }

        var node = await kubernetes.GetNodeAsync(nodeName);
        var readyCondition = node.Status?.Conditions?.FirstOrDefault(condition => condition.Type == "Ready");
        var ready = readyCondition?.Status == "True";
        var unschedulable = node.Spec?.Unschedulable == true;
        var taints = node.Spec?.Taints ?? [];

        logger.LogInformation(
            "Node {NodeName} state: Ready={Ready} ReadyReason={ReadyReason} Unschedulable={Unschedulable} Taints={Taints}",
            nodeName,
            ready,
            readyCondition?.Reason ?? "unknown",
            unschedulable,
            string.Join(",", taints.Select(taint => $"{taint.Key}={taint.Value}:{taint.Effect}")));

        var pods = await kubernetes.FindPodsAsync(null, nodeName);
        logger.LogInformation(
            "Node {NodeName} has {PodCount} assigned pod(s): {Pods}",
            nodeName,
            pods.Count,
            string.Join(",", pods.Select(pod => $"{pod.Metadata.NamespaceProperty}/{pod.Metadata.Name} ({pod.Status?.Phase ?? "unknown"})")));

        if (!ready)
        {
            logger.LogWarning("Node {NodeName} is not Ready; scheduling preparation is blocked", nodeName);
            return;
        }

        if (timeUntilPatch > CordoningWindow)
        {
            logger.LogInformation("Node {NodeName} is outside the two-hour cordoning window", nodeName);
            return;
        }

        if (unschedulable)
        {
            logger.LogInformation("Node {NodeName} is already cordoned; no change required", nodeName);
            return;
        }

        if (_dryRun)
        {
            logger.LogWarning("Dry run: would cordon node {NodeName}; no cluster changes made", nodeName);
            return;
        }

        await operations.SetNodeUnschedulableAsync(nodeName, true);
        logger.LogWarning("Cordoned node {NodeName}; no new pods should be scheduled there", nodeName);
    }

    private static bool TryResolvePatchTime(
        PatchEntry entry,
        out DateTimeOffset patchUtc,
        out DateTime localTime,
        out TimeZoneInfo zone,
        out string error)
    {
        patchUtc = default;
        localTime = default;
        zone = TimeZoneInfo.Utc;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(entry.PatchAt) ||
            !DateTime.TryParseExact(entry.PatchAt, LocalDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out localTime))
        {
            error = "patchAt must be an ISO local date/time without a UTC offset";
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.TimeZone))
        {
            error = "timeZone is required";
            return false;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(entry.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            error = $"unknown time zone '{entry.TimeZone}'";
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            error = $"invalid time zone '{entry.TimeZone}'";
            return false;
        }

        if (zone.IsInvalidTime(localTime))
        {
            error = $"{localTime:O} does not exist in time zone '{zone.Id}' because of a daylight-saving transition";
            return false;
        }

        if (zone.IsAmbiguousTime(localTime))
        {
            error = $"{localTime:O} is ambiguous in time zone '{zone.Id}' because of a daylight-saving transition";
            return false;
        }

        patchUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localTime, zone), TimeSpan.Zero);
        return true;
    }

    private static int ParsePositiveInt(string? value, int defaultValue) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
}

public sealed class PatchSchedule
{
    public List<PatchEntry> Patches { get; init; } = [];
}

public sealed class PatchEntry
{
    public string NodeName { get; init; } = string.Empty;
    public string PatchAt { get; init; } = string.Empty;
    public string TimeZone { get; init; } = string.Empty;
}