import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTableModule } from '@angular/material/table';
import { MatToolbarModule } from '@angular/material/toolbar';
import { firstValueFrom } from 'rxjs';
import { CrmApiService } from '../../crm-api.service';
import { User, Zone } from '../../models';

@Component({
  selector: 'app-users-zones-page',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatDividerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatTableModule,
    MatToolbarModule,
  ],
  templateUrl: './users-zones-page.component.html',
  styleUrl: './users-zones-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UsersZonesPageComponent {
  private readonly api = inject(CrmApiService);

  protected readonly loading = signal(true);
  protected readonly errorMessage = signal('');
  protected readonly zones = signal<Zone[]>([]);
  protected readonly users = signal<User[]>([]);
  protected readonly userPage = signal(1);
  protected readonly userTotal = signal(0);

  protected readonly zoneForm = signal({
    id: null as number | null,
    name: '',
    code: '',
    description: '',
    active: true,
  });

  protected readonly userForm = signal({
    id: null as number | null,
    loginName: '',
    fullName: '',
    email: '',
    role: 'seller',
    phone: '',
    active: true,
    zoneId: '',
    password: '',
    mustChangePassword: false,
  });

  constructor() {
    void this.load();
  }

  protected patchZoneForm<K extends keyof ReturnType<typeof this.zoneForm>>(field: K, value: ReturnType<typeof this.zoneForm>[K]): void {
    this.zoneForm.update((current) => ({ ...current, [field]: value }));
  }

  protected patchUserForm<K extends keyof ReturnType<typeof this.userForm>>(field: K, value: ReturnType<typeof this.userForm>[K]): void {
    this.userForm.update((current) => ({ ...current, [field]: value }));
  }

  protected editZone(zone: Zone): void {
    this.zoneForm.set({
      id: zone.id,
      name: zone.name,
      code: zone.code,
      description: zone.description ?? '',
      active: zone.active,
    });
  }

  protected editUser(user: User): void {
    this.userForm.set({
      id: user.id,
      loginName: user.loginName,
      fullName: user.fullName,
      email: user.email,
      role: user.role,
      phone: user.phone ?? '',
      active: user.active,
      zoneId: user.zoneId?.toString() ?? '',
      password: '',
      mustChangePassword: user.mustChangePassword,
    });
  }

  protected resetZoneForm(): void {
    this.zoneForm.set({
      id: null,
      name: '',
      code: '',
      description: '',
      active: true,
    });
  }

  protected resetUserForm(): void {
    this.userForm.set({
      id: null,
      loginName: '',
      fullName: '',
      email: '',
      role: 'seller',
      phone: '',
      active: true,
      zoneId: '',
      password: '',
      mustChangePassword: false,
    });
  }

  protected async saveZone(): Promise<void> {
    this.errorMessage.set('');
    const form = this.zoneForm();

    try {
      if (form.id) {
        await firstValueFrom(this.api.updateZone(form.id, form));
      } else {
        await firstValueFrom(this.api.createZone(form));
      }
      this.resetZoneForm();
      await this.load();
    } catch (error) {
      this.errorMessage.set(this.extractError(error));
    }
  }

  protected async saveUser(): Promise<void> {
    this.errorMessage.set('');
    const form = this.userForm();
    const payload = {
      loginName: form.loginName,
      fullName: form.fullName,
      email: form.email,
      role: form.role,
      phone: form.phone || null,
      active: form.active,
      zoneId: form.zoneId ? Number(form.zoneId) : null,
      password: form.password || null,
      mustChangePassword: form.mustChangePassword,
    };

    try {
      if (form.id) {
        await firstValueFrom(this.api.updateUser(form.id, payload));
      } else {
        await firstValueFrom(this.api.createUser(payload));
      }
      this.resetUserForm();
      await this.load();
    } catch (error) {
      this.errorMessage.set(this.extractError(error));
    }
  }

  protected async changeUserPage(nextPage: number): Promise<void> {
    if (nextPage < 1) {
      return;
    }

    this.userPage.set(nextPage);
    await this.loadUsers();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      await Promise.all([this.loadZones(), this.loadUsers()]);
    } finally {
      this.loading.set(false);
    }
  }

  private async loadZones(): Promise<void> {
    this.zones.set(await firstValueFrom(this.api.getZones()));
  }

  private async loadUsers(): Promise<void> {
    const response = await firstValueFrom(this.api.getUsers(this.userPage(), 12));
    this.users.set(response.items);
    this.userTotal.set(response.totalCount);
  }

  private extractError(error: unknown): string {
    if (error instanceof HttpErrorResponse && typeof error.error?.detail === 'string') {
      return error.error.detail;
    }

    return 'No se pudo guardar la informacion.';
  }
}
