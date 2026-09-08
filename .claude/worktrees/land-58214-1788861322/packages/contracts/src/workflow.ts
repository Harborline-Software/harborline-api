/**
 * Declarative workflow-definition contract (WF-KEY — the process keystone of
 * ADR 0140; a thin authoring shape over the ADR 0135 durable process engine).
 *
 * This is the process-half analog of `FormDefinition` (./forms.ts): the editable,
 * content-addressed TEMPLATE (FE-3's **Workflow**) that the visual workflow-builder
 * authors and the ADR 0135 engine instantiates into a **Process**
 * (`WorkflowInstanceRecord`). It is the data-driven authoring shape of a hand-written
 * typed `IWorkflowStepHandler` — states / guarded transitions / triggers / actions
 * as DATA, not C#.
 *
 * WF-KEY adds NO new runtime primitive and NO new authority primitive:
 *  - triggers map 1:1 onto the engine's existing `WorkflowTriggerKind`;
 *  - guards / action-conditions are `RuleDefinition`-shaped boolean expressions
 *    (SPINE-1 `shipyard-jsonlogic/v1`), evaluated by the rule engine's
 *    `IGuardEvaluator` — the SAME engine the form rules use, extended only with the
 *    `wf.` / `timer.` context-addressing prefixes (see @harborline-software/rule-engine grammar);
 *  - actions are guarded capability-calls carrying an authoring-declared CP / AP
 *    classification (ADR 0128) that drives the CP-park safety fence.
 *
 * THE SAFETY FENCE (ADR 0135 Amendment 2026-06-24, the deferred A1 interpreter gate).
 * This contract is PRECISELY the input schema the deferred data-driven interpreter
 * (A1) would consume. In WF-KEY v1 a `WorkflowDefinition` is AUTHORED, VALIDATED
 * (`validateWorkflowAdmission`, ./workflow-admission.ts — the authoring-time
 * CP-reachability gate), STORED and SIMULATED — it is NOT executed by a general
 * interpreter. Live execution stays gated behind A1 + the broker-PEP. The validator
 * is the authoring face of A1's R-1 load-time validator: it refuses the very graphs
 * R-1 would refuse to load, surfaced at design time instead of load time.
 *
 * Canonical source parity: the .NET mirror is
 * `packages/blocks-workflow/src/durable/WorkflowDefinitionModel.cs` (+ the matching
 * `WorkflowAdmissionValidator.cs`). Field names are camelCase to match the ASP.NET
 * Core serialiser the node host returns. A drift between the two is what
 * `__tests__/workflow.test.ts` pins.
 */

import type { IdentityRef, InternationalizedText, RuleDefinition } from './forms.js'
import type { RoleReference, RecordStandingReference } from './authorization-references.js'
export { parseRoleReference, parseRecordStandingReference } from './authorization-references.js'
export type { RoleReference, RecordStandingReference } from './authorization-references.js'

// -- Enums (closed; mirror the .NET enums) ------------------------------------

/** Lifecycle status of a `WorkflowDefinition` revision (`WorkflowDefinitionStatus`). */
export type WorkflowDefinitionStatus = 'Draft' | 'Published' | 'Deprecated' | 'Withdrawn'

/**
 * Editability posture of a definition (`Mutability`, ADR 0135 4.4). `Locked` is
 * rigid orchestration (replay always resolves against the pinned version);
 * `Editable` is a project-style definition re-pinned on each human edit.
 */
export type WorkflowMutability = 'Locked' | 'Editable'

/** A state's kind (`StateKind`). A `Terminal` state has no outgoing transitions. */
export type WorkflowStateKind = 'Normal' | 'Terminal'

/**
 * The lens a state suggests the renderer use (`ViewHint`, ADR 0140 D3 / 0135 4.1).
 * Presentation-only -- it does not affect execution or admission.
 */
export type WorkflowViewHint = 'flow' | 'gantt' | 'kanban'

