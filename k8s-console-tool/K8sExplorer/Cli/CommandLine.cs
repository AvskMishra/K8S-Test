using k8s.Models;
using K8sExplorer.Display;
using K8sExplorer.Menus;
using K8sExplorer.Services;
using Spectre.Console;

namespace K8sExplorer.Cli;

// Non-interactive mode: `K8sExplorer <resource> <verb> [names...] [options]`.
// Same KubernetesService / ClusterOperationsService calls and same output
// (Display/Views) as the menus, but everything comes from the command line
// and nothing prompts — so it can be scripted. Syntax deliberately follows
// kubectl where there's an equivalent (taint/label specs, -n, -A, -c, --).
//
// Exit codes: 0 success, 1 an operation failed (on any selected item),
// 2 bad usage.
public static class CommandLine
{
    public const string Usage = """
        Usage: K8sExplorer <resource> <verb> [names...] [options]
               K8sExplorer                      (no arguments: interactive menus)

        Nodes (node | nodes | no)
          nodes list                            list all nodes
          nodes get <node>...                   full details (capacity, conditions, taints, labels)
          nodes events <node>                   events recorded for the node
          nodes pods <node>                     pods running on the node (all namespaces)
          nodes cordon <node>...                mark unschedulable
          nodes uncordon <node>...              mark schedulable again
          nodes drain <node>... [--ignore-daemonsets] [--delete-emptydir-data] [--force] [--timeout 120]
          nodes taint <node>... key[=value]:Effect ...    add/update (Effect: NoSchedule|PreferNoSchedule|NoExecute)
          nodes taint <node>... key[:Effect]- ...         remove
          nodes label <node>... key=value ... | key- ...  add/update or remove labels

        Pods (pod | pods | po)          -n <namespace> (default: "default")
          pods list [-n ns | -A] [--node <node>]
          pods get <pod>... [-n ns]             details + recent events
          pods logs <pod> [-n ns] [-c container] [--tail 100]
          pods exec <pod> [-n ns] [-c container] [--] <command...>   options go before the command;
                                                pass one quoted string ("ls /app | wc -l") to run it through sh -c
          pods delete <pod>... [-n ns] [--force]    --force = grace period 0
          pods evict <pod>... [-n ns]           respects PodDisruptionBudgets
          pods restart <pod>... [-n ns]         rollout-restart the owning Deployment/StatefulSet/DaemonSet
          pods label <pod>... [-n ns] key=value ... | key- ...

        Other
          deployments list [-n ns]    (deployment | deploy)
          services list [-n ns]       (service | svc)
          namespaces list             (namespace | ns)
          events [-n ns]              (event | ev; all namespaces unless -n)

        Global options
          --kubeconfig <path>   defaults to ../kubeconfig-direct.yaml if present, else ~/.kube/config / in-cluster
          -h, --help

        Examples
          K8sExplorer nodes cordon k8slab-m02 k8slab-m03
          K8sExplorer nodes drain k8slab-m04 --ignore-daemonsets --delete-emptydir-data
          K8sExplorer nodes taint k8slab-m02 dedicated=db:NoSchedule
          K8sExplorer nodes taint k8slab-m02 dedicated:NoSchedule-
          K8sExplorer pods list -n product-catalog
          K8sExplorer pods restart product-api-84955bd674-2xdj4 -n product-catalog
          K8sExplorer pods exec product-api-84955bd674-2xdj4 -n product-catalog printenv HOSTNAME
        """;

    public static async Task<int> RunAsync(string[] args, string defaultConfigPath)
    {
        ParsedArgs parsed;
        try
        {
            parsed = ParsedArgs.Parse(args);
        }
        catch (UsageException ex)
        {
            return UsageError(ex.Message);
        }

        if (parsed.Positionals.Count == 0 || parsed.Has("help") || parsed.Positionals[0] == "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var kubeconfig = parsed.Get("kubeconfig") ?? (File.Exists(defaultConfigPath) ? defaultConfigPath : null);

        try
        {
            // Lazy so argument mistakes are reported before anything tries to
            // load a kubeconfig or reach the cluster.
            var commands = new Commands(new Lazy<k8s.IKubernetes>(() => KubeClientFactory.Create(kubeconfig)), parsed);
            return await commands.DispatchAsync();
        }
        catch (UsageException ex)
        {
            return UsageError(ex.Message);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ActionHelpers.ErrorMessage(ex))}");
            return 1;
        }
    }

    private static int UsageError(string message)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        Console.WriteLine("Run 'K8sExplorer --help' for usage.");
        return 2;
    }
}

