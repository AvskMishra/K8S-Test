using Microsoft.OpenApi;
using ProductApi.Services;
using ProductApi.Settings;

var builder = WebApplication.CreateBuilder(args);

// --- MongoDB settings & service ---
builder.Services.Configure<MongoDbSettings>(
    builder.Configuration.GetSection("MongoDbSettings"));
builder.Services.AddSingleton<ProductService>();

// --- Controllers ---
builder.Services.AddControllers();

// --- Swagger (interactive API docs / manual testing) ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Product API", Version = "v1" });
});

// --- CORS: allow the Angular dev server (and any origin in this learning setup) ---
const string CorsPolicy = "AllowAngularApp";
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicy, policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Product API v1");
});

app.UseCors(CorsPolicy);
app.UseAuthorization();
app.MapControllers();

// Simple liveness/readiness endpoint — useful now, essential later for Kubernetes probes.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