/**
 * The four triggers that advance a process -- mapped 1:1 onto the engine's
 * `WorkflowTriggerKind` (ADR 0135 D1). WF-KEY adds NO trigger primitive.
 *
 * - `Event`              -- a domain / form-instance event (the EXTENDED rule-engine
 *                           event-bridge routes a subject-form lifecycle event here).
 * - `Schedule`           -- an RRULE / daemon occurrence (spawns a fresh per-period
 *                           instance, never an immortal loop, 0135 4.5).
 * - `HumanAction`        -- a typed result for a parked human-task (approve / reject /
 *                           send-back) -- the Ask-bar Inbox.
 * - `DependencyComplete` -- a prerequisite step / sub-process finished (the Gantt join).
 */
export type WorkflowTriggerKind = 'Event' | 'Schedule' | 'HumanAction' | 'DependencyComplete'

/**
 * What an action does (`WorkflowActionKind`, ADR 0140 -- the security-relevant
 * surface). `StartSubProcess` / `EmitEvent` are the orchestration / choreography
 * hand-offs DEFERRED with their 0135 slice-B counterparts (no consumer yet); they
 * are valid in the contract but admission treats them like any other action for
 * CP-reachability.
 */
export type WorkflowActionKind =
  | 'Notify'
  | 'CreateRecord'
  | 'UpdateField'
  | 'InvokeService'
  | 'StartSubProcess'
  | 'EmitEvent'

/**
 * The authoring-declared authority an action consumes (`ActionClassification`,
 * ADR 0128). `CP` (post to GL, send money, delete, touch a CP-locked field) MUST
 * interpose a human-task before firing under the 0135 v1 CP-park interim; `AP` may
 * run autonomously. An UNCLASSIFIED action is refused at admission (never
 * default-allow) -- that fail-closed default is the load-bearing invariant.
 */
export type ActionClassification = 'CP' | 'AP'

// -- Guard / action-condition reference (SPINE-1) -----------------------------

/**
 * A reference to a guard rule by id. The referenced `RuleDefinition` is a
 * `shipyard-jsonlogic/v1` boolean expression evaluated by the rule engine's
 * `IGuardEvaluator`. A transition with no guard always fires on its trigger; an
 * action with no condition always runs when reached.
 *
 * The guard expression may address the subject form's cells via the shared
 * `field.<name>` grammar AND the process context via the net-new `wf.state` /
 * `wf.actor` / `wf.iteration` / `timer.<id>` prefixes.
 */
export type GuardRef = string

/** Compatibility alias retained for callers that used the workflow-specific name. */
export type WorkflowRoleReference = RoleReference

// -- States / transitions / triggers / actions --------------------------------

/** A state an instance parks at / advances through (the engine's `CurrentStep`). */
export interface WorkflowState {
  /** Stable state id (matches `WorkflowInstanceRecord.CurrentStep`). */
  id: string
  /** Display label (chrome). */
  label: InternationalizedText
  /** `Terminal` => no outgoing transitions; `Normal` => must have >=1 outgoing. */
  kind: WorkflowStateKind
  /** Presentation lens hint; absent => `'flow'`. Does not affect admission. */
  viewHint?: WorkflowViewHint
}

/**
 * A guarded edge `{from, on, to}` (ADR 0135 D1 + SPINE-1). The declarative form of a
 * typed handler's `switch (trigger.Step)` decision. `guard` (optional) is evaluated
 * by `IGuardEvaluator`. A transition with no guard fires whenever its trigger arrives.
 */
export interface WorkflowTransition {
  id: string
  /** Source state id. */
  from: string
  /** The trigger that offers this transition (a `WorkflowTriggerBinding.id`). */
  on: string
  /** Destination state id. */
  to: string
  /** Optional guard rule id (a `RuleDefinition` in `guards`). */
  guard?: GuardRef
  /** Qualified role vocabulary names required by this transition. */
  requiredRoles?: WorkflowRoleReference[]
  /** Record-derived facts required by this transition; standings are not roles. */
  requiredStandings?: RecordStandingReference[]
}

/**
 * A trigger the definition declares against (ADR 0135 D1; reuses the four
 * `WorkflowTriggerKind` -- none net-new).
 */
