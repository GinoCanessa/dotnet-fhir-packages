# Version Resolution Policy

The package manager captures and validates its configuration when it is
constructed. Later mutations to the original `FhirPackageManagerOptions`
instance or its registry and fixup collections do not change that manager's
behavior.

## Configuration Validation

Invalid policy is rejected before cache or registry work begins. Validation
includes:

- positive HTTP timeout and redirect limits;
- `MaxParallelRegistryQueries` of at least 1;
- a non-negative resource-cache size;
- defined cache, safe-mode, and registry enum values;
- absolute HTTP/HTTPS registry URLs and trusted origins; and
- well-formed, non-duplicate version fixups.

Version-fixup keys use `<package>@<source-version>`. The final `@` is the
separator, so scoped names such as `@scope/package@1.0.0` are supported. Source
and target versions must both be concrete semantic versions.

The defaults correct both known core aliases:

```text
hl7.fhir.r4.core@4.0.0              -> 4.0.1
hl7.fhir.r4b.core@4.3.0-snapshot1   -> 4.3.0
```

Replacing `VersionFixups` with an empty dictionary disables configured version
rewrites. It does not disable package-name canonicalization or unconditional
`-cibuild` suffix removal.

## Candidate Selection

Registry clients and `VersionResolver` use the same selector for exact,
`latest`, wildcard, and range requests.

1. Invalid version keys are ignored.
2. Pre-release candidates are removed when `AllowPreRelease` is `false`,
   including explicitly requested pre-release versions.
3. When a preferred FHIR release is configured, candidates are filtered by
   `fhirVersion` (string or array), `fhirVersions`, and finally package-name
   inference when no explicit metadata exists.
4. Explicit missing, invalid, or incompatible FHIR metadata is rejected; package
   name inference never overrides it.
5. The request type is evaluated and the highest eligible match is selected.

If `dist-tags.latest` points to an ineligible candidate, resolution falls back
to the highest eligible version. The original registry dictionary key is
preserved in the result rather than reconstructed from the parsed semantic
version.

See [Versioning](../reference/versioning.md) for the supported range grammar and
FHIR-aware comparison rules.
