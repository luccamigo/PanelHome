/*++

Module Name:

    TestMain.cpp

Abstract:

    Unit tests for the parts of PanelDisplayDriver that do not need WDF, IddCx or
    an installed driver:

        - command line parsing            (tools/PanelDisplayCtl/CommandLine.cpp)
        - mode validation and mode table  (common/PanelDisplayModes.cpp)
        - connect/disconnect state machine(common/PanelMonitorState.cpp)

    The driver itself is verified by the manual procedure in README.md.

--*/

#include <windows.h>
#include <winioctl.h>

#include "TestFramework.h"

#include "../common/PanelDisplayModes.h"
#include "../common/PanelMonitorState.h"
#include "../common/LatestFrameStore.h"
#include "../common/PanelFrameDiagnostics.h"
#include "../common/CaptureScheduler.h"
#include "../tools/PanelDisplayCtl/CommandLine.h"

using namespace PanelOS;

namespace
{
    constexpr ULONGLONG SchedulerFrequency = 60'000'000u;

    int SimulateCaptureRate(ULONG InputRateNumerator,
                            ULONG InputRateDenominator,
                            ULONG DurationSeconds,
                            ULONG TargetFramesPerSecond = 30u,
                            _In_opt_ const LONG* JitterMilliseconds = nullptr,
                            size_t JitterCount = 0)
    {
        CaptureScheduler scheduler(SchedulerFrequency, TargetFramesPerSecond);
        const ULONGLONG duration = SchedulerFrequency * DurationSeconds;
        int captured = 0;
        ULONGLONG index = 0;
        ULONGLONG previous = 0;

        for (;; ++index)
        {
            const ULONGLONG base =
                index * SchedulerFrequency * InputRateDenominator /
                InputRateNumerator;
            if (base >= duration)
            {
                break;
            }

            LONGLONG timestamp = static_cast<LONGLONG>(base);
            if (JitterMilliseconds != nullptr && JitterCount != 0)
            {
                timestamp += static_cast<LONGLONG>(
                    JitterMilliseconds[index % JitterCount]) *
                    static_cast<LONGLONG>(SchedulerFrequency / 1000u);
            }

            ULONGLONG monotonic = timestamp <= 0
                ? 0
                : static_cast<ULONGLONG>(timestamp);
            if (index != 0 && monotonic <= previous)
            {
                monotonic = previous + 1u;
            }
            previous = monotonic;

            if (scheduler.ShouldCapture(monotonic))
            {
                ++captured;
            }
        }

        return captured;
    }

    struct PacingSimulation
    {
        int Published;
        ULONGLONG Scheduled;
        ULONGLONG Missed;
    };

    PacingSimulation SimulatePendingStagingPacing(
        ULONG ProducerFramesPerSecond,
        ULONG DurationSeconds,
        ULONG TargetFramesPerSecond,
        _In_opt_ const LONG* JitterMilliseconds = nullptr,
        size_t JitterCount = 0)
    {
        CaptureScheduler scheduler(SchedulerFrequency, TargetFramesPerSecond);
        const ULONGLONG duration = SchedulerFrequency * DurationSeconds;
        PacingSimulation result = {};
        bool hasPendingStaging = false;
        ULONGLONG previous = 0;

        for (ULONGLONG index = 0;; ++index)
        {
            const ULONGLONG base =
                index * SchedulerFrequency / ProducerFramesPerSecond;
            if (base >= duration)
            {
                break;
            }

            LONGLONG timestamp = static_cast<LONGLONG>(base);
            if (JitterMilliseconds != nullptr && JitterCount != 0)
            {
                timestamp += static_cast<LONGLONG>(
                    JitterMilliseconds[index % JitterCount]) *
                    static_cast<LONGLONG>(SchedulerFrequency / 1000u);
            }
            ULONGLONG monotonic = timestamp <= 0
                ? 0
                : static_cast<ULONGLONG>(timestamp);
            if (index != 0 && monotonic <= previous)
            {
                monotonic = previous + 1u;
            }
            previous = monotonic;

            const CaptureScheduleDecision decision =
                scheduler.Evaluate(monotonic);
            if (decision.ShouldCapture && hasPendingStaging)
            {
                ++result.Published;
                result.Scheduled += decision.ElapsedDeadlines;
                result.Missed += decision.ElapsedDeadlines - 1u;
            }

            // Every arrival refreshes the single pending staging resource;
            // there is never a queue and never a synthetic duplicate.
            hasPendingStaging = true;
        }

        return result;
    }

    void CheckCaptureRate(const char* Name,
                          ULONG InputRateNumerator,
                          ULONG InputRateDenominator,
                          int Expected,
                          ULONG TargetFramesPerSecond = 30u)
    {
        PANEL_TEST_CASE(Name);
        const int captured = SimulateCaptureRate(
            InputRateNumerator,
            InputRateDenominator,
            10u,
            TargetFramesPerSecond);
        PANEL_CHECK(captured >= Expected - 1);
        PANEL_CHECK(captured <= Expected + 1);
    }

    void TestCaptureSchedulerNominalRates()
    {
        CheckCaptureRate("Capture scheduler samples 60 Hz at 30 FPS", 60u, 1u, 300);
        CheckCaptureRate(
            "Capture scheduler samples 59.94 Hz at 30 FPS",
            60'000u,
            1001u,
            300);
        CheckCaptureRate("Capture scheduler samples 53 Hz at 30 FPS", 53u, 1u, 300);
        CheckCaptureRate("Capture scheduler samples 50 Hz at 30 FPS", 50u, 1u, 300);
        CheckCaptureRate("Capture scheduler preserves 30 Hz input", 30u, 1u, 300);
        CheckCaptureRate("Capture scheduler preserves 25 Hz input", 25u, 1u, 250);
        CheckCaptureRate("Capture scheduler preserves 20 Hz input", 20u, 1u, 200);
        CheckCaptureRate("Capture scheduler samples 60 Hz at 45 FPS",
                         60u, 1u, 450, 45u);
        CheckCaptureRate("Capture scheduler preserves 60 FPS",
                         60u, 1u, 600, 60u);
    }

    void TestCaptureSchedulerJitter()
    {
        PANEL_TEST_CASE("Capture scheduler tolerates bounded arrival jitter");
        constexpr LONG jitter[] = { -3, 2, -1, 4, 0, -2, 3, 1 };
        const int captured = SimulateCaptureRate(
            60u,
            1u,
            10u,
            30u,
            jitter,
            ARRAYSIZE(jitter));
        PANEL_CHECK(captured >= 295);
        PANEL_CHECK(captured <= 305);
    }

