using System.Runtime.CompilerServices;

// The test project needs the internal injectable controller constructor to
// cover driver-unavailable/driver-ready mapping without an installed driver.
[assembly: InternalsVisibleTo("PanelOS.ControlCenter.Tests")]
