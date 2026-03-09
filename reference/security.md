# Security

This document covers security considerations for FHIR package management: authentication, transport security, package integrity, and cache protection.

## Transport Security

### HTTPS

All package registries and CI build servers use HTTPS:

| Endpoint | Protocol |
|----------|----------|
| `packages.fhir.org` | HTTPS |
| `packages2.fhir.org` | HTTPS |
| `build.fhir.org` | HTTPS |
| `hl7.org/fhir` | HTTPS (some legacy links use HTTP) |

**Certificate validation** should be enforced by default. Implementations may offer an `insecure` mode for development/testing environments (e.g., self-signed certificates), but this should be explicitly opt-in.

### Redirect Handling

When following HTTP redirects:

- Follow redirects but validate the destination URL
- Limit the number of redirects (recommended: maximum 5)
- Do not follow redirects from HTTPS to HTTP

## Authentication

### Registry Authentication

**FHIR registries** (`packages.fhir.org`, `packages2.fhir.org`) are currently public and do not require authentication for read operations.

**Custom/private registries** may require authentication:

| Implementation | Mechanism | Configuration |
|---------------|-----------|---------------|
| SUSHI | Bearer token | `FPL_REGISTRY_TOKEN` env var |
| Firely | HTTP client configuration | Custom `HttpClient` or `insecure` flag |
| CodeGen | Auth header + custom headers | `RegistryEndpointRecord.AuthHeaderValue` |
| Java Publisher | None built-in | — |

**Example: Configuring a private registry (SUSHI)**

```bash
export FPL_REGISTRY=https://my-private-registry.example.com
export FPL_REGISTRY_TOKEN=ghp_xxxxxxxxxxxxxxxxxxxx
```

**Example: Configuring a private registry (CodeGen)**

```csharp
var endpoint = new RegistryEndpointRecord
{
    Url = "https://my-private-registry.example.com/",
    RegistryType = RegistryEndpointRecord.RegistryTypeCodes.FhirNpm,
    AuthHeaderValue = "Bearer ghp_xxxxxxxxxxxxxxxxxxxx",
    CustomHeaders = new List<(string, string)>
    {
        ("X-Organization", "my-org")
    }
};
```

### Package Publish Authentication

Publishing packages requires authentication. The Firely implementation supports:

```csharp
var response = await client.Publish(reference, packageBuffer);
// Uses configured HttpClient with appropriate auth headers
```

## Package Integrity

### SHA Checksum Verification

Package registries include SHA checksums in version metadata:

```json
{
  "dist": {
    "shasum": "abc123def456...",
    "tarball": "https://packages.fhir.org/hl7.fhir.us.core/6.1.0"
  }
}
```

**Verification process:**

1. Download the tarball
2. Compute SHA-1 hash of the downloaded content
3. Compare against the `shasum` value from the registry
4. Reject the package if checksums don't match

**Implementation support:**

| Implementation | Checksum Verification |
|---------------|----------------------|
| SUSHI | Not implemented |
| Firely | ✅ `CheckSum` utility class |
| CodeGen | ✅ Via `ResolvedDirectiveUri.ShaSum` |
| Java Publisher | Not implemented |

### Post-Extraction Validation

After extracting a package, validate:

1. **Manifest present:** `package/package.json` must exist
2. **Name matches:** Manifest `name` should match the requested package
3. **Version matches:** Manifest `version` should match the resolved version
4. **Structure valid:** Package contents should be within the `package/` directory

## Server-Side Security

### Request Validation

The package server validates all incoming requests:

- **Parameter pollution detection:** Arrays in query parameters are rejected (HTTP 400)
- **Parameter validation:** Each parameter is checked for type, length, and pattern
- **Unknown parameters:** Rejected with HTTP 400

### URL Validation for External Requests

When the server fetches from external URLs (e.g., during crawling):

- **Protocol restriction:** Only `http://` and `https://` allowed
- **Private IP blocking:** Requests to private/internal IPs are blocked:
  - `127.0.0.0/8` (loopback)
  - `10.0.0.0/8` (private)
  - `192.168.0.0/16` (private)
  - `172.16.0.0/12` (private)
- **Size limits:** Content-Length checked against maximum (default: 50 MB)
- **Timeout protection:** Default 30-second timeout
- **Redirect limits:** Maximum 5 redirects

### Response Security Headers

All server responses include:

```http
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
X-XSS-Protection: 1; mode=block
Referrer-Policy: strict-origin-when-cross-origin
Content-Security-Policy: default-src 'self'; ...
```

The `X-Powered-By` header is removed.

## Cache Security

### File Permissions

The local package cache (`~/.fhir/packages/`) should be restricted to the current user:

```bash
# Linux/macOS
chmod 700 ~/.fhir/packages/
```

### No Credential Storage

Package cache directories should not contain credentials. Authentication tokens should be provided via:

- Environment variables (preferred)
- Configuration files outside the cache
- In-memory configuration

### Atomic Installation

Packages are extracted to a temporary directory first, then atomically moved to the cache. This prevents:

- Partial extraction (interrupted downloads)
- Race conditions (concurrent installations)
- Cache corruption

## Trust Model

```mermaid
flowchart TD
    subgraph Trusted["Trusted Sources"]
        A["packages.fhir.org<br/>(Firely — HL7 contract)"]
        B["packages2.fhir.org<br/>(HL7 managed)"]
        C["hl7.org/fhir<br/>(HL7 authoritative)"]
    end

    subgraph Semi["Semi-Trusted"]
        D["build.fhir.org<br/>(CI builds — not validated)"]
    end

    subgraph User["User-Configured"]
        E["Custom registries<br/>(user assumes responsibility)"]
        F["NPM registry<br/>(public, unvetted)"]
    end

    A --> G[Package Consumer]
    B --> G
    C --> G
    D --> G
    E --> G
    F --> G
```

**Trust levels:**

| Source | Trust Level | Validation |
|--------|------------|------------|
| Primary/Secondary registries | High | Official, curated |
| HL7 website | Highest | Authoritative publication |
| CI builds | Medium | Automated, may contain errors |
| Custom registries | User-dependent | User must vet |
| NPM registry | Low | Public, anyone can publish |

## Proxy Support

For environments behind corporate proxies:

| Implementation | Proxy Configuration |
|---------------|-------------------|
| SUSHI | `HTTPS_PROXY` environment variable |
| Firely | Custom `HttpClient` with proxy handler |
| CodeGen | Custom `HttpClient` with proxy handler |
| Java Publisher | JVM proxy settings |

```bash
# SUSHI proxy configuration
export HTTPS_PROXY=http://proxy.example.com:8080
```

## Certificate Handling

For environments with custom CA certificates:

```bash
# Node.js (SUSHI)
export NODE_EXTRA_CA_CERTS=/path/to/custom-ca.pem
```

For .NET implementations, configure the `HttpClient` with custom certificate handling or install certificates in the system trust store.
