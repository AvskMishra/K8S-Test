using System.Text.Json;
using k8s.Autorest;
using Spectre.Console;

namespace K8sExplorer.Menus;

// Shared plumbing for the menu options that change cluster state: an
// explicit confirmation step, and running one action per selected item so a
// failure on one node/pod is reported without abandoning the rest.
internal static class ActionHelpers
{
    public static bool Confirm(string question, bool defaultValue = false) =>
        AnsiConsole.Prompt(new ConfirmationPrompt(question) { DefaultValue = defaultValue });

    // Returns how many items failed (the command-line mode turns that into
    // its exit code).
    public static async Task<int> ForEachAsync<T>(IEnumerable<T> items, Func<T, string> describe, Func<T, Task> action)
    {
        var failures = 0;
        foreach (var item in items)
        {
            var label = Markup.Escape(describe(item));
            try
            {
                await action(item);
                AnsiConsole.MarkupLine($"  [green]ok[/]     {label}");
            }
            catch (Exception ex)
            {
                failures++;
                AnsiConsole.MarkupLine($"  [red]failed[/] {label}: {Markup.Escape(ErrorMessage(ex))}");
            }
        }
        return failures;
    }

    // The client's HttpOperationException message is just "Operation returned
    // an invalid status code 'Forbidden'"; the API server's Status body has
    // the useful part (e.g. which RBAC verb is missing), so surface that.
    public static string ErrorMessage(Exception ex)
    {
        if (ex is HttpOperationException { Response.Content: { Length: > 0 } content })
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("message", out var message))
                    return message.GetString() ?? ex.Message;
            }
            catch (JsonException)
            {
            }
        }
        return ex.Message;
    }
}
