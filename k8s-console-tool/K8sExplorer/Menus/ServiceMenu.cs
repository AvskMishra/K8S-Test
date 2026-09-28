using K8sExplorer.Display;
using K8sExplorer.Services;
using Spectre.Console;

namespace K8sExplorer.Menus;

public class ServiceMenu
{
    private readonly KubernetesService _k8s;

    public ServiceMenu(KubernetesService k8s) => _k8s = k8s;

    public async Task RunAsync()
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Services[/]")
                    .AddChoices("4.1 List services (select a namespace)", "0. Back"));

            if (choice == "0. Back") return;

            var namespaces = await _k8s.GetNamespacesAsync();
            var ns = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a namespace")
                    .PageSize(15)
                    .AddChoices(namespaces.Select(n => n.Metadata.Name)));

            var services = await AnsiConsole.Status().StartAsync("Fetching services...", _ => _k8s.GetServicesAsync(ns));
            Views.Services(services);
        }
    }
}
