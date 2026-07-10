using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows;

[assembly: InternalsVisibleTo("Tests")]

// F-025: this is a Windows-only WPF/WinForms app (low-level hooks, GDI gamma, NVAPI, WMI, Task
// Scheduler). GenerateAssemblyInfo=false suppresses the SDK's auto platform attribute, so declare
// it explicitly here — this clears the CA1416 "supported on 'windows'" baseline without a blanket
// NoWarn, keeping the door open for a warnings-as-errors gate on genuinely new diagnostics.
[assembly: SupportedOSPlatform("windows")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
