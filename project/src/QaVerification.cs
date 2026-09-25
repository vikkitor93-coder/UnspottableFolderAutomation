using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Rewired;
using UnityEngine;

namespace UnspottableExpanded
{
    public sealed partial class Plugin
    {
        // Verification evidence is QA-only and intentionally scoped to already-discovered
        // gameplay actors. No whole-scene scan occurs on the per-frame verification tick.
        private sealed class QaInputEvidenceCounter
        {
            public long Injections;
            public long SyntheticReads;
            public long SyntheticTrueReads;
            public long NeutralizedReads;
        }

        private sealed class QaActorBinding
        {
            public Component Actor;
            public int InstanceId;
            public int RewiredPlayerId = -1;
            public string Resolution = "unresolved";
        }

        private sealed class QaPunchEvidence
        {
            public int RewiredPlayerId;
            public int ActorInstanceId;
            public bool Armed;
            public bool SyntheticConsumed;
            public float SyntheticConsumedAt;
            public string PlayerPunchFsmName = string.Empty;
            public string StateAtArm = string.Empty;
            public string StateAtConsumption = string.Empty;
            public bool ExecutionObserved;
            public float ExecutionAt;
            public string ExecutionState = string.Empty;
            public Component Target;
            public int TargetInstanceId;
            public Vector3 TargetOriginalPosition;
            public Quaternion TargetOriginalRotation;
            public bool TargetOriginalActive;
            public bool TargetPrepared;
            public Vector3 TargetAtExecutionPosition;
            public bool TargetAtExecutionActive;
            public Dictionary<string, string> TargetStatesAtPrepared;
            public Vector3 TargetPreparedPosition;
            public bool TargetPreparedActive;
            public Dictionary<string, string> TargetStatesAtExecution;
            public bool ImpactObserved;
            public string ImpactEvidence = string.Empty;
            public float WeakTargetDisplacement;
        }

        private const float QaPunchExecutionWindowSeconds = 2.0f;
        private const float QaPunchImpactWindowSeconds = 2.0f;

        private readonly Dictionary<string, QaInputEvidenceCounter> _qaInputEvidence =
            new Dictionary<string, QaInputEvidenceCounter>(StringComparer.OrdinalIgnoreCase);
        private QaPunchEvidence _qaPunchEvidence;
        private float _nextQaVerificationTick;

        private string HandleQaBridgeVerificationCommand(string line)
        {
            QaBridgeCommand cmd = new QaBridgeCommand();
            cmd.Line = line;
            _qaCommandQueue.Enqueue(cmd);
            if (!cmd.Done.WaitOne(1500))
                return "{\"ok\":false,\"error\":\"main-thread command timeout\"}";
            return cmd.Response ?? "{\"ok\":false,\"error\":\"empty response\"}";
        }

        private void RecordQaInputInjectionNoLock(int playerId, string action, string kind)
        {
            QaInputEvidenceCounter counter = GetQaInputEvidenceNoLock(playerId, action, kind);
            counter.Injections++;
        }

        private void RecordQaInputConsumptionNoLock(int playerId, string action, string query, bool synthetic, bool returnedActive)
        {
            QaInputEvidenceCounter counter = GetQaInputEvidenceNoLock(playerId, action, query);
            if (synthetic)
            {
                counter.SyntheticReads++;
                if (returnedActive)
                    counter.SyntheticTrueReads++;
            }
            else
            {
                counter.NeutralizedReads++;
            }

            if (!synthetic || !returnedActive ||
                !string.Equals(action, "punch", StringComparison.OrdinalIgnoreCase) ||
                _qaPunchEvidence == null || !_qaPunchEvidence.Armed ||
                _qaPunchEvidence.RewiredPlayerId != playerId)
                return;

            if (!_qaPunchEvidence.SyntheticConsumed)
            {
                _qaPunchEvidence.SyntheticConsumed = true;
                _qaPunchEvidence.SyntheticConsumedAt = Time.unscaledTime;
                Component actor = FindQaActorByRewiredId(playerId, out _);
                string fsmName;
                string state;
                if (actor != null && TryGetPlayerPunchState(actor.gameObject, out fsmName, out state))
                {
                    _qaPunchEvidence.PlayerPunchFsmName = fsmName;
                    _qaPunchEvidence.StateAtConsumption = state ?? string.Empty;
                }
                Logger.LogInfo("QA VERIFY: synthetic punch was consumed by Rewired player " + playerId + "; awaiting PlayerPunch FSM evidence.");
            }
        }

