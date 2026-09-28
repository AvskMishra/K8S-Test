using k8s.Models;
using K8sExplorer.Services;
using Spectre.Console;

namespace K8sExplorer.Display;

// Rendering for every resource view, shared by the interactive menus and the
// command-line mode (Cli/CommandLine.cs) so both print exactly the same thing.
public static class Views
{
    // kubectl shows a cordoned node as "Ready,SchedulingDisabled".
    public static string NodeStatusMarkup(V1Node node)
    {
        var status = Formatting.GetNodeStatus(node) == "Ready" ? "[green]Ready[/]" : "[red]NotReady[/]";
        return node.Spec?.Unschedulable == true ? $"{status},[yellow]SchedulingDisabled[/]" : status;
    }

    public static void Nodes(IEnumerable<V1Node> nodes)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("Status");
        table.AddColumn("Roles");
        table.AddColumn("Age");
        table.AddColumn("Version");
        table.AddColumn("Internal IP");
        table.AddColumn("Container Runtime");
        table.AddColumn("Taints");

        foreach (var node in nodes)
        {
            var internalIp = node.Status?.Addresses?.FirstOrDefault(a => a.Type == "InternalIP")?.Address ?? "-";
            table.AddRow(
                Markup.Escape(node.Metadata.Name),
                NodeStatusMarkup(node),
                Markup.Escape(Formatting.GetNodeRoles(node)),
                Formatting.GetAge(node.Metadata.CreationTimestamp),
                node.Status?.NodeInfo?.KubeletVersion ?? "-",
                internalIp,
                Markup.Escape(node.Status?.NodeInfo?.ContainerRuntimeVersion ?? "-"),
                Markup.Escape(Formatting.GetNodeTaints(node)));
        }

