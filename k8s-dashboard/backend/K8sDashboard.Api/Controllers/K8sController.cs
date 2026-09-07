using K8sDashboard.Api.Models;
using K8sDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace K8sDashboard.Api.Controllers;

// Every route here is scoped under a registered cluster id, e.g.
// GET /api/clusters/{clusterId}/nodes — same resource shapes regardless of
// which cluster clusterId points at, since the underlying query service
// only ever talks to the generic Kubernetes API.
[ApiController]
[Route("api/clusters/{clusterId}")]
public class K8sController : ControllerBase
{
    private readonly ClusterClientFactory _clientFactory;
    private readonly KubernetesQueryService _query;

    public K8sController(ClusterClientFactory clientFactory, KubernetesQueryService query)
    {
        _clientFactory = clientFactory;
        _query = query;
    }

    [HttpGet("nodes")]
    public async Task<IActionResult> GetNodes(Guid clusterId)
    {
        var client = await _clientFactory.GetClientAsync(clusterId);
        return Ok(await _query.GetNodesAsync(client));
    }

    [HttpGet("nodes/{name}")]
    public async Task<IActionResult> GetNode(Guid clusterId, string name)
    {
        var client = await _clientFactory.GetClientAsync(clusterId);
        return Ok(await _query.GetNodeAsync(client, name));
    }

    [HttpGet("nodes/{name}/events")]
    public async Task<IActionResult> GetNodeEvents(Guid clusterId, string name)
    {
        var client = await _clientFactory.GetClientAsync(clusterId);
        return Ok(await _query.GetEventsForObjectAsync(client, "Node", name));
    }

    [HttpGet("namespaces")]
    public async Task<IActionResult> GetNamespaces(Guid clusterId)
    {
        var client = await _clientFactory.GetClientAsync(clusterId);
        return Ok(await _query.GetNamespacesAsync(client));
    }

    [HttpGet("namespaces/{ns}/pods")]
    public async Task<IActionResult> GetPods(Guid clusterId, string ns)
    {
        var client = await _clientFactory.GetClientAsync(clusterId);
        return Ok(await _query.GetPodsAsync(client, ns));
    }

    [HttpGet("namespaces/{ns}/pods/{pod}")]
    public async Task<IActionResult> GetPod(Guid clusterId, string ns, string pod)
    {
        var client = await _clientFactory.GetClientAsync(clusterId);
        return Ok(await _query.GetPodAsync(client, ns, pod));
    }

    [HttpGet("namespaces/{ns}/pods/{pod}/events")]
    public async Task<IActionResult> GetPodEvents(Guid clusterId, string ns, string pod)
    {
        var client = await _clientFactory.GetClientAsync(clusterId);
        return Ok(await _query.GetEventsForObjectAsync(client, "Pod", pod, ns));
    }

    [HttpGet("namespaces/{ns}/pods/{pod}/logs")]
    public async Task<IActionResult> GetPodLogs(Guid clusterId, string ns, string pod, [FromQuery] string container, [FromQuery] int tailLines = 100)
    {
        var client = await _clientFactory.GetClientAsync(clusterId);
        var logs = await _query.GetPodLogsAsync(client, ns, pod, container, tailLines);
        return Ok(new { logs });
    }

    [HttpPost("namespaces/{ns}/pods/{pod}/exec")]
    public async Task<IActionResult> ExecInPod(Guid clusterId, string ns, string pod, [FromBody] ExecRequest request)
    {
        var client = await _clientFactory.GetClientAsync(clusterId);
        var output = await _query.ExecInPodAsync(client, ns, pod, request.Container, request.Command);
        return Ok(new { output });
    }

    [HttpGet("namespaces/{ns}/deployments")]
    public async Task<IActionResult> GetDeployments(Guid clusterId, string ns)
    {
        var client = await _clientFactory.GetClientAsync(clusterId);
        return Ok(await _query.GetDeploymentsAsync(client, ns));
    }

    [HttpGet("namespaces/{ns}/services")]
    public async Task<IActionResult> GetServices(Guid clusterId, string ns)
    {
        var client = await _clientFactory.GetClientAsync(clusterId);
        return Ok(await _query.GetServicesAsync(client, ns));
    }

    [HttpGet("events")]
    public async Task<IActionResult> GetEvents(Guid clusterId, [FromQuery] string? ns = null)
    {
        var client = await _clientFactory.GetClientAsync(clusterId);
        return Ok(await _query.GetEventsAsync(client, ns));
    }
}
