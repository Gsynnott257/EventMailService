-- Timed events: run .bat/.cmd/.exe on schedule
CREATE TABLE dbo.Event_Mail_Service_Time_Events (
  ID                     INT IDENTITY PRIMARY KEY,
  Job_Name               NVARCHAR(200) NOT NULL,
  File_Path              NVARCHAR(1024) NOT NULL,  -- batch/exe
  Arguments              NVARCHAR(1024) NULL,
  Working_Directory      NVARCHAR(1024) NULL,
  Last_Run_Time          DATETIME2(0) NULL,
  Next_Run_Time          DATETIME2(0) NOT NULL,
  Interval_Minutes       INT NULL,                 -- optional auto-recurring
  Enabled                BIT NOT NULL DEFAULT(1),
  Max_Retries            INT NOT NULL DEFAULT(3),
  Retry_Interval_Seconds INT NOT NULL DEFAULT(30)
);
GO

-- Stored procedure monitor: which SP to run + who to notify
CREATE TABLE dbo.Event_Mail_Service_Stored_Procedure_Events (
  ID                 INT IDENTITY PRIMARY KEY,
  Stored_Proc_Name   SYSNAME NOT NULL,          -- schema.proc
  Database_Name      SYSNAME NULL,              -- optional cross-db
  Poll_Interval_Sec  INT NOT NULL DEFAULT(60),
  Fire_On_Any_True   BIT NOT NULL DEFAULT(1),
  Email_Group_Alias  NVARCHAR(512) NOT NULL,    -- DL or CSV
  Enabled            BIT NOT NULL DEFAULT(1),
  Last_Run_Time      DATETIME2(0) NULL,
  Next_Run_Time      DATETIME2(0) NOT NULL
);
GO

-- Strongly typed parameters for the SP
CREATE TABLE dbo.Event_Mail_Service_Stored_Procedure_Parameters (
  ID                 INT IDENTITY PRIMARY KEY,
  Stored_Proc_ID     INT NOT NULL FOREIGN KEY REFERENCES dbo.Event_Mail_Service_Stored_Procedure_Events(ID),
  Stored_Proc_Param  SYSNAME NOT NULL,          -- e.g., @PlantId
  Sql_Db_Type        NVARCHAR(50) NOT NULL,     -- Int, NVarChar, DateTime2, Bit, Decimal
  Direction          NVARCHAR(20) NOT NULL DEFAULT('Input'),  -- Input/Output/InputOutput
  Value_NVarChar     NVARCHAR(1024) NULL,
  Value_Int          INT NULL,
  Value_Decimal      DECIMAL(18,4) NULL,
  Value_DateTime2    DATETIME2(0) NULL,
  Value_Bit          BIT NULL
);
GO

CREATE OR ALTER PROCEDURE dbo.ExampleAlert @PlantId INT AS
BEGIN
  SET NOCOUNT ON;
  -- Multiple rows example
  SELECT TOP 10
      CAST(CASE WHEN SomeCondition=1 THEN 1 ELSE 0 END AS bit) AS Triggered,
      CONCAT('Part ', PartNo, ' below min: ', Qty) AS Details,
      PartNo, Qty
  FROM dbo.Inventory WHERE PlantId = @PlantId;
END