using k8s;
using PatchOrchestrator;
using PatchOrchestrator.Services;

var builder = Host.CreateApplicationBuilder(args);

// One Kubernetes client for the app's whole lifetime, built once from the kubeconfig configured
// in appsettings.json ("PatchOrchestrator:KubeconfigPath").
builder.Services.AddSingleton(sp => KubeClientFactory.BuildFromConfig(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddSingleton<ScheduleStore>();
builder.Services.AddSingleton<NodePatchOperations>();
builder.Services.AddSingleton<PatchWorkflowEngine>();
builder.Services.AddSingleton<NodeStateReporter>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
