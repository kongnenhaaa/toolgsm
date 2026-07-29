using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using gsm.Models;
using gsm.ViewModels;

namespace gsm.Services;

/// <summary>
/// Explicit, one-shot real-device acceptance test. The request file is renamed
/// before it is parsed or any modem operation starts, so relaunching the app
/// with the same command line cannot repeat a paid SMS or call.
/// </summary>
public sealed class RealDeviceSmokeTestRunner
{
    public const string RequestArgument = "--real-device-smoke-request";
    private const string ExpectedProvider = "VinaPhone";
    private const string ExpectedCcidPrefix = "898402";
    private const string AutomaticUssd = "*101#";
    private const string AutomaticUssdDescription = "*101# tự động theo phản hồi COPS VinaPhone";
    private const string ManualUssd = "*101#";
    private const string CallRecipient = "900";
    private const int CallDurationSeconds = 15;
    private const string SmsRecipient = "888";
    private const string SmsBody = "data";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly IRealDeviceSmokeHost _host;
    private readonly string _resultsRoot;
    private readonly TimeProvider _timeProvider;
    private string _potentialActiveCallPort = string.Empty;

    public RealDeviceSmokeTestRunner(MainViewModel viewModel)
        : this(
            new MainViewModelSmokeHost(viewModel),
            Path.Combine(AppPaths.UserDataDirectory, "SmokeTests"),
            TimeProvider.System)
    {
    }