export interface WorkflowTriggerBinding {
  id: string
  kind: WorkflowTriggerKind
  /** `Event` -- the subject-form/domain event type that fires this (e.g. `"Issued"`). */
  eventType?: string
  /** `Schedule` -- the RRULE the daemon expands (a fresh instance per occurrence). */
  rrule?: string
  /** `HumanAction` -- the parked human-task key whose typed result fires this. */
  task?: string
  /** `DependencyComplete` -- the prerequisite step / sub-process id. */
  dependency?: string
}

/**
 * Declarative effect parameters an action carries (`WorkflowEffectParams`). The (gated) A1
 * interpreter lowers these to the runtime `WorkflowEffect` closure (ADR 0126); in v1
 * they are authored + stored + shown in simulation, NOT lowered/executed.
 */
export type WorkflowEffectParams = Record<string, unknown>

/**
 * A guarded capability-call fired on entering a state or on a transition (ADR 0140 --
 * the security-relevant surface). Adds no new authority primitive: it names a guarded
 * capability + an authoring-declared CP / AP classification that drives the CP-park
 * fence. A `CreateRecord` / `UpdateField` action on the subject form is the
 * composition write-path (governed by the SPINE-2 Store-PEP at execute time).
 */
export interface WorkflowActionBinding {
  id: string
  /** Where the action fires -- on entering a state, or on a transition. */
  on: { state: string } | { transition: string }
  kind: WorkflowActionKind
  /** The guarded capability invoked (a Tool from the action palette). */
  capabilityRef: string
  /**
   * Authoring-declared authority class (ADR 0128). REQUIRED -- an action with no
   * classification is refused at admission (fail-closed, never default-allow).
   */
  classification: ActionClassification
  /** Optional `IGuardEvaluator` action-condition rule id ("should this run?"). */
  condition?: GuardRef
  /** Qualified role vocabulary names required by this action. */
  requiredRoles?: WorkflowRoleReference[]
  /** Record-derived facts required by this action; standings are not roles. */
  requiredStandings?: RecordStandingReference[]
  /** Declarative effect params (lowered to a `WorkflowEffect` by the gated A1 path). */
  params?: WorkflowEffectParams
}

/** Optional extension lineage -- this definition forks another. */
export interface WorkflowDefinitionLineage {
  parentDefinitionKey: string
  /** Canonical `"{major}.{minor}.{patch}"`. */
  parentVersion: string
}

/** A content-addressed reference to a `FormDefinition` (`FormDefinitionRef`). */
export interface FormDefinitionRef {
  /** The form definition id. */
  formId: string
  /** Canonical `"{major}.{minor}.{patch}"`. */
  version: string
}

/**
 * The canonical declarative workflow keystone (`WorkflowDefinition`) -- the process
 * analog of `FormDefinition`. The editable template the builder authors; the ADR 0135
 * engine instantiates it into a Process.
 */
export interface WorkflowDefinition {
  /** Definition key (matches `WorkflowInstanceRecord.DefinitionKey`). */
  key: string
  /** Canonical `"{major}.{minor}.{patch}"` -- the D7-pinned version. */
  version: string
  status: WorkflowDefinitionStatus
  /** Owning tenant id. */
  tenant: string
  owner: IdentityRef
  /** Display title (chrome). */
  title: InternationalizedText
  description?: InternationalizedText
  /**
   * The `FormDefinition` this process operates on (ADR 0140 D3 composition binding).
   * A guard reads its fields via the shared `field.<name>` grammar; a `CreateRecord`
   * / `UpdateField` action writes it via the Store-PEP.
   */
  subjectFormRef?: FormDefinitionRef
  /** Editability posture (ADR 0135 4.4). */
  mutability: WorkflowMutability
  /** The state new instances start in (must be a `states[].id`). */
  initialState: string
  states: WorkflowState[]
  transitions: WorkflowTransition[]
  triggers: WorkflowTriggerBinding[]
  actions: WorkflowActionBinding[]
  /**
   * The guard / action-condition rules referenced by `transitions[].guard` and
   * `actions[].condition` -- inlined here as data exactly as `FormDefinition` inlines
   * `overlay.rules`.
   */
  guards: RuleDefinition[]
  lineage?: WorkflowDefinitionLineage
  /** ISO-8601 UTC. */
  createdAt: string
  /** ISO-8601 UTC. */
  updatedAt: string
}
