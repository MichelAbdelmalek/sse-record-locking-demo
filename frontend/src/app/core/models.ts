export type RecordStatus = 'Pending' | 'InReview' | 'Approved' | 'Rejected';

export interface LockInfo {
  holderPublicId: string;
  holderName: string;
  acquiredAt: string;
}

export interface WorkRecord {
  id: number;
  reference: string;
  title: string;
  status: RecordStatus;
  priority: number;
  notes: string;
  updatedAt: string;
  updatedBy: string | null;
  version: number;
  lock: LockInfo | null;
}

export interface RecordPage {
  items: WorkRecord[];
  next: { priority: number; id: number } | null;
}

export interface RecordUpdate {
  title: string;
  status: RecordStatus;
  notes: string;
  version: number;
}

/** Events pushed by the API over SSE. */
export type LiveEvent =
  | { type: 'lock-changed'; recordId: number; data: LockInfo | null }
  | { type: 'lock-lost'; recordId: number; data: { byName: string } }
  | { type: 'record-updated'; recordId: number; data: WorkRecord };

export type ConnectionState = 'connecting' | 'live' | 'reconnecting';
