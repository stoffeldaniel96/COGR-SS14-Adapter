# Source Provenance

This repository was created to extract reusable COGR-specific Space Station 14 adapter source from the complete `stoffeldaniel96/COGR-Station` integration fork.

## Initial extraction baseline

- Source repository: `stoffeldaniel96/COGR-Station`
- Source branch: `main`
- Source commit: `947b7462235f95bc3f9d48d834e6485af1557a91`
- Recorded SS14 integration base: `3aa6d7bf211fe7eaf7f3b9c96c455290db7fd29c`
- Recorded initial COGR adapter import in Station history: `5ebc4178304273578bd0d2ff0a7cb2b0dde2997d`
- Extraction date: August 30, 2026

The initial standalone repository does not claim ownership of the complete Station fork, its history, assets, or unrelated source.

## Initial adapter-owned source roots

The initial reusable-source audit starts from:

```text
Content.Server/COGR/
Content.Shared/COGR/
```

Those paths are mirrored into this repository under `overlay/` so their intended Station destination remains explicit.

These roots are COGR-specific integration areas, but provenance still applies file-by-file: code copied or substantially derived from upstream Station/Robust implementation retains the applicable upstream copyright/license notice.

## Material not imported by default

The following remain outside the adapter source unless explicitly classified later:

- complete SS14 source outside the declared COGR roots;
- `Resources/Maps/COGR/` and acceptance-map content;
- Station-wide configuration and launch presets;
- `lib/` bundled COGR assemblies;
- generated build artifacts;
- SS14 assets; and
- arbitrary scripts from the integration fork.

Adapter install/synchronization tooling may be moved or rewritten here when it is specifically reusable adapter tooling.

## Licensing

Original COGR adapter source in this repository is distributed under the repository MIT License.

The MIT License does not relicense:

- Apache-2.0 COGR runtime source or binaries;
- SS14/Robust third-party code under separate notices; or
- SS14 assets under Creative Commons or other asset-specific terms.

## Extraction history policy

The source commit above is the authoritative content provenance boundary for the first standalone extraction. Historical Station commits remain traceable in `COGR-Station`.

Where practical, later history-preserving migration may graft/filter the adapter-relevant Station history into this repository. Such history work must preserve this repository's clean ownership boundary and must not import unrelated Station source merely to retain commit topology.

Until that is done, every extracted snapshot/update must record the exact source or adapter revision so source identity is not inferred from timestamps or copied files.

## Updating provenance

When synchronized adapter source becomes canonical here, future changes should flow:

```text
COGR-SS14-Adapter commit
        ↓ deterministic synchronization
COGR-Station mirror
```

`COGR-Station` should record the exact adapter commit consumed by each synchronized integration update.