internal class UsageException(string message) : Exception(message);

internal class Commands
{
    private readonly Lazy<KubernetesService> _k8sLazy;
    private readonly Lazy<ClusterOperationsService> _opsLazy;
    private readonly ParsedArgs _args;

    public Commands(Lazy<k8s.IKubernetes> client, ParsedArgs args)
    {
        _k8sLazy = new Lazy<KubernetesService>(() => new KubernetesService(client.Value));
        _opsLazy = new Lazy<ClusterOperationsService>(() => new ClusterOperationsService(client.Value));
        _args = args;
    }

    private KubernetesService _k8s => _k8sLazy.Value;
    private ClusterOperationsService _ops => _opsLazy.Value;

    private string Resource => _args.Positionals[0].ToLowerInvariant();
    private string Verb => _args.Positionals.Count > 1 ? _args.Positionals[1].ToLowerInvariant() : "list";
    private List<string> Operands => _args.Positionals.Skip(2).ToList();
    private string Namespace => _args.Get("namespace") ?? "default";

    public Task<int> DispatchAsync() => Resource switch
    {
        "node" or "nodes" or "no" => NodesAsync(),
        "pod" or "pods" or "po" => PodsAsync(),
        "deployment" or "deployments" or "deploy" => ListOnlyAsync(async () => Views.Deployments(await _k8s.GetDeploymentsAsync(Namespace))),
        "service" or "services" or "svc" => ListOnlyAsync(async () => Views.Services(await _k8s.GetServicesAsync(Namespace))),
        "namespace" or "namespaces" or "ns" => ListOnlyAsync(async () => Views.Namespaces(await _k8s.GetNamespacesAsync())),
        "event" or "events" or "ev" => EventsAsync(),
        _ => throw new UsageException($"Unknown resource '{_args.Positionals[0]}'.")
    };

    // ---------------------------------------------------------------- nodes

    private async Task<int> NodesAsync()
    {
        switch (Verb)
        {
            case "list":
                Views.Nodes(await _k8s.GetNodesAsync());
                return 0;

            case "get":
            case "describe":
                return await ForEachReadAsync(RequireNames("node"), async name => Views.NodeDetails(await _k8s.GetNodeAsync(name)));

            case "events":
                return await ForEachReadAsync(RequireNames("node"), async name =>
                    Views.ObjectEvents(await _k8s.GetEventsForObjectAsync("Node", name), $"No events found for node {name}."));

            case "pods":
                return await ForEachReadAsync(RequireNames("node"), async name =>
                {
                    AnsiConsole.Write(new Rule($"[bold]Pods on {Markup.Escape(name)}[/]").LeftJustified());
                    Views.Pods(await _k8s.FindPodsAsync(null, name), showNamespace: true);
                });

            case "cordon":
            case "uncordon":
                var cordon = Verb == "cordon";
                return await ActionHelpers.ForEachAsync(RequireNames("node"), n => $"{Verb} {n}",
                    n => _ops.SetNodeUnschedulableAsync(n, cordon)) > 0 ? 1 : 0;

            case "drain":
                return await DrainAsync(RequireNames("node"));

            case "taint":
            {
                var (nodes, specs) = SplitNamesAndSpecs("node", "taint");
                var taints = specs.Select(TaintSpec.Parse).ToList();
                var work = nodes.SelectMany(n => taints.Select(t => (Node: n, Taint: t)));
                return await ActionHelpers.ForEachAsync(work, w => $"{w.Node}: {w.Taint}", w => ApplyTaintAsync(w.Node, w.Taint)) > 0 ? 1 : 0;
            }

            case "label":
            {
                var (nodes, specs) = SplitNamesAndSpecs("node", "label");
                var labels = specs.Select(LabelSpec.Parse).ToList();
                var work = nodes.SelectMany(n => labels.Select(l => (Node: n, Label: l)));
                return await ActionHelpers.ForEachAsync(work, w => $"{w.Node}: {w.Label}",
                    w => _ops.SetNodeLabelAsync(w.Node, w.Label.Key, w.Label.Value)) > 0 ? 1 : 0;
            }

            default:
                throw new UsageException($"Unknown nodes verb '{Verb}'.");
        }
    }