    void TestCaptureSchedulerProducerTargetMatrix()
    {
        constexpr ULONG producers[] = { 30u, 45u, 55u, 60u, 75u, 120u };
        constexpr ULONG targets[] = { 30u, 45u, 60u };
        for (const ULONG producer : producers)
        {
            for (const ULONG target : targets)
            {
                char name[128] = {};
                _snprintf_s(
                    name,
                    sizeof(name),
                    _TRUNCATE,
                    "Pending staging producer %lu target %lu over 10 seconds",
                    producer,
                    target);
                PANEL_TEST_CASE(name);
                const PacingSimulation result =
                    SimulatePendingStagingPacing(producer, 10u, target);
                const int expected = static_cast<int>(
                    (producer < target ? producer : target) * 10u);
                PANEL_CHECK(result.Published >= expected - 2);
                PANEL_CHECK(result.Published <= expected + 1);
                PANEL_CHECK_EQ(
                    result.Scheduled,
                    static_cast<ULONGLONG>(result.Published) + result.Missed);
            }
        }
    }

    void TestCaptureSchedulerPhysicalJitterMatrix()
    {
        PANEL_TEST_CASE("Pending staging sustains physical 55 Hz jitter");
        constexpr LONG jitter[] = { -1, 0, 1, 0, -1, 1, 0, 1, 0, -1 };
        for (const ULONG target : { 30u, 45u, 60u })
        {
            const PacingSimulation result = SimulatePendingStagingPacing(
                55u,
                10u,
                target,
                jitter,
                ARRAYSIZE(jitter));
            const int expected = static_cast<int>(
                (55u < target ? 55u : target) * 10u);
            printf("  target=%lu published=%d scheduled=%llu missed=%llu\n",
                   target,
                   result.Published,
                   static_cast<unsigned long long>(result.Scheduled),
                   static_cast<unsigned long long>(result.Missed));
            PANEL_CHECK(result.Published >= expected - 4);
            PANEL_CHECK(result.Published <= expected + 1);
        }
    }

    void TestCaptureSchedulerLongPauseAndResume()
    {
        PANEL_TEST_CASE("Capture scheduler resumes latest-only after a long pause");
        CaptureScheduler scheduler(SchedulerFrequency, 30u);
        int beforePause = 0;
        for (ULONGLONG index = 0; index < 60u; ++index)
        {
            if (scheduler.ShouldCapture(index * SchedulerFrequency / 60u))
            {
                ++beforePause;
            }
        }

        const ULONGLONG resumedAt = SchedulerFrequency * 10u;
        PANEL_CHECK(scheduler.ShouldCapture(resumedAt));
        int afterPause = 1;
        for (ULONGLONG index = 1; index < 60u; ++index)
        {
            if (scheduler.ShouldCapture(
                    resumedAt + index * SchedulerFrequency / 60u))
            {
                ++afterPause;
            }
        }

        PANEL_CHECK(beforePause >= 29 && beforePause <= 31);
        PANEL_CHECK(afterPause >= 29 && afterPause <= 31);
    }

    void TestCaptureSchedulerReportsDeadlineLatenessAndMisses()
    {
        PANEL_TEST_CASE("Capture scheduler reports lateness and elapsed deadlines");
        CaptureScheduler scheduler(SchedulerFrequency, 30u);
        PANEL_CHECK(scheduler.Evaluate(0).ShouldCapture);

        const ULONGLONG period = SchedulerFrequency / 30u;
        const CaptureScheduleDecision late = scheduler.Evaluate(period + 321u);
        PANEL_CHECK(late.ShouldCapture);
        PANEL_CHECK_EQ(late.ScheduledDeadline, period);
        PANEL_CHECK_EQ(late.DeadlineLateness, 321u);
        PANEL_CHECK_EQ(late.ElapsedDeadlines, 1u);

        const CaptureScheduleDecision resumed =
            scheduler.Evaluate(SchedulerFrequency);
        PANEL_CHECK(resumed.ShouldCapture);
        PANEL_CHECK(resumed.ElapsedDeadlines > 1u);
        PANEL_CHECK(resumed.ScheduledDeadline <= SchedulerFrequency);
    }

    void TestCaptureSchedulerHasNoLongRunDrift()
    {
        PANEL_TEST_CASE("Capture scheduler accumulated deadlines do not drift");
        for (const ULONG target : { 30u, 45u, 60u })
        {
            const int captured = SimulateCaptureRate(
                120u,
                1u,
                600u,
                target);
            const int expected = static_cast<int>(target * 600u);
            PANEL_CHECK(captured >= expected - 1);
            PANEL_CHECK(captured <= expected + 1);
        }
    }

    void TestCaptureSchedulerRuntimeTargetChanges()
    {
        PANEL_TEST_CASE("Capture scheduler resets safely for 30 to 45 to 60 to 30");
        CaptureScheduler scheduler(SchedulerFrequency, 30u);
        ULONGLONG timestamp = 1'000u;
        for (const ULONG target : { 30u, 45u, 60u, 30u })
        {
            scheduler.Reset(SchedulerFrequency, target);
            const CaptureScheduleDecision first = scheduler.Evaluate(timestamp);
            PANEL_CHECK(first.ShouldCapture);
            PANEL_CHECK_EQ(first.ScheduledDeadline, timestamp);
            PANEL_CHECK_EQ(first.ElapsedDeadlines, 1u);
            PANEL_CHECK(!scheduler.Evaluate(timestamp + 1u).ShouldCapture);
            timestamp += SchedulerFrequency;
        }
    }

    // -----------------------------------------------------------------------
    // Command line parsing
    // -----------------------------------------------------------------------

    void TestParseModeSpec()
    {
        PANEL_TEST_CASE("ParseModeSpec accepts and rejects the right shapes");

        PANEL_DISPLAY_MODE mode = {};

        PANEL_CHECK(ParseModeSpec(L"1920x1200@60", mode));
        PANEL_CHECK_EQ(mode.Width, 1920u);
        PANEL_CHECK_EQ(mode.Height, 1200u);
        PANEL_CHECK_EQ(mode.RefreshHz, 60u);

        //
        // Capital X is accepted because people type it.
        //
        PANEL_CHECK(ParseModeSpec(L"1280X800@60", mode));
        PANEL_CHECK_EQ(mode.Width, 1280u);

        PANEL_CHECK(!ParseModeSpec(L"1920x1200", mode));       // no refresh
        PANEL_CHECK(!ParseModeSpec(L"1920@60", mode));         // no height
        PANEL_CHECK(!ParseModeSpec(L"x1200@60", mode));        // no width
        PANEL_CHECK(!ParseModeSpec(L"1920x1200@60junk", mode));// trailing junk
        PANEL_CHECK(!ParseModeSpec(L"", mode));
        PANEL_CHECK(!ParseModeSpec(L"abc", mode));
        PANEL_CHECK(!ParseModeSpec(nullptr, mode));

        //
        // Overflow must be rejected rather than wrapping into a plausible value.
        //
        PANEL_CHECK(!ParseModeSpec(L"999999999999x1200@60", mode));
    }

