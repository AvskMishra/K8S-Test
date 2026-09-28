using k8s.Models;
using K8sExplorer.Display;
using K8sExplorer.Services;
using Spectre.Console;

namespace K8sExplorer.Menus;

public class NodeMenu
{
    private readonly KubernetesService _k8s;
    private readonly ClusterOperationsService _ops;

    public NodeMenu(KubernetesService k8s, ClusterOperationsService ops)
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
                    .Title("[bold]Nodes[/]")
                    .PageSize(15)
                    .AddChoices(
                        "1.1 List all nodes",
                        "1.2 Node details (select a node)",
                        "1.3 Node events (select a node)",
                        "1.4 Cordon node(s)",
                        "1.5 Uncordon node(s)",
                        "1.6 Drain node(s)",
                        "1.7 Add / update a taint",
                        "1.8 Remove a taint",
                        "1.9 Add / update a label",
                        "1.10 Remove a label",
                        "0. Back"));

            switch (choice)
            {
                case "1.1 List all nodes": await ListNodesAsync(); break;
                case "1.2 Node details (select a node)": await ShowNodeDetailsAsync(); break;
                case "1.3 Node events (select a node)": await ShowNodeEventsAsync(); break;
                case "1.4 Cordon node(s)": await SetSchedulableAsync(cordon: true); break;
                case "1.5 Uncordon node(s)": await SetSchedulableAsync(cordon: false); break;
                case "1.6 Drain node(s)": await DrainNodesAsync(); break;
                case "1.7 Add / update a taint": await AddTaintAsync(); break;
                case "1.8 Remove a taint": await RemoveTaintAsync(); break;
                case "1.9 Add / update a label": await SetLabelAsync(remove: false); break;
                case "1.10 Remove a label": await SetLabelAsync(remove: true); break;
                default: return;
            }
        }
    }

    private async Task ListNodesAsync()
    {
        var nodes = await AnsiConsole.Status().StartAsync("Fetching nodes...", _ => _k8s.GetNodesAsync());
        Views.Nodes(nodes);
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
        Views.NodeDetails(node);
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
        Views.ObjectEvents(events, "No events found for this node.");
    }

    private async Task<List<V1Node>> PickNodesAsync(string title)
    {
        var nodes = await AnsiConsole.Status().StartAsync("Fetching nodes...", _ => _k8s.GetNodesAsync());
        if (nodes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No nodes found.[/]");
            return new List<V1Node>();
        }

        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<V1Node>()
                .Title(title)
                .NotRequired()
                .PageSize(15)
                .InstructionsText("[grey](space to toggle, enter to accept)[/]")
                .UseConverter(n => $"{Markup.Escape(n.Metadata.Name)}  {Views.NodeStatusMarkup(n)}  [grey]{Markup.Escape(Formatting.GetNodeRoles(n))} | taints: {Markup.Escape(Formatting.GetNodeTaints(n))}[/]")
                .AddChoices(nodes));

        if (selected.Count == 0) AnsiConsole.MarkupLine("[grey]No nodes selected.[/]");
        return selected;
    }

    private async Task SetSchedulableAsync(bool cordon)
    {
        var verb = cordon ? "Cordon" : "Uncordon";
        var nodes = await PickNodesAsync($"{verb} which node(s)?");
        if (nodes.Count == 0) return;

        if (cordon)
            AnsiConsole.MarkupLine("[grey]Cordoning stops new pods being scheduled on a node; pods already running there are left alone.[/]");
        if (!ActionHelpers.Confirm($"{verb} {nodes.Count} node(s)?", defaultValue: true)) return;

        await ActionHelpers.ForEachAsync(nodes, n => n.Metadata.Name, n => _ops.SetNodeUnschedulableAsync(n.Metadata.Name, cordon));
    }

    private async Task DrainNodesAsync()
    {
        var allNodes = await _k8s.GetNodesAsync();
        var nodes = await PickNodesAsync("Drain which node(s)?");
        if (nodes.Count == 0) return;

        if (nodes.Count == allNodes.Count)
            AnsiConsole.MarkupLine("[red]Warning:[/] every node is selected — evicted pods will have nowhere to be rescheduled.");

        var options = new DrainOptions(
            IgnoreDaemonSets: ActionHelpers.Confirm("Ignore DaemonSet-managed pods? (--ignore-daemonsets)", defaultValue: true),
            DeleteEmptyDirData: ActionHelpers.Confirm("Allow evicting pods that use emptyDir volumes? Their data is lost. (--delete-emptydir-data)"),
            Force: ActionHelpers.Confirm("Also delete pods with no controller? They will NOT come back. (--force)"),
            Timeout: TimeSpan.FromSeconds(AnsiConsole.Prompt(new TextPrompt<int>("Timeout in seconds:").DefaultValue(120))));

        if (!ActionHelpers.Confirm($"Cordon and evict pods from {string.Join(", ", nodes.Select(n => n.Metadata.Name))}?")) return;

        foreach (var node in nodes)
        {
            AnsiConsole.Write(new Rule($"[bold]Draining {Markup.Escape(node.Metadata.Name)}[/]").LeftJustified());
            try
            {
                var result = await _ops.DrainNodeAsync(
                    node.Metadata.Name,
                    options,
                    msg => AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(msg)}[/]"));

                Views.DrainResult(node.Metadata.Name, result);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]failed[/] {Markup.Escape(ActionHelpers.ErrorMessage(ex))}");
            }
        }
    }

    private async Task AddTaintAsync()
    {
        var nodes = await PickNodesAsync("Taint which node(s)?");
        if (nodes.Count == 0) return;

        var key = AnsiConsole.Prompt(
            new TextPrompt<string>("Taint key (e.g. dedicated or example.com/maintenance):")
                .Validate(k => !string.IsNullOrWhiteSpace(k) && !k.Any(char.IsWhiteSpace), "Key must be non-empty with no spaces"));
        var value = AnsiConsole.Prompt(new TextPrompt<string>("Taint value (blank for none):").AllowEmpty());
        var effect = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Effect")
                .AddChoices("NoSchedule", "PreferNoSchedule", "NoExecute"));

        if (effect == "NoExecute")
            AnsiConsole.MarkupLine("[yellow]NoExecute evicts every running pod on the node that doesn't tolerate this taint.[/]");

        var display = string.IsNullOrEmpty(value) ? $"{key}:{effect}" : $"{key}={value}:{effect}";
        if (!ActionHelpers.Confirm($"Apply taint {Markup.Escape(display)} to {nodes.Count} node(s)?", defaultValue: effect != "NoExecute")) return;

        var taint = new V1Taint { Key = key, Value = string.IsNullOrEmpty(value) ? null : value, Effect = effect };
        await ActionHelpers.ForEachAsync(nodes, n => n.Metadata.Name, n => _ops.AddOrUpdateTaintAsync(n.Metadata.Name, taint));
    }

    private async Task RemoveTaintAsync()
    {
        var nodes = await PickNodesAsync("Remove a taint from which node(s)?");
        if (nodes.Count == 0) return;

        var taints = nodes
            .SelectMany(n => n.Spec?.Taints ?? new List<V1Taint>())
            .Select(t => $"{t.Key}:{t.Effect}")
            .Distinct()
            .ToList();
        if (taints.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]The selected node(s) have no taints.[/]");
            return;
        }

        var chosen = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Remove which taint?")
                .UseConverter(Markup.Escape)
                .AddChoices(taints));
        var separator = chosen.LastIndexOf(':');
        var key = chosen[..separator];
        var effect = chosen[(separator + 1)..];

        await ActionHelpers.ForEachAsync(nodes, n => n.Metadata.Name, async n =>
        {
            if (!await _ops.RemoveTaintAsync(n.Metadata.Name, key, effect))
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(n.Metadata.Name)} didn't have this taint[/]");
        });
    }

    private async Task SetLabelAsync(bool remove)
    {
        var nodes = await PickNodesAsync(remove ? "Remove a label from which node(s)?" : "Label which node(s)?");
        if (nodes.Count == 0) return;

        string key;
        string? value = null;
        if (remove)
        {
            var keys = nodes.SelectMany(n => n.Metadata.Labels?.Keys ?? Enumerable.Empty<string>()).Distinct().Order().ToList();
            if (keys.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]The selected node(s) have no labels.[/]");
                return;
            }
            key = AnsiConsole.Prompt(
                new SelectionPrompt<string>().Title("Remove which label?").PageSize(15).UseConverter(Markup.Escape).AddChoices(keys));
            if (key.Contains("kubernetes.io/"))
                AnsiConsole.MarkupLine("[yellow]This is a system label — the kubelet or other components may rely on it (or re-add it).[/]");
        }
        else
        {
            key = AnsiConsole.Prompt(
                new TextPrompt<string>("Label key:")
                    .Validate(k => !string.IsNullOrWhiteSpace(k) && !k.Any(char.IsWhiteSpace), "Key must be non-empty with no spaces"));
            value = AnsiConsole.Prompt(new TextPrompt<string>("Label value (blank allowed):").AllowEmpty());
        }

        var description = remove ? $"remove label {key}" : $"set label {key}={value}";
        if (!ActionHelpers.Confirm($"{Markup.Escape(description)} on {nodes.Count} node(s)?", defaultValue: !remove)) return;

        await ActionHelpers.ForEachAsync(nodes, n => n.Metadata.Name, n => _ops.SetNodeLabelAsync(n.Metadata.Name, key, value));
    }
}
