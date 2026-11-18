import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', loadComponent: () => import('./queue/queue.page').then((m) => m.QueuePage), title: 'Record locking over SSE' },
  { path: '**', redirectTo: '' },
];