    void TestParseCommandLineVerbs()
    {
        PANEL_TEST_CASE("ParseCommandLine recognises the three verbs");

        const wchar_t* statusArgs[] = { L"PanelDisplayCtl.exe", L"status" };
        ParsedCommandLine parsed = ParseCommandLine(2, statusArgs);
        PANEL_CHECK(parsed.Command == CtlCommand::Status);
        PANEL_CHECK(parsed.Error == nullptr);
        PANEL_CHECK(!parsed.Verbose);

        const wchar_t* connectArgs[] = { L"PanelDisplayCtl.exe", L"connect" };
        parsed = ParseCommandLine(2, connectArgs);
        PANEL_CHECK(parsed.Command == CtlCommand::Connect);
        PANEL_CHECK(!parsed.HasPreferredMode);

        const wchar_t* disconnectArgs[] = { L"PanelDisplayCtl.exe", L"disconnect" };
        parsed = ParseCommandLine(2, disconnectArgs);
        PANEL_CHECK(parsed.Command == CtlCommand::Disconnect);

        //
        // Verbs are case insensitive.
        //
        const wchar_t* upperArgs[] = { L"PanelDisplayCtl.exe", L"CONNECT" };
        parsed = ParseCommandLine(2, upperArgs);
        PANEL_CHECK(parsed.Command == CtlCommand::Connect);
    }

    void TestParseCommandLineHelpAndErrors()
    {
        PANEL_TEST_CASE("ParseCommandLine handles help and bad input");

        const wchar_t* noArgs[] = { L"PanelDisplayCtl.exe" };
        ParsedCommandLine parsed = ParseCommandLine(1, noArgs);
        PANEL_CHECK(parsed.Command == CtlCommand::Help);
        PANEL_CHECK(parsed.Error == nullptr);

        const wchar_t* helpArgs[] = { L"PanelDisplayCtl.exe", L"--help" };
        parsed = ParseCommandLine(2, helpArgs);
        PANEL_CHECK(parsed.Command == CtlCommand::Help);

        const wchar_t* badVerb[] = { L"PanelDisplayCtl.exe", L"frobnicate" };
        parsed = ParseCommandLine(2, badVerb);
        PANEL_CHECK(parsed.Error != nullptr);

        const wchar_t* badOption[] = { L"PanelDisplayCtl.exe", L"status", L"--nope" };
        parsed = ParseCommandLine(3, badOption);
        PANEL_CHECK(parsed.Error != nullptr);
    }

    void TestParseCommandLineOptions()
    {
        PANEL_TEST_CASE("ParseCommandLine binds options to the right verb");

        const wchar_t* connectMode[] = { L"PanelDisplayCtl.exe", L"connect", L"--mode", L"1920x1080@60" };
        ParsedCommandLine parsed = ParseCommandLine(4, connectMode);
        PANEL_CHECK(parsed.Error == nullptr);
        PANEL_CHECK(parsed.HasPreferredMode);
        PANEL_CHECK_EQ(parsed.PreferredMode.Width, 1920u);
        PANEL_CHECK_EQ(parsed.PreferredMode.Height, 1080u);

        //
        // --mode without a value must not read past the end of argv.
        //
        const wchar_t* missingValue[] = { L"PanelDisplayCtl.exe", L"connect", L"--mode" };
        parsed = ParseCommandLine(3, missingValue);
        PANEL_CHECK(parsed.Error != nullptr);

        const wchar_t* badMode[] = { L"PanelDisplayCtl.exe", L"connect", L"--mode", L"wide" };
        parsed = ParseCommandLine(4, badMode);
        PANEL_CHECK(parsed.Error != nullptr);

        //
        // Options belong to one verb each.
        //
        const wchar_t* modeOnStatus[] = { L"PanelDisplayCtl.exe", L"status", L"--mode", L"1920x1080@60" };
        parsed = ParseCommandLine(4, modeOnStatus);
        PANEL_CHECK(parsed.Error != nullptr);

        const wchar_t* verboseOnConnect[] = { L"PanelDisplayCtl.exe", L"connect", L"--verbose" };
        parsed = ParseCommandLine(3, verboseOnConnect);
        PANEL_CHECK(parsed.Error != nullptr);

        const wchar_t* verboseStatus[] = { L"PanelDisplayCtl.exe", L"status", L"--verbose" };
        parsed = ParseCommandLine(3, verboseStatus);
        PANEL_CHECK(parsed.Error == nullptr);
        PANEL_CHECK(parsed.Verbose);
    }

    // -----------------------------------------------------------------------
    // Mode validation
    // -----------------------------------------------------------------------

    void TestModeValidation()
    {
        PANEL_TEST_CASE("IsModeValid enforces the documented limits");

        PANEL_CHECK(IsModeValid(PANEL_DISPLAY_MODE{ 1920, 1200, 60 }));
        PANEL_CHECK(IsModeValid(PANEL_DISPLAY_MODE{ 1280,  800, 60 }));

        //
        // All three built-in modes must pass their own validation, otherwise the
        // driver would advertise modes it would refuse from the host.
        //
        const PANEL_DISPLAY_MODE* defaults = DefaultModes();
        for (ULONG i = 0; i < DefaultModeCount; ++i)
        {
            PANEL_CHECK(IsModeValid(defaults[i]));
        }

        PANEL_CHECK(!IsModeValid(PANEL_DISPLAY_MODE{ 0, 0, 0 }));
        PANEL_CHECK(!IsModeValid(PANEL_DISPLAY_MODE{ 320, 240, 60 }));      // too small
        PANEL_CHECK(!IsModeValid(PANEL_DISPLAY_MODE{ 7680, 4320, 60 }));    // too large
        PANEL_CHECK(!IsModeValid(PANEL_DISPLAY_MODE{ 1920, 1200, 10 }));    // refresh too low
        PANEL_CHECK(!IsModeValid(PANEL_DISPLAY_MODE{ 1920, 1200, 480 }));   // refresh too high
        PANEL_CHECK(!IsModeValid(PANEL_DISPLAY_MODE{ 1921, 1200, 60 }));    // odd width
        PANEL_CHECK(!IsModeValid(PANEL_DISPLAY_MODE{ 1920, 1201, 60 }));    // odd height

        //
        // Boundaries themselves are inclusive.
        //
        PANEL_CHECK(IsModeValid(PANEL_DISPLAY_MODE{ ModeLimits::MinWidth, ModeLimits::MinHeight, ModeLimits::MinRefreshHz }));
        PANEL_CHECK(IsModeValid(PANEL_DISPLAY_MODE{ ModeLimits::MaxWidth, ModeLimits::MaxHeight, ModeLimits::MaxRefreshHz }));
    }

