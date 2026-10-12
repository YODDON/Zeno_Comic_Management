\set ON_ERROR_STOP on

\if :{?target_user_id}
\else
    \error 'target_user_id is required'
\endif

\if :{?change_ticket}
\else
    \error 'change_ticket is required'
\endif

\if :{?operator_id}
\else
    \error 'operator_id is required'
\endif

\if :{?reviewer_id}
\else
    \error 'reviewer_id is required'
\endif

BEGIN TRANSACTION ISOLATION LEVEL SERIALIZABLE;

SELECT set_config(
    'application_name',
    'comic-management-first-admin/' || :'change_ticket',
    true);
SELECT set_config(
    'comic_management.first_admin_target_user_id',
    :'target_user_id',
    true);
SELECT pg_advisory_xact_lock(
    hashtextextended('comic-management:first-admin-provisioning', 0));

DO $provision$
DECLARE
    target_id uuid := current_setting(
        'comic_management.first_admin_target_user_id')::uuid;
    target_role text;
    target_status text;
BEGIN
    IF EXISTS (
        SELECT 1
        FROM identity.users
        WHERE role = 'Admin')
    THEN
        RAISE EXCEPTION
            'First Admin already exists; this procedure cannot provision another Admin.';
    END IF;

    SELECT role, status
    INTO target_role, target_status
    FROM identity.users
    WHERE id = target_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Target user does not exist.';
    END IF;

    IF target_role <> 'User' OR target_status <> 'Active' THEN
        RAISE EXCEPTION
            'Target must be an Active user with the User role.';
    END IF;

    UPDATE identity.users
    SET role = 'Admin',
        updated_at = clock_timestamp(),
        version = version + 1
    WHERE id = target_id;
END
$provision$;

SELECT
    :'change_ticket' AS change_ticket,
    :'operator_id' AS operator_id,
    :'reviewer_id' AS reviewer_id,
    id AS target_user_id,
    role,
    status,
    updated_at,
    version
FROM identity.users
WHERE id = :'target_user_id'::uuid;

COMMIT;
