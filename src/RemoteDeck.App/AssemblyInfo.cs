using System.Resources;
using System.Windows;

// The neutral Strings.resx is English, so an English UI needs no satellite assembly: the resource
// manager stops looking as soon as it sees this attribute (spec §9). Every other culture falls back
// to it, and fr-FR finds its own satellite first.
[assembly: NeutralResourcesLanguage("en")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
