import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';

@Component({
  selector: 'app-login-page',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './login-page.component.html',
  styleUrl: './login-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginPageComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly submitting = signal(false);
  protected readonly errorMessage = signal('');

  protected readonly form = {
    identifier: '',
    password: '',
    rememberMe: true,
  };

  protected async submit(): Promise<void> {
    this.errorMessage.set('');
    this.submitting.set(true);

    try {
      await this.auth.login(this.form);
      const redirectUrl = this.route.snapshot.queryParamMap.get('redirectUrl') || '/commercial';
      await this.router.navigateByUrl(redirectUrl);
    } catch (error) {
      this.errorMessage.set(error instanceof HttpErrorResponse && error.status === 401
        ? 'Credenciales invalidas.'
        : 'No se pudo iniciar sesion.');
    } finally {
      this.submitting.set(false);
    }
  }
}