    private async Task<int> DrainAsync(List<string> nodes)
    {
        var options = new DrainOptions(
            IgnoreDaemonSets: _args.Has("ignore-daemonsets"),
            DeleteEmptyDirData: _args.Has("delete-emptydir-data"),
            Force: _args.Has("force"),
            Timeout: TimeSpan.FromSeconds(_args.GetInt("timeout", 120)));

        var failed = 0;
        foreach (var node in nodes)
        {
            AnsiConsole.Write(new Rule($"[bold]Draining {Markup.Escape(node)}[/]").LeftJustified());
            try
            {
                var result = await _ops.DrainNodeAsync(node, options, msg => AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(msg)}[/]"));
                Views.DrainResult(node, result);
                if (!result.Succeeded) failed++;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]failed[/] {Markup.Escape(ActionHelpers.ErrorMessage(ex))}");
                failed++;
            }
        }
        return failed > 0 ? 1 : 0;
    }

    private async Task ApplyTaintAsync(string node, TaintSpec spec)
    {
        if (!spec.Remove)
        {
            await _ops.AddOrUpdateTaintAsync(node, new V1Taint { Key = spec.Key, Value = spec.Value, Effect = spec.Effect });
            return;
        }
        if (!await _ops.RemoveTaintAsync(node, spec.Key, spec.Effect))
            throw new InvalidOperationException("taint not found on this node");
    }

    // ----------------------------------------------------------------- pods

    private async Task<int> PodsAsync()
    {
        var ns = Namespace;
        switch (Verb)
        {
            case "list":
                var allNamespaces = _args.Has("all-namespaces");
                Views.Pods(await _k8s.FindPodsAsync(allNamespaces ? null : ns, _args.Get("node")), showNamespace: allNamespaces);
                return 0;

            case "get":
            case "describe":
                return await ForEachReadAsync(RequireNames("pod"), async name =>
                    Views.PodDetails(await _k8s.GetPodAsync(ns, name), await _k8s.GetEventsForObjectAsync("Pod", name, ns)));

            case "logs":
            {
                var pod = await _k8s.GetPodAsync(ns, SingleName("pod"));
                var logs = await _k8s.GetPodLogsAsync(ns, pod.Metadata.Name, ResolveContainer(pod), _args.GetInt("tail", 100));
                Console.Write(logs);
                return 0;
            }

            case "exec":
            {
                if (_args.Command.Count == 0)
                    throw new UsageException("pods exec needs a command after the pod name, e.g. pods exec <pod> -n ns ls -la /app");
                var pod = await _k8s.GetPodAsync(ns, SingleName("pod"));
                // Separate words run as argv, exactly like kubectl exec. One
                // quoted string with spaces ("ls /app | wc -l") goes through
                // /bin/sh -c, so pipes and redirects work too.
                var container = ResolveContainer(pod);
                var output = _args.Command.Count == 1 && _args.Command[0].Any(char.IsWhiteSpace)
                    ? await _k8s.ExecInPodAsync(ns, pod.Metadata.Name, container, _args.Command[0])
                    : await _k8s.ExecInPodAsync(ns, pod.Metadata.Name, container, _args.Command.ToArray());
                Console.Write(output);
                return 0;
            }

            case "delete":
                var force = _args.Has("force");
                return await ActionHelpers.ForEachAsync(RequireNames("pod"), p => $"delete {ns}/{p}{(force ? " (force)" : "")}",
                    p => _ops.DeletePodAsync(ns, p, force)) > 0 ? 1 : 0;

            case "evict":
                return await ActionHelpers.ForEachAsync(RequireNames("pod"), p => $"evict {ns}/{p}",
                    p => _ops.EvictPodAsync(ns, p)) > 0 ? 1 : 0;

            case "restart":
                return await RestartAsync(ns, RequireNames("pod"));

            case "label":
            {
                var (pods, specs) = SplitNamesAndSpecs("pod", "label");
                var labels = specs.Select(LabelSpec.Parse).ToList();
                var work = pods.SelectMany(p => labels.Select(l => (Pod: p, Label: l)));
                return await ActionHelpers.ForEachAsync(work, w => $"{ns}/{w.Pod}: {w.Label}",
                    w => _ops.SetPodLabelAsync(ns, w.Pod, w.Label.Key, w.Label.Value)) > 0 ? 1 : 0;
            }

            default:
                throw new UsageException($"Unknown pods verb '{Verb}'.");
        }
    }

    // Several pods usually share one Deployment, so restart each workload once.
    private async Task<int> RestartAsync(string ns, List<string> podNames)
    {
        var failed = 0;
        var workloads = new List<WorkloadRef>();
        foreach (var name in podNames)
        {
            try
            {
                var workload = await _ops.GetOwningWorkloadAsync(await _k8s.GetPodAsync(ns, name));
                if (workload is null)
                {
                    AnsiConsole.MarkupLine($"  [red]failed[/] {Markup.Escape(name)}: not owned by a Deployment/StatefulSet/DaemonSet (use 'pods delete')");
                    failed++;
                }
                else if (!workloads.Contains(workload))
                {
                    workloads.Add(workload);
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]failed[/] {Markup.Escape(name)}: {Markup.Escape(ActionHelpers.ErrorMessage(ex))}");
                failed++;
            }
        }

        failed += await ActionHelpers.ForEachAsync(workloads, w => $"rollout restart {w}", _ops.RestartWorkloadAsync);
        return failed > 0 ? 1 : 0;
    }

    // Like kubectl: -c if given, otherwise the pod's first container.
    private string ResolveContainer(V1Pod pod)
    {
        var requested = _args.Get("container");
        var names = pod.Spec.Containers.Select(c => c.Name).ToList();
        if (requested is null)
        {
            if (names.Count > 1)
                AnsiConsole.MarkupLine($"[grey]Defaulting to container '{Markup.Escape(names[0])}' (others: {Markup.Escape(string.Join(", ", names.Skip(1)))}); use -c to pick.[/]");
            return names[0];
        }
        if (!names.Contains(requested))
            throw new UsageException($"Pod {pod.Metadata.Name} has no container '{requested}' (has: {string.Join(", ", names)}).");
        return requested;
    }

    // ------------------------------------------------------------- the rest

    private async Task<int> ListOnlyAsync(Func<Task> list)
    {
        if (Verb != "list") throw new UsageException($"'{Resource}' only supports 'list'.");
        await list();
        return 0;
    }

    private async Task<int> EventsAsync()
    {
        if (Verb != "list") throw new UsageException("'events' only supports 'list'.");
        Views.RecentEvents(await _k8s.GetEventsAsync(_args.Get("namespace")));
        return 0;
    }

    // ------------------------------------------------------------- helpers

    private async Task<int> ForEachReadAsync(List<string> names, Func<string, Task> show)
    {
        var failed = 0;
        foreach (var name in names)
        {
            try
            {
                await show(name);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(name)}:[/] {Markup.Escape(ActionHelpers.ErrorMessage(ex))}");
                failed++;
            }
        }
        return failed > 0 ? 1 : 0;
    }

    private List<string> RequireNames(string kind)
    {
        if (Operands.Count == 0) throw new UsageException($"'{Resource} {Verb}' needs at least one {kind} name.");
        return Operands;
    }

    private string SingleName(string kind)
    {
        if (Operands.Count != 1) throw new UsageException($"'{Resource} {Verb}' takes exactly one {kind} name.");
        return Operands[0];
    }

    // Object names can't contain '=' or ':' or end in '-', so anything that
    // does is a taint/label spec — the same rule kubectl relies on.
    private (List<string> Names, List<string> Specs) SplitNamesAndSpecs(string kind, string specKind)
    {
        static bool IsSpec(string s) => s.Contains('=') || s.Contains(':') || s.EndsWith('-');
        var names = Operands.Where(s => !IsSpec(s)).ToList();
        var specs = Operands.Where(IsSpec).ToList();
        if (names.Count == 0) throw new UsageException($"'{Resource} {Verb}' needs at least one {kind} name.");
        if (specs.Count == 0) throw new UsageException($"'{Resource} {Verb}' needs at least one {specKind} spec (see --help).");
        return (names, specs);
    }
}

