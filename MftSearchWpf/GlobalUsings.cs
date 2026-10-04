// Global using aliases to resolve WPF vs WinForms naming collisions that arise
// because both UseWPF and UseWindowsForms are enabled in the project.
// All existing code that writes 'Application', 'MessageBox', or 'Clipboard'
// without a namespace prefix will resolve to the WPF types, which is correct.
global using Application = System.Windows.Application;
global using MessageBox   = System.Windows.MessageBox;
global using Clipboard    = System.Windows.Clipboard;
