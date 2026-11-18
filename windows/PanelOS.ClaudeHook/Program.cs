using PanelOS.Windows.Management;

// Hooks must exit zero, without stdout on failure: Claude Code keeps the decision in the terminal.
await ClaudeHookProtocol.RunAsync(Console.In, Console.Out);
