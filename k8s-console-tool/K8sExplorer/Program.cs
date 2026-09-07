using K8sExplorer.Menus;
using K8sExplorer.Services;
using Spectre.Console;

var defaultConfigPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "kubeconfig-direct.yaml"));

// --smoke-test <kubeconfig>: bypasses the interactive menu (which needs a
// real terminal for arrow-key navigation) and exercises the same
// KubernetesService calls directly, printing plain output. Used to verify
// real cluster connectivity in non-interactive contexts; the normal
// interactive experience is the default (no args) path below.
if (args.Length > 0 && args[0] == "--smoke-test")
{
    var path = args.Length > 1 ? args[1] : defaultConfigPath;
    await SmokeTest.RunAsync(path);
    return;
}

AnsiConsole.Write(new FigletText("K8sExplorer").Color(Color.Blue));
AnsiConsole.MarkupLine("[grey]Browse any Kubernetes cluster's nodes, pods, deployments, services, and events — no SSH, no kubectl required.[/]\n");

var kubeconfigPath = AnsiConsole.Prompt(
    new TextPrompt<string>("Path to kubeconfig file (blank = default ~/.kube/config):")
        .DefaultValue(File.Exists(defaultConfigPath) ? defaultConfigPath : string.Empty)
        .AllowEmpty());

KubernetesService k8sService;
try
{
    var client = KubeClientFactory.Create(string.IsNullOrWhiteSpace(kubeconfigPath) ? null : kubeconfigPath);
    k8sService = new KubernetesService(client);

    // Quick connectivity check before showing the menu.
    await AnsiConsole.Status().StartAsync("Connecting to cluster...", async _ => await k8sService.GetNamespacesAsync());
    AnsiConsole.MarkupLine("[green]Connected.[/]\n");
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Could not connect to the cluster:[/] {ex.Message}");
    return;
}

var nodeMenu = new NodeMenu(k8sService);
var podMenu = new PodMenu(k8sService);
var deploymentMenu = new DeploymentMenu(k8sService);
var serviceMenu = new ServiceMenu(k8sService);
var namespaceMenu = new NamespaceMenu(k8sService);
var eventMenu = new EventMenu(k8sService);

while (true)
{
    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[bold underline]Main Menu[/]")
            .AddChoices(
                "1. Nodes",
                "2. Pods",
                "3. Deployments",
                "4. Services",
                "5. Namespaces",
                "6. Events",
                "0. Exit"));

    try
    {
        switch (choice)
        {
            case "1. Nodes": await nodeMenu.RunAsync(); break;
            case "2. Pods": await podMenu.RunAsync(); break;
            case "3. Deployments": await deploymentMenu.RunAsync(); break;
            case "4. Services": await serviceMenu.RunAsync(); break;
            case "5. Namespaces": await namespaceMenu.RunAsync(); break;
            case "6. Events": await eventMenu.RunAsync(); break;
            case "0. Exit": return;
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
    }

    AnsiConsole.WriteLine();
}