        private QaInputEvidenceCounter GetQaInputEvidenceNoLock(int playerId, string action, string query)
        {
            string key = BuildQaInputEvidenceKey(playerId, action, query);
            QaInputEvidenceCounter counter;
            if (!_qaInputEvidence.TryGetValue(key, out counter))
            {
                counter = new QaInputEvidenceCounter();
                _qaInputEvidence[key] = counter;
            }
            return counter;
        }

        private static string BuildQaInputEvidenceKey(int playerId, string action, string query)
        {
            return playerId.ToString(CultureInfo.InvariantCulture) + "|" +
                (action ?? string.Empty).ToLowerInvariant() + "|" +
                (query ?? string.Empty).ToLowerInvariant();
        }

        private long SumQaInputEvidence(int playerId, string action, int field)
        {
            long value = 0;
            lock (_qaInputLock)
            {
                string prefix = playerId.ToString(CultureInfo.InvariantCulture) + "|" +
                    (action ?? string.Empty).ToLowerInvariant() + "|";
                foreach (KeyValuePair<string, QaInputEvidenceCounter> pair in _qaInputEvidence)
                {
                    if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (field == 0) value += pair.Value.Injections;
                    else if (field == 1) value += pair.Value.SyntheticReads;
                    else if (field == 2) value += pair.Value.SyntheticTrueReads;
                    else value += pair.Value.NeutralizedReads;
                }
            }
            return value;
        }

        private void ResetQaVerificationCounters()
        {
            lock (_qaInputLock)
                _qaInputEvidence.Clear();
        }

        private void UpdateQaVerification()
        {
            if (!_qaGameplayEnabled || _qaPunchEvidence == null || !_qaPunchEvidence.Armed)
                return;

            float now = Time.unscaledTime;
            if (now < _nextQaVerificationTick)
                return;
            _nextQaVerificationTick = now + 0.05f;

            QaPunchEvidence p = _qaPunchEvidence;
            if (!p.SyntheticConsumed)
                return;

            Component actor = FindQaActorByRewiredId(p.RewiredPlayerId, out _);
            if (actor != null && !p.ExecutionObserved &&
                now - p.SyntheticConsumedAt <= QaPunchExecutionWindowSeconds)
            {
                string fsmName;
                string currentState;
                if (TryGetPlayerPunchState(actor.gameObject, out fsmName, out currentState))
                {
                    if (string.IsNullOrEmpty(p.StateAtConsumption))
                        p.StateAtConsumption = currentState ?? string.Empty;
                    if (!string.IsNullOrEmpty(currentState) &&
                        !string.Equals(currentState, p.StateAtConsumption, StringComparison.Ordinal))
                    {
                        p.ExecutionObserved = true;
                        p.ExecutionAt = now;
                        p.PlayerPunchFsmName = fsmName ?? string.Empty;
                        p.ExecutionState = currentState;
                        CaptureTargetAtPunchExecution(p);
                        Logger.LogInfo("QA VERIFY: PlayerPunch FSM transition observed inside the correlation window for Rewired player " + p.RewiredPlayerId + ".");
                    }
                }
            }

            if (p.ExecutionObserved && p.TargetPrepared && !p.ImpactObserved &&
                now - p.ExecutionAt <= QaPunchImpactWindowSeconds)
                UpdateQaPunchImpact(p);
        }

