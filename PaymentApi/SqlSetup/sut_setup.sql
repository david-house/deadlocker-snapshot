USE master;
GO

DROP DATABASE IF EXISTS sut;
GO

CREATE DATABASE sut;
GO

ALTER DATABASE sut SET SINGLE_USER;
ALTER DATABASE sut SET RECOVERY SIMPLE;
ALTER DATABASE sut SET COMPATIBILITY_LEVEL = 100;
-- comment these out for non-snapshot
ALTER DATABASE sut SET ALLOW_SNAPSHOT_ISOLATION ON;
ALTER DATABASE sut SET READ_COMMITTED_SNAPSHOT OFF;

GO

USE sut;
GO

DROP TABLE IF EXISTS ClaimTransactions;
DROP TABLE IF EXISTS Claims;
GO

CREATE TABLE Claims (ClaimID INT IDENTITY(1,1) PRIMARY KEY,
    ClaimNumber INT,
    Token TIMESTAMP,
    IsLocked BIT DEFAULT 0)
GO

CREATE TABLE ClaimTransactions (
    ClaimTransactionID INT IDENTITY(1,1) PRIMARY KEY,
    CreatedDate DATETIME NOT NULL DEFAULT(GETDATE()),
    ClaimID INT NOT NULL,
    INDEX IX_ClaimTransactions_ClaimID NONCLUSTERED (ClaimID),
    CONSTRAINT FK_ClaimTransactions_Claims FOREIGN KEY (ClaimID) REFERENCES dbo.Claims (ClaimID),
    ClaimTransType CHAR(1) NOT NULL,
    CONSTRAINT CK_ClaimTransactions_ClaimTransType CHECK (ClaimTransType IN ('P', 'R')),
    Amount MONEY NOT NULL,
    ReserveBalance MONEY NOT NULL DEFAULT(0.00)
        )
GO

-- Set the seed for the RAND call, this will ensure that future calls follow the same sequence
DECLARE @FirstReserve FLOAT = RAND(45168)
DECLARE @NumberOfClaims INT = 10

WHILE @NumberOfClaims > 0
BEGIN
    INSERT Claims (ClaimNumber) VALUES (@NumberOfClaims)
    INSERT ClaimTransactions (ClaimID, ClaimTransType, Amount) SELECT SCOPE_IDENTITY(), 'R', ROUND(CONVERT(MONEY, RAND() * 100000 + 3500),0)
    SELECT @NumberOfClaims = @NumberOfClaims - 1
END

UPDATE ClaimTransactions SET ReserveBalance = Amount;
GO

USE master
GO

ALTER DATABASE sut SET MULTI_USER;
GO

USE sut
GO

--sp_who2
--select @@version