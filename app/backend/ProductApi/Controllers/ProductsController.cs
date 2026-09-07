using Microsoft.AspNetCore.Mvc;
using ProductApi.Models;
using ProductApi.Services;

namespace ProductApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly ProductService _productService;

    public ProductsController(ProductService productService)
    {
        _productService = productService;
    }

    // GET api/products
    [HttpGet]
    public async Task<ActionResult<List<Product>>> GetAll()
    {
        var products = await _productService.GetAllAsync();
        return Ok(products);
    }

    // GET api/products/{id}
    [HttpGet("{id}")]
    public async Task<ActionResult<Product>> GetById(string id)
    {
        var product = await _productService.GetByIdAsync(id);
        if (product is null) return NotFound();
        return Ok(product);
    }

    // POST api/products
    [HttpPost]
    public async Task<ActionResult<Product>> Create([FromBody] ProductInput input)
    {
        var product = new Product
        {
            Name = input.Name,
            Description = input.Description,
            Category = input.Category,
            Price = input.Price,
            Quantity = input.Quantity
        };

        var created = await _productService.CreateAsync(product);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    // PUT api/products/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] ProductInput input)
    {
        var existing = await _productService.GetByIdAsync(id);
        if (existing is null) return NotFound();

        existing.Name = input.Name;
        existing.Description = input.Description;
        existing.Category = input.Category;
        existing.Price = input.Price;
        existing.Quantity = input.Quantity;

        await _productService.UpdateAsync(id, existing);
        return NoContent();
    }

    // DELETE api/products/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var deleted = await _productService.DeleteAsync(id);
        if (!deleted) return NotFound();
        return NoContent();
    }
}
