using K8sExplorer.Services;
using Spectre.Console;

namespace K8sExplorer.Menus;

public class EventMenu
{
    private readonly KubernetesService _k8s;

    public EventMenu(KubernetesService k8s) => _k8s = k8s;

    public async Task RunAsync()
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Events[/]")
                    .AddChoices("6.1 List recent events (select a namespace, or all)", "0. Back"));

            if (choice == "0. Back") return;

            var namespaces = await _k8s.GetNamespacesAsync();
            var options = new List<string> { "(all namespaces)" };
            options.AddRange(namespaces.Select(n => n.Metadata.Name));

            var pick = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a namespace")
                    .PageSize(15)
                    .AddChoices(options));

            var ns = pick == "(all namespaces)" ? null : pick;
            var events = await AnsiConsole.Status().StartAsync("Fetching events...", _ => _k8s.GetEventsAsync(ns));

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Last Seen");
            table.AddColumn("Namespace");
            table.AddColumn("Type");
            table.AddColumn("Object");
            table.AddColumn("Reason");
            table.AddColumn("Message");

            foreach (var ev in events.OrderByDescending(e => e.LastTimestamp).Take(50))
            {
                table.AddRow(
                    ev.LastTimestamp?.ToString("u") ?? "-",
                    ev.Metadata.NamespaceProperty ?? "-",
                    ev.Type == "Warning" ? "[yellow]Warning[/]" : "Normal",
                    $"{ev.InvolvedObject.Kind}/{ev.InvolvedObject.Name}",
                    ev.Reason ?? "-",
                    ev.Message ?? "-");
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine("[grey](showing the 50 most recent)[/]");
        }
    }
}
