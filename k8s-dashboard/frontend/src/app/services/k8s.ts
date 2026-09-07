import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../api-config';
import { K8sNode, K8sNamespace, K8sPod, K8sDeployment, K8sService as K8sServiceResource, K8sEvent } from '../models/k8s.model';

// Every method is scoped by clusterId — the whole point of this service is
// that it works identically no matter which registered cluster you pass in.
@Injectable({ providedIn: 'root' })
export class K8sService {
  private http = inject(HttpClient);

  private base(clusterId: string) {
    return `${API_BASE_URL}/clusters/${clusterId}`;
  }

  getNodes(clusterId: string): Observable<K8sNode[]> {
    return this.http.get<K8sNode[]>(`${this.base(clusterId)}/nodes`);
  }

  getNodeEvents(clusterId: string, name: string): Observable<K8sEvent[]> {
    return this.http.get<K8sEvent[]>(`${this.base(clusterId)}/nodes/${name}/events`);
  }

  getNamespaces(clusterId: string): Observable<K8sNamespace[]> {
    return this.http.get<K8sNamespace[]>(`${this.base(clusterId)}/namespaces`);
  }

  getPods(clusterId: string, ns: string): Observable<K8sPod[]> {
    return this.http.get<K8sPod[]>(`${this.base(clusterId)}/namespaces/${ns}/pods`);
  }

  getPodEvents(clusterId: string, ns: string, pod: string): Observable<K8sEvent[]> {
    return this.http.get<K8sEvent[]>(`${this.base(clusterId)}/namespaces/${ns}/pods/${pod}/events`);
  }

  getPodLogs(clusterId: string, ns: string, pod: string, container: string, tailLines: number): Observable<{ logs: string }> {
    return this.http.get<{ logs: string }>(
      `${this.base(clusterId)}/namespaces/${ns}/pods/${pod}/logs?container=${encodeURIComponent(container)}&tailLines=${tailLines}`);
  }

  execInPod(clusterId: string, ns: string, pod: string, container: string, command: string): Observable<{ output: string }> {
    return this.http.post<{ output: string }>(
      `${this.base(clusterId)}/namespaces/${ns}/pods/${pod}/exec`, { container, command });
  }

  getDeployments(clusterId: string, ns: string): Observable<K8sDeployment[]> {
    return this.http.get<K8sDeployment[]>(`${this.base(clusterId)}/namespaces/${ns}/deployments`);
  }

  getServices(clusterId: string, ns: string): Observable<K8sServiceResource[]> {
    return this.http.get<K8sServiceResource[]>(`${this.base(clusterId)}/namespaces/${ns}/services`);
  }

  getEvents(clusterId: string, ns?: string): Observable<K8sEvent[]> {
    const query = ns ? `?ns=${encodeURIComponent(ns)}` : '';
    return this.http.get<K8sEvent[]>(`${this.base(clusterId)}/events${query}`);
  }
}
