import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ClusterSummary, ClusterInput, TestConnectionResult } from '../models/cluster.model';
import { API_BASE_URL } from '../api-config';

@Injectable({ providedIn: 'root' })
export class ClusterService {
  private http = inject(HttpClient);
  private baseUrl = `${API_BASE_URL}/clusters`;

  getAll(): Observable<ClusterSummary[]> {
    return this.http.get<ClusterSummary[]>(this.baseUrl);
  }

  create(input: ClusterInput): Observable<ClusterSummary> {
    return this.http.post<ClusterSummary>(this.baseUrl, input);
  }

  update(id: string, input: ClusterInput): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${id}`, input);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }

  testConnection(id: string): Observable<TestConnectionResult> {
    return this.http.get<TestConnectionResult>(`${this.baseUrl}/${id}/test`);
  }
}
