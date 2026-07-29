using System.Text.Json;
using gsm.Models;
using gsm.Services;

namespace gsm.Tests;

public sealed class RealDeviceSmokeTestRunnerTests
{
    [Fact]
    public void RequestArgument_RequiresOneFollowingAbsolutePathValue()
    {
        string expected = Path.GetFullPath(Path.Combine("requests", "smoke.json"));

        bool found = RealDeviceSmokeTestRunner.TryGetRequestPath(
            ["--unrelated", "x", RealDeviceSmokeTestRunner.RequestArgument, expected],
            out string actual,
            out string error);

        Assert.True(found);
        Assert.Equal(expected, actual);
        Assert.Empty(error);

        Assert.False(RealDeviceSmokeTestRunner.TryGetRequestPath(
            [RealDeviceSmokeTestRunner.RequestArgument],
            out _,
            out string missingError));
        Assert.Contains("Thiếu", missingError);
    }

    [Fact]
    public void Claim_RenamesRequestExactlyOnceAndParsesValidatedSchema()
    {
        string directory = CreateTempDirectory();
        try
        {
            string requestPath = Path.Combine(directory, "request.json");
            File.WriteAllText(requestPath, ValidRequestJson("claim-once", "COM86"));

            RealDeviceSmokeClaim claim =
                RealDeviceSmokeTestRunner.ClaimAndReadRequest(requestPath);

            Assert.False(File.Exists(requestPath));
            Assert.True(File.Exists(claim.ClaimedPath));
            Assert.Equal("claim-once", claim.RunId);
            Assert.Equal("COM86", claim.Request.PortName);
            Assert.Equal("89840200000000000003", claim.Request.ExpectedCcid);
            Assert.Throws<FileNotFoundException>(() =>
                RealDeviceSmokeTestRunner.ClaimAndReadRequest(requestPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Claim_InvalidConfirmationRemainsClaimedAndCannotBeEditedInPlace()
    {
        string directory = CreateTempDirectory();
        try
        {
            string requestPath = Path.Combine(directory, "request.json");
            File.WriteAllText(requestPath,
                """
                {
                  "schemaVersion": 1,
                  "requestId": "not-confirmed",
                  "confirmTelecomActions": false
                }
                """);

            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                RealDeviceSmokeTestRunner.ClaimAndReadRequest(requestPath));

            Assert.Contains("confirmTelecomActions", error.Message);
            Assert.False(File.Exists(requestPath));
            Assert.Single(Directory.GetFiles(
                directory, ".toolgsm-smoke-claimed-*.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Claim_UnknownJsonMember_IsRejectedAndRemainsClaimed()
    {
        string directory = CreateTempDirectory();
        try
        {
            string requestPath = Path.Combine(directory, "request.json");
            string json = ValidRequestJson(
                    "strict-schema", "COM86", "89840200000000000003")
                .Replace(
                    "\"smsResponseWaitSeconds\": 300",
                    "\"smsResponseWaitSeconds\": 300,\n  \"portNam\": \"COM99\"",
                    StringComparison.Ordinal);
            File.WriteAllText(requestPath, json);

            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                RealDeviceSmokeTestRunner.ClaimAndReadRequest(requestPath));

            Assert.Contains("JSON", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(requestPath));
            Assert.Single(Directory.GetFiles(
                directory, ".toolgsm-smoke-claimed-*.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("", "89840200000000000003")]
    [InlineData("COM86", "")]
    [InlineData("COM86", "89840400000000000003")]
    [InlineData("COM86", "898402123")]
    public void Request_RequiresExplicitPortAndExactVinaCcid(
        string portName,
        string expectedCcid)
    {
        var request = new RealDeviceSmokeRequest
        {
            ConfirmTelecomActions = true,
            PortName = portName,
            ExpectedCcid = expectedCcid
        };

        Assert.Throws<InvalidDataException>(() =>
            RealDeviceSmokeTestRunner.ValidateRequest(request));
    }

    [Fact]
    public void BatchRequest_RequiresExactUniquePortInventoryAndNoSinglePortFields()
    {
        string[] ports = Enumerable.Range(83, 32)
            .Select(number => $"COM{number}")
            .ToArray();
        var valid = new RealDeviceSmokeRequest
        {
            ConfirmTelecomActions = true,
            Scenario = RealDeviceSmokeScenario.ImeiUssdBatch,
            PortNames = ports,
            ExpectedPortCount = 32,
            MaxParallelPorts = 8
        };

        RealDeviceSmokeTestRunner.ValidateRequest(valid);

        Assert.Throws<InvalidDataException>(() =>
            RealDeviceSmokeTestRunner.ValidateRequest(valid with
            {
                PortNames = ports[..^1]
            }));
        Assert.Throws<InvalidDataException>(() =>
            RealDeviceSmokeTestRunner.ValidateRequest(valid with
            {
                PortNames = ports[..^1].Append("COM83").ToArray()
            }));
        Assert.Throws<InvalidDataException>(() =>
            RealDeviceSmokeTestRunner.ValidateRequest(valid with
            {
                PortName = "COM83",
                ExpectedCcid = "89840200000000000003"
            }));
        Assert.Throws<InvalidDataException>(() =>
            RealDeviceSmokeTestRunner.ValidateRequest(valid with
            {
                ExpectedPortCount = 1,
                PortNames = ["COM83"]
            }));
        Assert.Throws<InvalidDataException>(() =>
            RealDeviceSmokeTestRunner.ValidateRequest(valid with
            {
                Scenario = (RealDeviceSmokeScenario)999
            }));
    }

    [Fact]
    public void BatchTargetGenerator_RetriesKnownCollisionAndReservesUniqueTarget()
    {
        string existing = ImeiManagementService.GenerateRandomImei();
        string unique = ImeiManagementService.GenerateRandomImei();
        while (string.Equals(existing, unique, StringComparison.Ordinal))
            unique = ImeiManagementService.GenerateRandomImei();
        var generated = new Queue<string>([existing, unique]);
        var unavailable = new HashSet<string>(StringComparer.Ordinal)
        {
            existing
        };

        string target = RealDeviceSmokeTestRunner.GenerateUniqueBatchImeiTarget(
            unavailable,
            generated.Dequeue,
            maxAttempts: 2);

        Assert.Equal(unique, target);
        Assert.Contains(unique, unavailable);
        Assert.Equal(2, unavailable.Count);
    }

    [Fact]
    public void Checkpoint_ReplacesResultAndLeavesNoReadableTempCheckpoint()
    {
        string directory = CreateTempDirectory();
        try
        {
            AtomicSmokeResultStore store =
                AtomicSmokeResultStore.CreateNew(directory, "atomic-state");
            var state = new RealDeviceSmokeResult
            {
                RunId = "atomic-state",
                Outcome = RealDeviceSmokeOutcome.Running,
                CurrentStep = "first",
                StartedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            store.Save(state);
            state.CurrentStep = "second";
            store.Save(state);

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(store.ResultPath));
            Assert.Equal("second",
                document.RootElement.GetProperty("currentStep").GetString());
            Assert.Empty(Directory.GetFiles(store.RunDirectory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(RealDeviceSmokeStepStatus.Prepared)]
    [InlineData(RealDeviceSmokeStepStatus.AwaitingResponse)]
    public void InterruptedChargeableStep_IsAmbiguousAndMustNotBeReplayed(
        RealDeviceSmokeStepStatus status)
    {
        var step = new RealDeviceSmokeStep
        {
            Name = "paid-operation",
            Chargeable = true,
            Status = status
        };

        Assert.Equal(
            RealDeviceSmokeOutcome.Ambiguous,
            RealDeviceSmokeTestRunner.InterruptedOutcome(step, cancelled: false));
        Assert.Equal(
            RealDeviceSmokeOutcome.Ambiguous,
            RealDeviceSmokeTestRunner.InterruptedOutcome(step, cancelled: true));
    }

    [Fact]
    public void InterruptedNonChargeableStep_UsesNormalFailureOrCancellation()
    {
        var step = new RealDeviceSmokeStep
        {
            Name = "ussd",
            Chargeable = false,
            Status = RealDeviceSmokeStepStatus.Prepared
        };

        Assert.Equal(
            RealDeviceSmokeOutcome.Failed,
            RealDeviceSmokeTestRunner.InterruptedOutcome(step, cancelled: false));
        Assert.Equal(
            RealDeviceSmokeOutcome.Cancelled,
            RealDeviceSmokeTestRunner.InterruptedOutcome(step, cancelled: true));
    }

    [Fact]
    public void PortSelection_RequiresExplicitPortAndExactCcid()
    {
        string imeiA = ImeiManagementService.GenerateRandomImei();
        string imeiB = ImeiManagementService.GenerateRandomImei();
        var ports = new[]
        {
            Port("COM83", 5, "Viettel", "89840400000000000001", imeiA),
            Port("COM90", 4, "VinaPhone", "89840200000000000002", imeiA),
            Port("COM86", 2, "VinaPhone", "89840200000000000003", imeiB)
        };

        Assert.Equal("COM86",
            RealDeviceSmokeTestRunner.SelectEligiblePort(
                ports, "COM86", "89840200000000000003")?.PortName);
        Assert.Equal("COM90",
            RealDeviceSmokeTestRunner.SelectEligiblePort(
                ports, "com90", "89840200000000000002")?.PortName);
        Assert.Null(
            RealDeviceSmokeTestRunner.SelectEligiblePort(
                ports, "COM86", "89840200000000000002"));
        Assert.Null(
            RealDeviceSmokeTestRunner.SelectEligiblePort(
                ports, null, "89840200000000000003"));
    }

    [Fact]
    public void InitialSelection_CanSafelyPreemptOnlyTheBackgroundBalanceLookup()
    {
        RealDeviceSmokePortSnapshot port = Port(
            "COM83",
            5,
            "VinaPhone",
            "89840200000000000003",
            ImeiManagementService.GenerateRandomImei(),
            isBalanceLoading: true);

        Assert.NotNull(RealDeviceSmokeTestRunner.SelectEligiblePort(
            [port], "COM83", "89840200000000000003"));
        Assert.True(
            RealDeviceSmokeTestRunner.IsEligibleForInitialImeiSelection(port));
        Assert.False(RealDeviceSmokeTestRunner.IsEligible(port));
    }

    [Theory]
    [InlineData("4321 VND", true)]
    [InlineData("+CUSD: 0,\"9000 VND\",15", true)]
    [InlineData("ERROR: timeout", false)]
    [InlineData("", false)]
    public void UssdSuccess_IsConservative(string result, bool expected)
    {
        Assert.Equal(expected,
            RealDeviceSmokeTestRunner.IsSuccessfulUssdResult(result));
    }

    [Theory]
    [InlineData("Gửi thành công", true)]
    [InlineData("+CMGS: 42\r\nOK", true)]
    [InlineData("OK", false)]
    [InlineData("ERROR: Timeout sending SMS payload", false)]
    public void SmsSuccess_RequiresCarrierSubmitEvidence(string result, bool expected)
    {
        Assert.Equal(expected,
            RealDeviceSmokeTestRunner.IsSuccessfulSmsResult(result));
    }

    [Theory]
    [InlineData("Gửi thành công", RealDeviceSmsSubmitDisposition.Confirmed)]
    [InlineData("+CMGS: 42\r\nOK", RealDeviceSmsSubmitDisposition.Confirmed)]
    [InlineData("ERROR: [SMS_PAYLOAD_SUBMITTED] Timeout sending SMS payload", RealDeviceSmsSubmitDisposition.PayloadSubmittedUncertain)]
    [InlineData("ERROR: Timeout waiting for > prompt", RealDeviceSmsSubmitDisposition.PrePayloadFailed)]
    [InlineData("ERROR: SIM session changed before send", RealDeviceSmsSubmitDisposition.PrePayloadFailed)]
    public void SmsSubmitClassification_DistinguishesIrreversiblePayloadBoundary(
        string result,
        RealDeviceSmsSubmitDisposition expected)
    {
        Assert.Equal(expected,
            RealDeviceSmokeTestRunner.ClassifySmsSubmitResult(result));
    }

    [Fact]
    public void CallEvidence_AcceptsActiveOrCompletedNoAudioDurationAndTrustedHangup()
    {
        string[] evidence =
        [
            "[CALL_STATE] Phiên thoại đã tạo: DIALING.",
            "[CALL] Hết 15 giây → Dập máy (ATH)."
        ];

        Assert.False(RealDeviceSmokeTestRunner.HasActiveCallEvidence(evidence));
        Assert.False(RealDeviceSmokeTestRunner.HasCompletedVoiceDurationEvidence(
            evidence, 15));
        Assert.False(RealDeviceSmokeTestRunner.HasConfirmedCallHangupEvidence(
            evidence, 15));

        evidence =
        [
            "[CALL_STATE] Phiên thoại đã tạo: ACTIVE.",
            "[CALL_HANGUP_CONFIRMED] duration=15; ATH=OK; CLCC=EMPTY."
        ];

        Assert.True(RealDeviceSmokeTestRunner.HasActiveCallEvidence(evidence));
        Assert.False(RealDeviceSmokeTestRunner.HasCompletedVoiceDurationEvidence(
            evidence, 15));
        Assert.True(RealDeviceSmokeTestRunner.HasConfirmedCallHangupEvidence(
            evidence, 15));
        Assert.False(RealDeviceSmokeTestRunner.HasConfirmedCallHangupEvidence(
            evidence, 30));

        evidence =
        [
            "[CALL_STATE] Phiên thoại đã tạo: ALERTING.",
            "[CALL_DURATION_COMPLETE] duration=15; voice=DIALING_OR_ALERTING; active=false; mode=no-audio.",
            "[CALL_HANGUP_CONFIRMED] duration=15; ATH=OK; CLCC=EMPTY."
        ];

        Assert.False(RealDeviceSmokeTestRunner.HasActiveCallEvidence(evidence));
        Assert.True(RealDeviceSmokeTestRunner.HasCompletedVoiceDurationEvidence(
            evidence, 15));
        Assert.False(RealDeviceSmokeTestRunner.HasCompletedVoiceDurationEvidence(
            evidence, 30));
        Assert.True(RealDeviceSmokeTestRunner.HasConfirmedCallHangupEvidence(
            evidence, 15));
    }

    [Fact]
    public void Continuation_ReusesCommittedImeiOnlyAfterKnownEndedCallAndBeforeSms()
    {
        const string runId = "failed-call-run";
        const string ccid = "89840200000000000003";
        string imei = ImeiManagementService.GenerateRandomImei();
        var request = new RealDeviceSmokeRequest
        {
            ConfirmTelecomActions = true,
            PortName = "COM83",
            ExpectedCcid = ccid,
            ContinuationOfRunId = runId
        };
        RealDeviceSmokePortSnapshot selected = Port(
            "COM83", 5, "VinaPhone", ccid, imei);
        var prior = new RealDeviceSmokeResult
        {
            RunId = runId,
            Outcome = RealDeviceSmokeOutcome.Failed,
            PortName = "COM83",
            PinnedCcid = ccid,
            TargetImei = imei,
            CallEvidence =
            [
                "[CALL_STATE] Phiên thoại đã tạo: ALERTING.",
                "[CALL_HANGUP_CONFIRMED] duration=15; ATH=OK; CLCC=EMPTY."
            ],
            Steps =
            [
                PassedStep("select-port"),
                PassedStep("create-sauto-imei"),
                PassedStep("refresh-after-imei"),
                PassedStep("automatic-ussd-111-101"),
                PassedStep("manual-ussd-101"),
                new RealDeviceSmokeStep
                {
                    Name = "call-900-15s",
                    Chargeable = true,
                    Status = RealDeviceSmokeStepStatus.Failed
                }
            ]
        };

        Assert.Equal(
            imei,
            RealDeviceSmokeTestRunner.ValidateContinuationAndGetImei(
                request, prior, selected));

        prior.Steps =
        [
            PassedStep("continue-after-confirmed-hangup"),
            new RealDeviceSmokeStep
            {
                Name = "call-900-15s",
                Chargeable = true,
                Status = RealDeviceSmokeStepStatus.Failed
            }
        ];
        Assert.Equal(
            imei,
            RealDeviceSmokeTestRunner.ValidateContinuationAndGetImei(
                request, prior, selected));

        prior.Steps.Add(new RealDeviceSmokeStep
        {
            Name = "sms-data-to-888",
            Chargeable = true,
            Status = RealDeviceSmokeStepStatus.Prepared
        });
        Assert.Throws<RealDeviceSmokeRunException>(() =>
            RealDeviceSmokeTestRunner.ValidateContinuationAndGetImei(
                request, prior, selected));
    }

    private static RealDeviceSmokeStep PassedStep(string name) => new()
    {
        Name = name,
        Status = RealDeviceSmokeStepStatus.Passed
    };

    private static RealDeviceSmokePortSnapshot Port(
        string portName,
        int physicalIndex,
        string provider,
        string ccid,
        string imei,
        bool isBalanceLoading = false) => new(
            portName,
            physicalIndex,
            int.Parse(portName[3..]),
            IsActive: true,
            IsReadyForOperation: true,
            IsRebooting: false,
            IsBalanceLoading: isBalanceLoading,
            NetworkProvider: provider,
            NetworkType: "LTE",
            Ccid: ccid,
            Imei: imei);

    private static string ValidRequestJson(
        string requestId,
        string portName,
        string expectedCcid = "89840200000000000003") =>
        $$"""
        {
          "schemaVersion": 1,
          "requestId": "{{requestId}}",
          "confirmTelecomActions": true,
          "portName": "{{portName}}",
          "expectedCcid": "{{expectedCcid}}",
          "portWaitSeconds": 600,
          "stablePortSeconds": 5,
          "imeiWaitSeconds": 600,
          "automaticUssdWaitSeconds": 900,
          "postOperationWaitSeconds": 60,
          "smsResponseWaitSeconds": 300
        }
        """;

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "toolgsm-smoke-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class LogOnlySmokeHost : IRealDeviceSmokeHost
    {
        public event Action<LogMessage>? LogAdded;

        public void Emit(string message, string level) =>
            LogAdded?.Invoke(new LogMessage
            {
                Message = message,
                Level = level
            });

        public IReadOnlyList<RealDeviceSmokePortSnapshot> GetPorts() =>
            Array.Empty<RealDeviceSmokePortSnapshot>();

        public Task<(bool Success, string TargetImei)> CreateNewImeiForPortAsync(
            string portName,
            string expectedCcid) =>
            Task.FromResult((false, string.Empty));

        public Task<bool> ApplyImeiForCurrentSimAsync(
            string portName,
            string targetImei,
            string expectedCcid,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public IReadOnlySet<string> GetKnownImeis() =>
            new HashSet<string>(StringComparer.Ordinal);

        public bool IsImeiCommitted(string ccid, string targetImei) => false;

        public bool TryGetCurrentSessionEpoch(
            string portName,
            string expectedCcid,
            out long epoch)
        {
            epoch = 0;
            return false;
        }

        public Task<bool> VerifyPhysicalCcidAsync(
            string portName,
            string expectedCcid,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public bool TryReserveBatchImeiTarget(
            string owner,
            string portName,
            string ccid,
            string targetImei) => false;

        public void ReleaseBatchImeiReservations(string owner)
        {
        }

        public Task RefreshPortAsync(
            string portName,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> SendUssdForPortAsync(
            string portName,
            string ussdCode) => Task.FromResult(string.Empty);

        public Task<bool> ExecuteCallAsync(
            string portName,
            string recipient,
            int durationSeconds,
            string expectedCcid,
            Action<string> onStatus,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<string> QueueSmsAsync(
            string portName,
            string recipient,
            string content,
            string expectedCcid,
            CancellationToken cancellationToken) =>
            Task.FromResult(string.Empty);

        public IReadOnlyList<SmsInboxRecord> GetRecentSms(int count) =>
            Array.Empty<SmsInboxRecord>();

        public void Log(string message, string level) => Emit(message, level);
    }
}
