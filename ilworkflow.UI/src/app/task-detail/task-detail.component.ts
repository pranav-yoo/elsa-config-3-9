import { Component, computed, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';

export interface TaskAction { key: string; label: string; }
export interface Decision { action: TaskAction; data: Record<string, unknown>; }

@Component({
  selector: 'app-task-detail',
  standalone: true,
  imports: [DatePipe, FormsModule],
  templateUrl: './task-detail.component.html',
  styleUrl: './task-detail.component.scss',
})
export class TaskDetailComponent {
  task = input.required<any>();
  busy = input(false);
  error = input('');
  events = input<any[]>([]);
  users = input<{ id: string; name: string }[]>([]);
  release = output<void>();
  assign = output<{ userId: string; reason: string }>();
  assignTo = '';
  assignReason = '';
  close = output<void>();
  submitted = output<Decision>();

  selected = signal<TaskAction | null>(null);
  text = '';
  formError = signal('');

  /** Buttons configured for the stage (task.actions). */
  actions = computed<TaskAction[]>(() => {
    const raw = this.task().actions ?? [];
    return (Array.isArray(raw) ? raw : []).map((a: any) =>
      typeof a === 'string' ? { key: a, label: a } : { key: a.key ?? a.actionKey, label: a.label ?? a.key });
  });

  dataEntries = computed(() => {
    const d = this.task().data;
    return d && typeof d === 'object' ? Object.entries(d) : [];
  });

  /** allowedActions holds task permissions (e.g. "release", "complete"); stage buttons need "complete". */
  canDo(a: TaskAction): boolean {
    const allowed = (this.task().allowedActions ?? []).map((x: any) =>
      String(typeof x === 'string' ? x : x.key ?? x.actionKey).toUpperCase());
    return allowed.includes('COMPLETE') || allowed.includes(a.key.toUpperCase());
  }

  has(permission: string): boolean {
    return (this.task().allowedActions ?? []).map((x: any) => String(x).toLowerCase()).includes(permission);
  }

  doAssign() {
    if (this.assignTo) this.assign.emit({ userId: this.assignTo, reason: this.assignReason.trim() });
  }

  eventText(e: any): string {
    return e.type ?? e.eventType ?? e.action ?? e.name ?? '';
  }

  kind(): 'approve' | 'reject' | 'resubmit' | 'other' | null {
    const k = this.selected()?.key.toUpperCase();
    if (!k) return null;
    return k === 'APPROVE' ? 'approve' : k === 'REJECT' ? 'reject' : k === 'RESUBMIT' ? 'resubmit' : 'other';
  }

  choose(a: TaskAction) {
    if (!this.canDo(a)) return;
    this.selected.set(a);
    this.formError.set('');
    this.text = this.kind() === 'resubmit' ? JSON.stringify(this.task().data ?? {}, null, 2) : '';
  }

  who(a: any): string {
    if (!a) return '—';
    return typeof a === 'string' ? a : a.displayName ?? a.name ?? a.id ?? '—';
  }

  display(v: unknown): string {
    return typeof v === 'object' ? JSON.stringify(v) : String(v);
  }

  /** Payload rules (server): APPROVE {comment?}, REJECT {reason}, RESUBMIT {attributes}, other {reason?}. */
  submit() {
    const action = this.selected();
    if (!action) return;
    const text = this.text.trim();
    let data: Record<string, unknown>;
    switch (this.kind()) {
      case 'reject':
        if (!text) { this.formError.set('A reason is required to reject.'); return; }
        data = { reason: text }; break;
      case 'resubmit':
        try {
          const attributes = JSON.parse(text || '{}');
          if (attributes === null || typeof attributes !== 'object' || Array.isArray(attributes)) throw new Error();
          data = { attributes };
        } catch { this.formError.set('Attributes must be a valid JSON object.'); return; }
        break;
      case 'approve': data = text ? { comment: text } : {}; break;
      default: data = text ? { reason: text } : {};
    }
    this.formError.set('');
    this.submitted.emit({ action, data });
  }
}
