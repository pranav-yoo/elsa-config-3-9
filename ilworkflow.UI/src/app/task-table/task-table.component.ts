import { Component, input, output } from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';

@Component({
  selector: 'app-task-table',
  standalone: true,
  imports: [DatePipe, JsonPipe],
  templateUrl: './task-table.component.html',
  styleUrl: './task-table.component.scss',
})
export class TaskTableComponent {
  tasks = input.required<any[]>();
  busy = input(false);
  claim = output<any>();
  open = output<any>();

  isAvailable(t: any): boolean {
    return String(t.status ?? '').toLowerCase() === 'available';
  }

  isAssigned(t: any): boolean {
    return String(t.status ?? '').toLowerCase() === 'assigned';
  }

  who(a: any): string {
    if (!a) return '—';
    if (typeof a === 'string') return a;
    return a.displayName ?? a.name ?? a.id ?? '—';
  }
}
