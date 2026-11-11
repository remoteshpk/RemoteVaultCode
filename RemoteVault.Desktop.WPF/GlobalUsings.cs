// WPF vs WinForms type disambiguation
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using MessageBoxResult = System.Windows.MessageBoxResult;
global using RadioButton = System.Windows.Controls.RadioButton;
global using CheckBox = System.Windows.Controls.CheckBox;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

// WPF media types vs System.Drawing
global using Brush = System.Windows.Media.Brush;
global using SolidColorBrush = System.Windows.Media.SolidColorBrush;
global using Color = System.Windows.Media.Color;

// System.IO.Path vs System.Windows.Shapes.Path
global using Path = System.IO.Path;
global using File = System.IO.File;
global using Directory = System.IO.Directory;
global using SearchOption = System.IO.SearchOption;
