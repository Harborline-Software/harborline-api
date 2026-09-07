/**
 * Adapter from the existing Capability shell to the generated language-neutral CapabilityPort.
 *
 * The protocol owns serialized shapes; Capability continues to own behavior. In particular,
 * Invoke is composed inside the adapter so callers cannot inject a path around the membrane PEP.
 */

import type {
  AnnounceRequest,
  AnnounceResult,
  AddressRequest,
  AddressResult,
  CapabilityResult as ProtocolCapabilityResult,
  ComposeRequest,
  ComposeResult,
  HealthReport,
  CapabilityInvokeRequest,
  CapabilityPort,
  NegotiateRequest,
  NegotiateResult as ProtocolNegotiateResult,
  ObserveRequest,
  ResolveRequest,
  ResolutionResult as ProtocolResolutionResult,
  SecureRequest,
  SecureResult,
} from '@harborline-software/api-contracts/protocol'
import {
  assertOutboundProtocolModel,
  parseInboundProtocolModel,
} from '@harborline-software/api-contracts/protocol'
import type {
  CapabilityCore,
  CapabilityResult,
  InvokeRequest,
} from '@harborline-software/api-contracts'

import type { RuntimeConnection } from '../membrane/runtime-connection.js'
import {
  isContractVersionCompatible,
  negotiate as reconcileNegotiation,
} from '../membrane/negotiate.js'
import {
  secureInvoke,
} from '../membrane/pep.js'
import { currentHostPrincipal } from '../membrane/host-principal.js'
import { principalAttribution } from '../membrane/credential-boundary.js'
import type { InvokeDecisionSink } from '../membrane/pep.js'
import type { CapabilityShell } from '../shell/capability-shell.js'

declare const securedCapabilityInvokeBrand: unique symbol

/**
 * Compatibility type returned by `createSecuredCapabilityInvoke`.
 *
 * The brand prevents accidental assignment only. It is not a runtime security boundary; the
 * adapter owns enforcement by constructing its invoke closure from trusted collaborators.
 */
export type SecuredCapabilityInvoke = (
  (request: InvokeRequest) => Promise<CapabilityResult>
) & { readonly [securedCapabilityInvokeBrand]: true }

export interface SecuredCapabilityInvokeOptions {
  shell: CapabilityShell
  /** Optional decision sink; credential issuance remains inside the host composition root. */
  decisionSink?: InvokeDecisionSink
}

/**
 * Compatibility factory for callers that need the secured closure outside the port adapter.
 * It fixes the enforcement order as
 * authenticate -> authorize -> shell Invoke (whose membrane path owns idempotency and
 * redaction). `CapabilityShellPortAdapter` does not accept this function as an input.
 */
export function createSecuredCapabilityInvoke(options: SecuredCapabilityInvokeOptions): SecuredCapabilityInvoke {
  const shell = options.shell
  const shellInvoke = shell.invoke.bind(shell)
  const principal = currentHostPrincipal()
  // The shell is caller-supplied, so its invoke context reaches untrusted code. It gets INERT
  // attribution — the membrane only reads `.id` from it — never the credential, which the PEP
  // authenticates by object identity and would therefore accept back for any command.
  const attribution = principalAttribution(principal)
  const invoke = async (request: InvokeRequest): Promise<CapabilityResult> => secureInvoke(
    { command: 'invoke', capabilityId: request.capabilityId, request },
    principal,
    (securedRequest) => shellInvoke(securedRequest, { principal: attribution }),
    { decisionSink: options.decisionSink },
  )
  return invoke as SecuredCapabilityInvoke
}

export interface CapabilityPortAdapterOptions {
  shell: CapabilityShell
  /** Optional decision sink; the adapter owns host credential issuance. */
  decisionSink?: InvokeDecisionSink
  /** Optional policy preflight. Invoke enforcement is independently owned by the adapter. */
  inspectAuthority?: (request: SecureRequest) => SecureResult | Promise<SecureResult>
}

/** Generated-port implementation over the current TypeScript Capability runtime. */
export class CapabilityShellPortAdapter implements CapabilityPort {
  readonly #securedInvoke: SecuredCapabilityInvoke
  readonly #shell: CapabilityShell
  readonly #inspectAuthority: CapabilityPortAdapterOptions['inspectAuthority']

  constructor(options: CapabilityPortAdapterOptions) {
    this.#shell = options.shell
    this.#inspectAuthority = options.inspectAuthority
    this.#securedInvoke = createSecuredCapabilityInvoke(options)
  }

  async announce(request: AnnounceRequest): Promise<AnnounceResult> {
    const connection = this.requireConnection(request.runtimeId)
    return assertOutboundProtocolModel('AnnounceResult', 'capability.announce', {
      runtimeId: connection.manifest.runtimeId,
      name: connection.manifest.name,
      contractVersion: connection.manifest.contractVersion,
      capabilities: connection.manifest.capabilities.map((capability) => ({
        capabilityId: capability.capabilityId,
        schemaVersion: capability.schemaVersion,
        providers: capability.providers.map((provider) => ({ ...provider })),
      })) as AnnounceResult['capabilities'],
    })
  }

