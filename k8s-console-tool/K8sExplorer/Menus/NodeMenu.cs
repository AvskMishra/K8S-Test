using k8s.Models;
using K8sExplorer.Display;
using K8sExplorer.Services;
using Spectre.Console;

namespace K8sExplorer.Menus;

public class NodeMenu
{
    private readonly KubernetesService _k8s;

    public NodeMenu(KubernetesService k8s)
    {
        _k8s = k8s;
    }

    public async Task RunAsync()
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Nodes[/]")
                    .AddChoices("1.1 List all nodes", "1.2 Node details (select a node)", "1.3 Node events (select a node)", "0. Back"));

            switch (choice)
            {
                case "1.1 List all nodes": await ListNodesAsync(); break;
                case "1.2 Node details (select a node)": await ShowNodeDetailsAsync(); break;
                case "1.3 Node events (select a node)": await ShowNodeEventsAsync(); break;
                default: return;
            }
        }
    }

    private async Task ListNodesAsync()
    {
        var nodes = await AnsiConsole.Status().StartAsync("Fetching nodes...", _ => _k8s.GetNodesAsync());

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("Status");
        table.AddColumn("Roles");
        table.AddColumn("Age");
        table.AddColumn("Version");
        table.AddColumn("Internal IP");
        table.AddColumn("Container Runtime");

        foreach (var node in nodes)
        {
            var internalIp = node.Status?.Addresses?.FirstOrDefault(a => a.Type == "InternalIP")?.Address ?? "-";
            table.AddRow(
                node.Metadata.Name,
                Formatting.GetNodeStatus(node) == "Ready" ? "[green]Ready[/]" : "[red]NotReady[/]",
                Formatting.GetNodeRoles(node),
                Formatting.GetAge(node.Metadata.CreationTimestamp),
                node.Status?.NodeInfo?.KubeletVersion ?? "-",
                internalIp,
                node.Status?.NodeInfo?.ContainerRuntimeVersion ?? "-");
        }

        AnsiConsole.Write(table);
    }

    private async Task<V1Node?> PickNodeAsync()
    {
        var nodes = await AnsiConsole.Status().StartAsync("Fetching nodes...", _ => _k8s.GetNodesAsync());
        if (nodes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No nodes found.[/]");
            return null;
        }

        var name = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a node")
                .AddChoices(nodes.Select(n => n.Metadata.Name)));

        return nodes.First(n => n.Metadata.Name == name);
    }

    private async Task ShowNodeDetailsAsync()
    {
        var node = await PickNodeAsync();
        if (node is null) return;

        AnsiConsole.Write(new Rule($"[bold]{node.Metadata.Name}[/]").LeftJustified());

        AnsiConsole.MarkupLine($"Status:        {(Formatting.GetNodeStatus(node) == "Ready" ? "[green]Ready[/]" : "[red]NotReady[/]")}");
        AnsiConsole.MarkupLine($"Roles:         {Formatting.GetNodeRoles(node)}");
        AnsiConsole.MarkupLine($"Age:           {Formatting.GetAge(node.Metadata.CreationTimestamp)}");
        AnsiConsole.MarkupLine($"OS Image:      {node.Status?.NodeInfo?.OsImage}");
        AnsiConsole.MarkupLine($"Kernel:        {node.Status?.NodeInfo?.KernelVersion}");
        AnsiConsole.MarkupLine($"Kubelet:       {node.Status?.NodeInfo?.KubeletVersion}");
        AnsiConsole.MarkupLine($"Runtime:       {node.Status?.NodeInfo?.ContainerRuntimeVersion}");
        AnsiConsole.MarkupLine($"Architecture:  {node.Status?.NodeInfo?.Architecture}");

        var addrTable = new Table().Title("Addresses").Border(TableBorder.Simple);
        addrTable.AddColumn("Type");
        addrTable.AddColumn("Address");
        foreach (var addr in node.Status?.Addresses ?? new List<V1NodeAddress>())
            addrTable.AddRow(addr.Type, addr.Address);
        AnsiConsole.Write(addrTable);

        var capTable = new Table().Title("Capacity / Allocatable").Border(TableBorder.Simple);
        capTable.AddColumn("Resource");
        capTable.AddColumn("Capacity");
        capTable.AddColumn("Allocatable");
        var resourceNames = (node.Status?.Capacity?.Keys ?? Enumerable.Empty<string>())
            .Union(node.Status?.Allocatable?.Keys ?? Enumerable.Empty<string>())
            .Distinct();
        foreach (var res in resourceNames)
        {
            var cap = node.Status?.Capacity is { } capacity && capacity.TryGetValue(res, out var capVal) ? capVal.ToString() : "-";
            var alloc = node.Status?.Allocatable is { } allocatable && allocatable.TryGetValue(res, out var allocVal) ? allocVal.ToString() : "-";
            capTable.AddRow(res, cap, alloc);
        }
        AnsiConsole.Write(capTable);

        var condTable = new Table().Title("Conditions").Border(TableBorder.Simple);
        condTable.AddColumn("Type");
        condTable.AddColumn("Status");
        condTable.AddColumn("Reason");
        condTable.AddColumn("Message");
        foreach (var cond in node.Status?.Conditions ?? new List<V1NodeCondition>())
            condTable.AddRow(cond.Type, cond.Status, cond.Reason ?? "-", cond.Message ?? "-");
        AnsiConsole.Write(condTable);

        var taints = node.Spec?.Taints;
        AnsiConsole.MarkupLine(taints is { Count: > 0 }
            ? $"Taints: {string.Join(", ", taints.Select(t => $"{t.Key}={t.Value}:{t.Effect}"))}"
            : "Taints: [grey]<none>[/]");

        if (node.Metadata.Labels is { Count: > 0 })
        {
            var labelTable = new Table().Title("Labels").Border(TableBorder.Simple);
            labelTable.AddColumn("Key");
            labelTable.AddColumn("Value");
            foreach (var (key, value) in node.Metadata.Labels)
                labelTable.AddRow(key, value);
            AnsiConsole.Write(labelTable);
        }
    }

    private async Task ShowNodeEventsAsync()
    {
        var node = await PickNodeAsync();
        if (node is null) return;

        AnsiConsole.MarkupLine("[grey]Note: raw kubelet/host logs aren't exposed via the standard Kubernetes API " +
                                "(that's what SSH/journalctl on the node is for). Events are the closest generic, " +
                                "always-available equivalent — what the control plane itself observed about this node.[/]");

        var events = await AnsiConsole.Status().StartAsync(
            "Fetching events...",
            _ => _k8s.GetEventsForObjectAsync("Node", node.Metadata.Name));

        if (events.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No events found for this node.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Last Seen");
        table.AddColumn("Type");
        table.AddColumn("Reason");
        table.AddColumn("Count");
        table.AddColumn("Message");

        foreach (var ev in events.OrderByDescending(e => e.LastTimestamp))
        {
            table.AddRow(
                ev.LastTimestamp?.ToString("u") ?? "-",
                ev.Type == "Warning" ? "[yellow]Warning[/]" : "Normal",
                ev.Reason ?? "-",
                (ev.Count ?? 1).ToString(),
                ev.Message ?? "-");
        }

        AnsiConsole.Write(table);
    }
}
