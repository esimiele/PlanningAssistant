using System.Windows;
using System.Reflection;
using System.Runtime.CompilerServices;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

// TODO: Replace the following version attributes by creating AssemblyInfo.cs. You can do this in the properties of the Visual Studio project.
[assembly: AssemblyVersion("1.0.*")]
//[assembly: AssemblyFileVersion("1.0.0.1")]
[assembly: AssemblyInformationalVersion("1.0")]

// TODO: Uncomment the following line if the script requires write access.
[assembly: ESAPIScript(IsWriteable = true)]

namespace VMS.TPS
{
    public class Script
    {
        public Script()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext context/*, System.Windows.Window w, ScriptEnvironment environment*/)
        {
            Reflexion_assistant.UI ui = new Reflexion_assistant.UI(context);
            Window wi = new Window();
            wi.SizeToContent = SizeToContent.WidthAndHeight;
            wi.Content = ui;
            wi.ShowDialog();
        }
    }
}
