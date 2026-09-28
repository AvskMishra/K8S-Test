using k8s.Models;
using K8sExplorer.Display;
using K8sExplorer.Services;
using Spectre.Console;

namespace K8sExplorer.Menus;

public class PodMenu
{
    private readonly KubernetesService _k8s;
    private readonly ClusterOperationsService _ops;

    public PodMenu(KubernetesService k8s, ClusterOperationsService ops)
    {
        _k8s = k8s;
        _ops = ops;
    }

    public async Task RunAsync()
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Pods[/]")
                    .PageSize(15)
                    .AddChoices(
                        "2.1 List pods (select a namespace)",
                        "2.2 Pod details (select namespace + pod)",
                        "2.3 Pod logs (select namespace + pod + container)",
                        "2.4 Pod exec — run a command (select namespace + pod + container)",
                        "2.5 Delete / restart pod(s)",
                        "2.6 Evict pod(s) (respects PodDisruptionBudgets)",
                        "2.7 Rollout-restart owning Deployment/StatefulSet/DaemonSet",
                        "2.8 Add / remove a pod label",
                        "0. Back"));

            switch (choice)
            {
                case "2.1 List pods (select a namespace)": await ListPodsAsync(); break;
                case "2.2 Pod details (select namespace + pod)": await ShowPodDetailsAsync(); break;
                case "2.3 Pod logs (select namespace + pod + container)": await ShowPodLogsAsync(); break;
                case "2.4 Pod exec — run a command (select namespace + pod + container)": await ExecInPodAsync(); break;
                case "2.5 Delete / restart pod(s)": await DeletePodsAsync(); break;
                case "2.6 Evict pod(s) (respects PodDisruptionBudgets)": await EvictPodsAsync(); break;
                case "2.7 Rollout-restart owning Deployment/StatefulSet/DaemonSet": await RestartWorkloadsAsync(); break;
                case "2.8 Add / remove a pod label": await SetPodLabelAsync(); break;
                default: return;
            }
        }
    }

    private async Task<string?> PickNamespaceAsync()
    {
        var namespaces = await AnsiConsole.Status().StartAsync("Fetching namespaces...", _ => _k8s.GetNamespacesAsync());
        if (namespaces.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No namespaces found.[/]");
            return null;
        }

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a namespace")
                .PageSize(15)
                .AddChoices(namespaces.Select(n => n.Metadata.Name)));
    }

    private async Task<V1Pod?> PickPodAsync(string namespaceName)
    {
        var pods = await AnsiConsole.Status().StartAsync("Fetching pods...", _ => _k8s.GetPodsAsync(namespaceName));
        if (pods.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No pods found in namespace '{namespaceName}'.[/]");
            return null;
        }

        var name = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a pod")
                .PageSize(15)
                .AddChoices(pods.Select(p => p.Metadata.Name)));

        return pods.First(p => p.Metadata.Name == name);
    }

    private static string PickContainer(V1Pod pod)
    {
        var containers = pod.Spec.Containers.Select(c => c.Name).ToList();
        if (containers.Count == 1) return containers[0];

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a container")
                .AddChoices(containers));
    }

    private async Task ListPodsAsync()
    {
        var ns = await PickNamespaceAsync();
        if (ns is null) return;

        var pods = await AnsiConsole.Status().StartAsync("Fetching pods...", _ => _k8s.GetPodsAsync(ns));
        Views.Pods(pods);
    }

    private async Task ShowPodDetailsAsync()
    {
        var ns = await PickNamespaceAsync();
        if (ns is null) return;
        var pod = await PickPodAsync(ns);
        if (pod is null) return;

        var events = await _k8s.GetEventsForObjectAsync("Pod", pod.Metadata.Name, ns);
        Views.PodDetails(pod, events);
    }

    private async Task ShowPodLogsAsync()
    {
        var ns = await PickNamespaceAsync();
        if (ns is null) return;
        var pod = await PickPodAsync(ns);
        if (pod is null) return;
        var container = PickContainer(pod);

        var tailLines = AnsiConsole.Prompt(
            new TextPrompt<int>("How many lines to tail?").DefaultValue(100));

        var logs = await AnsiConsole.Status().StartAsync(
            "Fetching logs...",
            _ => _k8s.GetPodLogsAsync(ns, pod.Metadata.Name, container, tailLines));

        AnsiConsole.Write(new Rule($"[bold]Logs: {pod.Metadata.Name} / {container}[/]").LeftJustified());
        AnsiConsole.WriteLine(logs);
    }

    private async Task ExecInPodAsync()
    {
        var ns = await PickNamespaceAsync();
        if (ns is null) return;
        var pod = await PickPodAsync(ns);
        if (pod is null) return;
        var container = PickContainer(pod);

        var command = AnsiConsole.Prompt(
            new TextPrompt<string>("Command to run (e.g. 'ls -la /app' or 'printenv'):"));

        var output = await AnsiConsole.Status().StartAsync(
            "Executing...",
            _ => _k8s.ExecInPodAsync(ns, pod.Metadata.Name, container, command));

        AnsiConsole.Write(new Rule($"[bold]Output of '{command}'[/]").LeftJustified());
        AnsiConsole.WriteLine(output);
    }

    private async Task<(string Namespace, List<V1Pod> Pods)> PickPodsAsync(string title)
    {
        var ns = await PickNamespaceAsync();
        if (ns is null) return (string.Empty, new List<V1Pod>());

        var pods = await AnsiConsole.Status().StartAsync("Fetching pods...", _ => _k8s.GetPodsAsync(ns));
        if (pods.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No pods found in namespace '{Markup.Escape(ns)}'.[/]");
            return (ns, new List<V1Pod>());
        }

        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<V1Pod>()
                .Title(title)
                .NotRequired()
                .PageSize(15)
                .InstructionsText("[grey](space to toggle, enter to accept)[/]")
                .UseConverter(p => $"{Markup.Escape(p.Metadata.Name)}  [grey]{Markup.Escape(p.Status?.Phase ?? "Unknown")} | ready {Formatting.GetPodReadyCount(p)} | node {Markup.Escape(p.Spec?.NodeName ?? "-")}[/]")
                .AddChoices(pods));

        if (selected.Count == 0) AnsiConsole.MarkupLine("[grey]No pods selected.[/]");
        return (ns, selected);
    }

    private async Task DeletePodsAsync()
    {
        var (ns, pods) = await PickPodsAsync("Delete which pod(s)?");
        if (pods.Count == 0) return;

        var bare = pods.Where(p => p.Metadata.OwnerReferences?.Any(o => o.Controller == true) != true).ToList();
        if (bare.Count > 0)
            AnsiConsole.MarkupLine($"[yellow]No controller owns {string.Join(", ", bare.Select(p => Markup.Escape(p.Metadata.Name)))} — deleting removes it for good; it won't be recreated.[/]");
        else
            AnsiConsole.MarkupLine("[grey]All selected pods are controller-managed, so each will be recreated (i.e. restarted).[/]");

        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("How?")
                .AddChoices("Graceful (honour terminationGracePeriodSeconds)", "Force (grace period 0 — for stuck Terminating pods)", "Cancel"));
        if (mode == "Cancel") return;
        var force = mode.StartsWith("Force");

        if (!ActionHelpers.Confirm($"{(force ? "Force-delete" : "Delete")} {pods.Count} pod(s) in {Markup.Escape(ns)}?")) return;

        await ActionHelpers.ForEachAsync(pods, p => p.Metadata.Name, p => _ops.DeletePodAsync(ns, p.Metadata.Name, force));
    }

    private async Task EvictPodsAsync()
    {
        var (ns, pods) = await PickPodsAsync("Evict which pod(s)?");
        if (pods.Count == 0) return;

        AnsiConsole.MarkupLine("[grey]Eviction is a delete that the API server refuses if it would break a PodDisruptionBudget.[/]");
        if (!ActionHelpers.Confirm($"Evict {pods.Count} pod(s) in {Markup.Escape(ns)}?")) return;

        await ActionHelpers.ForEachAsync(pods, p => p.Metadata.Name, p => _ops.EvictPodAsync(ns, p.Metadata.Name));
    }

    private async Task RestartWorkloadsAsync()
    {
        var (_, pods) = await PickPodsAsync("Restart the workload behind which pod(s)?");
        if (pods.Count == 0) return;

        var workloads = new List<WorkloadRef>();
        foreach (var pod in pods)
        {
            var workload = await _ops.GetOwningWorkloadAsync(pod);
            if (workload is null)
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(pod.Metadata.Name)} isn't owned by a Deployment/StatefulSet/DaemonSet — skipped (use 2.5 to delete it instead)[/]");
            else if (!workloads.Contains(workload))
                workloads.Add(workload);
        }
        if (workloads.Count == 0) return;

        AnsiConsole.MarkupLine("[grey]A rollout restart replaces pods gradually using the workload's own update strategy.[/]");
        if (!ActionHelpers.Confirm($"Rollout-restart {string.Join(", ", workloads.Select(w => Markup.Escape(w.ToString())))}?", defaultValue: true)) return;

        await ActionHelpers.ForEachAsync(workloads, w => w.ToString(), _ops.RestartWorkloadAsync);
    }

    private async Task SetPodLabelAsync()
    {
        var (ns, pods) = await PickPodsAsync("Label which pod(s)?");
        if (pods.Count == 0) return;

        var remove = AnsiConsole.Prompt(new SelectionPrompt<string>().AddChoices("Add / update a label", "Remove a label")) == "Remove a label";
        AnsiConsole.MarkupLine("[yellow]Careful: changing a label a Service or ReplicaSet selects on detaches the pod from it " +
                               "(the controller will create a replacement).[/]");

        string key;
        string? value = null;
        if (remove)
        {
            var keys = pods.SelectMany(p => p.Metadata.Labels?.Keys ?? Enumerable.Empty<string>()).Distinct().Order().ToList();
            if (keys.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]The selected pod(s) have no labels.[/]");
                return;
            }
            key = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Remove which label?").PageSize(15).UseConverter(Markup.Escape).AddChoices(keys));
        }
        else
        {
            key = AnsiConsole.Prompt(
                new TextPrompt<string>("Label key:")
                    .Validate(k => !string.IsNullOrWhiteSpace(k) && !k.Any(char.IsWhiteSpace), "Key must be non-empty with no spaces"));
            value = AnsiConsole.Prompt(new TextPrompt<string>("Label value (blank allowed):").AllowEmpty());
        }

        var description = remove ? $"remove label {key}" : $"set label {key}={value}";
        if (!ActionHelpers.Confirm($"{Markup.Escape(description)} on {pods.Count} pod(s)?")) return;

        await ActionHelpers.ForEachAsync(pods, p => p.Metadata.Name, p => _ops.SetPodLabelAsync(ns, p.Metadata.Name, key, value));
    }
}
