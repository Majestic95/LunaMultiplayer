using LmpCommon.Xml;
using System;

namespace Server.Settings.Definition
{
    [Serializable]
    public class IntervalSettingsDefinition
    {
        [XmlComment(Value = "Interval in ms at which the client will send POSITION and FLIGHTSTATE updates of their vessel when other players are NEARBY. " +
                "Decrease it if your clients have good network connection and you plan to do dogfights, although in that case consider using interpolation aswell")]
        public int VesselUpdatesMsInterval { get; set; } = 50;

        [XmlComment(Value = "Interval in ms at which the client will send POSITION and FLIGHTSTATE updates for vessels that are uncontrolled and nearby them. " +
                            "This interval is also applied used to send position updates of HIS OWN vessel when NOBODY is around")]
        public int SecondaryVesselUpdatesMsInterval { get; set; } = 150;

        [XmlComment(Value = "Send/Receive tick clock. Keep this value low but at least above 2ms to avoid extreme CPU usage.")]
        public int SendReceiveThreadTickMs { get; set; } = 5;

        [XmlComment(Value = "Main thread polling in ms. Keep this value low but at least above 2ms to avoid extreme CPU usage.")]
        public int MainTimeTick { get; set; } = 5;

        [XmlComment(Value = "Interval in ms at which internal LMP structures (Subspaces, Vessels, Scenario files, ...) will be backed up to a file")]
        public int BackupIntervalMs { get; set; } = 30000;

        [XmlComment(Value = "Interval in hours at which a timestamped snapshot of the universe is written to a separate <Universe>/_archives/<UTC-timestamp>/ folder. " +
                            "Unlike BackupIntervalMs (which flushes in-memory state to the canonical files), this writes a separate copy that can be restored from. " +
                            "Set to 0 to disable archive backups entirely. Default 24 (one snapshot per day).")]
        public int ArchiveBackupIntervalHours { get; set; } = 24;

        [XmlComment(Value = "Number of timestamped archive snapshots to retain. Older snapshots are deleted automatically after each new archive. Default 14.")]
        public int ArchiveBackupRetentionCount { get; set; } = 14;

        [XmlComment(Value = "Interval to force a garbage collection and reduce the memory usage. Specify this value in minutes. 0 = deactivated.")]
        public int GcMinutesInterval { get; set; } = 15;
    }
}
