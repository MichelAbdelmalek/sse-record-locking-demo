import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { APP_CONFIG } from './app-config';
import { LockInfo, RecordPage, RecordUpdate, WorkRecord } from './models';
import { TabSessionService } from './tab-session.service';

@Injectable({ providedIn: 'root' })
export class RecordsApi {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiUrl}/api/records`;
  private readonly tab = inject(TabSessionService);

  private get headers() {
    return new HttpHeaders({ 'X-Tab-Id': this.tab.tabId });
  }

  page(after?: { priority: number; id: number }, limit = 25) {
    const params: Record<string, number> = { limit };
    if (after) Object.assign(params, { afterPriority: after.priority, afterId: after.id });
    return this.http.get<RecordPage>(this.base, { params });
  }

  nextUnlocked(after?: { priority: number; id: number }) {
    const params: Record<string, number> = after ? { afterPriority: after.priority, afterId: after.id } : {};
    return this.http.get<WorkRecord | null>(`${this.base}/next-unlocked`, { params });
  }

  lock(id: number, takeover = false) {
    return this.http.post<LockInfo>(`${this.base}/${id}/lock`, { takeover }, { headers: this.headers });
  }

  release(id: number) {
    return this.http.delete<void>(`${this.base}/${id}/lock`, { headers: this.headers });
  }

  save(id: number, update: RecordUpdate) {
    return this.http.put<WorkRecord>(`${this.base}/${id}`, update, { headers: this.headers });
  }
}
