/**
 * Exponential backoff strategy for restart attempts.
 *
 * Purpose: Provides intelligent retry delays to avoid overwhelming
 *   failing processes while allowing quick recovery from transient issues.
 * Complexity: Simple exponential backoff with configurable parameters.
 */

export interface BackoffOptions {
  /** Initial delay in milliseconds. Default: 1000 */
  initialDelayMs?: number;
  /** Maximum delay in milliseconds. Default: 30000 */
  maxDelayMs?: number;
  /** Multiplier for each subsequent attempt. Default: 2 */
  multiplier?: number;
}

/**
 * Exponential backoff calculator for restart attempts.
 */
export class BackoffStrategy {
  private readonly initialDelayMs: number;
  private readonly maxDelayMs: number;
  private readonly multiplier: number;
  private attempt = 0;

  constructor(options: BackoffOptions = {}) {
    this.initialDelayMs = options.initialDelayMs ?? 1000;
    this.maxDelayMs = options.maxDelayMs ?? 30000;
    this.multiplier = options.multiplier ?? 2;
  }

  /**
   * Gets the delay for the next attempt and increments the attempt counter.
   * @returns Delay in milliseconds
   */
  nextDelay(): number {
    const delay = Math.min(
      this.initialDelayMs * Math.pow(this.multiplier, this.attempt),
      this.maxDelayMs
    );
    this.attempt++;
    return delay;
  }

  /**
   * Resets the attempt counter (call after successful connection).
   */
  reset(): void {
    this.attempt = 0;
  }

  /**
   * Current attempt number (0-based before first failure).
   */
  get attempts(): number {
    return this.attempt;
  }
}
