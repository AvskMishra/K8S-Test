using K8sExplorer.Display;
using K8sExplorer.Services;
using Spectre.Console;

namespace K8sExplorer.Menus;

public class NamespaceMenu
{
    private readonly KubernetesService _k8s;

    public NamespaceMenu(KubernetesService k8s) => _k8s = k8s;

    public async Task RunAsync()
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Namespaces[/]")
                    .AddChoices("5.1 List all namespaces", "0. Back"));

            if (choice == "0. Back") return;

            var namespaces = await AnsiConsole.Status().StartAsync("Fetching namespaces...", _ => _k8s.GetNamespacesAsync());
            Views.Namespaces(namespaces);
        }
    }
}
