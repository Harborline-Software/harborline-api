/**
 * WF-KEY declarative workflow contract (ADR 0140) — shape + parity smoke test.
 *
 * The .NET mirror is packages/blocks-workflow/src/durable/WorkflowDefinitionModel.cs; this
 * pins the TS authoring shape (states / transitions / triggers / actions + the CP/AP
 * classification) so a drift surfaces here. The canonical invoice-approval graph — the
 * shape the .NET admission validator admits — is the worked example.
 */
import { describe, it, expect } from 'vitest'

import type {
  ActionClassification,
  WorkflowActionKind,
  WorkflowDefinition,
  WorkflowTriggerKind,
} from '../workflow.js'
import { parseRecordStandingReference, parseRoleReference } from '../authorization-references.js'

const approverRole = parseRoleReference('workflow.roles/approver')
const handlerStanding = parseRecordStandingReference('handler')

function itext(en: string) {
  return { defaultLocale: 'en', values: { en } }
}

const invoiceApproval: WorkflowDefinition = {
  key: 'invoice-approval',
  version: '1.0.0',
  status: 'Draft',
  tenant: 'tenant:acme',
  owner: { scheme: 'user', value: 'tenant-admin' },
  title: itext('Invoice approval'),
  subjectFormRef: { formId: 'invoice.v1', version: '1.0.0' },
  mutability: 'Locked',
  initialState: 'Draft',
  states: [
    { id: 'Draft', label: itext('Draft'), kind: 'Normal' },
    { id: 'PendingApproval', label: itext('Pending approval'), kind: 'Normal', viewHint: 'flow' },
    { id: 'Posted', label: itext('Posted'), kind: 'Terminal' },
    { id: 'Rejected', label: itext('Rejected'), kind: 'Terminal' },
  ],
  triggers: [
    { id: 'issued', kind: 'Event', eventType: 'Issued' },
    { id: 'approve', kind: 'HumanAction', task: 'invoice-approval' },
    { id: 'reject', kind: 'HumanAction', task: 'invoice-approval' },
  ],
  transitions: [
    { id: 't-issue', from: 'Draft', on: 'issued', to: 'PendingApproval', guard: 'g-over-threshold' },
    {
      id: 't-approve', from: 'PendingApproval', on: 'approve', to: 'Posted',
      requiredRoles: [approverRole], requiredStandings: [handlerStanding],
    },
    { id: 't-reject', from: 'PendingApproval', on: 'reject', to: 'Rejected' },
  ],
  // The CP post-JE fires on the HUMAN approve transition — the human is the gate.
  actions: [
    {
      id: 'a-post-je',
      on: { transition: 't-approve' },
      kind: 'CreateRecord',
      capabilityRef: 'ledger.post-journal-entry',
      classification: 'CP',
      requiredRoles: [approverRole],
      requiredStandings: [handlerStanding],
    },
  ],
  guards: [
    {
      id: 'g-over-threshold',
      tier: 'JsonLogic',
      scope: 'Schema',
      scopeTarget: '',
      // Reads the form field AND process state through the ONE addressing grammar.
      expression: JSON.stringify({
        and: [
          { '>': [{ var: 'field.amount' }, 5000] },
          { '<': [{ var: 'wf.iteration' }, 3] },
        ],
      }),
      action: 'Validate',
    },
  ],
  createdAt: '2026-06-30T00:00:00.000Z',
  updatedAt: '2026-06-30T00:00:00.000Z',
}

describe('WorkflowDefinition contract', () => {
  it('models the canonical invoice-approval graph', () => {
    expect(invoiceApproval.initialState).toBe('Draft')
    expect(invoiceApproval.states.map((s) => s.id)).toEqual(['Draft', 'PendingApproval', 'Posted', 'Rejected'])
    expect(invoiceApproval.transitions).toHaveLength(3)
  })

  it('binds a subject form (ADR 0140 D3 composition)', () => {
    expect(invoiceApproval.subjectFormRef).toEqual({ formId: 'invoice.v1', version: '1.0.0' })
  })

  it('the CP post action fires on the human approve transition', () => {
    const post = invoiceApproval.actions.find((a) => a.id === 'a-post-je')!
    expect(post.classification).toBe('CP')
    expect(post.on).toEqual({ transition: 't-approve' })
    expect(post.requiredStandings).toEqual([handlerStanding])
  })

  it('parses role and standing lanes with distinct shape rules', () => {
    expect(approverRole).toBe('workflow.roles/approver')
    expect(handlerStanding).toBe('handler')
    expect(() => parseRoleReference('handler')).toThrow('authorization.gate_reference.required_roles_invalid')
    expect(() => parseRecordStandingReference('workflow.roles/approver')).toThrow(
      'authorization.gate_reference.required_standings_invalid',
    )
  })

  it('the four trigger kinds are the engine kinds (none net-new)', () => {
    const kinds: WorkflowTriggerKind[] = ['Event', 'Schedule', 'HumanAction', 'DependencyComplete']
    expect(new Set(invoiceApproval.triggers.map((t) => t.kind)).has('Event')).toBe(true)
    expect(kinds).toContain('HumanAction')
  })

  it('classification is the closed CP/AP set', () => {
    const cls: ActionClassification[] = ['CP', 'AP']
    expect(cls).toContain(invoiceApproval.actions[0].classification)
  })

  it('action kinds are the closed action vocabulary', () => {
    const kinds: WorkflowActionKind[] = [
      'Notify', 'CreateRecord', 'UpdateField', 'InvokeService', 'StartSubProcess', 'EmitEvent',
    ]
    expect(kinds).toContain(invoiceApproval.actions[0].kind)
  })
})
