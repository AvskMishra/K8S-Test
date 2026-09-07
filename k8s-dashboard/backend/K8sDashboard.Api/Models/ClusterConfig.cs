namespace K8sDashboard.Api.Models;

// A registered cluster. The kubeconfig IS the connection — no separate
// host/auth-method fields, because kubeconfig already fully describes both
// for any cluster (client-cert, bearer token, or exec-plugin auth), on any
// OS, from any vendor's Kubernetes distribution.
public class ClusterConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string KubeconfigYaml { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
