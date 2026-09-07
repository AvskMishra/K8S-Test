export interface ClusterSummary {
  id: string;
  name: string;
  description?: string;
  createdAt: string;
}

export interface ClusterInput {
  name: string;
  description?: string;
  kubeconfigYaml: string;
}

export interface TestConnectionResult {
  reachable: boolean;
  namespaceCount?: number;
  error?: string;
}
