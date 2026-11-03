FROM postgres:18.0-alpine3.22@sha256:48c8ad3a7284b82be4482a52076d47d879fd6fb084a1cbfccbd551f9331b0e40
ARG RELEASE_REVISION=development
LABEL org.opencontainers.image.revision=$RELEASE_REVISION
RUN apk add --no-cache age openssl
COPY deployment/scripts/backup.sh /opt/comic-management/backup.sh
COPY deployment/scripts/restore-drill.sh /opt/comic-management/restore-drill.sh
COPY deployment/scripts/restore-target-guard.sh /opt/comic-management/restore-target-guard.sh
COPY deployment/scripts/recovery-verification.sql /opt/comic-management/recovery-verification.sql
RUN chmod 0555 /opt/comic-management/backup.sh \
    /opt/comic-management/restore-drill.sh \
    /opt/comic-management/restore-target-guard.sh
