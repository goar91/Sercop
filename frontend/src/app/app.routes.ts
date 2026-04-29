import { Routes } from '@angular/router';
import { authGuard, commercialAllGuard, guestGuard, roleGuard } from './core/auth.guard';

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
        redirectTo: 'commercial/quimica',
      },
      {
        path: 'dashboard',
        loadComponent: () => import('./features/dashboard/dashboard-page.component').then((m) => m.DashboardPageComponent),
      },
      {
        path: 'commercial',
        pathMatch: 'full',
        redirectTo: 'commercial/quimica',
      },
      {
        path: 'commercial/quimica',
        loadComponent: () => import('./features/commercial/commercial-page.component').then((m) => m.CommercialPageComponent),
        data: { scope: 'chemistry' },
      },
      {
        path: 'commercial/todos',
        canActivate: [commercialAllGuard],
        loadComponent: () => import('./features/commercial/commercial-page.component').then((m) => m.CommercialPageComponent),
        data: { scope: 'all' },
      },
      {
        path: 'management',
        canActivate: [roleGuard('admin', 'gerencia')],
        loadComponent: () => import('./features/management/management-page.component').then((m) => m.ManagementPageComponent),
      },
      {
        path: 'operations',
        canActivate: [roleGuard('admin', 'coordinator')],
        loadComponent: () => import('./features/operations/operations-layout.component').then((m) => m.OperationsLayoutComponent),
        children: [
          {
            path: 'users-zones',
            canActivate: [roleGuard('admin')],
            loadComponent: () => import('./features/operations/users-zones-page.component').then((m) => m.UsersZonesPageComponent),
          },
          {
            path: 'invitations',
            canActivate: [roleGuard('admin')],
            loadComponent: () => import('./features/operations/invitations-page.component').then((m) => m.InvitationsPageComponent),
          },
          {
            path: 'keywords',
            canActivate: [roleGuard('admin', 'coordinator')],
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
