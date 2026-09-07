using K8sExplorer.Display;
using K8sExplorer.Services;
using Spectre.Console;

namespace K8sExplorer.Menus;

public class DeploymentMenu
{
    private readonly KubernetesService _k8s;

    public DeploymentMenu(KubernetesService k8s) => _k8s = k8s;

    public async Task RunAsync()
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Deployments[/]")
                    .AddChoices("3.1 List deployments (select a namespace)", "0. Back"));

            if (choice == "0. Back") return;

            var namespaces = await _k8s.GetNamespacesAsync();
            var ns = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a namespace")
                    .PageSize(15)
                    .AddChoices(namespaces.Select(n => n.Metadata.Name)));

            var deployments = await AnsiConsole.Status().StartAsync("Fetching deployments...", _ => _k8s.GetDeploymentsAsync(ns));

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Name");
            table.AddColumn("Ready");
            table.AddColumn("Up-to-date");
            table.AddColumn("Available");
            table.AddColumn("Age");

            foreach (var d in deployments)
            {
                table.AddRow(
                    d.Metadata.Name,
                    $"{d.Status?.ReadyReplicas ?? 0}/{d.Status?.Replicas ?? 0}",
                    (d.Status?.UpdatedReplicas ?? 0).ToString(),
                    (d.Status?.AvailableReplicas ?? 0).ToString(),
                    Formatting.GetAge(d.Metadata.CreationTimestamp));
            }

            AnsiConsole.Write(table);
        }
    }
}
