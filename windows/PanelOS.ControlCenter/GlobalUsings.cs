// Windows Forms is referenced for the tray icon only, and the SDK's implicit
// usings for a Windows Forms project both pull System.Windows.Forms/
// System.Drawing in and drop System.IO. That makes several names ambiguous
// with their WPF counterparts, so the ones this application uses are pinned
// here: this is a WPF app, and Brush, Application, MessageBox, UserControl and
// OpenFileDialog always mean the WPF/Win32 type. The tray code reaches for
// Windows Forms through an explicit alias instead.
global using System.IO;
global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Clipboard = System.Windows.Clipboard;
global using MessageBox = System.Windows.MessageBox;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using UserControl = System.Windows.Controls.UserControl;
