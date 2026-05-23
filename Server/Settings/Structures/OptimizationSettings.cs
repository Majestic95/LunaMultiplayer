using Server.Settings.Base;
using Server.Settings.Definition;

namespace Server.Settings.Structures
{
    public class OptimizationSettings : SettingsBase<OptimizationSettingsDefinition>
    {
        protected override string Filename => "OptimizationSettings.xml";
    }
}