        AnsiConsole.Write(table);
    }

    public static void NodeDetails(V1Node node)
    {
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(node.Metadata.Name)}[/]").LeftJustified());

        AnsiConsole.MarkupLine($"Status:        {NodeStatusMarkup(node)}");
        AnsiConsole.MarkupLine($"Roles:         {Markup.Escape(Formatting.GetNodeRoles(node))}");
        AnsiConsole.MarkupLine($"Age:           {Formatting.GetAge(node.Metadata.CreationTimestamp)}");
        AnsiConsole.MarkupLine($"OS Image:      {Markup.Escape(node.Status?.NodeInfo?.OsImage ?? "")}");
        AnsiConsole.MarkupLine($"Kernel:        {Markup.Escape(node.Status?.NodeInfo?.KernelVersion ?? "")}");
        AnsiConsole.MarkupLine($"Kubelet:       {Markup.Escape(node.Status?.NodeInfo?.KubeletVersion ?? "")}");
        AnsiConsole.MarkupLine($"Runtime:       {Markup.Escape(node.Status?.NodeInfo?.ContainerRuntimeVersion ?? "")}");
        AnsiConsole.MarkupLine($"Architecture:  {Markup.Escape(node.Status?.NodeInfo?.Architecture ?? "")}");

        var addrTable = new Table().Title("Addresses").Border(TableBorder.Simple);
        addrTable.AddColumn("Type");
        addrTable.AddColumn("Address");
        foreach (var addr in node.Status?.Addresses ?? new List<V1NodeAddress>())
            addrTable.AddRow(Markup.Escape(addr.Type), Markup.Escape(addr.Address));
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
            capTable.AddRow(Markup.Escape(res), Markup.Escape(cap), Markup.Escape(alloc));
        }
        AnsiConsole.Write(capTable);

        var condTable = new Table().Title("Conditions").Border(TableBorder.Simple);
        condTable.AddColumn("Type");
        condTable.AddColumn("Status");
        condTable.AddColumn("Reason");
        condTable.AddColumn("Message");
        foreach (var cond in node.Status?.Conditions ?? new List<V1NodeCondition>())
            condTable.AddRow(Markup.Escape(cond.Type), Markup.Escape(cond.Status), Markup.Escape(cond.Reason ?? "-"), Markup.Escape(cond.Message ?? "-"));
        AnsiConsole.Write(condTable);

        var taints = node.Spec?.Taints;
        AnsiConsole.MarkupLine(taints is { Count: > 0 }
            ? $"Taints: {Markup.Escape(Formatting.GetNodeTaints(node))}"
            : "Taints: [grey]<none>[/]");

        if (node.Metadata.Labels is { Count: > 0 })
        {
            var labelTable = new Table().Title("Labels").Border(TableBorder.Simple);
            labelTable.AddColumn("Key");
            labelTable.AddColumn("Value");
            foreach (var (key, value) in node.Metadata.Labels)
                labelTable.AddRow(Markup.Escape(key), Markup.Escape(value));
            AnsiConsole.Write(labelTable);
        }
    }

    public static void ObjectEvents(IList<Corev1Event> events, string emptyMessage)
    {
        if (events.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(emptyMessage)}[/]");
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
                Markup.Escape(ev.Reason ?? "-"),
                (ev.Count ?? 1).ToString(),
                Markup.Escape(ev.Message ?? "-"));
        }

        AnsiConsole.Write(table);
    }

    // The Namespace column is only shown when the pods can span namespaces.
    public static void Pods(IEnumerable<V1Pod> pods, bool showNamespace = false)
    {
        var table = new Table().Border(TableBorder.Rounded);
        if (showNamespace) table.AddColumn("Namespace");
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
            var cells = new List<string>();
            if (showNamespace) cells.Add(Markup.Escape(pod.Metadata.NamespaceProperty));
            cells.AddRange(new[]
            {
                Markup.Escape(pod.Metadata.Name),
                Formatting.GetPodReadyCount(pod),
                phase == "Running" ? "[green]Running[/]" : Markup.Escape(phase),
                Formatting.GetPodRestarts(pod),
                Formatting.GetAge(pod.Metadata.CreationTimestamp),
                Markup.Escape(pod.Spec?.NodeName ?? "-"),
                pod.Status?.PodIP ?? "-"
            });
            table.AddRow(cells.ToArray());
        }

        AnsiConsole.Write(table);
    }

    public static void PodDetails(V1Pod pod, IList<Corev1Event> events)
    {
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(pod.Metadata.Name)}[/] ({Markup.Escape(pod.Metadata.NamespaceProperty)})").LeftJustified());
        AnsiConsole.MarkupLine($"Status:     {Markup.Escape(pod.Status?.Phase ?? "")}");
        AnsiConsole.MarkupLine($"Node:       {Markup.Escape(pod.Spec?.NodeName ?? "")}");
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
                Markup.Escape(container.Name),
                Markup.Escape(container.Image ?? "-"),
                status?.Ready.ToString() ?? "-",
                status?.RestartCount.ToString() ?? "-",
                Markup.Escape(state));
        }
        AnsiConsole.Write(table);

        if (pod.Metadata.Labels is { Count: > 0 })
            AnsiConsole.MarkupLine($"Labels:     {Markup.Escape(string.Join(", ", pod.Metadata.Labels.Select(l => $"{l.Key}={l.Value}")))}");

        AnsiConsole.MarkupLine("\n[bold]Recent events for this pod:[/]");
        if (events.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](none)[/]");
            return;
        }
        foreach (var ev in events.OrderByDescending(e => e.LastTimestamp))
            AnsiConsole.MarkupLine($"  [{(ev.Type == "Warning" ? "yellow" : "grey")}]{Markup.Escape(ev.Reason ?? "")}[/]: {Markup.Escape(ev.Message ?? "")}");
    }

    public static void Deployments(IEnumerable<V1Deployment> deployments)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("Ready");
        table.AddColumn("Up-to-date");
        table.AddColumn("Available");
        table.AddColumn("Age");

        foreach (var d in deployments)
        {
            table.AddRow(
                Markup.Escape(d.Metadata.Name),
                $"{d.Status?.ReadyReplicas ?? 0}/{d.Status?.Replicas ?? 0}",
                (d.Status?.UpdatedReplicas ?? 0).ToString(),
                (d.Status?.AvailableReplicas ?? 0).ToString(),
                Formatting.GetAge(d.Metadata.CreationTimestamp));
        }

        AnsiConsole.Write(table);
    }

    public static void Services(IEnumerable<V1Service> services)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("Type");
        table.AddColumn("Cluster-IP");
        table.AddColumn("Ports");
        table.AddColumn("Age");

        foreach (var svc in services)
        {
            var ports = svc.Spec?.Ports is { Count: > 0 }
                ? string.Join(", ", svc.Spec.Ports.Select(p =>
                    p.NodePort is > 0 ? $"{p.Port}:{p.NodePort}/{p.Protocol}" : $"{p.Port}/{p.Protocol}"))
                : "-";

            table.AddRow(
                Markup.Escape(svc.Metadata.Name),
                svc.Spec?.Type ?? "-",
                svc.Spec?.ClusterIP ?? "-",
                ports,
                Formatting.GetAge(svc.Metadata.CreationTimestamp));
        }

        AnsiConsole.Write(table);
    }

    public static void Namespaces(IEnumerable<V1Namespace> namespaces)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("Status");
        table.AddColumn("Age");

        foreach (var ns in namespaces)
            table.AddRow(Markup.Escape(ns.Metadata.Name), ns.Status?.Phase ?? "-", Formatting.GetAge(ns.Metadata.CreationTimestamp));

        AnsiConsole.Write(table);
    }

    public static void RecentEvents(IEnumerable<Corev1Event> events, int limit = 50)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Last Seen");
        table.AddColumn("Namespace");
        table.AddColumn("Type");
        table.AddColumn("Object");
        table.AddColumn("Reason");
        table.AddColumn("Message");

        foreach (var ev in events.OrderByDescending(e => e.LastTimestamp).Take(limit))
        {
            table.AddRow(
                ev.LastTimestamp?.ToString("u") ?? "-",
                Markup.Escape(ev.Metadata.NamespaceProperty ?? "-"),
                ev.Type == "Warning" ? "[yellow]Warning[/]" : "Normal",
                Markup.Escape($"{ev.InvolvedObject.Kind}/{ev.InvolvedObject.Name}"),
                Markup.Escape(ev.Reason ?? "-"),
                Markup.Escape(ev.Message ?? "-"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey](showing the {limit} most recent)[/]");
    }

    public static void DrainResult(string nodeName, DrainResult result)
    {
        foreach (var s in result.Skipped) AnsiConsole.MarkupLine($"  [grey]skipped[/] {Markup.Escape(s)}");
        foreach (var b in result.Blocked) AnsiConsole.MarkupLine($"  [red]blocked[/] {Markup.Escape(b)}");
        foreach (var f in result.Failed) AnsiConsole.MarkupLine($"  [red]failed[/]  {Markup.Escape(f)}");
        AnsiConsole.MarkupLine(result.Succeeded
            ? $"[green]{Markup.Escape(nodeName)} drained ({result.Evicted.Count} pod(s) evicted). Uncordon it to bring it back.[/]"
            : $"[yellow]{Markup.Escape(nodeName)} is cordoned but not fully drained.[/]");
    }
}
