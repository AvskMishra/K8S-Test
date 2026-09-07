using K8sDashboard.Api.Services;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ClusterConfigStore>();
builder.Services.AddSingleton<ClusterClientFactory>();
builder.Services.AddSingleton<KubernetesQueryService>();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Kubernetes model classes commonly have reference cycles / large
        // graphs (ManagedFields etc.) — ignore cycles so serialization
        // doesn't blow up, and skip nulls to keep responses readable.
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "K8s Dashboard API", Version = "v1" });
});

const string CorsPolicy = "AllowDashboardUi";
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicy, policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "K8s Dashboard API v1"));
app.UseCors(CorsPolicy);

// Cluster-not-found and upstream-cluster-unreachable both surface as
// exceptions from deep inside the Kubernetes client call chain — map them
// to sensible HTTP statuses in one place instead of try/catching in every
// controller action.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (KeyNotFoundException ex)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (k8s.Autorest.HttpOperationException ex)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsJsonAsync(new { error = $"The target cluster returned an error: {ex.Message}" });
    }
    catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsJsonAsync(new { error = $"Could not reach the target cluster: {ex.Message}" });
    }
});

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
