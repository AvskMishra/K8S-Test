import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import { K8sService } from '../../services/k8s';
import { K8sNode, K8sNamespace, K8sDeployment, K8sService as K8sSvc, K8sEvent } from '../../models/k8s.model';
import { PodExplorer } from '../pod-explorer/pod-explorer';

type Section = 'nodes' | 'namespaces' | 'pods' | 'deployments' | 'services' | 'events';

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, DatePipe, PodExplorer],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss'
})
export class Dashboard implements OnInit {
  private route = inject(ActivatedRoute);
  private k8s = inject(K8sService);

  clusterId = '';
  section = signal<Section>('nodes');
  loading = signal(false);
  error = signal<string | null>(null);

  namespaces = signal<K8sNamespace[]>([]);
  selectedNamespace = signal<string>('');

  nodes = signal<K8sNode[]>([]);
  selectedNode = signal<K8sNode | null>(null);
  nodeEvents = signal<K8sEvent[]>([]);

  deployments = signal<K8sDeployment[]>([]);
  services = signal<K8sSvc[]>([]);
  events = signal<K8sEvent[]>([]);

  ngOnInit(): void {
    this.clusterId = this.route.snapshot.paramMap.get('clusterId')!;
    this.loadNamespaces();
    this.selectSection('nodes');
  }

  private loadNamespaces(): void {
    this.k8s.getNamespaces(this.clusterId).subscribe({
      next: (ns) => {
        this.namespaces.set(ns);
        if (ns.length > 0) this.selectedNamespace.set(ns[0].metadata.name);
      }
    });
  }

  selectSection(section: Section): void {
    this.section.set(section);
    this.selectedNode.set(null);
    this.error.set(null);

    if (section === 'nodes') this.loadNodes();
    else if (section === 'deployments') this.loadDeployments();
    else if (section === 'services') this.loadServices();
    else if (section === 'events') this.loadEvents();
  }

  onNamespaceChange(ns: string): void {
    this.selectedNamespace.set(ns);
    if (this.section() === 'deployments') this.loadDeployments();
    else if (this.section() === 'services') this.loadServices();
  }

  private loadNodes(): void {
    this.loading.set(true);
    this.k8s.getNodes(this.clusterId).subscribe({
      next: (nodes) => { this.nodes.set(nodes); this.loading.set(false); },
      error: () => { this.error.set('Failed to load nodes.'); this.loading.set(false); }
    });
  }

  viewNodeDetails(node: K8sNode): void {
    this.selectedNode.set(node);
    this.k8s.getNodeEvents(this.clusterId, node.metadata.name).subscribe({
      next: (events) => this.nodeEvents.set(events)
    });
  }

  isNodeReady(node: K8sNode): boolean {
    return node.status?.conditions?.find((c) => c.type === 'Ready')?.status === 'True';
  }

  private loadDeployments(): void {
    if (!this.selectedNamespace()) return;
    this.loading.set(true);
    this.k8s.getDeployments(this.clusterId, this.selectedNamespace()).subscribe({
      next: (d) => { this.deployments.set(d); this.loading.set(false); },
      error: () => { this.error.set('Failed to load deployments.'); this.loading.set(false); }
    });
  }

  private loadServices(): void {
    if (!this.selectedNamespace()) return;
    this.loading.set(true);
    this.k8s.getServices(this.clusterId, this.selectedNamespace()).subscribe({
      next: (s) => { this.services.set(s); this.loading.set(false); },
      error: () => { this.error.set('Failed to load services.'); this.loading.set(false); }
    });
  }

  private loadEvents(): void {
    this.loading.set(true);
    this.k8s.getEvents(this.clusterId).subscribe({
      next: (e) => { this.events.set(e); this.loading.set(false); },
      error: () => { this.error.set('Failed to load events.'); this.loading.set(false); }
    });
  }
}
