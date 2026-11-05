using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppActiveRagdoll;
using Il2CppHexabodyVR.PlayerController;
using Il2CppHurricaneVR.Framework.Core.Player;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(LimbTear.Core), "LimbTear", "1.0.0", "geom2543")]
[assembly: MelonGame("Kubunautilus", "BrutalistickVR")]

namespace LimbTear
{
    // Hold an NPC body part and pull hard enough: the joint holding it to the body gives way and the part
    // comes off, using the game's own dismember damage and artery bleeding.
    public class Core : MelonMod
    {
        private MelonPreferences_Entry<bool> _enabled;
        private MelonPreferences_Entry<float> _force;
        private MelonPreferences_Entry<float> _hold;
        private MelonPreferences_Entry<float> _damage;
        private MelonPreferences_Entry<bool> _allowHead;
        private MelonPreferences_Entry<bool> _allowTorso;
        private MelonPreferences_Entry<bool> _verbose;
        private MelonPreferences_Entry<bool> _gore;
        private MelonPreferences_Entry<bool> _split;
        private MelonPreferences_Entry<float> _splitStretch;
        private MelonPreferences_Entry<float> _splitHold;

        // two-hand split state
        private IntPtr _splitPart;
        private float _splitBase, _splitOver, _splitMax;
        private MelonPreferences_Entry<int> _bleedSources;

        private class Grip
        {
            public IntPtr Limb;
            public string Name;
            public float Over;    // seconds above the tear force
            public float Max;     // strongest pull seen (for the log)
        }

        private readonly Dictionary<IntPtr, Grip> _grips = new();   // per player hand
        private readonly List<ConfigurableJoint> _torn = new();       // freed joints, kept limp every physics step
        private HexaBodyPlayer4 _body;
        private Transform _rig;

        public override void OnInitializeMelon()
        {
            var cat = MelonPreferences.CreateCategory("LimbTear");
            _enabled = cat.CreateEntry("Enabled", true, "Enabled");
            _force = cat.CreateEntry("TearForce", 1800f, "Tear force (N)",
                "Pull on a body part joint needed to rip it off. Lower = easier");
            _hold = cat.CreateEntry("HoldTime", 0.2f, "Hold time (s)", "How long the pull must stay above the tear force");
            _damage = cat.CreateEntry("DismemberDamage", 60f, "Damage on tear");
            _allowHead = cat.CreateEntry("AllowHead", true, "Heads can be ripped off");
            _allowTorso = cat.CreateEntry("TorsoCanTear", true, "Torso can be ripped apart (chest, neck, pelvis)");
            _gore = cat.CreateEntry("Gore", true, "Blood, splatter and sound on tear");
            _bleedSources = cat.CreateEntry("BleedSources", 3, "Bleeding spots per wound side");
            _split = cat.CreateEntry("SplitWithTwoHands", true, "Rip one body part in half with two hands");
            _splitStretch = cat.CreateEntry("SplitPull", 0.18f, "Split pull (m)",
                "How far apart your controllers must go past the grab (both hands on one part)");
            _splitHold = cat.CreateEntry("SplitHoldTime", 0.25f, "Split hold time (s)");
            _verbose = cat.CreateEntry("Verbose", true, "Log the strongest pull of every grab (to tune TearForce)");
            LoggerInstance.Msg("Loaded. Grab an NPC limb and pull hard to rip it off.");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            _grips.Clear();
            _torn.Clear();
            _body = null;
        }

        private bool FindBody()
        {
            if (_body != null && _body.WasCollected == false && _body) return true;
            _body = UnityEngine.Object.FindObjectOfType<HexaBodyPlayer4>();
            if (_body == null) return false;
            _rig = _body.transform.parent != null ? _body.transform.parent : _body.transform;
            return true;
        }

        public override void OnFixedUpdate()
        {
            // The game re-applies drive springs to all NPC joints (ragdoll, death...). Keep ours at zero.
            for (int i = _torn.Count - 1; i >= 0; i--)
            {
                var j = _torn[i];
                if (j == null || j.WasCollected || !j) { _torn.RemoveAt(i); continue; }
                Limp(j);
            }
        }

        public override void OnUpdate()
        {
            if (!_enabled.Value || !FindBody()) return;
            foreach (var h in _rig.GetComponentsInChildren<HVRHexaBodyHands>(false))
            {
                try { UpdateHand(h); }
                catch (Exception e) { LoggerInstance.Warning("LimbTear: " + e.Message); }
            }
            try { UpdateSplit(); }
            catch (Exception e) { LoggerInstance.Warning("Split: " + e.Message); }
        }

