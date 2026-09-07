using System.ComponentModel.DataAnnotations;

namespace K8sDashboard.Api.Models;

// What the client sends to register/update a cluster.
public class ClusterInput
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }

    [Required]
    public string KubeconfigYaml { get; set; } = string.Empty;
}

// What the client sees when listing/getting clusters — deliberately omits
// the kubeconfig so credentials don't casually end up in a browser's
// network tab or a screenshot.
public class ClusterSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }

    public static ClusterSummary From(ClusterConfig config) => new()
    {
        Id = config.Id,
        Name = config.Name,
        Description = config.Description,
        CreatedAt = config.CreatedAt
    };
}

public class ExecRequest
{
    [Required]
    public string Container { get; set; } = string.Empty;

    [Required]
    public string Command { get; set; } = string.Empty;
}