        private void CaptureTargetAtPunchExecution(QaPunchEvidence p)
        {
            if (!p.TargetPrepared)
                return;
            if (p.Target == null)
            {
                p.ImpactObserved = true;
                p.ImpactEvidence = "target disappeared at punch execution";
                return;
            }
            p.TargetAtExecutionPosition = p.Target.transform.position;
            p.TargetAtExecutionActive = p.Target.gameObject.activeInHierarchy;
            Dictionary<string, string> currentStates = CaptureQaFsmStates(p.Target.gameObject);
            if (p.TargetPreparedActive != p.TargetAtExecutionActive)
            {
                p.ImpactObserved = true;
                p.ImpactEvidence = "prepared target active-state changed by punch execution";
            }
            else
            {
                string transition;
                if (TryFindQaReactionTransition(p.TargetStatesAtPrepared, currentStates, out transition))
                {
                    p.ImpactObserved = true;
                    p.ImpactEvidence = transition;
                }
            }
            p.TargetStatesAtExecution = currentStates;
        }

        private void UpdateQaPunchImpact(QaPunchEvidence p)
        {
            if (p.Target == null)
            {
                p.ImpactObserved = true;
                p.ImpactEvidence = "prepared target disappeared after punch execution";
                return;
            }

            bool active = p.Target.gameObject.activeInHierarchy;
            if (active != p.TargetAtExecutionActive)
            {
                p.ImpactObserved = true;
                p.ImpactEvidence = "prepared target active-state changed after punch execution";
                return;
            }

            Dictionary<string, string> current = CaptureQaFsmStates(p.Target.gameObject);
            string transition;
            if (TryFindQaReactionTransition(p.TargetStatesAtExecution, current, out transition))
            {
                p.ImpactObserved = true;
                p.ImpactEvidence = transition;
                return;
            }

            Vector3 a = p.TargetAtExecutionPosition;
            Vector3 b = p.Target.transform.position;
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            float displacement = Mathf.Sqrt(dx * dx + dz * dz);
            if (displacement > p.WeakTargetDisplacement)
                p.WeakTargetDisplacement = displacement;
            // Displacement alone is intentionally not accepted as punch-impact proof because
            // an AI target can walk on its own.
        }

        private string ExecuteQaVerificationCommand(string[] parts)
        {
            if (!_qaEnabled)
                return "{\"ok\":false,\"error\":\"QA mode disabled\"}";

            string op = parts[1].ToUpperInvariant();
            if (op == "CAPABILITIES")
                return BuildQaCapabilitiesJson();
            if (op == "ACTORS")
                return BuildQaActorsJson();
            if (op == "RESETCOUNTERS")
            {
                ResetQaVerificationCounters();
                return "{\"ok\":true,\"op\":\"RESETCOUNTERS\"}";
            }
            if (op == "CLEANUP")
            {
                bool worldRestored = CleanupQaVerificationWorld();
                lock (_qaInputLock) _qaInjected.Clear();
                return worldRestored
                    ? "{\"ok\":true,\"op\":\"CLEANUP\",\"worldRestored\":true}"
                    : "{\"ok\":false,\"op\":\"CLEANUP\",\"worldRestored\":false,\"error\":\"prepared target could not be restored\"}";
            }

            int playerId;
            if (parts.Length < 3 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out playerId))
                return "{\"ok\":false,\"error\":\"Rewired player id required\"}";

