import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CrmApiService } from '../crm-api.service';
import { CurrentUser, LoginRequest } from '../models';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(CrmApiService);
  private readonly router = inject(Router);

  private readonly currentUserState = signal<CurrentUser | null | undefined>(undefined);
  private readonly loadingState = signal(false);

  readonly currentUser = computed(() => this.currentUserState());
  readonly isAuthenticated = computed(() => !!this.currentUserState());
  readonly isReady = computed(() => this.currentUserState() !== undefined);
  readonly loading = computed(() => this.loadingState());

  async ensureLoaded(): Promise<CurrentUser | null> {
    if (this.currentUserState() !== undefined) {
      return this.currentUserState() ?? null;
    }

    this.loadingState.set(true);
    try {
      const user = await firstValueFrom(this.api.getCurrentUser());
      this.currentUserState.set(user);
      return user;
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        this.currentUserState.set(null);
        return null;
      }

      this.currentUserState.set(null);
      throw error;
    } finally {
      this.loadingState.set(false);
    }
  }

  async login(payload: LoginRequest): Promise<CurrentUser> {
    this.loadingState.set(true);
    try {
      const response = await firstValueFrom(this.api.login(payload));
      this.currentUserState.set(response.user);
      return response.user;
    } finally {
      this.loadingState.set(false);
    }
  }

  async logout(redirect = true): Promise<void> {
    try {
      await firstValueFrom(this.api.logout());
    } catch {
    } finally {
      this.currentUserState.set(null);
      if (redirect) {
        await this.router.navigateByUrl('/login');
      }
    }
  }

  hasAnyRole(...roles: string[]): boolean {
    const user = this.currentUserState();
    return !!user && roles.includes(user.role);
  }
}
