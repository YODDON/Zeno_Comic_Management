FROM postgres:18.0-alpine3.22
RUN apk add --no-cache age
COPY deployment/scripts/backup.sh /opt/comic-management/backup.sh
COPY deployment/scripts/restore-drill.sh /opt/comic-management/restore-drill.sh
COPY deployment/scripts/restore-target-guard.sh /opt/comic-management/restore-target-guard.sh
COPY deployment/scripts/recovery-verification.sql /opt/comic-management/recovery-verification.sql
RUN chmod 0555 /opt/comic-management/backup.sh \
    /opt/comic-management/restore-drill.sh \
    /opt/comic-management/restore-target-guard.sh