        // ---------- Two hands on one part: rip it in half ----------
        // A rigid part can't stretch, so when both hands hold it and you pull the controllers apart, the
        // physics hands stay put and the controllers drift away from them. That drift is the effort.
        private static Rigidbody HeldPart(HVRHexaBodyHands h, out StickHealth npc)
        {
            npc = null;
            var g = h != null ? h.Grabber : null;
            var t = g != null && g.IsGrabbing ? g.GrabbedTarget : null;
            if (t == null) return null;
            npc = t.GetComponentInParent<StickHealth>();
            if (npc == null) return null;
            return t.Rigidbody != null ? t.Rigidbody : t.GetComponentInParent<Rigidbody>();
        }

        private void UpdateSplit()
        {
            if (!_split.Value) return;
            HVRHexaBodyHands l = null, r = null;
            foreach (var h in _rig.GetComponentsInChildren<HVRHexaBodyHands>(false))
            {
                if (h == null || h.Grabber == null) continue;
                if (h.Grabber.IsLeftHand) l = h; else r = h;
            }
            var pl = HeldPart(l, out var npc);
            var pr = HeldPart(r, out _);
            if (pl == null || pr == null || pl.Pointer != pr.Pointer || _body.LeftController == null || _body.RightController == null)
            {
                if (_splitPart != IntPtr.Zero && _verbose.Value && _splitMax > 0.01f)
                    LoggerInstance.Msg($"Let go (two hands): strongest pull-apart {_splitMax:F2} m (split at {_splitStretch.Value:F2} m)");
                _splitPart = IntPtr.Zero;
                return;
            }

            float scale = Mathf.Max(0.01f, _rig.lossyScale.x);
            Vector3 hl = l.transform.position, hr = r.transform.position;
            float stretch = (Vector3.Distance(_body.LeftController.position, _body.RightController.position) - Vector3.Distance(hl, hr)) / scale;

            if (_splitPart != pl.Pointer)
            {
                _splitPart = pl.Pointer;
                _splitBase = stretch;
                _splitOver = 0f;
                _splitMax = 0f;
                return;
            }

            float pull = stretch - _splitBase;
            _splitMax = Mathf.Max(_splitMax, pull);
            _splitOver = pull >= _splitStretch.Value ? _splitOver + Time.deltaTime : Mathf.Max(0f, _splitOver - Time.deltaTime);
            if (_splitOver < _splitHold.Value) return;

            _splitPart = IntPtr.Zero;
            Split(npc, pl, l, r, hl, hr, pull);
        }

        private void Split(StickHealth npc, Rigidbody part, HVRHexaBodyHands l, HVRHexaBodyHands r, Vector3 hl, Vector3 hr, float pull)
        {
            var cb = part.GetComponent<ColliderBridge>();
            if (cb == null) { LoggerInstance.Warning($"'{part.name}' can't be split (no ColliderBridge)"); return; }

            Vector3 axis = (hr - hl).normalized;
            Vector3 mid = (hl + hr) * 0.5f;
            var plane = new Plane(axis, mid);

            if (_gore.Value)
            {
                try { GoreFx(npc, part, mid, axis); GoreFx(npc, part, mid, -axis); } catch { }
            }

            // Same call a blade makes: the game cuts the mesh, moves joints to the right half and paints the cut.
            var lrb = l.RigidBody;
            var cols = lrb != null ? lrb.GetComponentsInChildren<Collider>() : null;
            var hand = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider>(cols != null ? cols.Length : 0);
            for (int i = 0; cols != null && i < cols.Length; i++) hand[i] = cols[i];
            cb.SliceColliderBridge(plane, mid, axis * 2f, lrb, hand, r.RigidBody, 0.3f);

            try { npc.DismemberDetected(_damage.Value, StickHealth.DamageType.Dismember, cb.damageRegion, cb); }
            catch (Exception e) { LoggerInstance.Warning("DismemberDetected: " + e.Message); }

            LoggerInstance.Msg($"Ripped '{part.name}' in half (pulled apart {pull:F2} m)");
        }

