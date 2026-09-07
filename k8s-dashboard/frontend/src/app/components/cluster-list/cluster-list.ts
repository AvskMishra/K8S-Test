import { Component, OnInit, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { DatePipe } from '@angular/common';
import { ClusterService } from '../../services/cluster';
import { ClusterSummary } from '../../models/cluster.model';

@Component({
  selector: 'app-cluster-list',
  imports: [ReactiveFormsModule, DatePipe],
  templateUrl: './cluster-list.html',
  styleUrl: './cluster-list.scss'
})
export class ClusterList implements OnInit {
  private clusterService = inject(ClusterService);
  private fb = inject(FormBuilder);
  private router = inject(Router);

  clusters = signal<ClusterSummary[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);
  showAddForm = signal(false);
  testResult = signal<string | null>(null);
  testing = signal(false);
  saving = signal(false);

  form = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    description: [''],
    kubeconfigYaml: ['', [Validators.required]]
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.clusterService.getAll().subscribe({
      next: (clusters) => {
        this.clusters.set(clusters);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Could not reach the dashboard API. Is it running?');
        this.loading.set(false);
      }
    });
  }

  openDashboard(cluster: ClusterSummary): void {
    this.router.navigate(['/clusters', cluster.id]);
  }

  removeCluster(cluster: ClusterSummary, event: Event): void {
    event.stopPropagation();
    if (!confirm(`Remove cluster "${cluster.name}" from the dashboard? (Does not affect the actual cluster.)`)) return;
    this.clusterService.delete(cluster.id).subscribe({
      next: () => this.clusters.update((list) => list.filter((c) => c.id !== cluster.id))
    });
  }

  testPastedConfig(): void {
    // Register a throwaway-less flow isn't possible pre-save (test needs a
    // stored id), so this validates via a quick save+test+(keep or nothing
    // extra to clean up — a failed connection is still a legitimately
    // "registered but unreachable" cluster the user can fix later).
    this.submit(true);
  }

  submit(testAfterSave = false): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    this.testResult.set(null);
    const value = this.form.getRawValue();

    this.clusterService.create({
      name: value.name!,
      description: value.description || undefined,
      kubeconfigYaml: value.kubeconfigYaml!
    }).subscribe({
      next: (created) => {
        this.saving.set(false);
        this.clusters.update((list) => [...list, created]);
        this.form.reset();
        this.showAddForm.set(false);

        if (testAfterSave) {
          this.testing.set(true);
          this.clusterService.testConnection(created.id).subscribe({
            next: (result) => {
              this.testing.set(false);
              this.testResult.set(result.reachable
                ? `Connected to "${created.name}" — ${result.namespaceCount} namespaces found.`
                : `Saved, but could not connect: ${result.error}`);
            },
            error: () => this.testing.set(false)
          });
        }
      },
      error: () => {
        this.saving.set(false);
        this.error.set('Failed to register cluster.');
      }
    });
  }
}
