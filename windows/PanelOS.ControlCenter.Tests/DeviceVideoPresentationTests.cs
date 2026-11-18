using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Management;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// The video section of the Device page. Its whole job is to report what the
/// agent measured and to be visibly silent about everything else.
/// </summary>
[TestClass]
public sealed class DeviceVideoPresentationTests
{
    [TestMethod]
    public void WithoutAManagementChannelEveryVideoFactIsAbsent()
    {
        var viewModel = new DashboardViewModel(
            new FakePanelDriverController(),
            new FakePanelHostProcessManager());

        Assert.IsFalse(viewModel.HasVideoSession);
        Assert.AreEqual("—", viewModel.VideoStatusText);
        Assert.AreEqual("—", viewModel.VideoCodecText);
        Assert.AreEqual("—", viewModel.VideoSendFpsText);
        Assert.AreEqual("—", viewModel.VideoCaptureFpsText);
        Assert.AreEqual("—", viewModel.VideoReceiveFpsText);
        Assert.AreEqual("—", viewModel.VideoDropsText);
        Assert.AreEqual("—", viewModel.VideoNegotiatedFpsText);
        Assert.AreEqual(PanelStatusLevel.Neutral, viewModel.VideoLevel);
    }

    [TestMethod]
    public void AConnectedSessionReportsTheRatesTheAgentMeasured()
    {
        var management = new FakeManagementClient();
        var viewModel = new DashboardViewModel(
            new FakePanelDriverController(),
            new FakePanelHostProcessManager(),
            managementClient: management);

        management.Publish(Snapshots.Healthy());

        Assert.IsTrue(viewModel.HasVideoSession);
        Assert.AreEqual("Ativo", viewModel.VideoStatusText);
        Assert.AreEqual(PanelStatusLevel.Good, viewModel.VideoLevel);
        Assert.AreEqual("H264", viewModel.VideoCodecText);
        Assert.AreEqual("hardware encoder", viewModel.VideoEncoderText);
        Assert.AreEqual("1920 × 1200", viewModel.VideoResolutionText);
        Assert.AreEqual("60 FPS", viewModel.VideoConfiguredFpsText);
        Assert.AreEqual("60 FPS", viewModel.VideoNegotiatedFpsText);
        Assert.AreEqual("59,4 FPS", viewModel.VideoCaptureFpsText);
        Assert.AreEqual("59,2 FPS", viewModel.VideoReceiveFpsText);
        Assert.AreEqual("59,1 FPS", viewModel.VideoSendFpsText);
        Assert.AreEqual("0 frames", viewModel.VideoDropsText);
    }

    [TestMethod]
    public void AMetricTheAgentDidNotReportIsAbsentRatherThanZero()
    {
        var management = new FakeManagementClient();
        var viewModel = new DashboardViewModel(
            new FakePanelDriverController(),
            new FakePanelHostProcessManager(),
            managementClient: management);

        var snapshot = Snapshots.Healthy();
        management.Publish(snapshot with
        {
            Video = snapshot.Video with
            {
                SendFps = null,
                DriverCaptureFps = null,
                SequenceGap = null,
                Codec = null,
            },
        });

        // "0 FPS" would read as a stalled pipeline that was measured; "—" is
        // the truth, which is that nothing was reported.
        Assert.AreEqual("—", viewModel.VideoSendFpsText);
        Assert.AreEqual("—", viewModel.VideoCaptureFpsText);
        Assert.AreEqual("—", viewModel.VideoDropsText);
        Assert.AreEqual("—", viewModel.VideoCodecText);

        // The session itself is still connected, and the rates that did arrive
        // are still shown.
        Assert.IsTrue(viewModel.HasVideoSession);
        Assert.AreEqual("59,2 FPS", viewModel.VideoReceiveFpsText);
    }

    [TestMethod]
    public void ADisconnectedSessionReportsNoRatesAtAll()
    {
        var management = new FakeManagementClient();
        var viewModel = new DashboardViewModel(
            new FakePanelDriverController(),
            new FakePanelHostProcessManager(),
            managementClient: management);

        management.Publish(Snapshots.Healthy());
        management.Publish(Snapshots.Idle());

        Assert.IsFalse(viewModel.HasVideoSession);
        Assert.AreEqual("Inativo", viewModel.VideoStatusText);
        Assert.AreEqual("—", viewModel.VideoSendFpsText);
        Assert.AreEqual("—", viewModel.VideoResolutionText);
        StringAssert.Contains(viewModel.VideoDetail, "Nenhuma sessão");
    }

    [TestMethod]
    public void TheTabletRenderRateIsNeverInvented()
    {
        var management = new FakeManagementClient();
        var viewModel = new DashboardViewModel(
            new FakePanelDriverController(),
            new FakePanelHostProcessManager(),
            managementClient: management);

        management.Publish(Snapshots.Healthy());

        // The tablet measures what it draws; Management v1 does not carry it
        // back. Borrowing the send rate here would present one measurement as
        // another, so the row stays absent and explains why.
        Assert.AreEqual("—", viewModel.VideoRenderFpsText);
        StringAssert.Contains(viewModel.VideoRenderFpsNote, "Management v1");
    }

    [TestMethod]
    public void DroppedFramesRaiseTheVideoStateToAWarning()
    {
        var management = new FakeManagementClient();
        var viewModel = new DashboardViewModel(
            new FakePanelDriverController(),
            new FakePanelHostProcessManager(),
            managementClient: management);

        var snapshot = Snapshots.Healthy();
        management.Publish(snapshot with
        {
            Video = snapshot.Video with { SequenceGap = 12 },
        });

        Assert.IsTrue(viewModel.HasVideoDrops);
        Assert.AreEqual("12 frames", viewModel.VideoDropsText);
        Assert.AreEqual(PanelStatusLevel.Warning, viewModel.VideoDropsLevel);
        Assert.AreEqual(PanelStatusLevel.Warning, viewModel.VideoLevel);
    }

    [TestMethod]
    public void TheTabletSectionReportsSessionsWithoutExposingIdentifiers()
    {
        var management = new FakeManagementClient();
        var viewModel = new DashboardViewModel(
            new FakePanelDriverController(),
            new FakePanelHostProcessManager(),
            managementClient: management);

        management.Publish(Snapshots.Healthy());

        Assert.AreEqual("Conectado", viewModel.TabletControlText);
        Assert.AreEqual("Conectado", viewModel.TabletVideoText);
        Assert.AreEqual("Protocolo v1", viewModel.TabletProtocolText);
        StringAssert.Contains(viewModel.TabletFeaturesText, "stream_deck");

        // Session generations are counters, not identities: a climbing number
        // is how a flapping link shows itself, and nothing here is a device id.
        StringAssert.Contains(viewModel.TabletSessionText, "2");
    }

    [TestMethod]
    public void TheManagementChannelReportsItsOwnState()
    {
        var management = new FakeManagementClient();
        var viewModel = new DashboardViewModel(
            new FakePanelDriverController(),
            new FakePanelHostProcessManager(),
            managementClient: management);

        Assert.IsFalse(viewModel.IsManagementConnected);
        Assert.AreEqual("Indisponível", viewModel.ManagementText);

        management.Publish(Snapshots.Healthy());

        Assert.IsTrue(viewModel.IsManagementConnected);
        StringAssert.Contains(
            viewModel.ManagementText,
            $"v{PanelHostManagementProtocol.Version}");
        Assert.AreEqual(PanelStatusLevel.Good, viewModel.ManagementLevel);
    }
}
