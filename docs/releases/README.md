# Release evidence

Each published `fhir-pkg-lib` release gets a `<version>.md` evidence record
after NuGet.org finishes repository signing.

Record both package hashes:

- **Pre-upload SHA-256:** the exact unsigned `.nupkg` produced, qualified, and
  uploaded by the release workflow.
- **Published SHA-256:** the repository-signed `.nupkg` downloaded from
  NuGet.org. This is expected to differ from the pre-upload hash.

The evidence record also includes the release commit and tag, NuGet version and
feed, signature verification, package repository metadata, qualification
commands and reports, operating-system/framework results, corpus outcome, and
accepted limitations. Never rewrite the release tag to add post-publication
evidence.
