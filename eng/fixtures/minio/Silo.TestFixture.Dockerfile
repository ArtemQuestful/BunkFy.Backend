# Disposable integration fixtures only; never attach existing customer data.
# Preserve the reviewed binary and its exact inherited source identity.
FROM pgsty/silo:RELEASE.2026-10-26T00-00-00Z@sha256:7bb2aff6b89efe1cb1303c10fc21d579447434ed06649ac9ec05a5e2dbf30ad7
# A single ownership-only layer lets the server keep its previous non-root UID
# and persistent /data semantics without compiling archived upstream Go source.
RUN mkdir -p /data && chown 10001:10001 /data
USER 10001:10001
CMD ["server", "/data", "--address", ":9000"]
