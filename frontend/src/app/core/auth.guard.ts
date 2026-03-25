import { CanActivateFn, CanMatchFn, Router } from '@angular/router';
import { inject } from '@angular/core';
import { AuthService } from './auth.service';

export const authGuard: CanActivateFn = async (_, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const user = await auth.ensureLoaded();

  if (user) {
    return true;
  }

  return router.createUrlTree(['/login'], { queryParams: { redirectUrl: state.url } });
};

export const guestGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const user = await auth.ensureLoaded();
  return user ? router.createUrlTree(['/commercial']) : true;
};

export const authMatchGuard: CanMatchFn = async () => {
  const auth = inject(AuthService);
  const user = await auth.ensureLoaded();
  return !!user;
};

export function roleGuard(...roles: string[]): CanActivateFn {
  return async () => {
    const auth = inject(AuthService);
    const router = inject(Router);
    const user = await auth.ensureLoaded();

    if (user && roles.includes(user.role)) {
      return true;
    }

    return router.createUrlTree(['/commercial']);
  };
}
