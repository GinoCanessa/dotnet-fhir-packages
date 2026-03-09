# CLI Documentation: `fhir-pkg`

The `fhir-pkg` command-line tool provides a terminal interface for managing FHIR packages. It wraps the `FhirPkg` library and supports all package management operations: installing, restoring, listing, searching, and cache management.

## Installation

```bash
# Install as a global .NET tool
dotnet tool install --global fhir-pkg

# Or install as a local project tool
dotnet tool install fhir-pkg
```

**Minimum Requirements:** .NET 9.0 Runtime

---

## Synopsis

```
fhir-pkg <command> [arguments] [options]
```

## Global Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--cache-path <path>` | `-c` | Override the local cache directory | `~/.fhir/packages` |
| `--verbose` | `-v` | Enable verbose (debug-level) logging | `false` |
| `--quiet` | `-q` | Suppress all non-error output | `false` |
| `--no-color` | | Disable colored output | `false` |
| `--json` | | Output results as JSON (machine-readable) | `false` |
| `--help` | `-h` | Show help for a command | |
| `--version` | | Show the tool version | |

---

## Commands

### `install`

Installs one or more FHIR packages into the local cache.

```
fhir-pkg install <packages...> [options]
```

**Arguments:**

| Argument | Description |
|----------|-------------|
| `<packages...>` | One or more package directives (name#version or name@version) |

**Options:**

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--with-dependencies` | `-d` | Also install transitive dependencies | `false` |
| `--overwrite` | | Re-download even if already cached | `false` |
| `--fhir-version <release>` | `-f` | Prefer a specific FHIR release (R4, R5, R6) | _(auto)_ |
| `--pre-release` | | Include pre-release versions in resolution | `true` |
| `--no-pre-release` | | Exclude pre-release versions | |
| `--registry <url>` | `-r` | Add a custom registry (can be repeated) | _(defaults)_ |
| `--auth <token>` | | Bearer token for the preceding `--registry` | |
| `--no-ci` | | Skip CI build registries | `false` |
| `--progress` | | Show download progress bars | `true` |

**Examples:**

```bash
# Install a specific version
fhir-pkg install hl7.fhir.us.core#6.1.0

# Install multiple packages
fhir-pkg install hl7.fhir.r4.core#4.0.1 hl7.fhir.r4.expansions#4.0.1

# Install latest version
fhir-pkg install hl7.fhir.us.core

# Install with all dependencies
fhir-pkg install hl7.fhir.us.core#6.1.0 --with-dependencies

# Install a CI build
fhir-pkg install hl7.fhir.us.core#current

# Install from a specific branch
fhir-pkg install hl7.fhir.us.core#current$R5

# Install using wildcard version
fhir-pkg install hl7.fhir.us.core#6.1.x

# Install from a private registry with auth
fhir-pkg install my-org.fhir.custom#1.0.0 \
  --registry https://my-registry.example.com \
  --auth "ghp_xxxxxxxxxxxxxxxxxxxx"

# Force re-download
fhir-pkg install hl7.fhir.us.core#6.1.0 --overwrite

# Install to a custom cache directory
fhir-pkg install hl7.fhir.us.core#6.1.0 --cache-path ./my-cache
```

**Output:**

```
Installing hl7.fhir.us.core#6.1.0...
  ✓ Resolved: hl7.fhir.us.core@6.1.0 from packages.fhir.org
  ✓ Downloaded: 1.6 MB
  ✓ Verified: SHA checksum OK
  ✓ Installed: ~/.fhir/packages/hl7.fhir.us.core#6.1.0

Installed 1 package.
```

**JSON Output (`--json`):**

```json
{
  "installed": [
    {
      "name": "hl7.fhir.us.core",
      "version": "6.1.0",
      "status": "Installed",
      "path": "~/.fhir/packages/hl7.fhir.us.core#6.1.0",
      "sizeBytes": 1677721,
      "source": "packages.fhir.org"
    }
  ],
  "failed": [],
  "totalInstalled": 1,
  "totalFailed": 0
}
```

---

### `restore`

Restores all dependencies declared in a project's `package.json` manifest. Performs full recursive dependency resolution and writes a lock file.

```
fhir-pkg restore [project-path] [options]
```

**Arguments:**

| Argument | Description |
|----------|-------------|
| `[project-path]` | Path to the project directory (default: current directory) |

**Options:**

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--lock-file` | `-l` | Path to the lock file | `fhirpkg.lock.json` |
| `--no-lock` | | Skip writing the lock file | `false` |
| `--conflict-strategy <strategy>` | | Version conflict strategy: `highest`, `first`, `error` | `highest` |
| `--max-depth <n>` | | Maximum dependency tree depth | `20` |
| `--fhir-version <release>` | `-f` | Prefer a specific FHIR release | _(auto)_ |

**Examples:**

```bash
# Restore current directory
fhir-pkg restore

# Restore a specific project
fhir-pkg restore ./my-ig

# Restore without writing a lock file
fhir-pkg restore --no-lock

# Restore with error on version conflicts
fhir-pkg restore --conflict-strategy error
```

**Output:**

```
Restoring dependencies for my-ig@0.1.0...
  ✓ hl7.fhir.r4.core@4.0.1 (cached)
  ✓ hl7.fhir.r4.expansions@4.0.1 (cached)
  ✓ hl7.fhir.uv.extensions.r4@1.0.0 (downloaded)
  ✓ hl7.fhir.us.core@6.1.0 (downloaded)

Restored 4 packages. 0 missing.
Lock file written: fhirpkg.lock.json
```

---

### `list`

Lists packages in the local cache.

```
fhir-pkg list [filter] [options]
```

**Arguments:**

| Argument | Description |
|----------|-------------|
| `[filter]` | Optional package ID prefix filter |

**Options:**

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--sort <field>` | `-s` | Sort by: `name`, `version`, `date`, `size` | `name` |
| `--show-size` | | Show package sizes | `false` |

**Examples:**

```bash
# List all cached packages
fhir-pkg list

# Filter by prefix
fhir-pkg list hl7.fhir.r4

# Show sizes and sort by size
fhir-pkg list --show-size --sort size

# Output as JSON
fhir-pkg list --json
```

**Output:**

```
Cached packages (6):

  hl7.fhir.r4.core#4.0.1
  hl7.fhir.r4.expansions#4.0.1
  hl7.fhir.us.core#6.1.0
  hl7.fhir.us.core#current
  hl7.fhir.uv.extensions.r4#1.0.0
  us.nlm.vsac#0.18.0
```

---

### `remove`

Removes one or more packages from the local cache.

```
fhir-pkg remove <packages...> [options]
```

**Arguments:**

| Argument | Description |
|----------|-------------|
| `<packages...>` | Package directives to remove |

**Options:**

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--force` | `-f` | Remove without confirmation | `false` |

**Examples:**

```bash
# Remove a specific package
fhir-pkg remove hl7.fhir.us.core#6.1.0

# Remove multiple packages
fhir-pkg remove hl7.fhir.us.core#6.1.0 hl7.fhir.us.core#current

# Remove without confirmation prompt
fhir-pkg remove hl7.fhir.us.core#6.1.0 --force
```

**Output:**

```
Remove hl7.fhir.us.core#6.1.0? [y/N] y
  ✓ Removed: hl7.fhir.us.core#6.1.0

Removed 1 package.
```

---

### `clean`

Removes all packages from the local cache.

```
fhir-pkg clean [options]
```

**Options:**

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--force` | `-f` | Clean without confirmation | `false` |
| `--ci-only` | | Remove only CI build packages (`#current`) | `false` |
| `--older-than <days>` | | Remove packages not accessed in N days | |

**Examples:**

```bash
# Clean entire cache
fhir-pkg clean

# Clean only CI builds
fhir-pkg clean --ci-only

# Clean packages older than 90 days
fhir-pkg clean --older-than 90

# Clean without confirmation
fhir-pkg clean --force
```

**Output:**

```
This will remove ALL 6 packages from ~/.fhir/packages.
Continue? [y/N] y
  ✓ Removed: hl7.fhir.r4.core#4.0.1
  ✓ Removed: hl7.fhir.r4.expansions#4.0.1
  ✓ Removed: hl7.fhir.us.core#6.1.0
  ✓ Removed: hl7.fhir.us.core#current
  ✓ Removed: hl7.fhir.uv.extensions.r4#1.0.0
  ✓ Removed: us.nlm.vsac#0.18.0

Removed 6 packages. Freed 156 MB.
```

---

### `search`

Searches package registries for packages matching criteria.

```
fhir-pkg search [options]
```

**Options:**

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--name <name>` | `-n` | Package name (prefix match) | |
| `--canonical <url>` | | Filter by canonical URL | |
| `--fhir-version <release>` | `-f` | Filter by FHIR version (R4, R5, R6) | |
| `--sort <field>` | `-s` | Sort: `name`, `date`, `-date`, `version` | `name` |
| `--limit <n>` | | Maximum results | `25` |
| `--registry <url>` | `-r` | Search a specific registry | _(all defaults)_ |

**Examples:**

```bash
# Search by name
fhir-pkg search --name hl7.fhir.us.core

# Search by FHIR version
fhir-pkg search --name hl7.fhir.us --fhir-version R4

# Search by canonical URL
fhir-pkg search --canonical http://hl7.org/fhir/us/core

# Sort by date, descending
fhir-pkg search --name hl7.fhir --sort -date --limit 10

# Output as JSON
fhir-pkg search --name hl7.fhir.us.core --json
```

**Output:**

```
Search results for "hl7.fhir.us.core" (5 packages):

  hl7.fhir.us.core           R4    8.0.1    HL7 FHIR Implementation Guide: US Core
  hl7.fhir.us.core.r4        R4    8.0.0    HL7 FHIR Implementation Guide: US Core
  hl7.fhir.us.core.v311      R4    3.1.1    (no description)
  hl7.fhir.us.core.v610      R4    6.1.0    (no description)
  hl7.fhir.us.core.v700      R4    7.0.0    (no description)
```

---

### `info`

Displays detailed information about a specific package.

```
fhir-pkg info <package> [options]
```

**Arguments:**

| Argument | Description |
|----------|-------------|
| `<package>` | Package name (shows all versions) or directive (shows specific version) |

**Options:**

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--versions` | | Show all available versions | `false` |
| `--dependencies` | | Show dependency tree | `false` |

**Examples:**

```bash
# Show package info
fhir-pkg info hl7.fhir.us.core

# Show all versions
fhir-pkg info hl7.fhir.us.core --versions

# Show info for specific version with dependency tree
fhir-pkg info hl7.fhir.us.core#6.1.0 --dependencies
```

**Output:**

```
hl7.fhir.us.core
  Description: HL7 FHIR Implementation Guide: US Core
  Latest:      8.0.1
  FHIR:        R4
  Canonical:   http://hl7.org/fhir/us/core
  License:     CC0-1.0

  Versions: 0.0.0, 1.0.0, 1.0.1, 2.0.0, 3.1.0, 3.1.1,
            4.0.0, 4.1.0, 5.0.0, 5.0.1, 6.0.0, 6.1.0,
            7.0.0, 7.0.0-ballot, 8.0.0, 8.0.1

  Cached:   6.1.0, current
```

---

### `resolve`

Resolves a directive to an exact version without installing.

```
fhir-pkg resolve <directive> [options]
```

**Examples:**

```bash
# Resolve latest
fhir-pkg resolve hl7.fhir.us.core

# Resolve wildcard
fhir-pkg resolve hl7.fhir.us.core#6.1.x

# Resolve CI build
fhir-pkg resolve hl7.fhir.us.core#current
```

**Output:**

```
hl7.fhir.us.core#6.1.x → 6.1.0
  Source:  packages.fhir.org
  Tarball: https://packages.simplifier.net/hl7.fhir.us.core/6.1.0
  SHA:     abc123def456...
```

---

### `publish`

Publishes a package tarball to a registry.

```
fhir-pkg publish <tarball> [options]
```

**Arguments:**

| Argument | Description |
|----------|-------------|
| `<tarball>` | Path to the `.tgz` package file |

**Options:**

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--registry <url>` | `-r` | Target registry URL | _(required)_ |
| `--auth <token>` | | Bearer token for authentication | _(required)_ |

**Examples:**

```bash
fhir-pkg publish ./output/my-ig.tgz \
  --registry https://packages.fhir.org \
  --auth "Bearer my-publish-token"
```

---

## Environment Variables

| Variable | Description | Overrides |
|----------|-------------|-----------|
| `FHIR_PACKAGE_CACHE` | Override default cache directory | `--cache-path` |
| `FHIR_REGISTRY` | Custom registry URL (prepended to default chain) | `--registry` |
| `FHIR_REGISTRY_TOKEN` | Bearer token for `FHIR_REGISTRY` | `--auth` |
| `HTTPS_PROXY` | HTTP proxy for all outbound requests | |
| `HTTP_PROXY` | HTTP proxy for non-TLS requests | |
| `NO_PROXY` | Comma-separated list of hosts to bypass proxy | |
| `FHIR_PKG_NO_CI` | Set to `1` to disable CI build resolution | `--no-ci` |
| `FHIR_PKG_VERBOSE` | Set to `1` for verbose logging | `--verbose` |

---

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Success |
| `1` | General error (see stderr for details) |
| `2` | Invalid arguments or options |
| `3` | Package not found |
| `4` | Network error (registry unreachable) |
| `5` | Checksum verification failed |
| `6` | Dependency resolution failure (missing required dependencies) |
| `7` | Cache error (permission denied, disk full) |
| `8` | Authentication error |

---

## Configuration File

The CLI optionally reads a `.fhir-pkg.json` configuration file from the current directory or home directory:

```json
{
  "cachePath": "~/.fhir/packages",
  "registries": [
    {
      "url": "https://my-private-registry.example.com/",
      "type": "FhirNpm",
      "auth": "Bearer my-token"
    }
  ],
  "includeCiBuilds": true,
  "verifyChecksums": true,
  "conflictStrategy": "highest",
  "maxDepth": 20
}
```

**Precedence (highest to lowest):**

1. Command-line options
2. Environment variables
3. `.fhir-pkg.json` in current directory
4. `.fhir-pkg.json` in home directory
5. Built-in defaults

---

## Shell Completions

```bash
# Generate completion scripts
fhir-pkg completions bash > ~/.fhir-pkg-completion.bash
fhir-pkg completions zsh > ~/.fhir-pkg-completion.zsh
fhir-pkg completions powershell > ~/.fhir-pkg-completion.ps1

# Enable (bash)
source ~/.fhir-pkg-completion.bash

# Enable (PowerShell)
. ~/.fhir-pkg-completion.ps1
```

---

## Common Workflows

### Setting up a new IG project

```bash
# Create project directory
mkdir my-ig && cd my-ig

# Create package.json
cat > package.json << 'EOF'
{
  "name": "my-org.fhir.us.my-ig",
  "version": "0.1.0",
  "fhirVersions": ["4.0.1"],
  "dependencies": {
    "hl7.fhir.r4.core": "4.0.1",
    "hl7.fhir.r4.expansions": "4.0.1",
    "hl7.fhir.us.core": "6.1.0"
  }
}
EOF

# Restore all dependencies
fhir-pkg restore

# Verify installation
fhir-pkg list
```

### Updating a dependency

```bash
# Check what's available
fhir-pkg info hl7.fhir.us.core --versions

# Install the new version
fhir-pkg install hl7.fhir.us.core#7.0.0 --with-dependencies

# Update package.json manually, then re-restore
fhir-pkg restore
```

### Working with CI builds

```bash
# Install the latest CI build
fhir-pkg install hl7.fhir.us.core#current

# Install a branch-specific CI build
fhir-pkg install hl7.fhir.us.core#current$R5

# Check if CI build is newer than cached
fhir-pkg resolve hl7.fhir.us.core#current

# Force refresh the CI build
fhir-pkg install hl7.fhir.us.core#current --overwrite
```

### Cache maintenance

```bash
# See what's in the cache
fhir-pkg list --show-size

# Remove stale CI builds
fhir-pkg clean --ci-only

# Free up space by removing old packages
fhir-pkg clean --older-than 180

# Nuclear option: clear everything
fhir-pkg clean --force
```