    void TestModeTableDefaults()
    {
        PANEL_TEST_CASE("ModeTable starts with the documented defaults");

        ModeTable table;

        PANEL_CHECK_EQ(table.Count(), DefaultModeCount);
        PANEL_CHECK_EQ(table.PreferredIndex(), DefaultPreferredModeIndex());

        //
        // The preferred default must be 1280x800@60, which is what the EDID's
        // preferred detailed timing describes.
        //
        PANEL_CHECK(AreModesEqual(table.Preferred(), PANEL_DISPLAY_MODE{ 1280, 800, 60 }));

        PANEL_CHECK(table.Contains(PANEL_DISPLAY_MODE{ 1280, 800, 60 }));
        PANEL_CHECK(table.Contains(PANEL_DISPLAY_MODE{ 1920, 1080, 60 }));
        PANEL_CHECK(!table.Contains(PANEL_DISPLAY_MODE{ 1024, 768, 60 }));

        PANEL_CHECK(table.ContainsResolution(1920, 1080));
        PANEL_CHECK(!table.ContainsResolution(1024, 768));
    }

    void TestModeTableSetPreferred()
    {
        PANEL_TEST_CASE("ModeTable promotes or appends a host supplied mode");

        ModeTable table;

        //
        // A mode already in the list is promoted, not duplicated.
        //
        PANEL_CHECK(table.SetPreferredMode(PANEL_DISPLAY_MODE{ 1280, 800, 60 }));
        PANEL_CHECK_EQ(table.Count(), DefaultModeCount);
        PANEL_CHECK(AreModesEqual(table.Preferred(), PANEL_DISPLAY_MODE{ 1280, 800, 60 }));

        //
        // A new valid mode is appended and becomes preferred; the defaults stay
        // available as fallbacks.
        //
        PANEL_CHECK(table.SetPreferredMode(PANEL_DISPLAY_MODE{ 2560, 1600, 60 }));
        PANEL_CHECK_EQ(table.Count(), DefaultModeCount + 1);
        PANEL_CHECK(AreModesEqual(table.Preferred(), PANEL_DISPLAY_MODE{ 2560, 1600, 60 }));
        PANEL_CHECK(table.Contains(PANEL_DISPLAY_MODE{ 1920, 1200, 60 }));

        //
        // An invalid mode changes nothing at all.
        //
        const ULONG countBefore = table.Count();
        const ULONG preferredBefore = table.PreferredIndex();
        PANEL_CHECK(!table.SetPreferredMode(PANEL_DISPLAY_MODE{ 1, 1, 1 }));
        PANEL_CHECK_EQ(table.Count(), countBefore);
        PANEL_CHECK_EQ(table.PreferredIndex(), preferredBefore);

        table.Reset();
        PANEL_CHECK_EQ(table.Count(), DefaultModeCount);
        PANEL_CHECK_EQ(table.PreferredIndex(), DefaultPreferredModeIndex());
    }

    void TestModeTableCapacity()
    {
        PANEL_TEST_CASE("ModeTable never overflows its fixed array");

        ModeTable table;

        //
        // Push distinct valid modes well past capacity; the table must stay
        // within PANEL_MAX_REPORTED_MODES and keep honouring the last request.
        //
        for (ULONG i = 0; i < PANEL_MAX_REPORTED_MODES * 2; ++i)
        {
            const PANEL_DISPLAY_MODE mode = { 1280 + (i * 2), 800, 60 };
            PANEL_CHECK(table.SetPreferredMode(mode));
            PANEL_CHECK(table.Count() <= PANEL_MAX_REPORTED_MODES);
            PANEL_CHECK(AreModesEqual(table.Preferred(), mode));
        }

        PANEL_CHECK_EQ(table.Count(), static_cast<ULONG>(PANEL_MAX_REPORTED_MODES));

        //
        // At() clamps rather than reading past the end.
        //
        PANEL_CHECK(table.At(PANEL_MAX_REPORTED_MODES + 5).Width != 0);
    }

    // -----------------------------------------------------------------------
    // Connect / disconnect state machine
    // -----------------------------------------------------------------------

    void TestStateMachineRequiresReadyAdapter()
    {
        PANEL_TEST_CASE("Connect is refused until the adapter is ready");

        MonitorStateMachine state;
        PANEL_COMMAND_RESULT result = PanelCommandResultFailed;

        PANEL_CHECK(state.DriverState() == PanelDriverStateInitializing);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateDisconnected);

