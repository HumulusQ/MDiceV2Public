using System;
using System.ComponentModel.Composition;

namespace ABotTestPanel
{
    /// <summary>
    /// ABot test panel service
    /// </summary>
    [Export]
    public class TestPanelService
    {
        public string Name => "ABot Test Panel";
        public string Version => "1.0.0";

        public void Initialize()
        {
            Console.WriteLine($"Initializing {Name} v{Version}");
        }

        public void RunTests()
        {
            Console.WriteLine("Running ABot tests...");
        }
    }
}
