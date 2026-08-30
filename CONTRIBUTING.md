# Contributing to COGR SS14 Adapter

Contributions are welcome when they preserve the adapter's environment boundary, source provenance, and Station-native authority.

## License of contributions

This repository is licensed under the MIT License. Unless explicitly stated otherwise, intentionally submitted contributions are provided under the repository's MIT License without a separate Contributor License Agreement or copyright assignment.

Do not submit code, assets, documentation, or generated material that you do not have the right to contribute under compatible terms.

## Before changing source

Read:

- `AGENTS.md`;
- `docs/ARCHITECTURE.md`;
- `docs/PROVENANCE.md`; and
- `adapter-manifest.json`.

If the issue is fundamentally cognitive rather than environmental, make the change in COGR instead of encoding a workaround in the adapter.

## Pull requests

A behavioral adapter change should normally include relevant tests and must preserve:

- Station authority over native world mechanics and physical outcomes;
- bounded actor-relative evidence;
- opaque-reference scope/lifecycle rules;
- runtime/adapter ownership boundaries;
- population/interleaving invariants; and
- explicit provenance for third-party or substantially derived material.

Changes that affect Station integration should be synchronized into the target `COGR-Station` checkout and built there before being described as integration-green.

## AI-assisted contributions

AI-assisted contributions are allowed when a human contributor reviews and owns the result. Disclose material AI assistance in the pull request. AI assistance does not relax licensing, provenance, architecture, test, security, or correctness requirements.

## Upstream-derived changes

The adapter necessarily uses SS14/Robust APIs, but do not copy arbitrary upstream implementation into this repository. If code is copied or substantially derived from upstream Station/Robust source, retain the applicable copyright/license notice and record the source in `docs/PROVENANCE.md` or a more specific provenance record.

## Security and configuration

Do not commit credentials, shared launch tokens, private endpoints, private logs, or machine-local secrets. Development defaults must fail safely and should not masquerade as deployable authentication configuration.
