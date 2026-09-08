import type { CapabilityPortAdapterOptions } from './capability-port-adapter.js'
import type { MembranePrincipal, SecureInvokeOptions } from '../membrane/pep.js'
import type { CapabilityShell } from '../shell/capability-shell.js'

type Assert<T extends true> = T
/** Compile-time negative test: adapter options expose no execution-function seam. */
export type ExecutionPathCannotBeSupplied = Assert<
  Extract<keyof CapabilityPortAdapterOptions, 'securedInvoke' | 'invoke' | 'executor'> extends never
    ? true
    : false
>

export type PolicyCannotBeSupplied = Assert<
  Extract<keyof CapabilityPortAdapterOptions, 'policyDecisionPoint'> extends never ? true : false
>

export type TokenConsumerCannotBeSupplied = Assert<
  Extract<keyof SecureInvokeOptions, 'consumeToken'> extends never ? true : false
>

export type CredentialInputsCannotBeSupplied = Assert<
  Extract<keyof CapabilityPortAdapterOptions, 'principal' | 'confirmationBroker' | 'secureOptions'> extends never
    ? true
    : false
>

// These are host-only issuers. The public Capability entry point intentionally has no type-only escape
// hatch for importing them either.
// @ts-expect-error host principal minting is not part of the public Capability API.
import type { mintMembranePrincipal } from '../index.js'
// @ts-expect-error confirmation evidence issuance is not part of the public Capability API.
import type { TrustedConfirmationBroker } from '../index.js'
// @ts-expect-error node signing-key issuance is not part of the public Capability API.
import type { NodeSigningKey } from '../index.js'
// @ts-expect-error signed-principal issuance is not part of the public Capability API.
import type { signPrincipal } from '../index.js'
// @ts-expect-error the host OS principal reader is not part of the public Capability API.
import type { currentHostPrincipal } from '../index.js'

const forgedPrincipal = { id: 'os:caller-controlled' }
// @ts-expect-error A plain object must not satisfy the host-minted principal brand.
const rejectedPrincipal: MembranePrincipal = forgedPrincipal

declare const trustedShell: CapabilityShell
const rejectedAdapterOptions: CapabilityPortAdapterOptions = {
  shell: trustedShell,
  // @ts-expect-error Credential inputs are owned by the host composition root.
  principal: { id: 'os:caller-controlled' },
}

const rejectedPolicyOption: CapabilityPortAdapterOptions = {
  shell: trustedShell,
  // @ts-expect-error A caller cannot supply the removed policy function seam.
  policyDecisionPoint: (_command: string) => ({ authority: 'AP', summary: 'caller-controlled allow' }),
}

void (undefined as unknown as typeof mintMembranePrincipal)
void (undefined as unknown as typeof TrustedConfirmationBroker)
void (undefined as unknown as typeof NodeSigningKey)
void (undefined as unknown as typeof signPrincipal)
void (undefined as unknown as typeof currentHostPrincipal)
void rejectedPrincipal
void rejectedAdapterOptions
void rejectedPolicyOption
