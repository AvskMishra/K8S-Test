using System.Text.Json;
using K8sDashboard.Api.Models;

namespace K8sDashboard.Api.Services;

// File-backed registry of clusters — deliberately not a full database (see
// PLAN.md section 2): the data shape is tiny, and a JSON file is exactly
// the same trust model a kubeconfig file on disk already has. All reads
// and writes to the underlying file are async and serialized by a
// semaphore, since concurrent requests could otherwise race writing it.
public class ClusterConfigStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<ClusterConfig> _cache = new();

    public ClusterConfigStore(IConfiguration configuration)
    {
        var dataDir = configuration["ClusterStore:DataDirectory"] ?? "data";
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "clusters.json");

        if (File.Exists(_filePath))
        {
            var json = File.ReadAllText(_filePath);
            _cache = JsonSerializer.Deserialize<List<ClusterConfig>>(json) ?? new();
        }
    }

    public async Task<IReadOnlyList<ClusterConfig>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _cache.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ClusterConfig?> GetByIdAsync(Guid id)
    {
        await _lock.WaitAsync();
        try
        {
            return _cache.FirstOrDefault(c => c.Id == id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ClusterConfig> AddAsync(ClusterConfig config)
    {
        await _lock.WaitAsync();
        try
        {
            _cache.Add(config);
            await SaveAsync();
            return config;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> UpdateAsync(Guid id, string name, string? description, string kubeconfigYaml)
    {
        await _lock.WaitAsync();
        try
        {
            var existing = _cache.FirstOrDefault(c => c.Id == id);
            if (existing is null) return false;

            existing.Name = name;
            existing.Description = description;
            existing.KubeconfigYaml = kubeconfigYaml;
            await SaveAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        await _lock.WaitAsync();
        try
        {
            var removed = _cache.RemoveAll(c => c.Id == id) > 0;
            if (removed) await SaveAsync();
            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_filePath, json);
    }
}
