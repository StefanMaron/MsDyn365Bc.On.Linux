/// <summary>
/// Creates the tenant encryption key this container never had (issue #75).
///
/// AL's ENCRYPT/DECRYPT, IsolatedStorage(Encrypted = true) and the Data
/// Encryption Management pages all read one RSA key that belongs to the
/// tenant. Business Central does not create that key on its own: on Windows
/// it is an explicit setup step (New-NAVEncryptionKey, or the Enable
/// Encryption action on page 9905), and a Business Central online tenant is
/// handed one by the service. This container had neither, so every AL
/// encryption call failed with "An encryption key is required to complete
/// the request." — correct behavior for a tier with no key, and a permanent
/// difference from the sandbox this container is meant to match.
///
/// Issue #66 is why this is possible at all: the CRONUS demo backup ships
/// its single [$ndo$tenantproperty] row with a blank tenantid, so the write
/// that records the new key's file name matched no rows and CreateKey()
/// failed on the next line. scripts/entrypoint.sh sets that tenantid now.
///
/// Why it lives in the test runner extension: this is the one extension the
/// entrypoint publishes on every boot, so putting the key setup here costs
/// nothing. A separate app would add another publish to every startup.
///
/// The key survives a container recreate — entrypoint.sh symlinks the
/// server's Keys directory onto the /bc/service volume, so the file and the
/// file name recorded in [$ndo$tenantproperty] are kept together.
/// </summary>
codeunit 99907 "Encryption Key Bootstrap"
{
    Access = Internal;

    trigger OnRun()
    begin
        EnsureTenantEncryptionKey();
    end;

    /// <summary>
    /// A try method, not a Codeunit.Run wrapper. Codeunit.Run opens a nested
    /// transaction, and creating a key inside one fails the whole install
    /// with "An error occurred and the transaction is stopped" — measured on
    /// BC 28.4, where the same calls made directly succeed.
    /// </summary>
    [TryFunction]
    procedure TryEnsureTenantEncryptionKey()
    begin
        EnsureTenantEncryptionKey();
    end;

    procedure EnsureTenantEncryptionKey()
    begin
        if EncryptionEnabled() then begin
            if EncryptionKeyExists() then
                exit;

            // The tenant has a key file name recorded but the file itself is
            // gone — the state you get if the SQL database outlives the
            // /bc/service volume. Nothing encrypted with that key is
            // readable any more, and every call fails with
            // NavEncryptionKeyNotFoundException until the record is cleared,
            // so drop it and make a usable key.
            DeleteEncryptionKey();
        end;

        // The Boolean return is what keeps a failed creation from raising.
        if not CreateEncryptionKey() then
            exit;
    end;
}

/// <summary>
/// Runs the key setup when the extension is installed. The container's SQL
/// data lives on a tmpfs, so the database is restored fresh on every start
/// and this extension is installed again each time — which is exactly when a
/// new tenant needs a key.
/// </summary>
codeunit 99908 "Encryption Key Install"
{
    Subtype = Install;

    trigger OnInstallAppPerDatabase()
    var
        Bootstrap: Codeunit "Encryption Key Bootstrap";
    begin
        // A tier without an encryption key still runs tests; a tier whose
        // install trigger raised does not publish at all. Swallow it.
        if not Bootstrap.TryEnsureTenantEncryptionKey() then
            ClearLastError();
    end;
}

/// <summary>
/// Runs the same check when the extension is upgraded, so a tier that kept
/// its database across a version bump of this extension also gets a key.
/// </summary>
codeunit 99909 "Encryption Key Upgrade"
{
    Subtype = Upgrade;

    trigger OnUpgradePerDatabase()
    var
        Bootstrap: Codeunit "Encryption Key Bootstrap";
    begin
        if not Bootstrap.TryEnsureTenantEncryptionKey() then
            ClearLastError();
    end;
}
