# Isolated S3 provider fixture

`../../build-provider-images.ps1` prepares the reviewed PGSTY Silo binary from
an immutable Linux amd64 image. Its single additional filesystem layer only
creates/chowns `/data`; the resulting image runs as UID/GID 10001. It preserves
the upstream entrypoint and source label. No archived Go source is compiled.
The build script verifies the base configuration digest, every inherited
filesystem layer, recipe hash, platform, source revision and non-root user.

The six existing integration fixtures keep using the Minio S3 adapter. Their
server uses an owned data volume, loopback published ports, 512 MiB memory with
no additional swap, one CPU, a 128-PID limit, no Linux capabilities and no new
privileges. The ordinary file-storage round trip also stops/restarts the
actual provider and reads the committed object before deletion. These are
synthetic test accounts and objects; never attach customer data or use the
fixture as a production storage image.

Before the expensive provider matrix starts, its storage job scans the actual
local image with checksum-verified Trivy 0.76.1, records an SBOM, and runs 38
bounded S3/security/restart controls under the same non-root user and memory
limit. It verifies the actual process UID and data-volume ownership. Raw
findings, scanner identity, exact image configuration, recipe/source evidence,
cleanup results and a scoped consumer OpenVEX assessment are retained.

The fork retains the upstream MinIO Go module name. Its inherited
CVE-2026-39414 advisory match is classified as fixed only after the current
source-byte checks, reviewed base/recipe, fresh raw scan and all runtime
controls pass for that exact built configuration. Different findings or
subjects, missing coverage, stale scans and an expired review fail before the
native suites are allocated. No global advisory ignore or GMA security
exception is added. The current review expires 2026-12-04 and must be renewed
with fresh source, binary and scanner evidence.

The local image has no published registry-manifest digest. Its assessment names
its actual Docker configuration digest and retained inherited rootfs IDs;
it does not reuse the base image's OCI identity as the derived image's identity.

The qualified fixture platform is Linux amd64. Other platforms need their own
binary/scan assessment. Existing MinIO data formats, distributed durability,
whole-application recovery and production admission are separate qualifications.

Primary references:

- [Pinned Silo release](https://github.com/pgsty/silo/releases/tag/RELEASE.2026-10-26T00-00-00Z)
- [Silo compatibility and migration notes](https://silo.pgsty.com/compatibility/)
- [Upstream advisory](https://github.com/minio/minio/security/advisories/GHSA-h749-fxx7-pwpg)
- [Supplier fix disclosure](https://silo.pigsty.cc/blog/security/cve-2026-39414/)
