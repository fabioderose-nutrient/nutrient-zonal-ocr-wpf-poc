using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace ZonalOcrViewer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            AppDomain.CurrentDomain.AssemblyLoad += CurrentDomain_AssemblyLoad;
            base.OnStartup(e);
        }

        private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            Debug.WriteLine("AssemblyResolve request for: " + args.Name);
            // return null to let normal resolution continue; we only log here.
            return null;
        }

        private void CurrentDomain_AssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            Debug.WriteLine("Loaded: " + args.LoadedAssembly.FullName);
        }
    }
}
