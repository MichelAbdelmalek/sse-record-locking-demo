import { Injectable, inject, signal } from '@angular/core';
import { Subject } from 'rxjs';
import { APP_CONFIG } from './app-config';
import { ConnectionState, LiveEvent } from './models';

const NAME_KEY = 'lock-demo-name';

/**
 * One live SSE connection per browser tab.
 * The tab id is random per page load, so two tabs of the same browser are two different editors.
 * While the stream is open the API counts this tab as connected, which keeps its locks valid.
 */
@Injectable({ providedIn: 'root' })
export class TabSessionService {
  private readonly config = inject(APP_CONFIG);
  private source?: EventSource;

  readonly tabId = crypto.randomUUID();
  readonly name = signal(this.initialName());
  /** Public id the API gave this tab; used to recognise our own locks. */
  readonly publicId = signal<string | null>(null);
  readonly state = signal<ConnectionState>('connecting');
  readonly events = new Subject<LiveEvent>();

  connect(): void {
    this.source?.close();
    this.state.set('connecting');
    const url = `${this.config.apiUrl}/api/events?tabId=${this.tabId}&name=${encodeURIComponent(this.name())}`;
    const es = new EventSource(url);
    this.source = es;

    es.addEventListener('hello', (e) => {
      this.publicId.set(JSON.parse((e as MessageEvent).data).publicId);
      this.state.set('live');
    });
    for (const type of ['lock-changed', 'lock-lost', 'record-updated'] as const) {
      es.addEventListener(type, (e) => {
        const { recordId, data } = JSON.parse((e as MessageEvent).data);
        this.events.next({ type, recordId, data } as LiveEvent);
      });
    }
    // EventSource retries on its own with the same tab id, so locks survive a short drop.
    es.onerror = () => this.state.set('reconnecting');
  }

  rename(next: string): void {
    const clean = next.trim().slice(0, 40);
    if (!clean || clean === this.name()) return;
    this.name.set(clean);
    try { localStorage.setItem(NAME_KEY, clean); } catch { /* private mode */ }
    this.connect();
  }

  private initialName(): string {
    try {
      const saved = localStorage.getItem(NAME_KEY);
      if (saved) return saved;
    } catch { /* private mode */ }
    return `Reviewer ${Math.floor(10 + Math.random() * 90)}`;
  }
}
