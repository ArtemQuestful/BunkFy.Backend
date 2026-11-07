# Exact fixture HTML-parser applicability

The unchanged c3ad Silo fixture carries `golang.org/x/net v0.61.0` in both Go
module inventories. A fresh Trivy report therefore includes
[CVE-2026-78661 / GO-2026-6860](https://pkg.go.dev/vuln/GO-2026-6860).
Its affected package is `golang.org/x/net/html`: Parse, ParseFragment,
ParseFragmentWithOptions and ParseWithOptions. The whole package is absent from
both exact shipped binaries. Other x/net packages remain linked.

This assessment applies only to the existing digest-pinned Linux amd64 base and
its unchanged ownership-only derived fixture. `inspect-preview-storage-parser.py`
pins both actual binary SHA256s, complete function-name-table SHA256s and physical
function counts. An unknown binary, table, compiler format, source/image or other
advisory cannot inherit this classification. The existing thirty-day source
assessment, fresh scanner/SBOM and all38 actual storage controls still apply.

Go1.27.3's [linker contract](https://github.com/golang/go/blob/go1.27.3/src/cmd/link/internal/ld/pcln.go)
uses walkFuncs to visit each physical and inlined function, then
generateFuncnametab records all their names. The reader checks ELF/PCLN bounds,
every physical function/name reference and terminal PC, then reads the entire
bounded name region, including inline-only names. It requires the entire HTML
namespace to be absent. Go build metadata and the exact binary hashes bind this
compiler contract. This avoids treating govulncheck's conservative stripped-ELF
module fallback as actual function presence or absence.

Independent Go1.27.3 compiler controls used the actual x/net0.61.0 module: a
stripped binary referencing all four parser exports retained all four; a binary
using x/net's HPACK package retained the module with no HTML package; a third
control retained a confirmed inlined-only function name after its physical
function disappeared. Neither provider binary was executed during this analysis.
The exact Silo and mcli tables validate126854 and33911 physical functions, with
13726 and6011 additional names absent from their physical tables respectively.

During qualification the guard creates an owned, never-started container from
the exact local image configuration ID, with no network, a read-only filesystem
and a tmpfs override for /data. It copies and hashes only /usr/bin/silo and
/usr/bin/mcli, repeats the bounded table inspection, and verifies removal of that
exact container. No entrypoint or target binary runs for this inspection and no
existing volume is attached. Failure or incomplete cleanup fails admission.

Raw findings remain intact. Only this advisory/module/installed-version/PURL,
these two targets and these exact binary/image proofs receive the OpenVEX
`not_affected` / `vulnerable_code_not_present` statement. The separate bounded
S3 Select fix remains `fixed`; other HIGH/CRITICAL, secret, source, inventory,
runtime or cleanup failures remain blocking. No global scanner ignore, provider
source fork, replacement image or production approval is introduced.
