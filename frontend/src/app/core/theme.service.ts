import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';

export type ThemeMode = 'system' | 'light' | 'dark';
export type EffectiveTheme = 'light' | 'dark';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly storageKey = 'crm_theme_mode';
  private mediaQuery: MediaQueryList | null = null;

  readonly mode = signal<ThemeMode>('system');
  readonly effectiveTheme = signal<EffectiveTheme>('light');

  init(): void {
    const stored = this.readStoredMode();
    this.mode.set(stored);

    if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
      this.mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
      this.attachMediaListener();
    }

    this.apply(this.mode());
  }

  setMode(mode: ThemeMode): void {
    const normalized = this.normalizeMode(mode);
    this.mode.set(normalized);
    this.persistMode(normalized);
    this.apply(normalized);
  }

  private attachMediaListener(): void {
    if (!this.mediaQuery) {
      return;
    }

    const handler = () => {
      if (this.mode() === 'system') {
        this.apply('system');
      }
    };

    try {
      this.mediaQuery.addEventListener('change', handler);
    } catch {
      // Safari / legacy
      // eslint-disable-next-line deprecation/deprecation
      this.mediaQuery.addListener(handler);
    }
  }

  private readStoredMode(): ThemeMode {
    try {
      const raw = window.localStorage.getItem(this.storageKey);
      return this.normalizeMode(raw);
    } catch {
      return 'system';
    }
  }

  private persistMode(mode: ThemeMode): void {
    try {
      window.localStorage.setItem(this.storageKey, mode);
    } catch {
      // ignore
    }
  }

  private normalizeMode(value: unknown): ThemeMode {
    const raw = String(value ?? '').trim().toLowerCase();
    if (raw === 'light' || raw === 'dark' || raw === 'system') {
      return raw;
    }

    return 'system';
  }

  private apply(mode: ThemeMode): void {
    const root = this.document.documentElement;
    const effective = this.resolveEffectiveTheme(mode);

    if (mode === 'system') {
      root.removeAttribute('data-theme');
    } else {
      root.setAttribute('data-theme', mode);
    }

    this.effectiveTheme.set(effective);
    this.updateMetaThemeColor(effective);
  }

  private resolveEffectiveTheme(mode: ThemeMode): EffectiveTheme {
    if (mode === 'dark') {
      return 'dark';
    }

    if (mode === 'light') {
      return 'light';
    }

    return this.mediaQuery?.matches ? 'dark' : 'light';
  }

  private updateMetaThemeColor(effective: EffectiveTheme): void {
    const meta = this.document.querySelector('meta[name="theme-color"]');
    if (!meta) {
      return;
    }

    meta.setAttribute('content', effective === 'dark' ? '#0b1220' : '#f6f7f9');
  }
}

