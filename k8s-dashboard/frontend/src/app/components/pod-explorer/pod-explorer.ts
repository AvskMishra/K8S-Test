import { Component, Input, OnChanges, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { K8sService } from '../../services/k8s';
import { K8sPod, K8sNamespace, K8sEvent } from '../../models/k8s.model';

type Tab = 'details' | 'logs' | 'exec';

@Component({
  selector: 'app-pod-explorer',
  imports: [FormsModule],
  templateUrl: './pod-explorer.html',
  styleUrl: './pod-explorer.scss'
})
export class PodExplorer implements OnChanges {
  @Input() clusterId = '';
  @Input() namespaces: K8sNamespace[] = [];

  private k8s = inject(K8sService);

  selectedNamespace = signal('');
  pods = signal<K8sPod[]>([]);
  selectedPod = signal<K8sPod | null>(null);
  podEvents = signal<K8sEvent[]>([]);
  loading = signal(false);
  error = signal<string | null>(null);

  activeTab = signal<Tab>('details');
  selectedContainer = signal('');
  tailLines = 100;
  logs = signal<string | null>(null);
  logsLoading = signal(false);

  execCommand = 'printenv';
  execOutput = signal<string | null>(null);
  execLoading = signal(false);

  ngOnChanges(): void {
    if (this.namespaces.length > 0 && !this.selectedNamespace()) {
      this.selectedNamespace.set(this.namespaces[0].metadata.name);
      this.loadPods();
    }
  }

  onNamespaceChange(ns: string): void {
    this.selectedNamespace.set(ns);
    this.selectedPod.set(null);
    this.loadPods();
  }

  loadPods(): void {
    if (!this.selectedNamespace()) return;
    this.loading.set(true);
    this.k8s.getPods(this.clusterId, this.selectedNamespace()).subscribe({
      next: (pods) => { this.pods.set(pods); this.loading.set(false); },
      error: () => { this.error.set('Failed to load pods.'); this.loading.set(false); }
    });
  }

  readyCount(pod: K8sPod): string {
    const statuses = pod.status?.containerStatuses ?? [];
    if (statuses.length === 0) return '0/0';
    return `${statuses.filter((s) => s.ready).length}/${statuses.length}`;
  }

  selectPod(pod: K8sPod): void {
    this.selectedPod.set(pod);
    this.activeTab.set('details');
    this.logs.set(null);
    this.execOutput.set(null);
    this.selectedContainer.set(pod.spec?.containers?.[0]?.name ?? '');

    this.k8s.getPodEvents(this.clusterId, this.selectedNamespace(), pod.metadata.name).subscribe({
      next: (events) => this.podEvents.set(events)
    });
  }

  fetchLogs(): void {
    const pod = this.selectedPod();
    if (!pod) return;
    this.logsLoading.set(true);
    this.k8s.getPodLogs(this.clusterId, this.selectedNamespace(), pod.metadata.name, this.selectedContainer(), this.tailLines)
      .subscribe({
        next: (res) => { this.logs.set(res.logs); this.logsLoading.set(false); },
        error: () => { this.logs.set('Failed to fetch logs.'); this.logsLoading.set(false); }
      });
  }

  runExec(): void {
    const pod = this.selectedPod();
    if (!pod || !this.execCommand.trim()) return;
    this.execLoading.set(true);
    this.k8s.execInPod(this.clusterId, this.selectedNamespace(), pod.metadata.name, this.selectedContainer(), this.execCommand)
      .subscribe({
        next: (res) => { this.execOutput.set(res.output); this.execLoading.set(false); },
        error: (err) => { this.execOutput.set(`Error: ${err.error?.error || err.message}`); this.execLoading.set(false); }
      });
  }
}