        PANEL_CHECK(!state.BeginConnect(result));
        PANEL_CHECK(result == PanelCommandResultAdapterNotReady);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateDisconnected);
        PANEL_CHECK(!state.IsTransitioning());

        state.OnAdapterReady();
        PANEL_CHECK(state.DriverState() == PanelDriverStateReady);
        PANEL_CHECK(state.BeginConnect(result));
    }

    void TestStateMachineHappyPath()
    {
        PANEL_TEST_CASE("Connect then disconnect walks the expected states");

        MonitorStateMachine state;
        PANEL_COMMAND_RESULT result = PanelCommandResultFailed;

        state.OnAdapterReady();

        PANEL_CHECK(state.BeginConnect(result));
        PANEL_CHECK(state.IsTransitioning());

        state.CompleteConnect(true, result);
        PANEL_CHECK(result == PanelCommandResultOk);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateConnected);
        PANEL_CHECK(!state.IsTransitioning());
        PANEL_CHECK_EQ(state.ConnectCount(), 1u);

        PANEL_CHECK(state.BeginDisconnect(result));
        state.CompleteDisconnect(true, result);
        PANEL_CHECK(result == PanelCommandResultOk);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateDisconnected);
        PANEL_CHECK(!state.IsTransitioning());

        //
        // A second cycle works and increments the counter again.
        //
        PANEL_CHECK(state.BeginConnect(result));
        state.CompleteConnect(true, result);
        PANEL_CHECK_EQ(state.ConnectCount(), 2u);
    }

    void TestStateMachineIdempotency()
    {
        PANEL_TEST_CASE("Duplicate connect and disconnect are idempotent");

        MonitorStateMachine state;
        PANEL_COMMAND_RESULT result = PanelCommandResultFailed;

        state.OnAdapterReady();

        //
        // DISCONNECT while already disconnected.
        //
        PANEL_CHECK(!state.BeginDisconnect(result));
        PANEL_CHECK(result == PanelCommandResultAlreadyDisconnected);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateDisconnected);

        state.BeginConnect(result);
        state.CompleteConnect(true, result);

        //
        // CONNECT while already connected. Critically, this must NOT report that
        // a monitor should be created - that is how a second monitor would leak.
        //
        PANEL_CHECK(!state.BeginConnect(result));
        PANEL_CHECK(result == PanelCommandResultAlreadyConnected);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateConnected);
        PANEL_CHECK_EQ(state.ConnectCount(), 1u);

        //
        // Repeating it does not drift either.
        //
        PANEL_CHECK(!state.BeginConnect(result));
        PANEL_CHECK(result == PanelCommandResultAlreadyConnected);
        PANEL_CHECK_EQ(state.ConnectCount(), 1u);

        state.BeginDisconnect(result);
        state.CompleteDisconnect(true, result);

        PANEL_CHECK(!state.BeginDisconnect(result));
        PANEL_CHECK(result == PanelCommandResultAlreadyDisconnected);
    }

    void TestStateMachineFailedConnectIsRetryable()
    {
        PANEL_TEST_CASE("A failed monitor creation leaves a retryable state");

        MonitorStateMachine state;
        PANEL_COMMAND_RESULT result = PanelCommandResultFailed;

        state.OnAdapterReady();

        PANEL_CHECK(state.BeginConnect(result));
        state.CompleteConnect(false, result);

        PANEL_CHECK(result == PanelCommandResultFailed);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateDisconnected);
        PANEL_CHECK(!state.IsTransitioning());
        PANEL_CHECK_EQ(state.ConnectCount(), 0u);

        //
        // The next attempt must be allowed to proceed.
        //
        PANEL_CHECK(state.BeginConnect(result));
        state.CompleteConnect(true, result);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateConnected);
    }

    void TestStateMachineRejectsOverlappingTransitions()
    {
        PANEL_TEST_CASE("A second command during a transition is refused, not queued");

        MonitorStateMachine state;
        PANEL_COMMAND_RESULT result = PanelCommandResultFailed;

        state.OnAdapterReady();

        PANEL_CHECK(state.BeginConnect(result));

        //
        // While a connect is in flight neither command may start work.
        //
        PANEL_CHECK(!state.BeginConnect(result));
        PANEL_CHECK(result == PanelCommandResultFailed);
        PANEL_CHECK(!state.BeginDisconnect(result));
        PANEL_CHECK(result == PanelCommandResultFailed);

        state.CompleteConnect(true, result);

        PANEL_CHECK(state.BeginDisconnect(result));
        PANEL_CHECK(!state.BeginConnect(result));
        state.CompleteDisconnect(true, result);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateDisconnected);
    }

    void TestStateMachineDeviceStopped()
    {
        PANEL_TEST_CASE("Device stop drops the monitor and blocks further connects");

        MonitorStateMachine state;
        PANEL_COMMAND_RESULT result = PanelCommandResultFailed;

        state.OnAdapterReady();
        state.BeginConnect(result);
        state.CompleteConnect(true, result);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateConnected);

        state.OnDeviceStopped();

        PANEL_CHECK(state.DriverState() == PanelDriverStateStopped);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateDisconnected);
        PANEL_CHECK(!state.IsTransitioning());

        PANEL_CHECK(!state.BeginConnect(result));
        PANEL_CHECK(result == PanelCommandResultAdapterNotReady);

        state.OnAdapterReady();
        PANEL_CHECK(state.BeginConnect(result));
    }

    void TestStateMachineRepeatedLifecycle()
    {
        PANEL_TEST_CASE("Ten lifecycle cycles and duplicate commands remain stable");

        MonitorStateMachine state;
        PANEL_COMMAND_RESULT result = PanelCommandResultFailed;
        state.OnAdapterReady();

        for (ULONG cycle = 0; cycle < 10; ++cycle)
        {
            PANEL_CHECK(state.BeginConnect(result));
            state.CompleteConnect(true, result);
            PANEL_CHECK(result == PanelCommandResultOk);
            PANEL_CHECK(state.MonitorState() == PanelMonitorStateConnected);

            PANEL_CHECK(state.BeginDisconnect(result));
            state.CompleteDisconnect(true, result);
            PANEL_CHECK(result == PanelCommandResultOk);
            PANEL_CHECK(state.MonitorState() == PanelMonitorStateDisconnected);
        }

        PANEL_CHECK_EQ(state.ConnectCount(), 10u);

        PANEL_CHECK(state.BeginConnect(result));
        state.CompleteConnect(true, result);
        PANEL_CHECK(!state.BeginConnect(result));
        PANEL_CHECK(result == PanelCommandResultAlreadyConnected);

        PANEL_CHECK(state.BeginDisconnect(result));
        state.CompleteDisconnect(true, result);
        PANEL_CHECK(!state.BeginDisconnect(result));
        PANEL_CHECK(result == PanelCommandResultAlreadyDisconnected);
        PANEL_CHECK_EQ(state.ConnectCount(), 11u);
    }

    void TestStateMachineFailedDepartureIsReportedWithoutHandleReuse()
    {
        PANEL_TEST_CASE("A failed departure is reported and leaves no reusable monitor state");

        MonitorStateMachine state;
        PANEL_COMMAND_RESULT result = PanelCommandResultFailed;
        state.OnAdapterReady();
        PANEL_CHECK(state.BeginConnect(result));
        state.CompleteConnect(true, result);

        PANEL_CHECK(state.BeginDisconnect(result));
        state.CompleteDisconnect(false, result);

        PANEL_CHECK(result == PanelCommandResultFailed);
        PANEL_CHECK(state.DriverState() == PanelDriverStateStopped);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateDisconnected);
        PANEL_CHECK(!state.IsTransitioning());
        PANEL_CHECK(!state.BeginDisconnect(result));
        PANEL_CHECK(result == PanelCommandResultAlreadyDisconnected);
        PANEL_CHECK(!state.BeginConnect(result));
        PANEL_CHECK(result == PanelCommandResultAdapterNotReady);

        state.OnAdapterReady();
        PANEL_CHECK(state.BeginConnect(result));
    }

    void TestStateMachineOutOfOrderCompletion()
    {
        PANEL_TEST_CASE("Completing a transition that never started is rejected");

        MonitorStateMachine state;
        PANEL_COMMAND_RESULT result = PanelCommandResultOk;

        state.OnAdapterReady();

        state.CompleteConnect(true, result);
        PANEL_CHECK(result == PanelCommandResultFailed);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateDisconnected);

        result = PanelCommandResultOk;
        state.CompleteDisconnect(true, result);
        PANEL_CHECK(result == PanelCommandResultFailed);
        PANEL_CHECK(state.MonitorState() == PanelMonitorStateDisconnected);
    }

    // -----------------------------------------------------------------------
    // Latest-frame store and v3 frame contract
    // -----------------------------------------------------------------------

    std::vector<BYTE> MakeTestPixels(BYTE Seed)
    {
        std::vector<BYTE> pixels(4u * 2u * 4u);
        for (size_t index = 0; index < pixels.size(); ++index)
        {
            pixels[index] = static_cast<BYTE>(Seed + index);
        }
        return pixels;
    }

    void SetTestPixel(std::vector<BYTE>& Pixels,
                      ULONG Stride,
                      PANEL_FRAME_FORMAT Format,
                      ULONG X,
                      ULONG Y,
                      BYTE Blue,
                      BYTE Green,
                      BYTE Red,
                      BYTE Alpha)
    {
        const size_t offset = static_cast<size_t>(Y) * Stride +
                              static_cast<size_t>(X) * 4u;
        if (Format == PanelFrameFormatBgra8888)
        {
            Pixels[offset + 0] = Blue;
            Pixels[offset + 1] = Green;
            Pixels[offset + 2] = Red;
            Pixels[offset + 3] = Alpha;
        }
        else
        {
            Pixels[offset + 0] = Red;
            Pixels[offset + 1] = Green;
            Pixels[offset + 2] = Blue;
            Pixels[offset + 3] = Alpha;
        }
    }

    void TestFrameDiagnosticsCrc32()
    {
        PANEL_TEST_CASE("Frame diagnostics uses standard IEEE CRC-32");

        const BYTE reference[] = { '1', '2', '3', '4', '5', '6', '7', '8', '9' };
        PANEL_CHECK_EQ(ComputeFrameCrc32(reference, ARRAYSIZE(reference)), 0xCBF43926u);
        PANEL_CHECK_EQ(ComputeFrameCrc32(nullptr, 0), 0u);
    }

    void TestFrameDiagnosticsCompactBgraAndAlpha()
    {
        PANEL_TEST_CASE("Compact BGRA diagnostics preserve colours and zero alpha");

        constexpr ULONG width = 2;
        constexpr ULONG height = 2;
        constexpr ULONG stride = width * 4u;
        std::vector<BYTE> pixels(stride * height, 0);

        SetTestPixel(pixels, stride, PanelFrameFormatBgra8888,
                     0, 0, 0x33, 0x22, 0x11, 0x00);
        SetTestPixel(pixels, stride, PanelFrameFormatBgra8888,
                     1, 0, 0x00, 0xFF, 0x00, 0xFF);
        SetTestPixel(pixels, stride, PanelFrameFormatBgra8888,
                     0, 1, 0xFF, 0x00, 0x00, 0xFF);
        SetTestPixel(pixels, stride, PanelFrameFormatBgra8888,
                     1, 1, 0xFF, 0xFF, 0xFF, 0xFF);

        FrameDiagnosticSummary summary = {};
        PANEL_CHECK(TryAnalyzeFrame(width, height, stride,
                                    PanelFrameFormatBgra8888,
                                    pixels.data(), pixels.size(), summary));
        PANEL_CHECK_EQ(summary.TopLeft.Blue, 0x33u);
        PANEL_CHECK_EQ(summary.TopLeft.Green, 0x22u);
        PANEL_CHECK_EQ(summary.TopLeft.Red, 0x11u);
        PANEL_CHECK_EQ(summary.TopLeft.Alpha, 0u);
        PANEL_CHECK_EQ(summary.TopRight.Green, 0xFFu);
        PANEL_CHECK_EQ(summary.BottomLeft.Blue, 0xFFu);
        PANEL_CHECK_EQ(summary.BottomRight.Red, 0xFFu);
        PANEL_CHECK_EQ(summary.BottomRight.Green, 0xFFu);
        PANEL_CHECK_EQ(summary.BottomRight.Blue, 0xFFu);
        PANEL_CHECK_EQ(summary.Luminance.SampleCount, 256u);
        PANEL_CHECK_EQ(summary.Luminance.NonBlackSamples, 256u);
        PANEL_CHECK_EQ(summary.Luminance.Maximum, 255u);
    }

    void TestFrameDiagnosticsPaddedRgba()
    {
        PANEL_TEST_CASE("Padded RGBA diagnostics skip RowPitch padding and map channels");

        constexpr ULONG width = 2;
        constexpr ULONG height = 2;
        constexpr ULONG stride = 16;
        std::vector<BYTE> pixels(stride * height, 0xEE);

        SetTestPixel(pixels, stride, PanelFrameFormatRgba8888,
                     0, 0, 3, 2, 1, 4);
        SetTestPixel(pixels, stride, PanelFrameFormatRgba8888,
                     1, 0, 30, 20, 10, 40);
        SetTestPixel(pixels, stride, PanelFrameFormatRgba8888,
                     0, 1, 90, 80, 70, 60);
        SetTestPixel(pixels, stride, PanelFrameFormatRgba8888,
                     1, 1, 130, 120, 110, 100);

        FrameDiagnosticSummary summary = {};
        PANEL_CHECK(TryAnalyzeFrame(width, height, stride,
                                    PanelFrameFormatRgba8888,
                                    pixels.data(), pixels.size(), summary));
        PANEL_CHECK_EQ(summary.TopLeft.Blue, 3u);
        PANEL_CHECK_EQ(summary.TopLeft.Green, 2u);
        PANEL_CHECK_EQ(summary.TopLeft.Red, 1u);
        PANEL_CHECK_EQ(summary.TopLeft.Alpha, 4u);
        PANEL_CHECK_EQ(summary.BottomRight.Blue, 130u);
        PANEL_CHECK_EQ(summary.BottomRight.Green, 120u);
        PANEL_CHECK_EQ(summary.BottomRight.Red, 110u);
        PANEL_CHECK_EQ(summary.BottomRight.Alpha, 100u);

        const ULONG crcWithPadding = summary.Crc32;
        pixels[8] ^= 0xFF;
        PANEL_CHECK(TryAnalyzeFrame(width, height, stride,
                                    PanelFrameFormatRgba8888,
                                    pixels.data(), pixels.size(), summary));
        PANEL_CHECK(summary.Crc32 != crcWithPadding);
        PANEL_CHECK_EQ(summary.TopLeft.Blue, 3u);
        PANEL_CHECK_EQ(summary.BottomRight.Blue, 130u);
    }

    void TestFrameDiagnosticsBlackFrame()
    {
        PANEL_TEST_CASE("Black frame diagnostics report no visible samples");

        constexpr ULONG width = 4;
        constexpr ULONG height = 4;
        constexpr ULONG stride = width * 4u;
        std::vector<BYTE> pixels(stride * height, 0);

        FrameDiagnosticSummary summary = {};
        PANEL_CHECK(TryAnalyzeFrame(width, height, stride,
                                    PanelFrameFormatBgra8888,
                                    pixels.data(), pixels.size(), summary));
        PANEL_CHECK_EQ(summary.Luminance.Minimum, 0u);
        PANEL_CHECK_EQ(summary.Luminance.Maximum, 0u);
        PANEL_CHECK_EQ(summary.Luminance.Average, 0u);
        PANEL_CHECK_EQ(summary.Luminance.NonBlackSamples, 0u);
        PANEL_CHECK_EQ(summary.Luminance.SampleCount, 256u);
    }

    void TestLatestFrameStorePreservesPaddedStride()
    {
        PANEL_TEST_CASE("LatestFrameStore preserves compact and padded rows exactly");

        LatestFrameStore store;
        const ULONGLONG generation = store.BeginGeneration();

        constexpr ULONG width = 3;
        constexpr ULONG height = 2;
        constexpr ULONG compactStride = width * 4u;
        const auto compact = std::vector<BYTE>(compactStride * height, 0x21);
        ULONGLONG compactSequence = 0;
        PANEL_CHECK(store.TryPublish(generation, 1, 1000, width, height,
                                     compactStride, PanelFrameFormatBgra8888,
                                     compact.data(), compact.size(), &compactSequence));
        PANEL_CHECK(compactSequence != 0);

        constexpr ULONG paddedStride = 20;
        const auto padded = std::vector<BYTE>(paddedStride * height, 0xA5);
        ULONGLONG paddedSequence = 0;
        PANEL_CHECK(store.TryPublish(generation, 2, 1000, width, height,
                                     paddedStride, PanelFrameFormatRgba8888,
                                     padded.data(), padded.size(), &paddedSequence));
        PANEL_CHECK(paddedSequence > compactSequence);

        PANEL_FRAME_OUTPUT output = {};
        std::shared_ptr<const LatestFrameSnapshot> snapshot;
        PANEL_CHECK(store.ReadFrame(generation, paddedSequence, output, snapshot) ==
                    PanelFrameResultOk);
        PANEL_CHECK(snapshot != nullptr);
        PANEL_CHECK_EQ(output.Stride, paddedStride);
        PANEL_CHECK_EQ(output.PayloadBytes, padded.size());
        PANEL_CHECK(snapshot->Pixels == padded);
    }

    void TestFrameContractLayoutAndIoctls()
    {
        PANEL_TEST_CASE("Frame protocol v5 layout and IOCTL access stay stable");

        PANEL_CHECK_EQ(PANEL_DISPLAY_PROTOCOL_VERSION, 5u);
        PANEL_CHECK_EQ(sizeof(PANEL_FRAME_QUERY_INPUT), 16u);
        PANEL_CHECK_EQ(sizeof(PANEL_FRAME_READ_INPUT), 24u);
        PANEL_CHECK_EQ(sizeof(PANEL_FRAME_OUTPUT), 64u);
        PANEL_CHECK_EQ(IOCTL_PANEL_GET_FRAME_INFO,
            CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 3,
                     METHOD_BUFFERED, FILE_READ_ACCESS));
        PANEL_CHECK_EQ(IOCTL_PANEL_READ_LATEST_FRAME,
            CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 4,
                     METHOD_OUT_DIRECT, FILE_READ_ACCESS));
        PANEL_CHECK_EQ(sizeof(PANEL_CAPTURE_CONFIG_INPUT), 8u);
        PANEL_CHECK_EQ(sizeof(PANEL_CAPTURE_CONFIG_OUTPUT), 16u);
        PANEL_CHECK_EQ(sizeof(PANEL_CAPTURE_METRICS_OUTPUT), 128u);
        PANEL_CHECK_EQ(sizeof(PANEL_CAPTURE_PACING_METRICS_OUTPUT), 200u);
        PANEL_CHECK_EQ(PANEL_CAPTURE_LATENESS_HISTOGRAM_BUCKETS, 16u);
        PANEL_CHECK_EQ(IOCTL_PANEL_GET_CAPTURE_METRICS,
            CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 5,
                     METHOD_BUFFERED, FILE_READ_ACCESS));
        PANEL_CHECK_EQ(IOCTL_PANEL_SET_CAPTURE_CONFIG,
            CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 6,
                     METHOD_BUFFERED, FILE_WRITE_ACCESS));
        PANEL_CHECK_EQ(IOCTL_PANEL_GET_CAPTURE_PACING_METRICS,
            CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 7,
                     METHOD_BUFFERED, FILE_READ_ACCESS));
    }

    void TestLatestFrameStoreEmptyAndNoNewFrame()
    {
        PANEL_TEST_CASE("LatestFrameStore reports empty and no-new-frame normally");

        LatestFrameStore store;
        PANEL_FRAME_OUTPUT output = {};

        PANEL_CHECK(store.GetFrameInfo(0, output) == PanelFrameResultNoFrame);
        PANEL_CHECK_EQ(output.Size, sizeof(PANEL_FRAME_OUTPUT));
        PANEL_CHECK_EQ(output.PayloadBytes, 0u);

        const ULONGLONG generation = store.BeginGeneration();
        const auto pixels = MakeTestPixels(1);
        PANEL_CHECK(store.TryPublish(generation, 100, 10'000'000, 4, 2, 16,
                                     PanelFrameFormatBgra8888,
                                     pixels.data(), pixels.size()));

        PANEL_CHECK(store.GetFrameInfo(0, output) == PanelFrameResultOk);
        const ULONGLONG sequence = output.Sequence;
        PANEL_CHECK(sequence != 0);
        PANEL_CHECK(store.GetFrameInfo(sequence, output) == PanelFrameResultNoNewFrame);
        PANEL_CHECK_EQ(output.Sequence, sequence);
        PANEL_CHECK_EQ(output.PayloadBytes, pixels.size());
    }

    void TestLatestFrameStorePublicationAndRead()
    {
        PANEL_TEST_CASE("LatestFrameStore publishes complete immutable snapshots");

        LatestFrameStore store;
        const ULONGLONG generation = store.BeginGeneration();
        const auto pixels = MakeTestPixels(7);

        PANEL_CHECK(store.TryPublish(generation, 1234, 10'000'000, 4, 2, 16,
                                     PanelFrameFormatRgba8888,
                                     pixels.data(), pixels.size()));

        PANEL_FRAME_OUTPUT info = {};
        PANEL_CHECK(store.GetFrameInfo(0, info) == PanelFrameResultOk);
        PANEL_CHECK_EQ(info.Generation, generation);
        PANEL_CHECK_EQ(info.CaptureTimestampQpc, 1234u);
        PANEL_CHECK_EQ(info.Width, 4u);
        PANEL_CHECK_EQ(info.Height, 2u);
        PANEL_CHECK_EQ(info.Stride, 16u);
        PANEL_CHECK(info.Format == PanelFrameFormatRgba8888);

        PANEL_FRAME_OUTPUT read = {};
        std::shared_ptr<const LatestFrameSnapshot> snapshot;
        PANEL_CHECK(store.ReadFrame(generation, info.Sequence, read, snapshot) ==
                    PanelFrameResultOk);
        PANEL_CHECK(snapshot != nullptr);
        PANEL_CHECK(snapshot->Pixels == pixels);
        PANEL_CHECK_EQ(read.PayloadBytes, pixels.size());
    }

    void TestLatestFrameStoreSequenceReplacementAndDrop()
    {
        PANEL_TEST_CASE("LatestFrameStore replaces old frames and rejects stale publishers");

        LatestFrameStore store;
        const ULONGLONG generation = store.BeginGeneration();
        const auto firstPixels = MakeTestPixels(10);
        const auto secondPixels = MakeTestPixels(20);

        PANEL_CHECK(store.TryPublish(generation, 1, 1000, 4, 2, 16,
                                     PanelFrameFormatBgra8888,
                                     firstPixels.data(), firstPixels.size()));
        PANEL_FRAME_OUTPUT first = {};
        store.GetFrameInfo(0, first);

        PANEL_CHECK(store.TryPublish(generation, 2, 1000, 4, 2, 16,
                                     PanelFrameFormatBgra8888,
                                     secondPixels.data(), secondPixels.size()));
        PANEL_FRAME_OUTPUT second = {};
        store.GetFrameInfo(first.Sequence, second);

        PANEL_CHECK(second.Sequence > first.Sequence);

        PANEL_FRAME_OUTPUT replaced = {};
        std::shared_ptr<const LatestFrameSnapshot> snapshot;
        PANEL_CHECK(store.ReadFrame(generation, first.Sequence, replaced, snapshot) ==
                    PanelFrameResultFrameReplaced);
        PANEL_CHECK(snapshot == nullptr);
        PANEL_CHECK_EQ(replaced.Sequence, second.Sequence);

        const ULONGLONG dropsBefore = store.DroppedPublications();
        store.InvalidateGeneration(generation);
        PANEL_CHECK(!store.TryPublish(generation, 3, 1000, 4, 2, 16,
                                      PanelFrameFormatBgra8888,
                                      firstPixels.data(), firstPixels.size()));
        PANEL_CHECK_EQ(store.DroppedPublications(), dropsBefore + 1);
    }

    void TestLatestFrameStoreGenerationAndCleanup()
    {
        PANEL_TEST_CASE("Swapchain generations clear pixels without resetting sequence");

        LatestFrameStore store;
        const auto pixels = MakeTestPixels(30);
        const ULONGLONG oldGeneration = store.BeginGeneration();
        PANEL_CHECK(store.TryPublish(oldGeneration, 1, 1000, 4, 2, 16,
                                     PanelFrameFormatBgra8888,
                                     pixels.data(), pixels.size()));

        PANEL_FRAME_OUTPUT oldInfo = {};
        store.GetFrameInfo(0, oldInfo);

        const ULONGLONG newGeneration = store.BeginGeneration();
        PANEL_CHECK(newGeneration != oldGeneration);

        PANEL_FRAME_OUTPUT empty = {};
        PANEL_CHECK(store.GetFrameInfo(0, empty) == PanelFrameResultNoFrame);
        PANEL_CHECK_EQ(empty.Generation, newGeneration);
        PANEL_CHECK(!store.TryPublish(oldGeneration, 2, 1000, 4, 2, 16,
                                      PanelFrameFormatBgra8888,
                                      pixels.data(), pixels.size()));
        PANEL_CHECK(store.TryPublish(newGeneration, 3, 1000, 4, 2, 16,
                                     PanelFrameFormatBgra8888,
                                     pixels.data(), pixels.size()));

        PANEL_FRAME_OUTPUT newInfo = {};
        store.GetFrameInfo(0, newInfo);
        PANEL_CHECK(newInfo.Sequence > oldInfo.Sequence);

        store.InvalidateAll();
        PANEL_CHECK(store.GetFrameInfo(0, empty) == PanelFrameResultNoFrame);
        PANEL_CHECK(empty.Generation != newGeneration);
    }

    void TestLatestFrameStoreRejectsInvalidFrames()
    {
        PANEL_TEST_CASE("LatestFrameStore rejects invalid sizes and formats defensively");

        LatestFrameStore store;
        const ULONGLONG generation = store.BeginGeneration();
        const auto pixels = MakeTestPixels(40);

        PANEL_CHECK(!store.TryPublish(generation, 1, 1000, 0, 2, 16,
                                      PanelFrameFormatBgra8888,
                                      pixels.data(), pixels.size()));
        PANEL_CHECK(!store.TryPublish(generation, 1, 1000, 4, 2, 15,
                                      PanelFrameFormatBgra8888,
                                      pixels.data(), pixels.size()));
        PANEL_CHECK(!store.TryPublish(generation, 1, 1000, 4, 2, 16,
                                      PanelFrameFormatUnknown,
                                      pixels.data(), pixels.size()));
        PANEL_CHECK(!store.TryPublish(generation, 1, 1000, 4, 2, 16,
                                      PanelFrameFormatBgra8888,
                                      nullptr, pixels.size()));
        PANEL_CHECK(!store.TryPublish(generation, 1, 1000, 4, 2, 16,
                                      PanelFrameFormatBgra8888,
                                      pixels.data(), pixels.size() - 1));
        PANEL_CHECK(!store.TryPublish(generation, 1, 1000, 4, 2,
                                      MaxCapturedFramePayloadBytes,
                                      PanelFrameFormatBgra8888,
                                      pixels.data(), MaxCapturedFramePayloadBytes));

        PANEL_FRAME_OUTPUT output = {};
        PANEL_CHECK(store.GetFrameInfo(0, output) == PanelFrameResultNoFrame);
    }
}

