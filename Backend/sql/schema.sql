-- Dropshock account backend — SQL Server schema.
--
-- Design constraints, all from ARCHITECTURE.md:
--   * Central, not per-game-server. A player reaches the same stash from any server.
--   * The client never holds credentials and never talks to this directly.
--   * Writes are idempotent, keyed on a caller-supplied operation id.
--   * One custody slot per account.
--   * Server clock only. No client-supplied timestamp is ever stored.
--
-- Any error aborts the whole batch and rolls back. This matters when running the
-- file by hand in SSMS, where batches would otherwise be applied one at a time and
-- a failure halfway would leave a partial schema. The migration tool wraps the
-- whole file in its own transaction as well; this is belt and braces.
SET XACT_ABORT ON;
GO

CREATE TABLE dbo.Account
(
    AccountId       NVARCHAR(64)   NOT NULL PRIMARY KEY,
    CreatedAtUtc    DATETIME2(3)   NOT NULL CONSTRAINT DF_Account_Created DEFAULT SYSUTCDATETIME()
);
GO

-- The single custody slot. AccountId is the primary key, so "one slot per account"
-- is enforced by the schema rather than by application code that could drift from
-- the design decision.
CREATE TABLE dbo.CustodyRecord
(
    AccountId        NVARCHAR(64)  NOT NULL PRIMARY KEY
        CONSTRAINT FK_Custody_Account REFERENCES dbo.Account(AccountId),
    CustodyId        NVARCHAR(64)  NOT NULL,
    ConfiscatedAtUtc DATETIME2(3)  NOT NULL
);
GO

-- ONE table for every persisted item, with a location discriminator — NOT separate
-- Stash and Custody tables.
--
-- This is the important decision in this file. With two tables, InstanceId is
-- unique within each, and nothing stops the same instance existing in both: the
-- exact duplication the audit trail is meant to catch would be representable, and
-- moving an item would be a DELETE plus an INSERT with a window between them.
-- Here InstanceId is the primary key across ALL persisted items, so an item is in
-- exactly one place by construction, and moving it is a single UPDATE.
--
-- Duplication becomes impossible to store rather than merely possible to detect.
CREATE TABLE dbo.Item
(
    InstanceId      BIGINT         NOT NULL PRIMARY KEY,
    AccountId       NVARCHAR(64)   NOT NULL
        CONSTRAINT FK_Item_Account REFERENCES dbo.Account(AccountId),
    ItemId          INT            NOT NULL,
    Count           INT            NOT NULL CONSTRAINT CK_Item_Count CHECK (Count > 0),

    -- Stash   = in permanent storage, reachable from the compound
    -- Custody = confiscated on abduction, recoverable only by escaping
    Location        NVARCHAR(16)   NOT NULL
        CONSTRAINT CK_Item_Location CHECK (Location IN ('Stash', 'Custody')),

    -- Items in raid are absent from this table entirely: checkout deletes them.
    -- That is what makes "death writes nothing" work — see ARCHITECTURE.md.
);
GO

-- Note there is deliberately NO foreign key from Item to CustodyRecord. One would
-- force every account to have a custody record, which is wrong: most accounts have
-- an empty slot. The rule "an item may only be Location='Custody' while a
-- CustodyRecord exists for that account" is enforced in the service, and is covered
-- by its tests.

CREATE INDEX IX_Item_Account_Location ON dbo.Item(AccountId, Location) INCLUDE (ItemId, Count);
GO

-- Idempotency ledger. A write is applied at most once per OperationId.
--
-- CRITICAL: inserting here must happen in the SAME transaction as the effect it
-- describes. Recording the id in a separate commit reintroduces exactly the crash
-- window that idempotency exists to close — see IAccountStore.Save.
CREATE TABLE dbo.AppliedOperation
(
    OperationId     NVARCHAR(64)   NOT NULL PRIMARY KEY,
    AccountId       NVARCHAR(64)   NOT NULL
        CONSTRAINT FK_AppliedOperation_Account REFERENCES dbo.Account(AccountId),
    Kind            NVARCHAR(32)   NOT NULL,   -- Checkout | Extraction | Confiscate | Recover | Forfeit
    AppliedAtUtc    DATETIME2(3)   NOT NULL CONSTRAINT DF_AppliedOp_At DEFAULT SYSUTCDATETIME()
);
GO

CREATE INDEX IX_AppliedOperation_Account ON dbo.AppliedOperation(AccountId, AppliedAtUtc);
GO

-- Audit trail for every crossing of the raid/stash boundary. Duplication is meant
-- to be detectable after the fact, which requires recording crossings even when
-- they succeed. Append-only; never updated.
CREATE TABLE dbo.ItemTransferLog
(
    LogId           BIGINT IDENTITY(1,1) PRIMARY KEY,
    OperationId     NVARCHAR(64)   NOT NULL,
    AccountId       NVARCHAR(64)   NOT NULL,
    InstanceId      BIGINT         NOT NULL,
    ItemId          INT            NOT NULL,
    Count           INT            NOT NULL,
    Direction       NVARCHAR(16)   NOT NULL
        CONSTRAINT CK_TransferLog_Direction CHECK
        (Direction IN ('IntoRaid', 'IntoStash', 'IntoCustody', 'OutOfCustody', 'Forfeited')),
    LoggedAtUtc     DATETIME2(3)   NOT NULL CONSTRAINT DF_TransferLog_At DEFAULT SYSUTCDATETIME()
);
GO

CREATE INDEX IX_ItemTransferLog_Instance ON dbo.ItemTransferLog(InstanceId, LoggedAtUtc);
GO
