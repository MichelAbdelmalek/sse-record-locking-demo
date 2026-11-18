import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ConfirmationService, MessageService } from 'primeng/api';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { InputTextModule } from 'primeng/inputtext';
import { DropdownModule } from 'primeng/dropdown';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { InputTextareaModule } from 'primeng/inputtextarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { LiveEvent, LockInfo, RecordStatus, WorkRecord } from '../core/models';
import { RecordsApi } from '../core/records-api.service';
import { TabSessionService } from '../core/tab-session.service';

/** What the editor panel is doing with the open record. */
type EditorMode = 'locking' | 'editing' | 'blocked' | 'lost';

interface Draft { title: string; status: RecordStatus; notes: string }

@Component({
  selector: 'app-queue-page',
  standalone: true,
  imports: [
    DatePipe, FormsModule, ButtonModule, ConfirmDialogModule, DropdownModule, InputTextModule,
    InputTextareaModule, TableModule, TagModule, ToastModule, TooltipModule,
  ],
  providers: [MessageService, ConfirmationService],
  templateUrl: './queue.page.html',
})
export class QueuePage {
  private readonly api = inject(RecordsApi);
  private readonly toast = inject(MessageService);
  private readonly confirm = inject(ConfirmationService);
  readonly tab = inject(TabSessionService);

  readonly statuses: { label: string; value: RecordStatus }[] = [
    { label: 'Pending', value: 'Pending' },
    { label: 'In review', value: 'InReview' },
    { label: 'Approved', value: 'Approved' },
    { label: 'Rejected', value: 'Rejected' },
  ];

  readonly records = signal<WorkRecord[]>([]);
  readonly cursor = signal<{ priority: number; id: number } | null>(null);
  readonly loading = signal(true);

  readonly openId = signal<number | null>(null);
  readonly mode = signal<EditorMode>('locking');
  readonly blockedBy = signal<LockInfo | null>(null);
  readonly lostTo = signal<string | null>(null);
  readonly saving = signal(false);
  draft: Draft = { title: '', status: 'Pending', notes: '' };

  readonly open = computed(() => this.records().find((r) => r.id === this.openId()) ?? null);
  readonly lockedCount = computed(() => this.records().filter((r) => r.lock).length);
  nameInput = this.tab.name();
  /** Ticks every 30 s so "held for" times stay current without a request. */
  readonly now = signal(Date.now());

  constructor() {
    const destroyRef = inject(DestroyRef);
    const clock = setInterval(() => this.now.set(Date.now()), 30_000);
    destroyRef.onDestroy(() => clearInterval(clock));
    this.tab.events.pipe(takeUntilDestroyed(destroyRef)).subscribe((e) => this.onLive(e));
    this.tab.connect();
    this.loadMore();
  }

  isMine(lock: LockInfo | null): boolean {
    return !!lock && lock.holderPublicId === this.tab.publicId();
  }

  heldFor(lock: LockInfo): string {
    const min = Math.floor((this.now() - Date.parse(lock.acquiredAt)) / 60_000);
    return min < 1 ? 'now' : `${min} min`;
  }

  statusLabel(s: RecordStatus): string {
    return this.statuses.find((x) => x.value === s)?.label ?? s;
  }

  statusSeverity(s: RecordStatus): 'secondary' | 'info' | 'success' | 'danger' {
    return s === 'InReview' ? 'info' : s === 'Approved' ? 'success' : s === 'Rejected' ? 'danger' : 'secondary';
  }

  loadMore(): void {
    this.loading.set(true);
    this.api.page(this.cursor() ?? undefined).subscribe({
      next: (page) => {
        this.records.update((list) => [...list, ...page.items]);
        this.cursor.set(page.next);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.add({ severity: 'error', summary: 'Could not load the queue', detail: 'The API may be waking up. Try again in a minute.' });
      },
    });
  }

  /** Opening a record asks for its lock straight away. */
  openRecord(rec: WorkRecord): void {
    if (this.openId() === rec.id) return;
    this.closeRecord();
    this.openId.set(rec.id);
    this.fillDraft(rec);
    this.requestLock(rec.id, false);
  }

