# The backup job's image: pg_dump and pg_restore from the server's own image, so they match its
# major version; age, Debian trixie's 1.2.1, to encrypt each dump to a public key; and curl with CA
# certificates, to upload it to the bucket. Its scripts are bash, beside this file. See "Backups"
# in infra/hosted/README.md.
#
# Built from the repository root, like every hosted image: docker compose builds it as `backup`.
FROM postgres:18
RUN apt-get update \
    && apt-get install -y --no-install-recommends age ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

# A user that is not root, which owns the two folders the compose file mounts its volumes on, so a
# volume created empty starts owned by it. Nothing else of the image is writable by it.
RUN useradd --system --uid 10001 --no-create-home --shell /usr/sbin/nologin backup-job \
    && mkdir /backups /status \
    && chown backup-job: /backups /status \
    && chmod 0700 /backups \
    && chmod 0755 /status
COPY --chmod=a+rX infra/hosted/backup/*.sh /usr/local/lib/backup/
USER backup-job
WORKDIR /backups
# The postgres image stops its server with SIGINT; the scheduler stops on SIGTERM, docker's default.
STOPSIGNAL SIGTERM
# `schedule` with no arguments; `now` and `restore <database> <file> [--as <name>] [--replace]`.
ENTRYPOINT ["/usr/local/lib/backup/main.sh"]
CMD ["schedule"]
