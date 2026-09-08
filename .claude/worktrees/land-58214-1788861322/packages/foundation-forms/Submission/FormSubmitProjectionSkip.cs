namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// A <b>binding-declared capture a post-submit projection could NOT land</b> — surfaced up the submit
/// path so the route (and the user) learns the intended side record did not happen, instead of a silent
/// success-with-silence (ADR 0101 Rev 3.1 Wave 3a — deep-review gate <b>F3</b>).
/// </summary>
/// <remarks>
/// <para>
/// This does NOT change the server's skip-AND-audit semantics: a projection still writes its durable
/// <c>ProjectionSkipped</c> audit record and still returns gracefully (a skip is never a throw — the
/// submission committed). It merely ALSO <i>reports</i> the skip so the caller can honestly say "the
/// form saved, but the condition rating for field X was not recorded because Y." A genuine no-op — no
/// binding on the form, an unfilled field — is NOT a skip and is never reported (it belongs in neither
/// the audit trail nor the response).
/// </para>
/// <para>
/// <b>Value-free by contract.</b> The record carries only the stable machine <see cref="Reason"/> code,
/// the schema <see cref="FieldPointer"/> (metadata, not a submitted value), and an optional
/// <see cref="Target"/> the projection itself resolved — never a user-submitted value. The client maps
/// the reason code to a localized message.
/// </para>
/// </remarks>
/// <param name="Reason">A stable, projection-defined machine code (e.g. <c>grade-out-of-range</c>); the client localizes it.</param>
/// <param name="FieldPointer">The schema field pointer the skip concerns (so the user learns WHICH field).</param>
/// <param name="Target">An optional projection-resolved target reference; null when unknown/unresolved.</param>
public sealed record FormSubmitProjectionSkip(string Reason, string FieldPointer, string? Target = null);