int main()
{
    printf("PanelDisplayDriver unit tests\n\n");

    TestCaptureSchedulerNominalRates();
    TestCaptureSchedulerJitter();
    TestCaptureSchedulerProducerTargetMatrix();
    TestCaptureSchedulerPhysicalJitterMatrix();
    TestCaptureSchedulerLongPauseAndResume();
    TestCaptureSchedulerReportsDeadlineLatenessAndMisses();
    TestCaptureSchedulerHasNoLongRunDrift();
    TestCaptureSchedulerRuntimeTargetChanges();

    TestParseModeSpec();
    TestParseCommandLineVerbs();
    TestParseCommandLineHelpAndErrors();
    TestParseCommandLineOptions();

    TestModeValidation();
    TestModeTableDefaults();
    TestModeTableSetPreferred();
    TestModeTableCapacity();

    TestStateMachineRequiresReadyAdapter();
    TestStateMachineHappyPath();
    TestStateMachineIdempotency();
    TestStateMachineFailedConnectIsRetryable();
    TestStateMachineRejectsOverlappingTransitions();
    TestStateMachineDeviceStopped();
    TestStateMachineRepeatedLifecycle();
    TestStateMachineFailedDepartureIsReportedWithoutHandleReuse();
    TestStateMachineOutOfOrderCompletion();

    TestFrameContractLayoutAndIoctls();
    TestFrameDiagnosticsCrc32();
    TestFrameDiagnosticsCompactBgraAndAlpha();
    TestFrameDiagnosticsPaddedRgba();
    TestFrameDiagnosticsBlackFrame();
    TestLatestFrameStorePreservesPaddedStride();
    TestLatestFrameStoreEmptyAndNoNewFrame();
    TestLatestFrameStorePublicationAndRead();
    TestLatestFrameStoreSequenceReplacementAndDrop();
    TestLatestFrameStoreGenerationAndCleanup();
    TestLatestFrameStoreRejectsInvalidFrames();

    printf("\n%d checks, %d failures\n", PanelTest::CheckCount(), PanelTest::FailureCount());

    return PanelTest::FailureCount() == 0 ? 0 : 1;
}