  async negotiate(request: NegotiateRequest): Promise<ProtocolNegotiateResult> {
    const declaration = parseInboundProtocolModel('NegotiateRequest', 'capability.negotiate', request)
    const connection = this.requireConnection(declaration.runtimeId)
    const callerNegotiation = reconcileNegotiation(connection.shellProfile, declaration)
    const invalidSchema = Object.entries(declaration.capabilitySchemaVersions)
      .find(([, schemaVersion]) => !isContractVersionCompatible(schemaVersion, schemaVersion))
    const compatible = connection.negotiation.compatible
      && callerNegotiation.compatible
      && invalidSchema === undefined
    const reason = !connection.negotiation.compatible
      ? connection.negotiation.reason
      : !callerNegotiation.compatible
        ? callerNegotiation.reason
        : invalidSchema === undefined
          ? null
          : `capability-schema-version invalid: capability '${invalidSchema[0]}' declared '${invalidSchema[1]}'`
    const callerCapabilities = new Set(callerNegotiation.acceptedCapabilities)
    const acceptedCapabilities = compatible
      ? connection.negotiation.acceptedCapabilities.filter((capabilityId) =>
        callerCapabilities.has(capabilityId),
      )
      : []
    return assertOutboundProtocolModel('NegotiateResult', 'capability.negotiate', {
      compatible,
      agreedContractVersion: connection.negotiation.agreedContractVersion,
      acceptedCapabilities,
      reason: reason ?? null,
    })
  }

  async address(request: AddressRequest): Promise<AddressResult> {
    const connection = this.requireConnection(request.runtimeId)
    return assertOutboundProtocolModel('AddressResult', 'capability.address', {
      runtimeId: connection.runtimeId,
      mode: connection.transport.mode,
    })
  }

  async secure(request: SecureRequest): Promise<SecureResult> {
    const result = this.#inspectAuthority?.(request) ?? {
      allowed: false,
      authority: 'CP',
      reason: 'No policy preflight adapter is configured; deny by default.',
    }
    return assertOutboundProtocolModel('SecureResult', 'capability.secure', await result)
  }

  async invoke(request: CapabilityInvokeRequest): Promise<ProtocolCapabilityResult> {
    const invokeRequest: InvokeRequest = {
      capabilityId: request.capability,
      core: request.core as unknown as CapabilityCore,
      providerInputs: {},
      attachments: [],
      idempotencyKey: request.idempotencyKey,
      correlationId: request.correlationId,
      transport: 'sync',
    }
    const result = await this.#securedInvoke(invokeRequest)
    return assertOutboundProtocolModel('CapabilityResult', 'capability.invoke', result)
  }

  async observe(request: ObserveRequest): Promise<HealthReport> {
    const connections = request.runtimeId
      ? [this.requireConnection(request.runtimeId)]
      : [...this.#shell.runtimes]
    const runtimes = await Promise.all(connections.map(async (connection) => {
      const probe = await this.#shell.probe(connection.runtimeId, request.kind)
      const capability = connection.negotiation.acceptedCapabilities[0] ?? 'unknown'
      return {
        runtimeId: connection.runtimeId,
        capability,
        state: probe.state,
        detail: probe.detail ?? null,
      }
    }))
    return assertOutboundProtocolModel('HealthReport', 'capability.observe', { runtimes })
  }

  async resolve(request: ResolveRequest): Promise<ProtocolResolutionResult> {
    const result = await this.#shell.resolve(request.capabilityId)
    return assertOutboundProtocolModel('ResolutionResult', 'capability.resolve', result)
  }

  async compose(request: ComposeRequest): Promise<ComposeResult> {
    const composition = this.#shell.composition
    const available = new Set(composition.capabilities
      .filter((entry) => entry.membership !== 'n-a')
      .map((entry) => entry.capabilityId))
    const editionMatches = request.editionId === composition.solutionId
    return assertOutboundProtocolModel('ComposeResult', 'capability.compose', {
      editionId: request.editionId,
      acceptedCapabilityIds: editionMatches
        ? request.capabilityIds.filter((id) => available.has(id))
        : [],
      rejectedCapabilityIds: editionMatches
        ? request.capabilityIds.filter((id) => !available.has(id))
        : [...request.capabilityIds],
    })
  }

  private requireConnection(runtimeId: string): RuntimeConnection {
    const connection = this.#shell.runtimes.find((item) => item.runtimeId === runtimeId)
    if (!connection) throw new Error(`CapabilityPort: runtime '${runtimeId}' is not connected`)
    return connection
  }
}

/**
 * Production host composition point. The host supplies only its shell; the trusted principal
 * and ADR 0128-backed policy evaluator are owned inside the Capability package.
 */
export function createProductionCapabilityPortAdapter(
  shell: CapabilityShell,
  inspectAuthority?: CapabilityPortAdapterOptions['inspectAuthority'],
): CapabilityShellPortAdapter {
  return new CapabilityShellPortAdapter({
    shell,
    inspectAuthority,
  })
}
