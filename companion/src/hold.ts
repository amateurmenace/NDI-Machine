import { performance } from 'node:perf_hooks'
// Execute only on release: a lost USB release cannot trigger a delayed undo.
export class HoldGesture {
  private held?: {owner: string; at: number; prime: boolean}
  constructor(private now: () => number = () => performance.now()) {}
  down(owner: string, prime = false): void {
    if (!this.held || this.held.owner === owner || this.now() - this.held.at > 60000) this.held = {owner, at: this.now(), prime}
  }
  up(owner: string): 'toggle' | 'undo' | 'prime' | undefined {
    if (!this.held || this.held.owner !== owner) return undefined
    const elapsed = this.now() - this.held.at; const prime=this.held.prime; this.held = undefined
    if (elapsed < 0 || elapsed > 60000) return undefined
    return prime ? 'prime' : elapsed >= 3000 ? 'undo' : 'toggle'
  }
  get hint(): string { return !this.held ? '' : this.held.prime ? 'RELEASE TO PRIME' : this.now() - this.held.at >= 3000 ? 'RELEASE TO REVERT' : 'HOLD 3s TO REVERT' }
  reset(): void {this.held = undefined}
}
