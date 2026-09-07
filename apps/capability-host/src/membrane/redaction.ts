/**
 * SEC-3 — redaction at the M3 normalization boundary (ADR 0124 council fold).
 *
 * "A token in a native MCP/CLI error leaks there" — so the membrane MUST redact
 * secrets out of any text/log it emits AT the M3 boundary, before that text
 * crosses into the shell's logs/traces. This generalizes the ADR 0112 invariant-7
 * (mandatory log redaction) fleet-wide, with a required redaction test
 * (ADR 0124 Part V "Logs — structured + mandatory redaction").
 *
 * The redactor is conservative: it scrubs (a) any value the caller registers as a
 * secret (exact-substring), and (b) common credential SHAPES (bearer tokens, API
 * keys, `key=`/`token=`/`secret=`/`password=` assignments, connection strings).
 *
 * `correlationId` is NOT a secret — it is propagated for tracing (ADR 0124 Part
 * IV.4) and SURVIVES redaction (the membrane redacts secrets, not the trace id).
 */

/** The replacement marker substituted for any redacted span. */
export const REDACTION_MARKER = '[REDACTED]'

/**
 * Credential-shape patterns scrubbed from any membrane-emitted text. Order does
 * not matter (each runs independently). These are deliberately broad — a false
 * redaction is harmless; a leaked token is not (SEC-3 is fail-closed in spirit).
 */
const CREDENTIAL_SHAPE_PATTERNS: readonly RegExp[] = [
  // Bearer / Authorization header values.
  /\bBearer\s+[A-Za-z0-9._\-+/=]{8,}/gi,
  // key=VALUE / token=VALUE / secret=VALUE / password=VALUE / apikey=VALUE
  // (also matches `"apiKey": "VALUE"` JSON-ish forms via the separator class).
  /\b(api[_-]?key|access[_-]?token|token|secret|password|passwd|pwd|client[_-]?secret)\b\s*[:=]\s*["']?[A-Za-z0-9._\-+/=]{4,}["']?/gi,
  // sk-/pk- style provider keys (OpenAI/Stripe-ish prefixes).
  /\b[sp]k[-_][A-Za-z0-9]{16,}/g,
  // Connection-string password fragments.
  /\bPassword=[^;"'\s]+/gi,
]

/** Options for {@link redact}. */
export interface RedactionOptions {
  /**
   * Caller-registered secret literals (a runtime's loopback bearer, an
   * operator-supplied connection token, etc.) scrubbed by exact substring.
   * Short/empty values are ignored (redacting a 1-char secret would scrub
   * everything).
   */
  secrets?: readonly string[]
}

/** Escape a literal for use inside a `RegExp`. */
function escapeRegExp(literal: string): string {
  return literal.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

/**
 * Redact secrets from a single text span (the M3-boundary scrub, SEC-3). Applies
 * registered secret-literals first, then the credential-shape patterns. Returns
 * the scrubbed text; never throws.
 */
export function redact(text: string, options: RedactionOptions = {}): string {
  let out = text

  for (const secret of options.secrets ?? []) {
    if (secret.length < 4) continue // too short to safely redact
    out = out.replace(new RegExp(escapeRegExp(secret), 'g'), REDACTION_MARKER)
  }

  for (const pattern of CREDENTIAL_SHAPE_PATTERNS) {
    out = out.replace(pattern, REDACTION_MARKER)
  }

  return out
}

/**
 * Deep-redact an arbitrary structured value (a normalized error message, a log
 * record, a native provider payload). Strings are scrubbed; objects/arrays are
 * walked. `correlationId` values are preserved verbatim (trace id, not a secret).
 * Returns a structurally-identical clone with secrets scrubbed.
 */
export function redactDeep<T>(value: T, options: RedactionOptions = {}): T {
  if (typeof value === 'string') {
    return redact(value, options) as unknown as T
  }
  if (Array.isArray(value)) {
    return value.map((v) => redactDeep(v, options)) as unknown as T
  }
  if (value !== null && typeof value === 'object') {
    const out: Record<string, unknown> = {}
    for (const [k, v] of Object.entries(value)) {
      // correlationId is a trace id, never a secret — preserve it verbatim so
      // a redacted log line is still correlatable (ADR 0124 Part IV.4 + V).
      out[k] = k === 'correlationId' ? v : redactDeep(v, options)
    }
    return out as unknown as T
  }
  return value
}
