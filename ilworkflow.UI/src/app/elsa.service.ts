import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

// DEV ONLY: mirrors ilworkflow.API appsettings.Development.json. Signing tokens in the browser
// exposes the key — in production a backend must issue the user token.
export const DEV_CONFIG = {
  adminUser: 'admin',
  adminPassword: 'password',
  signingKey: 'dev-only-user-token-signing-key-change-me-0123456789',
};

export interface ElsaUser { id: string; name: string; roles?: string[]; }
export type Scope = 'assigned' | 'available' | 'history' | 'all';

@Injectable({ providedIn: 'root' })
export class ElsaService {
  private http = inject(HttpClient);
  private adminToken?: string;

  private async admin(): Promise<string> {
    if (!this.adminToken) {
      const res = await firstValueFrom(this.http.post<{ accessToken: string }>('/elsa/api/identity/login',
        { username: DEV_CONFIG.adminUser, password: DEV_CONFIG.adminPassword }));
      this.adminToken = res.accessToken;
    }
    return this.adminToken;
  }

  async getUsers(): Promise<ElsaUser[]> {
    const headers = new HttpHeaders({ Authorization: `Bearer ${await this.admin()}` });
    const res = await firstValueFrom(this.http.get<{ users: ElsaUser[] }>('/elsa/api/identity/users', { headers }));
    return res.users ?? [];
  }

  /** Builds the HS256 "uid" token that UserTokenAuthentication accepts. */
  async createUserToken(user: ElsaUser): Promise<string> {
    const now = Math.floor(Date.now() / 1000);
    const enc = (o: object) => b64url(new TextEncoder().encode(JSON.stringify(o)));
    const unsigned = `${enc({ alg: 'HS256', typ: 'JWT' })}.${enc({ uid: user.id, name: user.name, iat: now, exp: now + 3600 })}`;
    const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(DEV_CONFIG.signingKey),
      { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
    const sig = await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(unsigned));
    return `${unsigned}.${b64url(new Uint8Array(sig))}`;
  }

  /** GET /api/tasks — scope: assigned | available | history | all. */
  async getTasks(token: string, scope: Scope, statusFilter = ''): Promise<any[]> {
    // "assigned" also returns completed history (paged); show only open work for the inbox tabs.
    // A chosen status chip overrides that default.
    const status = statusFilter ? '&status=' + statusFilter
      : scope === 'assigned' ? '&status=Assigned' : scope === 'available' ? '&status=Available' : '';
    const res = await firstValueFrom(this.http.get<any>(
      `/api/tasks?scope=${scope}${status}&include_total_count=true`, { headers: this.auth(token) }));
    return camel(unwrap(res)).items ?? [];
  }

  /** GET /api/tasks/{id} — detail with actions, allowed_actions, revision and data. */
  async getTask(token: string, id: string): Promise<any> {
    return camel(unwrap(await firstValueFrom(this.http.get<any>(`/api/tasks/${id}`, { headers: this.auth(token) }))));
  }

  /** GET /api/tasks/{id}/events — task history. */
  async getEvents(token: string, id: string): Promise<any[]> {
    const res = await firstValueFrom(this.http.get<any>(`/api/tasks/${id}/events?limit=50`, { headers: this.auth(token) }));
    return camel(unwrap(res)).items ?? [];
  }

  claimTask(token: string, task: any): Promise<any> { return this.command(token, task, 'claim'); }
  releaseTask(token: string, task: any): Promise<any> { return this.command(token, task, 'release'); }

  assignTask(token: string, task: any, userId: string, reason = ''): Promise<any> {
    return this.command(token, task, 'assign', { user_id: userId, ...(reason ? { reason } : {}) });
  }

  completeTask(token: string, task: any, actionKey: string, data: any = {}): Promise<any> {
    return this.command(token, task, 'complete', { action_key: actionKey, data });
  }

  /** Commands answer 202 (accepted) or 409 (conflict: reload and retry). */
  private command(token: string, task: any, name: string, body: object = {}): Promise<any> {
    return firstValueFrom(this.http.post(`/api/tasks/${task.id}/${name}`,
      { expected_revision: task.revision, operation_id: crypto.randomUUID(), ...body }, { headers: this.auth(token) }));
  }

  private auth(token: string) { return new HttpHeaders({ Authorization: `Bearer ${token}` }); }
}

/** /api/* responses are wrapped as { trace_id, messages, data }; return the inner payload. */
function unwrap(res: any): any {
  return res && typeof res === 'object' && 'trace_id' in res && 'data' in res ? res.data : res;
}

/** /api/* responses are snake_case; the components use camelCase. Ticket attributes (`data`) are left as-is. */
function camel(v: any): any {
  if (Array.isArray(v)) return v.map(camel);
  if (v && typeof v === 'object') {
    return Object.fromEntries(Object.entries(v).map(([k, val]) =>
      [k.replace(/_([a-z0-9])/g, (_, c) => c.toUpperCase()), k === 'data' ? val : camel(val)]));
  }
  return v;
}

function b64url(bytes: Uint8Array): string {
  let s = '';
  bytes.forEach(b => (s += String.fromCharCode(b)));
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
