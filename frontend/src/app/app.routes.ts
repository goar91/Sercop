import { Routes } from '@angular/router';
import { authGuard, guestGuard, roleGuard } from './core/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [guestGuard],
    loadComponent: () => import('./features/login/login-page.component').then((m) => m.LoginPageComponent),
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./layout/app-shell.component').then((m) => m.AppShellComponent),
    children: [
      {
        path: '',
        pathMatch: 'full',
        redirectTo: 'commercial',
      },
      {
        path: 'dashboard',
        loadComponent: () => import('./features/dashboard/dashboard-page.component').then((m) => m.DashboardPageComponent),
      },
      {
        path: 'commercial',
        loadComponent: () => import('./features/commercial/commercial-page.component').then((m) => m.CommercialPageComponent),
      },
      {
        path: 'management',
        canActivate: [roleGuard('admin', 'gerencia')],
        loadComponent: () => import('./features/management/management-page.component').then((m) => m.ManagementPageComponent),
      },
      {
        path: 'operations',
        canActivate: [roleGuard('admin')],
        loadComponent: () => import('./features/operations/operations-layout.component').then((m) => m.OperationsLayoutComponent),
        children: [
          {
            path: '',
            pathMatch: 'full',
            redirectTo: 'users-zones',
          },
          {
            path: 'users-zones',
            loadComponent: () => import('./features/operations/users-zones-page.component').then((m) => m.UsersZonesPageComponent),
          },
          {
            path: 'invitations',
            loadComponent: () => import('./features/operations/invitations-page.component').then((m) => m.InvitationsPageComponent),
          },
          {
            path: 'keywords',
            loadComponent: () => import('./features/operations/keywords-page.component').then((m) => m.KeywordsPageComponent),
          },
        ],
      },
      {
        path: 'automation',
        canActivate: [roleGuard('admin', 'analyst')],
        loadComponent: () => import('./features/automation/workflows-page.component').then((m) => m.WorkflowsPageComponent),
      },
    ],
  },
  {
    path: '**',
    redirectTo: '',
  },
];