  takeOver(): void {
    const rec = this.open();
    if (!rec) return;
    this.confirm.confirm({
      header: 'Take over this record?',
      message: `${this.blockedBy()?.holderName ?? 'The other editor'} will be told right away, and any changes they have not saved will be rejected.`,
      acceptLabel: 'Take over',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-secondary p-button-outlined',
      accept: () => this.requestLock(rec.id, true),
    });
  }

  save(): void {
    const rec = this.open();
    if (!rec || this.mode() !== 'editing') return;
    this.saving.set(true);
    this.api.save(rec.id, { ...this.draft, version: rec.version }).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.replace(saved);
        this.toast.add({ severity: 'success', summary: 'Saved', detail: saved.reference, life: 2500 });
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        if (err.status === 423) {
          this.mode.set('lost');
        } else if (err.status === 409 && err.error?.record) {
          this.replace(err.error.record);
          this.fillDraft(err.error.record);
          this.toast.add({ severity: 'warn', summary: 'Someone saved first', detail: 'The form now shows the latest version.' });
        } else {
          this.toast.add({ severity: 'error', summary: 'Save failed', detail: err.error?.detail ?? err.message });
        }
      },
    });
  }

  closeRecord(): void {
    const id = this.openId();
    if (id !== null && this.mode() === 'editing') this.api.release(id).subscribe({ error: () => {} });
    this.openId.set(null);
    this.blockedBy.set(null);
    this.lostTo.set(null);
  }

  /** Keyset walk to the next record nobody is editing, in queue order. */
  nextUnlocked(): void {
    const cur = this.open();
    this.api.nextUnlocked(cur ? { priority: cur.priority, id: cur.id } : undefined).subscribe((rec) => {
      if (!rec) {
        this.toast.add({ severity: 'info', summary: 'Nothing left', detail: 'Every remaining record is locked or done.' });
        return;
      }
      if (!this.records().some((r) => r.id === rec.id)) this.records.update((l) => [...l, rec]);
      this.openRecord(rec);
    });
  }

  openAnotherTab(): void {
    window.open(window.location.href, '_blank', 'noopener');
  }

  applyName(): void {
    this.tab.rename(this.nameInput);
    this.nameInput = this.tab.name();
  }

  private requestLock(id: number, takeover: boolean): void {
    this.mode.set('locking');
    this.api.lock(id, takeover).subscribe({
      next: (lock) => {
        if (this.openId() !== id) return;
        this.patchLock(id, lock);
        this.blockedBy.set(null);
        this.lostTo.set(null);
        this.mode.set('editing');
      },
      error: (err: HttpErrorResponse) => {
        if (this.openId() !== id) return;
        if (err.status === 409) {
          this.blockedBy.set(err.error?.lock ?? null);
          this.mode.set('blocked');
        } else if (err.status === 428) {
          // Our stream is not up yet (first load or reconnecting); try again shortly.
          setTimeout(() => this.openId() === id && this.requestLock(id, takeover), 1000);
        } else {
          this.toast.add({ severity: 'error', summary: 'Could not lock the record', detail: err.message });
        }
      },
    });
  }

  private onLive(e: LiveEvent): void {
    switch (e.type) {
      case 'lock-changed':
        this.patchLock(e.recordId, e.data);
        if (e.recordId === this.openId() && this.mode() === 'blocked' && !e.data) this.requestLock(e.recordId, false);
        else if (e.recordId === this.openId() && this.mode() === 'blocked') this.blockedBy.set(e.data);
        break;
      case 'lock-lost':
        if (e.recordId === this.openId()) {
          this.mode.set('lost');
          this.lostTo.set(e.data.byName);
        }
        this.toast.add({
          severity: 'warn', sticky: true, summary: 'You lost the lock',
          detail: `${e.data.byName} took over this record. Changes you had not saved cannot be saved now.`,
        });
        break;
      case 'record-updated':
        this.replace(e.data);
        if (e.recordId === this.openId() && this.mode() !== 'editing') this.fillDraft(e.data);
        break;
    }
  }

  private patchLock(id: number, lock: LockInfo | null): void {
    this.records.update((list) => list.map((r) => (r.id === id ? { ...r, lock } : r)));
  }

  private replace(rec: WorkRecord): void {
    this.records.update((list) => list.map((r) => (r.id === rec.id ? rec : r)));
  }

  private fillDraft(rec: WorkRecord): void {
    this.draft = { title: rec.title, status: rec.status, notes: rec.notes };
  }
}
