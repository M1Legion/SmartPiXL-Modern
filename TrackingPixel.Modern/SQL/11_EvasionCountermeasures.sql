-- ============================================================================
-- SmartPiXL Evasion Countermeasures - Schema Migration
-- Run AFTER 03_StreamlinedSchema.sql
-- Adds columns for all V-01 through V-10 countermeasure data
-- ============================================================================

-- ============================================================================
-- SECTION 1: Client-Side Evasion Detection Columns
-- ============================================================================

-- V-01: Canvas noise injection detection
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'CanvasConsistency')
    ALTER TABLE PiXL_Test ADD CanvasConsistency NVARCHAR(20) NULL;

-- V-02: Audio fingerprint stability check
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'AudioStable')
    ALTER TABLE PiXL_Test ADD AudioStable BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'AudioNoiseDetected')
    ALTER TABLE PiXL_Test ADD AudioNoiseDetected BIT NULL;

-- V-03: Behavioral analysis
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'MouseMoves')
    ALTER TABLE PiXL_Test ADD MouseMoves INT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'MouseEntropy')
    ALTER TABLE PiXL_Test ADD MouseEntropy INT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'Scrolled')
    ALTER TABLE PiXL_Test ADD Scrolled BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'ScrollY')
    ALTER TABLE PiXL_Test ADD ScrollY INT NULL;

-- V-04: Stealth plugin detection
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'StealthSignals')
    ALTER TABLE PiXL_Test ADD StealthSignals NVARCHAR(500) NULL;

-- V-09: Font spoof detection
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'FontMethodMismatch')
    ALTER TABLE PiXL_Test ADD FontMethodMismatch BIT NULL;

-- V-10: Evasion/Tor signals
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'EvasionSignals')
    ALTER TABLE PiXL_Test ADD EvasionSignals NVARCHAR(500) NULL;

-- ============================================================================
-- SECTION 2: Server-Side Detection Columns
-- ============================================================================

-- V-05: Fingerprint stability tracking
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'FP_IsStable')
    ALTER TABLE PiXL_Test ADD FP_IsStable BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'FP_UniqueCount')
    ALTER TABLE PiXL_Test ADD FP_UniqueCount INT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'FP_ObservationCount')
    ALTER TABLE PiXL_Test ADD FP_ObservationCount INT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'FP_SuspiciousVariation')
    ALTER TABLE PiXL_Test ADD FP_SuspiciousVariation BIT NULL;

-- V-07: TLS fingerprinting
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'JA3Fingerprint')
    ALTER TABLE PiXL_Test ADD JA3Fingerprint NVARCHAR(64) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'TLSVersion')
    ALTER TABLE PiXL_Test ADD TLSVersion NVARCHAR(20) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'TLSCipher')
    ALTER TABLE PiXL_Test ADD TLSCipher NVARCHAR(100) NULL;

-- V-08: Datacenter IP detection
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'IsDatacenterIP')
    ALTER TABLE PiXL_Test ADD IsDatacenterIP BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'DatacenterProvider')
    ALTER TABLE PiXL_Test ADD DatacenterProvider NVARCHAR(50) NULL;

-- ============================================================================
-- SECTION 3: Removed Columns (Permission-Gated APIs)
-- ============================================================================
-- The following columns are no longer populated. They remain in the schema
-- for backward compatibility but will always be NULL for new data:
--   BluetoothSupport, USBSupport, SerialSupport, HIDSupport, MIDISupport,
--   WebXRSupport, ShareSupport, CredentialsSupport, GeolocationSupport,
--   NotificationsSupport, PushSupport, PaymentSupport, SpeechRecogSupport
-- Reason: Zero fingerprint entropy, triggers browser permission prompts,
--         looks alarming to end users inspecting source code.

-- ============================================================================
-- SECTION 4: Useful Indexes
-- ============================================================================

-- Fast lookup of suspicious fingerprint variation
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Test_FP_Suspicious')
    CREATE INDEX IX_PiXL_Test_FP_Suspicious
    ON PiXL_Test(FP_SuspiciousVariation)
    WHERE FP_SuspiciousVariation = 1;

-- Fast lookup of datacenter IPs
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Test_Datacenter')
    CREATE INDEX IX_PiXL_Test_Datacenter
    ON PiXL_Test(IsDatacenterIP)
    WHERE IsDatacenterIP = 1;

-- Fast lookup of evasion signals
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Test_EvasionSignals')
    CREATE INDEX IX_PiXL_Test_EvasionSignals
    ON PiXL_Test(EvasionSignals)
    WHERE EvasionSignals IS NOT NULL AND EvasionSignals <> '';

-- Fast lookup of stealth detections
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Test_StealthSignals')
    CREATE INDEX IX_PiXL_Test_StealthSignals
    ON PiXL_Test(StealthSignals)
    WHERE StealthSignals IS NOT NULL AND StealthSignals <> '';

PRINT 'Evasion countermeasure schema migration complete.';
GO
