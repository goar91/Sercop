import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { CrmApiService } from '../../crm-api.service';
import { BulkInvitationImportResult, InvitationSyncResult } from '../../models';

@Component({
  selector: 'app-invitations-page',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './invitations-page.component.html',
  styleUrl: './invitations-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InvitationsPageComponent {
  private readonly api = inject(CrmApiService);

  protected readonly importing = signal(false);
  protected readonly syncing = signal(false);
  protected readonly importResult = signal<BulkInvitationImportResult | null>(null);
  protected readonly syncResult = signal<InvitationSyncResult | null>(null);

  protected readonly form = signal({
    codesText: '',
    invitationSource: 'manual_crm',
    invitationEvidenceUrl: '',
    invitationNotes: '',
  });

  protected patchForm<K extends keyof ReturnType<typeof this.form>>(field: K, value: ReturnType<typeof this.form>[K]): void {
    this.form.update((current) => ({ ...current, [field]: value }));
  }

  protected async importCodes(): Promise<void> {
    this.importing.set(true);
    try {
      this.importResult.set(await firstValueFrom(this.api.importInvitations(this.form())));
    } finally {
      this.importing.set(false);
    }
  }

  protected async syncPublicInvitations(): Promise<void> {
    this.syncing.set(true);
    try {
      this.syncResult.set(await firstValueFrom(this.api.syncInvitations()));
    } finally {
      this.syncing.set(false);
    }
  }
}