    internal RealDeviceSmokeTestRunner(
        IRealDeviceSmokeHost host,
        string resultsRoot,
        TimeProvider? timeProvider = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _resultsRoot = Path.GetFullPath(resultsRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Non-empty only while a smoke-test call may still exist in the modem.
    /// App shutdown uses this as a last-resort ATH target before closing COM.
    /// </summary>
    internal string PotentialActiveCallPort =>
        Volatile.Read(ref _potentialActiveCallPort);

    /// <summary>
    /// Returns false when the command line does not request a smoke test. All
    /// requested runs are contained and reported rather than crashing the GUI.
    /// </summary>
    public async Task<bool> RunIfRequestedAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetRequestPath(args, out string requestPath, out string argumentError))
        {
            if (!string.IsNullOrWhiteSpace(argumentError))
                _host.Log($"[REAL_DEVICE_SMOKE_REJECTED] {argumentError}", "ERROR");
            return false;
        }

        RealDeviceSmokeClaim claim;
        try
        {
            claim = ClaimAndReadRequest(requestPath);
        }
        catch (Exception ex)
        {
            _host.Log(
                $"[REAL_DEVICE_SMOKE_REJECTED] Không nhận request: {ex.Message}",
                "ERROR");
            return true;
        }

        AtomicSmokeResultStore store;
        try
        {
            store = AtomicSmokeResultStore.CreateNew(_resultsRoot, claim.RunId);
        }
        catch (Exception ex)
        {
            _host.Log(
                $"[REAL_DEVICE_SMOKE_REJECTED] Request đã được claim nhưng không tạo được thư mục kết quả: {ex.Message}; claimed={claim.ClaimedPath}",
                "ERROR");
            return true;
        }

        if (claim.Request.Scenario == RealDeviceSmokeScenario.ImeiUssdBatch)
        {
            await ExecuteBulkImeiUssdRequestAsync(
                    claim, store, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (claim.Request.Scenario == RealDeviceSmokeScenario.SmsOnly)
        {
            await ExecuteSmsOnlyRequestAsync(claim, store, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await ExecuteClaimedRequestAsync(claim, store, cancellationToken)
                .ConfigureAwait(false);
        }
        return true;
    }

    private async Task ExecuteClaimedRequestAsync(
        RealDeviceSmokeClaim claim,
        AtomicSmokeResultStore store,
        CancellationToken cancellationToken)
    {
        var state = new RealDeviceSmokeResult
        {
            RunId = claim.RunId,
            Outcome = RealDeviceSmokeOutcome.Running,
            CurrentStep = "startup",
            StartedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow,
            OriginalRequestPath = claim.OriginalPath,
            ClaimedRequestPath = claim.ClaimedPath,
            ResultPath = store.ResultPath,
            RequestedPortName = claim.Request.PortName?.Trim() ?? string.Empty,
            RequestedCcid = NormalizeDigits(claim.Request.ExpectedCcid),
            ContinuationOfRunId = claim.Request.ContinuationOfRunId?.Trim()
                ?? string.Empty,
            FixedActions = new RealDeviceSmokeActions
            {
                Provider = ExpectedProvider,
                ExpectedCcid = NormalizeDigits(claim.Request.ExpectedCcid),
                ContinuationOfRunId = claim.Request.ContinuationOfRunId?.Trim()
                    ?? string.Empty,
                AutomaticUssd = [AutomaticUssd],
                ManualUssd = ManualUssd,
                CallRecipient = CallRecipient,
                CallDurationSeconds = CallDurationSeconds,
                SmsRecipient = SmsRecipient,
                SmsBody = SmsBody
            }
        };

        using var logs = new RealDeviceSmokeLogCollector(_host, _timeProvider);
        Checkpoint(store, state, logs);
        _host.Log(
            $"[REAL_DEVICE_SMOKE_START] run={state.RunId}; result={state.ResultPath}",
            "INFO");

        try
        {
            BeginStep(store, state, logs, "select-port", chargeable: false,
                "Chờ một cổng VinaPhone Active có phiên SIM ổn định.");
            RealDeviceSmokePortSnapshot selected = await WaitForStablePortAsync(
                    claim.Request, cancellationToken)
                .ConfigureAwait(false);
            state.PortName = selected.PortName;
            state.PinnedCcid = selected.Ccid;
            state.InitialImei = selected.Imei;
            state.NetworkProvider = selected.NetworkProvider;
            CompleteStep(store, state, logs,
                $"Đã ghim {selected.PortName}; CCID={selected.Ccid}; IMEI={selected.Imei}; provider={selected.NetworkProvider}.");

            if (string.IsNullOrWhiteSpace(claim.Request.ContinuationOfRunId))
            {
            BeginStep(store, state, logs, "verify-current-imei", chargeable: false,
                "Xác minh IMEI hiện có của modem; chế độ nofake không tạo, đổi hoặc lưu backup IMEI.");
            state.TargetImei = NormalizeDigits(selected.Imei);
            if (!ImeiManagementService.IsUsableObservedImei(state.TargetImei))
            {
                FailKnown(store, state, logs,
                    $"IMEI hiện có không hợp lệ: {selected.Imei}.");
            }

            RealDeviceSmokePortSnapshot afterImei = await WaitForPinnedPortAsync(
                    state,
                    claim.Request.ImeiWaitSeconds,
                    requireTargetImei: true,
                    cancellationToken)
                .ConfigureAwait(false);
            CompleteStep(store, state, logs,
                $"IMEI hiện có đã được giữ nguyên và cổng đang Active: {afterImei.Imei}.");

            // Force a fresh SIM epoch so the configured automatic USSD plan also
            // proves that reconnecting preserves the modem's existing identity.
            DateTimeOffset automaticUssdStartedAt = UtcNow;
            BeginStep(store, state, logs, "refresh-preserve-imei", chargeable: false,
                "Mở lại riêng COM để kiểm tra IMEI hiện có được giữ nguyên và chạy USSD tự động trên epoch mới.");
            await _host.RefreshPortAsync(state.PortName, cancellationToken)
                .ConfigureAwait(false);
            RealDeviceSmokePortSnapshot afterRefresh = await WaitForPinnedPortAsync(
                    state,
                    claim.Request.ImeiWaitSeconds,
                    requireTargetImei: true,
                    cancellationToken)
                .ConfigureAwait(false);
            CompleteStep(store, state, logs,
                $"COM mở lại đúng CCID/IMEI: {afterRefresh.Ccid}/{afterRefresh.Imei}.");

            BeginStep(store, state, logs, "automatic-ussd", chargeable: false,
                $"Chờ hoàn tất USSD tự động {AutomaticUssdDescription}.");
            await logs.WaitForAsync(
                    state.PortName,
                    "[SAUTO_AUTO_USSD_RESULT]",
                    automaticUssdStartedAt,
                    TimeSpan.FromSeconds(claim.Request.AutomaticUssdWaitSeconds),
                    cancellationToken,
                    requiredText: $"code={AutomaticUssd}")
                .ConfigureAwait(false);
            CompleteStep(store, state, logs,
                $"Đã hoàn tất {AutomaticUssdDescription} trên đúng COM sau refresh.");

            BeginStep(store, state, logs, "manual-ussd-101", chargeable: false,
                "Chạy kiểm tra thủ công *101# qua pipeline USSD hiện hữu.");
            EnsurePinnedPortNow(state, requireTargetImei: true);
            string ussdResult = await _host.SendUssdForPortAsync(
                    state.PortName, ManualUssd)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            state.ManualUssdResult = ussdResult;
            if (!IsSuccessfulUssdResult(ussdResult))
            {
                FailKnown(store, state, logs,
                    $"*101# không trả dữ liệu hợp lệ: {ussdResult}");
            }
            EnsurePinnedPortNow(state, requireTargetImei: true);
            CompleteStep(store, state, logs,
                $"*101# trả dữ liệu hợp lệ ({ussdResult.Length} ký tự).");
            }
            else
            {
                BeginStep(
                    store,
                    state,
                    logs,
                    "continue-after-confirmed-hangup",
                    chargeable: false,
                    "Xác minh kết quả trước đã hoàn tất IMEI/USSD, cuộc gọi lỗi đã ATH và chưa gửi SMS.");
                RealDeviceSmokeResult prior = LoadContinuationResult(
                    claim.Request.ContinuationOfRunId);
                state.TargetImei = ValidateContinuationAndGetImei(
                    claim.Request,
                    prior,
                    selected);
                CompleteStep(
                    store,
                    state,
                    logs,
                    $"Tiếp tục từ run={prior.RunId}; giữ IMEI hiện có {state.TargetImei}; không đổi IMEI hoặc chạy lại USSD.");
            }

            BeginStep(store, state, logs, "call-900-15s", chargeable: true,
                "Chuẩn bị gọi 900 đúng 15 giây; không có retry ở runner.");
            EnsurePinnedPortNow(state, requireTargetImei: true);
            Interlocked.Exchange(ref _potentialActiveCallPort, state.PortName);
            var callEvidence = new ConcurrentQueue<string>();
            var stopwatch = Stopwatch.StartNew();
            bool callSucceeded = await _host.ExecuteCallAsync(
                    state.PortName,
                    CallRecipient,
                    CallDurationSeconds,
                    state.PinnedCcid,
                    message =>
                    {
                        if (!string.IsNullOrWhiteSpace(message))
                            callEvidence.Enqueue(TextEncodingNormalizer.RepairMojibake(message));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            string[] callLines = callEvidence.TakeLast(100).ToArray();
            state.CallElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3);
            state.CallEvidence = callLines;
            bool sawActiveVoiceSession = HasActiveCallEvidence(callLines);
            bool sawCompletedVoiceDuration = HasCompletedVoiceDurationEvidence(
                callLines, CallDurationSeconds);
            bool sawQualifiedVoiceSession = sawActiveVoiceSession
                || sawCompletedVoiceDuration;
            bool sawConfirmedHangup = HasConfirmedCallHangupEvidence(
                callLines, CallDurationSeconds);
            if (sawConfirmedHangup)
                Interlocked.Exchange(ref _potentialActiveCallPort, string.Empty);
            if (!callSucceeded
                || stopwatch.Elapsed < TimeSpan.FromSeconds(14)
                || stopwatch.Elapsed > TimeSpan.FromSeconds(45)
                || !sawQualifiedVoiceSession
                || !sawConfirmedHangup)
            {
                FailKnown(store, state, logs,
                    $"Call test không đạt; success={callSucceeded}; elapsed={stopwatch.Elapsed.TotalSeconds:0.000}s; active={sawActiveVoiceSession}; durationComplete={sawCompletedVoiceDuration}; hangupConfirmed={sawConfirmedHangup}.");
            }
            await WaitForPinnedPortAsync(
                    state,
                    claim.Request.PostOperationWaitSeconds,
                    requireTargetImei: true,
                    cancellationToken)
                .ConfigureAwait(false);
            CompleteStep(store, state, logs,
                $"Gọi 900 đã tạo phiên thoại và giữ đủ {CallDurationSeconds} giây; ATH được modem xác nhận và CLCC đã trống; active={sawActiveVoiceSession}; elapsed={stopwatch.Elapsed.TotalSeconds:0.000}s.");

            // SMS is deliberately last. Besides avoiding the built-in delayed
            // balance lookup colliding with the call, the incoming carrier reply
            // proves receive/durable-inbox health after every earlier operation.
            IReadOnlySet<string> baselineDeliveryIds = _host.GetRecentSms(1000)
                .Select(record => record.DeliveryId)
                .ToHashSet(StringComparer.Ordinal);
            DateTimeOffset smsStartedAt = UtcNow;
            BeginStep(store, state, logs, "sms-data-to-888", chargeable: true,
                "Chuẩn bị gửi chính xác 'data' đến 888; không có retry ở runner.");
            EnsurePinnedPortNow(state, requireTargetImei: true);
            string smsResult = await _host.QueueSmsAsync(
                    state.PortName,
                    SmsRecipient,
                    SmsBody,
                    state.PinnedCcid,
                    cancellationToken)
                .ConfigureAwait(false);
            state.SmsSendResult = smsResult;
            RealDeviceSmsSubmitDisposition smsDisposition =
                ClassifySmsSubmitResult(smsResult);
            if (smsDisposition == RealDeviceSmsSubmitDisposition.PrePayloadFailed)
            {
                FailKnown(store, state, logs,
                    $"SMS bị từ chối trước khi payload được submit; không có SMS để chờ: {smsResult}");
            }

            SetCurrentStepStatus(
                state,
                RealDeviceSmokeStepStatus.AwaitingResponse,
                smsDisposition == RealDeviceSmsSubmitDisposition.Confirmed
                    ? $"Modem đã xác nhận submit; đang chờ phản hồi mới từ {SmsRecipient} trong durable inbox."
                    : $"Payload đã qua Ctrl+Z nhưng phản hồi modem không chắc chắn; tuyệt đối không retry, đang chờ phản hồi mới từ {SmsRecipient} để chứng minh carrier đã nhận.");
            Checkpoint(store, state, logs);

            SmsInboxRecord incoming = await WaitForIncomingSmsAsync(
                    state.PortName,
                    smsStartedAt,
                    baselineDeliveryIds,
                    claim.Request.SmsResponseWaitSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            state.IncomingSms = incoming;
            await WaitForPinnedPortAsync(
                    state,
                    claim.Request.PostOperationWaitSeconds,
                    requireTargetImei: true,
                    cancellationToken)
                .ConfigureAwait(false);
            CompleteStep(store, state, logs,
                $"Đã nhận và đọc lại durable SMS delivery={incoming.DeliveryId}; sender={incoming.Sender}; chars={incoming.Content.Length}.");

            state.Outcome = RealDeviceSmokeOutcome.Passed;
            state.CurrentStep = "complete";
            state.CompletedAtUtc = UtcNow;
            state.Error = string.Empty;
            Checkpoint(store, state, logs);
            _host.Log(
                $"[{state.PortName}] [REAL_DEVICE_SMOKE_PASSED] run={state.RunId}; result={state.ResultPath}",
                "SUCCESS");
        }
        catch (OperationCanceledException ex)
        {
            FinalizeInterrupted(store, state, logs, ex.Message, cancelled: true);
        }
        catch (RealDeviceSmokeRunException ex)
        {
            FinalizeInterrupted(store, state, logs, ex.Message, cancelled: false);
        }
        catch (Exception ex)
        {
            FinalizeInterrupted(store, state, logs, ex.ToString(), cancelled: false);
        }
    }

    private async Task ExecuteBulkImeiUssdRequestAsync(
        RealDeviceSmokeClaim claim,
        AtomicSmokeResultStore store,
        CancellationToken cancellationToken)
    {
        string[] requestedPorts = claim.Request.PortNames
            .Select(port => port.Trim().ToUpperInvariant())
            .OrderBy(ParsePortNumber)
            .ToArray();
        var state = new RealDeviceSmokeResult
        {
            RunId = claim.RunId,
            Scenario = RealDeviceSmokeScenario.ImeiUssdBatch,
            ResumeRunId = (claim.Request.ResumeRunId ?? string.Empty).Trim(),
            Outcome = RealDeviceSmokeOutcome.Running,
            CurrentStep = "inventory",
            StartedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow,
            OriginalRequestPath = claim.OriginalPath,
            ClaimedRequestPath = claim.ClaimedPath,
            ResultPath = store.ResultPath,
            RequestedPortName = string.Join(",", requestedPorts),
            FixedActions = new RealDeviceSmokeActions
            {
                Provider = ExpectedProvider,
                AutomaticUssd = [AutomaticUssd]
            }
        };
        using var logs = new RealDeviceSmokeLogCollector(_host, _timeProvider);
        object stateGate = new();

        void SaveState()
        {
            lock (stateGate)
                Checkpoint(store, state, logs);
        }

        void UpdatePort(
            RealDeviceSmokeBulkPortResult port,
            Action<RealDeviceSmokeBulkPortResult> update)
        {
            lock (stateGate)
            {
                update(port);
                port.UpdatedAtUtc = UtcNow;
                int finished = state.BulkPorts.Count(item =>
                    item.Outcome is RealDeviceSmokeOutcome.Passed
                        or RealDeviceSmokeOutcome.Failed
                        or RealDeviceSmokeOutcome.Cancelled);
                state.CurrentStep = $"ports-{finished}-of-{state.BulkPorts.Count}";
                Checkpoint(store, state, logs);
            }
        }

        SaveState();
        _host.Log(
            $"[BULK_IMEI_USSD_START] run={state.RunId}; ports={requestedPorts.Length}; parallel={claim.Request.MaxParallelPorts}; result={state.ResultPath}",
            "INFO");

        try
        {
            IReadOnlyList<RealDeviceSmokePortSnapshot> inventory =
                await WaitForStableBulkInventoryAsync(
                        requestedPorts,
                        claim.Request.PortWaitSeconds,
                        claim.Request.StablePortSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);

            RealDeviceSmokeResult? prior = string.IsNullOrWhiteSpace(
                    claim.Request.ResumeRunId)
                ? null
                : LoadBulkResumeResult(
                    claim.Request.ResumeRunId,
                    requestedPorts,
                    inventory);

            var priorByPort = prior?.BulkPorts.ToDictionary(
                port => port.PortName,
                StringComparer.OrdinalIgnoreCase);
            foreach (RealDeviceSmokePortSnapshot snapshot in inventory
                         .OrderBy(port => port.PortNumber))
            {
                RealDeviceSmokeBulkPortResult? previous = null;
                priorByPort?.TryGetValue(snapshot.PortName, out previous);
                string target = NormalizeDigits(snapshot.Imei);
                if (!ImeiManagementService.IsUsableObservedImei(target))
                {
                    throw new RealDeviceSmokeRunException(
                        $"IMEI hiện có không hợp lệ trên {snapshot.PortName}: {target}.");
                }

                state.BulkPorts.Add(new RealDeviceSmokeBulkPortResult
                {
                    PortName = snapshot.PortName,
                    PhysicalIndex = snapshot.PhysicalIndex,
                    PinnedCcid = NormalizeDigits(snapshot.Ccid),
                    InitialImei = NormalizeDigits(snapshot.Imei),
                    TargetImei = target,
                    Outcome = previous?.Outcome == RealDeviceSmokeOutcome.Passed
                        ? RealDeviceSmokeOutcome.Passed
                        : RealDeviceSmokeOutcome.Running,
                    CurrentStep = previous?.Outcome == RealDeviceSmokeOutcome.Passed
                        ? "resume-verify-passed"
                        : "current-imei-prepared",
                    ImeiAttempts = previous?.ImeiAttempts ?? 0,
                    UssdRefreshAttempts = previous?.UssdRefreshAttempts ?? 0,
                    ImeiVerified = previous?.ImeiVerified ?? false,
                    ImeiBackupCommitted = false,
                    NetworkReady = previous?.NetworkReady ?? false,
                    Ussd111Passed = previous?.Ussd111Passed ?? false,
                    Ussd101DirectPassed = previous?.Ussd101DirectPassed ?? false,
                    UssdInitialComplete = previous?.UssdInitialComplete ?? false,
                    Ussd101Evidence = previous?.Ussd101Evidence ?? string.Empty,
                    StartedAtUtc = previous?.StartedAtUtc ?? UtcNow,
                    UpdatedAtUtc = UtcNow,
                    CompletedAtUtc = previous?.CompletedAtUtc,
                    Error = string.Empty
                });
            }

            await VerifyBulkPhysicalIdentitiesAsync(
                    state.BulkPorts,
                    claim.Request.MaxParallelPorts,
                    cancellationToken)
                .ConfigureAwait(false);

            // Persist the observed identities before refreshing any ports. The
            // nofake scenario never generates, reserves, or writes an IMEI.
            state.CurrentStep = "current-imeis-checkpointed";
            SaveState();
            _host.Log(
                $"[BULK_CURRENT_IMEIS_CHECKPOINTED] run={state.RunId}; ports={state.BulkPorts.Count}; no-imei-write=true; no-call=true; no-sms=true",
                "SUCCESS");
            await VerifyBulkPhysicalIdentitiesAsync(
                    state.BulkPorts,
                    claim.Request.MaxParallelPorts,
                    cancellationToken)
                .ConfigureAwait(false);

            BackendConcurrency.ConfigureThreadPool(state.BulkPorts.Count);
            RealDeviceSmokeBulkPortResult[][] waves = state.BulkPorts
                .OrderBy(port => ParsePortNumber(port.PortName))
                .Chunk(claim.Request.MaxParallelPorts)
                .Select(chunk => chunk.ToArray())
                .ToArray();
            for (int waveIndex = 0; waveIndex < waves.Length; waveIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await VerifyBulkPhysicalIdentitiesAsync(
                        waves[waveIndex],
                        waves[waveIndex].Length,
                        cancellationToken)
                    .ConfigureAwait(false);
                _host.Log(
                    $"[BULK_WAVE_START] run={state.RunId}; wave={waveIndex + 1}/{waves.Length}; ports={string.Join(",", waves[waveIndex].Select(port => port.PortName))}",
                    "INFO");
                Task[] waveTasks = waves[waveIndex].Select(port =>
                    ExecuteBulkImeiUssdPortAsync(
                            port,
                            claim.Request,
                            logs,
                            UpdatePort,
                            cancellationToken))
                    .ToArray();
                await Task.WhenAll(waveTasks).ConfigureAwait(false);
                _host.Log(
                    $"[BULK_WAVE_COMPLETE] run={state.RunId}; wave={waveIndex + 1}/{waves.Length}",
                    "INFO");
                if (waveIndex + 1 < waves.Length
                    && claim.Request.WaveCooldownSeconds > 0)
                {
                    await Task.Delay(
                            TimeSpan.FromSeconds(
                                claim.Request.WaveCooldownSeconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            int passed = state.BulkPorts.Count(port =>
                port.Outcome == RealDeviceSmokeOutcome.Passed);
            state.Outcome = passed == state.BulkPorts.Count
                ? RealDeviceSmokeOutcome.Passed
                : RealDeviceSmokeOutcome.Failed;
            state.CurrentStep = state.Outcome == RealDeviceSmokeOutcome.Passed
                ? "complete"
                : "failed";
            state.CompletedAtUtc = UtcNow;
            state.Error = state.Outcome == RealDeviceSmokeOutcome.Passed
                ? string.Empty
                : $"Chỉ {passed}/{state.BulkPorts.Count} cổng đạt.";
            SaveState();
            _host.Log(
                $"[BULK_IMEI_USSD_COMPLETE] run={state.RunId}; passed={passed}/{state.BulkPorts.Count}; outcome={state.Outcome}; result={state.ResultPath}",
                state.Outcome == RealDeviceSmokeOutcome.Passed ? "SUCCESS" : "ERROR");
        }
        catch (OperationCanceledException ex)
        {
            lock (stateGate)
            {
                foreach (RealDeviceSmokeBulkPortResult port in state.BulkPorts
                             .Where(port => port.Outcome == RealDeviceSmokeOutcome.Running))
                {
                    port.Outcome = RealDeviceSmokeOutcome.Cancelled;
                    port.CurrentStep = port.ImeiAttempts == 0
                        ? "cancelled-before-start"
                        : "cancelled-after-recovery";
                    port.Error = "Batch đã bị hủy; snapshot IMEI hiện có vẫn được giữ để resume.";
                    port.UpdatedAtUtc = UtcNow;
                    port.CompletedAtUtc = UtcNow;
                }
            }
            state.Outcome = RealDeviceSmokeOutcome.Cancelled;
            state.CurrentStep = "cancelled";
            state.Error = ex.Message;
            state.CompletedAtUtc = UtcNow;
            SaveState();
        }
        catch (Exception ex)
        {
            state.Outcome = RealDeviceSmokeOutcome.Failed;
            state.CurrentStep = "failed";
            state.Error = ex.Message;
            state.CompletedAtUtc = UtcNow;
            SaveState();
            _host.Log(
                $"[BULK_IMEI_USSD_FAILED] run={state.RunId}; error={ex.Message}; result={state.ResultPath}",
                "ERROR");
        }
    }

    /// <summary>
    /// Chỉ kiểm tra đường nhận SMS: gửi đúng một SMS 'data' tới 888 rồi chờ
    /// phản hồi xuất hiện trong durable inbox. Không chạm vào IMEI, không gọi.
    /// </summary>
    private async Task ExecuteSmsOnlyRequestAsync(
        RealDeviceSmokeClaim claim,
        AtomicSmokeResultStore store,
        CancellationToken cancellationToken)
    {
        var state = new RealDeviceSmokeResult
        {
            RunId = claim.RunId,
            Scenario = RealDeviceSmokeScenario.SmsOnly,
            Outcome = RealDeviceSmokeOutcome.Running,
            CurrentStep = "startup",
            StartedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow,
            OriginalRequestPath = claim.OriginalPath,
            ClaimedRequestPath = claim.ClaimedPath,
            ResultPath = store.ResultPath,
            RequestedPortName = claim.Request.PortName?.Trim() ?? string.Empty,
            RequestedCcid = NormalizeDigits(claim.Request.ExpectedCcid),
            FixedActions = new RealDeviceSmokeActions
            {
                Provider = ExpectedProvider,
                ExpectedCcid = NormalizeDigits(claim.Request.ExpectedCcid),
                SmsRecipient = SmsRecipient,
                SmsBody = SmsBody
            }
        };

        using var logs = new RealDeviceSmokeLogCollector(_host, _timeProvider);
        Checkpoint(store, state, logs);
        _host.Log(
            $"[REAL_DEVICE_SMS_ONLY_START] run={state.RunId}; port={state.RequestedPortName}; result={state.ResultPath}",
            "INFO");

        try
        {
            BeginStep(store, state, logs, "select-port", chargeable: false,
                "Chờ đúng cổng đã ghim ở trạng thái Active/VinaPhone ổn định.");
            RealDeviceSmokePortSnapshot selected = await WaitForStablePortAsync(
                    claim.Request, cancellationToken)
                .ConfigureAwait(false);
            state.PortName = selected.PortName;
            state.PinnedCcid = selected.Ccid;
            state.InitialImei = selected.Imei;
            // IMEI hiện tại là mốc để phát hiện hot-swap; scenario này không ghi IMEI.
            state.TargetImei = NormalizeDigits(selected.Imei);
            state.NetworkProvider = selected.NetworkProvider;
            CompleteStep(store, state, logs,
                $"Đã ghim {selected.PortName}; CCID={selected.Ccid}; IMEI={selected.Imei}; provider={selected.NetworkProvider}.");

            IReadOnlySet<string> baselineDeliveryIds = _host.GetRecentSms(1000)
                .Select(record => record.DeliveryId)
                .ToHashSet(StringComparer.Ordinal);
            DateTimeOffset smsStartedAt = UtcNow;
            BeginStep(store, state, logs, "sms-data-to-888", chargeable: true,
                "Chuẩn bị gửi chính xác 'data' đến 888; không có retry ở runner.");
            EnsurePinnedPortNow(state, requireTargetImei: true);
            string smsResult = await _host.QueueSmsAsync(
                    state.PortName,
                    SmsRecipient,
                    SmsBody,
                    state.PinnedCcid,
                    cancellationToken)
                .ConfigureAwait(false);
            state.SmsSendResult = smsResult;
            RealDeviceSmsSubmitDisposition smsDisposition =
                ClassifySmsSubmitResult(smsResult);
            if (smsDisposition == RealDeviceSmsSubmitDisposition.PrePayloadFailed)
            {
                FailKnown(store, state, logs,
                    $"SMS bị từ chối trước khi payload được submit; không có SMS để chờ: {smsResult}");
            }

            SetCurrentStepStatus(
                state,
                RealDeviceSmokeStepStatus.AwaitingResponse,
                smsDisposition == RealDeviceSmsSubmitDisposition.Confirmed
                    ? $"Modem đã xác nhận submit; đang chờ phản hồi mới từ {SmsRecipient} trong durable inbox."
                    : $"Payload đã qua Ctrl+Z nhưng phản hồi modem không chắc chắn; tuyệt đối không retry, đang chờ phản hồi mới từ {SmsRecipient}.");
            Checkpoint(store, state, logs);

            SmsInboxRecord incoming = await WaitForIncomingSmsAsync(
                    state.PortName,
                    smsStartedAt,
                    baselineDeliveryIds,
                    claim.Request.SmsResponseWaitSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            state.IncomingSms = incoming;
            await WaitForPinnedPortAsync(
                    state,
                    claim.Request.PostOperationWaitSeconds,
                    requireTargetImei: true,
                    cancellationToken)
                .ConfigureAwait(false);
            CompleteStep(store, state, logs,
                $"Đã nhận và đọc lại durable SMS delivery={incoming.DeliveryId}; sender={incoming.Sender}; chars={incoming.Content.Length}; trễ={(incoming.ReceivedAtUtc - smsStartedAt).TotalSeconds:0.0}s.");

            state.Outcome = RealDeviceSmokeOutcome.Passed;
            state.CurrentStep = "complete";
            state.CompletedAtUtc = UtcNow;
            state.Error = string.Empty;
            Checkpoint(store, state, logs);
            _host.Log(
                $"[{state.PortName}] [REAL_DEVICE_SMS_ONLY_PASSED] run={state.RunId}; result={state.ResultPath}",
                "SUCCESS");
        }
        catch (OperationCanceledException ex)
        {
            FinalizeInterrupted(store, state, logs, ex.Message, cancelled: true);
        }
        catch (RealDeviceSmokeRunException ex)
        {
            FinalizeInterrupted(store, state, logs, ex.Message, cancelled: false);
        }
        catch (Exception ex)
        {
            FinalizeInterrupted(store, state, logs, ex.ToString(), cancelled: false);
        }
    }

    private async Task VerifyBulkPhysicalIdentitiesAsync(
        IReadOnlyList<RealDeviceSmokeBulkPortResult> ports,
        int maximumParallel,
        CancellationToken cancellationToken)
    {
        foreach (RealDeviceSmokeBulkPortResult[] wave in ports
                     .OrderBy(port => ParsePortNumber(port.PortName))
                     .Chunk(Math.Max(1, maximumParallel)))
        {
            bool[] verified = await Task.WhenAll(wave.Select(async expected =>
            {
                RealDeviceSmokePortSnapshot? snapshot = FindExactBulkIdentity(expected);
                if (snapshot == null
                    || snapshot.IsRebooting
                    || !ImeiManagementService.IsUsableObservedImei(
                        NormalizeDigits(snapshot.Imei))
                    || !ImeiManagementService.AreEquivalentImei(
                        snapshot.Imei,
                        expected.TargetImei))
                {
                    return false;
                }
                return await _host.VerifyPhysicalCcidAsync(
                        expected.PortName,
                        expected.PinnedCcid,
                        cancellationToken)
                    .ConfigureAwait(false);
            })).ConfigureAwait(false);
            if (verified.Any(value => !value))
            {
                string failed = string.Join(",", wave
                    .Where((_, index) => !verified[index])
                    .Select(port => port.PortName));
                throw new RealDeviceSmokeRunException(
                    $"Physical CCID preflight thất bại trên {failed}; wave chưa được phép chạy.");
            }
        }
    }

    private async Task ExecuteBulkImeiUssdPortAsync(
        RealDeviceSmokeBulkPortResult port,
        RealDeviceSmokeRequest request,
        RealDeviceSmokeLogCollector logs,
        Action<RealDeviceSmokeBulkPortResult, Action<RealDeviceSmokeBulkPortResult>> update,
        CancellationToken cancellationToken)
    {
        try
        {
            RealDeviceSmokePortSnapshot? live = FindExactBulkIdentity(port);
            bool alreadyVerified = IsCurrentImeiReady(live, port.TargetImei);
            if (port.Outcome == RealDeviceSmokeOutcome.Passed
                && alreadyVerified)
            {
                update(port, item =>
                {
                    item.ImeiVerified = true;
                    item.CurrentStep = "resume-verified-passed";
                    item.Error = string.Empty;
                });
                return;
            }

            update(port, item =>
            {
                item.Outcome = RealDeviceSmokeOutcome.Running;
                item.CurrentStep = alreadyVerified
                    ? "current-imei-resume-verified"
                    : "wait-current-imei";
                item.Error = string.Empty;
            });

            bool imeiReady = alreadyVerified;
            for (int attempt = 1; !imeiReady && attempt <= 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                update(port, item =>
                {
                    item.ImeiAttempts++;
                    item.CurrentStep = $"verify-current-imei-{attempt}";
                });

                await _host.RefreshPortAsync(
                        port.PortName, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    live = await WaitForBulkTargetAsync(
                            port,
                            TimeSpan.FromSeconds(request.ImeiWaitSeconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (RealDeviceSmokeRunException) when (attempt < 3)
                {
                    live = null;
                }
                imeiReady = IsCurrentImeiReady(live, port.TargetImei);
                if (imeiReady) break;

                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            if (!imeiReady)
            {
                throw new RealDeviceSmokeRunException(
                    $"{port.PortName} chưa Active/Ready với IMEI hiện có {port.TargetImei} sau 3 lần refresh; nofake không ghi IMEI.");
            }

            update(port, item =>
            {
                item.ImeiVerified = true;
                item.CurrentStep = "refresh-for-new-ussd-epoch";
            });

            bool ussdPassed = false;
            string lastUssdError = string.Empty;
            for (int cycle = 1; cycle <= 3 && !ussdPassed; cycle++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DateTimeOffset refreshStartedAt = UtcNow;
                long refreshEvidenceSequence = logs.CurrentSequence;
                update(port, item =>
                {
                    item.UssdRefreshAttempts++;
                    item.NetworkReady = false;
                    item.Ussd111Passed = false;
                    item.Ussd101DirectPassed = false;
                    item.UssdInitialComplete = false;
                    item.Ussd101Evidence = string.Empty;
                    item.CurrentStep = $"ussd-refresh-{cycle}";
                });

                try
                {
                    await _host.RefreshPortAsync(
                            port.PortName, cancellationToken)
                        .ConfigureAwait(false);
                    await WaitForBulkTargetAsync(
                            port,
                            TimeSpan.FromSeconds(request.ImeiWaitSeconds),
                            cancellationToken)
                        .ConfigureAwait(false);

                    DateTimeOffset ussdDeadline = UtcNow.AddSeconds(
                        request.AutomaticUssdWaitSeconds);
                    if (!_host.TryGetCurrentSessionEpoch(
                            port.PortName,
                            port.PinnedCcid,
                            out _))
                    {
                        throw new RealDeviceSmokeRunException(
                            "Không đọc được epoch phiên SIM sau refresh.");
                    }
                    string sessionMarker =
                        $"ccid={port.PinnedCcid};";
                    RealDeviceSmokeEvidence network = await logs.WaitForAsync(
                            port.PortName,
                            "[SAUTO_NETWORK_READY]",
                            refreshStartedAt,
                            Remaining(ussdDeadline),
                            cancellationToken,
                            afterSequence: refreshEvidenceSequence,
                            requiredText: sessionMarker)
                        .ConfigureAwait(false);
                    update(port, item =>
                    {
                        item.NetworkReady = true;
                        item.CurrentStep = "ussd-111";
                    });

                    await logs.WaitForAsync(
                            port.PortName,
                            "[SAUTO_AUTO_USSD_RESULT]",
                            network.AtUtc,
                            Remaining(ussdDeadline),
                            cancellationToken,
                            afterSequence: network.Sequence,
                            requiredText: $"code={AutomaticUssd}")
                        .ConfigureAwait(false);
                    update(port, item =>
                    {
                        item.Ussd111Passed = true;
                        item.CurrentStep = "ussd-initial-complete";
                    });
                    live = await WaitForBulkTargetAsync(
                            port,
                            TimeSpan.FromSeconds(request.ImeiWaitSeconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                    ussdPassed = IsCurrentImeiReady(live, port.TargetImei);
                    if (ussdPassed)
                    {
                        update(port, item =>
                        {
                            item.UssdInitialComplete = true;
                            item.CurrentStep = "complete";
                        });
                    }
                }
                catch (RealDeviceSmokeRunException ex)
                {
                    lastUssdError = ex.Message;
                    _host.Log(
                        $"[{port.PortName}] [BULK_USSD_RETRY] cycle={cycle}/3; {ex.Message}",
                        "WARN");
                }
            }
            if (!ussdPassed)
            {
                throw new RealDeviceSmokeRunException(
                    $"{port.PortName} chưa hoàn tất {AutomaticUssdDescription} "
                    + $"sau 3 epoch refresh: {lastUssdError}");
            }

            update(port, item =>
            {
                item.Outcome = RealDeviceSmokeOutcome.Passed;
                item.CurrentStep = "complete";
                item.CompletedAtUtc = UtcNow;
                item.Error = string.Empty;
            });
            _host.Log(
                $"[{port.PortName}] [BULK_IMEI_USSD_PORT_PASSED] CCID={port.PinnedCcid}; "
                + $"IMEI={port.TargetImei}; automatic={AutomaticUssd}; manual101=false",
                "SUCCESS");
        }
        catch (OperationCanceledException ex)
        {
            update(port, item =>
            {
                item.Outcome = RealDeviceSmokeOutcome.Cancelled;
                item.CurrentStep = "cancelled";
                item.CompletedAtUtc = UtcNow;
                item.Error = ex.Message;
            });
            throw;
        }
        catch (Exception ex)
        {
            update(port, item =>
            {
                item.Outcome = RealDeviceSmokeOutcome.Failed;
                item.CurrentStep = "failed";
                item.CompletedAtUtc = UtcNow;
                item.Error = ex.Message;
            });
            _host.Log(
                $"[{port.PortName}] [BULK_IMEI_USSD_PORT_FAILED] CCID={port.PinnedCcid}; target={port.TargetImei}; error={ex.Message}",
                "ERROR");
        }
    }

    private async Task<IReadOnlyList<RealDeviceSmokePortSnapshot>>
        WaitForStableBulkInventoryAsync(
            IReadOnlyList<string> requestedPorts,
            int timeoutSeconds,
            int stableSeconds,
            CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = UtcNow.AddSeconds(timeoutSeconds);
        DateTimeOffset stableSince = UtcNow;
        string lastSignature = string.Empty;
        while (UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<RealDeviceSmokePortSnapshot> snapshots = _host.GetPorts();
            RealDeviceSmokePortSnapshot[] inventory = requestedPorts
                .Select(name => snapshots.FirstOrDefault(port =>
                    string.Equals(
                        port.PortName, name, StringComparison.OrdinalIgnoreCase)))
                .Where(port => port != null)
                .Cast<RealDeviceSmokePortSnapshot>()
                .OrderBy(port => port.PortNumber)
                .ToArray();
            bool valid = inventory.Length == requestedPorts.Count
                && inventory.All(IsSafeBulkInventoryPort)
                && inventory.Select(port => NormalizeDigits(port.Ccid))
                    .Distinct(StringComparer.Ordinal).Count() == inventory.Length;
            string signature = valid
                ? string.Join("|", inventory.Select(port =>
                    $"{port.PortName}:{NormalizeDigits(port.Ccid)}:{NormalizeDigits(port.Imei)}"))
                : string.Empty;
            if (!valid || !string.Equals(
                    signature, lastSignature, StringComparison.Ordinal))
            {
                lastSignature = signature;
                stableSince = UtcNow;
            }
            else if (UtcNow - stableSince >= TimeSpan.FromSeconds(stableSeconds))
            {
                return inventory;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                .ConfigureAwait(false);
        }

        string diagnostic = string.Join("; ", _host.GetPorts()
            .Where(port => requestedPorts.Contains(
                port.PortName, StringComparer.OrdinalIgnoreCase))
            .OrderBy(port => port.PortNumber)
            .Select(port =>
                $"{port.PortName}:ccid={NormalizeDigits(port.Ccid)},imei={NormalizeDigits(port.Imei)},reboot={port.IsRebooting}"));
        throw new RealDeviceSmokeRunException(
            $"Không ghim được đủ {requestedPorts.Count} COM/CCID/IMEI hiện có ổn định. {diagnostic}");
    }

    private static bool IsSafeBulkInventoryPort(
        RealDeviceSmokePortSnapshot port)
    {
        string ccid = NormalizeDigits(port.Ccid);
        return !port.IsRebooting
            && ccid.Length == 20
            && ccid.StartsWith(ExpectedCcidPrefix, StringComparison.Ordinal)
            && ImeiManagementService.IsUsableObservedImei(
                NormalizeDigits(port.Imei));
    }

    private RealDeviceSmokePortSnapshot? FindExactBulkIdentity(
        RealDeviceSmokeBulkPortResult expected) => _host.GetPorts()
        .FirstOrDefault(port =>
            string.Equals(
                port.PortName,
                expected.PortName,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                NormalizeDigits(port.Ccid),
                NormalizeDigits(expected.PinnedCcid),
                StringComparison.Ordinal));

    internal static bool IsCurrentImeiReady(
        RealDeviceSmokePortSnapshot? port,
        string expectedImei) =>
        port != null
        && port.IsActive
        && port.IsReadyForOperation
        && !port.IsRebooting
        && ImeiManagementService.AreEquivalentImei(
            port.Imei, expectedImei);

    private async Task<RealDeviceSmokePortSnapshot?> WaitForBulkTargetAsync(
        RealDeviceSmokeBulkPortResult expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = UtcNow.Add(timeout);
        while (UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RealDeviceSmokePortSnapshot? live = FindExactBulkIdentity(expected);
            if (IsCurrentImeiReady(live, expected.TargetImei))
            {
                return live;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new RealDeviceSmokeRunException(
            $"Hết hạn chờ {expected.PortName} Active với CCID={expected.PinnedCcid} và IMEI={expected.TargetImei}.");
    }

    private RealDeviceSmokeResult LoadBulkResumeResult(
        string runId,
        IReadOnlyList<string> requestedPorts,
        IReadOnlyList<RealDeviceSmokePortSnapshot> inventory)
    {
        string normalizedRunId = runId.Trim();
        string resultPath = Path.Combine(
            _resultsRoot, normalizedRunId, "result.json");
        if (!File.Exists(resultPath))
            throw new RealDeviceSmokeRunException(
                $"Không tìm thấy bulk result để resume: {resultPath}");
        RealDeviceSmokeResult prior;
        try
        {
            prior = JsonSerializer.Deserialize<RealDeviceSmokeResult>(
                    File.ReadAllText(resultPath, Encoding.UTF8), JsonOptions)
                ?? throw new RealDeviceSmokeRunException(
                    $"Bulk result {normalizedRunId} rỗng.");
        }
        catch (JsonException ex)
        {
            throw new RealDeviceSmokeRunException(
                $"Bulk result {normalizedRunId} không đọc được: {ex.Message}");
        }

        string[] priorPorts = prior.BulkPorts
            .Select(port => port.PortName)
            .OrderBy(ParsePortNumber)
            .ToArray();
        bool samePorts = prior.Scenario == RealDeviceSmokeScenario.ImeiUssdBatch
            && priorPorts.SequenceEqual(
                requestedPorts.OrderBy(ParsePortNumber),
                StringComparer.OrdinalIgnoreCase);
        bool exactLiveIdentities = prior.BulkPorts.All(previous =>
            inventory.Any(current =>
                string.Equals(
                    current.PortName,
                    previous.PortName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    NormalizeDigits(current.Ccid),
                    NormalizeDigits(previous.PinnedCcid),
                    StringComparison.Ordinal)
                && ImeiManagementService.AreEquivalentImei(
                    current.Imei,
                    previous.TargetImei)));
        bool validTargets = prior.BulkPorts.Count == requestedPorts.Count
            && prior.BulkPorts.All(port =>
                ImeiManagementService.IsUsableObservedImei(
                    NormalizeDigits(port.TargetImei)));
        if (!samePorts || !exactLiveIdentities || !validTargets)
        {
            throw new RealDeviceSmokeRunException(
                "Bulk resume bị chặn: port/CCID/IMEI hiện có không trùng tuyệt đối với checkpoint trước.");
        }

        return prior;
    }

    private static int ParsePortNumber(string portName) =>
        int.TryParse(Regex.Match(portName ?? string.Empty, @"\d+").Value,
            out int number)
            ? number
            : int.MaxValue;

    private TimeSpan Remaining(DateTimeOffset deadline)
    {
        TimeSpan remaining = deadline - UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new RealDeviceSmokeRunException(
                "Hết thời gian chờ chuỗi USSD trực tiếp.");
        return remaining;
    }

    private async Task<RealDeviceSmokePortSnapshot> WaitForStablePortAsync(
        RealDeviceSmokeRequest request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = UtcNow.AddSeconds(request.PortWaitSeconds);
        DateTimeOffset stableSince = UtcNow;
        RealDeviceSmokePortSnapshot? candidate = null;
        while (UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RealDeviceSmokePortSnapshot? current = SelectEligiblePort(
                _host.GetPorts(), request.PortName, request.ExpectedCcid);
            if (current == null)
            {
                candidate = null;
                stableSince = UtcNow;
            }
            else if (candidate == null
                || !SameIdentity(candidate, current))
            {
                candidate = current;
                stableSince = UtcNow;
            }
            else if (UtcNow - stableSince >=
                TimeSpan.FromSeconds(request.StablePortSeconds))
            {
                return current;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new RealDeviceSmokeRunException(
            request.PortName is { Length: > 0 }
                ? $"Hết hạn chờ {request.PortName} Active/VinaPhone ổn định."
                : "Hết hạn chờ một cổng Active/VinaPhone ổn định.");
    }

    private RealDeviceSmokeResult LoadContinuationResult(string runId)
    {
        string normalizedRunId = runId.Trim();
        string resultPath = Path.Combine(
            _resultsRoot, normalizedRunId, "result.json");
        if (!File.Exists(resultPath))
        {
            throw new RealDeviceSmokeRunException(
                $"Không tìm thấy result.json của run tiếp nối {normalizedRunId}.");
        }

        try
        {
            return JsonSerializer.Deserialize<RealDeviceSmokeResult>(
                    File.ReadAllText(resultPath, Encoding.UTF8),
                    JsonOptions)
                ?? throw new RealDeviceSmokeRunException(
                    $"Kết quả run {normalizedRunId} rỗng.");
        }
        catch (JsonException ex)
        {
            throw new RealDeviceSmokeRunException(
                $"Kết quả run {normalizedRunId} không đọc được: {ex.Message}");
        }
    }

    internal static string ValidateContinuationAndGetImei(
        RealDeviceSmokeRequest request,
        RealDeviceSmokeResult prior,
        RealDeviceSmokePortSnapshot selected)
    {
        string continuationId = request.ContinuationOfRunId?.Trim()
            ?? string.Empty;
        string expectedCcid = NormalizeDigits(request.ExpectedCcid);
        string targetImei = NormalizeDigits(prior.TargetImei);
        bool PassedAny(params string[] names) =>
            prior.Steps.Any(step =>
                step.Status == RealDeviceSmokeStepStatus.Passed
                && names.Contains(step.Name, StringComparer.Ordinal));

        bool requiredStepsPassed =
            PassedAny("select-port")
            && PassedAny("verify-current-imei", "create-sauto-imei")
            && PassedAny("refresh-preserve-imei", "refresh-after-imei")
            && PassedAny("automatic-ussd", "automatic-ussd-111-101")
            && PassedAny("manual-ussd-101");
        bool inheritedVerifiedState = prior.Steps.Any(step =>
            string.Equals(
                step.Name,
                "continue-after-confirmed-hangup",
                StringComparison.Ordinal)
            && step.Status == RealDeviceSmokeStepStatus.Passed);
        bool hasVerifiedImeiAndUssdState = requiredStepsPassed
            || inheritedVerifiedState;
        bool callFailedAndEnded = prior.Steps.Any(step =>
                string.Equals(
                    step.Name, "call-900-15s", StringComparison.Ordinal)
                && step.Status == RealDeviceSmokeStepStatus.Failed)
            && HasConfirmedCallHangupEvidence(
                prior.CallEvidence, CallDurationSeconds);
        bool smsWasNeverStarted = prior.Steps.All(step =>
                !string.Equals(
                    step.Name, "sms-data-to-888", StringComparison.Ordinal))
            && string.IsNullOrWhiteSpace(prior.SmsSendResult)
            && prior.IncomingSms == null;

        if (prior.Outcome != RealDeviceSmokeOutcome.Failed
            || !string.Equals(
                prior.RunId, continuationId, StringComparison.Ordinal)
            || !string.Equals(
                prior.PortName, selected.PortName,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                NormalizeDigits(prior.PinnedCcid), expectedCcid,
                StringComparison.Ordinal)
            || !string.Equals(
                NormalizeDigits(selected.Ccid), expectedCcid,
                StringComparison.Ordinal)
            || !ImeiManagementService.IsUsableObservedImei(targetImei)
            || !ImeiManagementService.AreEquivalentImei(
                selected.Imei, targetImei)
            || !hasVerifiedImeiAndUssdState
            || !callFailedAndEnded
            || !smsWasNeverStarted)
        {
            throw new RealDeviceSmokeRunException(
                "Run tiếp nối không đạt điều kiện an toàn: phải cùng COM/CCID/IMEI hiện có, các bước xác minh/USSD đã Passed, call cũ Failed nhưng ATH+CLCC trống, và SMS chưa từng bắt đầu.");
        }

        return targetImei;
    }

    private async Task<RealDeviceSmokePortSnapshot> WaitForPinnedPortAsync(
        RealDeviceSmokeResult state,
        int timeoutSeconds,
        bool requireTargetImei,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = UtcNow.AddSeconds(timeoutSeconds);
        while (UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RealDeviceSmokePortSnapshot? current = FindPinnedPortOrThrowOnSwap(state);
            if (current != null
                && IsEligible(current)
                && (!requireTargetImei
                    || ImeiManagementService.AreEquivalentImei(
                        current.Imei, state.TargetImei)))
            {
                return current;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new RealDeviceSmokeRunException(
            $"Hết hạn chờ {state.PortName} trở lại Active với đúng CCID/IMEI đã ghim.");
    }

    private void EnsurePinnedPortNow(
        RealDeviceSmokeResult state,
        bool requireTargetImei)
    {
        RealDeviceSmokePortSnapshot? current = FindPinnedPortOrThrowOnSwap(state);
        if (current == null || !IsEligible(current))
            throw new RealDeviceSmokeRunException(
                $"{state.PortName} không còn Active/VinaPhone trước thao tác {state.CurrentStep}.");
        if (requireTargetImei
            && !ImeiManagementService.AreEquivalentImei(
                current.Imei, state.TargetImei))
        {
            throw new RealDeviceSmokeRunException(
                $"{state.PortName} lệch IMEI trước thao tác {state.CurrentStep}; expected={state.TargetImei}; actual={current.Imei}.");
        }
    }

    private RealDeviceSmokePortSnapshot? FindPinnedPortOrThrowOnSwap(
        RealDeviceSmokeResult state)
    {
        RealDeviceSmokePortSnapshot? current = _host.GetPorts().FirstOrDefault(port =>
            string.Equals(port.PortName, state.PortName, StringComparison.OrdinalIgnoreCase));
        if (current == null) return null;

        string liveCcid = NormalizeDigits(current.Ccid);
        if (!string.IsNullOrWhiteSpace(liveCcid)
            && !string.Equals(
                liveCcid, state.PinnedCcid, StringComparison.Ordinal))
        {
            throw new RealDeviceSmokeRunException(
                $"Hot-swap detected on {state.PortName}; expected CCID={state.PinnedCcid}; live CCID={liveCcid}. Mọi thao tác tiếp theo đã bị chặn.");
        }
        return current;
    }

    private async Task<SmsInboxRecord> WaitForIncomingSmsAsync(
        string portName,
        DateTimeOffset sentAtUtc,
        IReadOnlySet<string> baselineDeliveryIds,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = UtcNow.AddSeconds(timeoutSeconds);
        while (UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SmsInboxRecord? record = _host.GetRecentSms(1000).FirstOrDefault(item =>
                !baselineDeliveryIds.Contains(item.DeliveryId)
                && string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Sender.Trim(), SmsRecipient, StringComparison.OrdinalIgnoreCase)
                && item.ReceivedAtUtc >= sentAtUtc.AddSeconds(-2)
                && !string.IsNullOrWhiteSpace(item.Content));
            if (record != null) return record;

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new RealDeviceSmokeRunException(
            $"Đã gửi SMS nhưng hết hạn {timeoutSeconds}s mà durable inbox chưa có phản hồi mới từ {SmsRecipient} trên {portName}.");
    }

    private static bool SameIdentity(
        RealDeviceSmokePortSnapshot left,
        RealDeviceSmokePortSnapshot right) =>
        string.Equals(left.PortName, right.PortName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Ccid, right.Ccid, StringComparison.Ordinal)
        && ImeiManagementService.AreEquivalentImei(left.Imei, right.Imei);

    internal static RealDeviceSmokePortSnapshot? SelectEligiblePort(
        IEnumerable<RealDeviceSmokePortSnapshot> ports,
        string? requestedPortName,
        string? expectedCcid)
    {
        string normalizedExpectedCcid = NormalizeDigits(expectedCcid);
        if (string.IsNullOrWhiteSpace(requestedPortName)
            || normalizedExpectedCcid.Length != 20)
            return null;

        IEnumerable<RealDeviceSmokePortSnapshot> eligible = ports.Where(port =>
            IsEligibleForInitialImeiSelection(port)
            && string.Equals(
                NormalizeDigits(port.Ccid),
                normalizedExpectedCcid,
                StringComparison.Ordinal));
        return eligible.FirstOrDefault(port => string.Equals(
            port.PortName,
            requestedPortName.Trim(),
            StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsEligibleForInitialImeiSelection(
        RealDeviceSmokePortSnapshot port) =>
        port.IsReadyForOperation
        && port.IsActive
        && !port.IsRebooting
        && string.Equals(
            port.NetworkProvider, ExpectedProvider, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(port.NetworkType)
        && NormalizeDigits(port.Ccid).StartsWith(
            ExpectedCcidPrefix, StringComparison.Ordinal)
        && ImeiManagementService.IsUsableObservedImei(
            NormalizeDigits(port.Imei));

    internal static bool IsEligible(RealDeviceSmokePortSnapshot port) =>
        IsEligibleForInitialImeiSelection(port)
        && !port.IsBalanceLoading;

    internal static bool IsSuccessfulUssdResult(string? result)
    {
        string normalized = TextEncodingNormalizer.RepairMojibake(result ?? string.Empty).Trim();
        return normalized.Length > 0
            && !normalized.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("NO RESPONSE", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("CANCEL", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSuccessfulSmsResult(string? result)
    {
        string normalized = TextEncodingNormalizer.RepairMojibake(result ?? string.Empty);
        return normalized.Contains("+CMGS:", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("gửi thành công", StringComparison.OrdinalIgnoreCase);
    }

    internal static RealDeviceSmsSubmitDisposition ClassifySmsSubmitResult(
        string? result)
    {
        string normalized = TextEncodingNormalizer.RepairMojibake(
            result ?? string.Empty);
        if (normalized.Contains(
                GsmModemService.SmsPayloadSubmittedMarker,
                StringComparison.Ordinal))
        {
            return RealDeviceSmsSubmitDisposition.PayloadSubmittedUncertain;
        }

        return IsSuccessfulSmsResult(normalized)
            ? RealDeviceSmsSubmitDisposition.Confirmed
            : RealDeviceSmsSubmitDisposition.PrePayloadFailed;
    }

    internal static bool HasActiveCallEvidence(IEnumerable<string> evidence) =>
        evidence.Any(line => Regex.IsMatch(
            line ?? string.Empty,
            @"\[CALL_STATE\].*\bACTIVE\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    internal static bool HasCompletedVoiceDurationEvidence(
        IEnumerable<string> evidence,
        int durationSeconds) => evidence.Any(line =>
            (line ?? string.Empty).Contains(
                "[CALL_DURATION_COMPLETE]", StringComparison.OrdinalIgnoreCase)
            && (line ?? string.Empty).Contains(
                $"duration={durationSeconds}", StringComparison.OrdinalIgnoreCase));

    internal static bool HasConfirmedCallHangupEvidence(
        IEnumerable<string> evidence,
        int durationSeconds) => evidence.Any(line =>
            (line ?? string.Empty).Contains(
                "[CALL_HANGUP_CONFIRMED]", StringComparison.OrdinalIgnoreCase)
            && (line ?? string.Empty).Contains(
                $"duration={durationSeconds}", StringComparison.OrdinalIgnoreCase));

    internal static RealDeviceSmokeOutcome InterruptedOutcome(
        RealDeviceSmokeStep? step,
        bool cancelled)
    {
        if (step is { Chargeable: true }
            && step.Status is RealDeviceSmokeStepStatus.Prepared
                or RealDeviceSmokeStepStatus.AwaitingResponse)
        {
            return RealDeviceSmokeOutcome.Ambiguous;
        }

        return cancelled
            ? RealDeviceSmokeOutcome.Cancelled
            : RealDeviceSmokeOutcome.Failed;
    }

    internal static bool TryGetRequestPath(
        IReadOnlyList<string>? args,
        out string requestPath,
        out string error)
    {
        requestPath = string.Empty;
        error = string.Empty;
        if (args == null || args.Count == 0) return false;

        int index = -1;
        for (int i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], RequestArgument, StringComparison.OrdinalIgnoreCase))
                continue;
            if (index >= 0)
            {
                error = $"{RequestArgument} chỉ được xuất hiện một lần.";
                return false;
            }
            index = i;
        }

        if (index < 0) return false;
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            error = $"Thiếu đường dẫn JSON sau {RequestArgument}.";
            return false;
        }
        if (args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error = $"Đường dẫn JSON sau {RequestArgument} không hợp lệ.";
            return false;
        }

        requestPath = args[index + 1];
        return true;
    }

    internal static RealDeviceSmokeClaim ClaimAndReadRequest(string requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath)
            || !Path.IsPathFullyQualified(requestPath))
        {
            throw new ArgumentException(
                "Đường dẫn request phải là đường dẫn tuyệt đối.",
                nameof(requestPath));
        }

        string originalPath = Path.GetFullPath(requestPath);
        if (!File.Exists(originalPath))
            throw new FileNotFoundException(
                "Request không tồn tại hoặc đã được claim ở lần chạy trước.",
                originalPath);

        string directory = Path.GetDirectoryName(originalPath)
            ?? throw new InvalidOperationException("Request không có thư mục cha.");
        string claimedPath = Path.Combine(
            directory,
            $".toolgsm-smoke-claimed-{Guid.NewGuid():N}.json");

        // Same-directory rename is the ownership boundary. Exactly one process
        // can win; malformed requests remain claimed so they cannot later be
        // edited into an accidental paid rerun.
        File.Move(originalPath, claimedPath, overwrite: false);

        var info = new FileInfo(claimedPath);
        if (info.Length <= 0 || info.Length > 64 * 1024)
            throw new InvalidDataException("Request JSON phải có kích thước 1..65536 bytes.");

        RealDeviceSmokeRequest request;
        try
        {
            request = JsonSerializer.Deserialize<RealDeviceSmokeRequest>(
                    File.ReadAllText(claimedPath, Encoding.UTF8),
                    JsonOptions)
                ?? throw new InvalidDataException("Request JSON rỗng.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Request JSON không hợp lệ.", ex);
        }

        ValidateRequest(request);
        string runId = string.IsNullOrWhiteSpace(request.RequestId)
            ? $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"
            : request.RequestId.Trim();
        return new RealDeviceSmokeClaim(
            originalPath, claimedPath, runId, request);
    }

    internal static void ValidateRequest(RealDeviceSmokeRequest request)
    {
        if (request.SchemaVersion != 1)
            throw new InvalidDataException("schemaVersion phải bằng 1.");
        if (!Enum.IsDefined(request.Scenario))
            throw new InvalidDataException("scenario không hợp lệ.");
        if (!request.ConfirmTelecomActions)
        {
            throw new InvalidDataException(
                "confirmTelecomActions phải là true vì full test sẽ gọi 900 và gửi SMS 888; IMEI chỉ được đọc/xác minh.");
        }
        if (!string.IsNullOrWhiteSpace(request.RequestId)
            && !Regex.IsMatch(request.RequestId, @"^[A-Za-z0-9_-]{1,80}$"))
        {
            throw new InvalidDataException(
                "requestId chỉ được gồm A-Z, a-z, 0-9, '_' hoặc '-' và dài tối đa 80 ký tự.");
        }
        if (!string.IsNullOrWhiteSpace(request.ContinuationOfRunId)
            && !Regex.IsMatch(
                request.ContinuationOfRunId.Trim(),
                @"^[A-Za-z0-9_-]{1,80}$"))
        {
            throw new InvalidDataException(
                "continuationOfRunId chỉ được gồm A-Z, a-z, 0-9, '_' hoặc '-' và dài tối đa 80 ký tự.");
        }
        if (!string.IsNullOrWhiteSpace(request.ResumeRunId)
            && !Regex.IsMatch(
                request.ResumeRunId.Trim(),
                @"^[A-Za-z0-9_-]{1,80}$"))
        {
            throw new InvalidDataException(
                "resumeRunId chỉ được gồm A-Z, a-z, 0-9, '_' hoặc '-' và dài tối đa 80 ký tự.");
        }

        if (request.Scenario == RealDeviceSmokeScenario.ImeiUssdBatch)
        {
            ValidateBulkRequest(request);
            ValidateSeconds(request.PortWaitSeconds, 30, 1800, nameof(request.PortWaitSeconds));
            ValidateSeconds(request.StablePortSeconds, 2, 30, nameof(request.StablePortSeconds));
            ValidateSeconds(request.ImeiWaitSeconds, 60, 1800, nameof(request.ImeiWaitSeconds));
            ValidateSeconds(request.AutomaticUssdWaitSeconds, 60, 1800, nameof(request.AutomaticUssdWaitSeconds));
            return;
        }

        if ((request.PortNames?.Count ?? 0) != 0
            || !string.IsNullOrWhiteSpace(request.ResumeRunId))
        {
            throw new InvalidDataException(
                "portNames/resumeRunId chỉ dùng cho scenario ImeiUssdBatch.");
        }
        if (request.Scenario == RealDeviceSmokeScenario.SmsOnly
            && !string.IsNullOrWhiteSpace(request.ContinuationOfRunId))
        {
            throw new InvalidDataException(
                "SmsOnly không dùng continuationOfRunId; scenario này chỉ gửi SMS và chờ phản hồi.");
        }
        if (string.IsNullOrWhiteSpace(request.PortName)
            || !Regex.IsMatch(request.PortName.Trim(), @"^COM[1-9][0-9]{0,4}$",
                RegexOptions.IgnoreCase))
        {
            throw new InvalidDataException(
                "portName là bắt buộc và phải có dạng COM86; không tự chọn cổng cho thao tác viễn thông thật.");
        }
        string expectedCcid = NormalizeDigits(request.ExpectedCcid);
        if (expectedCcid.Length != 20
            || !expectedCcid.StartsWith(ExpectedCcidPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "expectedCcid là bắt buộc, phải gồm đúng 20 chữ số và thuộc VinaPhone (898402...).");
        }

        ValidateSeconds(request.PortWaitSeconds, 30, 1800, nameof(request.PortWaitSeconds));
        ValidateSeconds(request.StablePortSeconds, 2, 30, nameof(request.StablePortSeconds));
        ValidateSeconds(request.ImeiWaitSeconds, 60, 1800, nameof(request.ImeiWaitSeconds));
        ValidateSeconds(request.AutomaticUssdWaitSeconds, 60, 1800, nameof(request.AutomaticUssdWaitSeconds));
        ValidateSeconds(request.PostOperationWaitSeconds, 15, 600, nameof(request.PostOperationWaitSeconds));
        ValidateSeconds(request.SmsResponseWaitSeconds, 30, 900, nameof(request.SmsResponseWaitSeconds));
    }

    private static void ValidateBulkRequest(RealDeviceSmokeRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.PortName)
            || !string.IsNullOrWhiteSpace(request.ExpectedCcid)
            || !string.IsNullOrWhiteSpace(request.ContinuationOfRunId))
        {
            throw new InvalidDataException(
                "ImeiUssdBatch dùng portNames và không được đặt portName/expectedCcid/continuationOfRunId.");
        }

        string[] ports = (request.PortNames ?? Array.Empty<string>())
            .Select(port => port?.Trim().ToUpperInvariant() ?? string.Empty)
            .ToArray();
        if (request.ExpectedPortCount != 32 || ports.Length != 32)
        {
            throw new InvalidDataException(
                "ImeiUssdBatch bắt buộc đúng 32 cổng và expectedPortCount=32.");
        }
        if (ports.Any(port => !Regex.IsMatch(port, @"^COM[1-9][0-9]{0,4}$"))
            || ports.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ports.Length)
        {
            throw new InvalidDataException(
                "portNames phải là danh sách COM hợp lệ và không trùng.");
        }
        if (request.MaxParallelPorts < 1
            || request.MaxParallelPorts > Math.Min(32, ports.Length))
        {
            throw new InvalidDataException(
                "maxParallelPorts phải trong khoảng 1..min(32, số cổng).");
        }
        if (request.WaveCooldownSeconds < 0
            || request.WaveCooldownSeconds > 120)
        {
            throw new InvalidDataException(
                "waveCooldownSeconds phải trong khoảng 0..120.");
        }
    }

    private static void ValidateSeconds(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"{name} phải trong khoảng {minimum}..{maximum} giây.");
        }
    }

    private static string NormalizeDigits(string? value) =>
        Regex.Replace(value ?? string.Empty, @"\D", string.Empty);

    private DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    private void BeginStep(
        AtomicSmokeResultStore store,
        RealDeviceSmokeResult state,
        RealDeviceSmokeLogCollector logs,
        string name,
        bool chargeable,
        string detail)
    {
        state.CurrentStep = name;
        state.Steps.Add(new RealDeviceSmokeStep
        {
            Name = name,
            Chargeable = chargeable,
            Status = RealDeviceSmokeStepStatus.Prepared,
            StartedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow,
            Detail = detail
        });
        Checkpoint(store, state, logs);
        _host.Log(
            $"[{state.PortName}] [REAL_DEVICE_SMOKE_STEP] run={state.RunId}; step={name}; status=Prepared",
            "INFO");
    }

    private void CompleteStep(
        AtomicSmokeResultStore store,
        RealDeviceSmokeResult state,
        RealDeviceSmokeLogCollector logs,
        string detail)
    {
        SetCurrentStepStatus(state, RealDeviceSmokeStepStatus.Passed, detail);
        Checkpoint(store, state, logs);
    }

    private void FailKnown(
        AtomicSmokeResultStore store,
        RealDeviceSmokeResult state,
        RealDeviceSmokeLogCollector logs,
        string detail)
    {
        SetCurrentStepStatus(state, RealDeviceSmokeStepStatus.Failed, detail);
        Checkpoint(store, state, logs);
        throw new RealDeviceSmokeRunException(detail);
    }

    private void MarkAmbiguousAndThrow(
        AtomicSmokeResultStore store,
        RealDeviceSmokeResult state,
        RealDeviceSmokeLogCollector logs,
        string detail)
    {
        SetCurrentStepStatus(state, RealDeviceSmokeStepStatus.Ambiguous, detail);
        state.Outcome = RealDeviceSmokeOutcome.Ambiguous;
        Checkpoint(store, state, logs);
        throw new RealDeviceSmokeRunException(detail);
    }

    private void FinalizeInterrupted(
        AtomicSmokeResultStore store,
        RealDeviceSmokeResult state,
        RealDeviceSmokeLogCollector logs,
        string error,
        bool cancelled)
    {
        RealDeviceSmokeStep? current = state.Steps.LastOrDefault();
        RealDeviceSmokeOutcome outcome = current?.Status == RealDeviceSmokeStepStatus.Ambiguous
            ? RealDeviceSmokeOutcome.Ambiguous
            : InterruptedOutcome(current, cancelled);
        if (current != null
            && current.Status is RealDeviceSmokeStepStatus.Prepared
                or RealDeviceSmokeStepStatus.AwaitingResponse)
        {
            current.Status = outcome == RealDeviceSmokeOutcome.Ambiguous
                ? RealDeviceSmokeStepStatus.Ambiguous
                : RealDeviceSmokeStepStatus.Failed;
            current.Detail = error;
            current.UpdatedAtUtc = UtcNow;
            current.CompletedAtUtc = UtcNow;
        }

        state.Outcome = outcome;
        state.Error = error;
        state.CompletedAtUtc = UtcNow;
        state.CurrentStep = outcome == RealDeviceSmokeOutcome.Ambiguous
            ? "ambiguous"
            : cancelled ? "cancelled" : "failed";
        try
        {
            Checkpoint(store, state, logs);
        }
        catch (Exception checkpointError)
        {
            _host.Log(
                $"[{state.PortName}] [REAL_DEVICE_SMOKE_CHECKPOINT_FAILED] {checkpointError.Message}",
                "ERROR");
        }

        string marker = outcome == RealDeviceSmokeOutcome.Ambiguous
            ? "REAL_DEVICE_SMOKE_AMBIGUOUS"
            : outcome == RealDeviceSmokeOutcome.Cancelled
                ? "REAL_DEVICE_SMOKE_CANCELLED"
                : "REAL_DEVICE_SMOKE_FAILED";
        _host.Log(
            $"[{state.PortName}] [{marker}] run={state.RunId}; step={state.CurrentStep}; error={error}; result={state.ResultPath}",
            outcome == RealDeviceSmokeOutcome.Ambiguous ? "WARN" : "ERROR");
    }

    private void SetCurrentStepStatus(
        RealDeviceSmokeResult state,
        RealDeviceSmokeStepStatus status,
        string detail)
    {
        RealDeviceSmokeStep step = state.Steps.Last();
        step.Status = status;
        step.Detail = detail;
        step.UpdatedAtUtc = UtcNow;
        if (status is RealDeviceSmokeStepStatus.Passed
            or RealDeviceSmokeStepStatus.Failed
            or RealDeviceSmokeStepStatus.Ambiguous)
        {
            step.CompletedAtUtc = UtcNow;
        }
    }

    private void Checkpoint(
        AtomicSmokeResultStore store,
        RealDeviceSmokeResult state,
        RealDeviceSmokeLogCollector logs)
    {
        state.UpdatedAtUtc = UtcNow;
        state.Evidence = logs.Snapshot(500);
        store.Save(state);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record RealDeviceSmokeRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string RequestId { get; init; } = string.Empty;
    public bool ConfirmTelecomActions { get; init; }
    public RealDeviceSmokeScenario Scenario { get; init; } = RealDeviceSmokeScenario.Full;
    public string PortName { get; init; } = string.Empty;
    public string ExpectedCcid { get; init; } = string.Empty;
    public string ContinuationOfRunId { get; init; } = string.Empty;
    public IReadOnlyList<string> PortNames { get; init; } = Array.Empty<string>();
    public int ExpectedPortCount { get; init; } = 32;
    public int MaxParallelPorts { get; init; } = 8;
    public int WaveCooldownSeconds { get; init; } = 10;
    public string ResumeRunId { get; init; } = string.Empty;
    public int PortWaitSeconds { get; init; } = 600;
    public int StablePortSeconds { get; init; } = 5;
    public int ImeiWaitSeconds { get; init; } = 600;
    public int AutomaticUssdWaitSeconds { get; init; } = 900;
    public int PostOperationWaitSeconds { get; init; } = 60;
    public int SmsResponseWaitSeconds { get; init; } = 300;
}

public sealed class RealDeviceSmokeResult
{
    public int SchemaVersion { get; init; } = 1;
    public string RunId { get; set; } = string.Empty;
    public RealDeviceSmokeScenario Scenario { get; set; } = RealDeviceSmokeScenario.Full;
    public string ResumeRunId { get; set; } = string.Empty;
    public RealDeviceSmokeOutcome Outcome { get; set; }
    public string CurrentStep { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string OriginalRequestPath { get; set; } = string.Empty;
    public string ClaimedRequestPath { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public string RequestedPortName { get; set; } = string.Empty;
    public string RequestedCcid { get; set; } = string.Empty;
    public string ContinuationOfRunId { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public string PinnedCcid { get; set; } = string.Empty;
    public string InitialImei { get; set; } = string.Empty;
    public string TargetImei { get; set; } = string.Empty;
    public string NetworkProvider { get; set; } = string.Empty;
    public string ManualUssdResult { get; set; } = string.Empty;
    public double CallElapsedSeconds { get; set; }
    public IReadOnlyList<string> CallEvidence { get; set; } = Array.Empty<string>();
    public string SmsSendResult { get; set; } = string.Empty;
    public SmsInboxRecord? IncomingSms { get; set; }
    public string Error { get; set; } = string.Empty;
    public RealDeviceSmokeActions FixedActions { get; set; } = new();
    public List<RealDeviceSmokeStep> Steps { get; set; } = new();
    public IReadOnlyList<RealDeviceSmokeEvidence> Evidence { get; set; } =
        Array.Empty<RealDeviceSmokeEvidence>();
    public List<RealDeviceSmokeBulkPortResult> BulkPorts { get; set; } = new();
}

public sealed class RealDeviceSmokeBulkPortResult
{
    public string PortName { get; set; } = string.Empty;
    public int PhysicalIndex { get; set; }
    public string PinnedCcid { get; set; } = string.Empty;
    public string InitialImei { get; set; } = string.Empty;
    public string TargetImei { get; set; } = string.Empty;
    public RealDeviceSmokeOutcome Outcome { get; set; } = RealDeviceSmokeOutcome.Running;
    public string CurrentStep { get; set; } = "current-imei-prepared";
    public int ImeiAttempts { get; set; }
    public int UssdRefreshAttempts { get; set; }
    public bool ImeiVerified { get; set; }
    // Kept only so schema-v1 checkpoints written before nofake can still be read.
    // New runs never set this flag and never use a backup as acceptance evidence.
    public bool ImeiBackupCommitted { get; set; }
    public bool NetworkReady { get; set; }
    public bool Ussd111Passed { get; set; }
    public bool Ussd101DirectPassed { get; set; }
    public bool UssdInitialComplete { get; set; }
    public string Ussd101Evidence { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string Error { get; set; } = string.Empty;
}

public sealed class RealDeviceSmokeActions
{
    public string Provider { get; set; } = string.Empty;
    public string ExpectedCcid { get; set; } = string.Empty;
    public string ContinuationOfRunId { get; set; } = string.Empty;
    public IReadOnlyList<string> AutomaticUssd { get; set; } = Array.Empty<string>();
    public string ManualUssd { get; set; } = string.Empty;
    public string CallRecipient { get; set; } = string.Empty;
    public int CallDurationSeconds { get; set; }
    public string SmsRecipient { get; set; } = string.Empty;
    public string SmsBody { get; set; } = string.Empty;
}

public sealed class RealDeviceSmokeStep
{
    public string Name { get; set; } = string.Empty;
    public bool Chargeable { get; set; }
    public RealDeviceSmokeStepStatus Status { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed record RealDeviceSmokeEvidence(
    DateTimeOffset AtUtc,
    string Level,
    string Message,
    long Sequence = 0);

public enum RealDeviceSmokeOutcome
{
    Running,
    Passed,
    Failed,
    Ambiguous,
    Cancelled
}

public enum RealDeviceSmokeScenario
{
    Full,
    ImeiUssdBatch,
    /// <summary>
    /// Chỉ kiểm tra đường nhận SMS trên một cổng đã Active: gửi 'data' tới 888
    /// rồi chờ phản hồi vào durable inbox. Không đổi IMEI, không gọi, không USSD.
    /// </summary>
    SmsOnly
}

public enum RealDeviceSmokeStepStatus
{
    Prepared,
    AwaitingResponse,
    Passed,
    Failed,
    Ambiguous
}

public enum RealDeviceSmsSubmitDisposition
{
    PrePayloadFailed,
    PayloadSubmittedUncertain,
    Confirmed
}

internal sealed record RealDeviceSmokeClaim(
    string OriginalPath,
    string ClaimedPath,
    string RunId,
    RealDeviceSmokeRequest Request);

internal sealed record RealDeviceSmokePortSnapshot(
    string PortName,
    int PhysicalIndex,
    int PortNumber,
    bool IsActive,
    bool IsReadyForOperation,
    bool IsRebooting,
    bool IsBalanceLoading,
    string NetworkProvider,
    string NetworkType,
    string Ccid,
    string Imei);

internal interface IRealDeviceSmokeHost
{
    event Action<LogMessage>? LogAdded;

    IReadOnlyList<RealDeviceSmokePortSnapshot> GetPorts();
    bool TryGetCurrentSessionEpoch(
        string portName,
        string expectedCcid,
        out long epoch);
    Task<bool> VerifyPhysicalCcidAsync(
        string portName,
        string expectedCcid,
        CancellationToken cancellationToken);
    Task RefreshPortAsync(
        string portName,
        CancellationToken cancellationToken);
    Task<string> SendUssdForPortAsync(
        string portName,
        string ussdCode);
    Task<bool> ExecuteCallAsync(
        string portName,
        string recipient,
        int durationSeconds,
        string expectedCcid,
        Action<string> onStatus,
        CancellationToken cancellationToken);
    Task<string> QueueSmsAsync(
        string portName,
        string recipient,
        string content,
        string expectedCcid,
        CancellationToken cancellationToken);
    IReadOnlyList<SmsInboxRecord> GetRecentSms(int count);
    void Log(string message, string level);
}

internal sealed class MainViewModelSmokeHost : IRealDeviceSmokeHost
{
    private readonly MainViewModel _viewModel;
    private readonly SmsInboxStore _inboxStore = new();

    public MainViewModelSmokeHost(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public event Action<LogMessage>? LogAdded
    {
        add => _viewModel.LogAdded += value;
        remove => _viewModel.LogAdded -= value;
    }

    public IReadOnlyList<RealDeviceSmokePortSnapshot> GetPorts() =>
        _viewModel.GetPortsSnapshot().Select(port => new RealDeviceSmokePortSnapshot(
            port.PortName,
            port.PhysicalIndex,
            port.PortNumber,
            string.Equals(port.Status, SimStatus.Active, StringComparison.Ordinal),
            _viewModel.IsPortReadyForOperation(port.PortName),
            port.IsRebooting,
            port.IsBalanceLoading,
            port.NetworkProvider,
            port.NetworkType,
            Regex.Replace(port.Serial ?? string.Empty, @"\D", string.Empty),
            Regex.Replace(port.Imei ?? string.Empty, @"\D", string.Empty)))
        .ToArray();

    public bool TryGetCurrentSessionEpoch(
        string portName,
        string expectedCcid,
        out long epoch) =>
        _viewModel.TryGetCurrentSimSessionIdentity(
            portName, expectedCcid, out epoch);

    public Task<bool> VerifyPhysicalCcidAsync(
        string portName,
        string expectedCcid,
        CancellationToken cancellationToken) =>
        _viewModel.VerifyPhysicalCcidAsync(
            portName, expectedCcid, cancellationToken);

    public Task RefreshPortAsync(
        string portName,
        CancellationToken cancellationToken) =>
        _viewModel.RefreshPortAsync(portName, cancellationToken);

    public Task<string> SendUssdForPortAsync(
        string portName,
        string ussdCode) =>
        _viewModel.SendUssdForPortAsync(portName, ussdCode);

    public Task<bool> ExecuteCallAsync(
        string portName,
        string recipient,
        int durationSeconds,
        string expectedCcid,
        Action<string> onStatus,
        CancellationToken cancellationToken) =>
        _viewModel.ExecuteCallFromUiAsync(
            portName,
            recipient,
            string.Empty,
            durationSeconds,
            record: false,
            onStatusUpdate: onStatus,
            ct: cancellationToken,
            expectedCcid: expectedCcid);

    public Task<string> QueueSmsAsync(
        string portName,
        string recipient,
        string content,
        string expectedCcid,
        CancellationToken cancellationToken) =>
        _viewModel.QueueSmsAsync(
            portName, recipient, content, cancellationToken, expectedCcid);

    public IReadOnlyList<SmsInboxRecord> GetRecentSms(int count) =>
        _inboxStore.GetRecent(count);

    public void Log(string message, string level) =>
        _viewModel.AddLog(message, level);
}

internal sealed class RealDeviceSmokeLogCollector : IDisposable
{
    private readonly IRealDeviceSmokeHost _host;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentQueue<RealDeviceSmokeEvidence> _entries = new();
    private readonly ConcurrentQueue<RealDeviceSmokeEvidence> _acceptanceMarkers = new();
    private long _sequence;

    public long CurrentSequence => Interlocked.Read(ref _sequence);

    public RealDeviceSmokeLogCollector(
        IRealDeviceSmokeHost host,
        TimeProvider timeProvider)
    {
        _host = host;
        _timeProvider = timeProvider;
        _host.LogAdded += OnLogAdded;
    }

    public IReadOnlyList<RealDeviceSmokeEvidence> Snapshot(int maximum) =>
        _entries.TakeLast(Math.Max(0, maximum)).ToArray();

    public async Task<RealDeviceSmokeEvidence> WaitForAsync(
        string portName,
        string marker,
        DateTimeOffset notBeforeUtc,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        long afterSequence = 0,
        string? requiredText = null)
    {
        DateTimeOffset deadline = _timeProvider.GetUtcNow().Add(timeout);
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RealDeviceSmokeEvidence? match = AcceptanceEvidence().FirstOrDefault(item =>
                item.AtUtc >= notBeforeUtc
                && item.Sequence > afterSequence
                && string.Equals(
                    item.Level, "SUCCESS", StringComparison.OrdinalIgnoreCase)
                && HasStructuredMarker(item.Message, portName, marker)
                && (string.IsNullOrWhiteSpace(requiredText)
                    || item.Message.Contains(
                        requiredText, StringComparison.OrdinalIgnoreCase)));
            if (match != null) return match;

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new RealDeviceSmokeRunException(
            $"Hết hạn chờ marker {marker} trên {portName}.");
    }

    public async Task<RealDeviceSmokeEvidence> WaitForAnyAsync(
        string portName,
        IReadOnlyList<string> markers,
        DateTimeOffset notBeforeUtc,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        long afterSequence = 0,
        string? requiredText = null)
    {
        if (markers.Count == 0)
            throw new ArgumentException("Phải có ít nhất một marker.", nameof(markers));
        DateTimeOffset deadline = _timeProvider.GetUtcNow().Add(timeout);
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RealDeviceSmokeEvidence? match = AcceptanceEvidence().FirstOrDefault(item =>
                item.AtUtc >= notBeforeUtc
                && item.Sequence > afterSequence
                && string.Equals(
                    item.Level, "SUCCESS", StringComparison.OrdinalIgnoreCase)
                && markers.Any(marker => HasStructuredMarker(
                    item.Message, portName, marker))
                && (string.IsNullOrWhiteSpace(requiredText)
                    || item.Message.Contains(
                        requiredText, StringComparison.OrdinalIgnoreCase)));
            if (match != null) return match;

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new RealDeviceSmokeRunException(
            $"Hết hạn chờ một trong các marker {string.Join(", ", markers)} trên {portName}.");
    }

    private IEnumerable<RealDeviceSmokeEvidence> AcceptanceEvidence() =>
        _acceptanceMarkers;

    private static bool HasStructuredMarker(
        string message,
        string portName,
        string marker) => message.StartsWith(
            $"[{portName}] {marker}",
            StringComparison.OrdinalIgnoreCase);

    private void OnLogAdded(LogMessage entry)
    {
        var evidence = new RealDeviceSmokeEvidence(
            _timeProvider.GetUtcNow(),
            entry.Level ?? "INFO",
            entry.Message ?? string.Empty,
            Interlocked.Increment(ref _sequence));
        _entries.Enqueue(evidence);
        if (IsAcceptanceMarker(evidence.Message))
            _acceptanceMarkers.Enqueue(evidence);
        while (_acceptanceMarkers.Count > 8192)
            _acceptanceMarkers.TryDequeue(out _);
        while (_entries.Count > 2000)
            _entries.TryDequeue(out _);
    }

    private static bool IsAcceptanceMarker(string message) =>
        message.Contains("[SAUTO_NETWORK_READY]", StringComparison.OrdinalIgnoreCase)
        || message.Contains(
            "[SAUTO_AUTO_USSD_RESULT]",
            StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _host.LogAdded -= OnLogAdded;
}

internal sealed class AtomicSmokeResultStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    private AtomicSmokeResultStore(string runDirectory)
    {
        RunDirectory = runDirectory;
        ResultPath = Path.Combine(runDirectory, "result.json");
    }

    public string RunDirectory { get; }
    public string ResultPath { get; }

    public static AtomicSmokeResultStore CreateNew(string resultsRoot, string runId)
    {
        if (!Regex.IsMatch(runId, @"^[A-Za-z0-9_-]{1,80}$"))
            throw new ArgumentException("Invalid smoke-test run id.", nameof(runId));

        string root = Path.GetFullPath(resultsRoot);
        Directory.CreateDirectory(root);
        string runDirectory = Path.Combine(root, runId);
        if (Directory.Exists(runDirectory))
        {
            throw new IOException(
                $"Run directory already exists; refusing to replay: {runDirectory}");
        }
        Directory.CreateDirectory(runDirectory);
        return new AtomicSmokeResultStore(runDirectory);
    }

    public void Save(RealDeviceSmokeResult result)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(result, Options);
        lock (_gate)
        {
            string tempPath = Path.Combine(
                RunDirectory,
                $".result-{Guid.NewGuid():N}.tmp");
            try
            {
                var streamOptions = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    BufferSize = 4096,
                    Options = FileOptions.WriteThrough
                };
                using (var stream = new FileStream(tempPath, streamOptions))
                {
                    stream.Write(json);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, ResultPath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // The committed result is authoritative. A stale uniquely
                    // named temp file is harmless and never read as a checkpoint.
                }
            }
        }
    }
}

internal sealed class RealDeviceSmokeRunException : Exception
{
    public RealDeviceSmokeRunException(string message) : base(message)
    {
    }
}
