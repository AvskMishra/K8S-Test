using k8s;

namespace K8sExplorer.Services;

public static class KubeClientFactory
{
    // Three ways to get credentials, tried in order:
    //  1. An explicit kubeconfig path, if one was given (same as `kubectl
    //     --kubeconfig`).
    //  2. In-cluster ServiceAccount config, auto-detected via IsInCluster()
    //     (true when running as a pod: KUBERNETES_SERVICE_HOST/_PORT are set
    //     and the ServiceAccount token/CA are mounted at
    //     /var/run/secrets/kubernetes.io/serviceaccount). This is what makes
    //     it work unmodified when deployed as a Deployment/Job in-cluster —
    //     no kubeconfig file needed at all, just RBAC on the ServiceAccount.
    //  3. The default kubeconfig lookup (~/.kube/config + KUBECONFIG env var
    //     + current-context) — same as bare `kubectl` with no flags.
    public static IKubernetes Create(string? kubeconfigPath)
    {
        KubernetesClientConfiguration config;
        if (!string.IsNullOrWhiteSpace(kubeconfigPath))
        {
            config = KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfigPath);
        }
        else if (KubernetesClientConfiguration.IsInCluster())
        {
            config = KubernetesClientConfiguration.InClusterConfig();
        }
        else
        {
            config = KubernetesClientConfiguration.BuildDefaultConfig();
        }

        return new Kubernetes(config);
    }
}
