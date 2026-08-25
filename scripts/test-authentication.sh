#!/bin/bash
# BC NavUserPassword regression: every positive assertion is paired with a
# negative assertion so an unconditional authentication bypass cannot pass.
set -euo pipefail

BC_SERVER_USERNAME="${BC_SERVER_USERNAME:-BCRUNNER}"
BC_SERVER_PASSWORD="${BC_SERVER_PASSWORD:-Admin123!}"
BC_ODATA_PORT="${BC_ODATA_PORT:-7048}"
BC_DEV_PORT="${BC_DEV_PORT:-7049}"
COMPOSE=(docker compose)

if [ -z "$BC_SERVER_PASSWORD" ]; then
    echo "authentication test: BC_SERVER_PASSWORD must not be empty" >&2
    exit 1
fi

WRONG_PASSWORD="${BC_SERVER_PASSWORD}__bc_auth_regression_wrong__"

ODATA_URL="http://127.0.0.1:${BC_ODATA_PORT}/BC/ODataV4/Company"
DEV_URL="http://127.0.0.1:${BC_DEV_PORT}/BC/dev/packages?publisher=Microsoft&appName=System&appVersion=0.0.0.0"

status_with_basic_auth() {
    local url=$1 username=$2 password=$3 output=$4
    curl --silent --show-error --output "$output" --write-out '%{http_code}' \
        --user "$username:$password" "$url"
}

expect_success() {
    local label=$1 status=$2
    case "$status" in
        2??) printf 'PASS  %-38s HTTP %s\n' "$label" "$status" ;;
        *) echo "FAIL  $label expected HTTP success, got $status" >&2; exit 1 ;;
    esac
}

expect_rejected() {
    local label=$1 status=$2
    case "$status" in
        401|403) printf 'PASS  %-38s HTTP %s\n' "$label" "$status" ;;
        *) echo "FAIL  $label expected authentication rejection, got $status" >&2; exit 1 ;;
    esac
}

# The disabled/expired-user assertions below insert or modify [User]/
# [User Property] rows directly via SQL while NST is already running, not
# through BC's own user-management API. BC's user cache can lag that
# out-of-band write by a second or two, so the very next request can 401 as
# "unknown user" even though the row is already correct in the database —
# observed intermittently in CI (a freshly-inserted enabled user 401ing, or a
# just-disabled user still authenticating). Retry until the class of
# response we expect shows up, or the deadline passes and we report
# whatever the last attempt got — a real auth bug still fails the test, a
# stale cache just costs a few seconds.
poll_status_class() {
    local class=$1 url=$2 username=$3 password=$4 output=$5
    local attempts=10 delay=1 status
    for ((i = 1; i <= attempts; i++)); do
        status=$(status_with_basic_auth "$url" "$username" "$password" "$output")
        case "$class:$status" in
            success:2??) echo "$status"; return ;;
            rejected:401|rejected:403) echo "$status"; return ;;
        esac
        [ "$i" -lt "$attempts" ] && sleep "$delay"
    done
    echo "$status"
}

tmp_dir=$(mktemp -d)
DISABLED_GUID='00000000-0000-0000-0000-000000000101'
EXPIRED_GUID='00000000-0000-0000-0000-000000000102'
DISABLED_USER="${BC_SERVER_USERNAME}_AUTH_DISABLED"
EXPIRED_USER="${BC_SERVER_USERNAME}_AUTH_EXPIRED"
DISABLED_USER_BEFORE="${DISABLED_USER}_BEFORE"
EXPIRED_USER_BEFORE="${EXPIRED_USER}_BEFORE"

sql() {
    "${COMPOSE[@]}" exec -T bc /opt/mssql-tools18/bin/sqlcmd \
        -S "${SQL_SERVER:-sql}" -U sa -P "${SA_PASSWORD:-Passw0rd123!}" \
        -C -No -d CRONUS -b -Q "$1"
}

cleanup() {
    sql "
DELETE FROM [Access Control] WHERE [User Security ID] IN ('$DISABLED_GUID','$EXPIRED_GUID');
DELETE FROM [User Property] WHERE [User Security ID] IN ('$DISABLED_GUID','$EXPIRED_GUID');
DELETE FROM [User] WHERE [User Security ID] IN ('$DISABLED_GUID','$EXPIRED_GUID');
" >/dev/null 2>&1 || true
    # These files contain response bodies, never request credentials.
    find "$tmp_dir" -type f -delete 2>/dev/null || true
    rmdir "$tmp_dir" 2>/dev/null || true
}
trap cleanup EXIT

