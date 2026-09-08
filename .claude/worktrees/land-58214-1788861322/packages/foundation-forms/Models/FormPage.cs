namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// One wizard page/step of a paged form (F-14 — goal §10, 2026-07-01). Pages are
/// a PRESENTATION/NAVIGATION grain layered ABOVE sections: a page carries an
/// ordered list of section ids the same way a <see cref="FormSection"/> carries
/// ordered field names (parent lists children). The Rev-7 recursive item tree
/// below sections is untouched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariants (enforced fail-closed by <see cref="FormDefinitionValidation"/>
/// at registration):</b> page ids are unique and non-empty; every page carries at
/// least one section; every referenced section exists; when pages are declared,
/// every section is assigned to exactly one page.
/// </para>
/// <para>
/// <b>Back-compat is non-negotiable:</b> a pageless definition carries
/// <see langword="null"/> on <see cref="HarborlineOverlay.Pages"/> — byte-identical
/// on the wire to a pre-F-14 definition, rendered as today (one implicit page).
/// </para>
/// </remarks>
/// <param name="Id">Stable page id, unique within the overlay.</param>
/// <param name="Title">Localized page title (the wizard step label).</param>
/// <param name="Sections">Ordered section ids on this page (each must appear in
/// <see cref="HarborlineOverlay.Sections"/>).</param>
/// <param name="VisibleWhen">Optional SPINE-1 (shipyard-jsonlogic/v1) boolean
/// guard expression, JSON-stringified — the SAME expression language rules use
/// (ratified Decision 1: expression-only). A page whose guard evaluates false is
/// SKIPPED by wizard navigation; flow is always linear next/prev over the
/// currently-visible pages (navigation is NEVER a rule output). Fail-closed: an
/// erroring/pending guard hides the page.</param>
/// <param name="Checks">Optional page-level validation checks (F-20): ordered ids
/// of <see cref="RuleActionKind.Validate"/>-action rules in
/// <see cref="HarborlineOverlay.Rules"/>. The wizard gates Next/submit on this page
/// on every referenced rule's Validity being ok (fail-closed). Checks are RULES
/// (ratified Decision 1: expression-only) — this member only BINDS them to a page.
/// Admission rejects an unknown id or a non-Validate rule reference.</param>
public sealed record FormPage(
    string Id,
    InternationalizedText Title,
    IReadOnlyList<string> Sections,
    string? VisibleWhen = null,
    IReadOnlyList<string>? Checks = null);

/// <summary>
/// A lookup-backed asynchronous validation check (F-20) — config only, no
/// imperative code in the definition (the <see cref="OnSuccessConfig"/> precedent).
/// The CLIENT tier calls the named host-registered connector with the input field
/// values (debounced + cancellable while typing) and attaches the verdict to
/// <paramref name="Field"/>; at page-Next and final submit the check is
/// FAIL-CLOSED (failed ⇒ blocks with <paramref name="FailCode"/>; unreachable ⇒
/// <c>forms.check.unavailable</c>; pending ⇒ <c>forms.check.pending</c>). The node
/// validates this config fail-closed at admission
/// (<see cref="FormDefinitionValidation"/>); node-side connector re-execution at
/// submit is a documented follow-up.
/// </summary>
/// <param name="Id">Stable check id, unique within the overlay.</param>
/// <param name="Connector">Registry key of the host-registered connector.</param>
/// <param name="Field">The field name the verdict attaches to (must exist in
/// <see cref="HarborlineOverlay.Fields"/>).</param>
/// <param name="FailCode">Stable, locale-independent failure code surfaced on a
/// negative verdict (the client localizes off this).</param>
/// <param name="Inputs">Additional field names feeding the connector's input bag
/// (the <paramref name="Field"/> value is always included); each must exist in
/// <see cref="HarborlineOverlay.Fields"/>.</param>
/// <param name="DebounceMs">Debounce while typing, in milliseconds. Null ⇒ the
/// client default (400).</param>
/// <param name="AllowsSensitiveInputs">SPINE-2 (ADR 0140 D2 §6) explicit author
/// acknowledgment that this check MAY feed fields whose effective classification is a
/// class-required tag (<c>pii</c>/<c>identifier</c>/<c>phi</c>/<c>pci</c>/<c>cui</c>)
/// to its host connector. <see langword="false"/> (default) ⇒ the governance admission
/// validator REJECTS the definition fail-closed if the target or any input field
/// resolves to a sensitive class, so a tagged value is never silently handed to a
/// connector. Additive — absent ⇒ byte-identical to a pre-SPINE-2 check.</param>
public sealed record AsyncValidationCheck(
    string Id,
    string Connector,
    string Field,
    string FailCode,
    IReadOnlyList<string>? Inputs = null,
    int? DebounceMs = null,
    bool AllowsSensitiveInputs = false);

/// <summary>
/// Declarative on-success config (F-14) — CONFIG FIELDS ONLY, no imperative code
/// in the definition. The HOST consumes them after a successful final submit.
/// </summary>
/// <param name="RedirectUrl">Where the host navigates after success (optional).</param>
/// <param name="HostCallback">The name of a host-registered callback to invoke on
/// success (optional).</param>
public sealed record OnSuccessConfig(
    string? RedirectUrl = null,
    string? HostCallback = null);

/// <summary>
/// Wizard chrome settings for a paged form (F-14). All additive: a null
/// <see cref="HarborlineOverlay.Wizard"/> means the defaults (no review step; the
/// confirmation step shown with the renderer's default localized message).
/// </summary>
/// <param name="Review">Auto-generated read-only review step before the final
/// submit. Default <see langword="false"/>.</param>
/// <param name="Confirmation">Show the confirmation step after a successful
/// submit. Default <see langword="true"/>.</param>
/// <param name="ConfirmationMessage">Custom localized confirmation message;
/// null ⇒ the renderer's default string.</param>
/// <param name="OnSuccess">Declarative on-success behavior the host consumes.</param>
public sealed record WizardSettings(
    bool Review = false,
    bool Confirmation = true,
    InternationalizedText? ConfirmationMessage = null,
    OnSuccessConfig? OnSuccess = null);