            if (op == "SNAPSHOT")
                return BuildQaFocusedSnapshotJson(playerId);
            if (op == "ARMPUNCH")
                return ArmQaPunch(playerId);
            if (op == "PUNCHSTATUS")
                return BuildQaPunchStatusJson(playerId);
            if (op == "PREPAREPUNCH")
            {
                if (parts.Length < 5)
                    return "{\"ok\":false,\"error\":\"direction x/z required\"}";
                float dx;
                float dz;
                if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out dx) ||
                    !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out dz))
                    return "{\"ok\":false,\"error\":\"bad direction\"}";
                float distance = 0.85f;
                if (parts.Length >= 6)
                    float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out distance);
                distance = Mathf.Clamp(distance, 0.55f, 1.25f);
                return PrepareQaPunchTarget(playerId, dx, dz, distance);
            }

            return "{\"ok\":false,\"error\":\"unknown QA verification op\"}";
        }

        private string BuildQaCapabilitiesJson()
        {
            RefreshQaObjects();
            List<QaActorBinding> actors = BuildQaActorBindings();
            int resolved = 0;
            HashSet<int> uniquePlayers = new HashSet<int>();
            bool punchFsm = false;
            for (int i = 0; i < actors.Count; i++)
            {
                if (actors[i].RewiredPlayerId >= 0)
                {
                    resolved++;
                    uniquePlayers.Add(actors[i].RewiredPlayerId);
                    string fsmName;
                    string state;
                    if (TryGetPlayerPunchState(actors[i].Actor.gameObject, out fsmName, out state))
                        punchFsm = true;
                }
            }

            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"ok\":true,\"op\":\"CAPABILITIES\",\"extension\":\"qa-verification-v1\",");
            AppendJsonNumber(sb, "liveActors", actors.Count); sb.Append(',');
            AppendJsonNumber(sb, "resolvedActors", resolved); sb.Append(',');
            AppendJsonBool(sb, "deterministicOwnership", actors.Count > 0 && resolved == actors.Count && uniquePlayers.Count == resolved); sb.Append(',');
            AppendJsonBool(sb, "p2IndependentSupported", uniquePlayers.Count >= 2); sb.Append(',');
            AppendJsonBool(sb, "playerPunchFsmObserved", punchFsm); sb.Append(',');
            AppendJsonBool(sb, "impactTargetAvailable", CountLiveComponents(_qaBots) > 0); sb.Append(',');
            AppendJsonString(sb, "impactRule", "reaction FSM/active-state only; displacement alone never passes");
            sb.Append('}');
            return sb.ToString();
        }

        private string BuildQaActorsJson()
        {
            RefreshQaObjects();
            List<QaActorBinding> actors = BuildQaActorBindings();
            StringBuilder sb = new StringBuilder(768);
            sb.Append("{\"ok\":true,\"op\":\"ACTORS\",\"actors\":[");
            for (int i = 0; i < actors.Count; i++)
            {
                if (i > 0) sb.Append(',');
                QaActorBinding a = actors[i];
                Vector3 p = a.Actor.transform.position;
                sb.Append('{');
                AppendJsonNumber(sb, "instanceId", a.InstanceId); sb.Append(',');
                AppendJsonNumber(sb, "rewiredPlayerId", a.RewiredPlayerId); sb.Append(',');
                AppendJsonString(sb, "resolution", a.Resolution); sb.Append(',');
                AppendJsonFloat(sb, "x", p.x); sb.Append(',');
                AppendJsonFloat(sb, "y", p.y); sb.Append(',');
                AppendJsonFloat(sb, "z", p.z);
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string BuildQaFocusedSnapshotJson(int playerId)
        {
            Component actor = FindQaActorByRewiredId(playerId, out string resolution);
            if (actor == null)
                return "{\"ok\":false,\"error\":\"no deterministically mapped gameplay actor for Rewired player " +
                    playerId.ToString(CultureInfo.InvariantCulture) + "\"}";

            Vector3 p = actor.transform.position;
            string fsmName;
            string fsmState;
            bool hasPunchFsm = TryGetPlayerPunchState(actor.gameObject, out fsmName, out fsmState);
            StringBuilder sb = new StringBuilder(768);
            sb.Append("{\"ok\":true,\"op\":\"SNAPSHOT\",");
            AppendJsonNumber(sb, "rewiredPlayerId", playerId); sb.Append(',');
            AppendJsonNumber(sb, "instanceId", actor.gameObject.GetInstanceID()); sb.Append(',');
            AppendJsonString(sb, "resolution", resolution); sb.Append(',');
            sb.Append("\"position\":{");
            AppendJsonFloat(sb, "x", p.x); sb.Append(','); AppendJsonFloat(sb, "y", p.y); sb.Append(','); AppendJsonFloat(sb, "z", p.z); sb.Append("},");
            AppendJsonBool(sb, "playerPunchFsmObserved", hasPunchFsm); sb.Append(',');
            AppendJsonString(sb, "playerPunchState", fsmState ?? string.Empty); sb.Append(',');
            sb.Append("\"input\":{");
            AppendQaInputEvidenceSummary(sb, playerId, "MoveX"); sb.Append(',');
            AppendQaInputEvidenceSummary(sb, playerId, "MoveY"); sb.Append(',');
            AppendQaInputEvidenceSummary(sb, playerId, "punch"); sb.Append(',');
            AppendQaInputEvidenceSummary(sb, playerId, "run");
            sb.Append("}}");
            return sb.ToString();
        }

        private void AppendQaInputEvidenceSummary(StringBuilder sb, int playerId, string action)
        {
            sb.Append('"').Append(JsonEscape(action)).Append("\":{");
            AppendJsonLong(sb, "injections", SumQaInputEvidence(playerId, action, 0)); sb.Append(',');
            AppendJsonLong(sb, "syntheticReads", SumQaInputEvidence(playerId, action, 1)); sb.Append(',');
            AppendJsonLong(sb, "syntheticActiveReads", SumQaInputEvidence(playerId, action, 2)); sb.Append(',');
            AppendJsonLong(sb, "neutralizedReads", SumQaInputEvidence(playerId, action, 3));
            sb.Append('}');
        }

        private string ArmQaPunch(int playerId)
        {
            Component actor = FindQaActorByRewiredId(playerId, out string resolution);
            if (actor == null)
                return "{\"ok\":false,\"skip\":true,\"reason\":\"player ownership unresolved\"}";

            string fsmName;
            string state;
            if (!TryGetPlayerPunchState(actor.gameObject, out fsmName, out state))
                return "{\"ok\":false,\"skip\":true,\"reason\":\"PlayerPunch FSM not available on mapped gameplay actor\"}";

            QaPunchEvidence old = _qaPunchEvidence;
            Component preparedTarget = old != null && old.TargetPrepared ? old.Target : null;
            int targetId = old != null ? old.TargetInstanceId : 0;
            Vector3 originalPos = old != null ? old.TargetOriginalPosition : Vector3.zero;
            Quaternion originalRot = old != null ? old.TargetOriginalRotation : Quaternion.identity;
            bool originalActive = old != null && old.TargetOriginalActive;
            bool prepared = old != null && old.TargetPrepared;
            Vector3 preparedPos = old != null ? old.TargetPreparedPosition : Vector3.zero;
            bool preparedActive = old != null && old.TargetPreparedActive;
            Dictionary<string, string> preparedStates = old != null ? old.TargetStatesAtPrepared : null;

            _qaPunchEvidence = new QaPunchEvidence
            {
                RewiredPlayerId = playerId,
                ActorInstanceId = actor.gameObject.GetInstanceID(),
                Armed = true,
                PlayerPunchFsmName = fsmName ?? string.Empty,
                StateAtArm = state ?? string.Empty,
                Target = preparedTarget,
                TargetInstanceId = targetId,
                TargetOriginalPosition = originalPos,
                TargetOriginalRotation = originalRot,
                TargetOriginalActive = originalActive,
                TargetPrepared = prepared,
                TargetPreparedPosition = preparedPos,
                TargetPreparedActive = preparedActive,
                TargetStatesAtPrepared = preparedStates
            };

            return "{\"ok\":true,\"op\":\"ARMPUNCH\",\"rewiredPlayerId\":" +
                playerId.ToString(CultureInfo.InvariantCulture) + ",\"resolution\":\"" + JsonEscape(resolution) +
                "\",\"playerPunchState\":\"" + JsonEscape(state ?? string.Empty) + "\",\"targetPrepared\":" +
                (prepared ? "true" : "false") + "}";
        }

        private string PrepareQaPunchTarget(int playerId, float dx, float dz, float distance)
        {
            if (!CleanupQaVerificationWorld())
                return "{\"ok\":false,\"error\":\"previous prepared target could not be restored\"}";
            RefreshQaObjects();
            Component actor = FindQaActorByRewiredId(playerId, out _);
            if (actor == null)
                return "{\"ok\":false,\"skip\":true,\"reason\":\"player ownership unresolved\"}";

            float mag = Mathf.Sqrt(dx * dx + dz * dz);
            if (mag < 0.05f)
                return "{\"ok\":false,\"skip\":true,\"reason\":\"movement direction unavailable; punch target was not repositioned\"}";
            dx /= mag;
            dz /= mag;

            Component target = FindNearestQaBot(actor.transform.position);
            if (target == null)
                return "{\"ok\":false,\"skip\":true,\"reason\":\"no live bot target available\"}";

            Vector3 original = target.transform.position;
            Quaternion originalRotation = target.transform.rotation;
            bool originalActive = target.gameObject.activeSelf;
            Vector3 actorPos = actor.transform.position;
            Vector3 prepared = new Vector3(actorPos.x + dx * distance, original.y, actorPos.z + dz * distance);
            target.transform.position = prepared;

            _qaPunchEvidence = new QaPunchEvidence
            {
                RewiredPlayerId = playerId,
                ActorInstanceId = actor.gameObject.GetInstanceID(),
                Target = target,
                TargetInstanceId = target.gameObject.GetInstanceID(),
                TargetOriginalPosition = original,
                TargetOriginalRotation = originalRotation,
                TargetOriginalActive = originalActive,
                TargetPrepared = true,
                TargetPreparedPosition = prepared,
                TargetPreparedActive = target.gameObject.activeInHierarchy,
                TargetStatesAtPrepared = CaptureQaFsmStates(target.gameObject)
            };

            StringBuilder sb = new StringBuilder(384);
            sb.Append("{\"ok\":true,\"op\":\"PREPAREPUNCH\",");
            AppendJsonNumber(sb, "rewiredPlayerId", playerId); sb.Append(',');
            AppendJsonNumber(sb, "targetInstanceId", target.gameObject.GetInstanceID()); sb.Append(',');
            AppendJsonFloat(sb, "distance", distance); sb.Append(',');
            AppendJsonString(sb, "note", "QA-only target relocation; QA CLEANUP restores transform");
            sb.Append('}');
            return sb.ToString();
        }

        private string BuildQaPunchStatusJson(int playerId)
        {
            QaPunchEvidence p = _qaPunchEvidence;
            if (p == null || p.RewiredPlayerId != playerId)
                return "{\"ok\":false,\"skip\":true,\"reason\":\"punch monitor not armed for requested player\"}";

            StringBuilder sb = new StringBuilder(640);
            sb.Append("{\"ok\":true,\"op\":\"PUNCHSTATUS\",");
            AppendJsonNumber(sb, "rewiredPlayerId", playerId); sb.Append(',');
            AppendJsonBool(sb, "armed", p.Armed); sb.Append(',');
            AppendJsonBool(sb, "syntheticConsumed", p.SyntheticConsumed); sb.Append(',');
            AppendJsonString(sb, "stateAtConsumption", p.StateAtConsumption ?? string.Empty); sb.Append(',');
            AppendJsonBool(sb, "executionObserved", p.ExecutionObserved); sb.Append(',');
            AppendJsonBool(sb, "executionWindowExpired", p.SyntheticConsumed && !p.ExecutionObserved &&
                Time.unscaledTime - p.SyntheticConsumedAt > QaPunchExecutionWindowSeconds); sb.Append(',');
            AppendJsonString(sb, "executionState", p.ExecutionState ?? string.Empty); sb.Append(',');
            AppendJsonBool(sb, "targetPrepared", p.TargetPrepared); sb.Append(',');
            AppendJsonBool(sb, "impactObserved", p.ImpactObserved); sb.Append(',');
            AppendJsonBool(sb, "impactWindowExpired", p.ExecutionObserved && p.TargetPrepared && !p.ImpactObserved &&
                Time.unscaledTime - p.ExecutionAt > QaPunchImpactWindowSeconds); sb.Append(',');
            AppendJsonString(sb, "impactEvidence", p.ImpactEvidence ?? string.Empty); sb.Append(',');
            AppendJsonFloat(sb, "weakTargetDisplacement", p.WeakTargetDisplacement);
            sb.Append('}');
            return sb.ToString();
        }

        private bool CleanupQaVerificationWorld()
        {
            QaPunchEvidence p = _qaPunchEvidence;
            bool restored = true;
            if (p != null && p.TargetPrepared)
            {
                if (p.Target == null)
                {
                    restored = false;
                }
                else
                {
                    try
                    {
                        p.Target.transform.position = p.TargetOriginalPosition;
                        p.Target.transform.rotation = p.TargetOriginalRotation;
                        p.Target.gameObject.SetActive(p.TargetOriginalActive);
                    }
                    catch
                    {
                        restored = false;
                    }
                }
            }
            _qaPunchEvidence = null;
            return restored;
        }

        private Component FindNearestQaBot(Vector3 origin)
        {
            Component best = null;
            float bestSqr = float.MaxValue;
            if (_qaBots == null)
                return null;
            for (int i = 0; i < _qaBots.Length; i++)
            {
                Component c = _qaBots[i] as Component;
                if (c == null || !c.gameObject.activeInHierarchy)
                    continue;
                Vector3 p = c.transform.position;
                float dx = p.x - origin.x;
                float dz = p.z - origin.z;
                float sqr = dx * dx + dz * dz;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = c;
                }
            }
            return best;
        }

        private Component FindQaActorByRewiredId(int playerId, out string resolution)
        {
            resolution = "unresolved";
            List<QaActorBinding> actors = BuildQaActorBindings();
            Component found = null;
            for (int i = 0; i < actors.Count; i++)
            {
                if (actors[i].RewiredPlayerId != playerId)
                    continue;
                if (found != null)
                {
                    resolution = "ambiguous duplicate owner";
                    return null;
                }
                found = actors[i].Actor;
                resolution = actors[i].Resolution;
            }
            return found;
        }

        private List<QaActorBinding> BuildQaActorBindings()
        {
            List<QaActorBinding> result = new List<QaActorBinding>();
            if (_qaPlayers == null || _qaPlayers.Length == 0)
                RefreshQaObjects();

            List<Component> live = new List<Component>();
            for (int i = 0; i < _qaPlayers.Length; i++)
            {
                Component c = _qaPlayers[i] as Component;
                if (c != null)
                    live.Add(c);
            }
            live.Sort((a, b) => a.gameObject.GetInstanceID().CompareTo(b.gameObject.GetInstanceID()));

            int singlePlayingId = -1;
            if (live.Count == 1 && ReInput.isReady)
            {
                int playingCount = 0;
                for (int i = 0; i < ReInput.players.playerCount; i++)
                {
                    Player rp = ReInput.players.GetPlayer(i);
                    if (rp != null && rp.isPlaying)
                    {
                        playingCount++;
                        singlePlayingId = rp.id;
                    }
                }
                if (playingCount != 1)
                    singlePlayingId = -1;
            }

            for (int i = 0; i < live.Count; i++)
            {
                Component actor = live[i];
                QaActorBinding binding = new QaActorBinding
                {
                    Actor = actor,
                    InstanceId = actor.gameObject.GetInstanceID()
                };

                HashSet<int> directIds = FindDirectRewiredReferences(actor.gameObject);
                if (directIds.Count == 1)
                {
                    foreach (int id in directIds) binding.RewiredPlayerId = id;
                    binding.Resolution = "direct Rewired.Player field reference";
                }
                else if (directIds.Count > 1)
                {
                    binding.Resolution = "ambiguous: multiple Rewired.Player field references";
                }
                else if (singlePlayingId >= 0)
                {
                    binding.RewiredPlayerId = singlePlayingId;
                    binding.Resolution = "unique live gameplay actor + unique isPlaying Rewired player";
                }
                else
                {
                    binding.Resolution = "unresolved: no unique direct Rewired.Player ownership evidence";
                }
                result.Add(binding);
            }
            return result;
        }

        private static HashSet<int> FindDirectRewiredReferences(GameObject go)
        {
            HashSet<int> ids = new HashSet<int>();
            if (go == null)
                return ids;

            MonoBehaviour[] components = go.GetComponentsInChildren<MonoBehaviour>(true);
            int inspectedFields = 0;
            for (int i = 0; i < components.Length && inspectedFields < 128; i++)
            {
                MonoBehaviour mb = components[i];
                if (mb == null)
                    continue;
                FieldInfo[] fields;
                try
                {
                    fields = mb.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                catch
                {
                    continue;
                }
                for (int f = 0; f < fields.Length && inspectedFields < 128; f++, inspectedFields++)
                {
                    FieldInfo fi = fields[f];
                    if (!typeof(Player).IsAssignableFrom(fi.FieldType))
                        continue;
                    try
                    {
                        Player rp = fi.GetValue(mb) as Player;
                        if (rp != null)
                            ids.Add(rp.id);
                    }
                    catch { }
                }
            }
            return ids;
        }

        private static bool TryGetPlayerPunchState(GameObject go, out string fsmName, out string state)
        {
            fsmName = string.Empty;
            state = string.Empty;
            if (go == null)
                return false;
            MonoBehaviour[] components = go.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < components.Length; i++)
            {
                MonoBehaviour mb = components[i];
                if (mb == null || !string.Equals(mb.GetType().Name, "PlayMakerFSM", StringComparison.Ordinal))
                    continue;
                string name = SafeReflectString(mb, "FsmName");
                if (!string.Equals(name, "PlayerPunch", StringComparison.OrdinalIgnoreCase))
                    continue;
                string active = SafeReflectString(mb, "ActiveStateName");
                if (string.IsNullOrEmpty(active))
                {
                    object fsm = SafeReflectValue(mb, "Fsm");
                    if (fsm != null)
                        active = SafeReflectString(fsm, "ActiveStateName");
                }
                fsmName = name;
                state = active ?? string.Empty;
                return true;
            }
            return false;
        }

        private static Dictionary<string, string> CaptureQaFsmStates(GameObject go)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (go == null)
                return result;
            MonoBehaviour[] components = go.GetComponentsInChildren<MonoBehaviour>(true);
            int ordinal = 0;
            for (int i = 0; i < components.Length && ordinal < 32; i++)
            {
                MonoBehaviour mb = components[i];
                if (mb == null || !string.Equals(mb.GetType().Name, "PlayMakerFSM", StringComparison.Ordinal))
                    continue;
                string name = SafeReflectString(mb, "FsmName");
                string state = SafeReflectString(mb, "ActiveStateName");
                if (string.IsNullOrEmpty(state))
                {
                    object fsm = SafeReflectValue(mb, "Fsm");
                    if (fsm != null)
                        state = SafeReflectString(fsm, "ActiveStateName");
                }
                string key = (name ?? string.Empty) + "#" + ordinal.ToString(CultureInfo.InvariantCulture);
                result[key] = state ?? string.Empty;
                ordinal++;
            }
            return result;
        }

        private static bool TryFindQaReactionTransition(Dictionary<string, string> before,
            Dictionary<string, string> after, out string detail)
        {
            detail = string.Empty;
            if (before == null || after == null)
                return false;
            foreach (KeyValuePair<string, string> pair in after)
            {
                string oldState;
                if (!before.TryGetValue(pair.Key, out oldState))
                    continue;
                if (string.Equals(oldState, pair.Value, StringComparison.Ordinal))
                    continue;
                if (ContainsQaReactionWord(pair.Key) || ContainsQaReactionWord(pair.Value))
                {
                    detail = "target reaction FSM transitioned: " + pair.Key + " " + oldState + " -> " + pair.Value;
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsQaReactionWord(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            // Split camelCase/PascalCase and punctuation before testing whole tokens.
            // This avoids false positives such as "Countdown" -> "down" or "White" -> "hit".
            string spaced = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1 $2");
            string[] tokens = Regex.Split(spaced.ToLowerInvariant(), "[^a-z0-9]+");
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i];
                if (t == "hit" || t == "punch" || t == "punched" || t == "punching" ||
                    t == "knock" || t == "knocked" || t == "knockdown" ||
                    t == "fall" || t == "falling" || t == "fallen" ||
                    t == "stun" || t == "stunned" || t == "dead" ||
                    t == "death" || t == "down")
                    return true;
            }
            return false;
        }
    }
}
