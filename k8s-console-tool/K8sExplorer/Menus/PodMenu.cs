using k8s.Models;
using K8sExplorer.Display;
using K8sExplorer.Services;
using Spectre.Console;

namespace K8sExplorer.Menus;

public class PodMenu
{
    private readonly KubernetesService _k8s;

    public PodMenu(KubernetesService k8s)
    {
        _k8s = k8s;
    }

    public async Task RunAsync()
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Pods[/]")
                    .AddChoices(
                        "2.1 List pods (select a namespace)",
                        "2.2 Pod details (select namespace + pod)",
                        "2.3 Pod logs (select namespace + pod + container)",
                        "2.4 Pod exec — run a command (select namespace + pod + container)",
                        "0. Back"));

            switch (choice)
            {
                case "2.1 List pods (select a namespace)": await ListPodsAsync(); break;
                case "2.2 Pod details (select namespace + pod)": await ShowPodDetailsAsync(); break;
                case "2.3 Pod logs (select namespace + pod + container)": await ShowPodLogsAsync(); break;
                case "2.4 Pod exec — run a command (select namespace + pod + container)": await ExecInPodAsync(); break;
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

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("Ready");
        table.AddColumn("Status");
        table.AddColumn("Restarts");
        table.AddColumn("Age");
        table.AddColumn("Node");
        table.AddColumn("Pod IP");

        foreach (var pod in pods)
        {
            var phase = pod.Status?.Phase ?? "Unknown";
            table.AddRow(
                pod.Metadata.Name,
                Formatting.GetPodReadyCount(pod),
                phase == "Running" ? "[green]Running[/]" : phase,
                Formatting.GetPodRestarts(pod),
                Formatting.GetAge(pod.Metadata.CreationTimestamp),
                pod.Spec?.NodeName ?? "-",
                pod.Status?.PodIP ?? "-");
        }

        AnsiConsole.Write(table);
    }

    private async Task ShowPodDetailsAsync()
    {
        var ns = await PickNamespaceAsync();
        if (ns is null) return;
        var pod = await PickPodAsync(ns);
        if (pod is null) return;

        AnsiConsole.Write(new Rule($"[bold]{pod.Metadata.Name}[/] ({ns})").LeftJustified());
        AnsiConsole.MarkupLine($"Status:     {pod.Status?.Phase}");
        AnsiConsole.MarkupLine($"Node:       {pod.Spec?.NodeName}");
        AnsiConsole.MarkupLine($"Pod IP:     {pod.Status?.PodIP}");
        AnsiConsole.MarkupLine($"Age:        {Formatting.GetAge(pod.Metadata.CreationTimestamp)}");

        var table = new Table().Title("Containers").Border(TableBorder.Simple);
        table.AddColumn("Name");
        table.AddColumn("Image");
        table.AddColumn("Ready");
        table.AddColumn("Restarts");
        table.AddColumn("State");

        foreach (var container in pod.Spec?.Containers ?? new List<V1Container>())
        {
            var status = pod.Status?.ContainerStatuses?.FirstOrDefault(s => s.Name == container.Name);
            var state = status?.State switch
            {
                { Running: not null } => "Running",
                { Waiting: not null } s => $"Waiting ({s.Waiting.Reason})",
                { Terminated: not null } s => $"Terminated ({s.Terminated.Reason})",
                _ => "Unknown"
            };
            table.AddRow(
                container.Name,
                container.Image,
                status?.Ready.ToString() ?? "-",
                status?.RestartCount.ToString() ?? "-",
                state);
        }
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[bold]Recent events for this pod:[/]");
        var events = await _k8s.GetEventsForObjectAsync("Pod", pod.Metadata.Name, ns);
        if (events.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](none)[/]");
        }
        else
        {
            foreach (var ev in events.OrderByDescending(e => e.LastTimestamp))
                AnsiConsole.MarkupLine($"  [{(ev.Type == "Warning" ? "yellow" : "grey")}]{ev.Reason}[/]: {ev.Message}");
        }
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
}
