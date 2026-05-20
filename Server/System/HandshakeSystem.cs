using LmpCommon.Enums;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.PlayerConnection;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Plugin;
using Server.Server;
using Server.Settings.Structures;
using Server.System.Agency;

namespace Server.System
{
    public partial class HandshakeSystem
    {
        public void HandleHandshakeRequest(ClientStructure client, HandshakeRequestMsgData data)
        {
            var valid = CheckServerFull(client, out var reason);
            valid &= valid && CheckUsernameLength(client, data.PlayerName, out reason);
            valid &= valid && CheckUsernameCharacters(client, data.PlayerName, out reason);
            valid &= valid && CheckPlayerIsAlreadyConnected(client, data.PlayerName, out reason);
            valid &= valid && CheckUsernameIsReserved(client, data.PlayerName, out reason);
            valid &= valid && CheckPlayerIsBanned(client, data.UniqueIdentifier, out reason);

            if (!valid)
            {
                LunaLog.Normal($"Client {data.PlayerName} ({data.UniqueIdentifier}) failed to handshake: {reason}. Disconnecting");
                client.DisconnectClient = true;
                ClientConnectionHandler.DisconnectClient(client, reason);
            }
            else
            {
                client.PlayerName = data.PlayerName;
                client.UniqueIdentifier = data.UniqueIdentifier;
                client.KspVersion = string.IsNullOrWhiteSpace(data.KspVersion) ? "Unknown" : data.KspVersion;
                client.LmpVersion = $"{data.MajorVersion}.{data.MinorVersion}.{data.BuildVersion}";
                client.Authenticated = true;

                // [Stage 5.15a] Register-or-load this player's per-agency career BEFORE
                // the plugin handler fires, so a plugin's OnClientAuthenticated callback
                // can rely on AgencySystem.AgencyByPlayerName[client.PlayerName] being
                // populated. No-op when GameplaySettings.PerAgencyCareer is false — the
                // shared-agency path (Share* systems) remains the authority and AgencySystem
                // stays invisible to plugins.
                AgencySystem.OnPlayerAuthenticated(client.PlayerName);

                LmpPluginHandler.FireOnClientAuthenticated(client);

                LunaLog.Normal($"Client {data.PlayerName} ({data.UniqueIdentifier}) handshake successful, LMP Version: {client.LmpVersion}, KSP Version: {client.KspVersion}");

                HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.HandshookSuccessfully, "success");

                // [Stage 5.15c, gate refined 5.17e-1] Push the per-agency handshake +
                // assigned-agency state on top of the LMP handshake reply, so the client's
                // AgencySystem mirror (Stage 5.18a) lands populated by the time the player
                // reaches the main menu. No-op when AgencySystem.PerAgencyEnabled is false
                // (gate off OR non-Career game mode — spec §10 Q-Mode Career-only sign-off).
                if (AgencySystem.PerAgencyEnabled
                    && AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var assignedAgencyId))
                {
                    AgencySystemSender.SendHandshakeTo(client, assignedAgencyId);
                    if (AgencySystem.Agencies.TryGetValue(assignedAgencyId, out var assignedState))
                    {
                        AgencySystemSender.SendStateTo(client, assignedState);
                        // Stage 5.17d catch-up: persisted per-agency contracts (Active +
                        // Finished, populated by AgencyContractRouter on prior sessions)
                        // are pushed to the reconnecting owner so the 5.18a client mirror
                        // doesn't have to wait for a mutation to learn the inherited state.
                        // No-op when the agency has zero contracts yet.
                        AgencySystemSender.SendContractCatchupTo(client, assignedState);
                        // [Phase 3 Slice B] MKS kolony catch-up: persisted per-agency
                        // KolonyEntries (populated by AgencyKolonyRouter on prior sessions)
                        // are pushed to the reconnecting owner BEFORE any mid-session
                        // mutation arrives — the pre-5.18-series client mirror author needs
                        // the full state at connect time to render KolonizationManager-bound
                        // UI accurately. Sends unconditionally under gate=on (even an empty
                        // dict — see SendKolonyCatchupTo XML on "empty distinguishes from
                        // unsynced").
                        AgencySystemSender.SendKolonyCatchupTo(client, assignedState);
                        // [Phase 3 Slice C] MKS planetary catch-up: same structure as
                        // kolony — persisted per-agency PlanetaryEntries (populated by
                        // AgencyPlanetaryRouter on prior sessions) ship to the reconnecting
                        // owner BEFORE any mid-session mutation. Unconditional under
                        // gate=on so the empty-dict case is observable by the pre-Slice-C
                        // client mirror author.
                        AgencySystemSender.SendPlanetaryCatchupTo(client, assignedState);
                        // [Phase 3 Slice D] MKS orbital catch-up: persisted per-agency
                        // OrbitalTransfers (populated by AgencyOrbitalRouter on prior
                        // sessions OR by the Slice E transferagency-MKS extension
                        // migrating a destination vessel A→B) ship to the reconnecting
                        // owner BEFORE any per-frame ScenarioOrbitalLogistics.Update
                        // cycle runs. Returning player's transfer queue appears in
                        // their MKS UI before they can interact with it. Unconditional
                        // under gate=on so the empty-dict case is observable by the
                        // pre-Slice-D client mirror author. The Slice D Deliver-prefix
                        // (OrbitalLogisticsTransferRequest_DeliverPrefix) runs gate-
                        // state-independent; this catchup only delivers the
                        // owner-only transfer-snapshot under gate=on.
                        AgencySystemSender.SendOrbitalCatchupTo(client, assignedState);
                        // [Phase 4 Slice B] MKS WOLF depot catch-up. Persisted
                        // per-agency WolfDepots (populated by
                        // AgencyWolfDepotRouter on prior sessions) ship to the
                        // reconnecting owner before any mid-session mutation.
                        // Unconditional under gate=on so the empty-dict case is
                        // observable by the pre-Slice-B client mirror author.
                        // Slice D appended SendWolfHopperCatchupTo +
                        // SendWolfTerminalCatchupTo (below); Slice E appended
                        // SendWolfCrewRouteCatchupTo (below).
                        AgencySystemSender.SendWolfDepotCatchupTo(client, assignedState);
                        // [Phase 4 Slice C] MKS WOLF cargo-route catch-up.
                        // Ordering: depots-then-routes mirrors WOLF's own
                        // OnLoad ordering invariant at
                        // ScenarioPersister.cs:288-302 — routes look up their
                        // origin/destination depots via
                        // _depotRegistry.GetDepot during OnLoad; depots must
                        // be present first or the lookup throws
                        // DepotDoesNotExistException. The wire ordering here
                        // protects a future Slice (C+) client mirror that
                        // applies the same OnLoad-equivalent sequence.
                        AgencySystemSender.SendWolfRouteCatchupTo(client, assignedState);
                        // [Phase 4 Slice D] MKS WOLF hopper catch-up.
                        // Hoppers reference one depot — WOLF's OnLoad at
                        // ScenarioPersister.cs:320-329 looks up the depot by
                        // Body+Biome and silently drops the hopper on miss.
                        // Depots ship first; hoppers follow.
                        AgencySystemSender.SendWolfHopperCatchupTo(client, assignedState);
                        // [Phase 4 Slice D] MKS WOLF terminal catch-up.
                        // Terminals do NOT depend on depots in WOLF's OnLoad
                        // (ScenarioPersister.cs:343-353 — TerminalMetadata
                        // carries its own Body+Biome and is loaded without a
                        // depot lookup), so terminal ordering is not load-
                        // bearing. Placing them last preserves a single
                        // "depots-then-children" invariant the Slice E
                        // CrewRoutes catchup will inherit.
                        AgencySystemSender.SendWolfTerminalCatchupTo(client, assignedState);
                        // [Phase 4 Slice E] MKS WOLF crew-route catch-up.
                        // Last in the WOLF catchup chain. CrewRoutes
                        // reference BOTH origin AND destination depot per
                        // CrewRoute.cs:249-250 (_registry.GetDepot throws
                        // DepotDoesNotExistException on FK miss — would kill
                        // the whole WOLF scenario OnLoad). Depots ship
                        // first; CrewRoutes come last so any future client
                        // mirror that applies a WOLF-equivalent OnLoad
                        // sequence has both endpoint depots available
                        // before CrewRoute resolution runs. The Phase 4
                        // catchup chain is now complete: depots → routes →
                        // hoppers → terminals → crewRoutes.
                        AgencySystemSender.SendWolfCrewRouteCatchupTo(client, assignedState);
                    }

                    // [Mod-compat / Path B D2 catch-up] Synchronous connect-
                    // time projection for mod-compat per-agency scenarios.
                    // Without this, a reconnecting owner sees the operator-
                    // seeded baseline blob for up to 30s (until the next SHA
                    // pass triggers a full SendScenarioModules tick).
                    // SendScenariosToClient runs the same per-agency projector
                    // splice that SendScenarioModules does, but targeted at
                    // the requesting client only and only for the named
                    // scenarios. (S3 / FarFutureTechnologyPersistence was
                    // retired 2026-05-19 — orphan file not in compiled FFT.dll;
                    // see docs/mod-compat/near-future-and-far-future.md.)
                    // [Phase 4 Slice E] WOLF_ScenarioModule joins the
                    // connect-time projection list: the per-agency wire
                    // catchups above ship raw entries to AgencyState, but
                    // the projector splice into WOLF_ScenarioModule only
                    // fires on the next ~30s SendScenarioModules tick.
                    // Adding WOLF_ScenarioModule here closes the reconnect
                    // window so a returning owner sees their per-agency
                    // depots/routes/hoppers/terminals/crewRoutes in the
                    // outgoing WOLF blob immediately. Integration-logic
                    // lens [SHOULD FIX] from the Slice E review.
                    ScenarioSystem.SendScenariosToClient(client,
                        "SCANcontroller",       // S2
                        "DMScienceScenario",    // S4
                        "WOLF_ScenarioModule"); // Phase 4 (all 5 family splices)
                }

                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<PlayerConnectionJoinMsgData>();
                msgData.PlayerName = client.PlayerName;
                MessageQueuer.RelayMessage<PlayerConnectionSrvMsg>(client, msgData);

                LunaLog.Debug($"Online Players: {ServerContext.PlayerCount}, connected: {ClientRetriever.GetClients().Length}");
            }
        }
    }
}
