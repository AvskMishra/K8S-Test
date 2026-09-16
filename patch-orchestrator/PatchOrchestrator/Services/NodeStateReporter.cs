using System.Text.Json;
using k8s;
using PatchOrchestrator.Models;

namespace PatchOrchestrator.Services;

// Answers "what's the current state of every server in this cluster right now" by combining:
//   - live facts read straight from the Kubernetes API (Ready?, cordoned?, taints, pod count), and
//   - whichever patch workflow (if any) is currently active for that node, from patch-schedule.json.
// Written to data/node-state.json on every tick, so there's always an up-to-date snapshot on
// disk - no need to query the cluster by hand to answer "is server03 mid-drain right now?".
public class NodeStateReporter
{
    private readonly IKubernetes _client;
    private readonly ILogger<NodeStateReporter> _logger;
    private readonly string _stateFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public NodeStateReporter(IKubernetes client, ILogger<NodeStateReporter> logger, IConfiguration configuration)
    {
        _client = client;
        _logger = logger;
        _stateFilePath = configuration["PatchOrchestrator:NodeStateFilePath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "node-state.json");
    }

    public async Task WriteSnapshotAsync(List<PatchSchedule> schedules)
    {
        var nodes = await _client.CoreV1.ListNodeAsync();
        var allPods = await _client.CoreV1.ListPodForAllNamespacesAsync();

        var snapshots = nodes.Items.Select(node =>
        {
            var nodeName = node.Metadata.Name;

            // A node only has an "active" patch state while it's actually mid-workflow -
            // Completed schedules are history, not current state.
            var activeSchedule = schedules.FirstOrDefault(s =>
                s.ServerName == nodeName && s.State != PatchScheduleState.Completed);

            return new NodeStateSnapshot
            {
                NodeName = nodeName,
                Ready = NodePatchOperations.IsReady(node),
                Unschedulable = node.Spec.Unschedulable ?? false,
                TaintCount = node.Spec.Taints?.Count ?? 0,
                RunningPodCount = allPods.Items.Count(p => p.Spec.NodeName == nodeName && p.Status?.Phase == "Running"),
                ActivePatchState = activeSchedule?.State.ToString(),
                LastCheckedUtc = DateTime.UtcNow
            };
        }).ToList();

        var directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(_stateFilePath, JsonSerializer.Serialize(snapshots, JsonOptions));

        foreach (var snapshot in snapshots)
        {
            _logger.LogInformation(
                "Node {Node}: Ready={Ready} Unschedulable={Unschedulable} Pods={Pods} PatchState={PatchState}",
                snapshot.NodeName, snapshot.Ready, snapshot.Unschedulable, snapshot.RunningPodCount, snapshot.ActivePatchState ?? "none");
        }
    }
}
