import { expect, test } from '@playwright/test';

async function login(page: import('@playwright/test').Page) {
  const loginName = process.env.CRM_ADMIN_LOGIN;
  const password = process.env.CRM_AUTH_BOOTSTRAP_PASSWORD;

  if (!loginName || !password) {
    throw new Error('Faltan CRM_ADMIN_LOGIN o CRM_AUTH_BOOTSTRAP_PASSWORD para ejecutar Playwright.');
  }

  await page.goto('/login');
  const loginForm = page.locator('input[formcontrolname="identifier"]');
  const destination = page.waitForURL(/\/commercial\/quimica$/, { timeout: 5000 }).then(() => 'authenticated' as const).catch(() => null);
  const formReady = loginForm.waitFor({ state: 'visible', timeout: 5000 }).then(() => 'login' as const).catch(() => null);
  const state = await Promise.race([destination, formReady]);

  if (state === 'authenticated' || !/\/login(?:\?|$)/i.test(page.url())) {
    return;
  }

  await loginForm.fill(loginName);
  await page.locator('input[formcontrolname="password"]').fill(password);
  await page.getByRole('button', { name: /entrar al crm/i }).click();
  await page.waitForURL(/\/commercial\/quimica$/);
}

test('login y workspace comercial químico', async ({ page }) => {
  await login(page);

  await expect(page.getByRole('heading', { name: /tablero por antiguedad|tablero por estado comercial/i }).first()).toBeVisible();
  await expect(page.getByText(/SERCOP:/i)).toBeVisible();
  await expect(page.getByRole('button', { name: /todos/i }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: /ínfimas/i })).toBeVisible();
  await expect(page.getByRole('button', { name: /^nco$/i })).toBeVisible();
  await expect(page.getByRole('button', { name: /^sie$/i })).toBeVisible();
  await expect(page.getByRole('button', { name: /régimen especial/i })).toBeVisible();
});

test('módulo comercial todos muestra filtros avanzados', async ({ page }) => {
  await login(page);

  await page.goto('/commercial/todos');
  await expect(page).toHaveURL(/\/commercial\/todos$/);
  await expect(page.getByText(/palabras/i).first()).toBeVisible();
  await expect(page.getByText(/entidad/i).first()).toBeVisible();
  await expect(page.getByText(/proceso/i).first()).toBeVisible();
  await expect(page.getByText(/solo invitados hdm/i).first()).toBeVisible();
});
