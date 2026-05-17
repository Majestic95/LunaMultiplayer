namespace LmpCommon.Message.Types
{
    public enum ShareProgressMessageType
    {
        FundsUpdate = 0,
        ScienceUpdate = 1,
        ScienceSubjectUpdate = 2,
        ReputationUpdate = 3,
        TechnologyUpdate = 4,
        ContractsUpdate = 5,
        AchievementsUpdate = 6,
        StrategyUpdate = 7,
        FacilityUpgrade = 8,
        PartPurchase = 9,
        ExperimentalPart = 10,
        // [fix:BUG-025] Server-to-client rejection of a duplicate tech purchase.
        // The sender deducted science locally before broadcasting; the server saw
        // the tech was already unlocked and tells the sender to refund.
        TechnologyRejected = 11,
    }
}