odata_correct=$(status_with_basic_auth "$ODATA_URL" "$BC_SERVER_USERNAME" "$BC_SERVER_PASSWORD" "$tmp_dir/odata-correct")
odata_wrong=$(status_with_basic_auth "$ODATA_URL" "$BC_SERVER_USERNAME" "$WRONG_PASSWORD" "$tmp_dir/odata-wrong")
expect_success "OData correct password" "$odata_correct"
expect_rejected "OData wrong password" "$odata_wrong"

dev_correct=$(status_with_basic_auth "$DEV_URL" "$BC_SERVER_USERNAME" "$BC_SERVER_PASSWORD" "$tmp_dir/dev-correct")
dev_wrong=$(status_with_basic_auth "$DEV_URL" "$BC_SERVER_USERNAME" "$WRONG_PASSWORD" "$tmp_dir/dev-wrong")
expect_success "developer endpoint correct password" "$dev_correct"
expect_rejected "developer endpoint wrong password" "$dev_wrong"

missing=$(status_with_basic_auth "$ODATA_URL" "${BC_SERVER_USERNAME}_AUTH_MISSING" "$BC_SERVER_PASSWORD" "$tmp_dir/missing")
empty=$(status_with_basic_auth "$ODATA_URL" "$BC_SERVER_USERNAME" "" "$tmp_dir/empty")
expect_rejected "nonexistent user" "$missing"
expect_rejected "empty password" "$empty"

if printf '%s' "$BC_SERVER_PASSWORD" | "${COMPOSE[@]}" exec -T bc \
    env DOTNET_STARTUP_HOOKS= /bc/tools/TestRunner/TestRunner --auth-probe \
        --host "localhost:7085" --user "$BC_SERVER_USERNAME" \
        --password-stdin >"$tmp_dir/ws-correct" 2>&1; then
    echo "PASS  WebSocket correct password"
else
    echo "FAIL  WebSocket correct-password probe failed" >&2
    sed 's/^/  /' "$tmp_dir/ws-correct" >&2
    exit 1
fi
set +e
printf '%s' "$WRONG_PASSWORD" | "${COMPOSE[@]}" exec -T bc \
    env DOTNET_STARTUP_HOOKS= /bc/tools/TestRunner/TestRunner --auth-probe \
        --host "localhost:7085" --user "$BC_SERVER_USERNAME" \
        --password-stdin >"$tmp_dir/ws-wrong" 2>&1
ws_wrong_status=$?
set -e
case "$ws_wrong_status" in
    2) echo "PASS  WebSocket wrong password rejected" ;;
    0) echo "FAIL  WebSocket wrong password authenticated" >&2; exit 1 ;;
    *)
        echo "FAIL  WebSocket wrong-password probe failed with non-authentication exit $ws_wrong_status" >&2
        sed 's/^/  /' "$tmp_dir/ws-wrong" >&2
        exit 1
        ;;
esac

disabled_hash=$(printf '%s' "$BC_SERVER_PASSWORD" | "${COMPOSE[@]}" exec -T bc \
    env DOTNET_STARTUP_HOOKS= /bc/tools/NavUserPasswordInspector/NavUserPasswordInspector \
        generate --user-security-id "$DISABLED_GUID")
expired_hash=$(printf '%s' "$BC_SERVER_PASSWORD" | "${COMPOSE[@]}" exec -T bc \
    env DOTNET_STARTUP_HOOKS= /bc/tools/NavUserPasswordInspector/NavUserPasswordInspector \
        generate --user-security-id "$EXPIRED_GUID")

