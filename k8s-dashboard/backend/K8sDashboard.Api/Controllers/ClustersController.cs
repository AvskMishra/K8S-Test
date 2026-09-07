using k8s;
using K8sDashboard.Api.Models;
using K8sDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace K8sDashboard.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ClustersController : ControllerBase
{
    private readonly ClusterConfigStore _store;
    private readonly ClusterClientFactory _clientFactory;

    public ClustersController(ClusterConfigStore store, ClusterClientFactory clientFactory)
    {
        _store = store;
        _clientFactory = clientFactory;
    }

    [HttpGet]
    public async Task<ActionResult<List<ClusterSummary>>> GetAll()
    {
        var clusters = await _store.GetAllAsync();
        return Ok(clusters.Select(ClusterSummary.From).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ClusterSummary>> GetById(Guid id)
    {
        var cluster = await _store.GetByIdAsync(id);
        if (cluster is null) return NotFound();
        return Ok(ClusterSummary.From(cluster));
    }

    [HttpPost]
    public async Task<ActionResult<ClusterSummary>> Create([FromBody] ClusterInput input)
    {
        var config = new Models.ClusterConfig
        {
            Name = input.Name,
            Description = input.Description,
            KubeconfigYaml = input.KubeconfigYaml
        };

        var created = await _store.AddAsync(config);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, ClusterSummary.From(created));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ClusterInput input)
    {
        var updated = await _store.UpdateAsync(id, input.Name, input.Description, input.KubeconfigYaml);
        if (!updated) return NotFound();

        _clientFactory.Invalidate(id); // next request rebuilds the client from the new kubeconfig
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var deleted = await _store.DeleteAsync(id);
        if (!deleted) return NotFound();

        _clientFactory.Invalidate(id);
        return NoContent();
    }

    // Verifies the stored kubeconfig actually reaches its cluster, without
    // requiring the caller to know which resource to ask for.
    [HttpGet("{id}/test")]
    public async Task<IActionResult> TestConnection(Guid id)
    {
        try
        {
            var client = await _clientFactory.GetClientAsync(id);
            var namespaces = await client.CoreV1.ListNamespaceAsync();
            return Ok(new { reachable = true, namespaceCount = namespaces.Items.Count });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return Ok(new { reachable = false, error = ex.Message });
        }
    }
}