// key[=value]:Effect to add, key[:Effect]- to remove.
internal record TaintSpec(string Key, string? Value, string? Effect, bool Remove)
{
    private static readonly string[] Effects = { "NoSchedule", "PreferNoSchedule", "NoExecute" };

    public static TaintSpec Parse(string spec)
    {
        var remove = spec.EndsWith('-');
        var body = remove ? spec[..^1] : spec;

        string keyValue = body;
        string? effect = null;
        var colon = body.LastIndexOf(':');
        if (colon >= 0)
        {
            keyValue = body[..colon];
            effect = Effects.FirstOrDefault(e => e.Equals(body[(colon + 1)..], StringComparison.OrdinalIgnoreCase))
                     ?? throw new UsageException($"Invalid taint effect in '{spec}' (use {string.Join(", ", Effects)}).");
        }
        else if (!remove)
        {
            throw new UsageException($"Taint '{spec}' needs an effect: key[=value]:{string.Join("|", Effects)}.");
        }

        var eq = keyValue.IndexOf('=');
        var key = eq >= 0 ? keyValue[..eq] : keyValue;
        var value = eq >= 0 ? keyValue[(eq + 1)..] : null;
        if (key.Length == 0) throw new UsageException($"Taint '{spec}' has an empty key.");
        return new TaintSpec(key, string.IsNullOrEmpty(value) ? null : value, effect, remove);
    }