sql "
DELETE FROM [Access Control] WHERE [User Security ID] IN ('$DISABLED_GUID','$EXPIRED_GUID');
DELETE FROM [User Property] WHERE [User Security ID] IN ('$DISABLED_GUID','$EXPIRED_GUID');
DELETE FROM [User] WHERE [User Security ID] IN ('$DISABLED_GUID','$EXPIRED_GUID');
INSERT INTO [User] ([User Security ID],[User Name],[Full Name],[State],[Expiry Date],[Windows Security ID],[Change Password],[License Type],[Authentication Email],[Contact Email],[Exchange Identifier],[Application ID],[\$systemId],[\$systemCreatedAt],[\$systemCreatedBy],[\$systemModifiedAt],[\$systemModifiedBy]) VALUES
('$DISABLED_GUID',N'$DISABLED_USER_BEFORE',N'Authentication disabled test',0,'2099-12-31',N'',0,0,N'',N'',N'','00000000-0000-0000-0000-000000000000',NEWID(),GETUTCDATE(),'$DISABLED_GUID',GETUTCDATE(),'$DISABLED_GUID'),
('$EXPIRED_GUID',N'$EXPIRED_USER_BEFORE',N'Authentication expired test',0,'2099-12-31',N'',0,0,N'',N'',N'','00000000-0000-0000-0000-000000000000',NEWID(),GETUTCDATE(),'$EXPIRED_GUID',GETUTCDATE(),'$EXPIRED_GUID');
INSERT INTO [User Property] ([User Security ID],[Password],[Name Identifier],[Authentication Key],[WebServices Key],[WebServices Key Expiry Date],[Authentication Object ID],[Directory Role ID],[Telemetry User ID],[\$systemId],[\$systemCreatedAt],[\$systemCreatedBy],[\$systemModifiedAt],[\$systemModifiedBy]) VALUES
('$DISABLED_GUID',N'$disabled_hash',N'',N'',N'','1753-01-01',N'',N'','$DISABLED_GUID',NEWID(),GETUTCDATE(),'$DISABLED_GUID',GETUTCDATE(),'$DISABLED_GUID'),
('$EXPIRED_GUID',N'$expired_hash',N'',N'',N'','1753-01-01',N'',N'','$EXPIRED_GUID',NEWID(),GETUTCDATE(),'$EXPIRED_GUID',GETUTCDATE(),'$EXPIRED_GUID');
INSERT INTO [Access Control] ([User Security ID],[Role ID],[Company Name],[Scope],[App ID],[\$systemId],[\$systemCreatedAt],[\$systemCreatedBy],[\$systemModifiedAt],[\$systemModifiedBy]) VALUES
('$DISABLED_GUID',N'SUPER',N'',0,'00000000-0000-0000-0000-000000000000',NEWID(),GETUTCDATE(),'$DISABLED_GUID',GETUTCDATE(),'$DISABLED_GUID'),
('$EXPIRED_GUID',N'SUPER',N'',0,'00000000-0000-0000-0000-000000000000',NEWID(),GETUTCDATE(),'$EXPIRED_GUID',GETUTCDATE(),'$EXPIRED_GUID');
" >/dev/null

disabled_before=$(poll_status_class success "$ODATA_URL" "$DISABLED_USER_BEFORE" "$BC_SERVER_PASSWORD" "$tmp_dir/disabled-before")
expired_before=$(poll_status_class success "$ODATA_URL" "$EXPIRED_USER_BEFORE" "$BC_SERVER_PASSWORD" "$tmp_dir/expired-before")
expect_success "disabled test user before disabling" "$disabled_before"
expect_success "expired test user before expiry" "$expired_before"

sql "
UPDATE [User]
SET [User Name] = N'$DISABLED_USER', [State] = 1,
    [\$systemModifiedAt] = GETUTCDATE(), [\$systemModifiedBy] = '$DISABLED_GUID'
WHERE [User Security ID] = '$DISABLED_GUID';
UPDATE [User]
SET [User Name] = N'$EXPIRED_USER', [Expiry Date] = '2000-01-01',
    [\$systemModifiedAt] = GETUTCDATE(), [\$systemModifiedBy] = '$EXPIRED_GUID'
WHERE [User Security ID] = '$EXPIRED_GUID';
" >/dev/null

disabled=$(poll_status_class rejected "$ODATA_URL" "$DISABLED_USER" "$BC_SERVER_PASSWORD" "$tmp_dir/disabled")
expired=$(poll_status_class rejected "$ODATA_URL" "$EXPIRED_USER" "$BC_SERVER_PASSWORD" "$tmp_dir/expired")
expect_rejected "disabled user" "$disabled"
expect_rejected "expired user" "$expired"

echo "Authentication regression: PASS"
