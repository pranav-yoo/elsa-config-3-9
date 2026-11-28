import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ElsaService, ElsaUser, Scope } from './elsa.service';
import { UserSelectComponent } from './user-select/user-select.component';
import { TaskTableComponent } from './task-table/task-table.component';
import { Decision, TaskDetailComponent } from './task-detail/task-detail.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [UserSelectComponent, TaskTableComponent, TaskDetailComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
})
export class AppComponent implements OnInit {
  private elsa = inject(ElsaService);
  scopes: Scope[] = ['assigned', 'available', 'observed', 'supervised', 'history', 'all'];
  users = signal<ElsaUser[]>([]);
  selectedId = signal('');
  token = signal('');
  scope = signal<Scope>('assigned');
  statuses = ['Assigned', 'Available', 'Completing', 'Completed'];
  status = signal('');

  /** The supervised tab is only for admins (server: admin role or user-tasks:supervise). */
  isAdmin = computed(() => {
    const u = this.users().find(x => x.id === this.selectedId());
    return !!u?.roles?.some(r => ['admin', 'administrator'].includes(String(r).toLowerCase()));
  });
  visibleScopes = computed(() => this.scopes.filter(s => s !== 'supervised' || this.isAdmin()));
  tasks = signal<any[]>([]);
  loading = signal(false);
  busy = signal(false);
  error = signal('');
  detail = signal<any | null>(null);
  events = signal<any[]>([]);

  async ngOnInit() {
    try { this.users.set(await this.elsa.getUsers()); }
    catch (e) { this.fail('Could not load Elsa users (is the API running on :5000 in Development?)', e); }
  }

  async selectUser(id: string) {
    this.selectedId.set(id);
    this.tasks.set([]);
    this.detail.set(null);
    this.token.set('');
    if (this.scope() === 'supervised') this.scope.set('assigned');
    const user = this.users().find(u => u.id === id);
    if (!user) return;
    this.token.set(await this.elsa.createUserToken(user));
    await this.load();
  }

  setScope(s: Scope) { this.scope.set(s); this.load(); }

  /** Click the active status chip again to clear the filter. */
  setStatus(st: string) { this.status.set(this.status() === st ? '' : st); this.load(); }

  async load() {
    this.error.set('');
    this.loading.set(true);
    try { this.tasks.set(await this.elsa.getTasks(this.token(), this.scope(), this.status())); }
    catch (e) { this.tasks.set([]); this.fail(`Failed to load ${this.scope()} tasks`, e); }
    finally { this.loading.set(false); }
  }

  /** Claim a bucket task; it then moves to the assigned scope. */
  async claim(t: any) {
    this.busy.set(true);
    this.error.set('');
    try {
      await this.elsa.claimTask(this.token(), t);
      this.scope.set('assigned');
    } catch (e) { this.fail('Claim failed', e); }
    finally { this.busy.set(false); }
    const err = this.error();
    await this.load();
    if (err) this.error.set(err);
  }

  /** Assigned task: GET /user-tasks/{id} and show its actions in the detail panel. */
  async openTask(t: any) {
    this.busy.set(true);
    this.error.set('');
    try {
      this.detail.set(await this.elsa.getTask(this.token(), t.id));
      this.events.set(await this.elsa.getEvents(this.token(), t.id).catch(() => []));
    }
    catch (e) { this.fail('Could not load task', e); }
    finally { this.busy.set(false); }
  }

  /** Re-read for the latest revision, then POST /complete with the chosen action. */
  async submit(d: Decision) {
    const id = this.detail()?.id;
    if (!id) return;
    this.busy.set(true);
    this.error.set('');
    try {
      const fresh = await this.elsa.getTask(this.token(), id);
      await this.elsa.completeTask(this.token(), fresh, d.action.key, d.data);
      this.detail.set(null);
      await new Promise(r => setTimeout(r, 1200)); // 202: the next task appears shortly
    } catch (e) { this.fail(d.action.label + ' failed', e); }
    finally { this.busy.set(false); }
    const err = this.error();
    await this.load();
    if (err) this.error.set(err);
  }

  /** Release an owned task back to the bucket. */
  async release() {
    const id = this.detail()?.id;
    if (id) await this.finishCommand('Release failed', id, (t) => this.elsa.releaseTask(this.token(), t));
  }

  /** Supervisor / owner reassignment. */
  async assign(e: { userId: string; reason: string }) {
    const id = this.detail()?.id;
    if (id) await this.finishCommand('Assign failed', id, (t) => this.elsa.assignTask(this.token(), t, e.userId, e.reason));
  }

  private async finishCommand(msg: string, id: string, fn: (fresh: any) => Promise<any>) {
    this.busy.set(true);
    this.error.set('');
    try {
      await fn(await this.elsa.getTask(this.token(), id));
      this.detail.set(null);
      await new Promise(r => setTimeout(r, 1200));
    } catch (e) { this.fail(msg, e); }
    finally { this.busy.set(false); }
    const err = this.error();
    await this.load();
    if (err) this.error.set(err);
  }

  private fail(msg: string, e: any) {
    const m = e?.error?.messages?.[0];
    const detail = (typeof m === 'string' ? m : m?.message ?? m?.text) ?? e?.error?.message ?? e?.error?.detail ?? e?.message ?? '';
    this.error.set(`${msg}${e?.status ? ' [' + e.status + ']' : ''} ${detail}`);
  }
}
