import { Routes } from '@angular/router';
import { ClusterList } from './components/cluster-list/cluster-list';
import { Dashboard } from './components/dashboard/dashboard';

export const routes: Routes = [
  { path: '', component: ClusterList },
  { path: 'clusters/:clusterId', component: Dashboard },
  { path: '**', redirectTo: '' }
];
