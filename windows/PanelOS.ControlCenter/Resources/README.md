# Control Center resources

`ControlCenterStrings.resx` is the neutral resource set and contains the current
default Portuguese UI text used by C# code. Keep Portuguese in this neutral file
until the product deliberately chooses another default locale.

Add a future translation as a sibling culture file, for example
`ControlCenterStrings.en.resx`. The SDK turns culture-suffixed files into
satellite assemblies, and `ControlCenterStrings` resolves them through
`CurrentUICulture` with the neutral Portuguese resources as fallback.
