# Isolated MinIO provider fixture

`../../build-provider-images.ps1` builds the last upstream community security
release from checksum-pinned source, with pinned Go and Alpine image digests.
There is no floating upstream binary or mirror fallback. The Dockerfile name
refers specifically to the upstream STS/service-account policy fix; it does not
mean every dependency is free of known vulnerabilities.

This image is for the six owned integration fixtures covering S3 round trips,
protected exports and ingestion/worker recovery. `ProviderTestContainers.Minio`
requires the local image, binds published ports to loopback, limits it to 512 MiB
and one CPU, and runs the non-root image with updates disabled. Do not use it for
production storage, shared credentials, real customer data or public endpoints.

The 2026-11-02 govulncheck v1.9.0 binary scan found known advisory matches in the
archived release's dependencies, including x/crypto, x/net, gRPC and AMQP.
Affected symbols are present; binary scanning does not establish whether these
test flows can reach them. Updating the Go toolchain does not update those
modules. x/crypto/openpgp also has an advisory with no fixed version.

Production storage qualification remains unresolved. It requires a maintained
implementation or a separately reviewed and tested dependency/source repair,
fresh Go and operating-system advisory scans, and application-level S3 contract
and recovery checks. Passing the isolated provider suite is not that admission.

For a repeatable scan, build the Dockerfile's `builder` target and run
`govulncheck -mode=binary /out/minio` using a pinned scanner version. Preserve the
image identity, source/recipe hash, scanner version, database timestamp and raw
findings. If using `-json`, explicitly inspect findings: that mode returns exit
code zero even when vulnerabilities are reported.

Primary references:

- [Archived upstream and source release](https://github.com/minio/minio)
- [govulncheck usage, binary limitations and JSON exit behavior](https://pkg.go.dev/golang.org/x/vuln/cmd/govulncheck)
- [Unmaintained OpenPGP advisory](https://pkg.go.dev/vuln/GO-2026-5932)
