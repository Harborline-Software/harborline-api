# Harborline API

> **Status: pre-release.** Harborline is under active development and is not ready for production use. APIs, schemas, storage formats and package names change without notice, and there are no supported installs yet. Source is licensed under [Apache-2.0](LICENSE); see [NOTICE](NOTICE) and the [trademark policy](TRADEMARKS.md).

Harborline API provides governed programmatic access and composes Harborline runtime services. Human interfaces, integrations and automation submit commands and queries through interfaces that return authoritative authorization, validation and execution results. Operational outcomes should retain their supporting evidence and context. Enforcement remains independent of the human App and optional operator Toolbox.

For the shared product model and repository roles, read the [Harborline solution overview](https://github.com/Harborline-Software/harborline-app/blob/docs/solution-purpose/docs/solution-overview.md).

## Find the right boundary

| Location | Start here for |
|---|---|
| [src](src/) | Client-facing contracts, transport adapters and testing support. |
| [packages](packages/) | Runtime capabilities and their contracts. |
| [Local-node host](apps/local-node-host/README.md) | Running and composing the headless node. |
| [Capability host](apps/capability-host/README.md) | Host-capability composition and invocation. |
| [Operator CLI](apps/node-operator-cli/README.md) | Command-line operations. |

Use the relevant project files for dependency and package identities, and the host guides for configuration. Development fixtures provide isolated test behavior; their availability does not establish production integration.

## Verify

Use the SDK selected by [global.json](global.json). Run the repository gate from a clean committed tree in a Bash environment:

```sh
bash eng/verify.sh
```

The [gate script](eng/verify.sh) defines the checks and prerequisites for this checkout. For a focused .NET test run:

```sh
dotnet test Harborline.Api.slnx
```

A focused test run covers only its selected projects. Consult [.github/workflows](.github/workflows/) for automation and [CONTRIBUTING.md](CONTRIBUTING.md) for change requirements. Where hooks are used, their installation is local to the clone; inspect the configured hooks rather than assuming a clone has enabled them.

## Packages

[Repository metadata](repository.yaml), project files and publishing workflows record package identities and release conditions. Check those sources before publishing or selecting a dependency. Consumers should depend on supported contracts and published artifacts according to the boundary checks, keeping repository layout out of runtime interfaces.

For usage questions and bug reports, see [SUPPORT.md](SUPPORT.md). Report sensitive vulnerabilities through [SECURITY.md](SECURITY.md).

For installation, diagnostics and recovery preparation, use the [operator guide](docs/operations/first-release-operator-guide.md).

