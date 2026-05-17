using LmpClient.Base;
using LmpClient.Extensions;
using LmpClient.Systems.VesselProtoSys;
using LmpCommon.Locks;

namespace LmpClient.Systems.AsteroidComet
{
    public class AsteroidCometEvents : SubSystem<AsteroidCometSystem>
    {
        /// <summary>
        /// Try to get asteroid lock
        /// </summary>
        public void LockReleased(LockDefinition lockDefinition)
        {
            if (lockDefinition.Type == LockType.AsteroidComet)
            {
                System.TryGetCometAsteroidLock();
            }
        }

        /// <summary>
        /// Try to get asteroid lock when loading a level
        /// </summary>
        public void LevelLoaded(GameScenes data)
        {
            System.TryGetCometAsteroidLock();
        }

        public void StartTrackingCometOrAsteroid(Vessel potato)
        {
            LunaLog.Log($"Started to track comet/asteroid {potato.id}");
            VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(potato, true, reason: "started tracking comet/asteroid");
        }

        public void StopTrackingCometOrAsteroid(Vessel potato)
        {
            LunaLog.Log($"Stopped to track comet/asteroid {potato.id}");
            VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(potato, true, reason: "stopped tracking comet/asteroid");
        }

        /// <summary>
        /// This event is called when accepting a recoverasset contract or when an asteroid spawns
        /// </summary>
        public void NewVesselCreated(Vessel vessel)
        {
            if (vessel.IsCometOrAsteroid())
                VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(vessel, true, reason: "new vessel: comet/asteroid spawned");
        }
    }
}
