// Loosely-typed views onto the Kubernetes API objects the backend passes
// straight through (it forwards the KubernetesClient's own deserialized
// objects as JSON) — only the fields this UI actually renders are declared.

export interface ObjectMeta {
  name: string;
  namespace?: string;
  creationTimestamp?: string;
  labels?: Record<string, string>;
}

export interface K8sNode {
  metadata: ObjectMeta;
  spec?: { taints?: { key: string; value?: string; effect: string }[] };
  status?: {
    conditions?: { type: string; status: string; reason?: string; message?: string }[];
    addresses?: { type: string; address: string }[];
    nodeInfo?: {
      kubeletVersion?: string;
      osImage?: string;
      kernelVersion?: string;
      containerRuntimeVersion?: string;
      architecture?: string;
    };
    capacity?: Record<string, string>;
    allocatable?: Record<string, string>;
  };
}

export interface K8sNamespace {
  metadata: ObjectMeta;
  status?: { phase?: string };
}

export interface ContainerStatus {
  name: string;
  ready: boolean;
  restartCount: number;
  state?: { running?: object; waiting?: { reason?: string }; terminated?: { reason?: string } };
}

export interface K8sPod {
  metadata: ObjectMeta;
  spec?: { nodeName?: string; containers: { name: string; image: string }[] };
  status?: { phase?: string; podIP?: string; containerStatuses?: ContainerStatus[] };
}

export interface K8sDeployment {
  metadata: ObjectMeta;
  status?: { replicas?: number; readyReplicas?: number; updatedReplicas?: number; availableReplicas?: number };
}

export interface K8sService {
  metadata: ObjectMeta;
  spec?: {
    type?: string;
    clusterIP?: string;
    ports?: { port: number; targetPort?: unknown; nodePort?: number; protocol?: string }[];
  };
}

export interface K8sEvent {
  metadata: ObjectMeta;
  type?: string;
  reason?: string;
  message?: string;
  count?: number;
  lastTimestamp?: string;
  involvedObject: { kind: string; name: string };
}