        private void UpdateHand(HVRHexaBodyHands h)
        {
            if (h == null) return;
            var g = h.Grabber;
            var target = g != null && g.IsGrabbing ? g.GrabbedTarget : null;
            Rigidbody limb = null;
            StickHealth npc = null;
            if (target != null)
            {
                limb = target.Rigidbody != null ? target.Rigidbody : target.GetComponentInParent<Rigidbody>();
                npc = target.GetComponentInParent<StickHealth>();
            }

            _grips.TryGetValue(h.Pointer, out var grip);
            if (limb == null || npc == null)
            {
                if (grip != null) { LogGrip(grip); _grips.Remove(h.Pointer); }
                return;
            }
            if (grip == null || grip.Limb != limb.Pointer)
            {
                if (grip != null) LogGrip(grip);
                grip = new Grip { Limb = limb.Pointer, Name = limb.name };
                _grips[h.Pointer] = grip;
            }

            if (!Allowed(limb.name)) return;
            var cuts = FindCuts(npc, limb);
            if (cuts.Count == 0) return;

            float f = 0f;
            foreach (var j in cuts) f = Mathf.Max(f, j.currentForce.magnitude);
            grip.Max = Mathf.Max(grip.Max, f);
            grip.Over = f >= _force.Value ? grip.Over + Time.deltaTime : Mathf.Max(0f, grip.Over - Time.deltaTime);

            if (grip.Over >= _hold.Value)
            {
                Tear(npc, limb, cuts, f);
                grip.Over = 0f;
            }
        }

        private bool Allowed(string n)
        {
            if (n.StartsWith("Head") || n == "Jaw") return _allowHead.Value;
            if (n.Contains("Chest") || n.Contains("Neck") || n.Contains("Spine") || n.Contains("Pelvis")) return _allowTorso.Value;
            return true;
        }

        // Joints between the limb and whatever still links it to the pelvis. Joints to parts that hang only
        // from this limb (forearm under an upper arm) are kept, so they come off together with it.
        private static List<ConfigurableJoint> FindCuts(StickHealth npc, Rigidbody limb)
        {
            var result = new List<ConfigurableJoint>();
            var joints = npc.GetComponentsInChildren<ConfigurableJoint>(false);
            Rigidbody root = null;
            var edges = new List<(IntPtr a, IntPtr b, ConfigurableJoint j)>();
            foreach (var j in joints)
            {
                if (j == null) continue;
                var a = j.GetComponent<Rigidbody>();
                var b = j.connectedBody;
                if (a == null || b == null) continue;
                if (IsFree(j)) continue; // already torn
                edges.Add((a.Pointer, b.Pointer, j));
                if (root == null && a.name == "Pelvis") root = a;
                if (root == null && b.name == "Pelvis") root = b;
            }
            // Which side stays: the pelvis side while the part is still on the body. If the part is the
            // pelvis itself, or belongs to a piece that is already off (a torn leg), it comes off the
            // bigger of the neighbouring pieces instead.
            var mine = Reach(edges, limb.Pointer, IntPtr.Zero);
            IntPtr anchor = IntPtr.Zero;
            if (root != null && root.Pointer != limb.Pointer && mine.Contains(root.Pointer))
                anchor = root.Pointer;
            else
            {
                float best = 0f;
                foreach (var e in edges)
                {
                    IntPtr o = e.a == limb.Pointer ? e.b : e.b == limb.Pointer ? e.a : IntPtr.Zero;
                    if (o == IntPtr.Zero) continue;
                    // tie (knee between thigh and foot): the skeleton parent (our joint's other end) stays
                    float size = Reach(edges, o, limb.Pointer).Count + (e.a == limb.Pointer ? 0.5f : 0f);
                    if (size > best) { best = size; anchor = o; }
                }
            }
            if (anchor == IntPtr.Zero) return result;
            var seen = Reach(edges, anchor, limb.Pointer);
            foreach (var e in edges)
            {
                if (e.a == limb.Pointer && seen.Contains(e.b)) result.Add(e.j);
                else if (e.b == limb.Pointer && seen.Contains(e.a)) result.Add(e.j);
            }
            return result;
        }

        // Bodies connected to start, not passing through skip.
        private static HashSet<IntPtr> Reach(List<(IntPtr a, IntPtr b, ConfigurableJoint j)> edges, IntPtr start, IntPtr skip)
        {
            var seen = new HashSet<IntPtr> { start };
            var queue = new Queue<IntPtr>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                foreach (var e in edges)
                {
                    IntPtr o = e.a == n ? e.b : e.b == n ? e.a : IntPtr.Zero;
                    if (o == IntPtr.Zero || o == skip || seen.Contains(o)) continue;
                    seen.Add(o);
                    queue.Enqueue(o);
                }
            }
            return seen;
        }