    public override string ToString() => Remove
        ? $"remove taint {Key}{(Effect is null ? "" : $":{Effect}")}"
        : $"taint {Key}{(Value is null ? "" : $"={Value}")}:{Effect}";
}

// key=value to set, key- to remove (Value null).
internal record LabelSpec(string Key, string? Value)
{
    public static LabelSpec Parse(string spec)
    {
        if (spec.EndsWith('-') && !spec.Contains('='))
            return new LabelSpec(spec[..^1], null);

        var eq = spec.IndexOf('=');
        if (eq <= 0) throw new UsageException($"Label '{spec}' must be key=value or key-.");
        return new LabelSpec(spec[..eq], spec[(eq + 1)..]);
    }

    public override string ToString() => Value is null ? $"remove label {Key}" : $"label {Key}={Value}";
}

internal class ParsedArgs
{
    // Short aliases -> canonical long names.
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["-n"] = "namespace", ["-c"] = "container", ["-A"] = "all-namespaces", ["-h"] = "help"
    };
    private static readonly HashSet<string> ValueOptions = new() { "namespace", "container", "tail", "timeout", "kubeconfig", "node" };
    private static readonly HashSet<string> FlagOptions = new() { "all-namespaces", "force", "ignore-daemonsets", "delete-emptydir-data", "help" };

    private readonly Dictionary<string, string?> _options = new();

    public List<string> Positionals { get; } = new();

    // Everything after a bare "--" (the command for `pods exec`).
    public List<string> Command { get; } = new();

    public bool Has(string name) => _options.ContainsKey(name);
    public string? Get(string name) => _options.TryGetValue(name, out var v) ? v : null;

    public int GetInt(string name, int defaultValue)
    {
        var raw = Get(name);
        if (raw is null) return defaultValue;
        return int.TryParse(raw, out var value) && value > 0
            ? value
            : throw new UsageException($"--{name} must be a positive number, got '{raw}'.");
    }

    public static ParsedArgs Parse(string[] args)
    {
        var parsed = new ParsedArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                parsed.Command.AddRange(args.Skip(i + 1));
                break;
            }
            if (!arg.StartsWith('-') || arg == "-")
            {
                // `pods exec <pod> <command...>`: once the pod name is known,
                // everything from the next word on is the command, dashes
                // included (like `docker exec`). Needed because Windows
                // PowerShell strips a bare "--" before the program sees it.
                if (parsed.Positionals.Count == 3 && parsed.Positionals[1].Equals("exec", StringComparison.OrdinalIgnoreCase))
                {
                    parsed.Command.AddRange(args.Skip(i));
                    break;
                }
                parsed.Positionals.Add(arg);
                continue;
            }

            string name;
            string? inlineValue = null;
            if (Aliases.TryGetValue(arg, out var alias))
            {
                name = alias;
            }
            else if (arg.StartsWith("--"))
            {
                var eq = arg.IndexOf('=');
                name = eq >= 0 ? arg[2..eq] : arg[2..];
                inlineValue = eq >= 0 ? arg[(eq + 1)..] : null;
            }
            else
            {
                // "key-" label/taint removals look like options only if they
                // start with '-', which names can't — so this is a real typo.
                throw new UsageException($"Unknown option '{arg}'.");
            }

            if (ValueOptions.Contains(name))
            {
                var value = inlineValue ?? (i + 1 < args.Length ? args[++i] : throw new UsageException($"--{name} needs a value."));
                parsed._options[name] = value;
            }
            else if (FlagOptions.Contains(name))
            {
                parsed._options[name] = null;
            }
            else
            {
                throw new UsageException($"Unknown option '{arg}'.");
            }
        }
        return parsed;
    }
}
