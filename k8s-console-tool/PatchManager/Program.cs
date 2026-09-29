using K8sExplorer.Services;
using PatchManager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var kubeconfigPath = builder.Configuration["KUBECONFIG"];

builder.Services.AddSingleton(_ => KubeClientFactory.Create(kubeconfigPath));
builder.Services.AddSingleton<KubernetesService>();
builder.Services.AddSingleton<ClusterOperationsService>();
builder.Services.AddHostedService<PatchScheduleWorker>();

await builder.Build().RunAsync();