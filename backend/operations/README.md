# Identity operations

## Provision the first Admin

The application deliberately has no route, command handler, or UI that changes a user's role.
Run `provision-first-admin.sql` once, from an approved operations workstation, only after the
target has completed Google sign-in and has supplied the UUID shown by `GET /api/v1/me`.

### Controls before execution

1. Open an approved change ticket and record the operator and independent reviewer. Neither
   identifier may contain a secret or personal credential.
2. Back up PostgreSQL and verify restore ownership. Use a dedicated short-lived database role,
   not the application role and not a shared owner account. It needs only `USAGE` on schema
   `identity` plus `SELECT (id, role, status, updated_at, version)` and
   `UPDATE (role, updated_at, version)` on `identity.users`.
3. Confirm the target UUID through an authenticated `/api/v1/me` response. Do not select or copy
   Google subject, email, tokens, cookies, database credentials, or other personal data into the
   ticket or transcript.
4. Have the reviewer confirm the environment, target UUID and absence of an existing Admin.

Run with TLS-enforced connection configuration supplied by the deployment secret store. Do not
put database credentials on the command line or in the repository:

```text
psql --set=target_user_id='<uuid>' \
  --set=change_ticket='<ticket-id>' \
  --set=operator_id='<operator-id>' \
  --set=reviewer_id='<reviewer-id>' \
  --file=backend/operations/provision-first-admin.sql
```

The script runs at `SERIALIZABLE`, takes a transaction-scoped advisory lock, locks the target row,
and aborts unless there is no existing Admin and the target is an Active User. It increments the
session version, so every pre-provision session is rejected and the new Admin role is available
only after a fresh Google login. Concurrent attempts serialize and all but the first fail.

### Audit evidence and verification

Capture the sanitized script output in the approved change system with the ticket, environment,
operator, reviewer, target UUID, execution time, result and database transaction evidence. Retain
it according to the operations audit policy. This external record is the I02 audit trail; the
append-only in-application Administration audit log is owned by the later Administration task and
must not be created early by this procedure.

After commit, verify that the old session receives `401`, sign in again with Google, check that
`GET /api/v1/me` reports `Admin`, and verify one Admin-only read endpoint. If any check fails, stop
and follow the incident/change rollback process; do not add a role-management API or edit another
user. Remove the temporary database role and attach the verification results to the ticket.
