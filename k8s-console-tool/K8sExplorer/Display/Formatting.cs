using k8s.Models;

namespace K8sExplorer.Display;

// Small formatting helpers so table-building code in the menus stays
// readable — mirrors the kind of formatting kubectl itself applies
// (Ready/NotReady from conditions, "roles" from well-known labels, age as
// a short relative duration).
public static class Formatting
{
    public static string GetNodeStatus(V1Node node)
    {
        var ready = node.Status?.Conditions?.FirstOrDefault(c => c.Type == "Ready");
        return ready?.Status == "True" ? "Ready" : "NotReady";
    }

    public static string GetNodeRoles(V1Node node)
    {
        var labels = node.Metadata.Labels;
        if (labels is null) return "<none>";

        var roles = labels.Keys
            .Where(k => k.StartsWith("node-role.kubernetes.io/"))
            .Select(k => k["node-role.kubernetes.io/".Length..])
            .ToList();

        return roles.Count > 0 ? string.Join(",", roles) : "<none>";
    }

    public static string GetAge(DateTime? creationTimestamp)
    {
        if (creationTimestamp is null) return "<unknown>";

        var span = DateTime.UtcNow - creationTimestamp.Value.ToUniversalTime();
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{(int)span.TotalSeconds}s";
    }

    public static string GetPodReadyCount(V1Pod pod)
    {
        var statuses = pod.Status?.ContainerStatuses;
        if (statuses is null || statuses.Count == 0) return "0/0";
        var ready = statuses.Count(s => s.Ready);
        return $"{ready}/{statuses.Count}";
    }

    public static string GetPodRestarts(V1Pod pod)
    {
        var statuses = pod.Status?.ContainerStatuses;
        if (statuses is null || statuses.Count == 0) return "0";
        return statuses.Sum(s => s.RestartCount).ToString();
    }
}