        private void Tear(StickHealth npc, Rigidbody limb, List<ConfigurableJoint> cuts, float force)
        {
            Vector3 pos = limb.worldCenterOfMass;
            foreach (var j in cuts)
            {
                pos = j.transform.TransformPoint(j.anchor);
                var other = j.GetComponent<Rigidbody>().Pointer == limb.Pointer ? j.connectedBody : j.GetComponent<Rigidbody>();
                Vector3 dir = (limb.worldCenterOfMass - pos).normalized;

                // Don't destroy the joint: game scripts hold references to it. Free every axis instead.
                j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Free;
                j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Free;
                j.projectionMode = JointProjectionMode.None;
                Limp(j);
                _torn.Add(j);
                StopDriving(j.gameObject);

                if (_gore.Value)
                {
                    Gore(npc, limb, pos, dir);
                    if (other != null) Gore(npc, other, pos, -dir);
                }
            }

            try
            {
                var cb = limb.GetComponent<ColliderBridge>();
                npc.DismemberDetected(_damage.Value, StickHealth.DamageType.Dismember, cb != null ? cb.damageRegion : limb.name, cb);
            }
            catch (Exception e) { LoggerInstance.Warning("DismemberDetected: " + e.Message); }

            LoggerInstance.Msg($"Ripped off '{limb.name}' ({force:F0} N, {cuts.Count} joint{(cuts.Count == 1 ? "" : "s")})");
        }

        // Same pieces the game uses for cuts: BleedSource prefab (dripping + pooling on the floor),
        // the body part's own blood particle systems and the flesh slash sound.
        private void GoreFx(StickHealth npc, Rigidbody part, Vector3 pos, Vector3 dir) => Gore(npc, part, pos, dir, false);

        private void Gore(StickHealth npc, Rigidbody part, Vector3 pos, Vector3 dir, bool bleedSources = true)
        {
            ColliderBridge cb = null;
            try { cb = part.GetComponent<ColliderBridge>(); } catch { }

            try
            {
                GameObject prefab = cb != null ? cb.bleedSourcePrefab : null;
                if (prefab == null && DefaultReferences.Instance != null) prefab = DefaultReferences.Instance.bleedSourcePrefab;
                if (prefab != null && bleedSources)
                {
                    for (int i = 0; i < Mathf.Max(0, _bleedSources.Value); i++)
                    {
                        Vector3 p = pos + UnityEngine.Random.insideUnitSphere * 0.03f;
                        var go = UnityEngine.Object.Instantiate(prefab, p, Quaternion.LookRotation(dir), part.transform);
                        go.SetActive(true);
                    }
                }
            }
            catch (Exception e) { LoggerInstance.Warning("Bleed source: " + e.Message); }

            try
            {
                foreach (var ps in part.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (ps == null) continue;
                    string n = ps.name;
                    if (!n.Contains("Confetti") && !n.Contains("Splash")) continue;
                    Splash(ps, pos, dir);
                }
                // NPC-wide puff that lives under Physical/Body
                foreach (var ps in npc.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (ps != null && ps.name == "BloodSmokePuff") { Splash(ps, pos, dir); break; }
                }
            }
            catch (Exception e) { LoggerInstance.Warning("Blood particles: " + e.Message); }

            try { if (cb != null) cb.PlaySlashSound(pos, 1f); } catch { }
            try { if (cb != null && cb.hasArtery) cb.ArteryCut(pos, dir); } catch { }
        }

        private static void Splash(ParticleSystem ps, Vector3 pos, Vector3 dir)
        {
            var t = ps.transform;
            t.position = pos;
            t.rotation = Quaternion.FromToRotation(t.up, dir) * t.rotation;
            if (!ps.gameObject.activeSelf) ps.gameObject.SetActive(true);
            ps.Play(true);
        }

        // Scripts that animate or "break" this joint would fight the tear; switch them off on that part.
        private static void StopDriving(GameObject go)
        {
            foreach (var mb in go.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                if (mb.TryCast<ActRagJoint>() != null || mb.TryCast<ActRagHipJoint>() != null || mb.TryCast<StickJointBreaker>() != null)
                    mb.enabled = false;
            }
        }

        private static bool IsFree(ConfigurableJoint j) =>
            j.xMotion == ConfigurableJointMotion.Free && j.yMotion == ConfigurableJointMotion.Free &&
            j.zMotion == ConfigurableJointMotion.Free && j.angularXMotion == ConfigurableJointMotion.Free;

        private static readonly JointDrive Zero = new JointDrive { positionSpring = 0f, positionDamper = 0f, maximumForce = 0f };

        private static void Limp(ConfigurableJoint j)
        {
            j.xDrive = Zero; j.yDrive = Zero; j.zDrive = Zero;
            j.angularXDrive = Zero; j.angularYZDrive = Zero; j.slerpDrive = Zero;
        }

        private void LogGrip(Grip g)
        {
            if (_verbose.Value && g.Max > 1f)
                LoggerInstance.Msg($"Let go of '{g.Name}': strongest pull {g.Max:F0} N (tear at {_force.Value:F0} N)");
        }
    }
}
