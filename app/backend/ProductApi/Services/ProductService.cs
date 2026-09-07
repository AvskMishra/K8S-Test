using Microsoft.Extensions.Options;
using MongoDB.Driver;
using ProductApi.Models;
using ProductApi.Settings;

namespace ProductApi.Services;

public class ProductService
{
    private readonly IMongoCollection<Product> _products;

    public ProductService(IOptions<MongoDbSettings> settings)
    {
        var client = new MongoClient(settings.Value.ConnectionString);
        var database = client.GetDatabase(settings.Value.DatabaseName);
        _products = database.GetCollection<Product>(settings.Value.ProductsCollectionName);
    }

    public async Task<List<Product>> GetAllAsync() =>
        await _products.Find(_ => true).SortByDescending(p => p.Id).ToListAsync();

    public async Task<Product?> GetByIdAsync(string id) =>
        await _products.Find(p => p.Id == id).FirstOrDefaultAsync();

    public async Task<Product> CreateAsync(Product product)
    {
        await _products.InsertOneAsync(product);
        return product;
    }

    public async Task<bool> UpdateAsync(string id, Product product)
    {
        var result = await _products.ReplaceOneAsync(p => p.Id == id, product);
        return result.MatchedCount > 0;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var result = await _products.DeleteOneAsync(p => p.Id == id);
        return result.DeletedCount > 0;
    }
}
