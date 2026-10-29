using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TerraformTech
{
    /// <summary>
    /// In-flight terraforming brush. F8 toggles the tool; hold left mouse on terrain to paint.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TerraformTool : MonoBehaviour
    {
        const KeyCode ToggleKey = KeyCode.F8;
        const float ApplyInterval = 0.05f;
        const int RingSegments = 48;
        const float RaycastDistance = 20000f;

        static readonly string[] OpNames = { "Raise", "Lower", "Flatten", "Smooth" };
        static readonly string[] ShaderCandidates =
        {
            "Sprites/Default", "KSP/Particles/Alpha Blended", "Legacy Shaders/Particles/Alpha Blended",
            "Particles/Alpha Blended", "Unlit/Color"
        };

        // Persist tool settings across flight scenes.
        static BrushOp op = BrushOp.Lower;
        static float radius = 40f;
        static float strength = 10f;

        bool active;
        Rect windowRect = new Rect(Screen.width - 320, 120, 300, 0);
        readonly int windowId = "TerraformTech".GetHashCode();

        LineRenderer ring;
        bool hasHit;
        Vector3d hitPoint;
        BodyTerrain hitBody;

        bool painting;
        double flattenAltitude;
        float lastApply;

        // stats
        double lastEditMs;
        QuadRebuilder.Stats lastStats;
        PQ hitQuad;
        double lastLift;

        readonly Stopwatch sw = new Stopwatch();
        readonly List<KeyValuePair<Part, double>> partAltitudes = new List<KeyValuePair<Part, double>>();

        void Start()
        {
            Terraform.AttachAll(); // in case the main menu hook was skipped
            CreateRing();
        }

        void OnDestroy()
        {
            if (ring != null) Destroy(ring.gameObject);
        }

        void CreateRing()
        {
            Shader shader = null;
            foreach (string name in ShaderCandidates)
            {
                shader = Shader.Find(name);
                if (shader != null) break;
            }
            if (shader == null)
            {
                Terraform.Log("no line shader found, brush preview disabled");
                return;
            }
            var go = new GameObject("TerraformBrushRing");
            ring = go.AddComponent<LineRenderer>();
            ring.material = new Material(shader);
            ring.useWorldSpace = true;
            ring.loop = true;
            ring.positionCount = RingSegments;
            ring.startColor = ring.endColor = new Color(0.3f, 0.9f, 1f, 0.9f);
            ring.enabled = false;
        }

        void Update()
        {
            if (Input.GetKeyDown(ToggleKey))
            {
                active = !active;
                ScreenMessages.PostScreenMessage("Terraform tool " + (active ? "ON" : "OFF"), 2f, ScreenMessageStyle.UPPER_CENTER);
                painting = false;
                BodyTerrain bt = Terraform.Get(FlightGlobals.currentMainBody);
                if (active && bt != null)
                    Terraform.Log(string.Format("tool on at {0}: maxLevel={1} vertexSpacing~{2:F1}m cell~{3:F2}m quadLayer={4}",
                        bt.body.bodyName, bt.pqs.maxLevel, bt.VertexSpacing(), bt.CellSize,
                        bt.pqs.quads != null && bt.pqs.quads.Length > 0 && bt.pqs.quads[0] != null ? bt.pqs.quads[0].gameObject.layer : -1));
            }
            if (!active)
            {
                if (ring != null) ring.enabled = false;
                return;
            }

            bool overWindow = windowRect.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y))
                || (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()); // KSP's own UI
            UpdateHit();

            if (!hasHit || overWindow || !Input.GetMouseButton(0))
            {
                if (painting)
                    Terraform.Log(string.Format("stroke end: {0} tiles, last edit {1:F1} ms, quads {2}",
                        hitBody != null ? hitBody.store.TileCount : 0, lastEditMs, lastStats));
                painting = false;
                return;
            }

            Vector3d localCenter = hitBody.WorldToLocalDir(hitPoint);
            if (!painting)
            {
                painting = true;
                flattenAltitude = hitBody.Altitude(localCenter);
                LogHitQuad();
                lastApply = Time.time - ApplyInterval;
            }

            float now = Time.time;
            if (now - lastApply < ApplyInterval) return;
            float dt = Mathf.Min(now - lastApply, 0.2f);
            lastApply = now;

            ApplyBrush(hitBody, hitPoint, localCenter, dt);
        }

        void LateUpdate()
        {
            if (ring == null) return;
            ring.enabled = active && hasHit;
            if (ring.enabled) UpdateRing();
        }

        void UpdateHit()
        {
            hasHit = false;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            RaycastHit[] hits = Physics.RaycastAll(ray, RaycastDistance, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (RaycastHit h in hits)
            {
                if (h.distance >= best) continue;
                PQ quad = h.collider.GetComponent<PQ>();
                if (quad == null) continue;
                BodyTerrain bt = Terraform.Get(quad.sphereRoot != null ? FindBody(quad.sphereRoot) : null);
                if (bt == null) continue;
                best = h.distance;
                hitPoint = h.point;
                hitBody = bt;
                hitQuad = quad;
                hasHit = true;
            }
        }

        void LogHitQuad()
        {
            PQ q = hitQuad;
            if (q == null) return;
            Vector3 local = q.transform.InverseTransformPoint(hitPoint);
            Terraform.Log(string.Format(
                "stroke start: quad {0} subdiv={1} built={2} active={3} cached={4} subdivided={5} meshNull={6} bounds={7} localHit={8} scale={9}",
                q.name, q.subdivision, q.isBuilt, q.isActive, q.isCached, q.isSubdivided, q.mesh == null,
                q.mesh != null ? q.mesh.bounds.ToString() : "-", local, q.transform.lossyScale));
        }

        static CelestialBody FindBody(PQS pqs)
        {
            CelestialBody main = FlightGlobals.currentMainBody;
            if (main != null && main.pqsController == pqs) return main;
            foreach (CelestialBody b in FlightGlobals.Bodies)
                if (b.pqsController == pqs) return b;
            return null;
        }

        void ApplyBrush(BodyTerrain bt, Vector3d worldCenter, Vector3d localCenter, float dt)
        {
            sw.Reset();
            sw.Start();

            double rate;
            switch (op)
            {
                case BrushOp.Raise:
                case BrushOp.Lower:
                    rate = strength * dt; // meters at brush center
                    break;
                default:
                    rate = 1.0 - Math.Exp(-strength * 0.2 * dt); // blend fraction
                    break;
            }

            CaptureNearbyParts(bt, worldCenter);
            bt.store.ApplyBrush(op, localCenter, radius, bt.pqs.radius, rate, flattenAltitude, bt.BaseAltitude);
            lastLift = LiftNearbyVessels(bt);
            lastStats = QuadRebuilder.RebuildArea(bt.pqs, worldCenter, radius);

            sw.Stop();
            lastEditMs = sw.Elapsed.TotalMilliseconds;
        }

        #region Vessel safety

        /// <summary>Remembers the terrain altitude under every part of grounded vessels near the brush.</summary>
        void CaptureNearbyParts(BodyTerrain bt, Vector3d worldCenter)
        {
            partAltitudes.Clear();
            double reach = radius + 5.0;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed || v.mainBody != bt.body) continue;
                if (!v.LandedOrSplashed && !(v.heightFromTerrain >= 0 && v.heightFromTerrain < 5)) continue;
                foreach (Part p in v.parts)
                {
                    if ((p.transform.position - (Vector3)worldCenter).sqrMagnitude > reach * reach) continue;
                    partAltitudes.Add(new KeyValuePair<Part, double>(p, bt.Altitude(bt.WorldToLocalDir(p.transform.position))));
                }
            }
        }

        /// <summary>Moves grounded vessels up by however much the terrain rose under them, so new colliders don't spawn inside parts.</summary>
        double LiftNearbyVessels(BodyTerrain bt)
        {
            if (partAltitudes.Count == 0) return 0;
            var lift = new Dictionary<Vessel, double>();
            foreach (var kv in partAltitudes)
            {
                Part p = kv.Key;
                if (p == null || p.vessel == null) continue;
                double rise = bt.Altitude(bt.WorldToLocalDir(p.transform.position)) - kv.Value;
                double current;
                lift.TryGetValue(p.vessel, out current);
                if (rise > current) lift[p.vessel] = rise;
            }

            double maxLift = 0;
            foreach (var kv in lift)
            {
                if (kv.Value < 0.01) continue;
                Vessel v = kv.Key;
                Vector3d up = (v.transform.position - bt.body.position).normalized;
                v.IgnoreGForces(10);
                v.Translate(up * (kv.Value + 0.05));
                maxLift = Math.Max(maxLift, kv.Value);
            }
            return maxLift;
        }

        #endregion

        void UpdateRing()
        {
            CelestialBody body = hitBody.body;
            Vector3d up = (hitPoint - body.position).normalized;
            Vector3d t1 = Vector3d.Cross(up, Math.Abs(up.y) < 0.9 ? Vector3d.up : Vector3d.right).normalized;
            Vector3d t2 = Vector3d.Cross(up, t1);
            ring.widthMultiplier = Mathf.Max(0.3f, radius * 0.015f);

            for (int i = 0; i < RingSegments; i++)
            {
                double ang = i * 2.0 * Math.PI / RingSegments;
                Vector3d p = hitPoint + (t1 * Math.Cos(ang) + t2 * Math.Sin(ang)) * radius;
                double lat = body.GetLatitude(p, false), lon = body.GetLongitude(p, false);
                double alt = body.TerrainAltitude(lat, lon, true);
                ring.SetPosition(i, body.GetWorldSurfacePosition(lat, lon, alt + 0.5));
            }
        }

        #region GUI

        void OnGUI()
        {
            if (!active) return;
            windowRect = GUILayout.Window(windowId, windowRect, DrawWindow, "Terraform (F8)", GUILayout.Width(300));
        }

        void DrawWindow(int id)
        {
            op = (BrushOp)GUILayout.Toolbar((int)op, OpNames);

            GUILayout.Label(string.Format("Radius: {0:F0} m", radius));
            radius = Mathf.Round(GUILayout.HorizontalSlider(radius, 5f, 250f));
            GUILayout.Label(string.Format("Strength: {0:F1}{1}", strength, op == BrushOp.Raise || op == BrushOp.Lower ? " m/s" : ""));
            strength = Mathf.Round(GUILayout.HorizontalSlider(strength, 0.5f, 50f) * 2f) / 2f;

            BodyTerrain bt = hasHit ? hitBody : Terraform.Get(FlightGlobals.currentMainBody);
            if (bt != null)
            {
                GUILayout.Space(6);
                GUILayout.Label(string.Format("{0}: PQS vertex ~{1:F1} m, edit cell ~{2:F1} m", bt.body.bodyName, bt.VertexSpacing(), bt.CellSize));
                GUILayout.Label(string.Format("Edited tiles: {0}", bt.store.TileCount));
                if (hasHit)
                {
                    Vector3d dir = bt.WorldToLocalDir(hitPoint);
                    GUILayout.Label(string.Format("Cursor alt: {0:F1} m (edit {1:+0.0;-0.0} m)", bt.Altitude(dir), bt.store.Sample(dir)));
                }
                GUILayout.Label(string.Format("Last edit: {0:F1} ms, {1} quads rebuilt", lastEditMs, lastStats.rebuilt));
                if (lastLift > 0) GUILayout.Label(string.Format("Lifted vessel by {0:F2} m", lastLift));

                GUILayout.Space(6);
                if (GUILayout.Button("Clear all edits on " + bt.body.bodyName))
                {
                    bt.QueueEditedAreaRebuild();
                    bt.store.Clear();
                }
            }
            else
            {
                GUILayout.Label("No terraformable body here.");
            }

            GUI.DragWindow();
        }

        #endregion
    }
}
