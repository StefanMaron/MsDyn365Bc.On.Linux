/// <summary>
/// Log Table - Execution audit trail for the Test Runner API system
///
/// This table stores a chronological record of codeunit executions performed
/// via the Test Runner API, providing an audit trail for troubleshooting and monitoring.
///
/// Purpose:
/// - Track all codeunit executions with timestamped entries
/// - Record execution messages (success/failure/custom messages)
/// - Identify which server/computer performed the execution (for distributed environments)
/// - Maintain sequential entry numbering for audit purposes
///
/// Usage Pattern:
/// Log.Init();
/// Log."Message" := 'Your execution message';
/// Log."Computer Name" := ServerInstance."Server Computer Name";
/// Log.Insert();
///
/// Key Features:
/// - Auto-incrementing Entry No. ensures unique, sequential record identification
/// - No automatic deletion - maintains complete audit history
/// - Can be queried via standard BC list pages or OData endpoints
/// </summary>
table 50002 "Log Table"
{
    DataClassification = ToBeClassified;

    fields
    {
        /// <summary>
        /// Unique, auto-incrementing identifier for each log entry.
        /// </summary>
        /// <remarks>
        /// Automatically assigned by the system on insert.
        /// Provides sequential ordering and unique identification for audit trail.
        /// </remarks>
        field(1; "Entry No."; Integer)
        {
            DataClassification = SystemMetadata;
            AutoIncrement = true;
        }

        /// <summary>
        /// The log message describing the execution or event.
        /// </summary>
        /// <remarks>
        /// Typical values:
        /// - "Test CU ran successfully"
        /// - "Codeunit 50XXX executed"
        /// - Custom execution messages from codeunits
        /// Maximum length: 250 characters
        /// </remarks>
        field(2; "Message"; Text[250])
        {
            DataClassification = ToBeClassified;
        }

        /// <summary>
        /// The name of the server/computer that executed the codeunit.
        /// </summary>
        /// <remarks>
        /// Useful for distributed environments with multiple BC server instances.
        /// Populated from ServerInstance."Server Computer Name".
        /// Maximum length: 100 characters
        /// </remarks>
        field(3; "Computer Name"; Text[100])
        {
            DataClassification = ToBeClassified;
        }

        /// <summary>
        /// The ID of the codeunit that was executed.
        /// </summary>
        field(4; "Codeunit ID"; Integer)
        {
            DataClassification = SystemMetadata;
        }

        /// <summary>
        /// The name of the codeunit that was executed.
        /// </summary>
        field(5; "Codeunit Name"; Text[250])
        {
            DataClassification = SystemMetadata;
        }

        /// <summary>
        /// The name of the test function that was executed.
        /// </summary>
        field(6; "Function Name"; Text[250])
        {
            DataClassification = SystemMetadata;
        }

        /// <summary>
        /// Indicates whether the test execution was successful.
        /// </summary>
        field(8; "Success"; Boolean)
        {
            DataClassification = SystemMetadata;
        }

        /// <summary>
        /// Error message from failed test execution (truncated to 2048 chars).
        /// </summary>
        field(9; "Error Message"; Text[2048])
        {
            DataClassification = SystemMetadata;
        }

        /// <summary>
        /// Call stack from failed test execution (truncated to 2048 chars).
        /// </summary>
        field(10; "Call Stack"; Text[2048])
        {
            DataClassification = SystemMetadata;
        }

    }

    keys
    {
        /// <summary>
        /// Primary key on Entry No., providing chronological ordering.
        /// </summary>
        key(PK; "Entry No.")
        {
            Clustered = true;
        }
    }

    /// <summary>
    /// OnInsert trigger - Executes when a new log entry is created.
    /// </summary>
    /// <remarks>
    /// Currently empty - Entry No. is auto-assigned by the AutoIncrement property.
    /// Can be extended to add timestamps, user IDs, or other audit fields.
    /// </remarks>
    trigger OnInsert()
    begin
        // Entry No. is automatically assigned via AutoIncrement
        // Additional audit fields (timestamps, user IDs) can be added here
    end;
}