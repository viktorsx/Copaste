// Copaste — samostalne ograde/živice (standalone net lanes).
//
// Anatomija u podacima igre: postavljena ograda = dva nevidljiva
// "container" čvora + container ivica (Game.Net.Edge + Curve +
// Game.Tools.EditorContainer{m_Prefab = NetLanePrefab} + PseudoRandomSeed +
// SubLane bafer, BEZ EdgeGeometry) + vidljivi lane koji Game.Net.LaneSystem
// regeneriše iz container krive na svaki Updated vlasnika. Zato se OVDE svuda
// radi sa container ivicom, nikad sa vidljivim lane-om.
//
// Postavljanje ide kroz igrin definicioni pipeline (isti kao NetToolSystem):
// CreationDefinition{m_Prefab = container NetPrefab, m_SubPrefab = ograda}
// + NetCourse + Updated. Pokretanje/rotacija = direktan prepis krive i
// pozicija čvorova + Updated.

namespace Copaste
{
    using System.Collections.Generic;
    using Colossal.Collections;
    using Colossal.Entities;
    using Colossal.Mathematics;
    using Game.Common;
    using Game.Prefabs;
    using Game.Rendering;
    using Game.Simulation;
    using Game.Tools;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Mathematics;

    public partial class CopasteToolSystem
    {
        // Klik bira najbližu krivu u ovom radijusu oko terenskog pogotka.
        private const float kLanePickRadius = 8f;
        private const float kLanePickThreshold = 1.5f;

        // Teselacija krive za overlay i marquee test.
        private const int kLaneOverlaySegments = 12;
        private const int kLaneMarqueeSamples = 8;

        private EntityQuery m_LaneQuery;
        private EntityQuery m_LaneContainerQuery;
        private Entity m_LaneContainerPrefab = Entity.Null;
        private bool m_LaneContainerMissingLogged;

        private readonly List<Entity> m_SelectedLanes = new List<Entity>();
        private readonly List<LaneClipboardItem> m_ClipboardLanes = new List<LaneClipboardItem>();
        private readonly List<LaneMoveItem> m_MoveLaneItems = new List<LaneMoveItem>();

        // Scratch za popis suseda pre mutacija (AddComponent invalidira bafere).
        private readonly List<Entity> m_LaneNeighborScratch = new List<Entity>();

        // Scratch grupa selektovanih ograda za vruce petlje (bez alokacija po frejmu).
        private readonly HashSet<Entity> m_LaneGroupScratch = new HashSet<Entity>();

        private HashSet<Entity> BuildLaneGroup()
        {
            m_LaneGroupScratch.Clear();
            foreach (Entity lane in m_SelectedLanes)
            {
                m_LaneGroupScratch.Add(lane);
            }

            return m_LaneGroupScratch;
        }

        private static bool SelectFences => Mod.Settings != null && Mod.Settings.SelectFences;

        // Ograda u clipboardu: prefab ograde (iz EditorContainer-a) + 4 bezier
        // tačke kao xz ofseti od centroida + visina svake tačke iznad terena.
        private struct LaneClipboardItem
        {
            public Entity m_Prefab;
            public float2[] m_CurveOffsets;
            public float[] m_HeightOffsets;
            public bool m_HasSeed;
            public ushort m_Seed;
            public int m_PreviewSeed;
        }

        private struct LaneMoveItem
        {
            public Entity m_Entity;
            public float2 m_Offset;
        }

        // Undo snimak: cela kriva + seed + elevacija. Za rekreaciju obrisane
        // ograde dovoljni su prefab + kriva (čvorovi se izvode iz krajeva).
        private struct LaneSnapshot
        {
            public Entity m_Entity;
            public Entity m_Prefab;
            public Bezier4x3 m_Curve;
            public bool m_HasSeed;
            public ushort m_Seed;
            public bool m_HadElevation;
            public float2 m_Elevation;
        }

        private void OnCreateFences()
        {
            // Samostalne ograde: container ivica. EditorContainer u upitu
            // elegantno isključuje puteve (oni ga nemaju), Owner isključuje
            // ograde u vlasništvu zgrada.
            m_LaneQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Net.Edge>(),
                    ComponentType.ReadOnly<Game.Net.Curve>(),
                    ComponentType.ReadOnly<Game.Tools.EditorContainer>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            // Prefab nevidljivog "Lane Editor Container" asseta — traži se po
            // podacima (EditorContainerData + NetData), ne po imenu.
            m_LaneContainerQuery = GetEntityQuery(ComponentType.ReadOnly<EditorContainerData>());
        }

        private static float3 LaneMidpoint(Bezier4x3 bezier) => MathUtils.Position(bezier, 0.5f);

        private bool TryGetLanePrefab(Entity lane, out Entity lanePrefab)
        {
            if (EntityManager.TryGetComponent(lane, out Game.Tools.EditorContainer container) &&
                container.m_Prefab != Entity.Null)
            {
                lanePrefab = container.m_Prefab;
                return true;
            }

            lanePrefab = Entity.Null;
            return false;
        }

        private bool IsCopyableLane(Entity lane)
        {
            return EntityManager.Exists(lane) &&
                EntityManager.HasComponent<Game.Net.Edge>(lane) &&
                EntityManager.HasComponent<Game.Net.Curve>(lane) &&
                EntityManager.HasComponent<Game.Tools.EditorContainer>(lane) &&
                !EntityManager.HasComponent<Owner>(lane) &&
                !EntityManager.HasComponent<Overridden>(lane) &&
                !EntityManager.HasComponent<Temp>(lane) &&
                !EntityManager.HasComponent<Deleted>(lane);
        }

        // Klik-selekcija ograde: najbliža container kriva oko terenskog pogotka
        // (geometrijski, kroz net quad tree — raycast maska se ne dira).
        private bool TryPickLaneAt(float3 position, out Entity lane)
        {
            lane = Entity.Null;
            if (m_NetSearchSystem == null)
            {
                m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            }

            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle dependencies);
            dependencies.Complete();

            RoadIterator iterator = new RoadIterator
            {
                m_Bounds = new Bounds3(
                    position - new float3(kLanePickRadius, 1000f, kLanePickRadius),
                    position + new float3(kLanePickRadius, 1000f, kLanePickRadius)),
                m_Results = new NativeList<Entity>(16, Allocator.Temp),
            };
            tree.Iterate(ref iterator, 0);

            // Prag odlučuje SAMO xz distanca; visinski penal je tu da razdvoji
            // kandidate koji su već prošli. (Kad je penal ulazio u isto
            // poređenje, podignuta ograda je gubila klikabilnu širinu sa
            // svakim metrom visine — na 20 m se više nije mogla ni izabrati.)
            float bestScore = float.MaxValue;
            for (int i = 0; i < iterator.m_Results.Length; i++)
            {
                Entity candidate = iterator.m_Results[i];
                if (!IsCopyableLane(candidate) ||
                    !EntityManager.TryGetComponent(candidate, out Game.Net.Curve curve))
                {
                    continue;
                }

                // T-filter važi i za ograde — isto obećanje kao za površine.
                if (m_SameFilterPrefab != Entity.Null &&
                    (!TryGetLanePrefab(candidate, out Entity lanePrefab) || lanePrefab != m_SameFilterPrefab))
                {
                    continue;
                }

                // xz distanca + mali visinski penal (kao mreže): klik raycast
                // pogađa TEREN ispod, pa bi 3D distanca podignutu ogradu
                // učinila neklikabilnom.
                // Podzemni režim i ovde: okvir ga poštuje (CollectLanesInMarquee),
                // pa bi bez ovoga klik i okvir davali različit rezultat —
                // klik na metro tunel bi selektovao živu ogradu iznad njega.
                if (UndergroundMode && !MatchesUndergroundMode(LaneMidpoint(curve.m_Bezier)))
                {
                    continue;
                }

                MathUtils.Distance(curve.m_Bezier, position, out float t);
                float3 closest = MathUtils.Position(curve.m_Bezier, t);
                float distance = math.distance(closest.xz, position.xz);
                if (distance <= kLanePickThreshold)
                {
                    float score = distance + (0.05f * math.abs(closest.y - position.y));
                    if (score < bestScore)
                    {
                        bestScore = score;
                        lane = candidate;
                    }
                }
            }

            iterator.m_Results.Dispose();
            return lane != Entity.Null;
        }

        // Postojeći container čvor na (skoro) istoj poziciji — za ponovno
        // vezivanje lančane ograde pri undo-u brisanja.
        private bool TryFindLaneNodeAt(float3 position, out Entity node)
        {
            node = Entity.Null;
            if (m_NetSearchSystem == null)
            {
                m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            }

            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle dependencies);
            dependencies.Complete();

            RoadIterator iterator = new RoadIterator
            {
                m_Bounds = new Bounds3(position - 0.5f, position + 0.5f),
                m_Results = new NativeList<Entity>(8, Allocator.Temp),
            };
            tree.Iterate(ref iterator, 0);

            for (int i = 0; i < iterator.m_Results.Length; i++)
            {
                Entity candidate = iterator.m_Results[i];
                if (EntityManager.HasComponent<Game.Tools.EditorContainer>(candidate) &&
                    !EntityManager.HasComponent<Temp>(candidate) &&
                    !EntityManager.HasComponent<Deleted>(candidate) &&
                    EntityManager.TryGetComponent(candidate, out Game.Net.Node nodeData) &&
                    math.distancesq(nodeData.m_Position, position) <= 0.01f)
                {
                    node = candidate;
                    break;
                }
            }

            iterator.m_Results.Dispose();
            return node != Entity.Null;
        }

        // Marquee: ograda ulazi ako bilo koji uzorak njene krive upadne u
        // kamera-poravnat okvir (isti test kao za površine).
        private void CollectLanesInMarquee()
        {
            if (!SelectFences)
            {
                return;
            }

            float2 delta = m_MarqueeEnd.xz - m_MarqueeStart.xz;
            float u = math.dot(delta, m_MarqueeRight);
            float v = math.dot(delta, m_MarqueeForward);
            float uMin = math.min(0f, u);
            float uMax = math.max(0f, u);
            float vMin = math.min(0f, v);
            float vMax = math.max(0f, v);

            // Geometrija PRE komponentnih provera (isti princip kao ostali
            // marquee skenovi) — box test odbije većinu mape jeftino.
            HashSet<Entity> alreadySelected = new HashSet<Entity>(m_SelectedLanes);
            NativeArray<Entity> lanes = m_LaneQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < lanes.Length; i++)
            {
                if (SelectedCount >= kMaxSelection)
                {
                    break;
                }

                if (alreadySelected.Contains(lanes[i]) ||
                    !EntityManager.TryGetComponent(lanes[i], out Game.Net.Curve curve))
                {
                    continue;
                }

                bool inside = false;
                for (int s = 0; s <= kLaneMarqueeSamples; s++)
                {
                    float3 sample = MathUtils.Position(curve.m_Bezier, s / (float)kLaneMarqueeSamples);
                    float2 offset = sample.xz - m_MarqueeStart.xz;
                    float pu = math.dot(offset, m_MarqueeRight);
                    float pv = math.dot(offset, m_MarqueeForward);
                    if (pu >= uMin && pu <= uMax && pv >= vMin && pv <= vMax)
                    {
                        inside = true;
                        break;
                    }
                }

                if (!inside || !IsCopyableLane(lanes[i]))
                {
                    continue;
                }

                if (m_SameFilterPrefab != Entity.Null &&
                    (!TryGetLanePrefab(lanes[i], out Entity lanePrefab) || lanePrefab != m_SameFilterPrefab))
                {
                    continue;
                }

                // Podzemni režim bira samo ono ispod zemlje; u normalnom se ne
                // filtrira (ukopana ograda je legitimna). Uzorak terena je
                // skup, pa ide tek pošto je okvir odbacio većinu kandidata.
                if (UndergroundMode && !MatchesUndergroundMode(LaneMidpoint(curve.m_Bezier)))
                {
                    continue;
                }

                m_SelectedLanes.Add(lanes[i]);
            }

            lanes.Dispose();

            if (m_SelectedLanes.Count > 0)
            {
                Mod.Log.Info($"Copaste: {m_SelectedLanes.Count} fences in selection");
            }
        }

        // Rotacija oko pivota + delta na sve 4 tačke krive; visina svake tačke
        // prati teren uz očuvan ofset (ograde leže na terenu — isto pravilo kao
        // TransformSurface). Čvorovi prate krajeve krive; dužina se preračunava.
        // markUpdated=false tokom drag-a VELIKIH selekcija (tick/settle pokrivaju
        // pun update) — za male je uvek true, da vidljiva ograda ne kasni.
        private void TransformLane(Entity lane, quaternion rotation, float3 pivot, float3 delta, ICollection<Entity> groupLanes = null, bool markUpdated = true)
        {
            if (!EntityManager.Exists(lane) ||
                EntityManager.HasComponent<Owner>(lane) ||
                !EntityManager.TryGetComponent(lane, out Game.Net.Curve curve))
            {
                return;
            }

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            curve.m_Bezier.a = TransformLanePoint(ref heightData, curve.m_Bezier.a, rotation, pivot, delta);
            curve.m_Bezier.b = TransformLanePoint(ref heightData, curve.m_Bezier.b, rotation, pivot, delta);
            curve.m_Bezier.c = TransformLanePoint(ref heightData, curve.m_Bezier.c, rotation, pivot, delta);
            curve.m_Bezier.d = TransformLanePoint(ref heightData, curve.m_Bezier.d, rotation, pivot, delta);
            curve.m_Length = MathUtils.Length(curve.m_Bezier);
            EntityManager.SetComponentData(lane, curve);

            if (EntityManager.TryGetComponent(lane, out Game.Net.Edge edge))
            {
                MoveLaneNode(edge.m_Start, curve.m_Bezier.a, curve.m_Bezier.b - curve.m_Bezier.a, lane, groupLanes, markUpdated);
                MoveLaneNode(edge.m_End, curve.m_Bezier.d, curve.m_Bezier.d - curve.m_Bezier.c, lane, groupLanes, markUpdated);
            }

            if (markUpdated)
            {
                EntityManager.AddComponent<Updated>(lane);
                EntityManager.AddComponent<BatchesUpdated>(lane);
            }
        }

        private float3 TransformLanePoint(ref TerrainHeightData heightData, float3 point, quaternion rotation, float3 pivot, float3 delta)
        {
            float heightOffset = point.y - TerrainUtils.SampleHeight(ref heightData, point);
            float3 position = pivot + math.mul(rotation, point - pivot) + delta;
            position.y = TerrainUtils.SampleHeight(ref heightData, position) + heightOffset;
            return position;
        }

        // Pomeri container čvor na novi kraj krive. Rotacija čvora se postavlja
        // APSOLUTNO iz tangente krive (ne kompozicijom) — deljeni čvor lančane
        // ograde bi inače pokupio rotaciju jednom po svakoj selektovanoj karici.
        // Susednoj karici koja NIJE u grupi prepiše se samo njen kraj krive —
        // lanac ostaje spojen, oblik susedne karike se rasteže.
        // Prava kroz dva SUSEDNA spoja lanca ograda, gledano iz spoja koji se
        // vuce: jedan kraj je drugi kraj OVE ograde, drugi je dalji kraj
        // nadovezane karike. Alt tokom vuce krajnje rucke drzi spoj na toj
        // pravoj — isto sto Alt radi sa cvorom puta.
        private bool TryGetLaneJointLine(Entity lane, bool atStart, out float3 from, out float3 to)
        {
            from = default;
            to = default;
            if (!EntityManager.TryGetComponent(lane, out Game.Net.Edge edgeData) ||
                !EntityManager.TryGetComponent(lane, out Game.Net.Curve curve))
            {
                return false;
            }

            // Blizi sused: drugi kraj same ove ograde.
            from = atStart ? curve.m_Bezier.d : curve.m_Bezier.a;

            Entity joint = atStart ? edgeData.m_Start : edgeData.m_End;
            if (!EntityManager.TryGetBuffer(joint, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
            {
                return false;
            }

            m_LaneJointScratch.Clear();
            for (int i = 0; i < connected.Length; i++)
            {
                m_LaneJointScratch.Add(connected[i].m_Edge);
            }

            foreach (Entity other in m_LaneJointScratch)
            {
                if (other == lane ||
                    !EntityManager.Exists(other) ||
                    EntityManager.HasComponent<Deleted>(other) ||
                    !EntityManager.TryGetComponent(other, out Game.Net.Edge otherEdge) ||
                    !EntityManager.TryGetComponent(other, out Game.Net.Curve otherCurve))
                {
                    continue;
                }

                // ConnectedEdge bafer nosi i karike kojima ovaj čvor NIJE kraj
                // (isti slučaj koji MoveLaneNode izričito preskače). Bez ove
                // provere bi "dalji kraj" bio početak nepovezane ograde, pa bi
                // klizanje po liniji gađalo drugi kraj mape.
                bool jointIsStart = otherEdge.m_Start == joint;
                if (!jointIsStart && otherEdge.m_End != joint)
                {
                    continue;
                }

                // Dalji kraj nadovezane karike.
                to = jointIsStart ? otherCurve.m_Bezier.d : otherCurve.m_Bezier.a;
                return math.distancesq(from.xz, to.xz) > 1e-4f;
            }

            return false;
        }

        private readonly List<Entity> m_LaneJointScratch = new List<Entity>();

        private void MoveLaneNode(Entity node, float3 position, float3 tangent, Entity sourceLane, ICollection<Entity> groupLanes, bool markUpdated = true)
        {
            if (!EntityManager.Exists(node) ||
                !EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
            {
                return;
            }

            nodeData.m_Position = position;
            if (math.lengthsq(tangent.xz) > 1e-6f)
            {
                nodeData.m_Rotation = Game.Net.NetUtils.GetNodeRotation(tangent);
            }

            EntityManager.SetComponentData(node, nodeData);
            if (markUpdated)
            {
                EntityManager.AddComponent<Updated>(node);
            }

            if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> edges))
            {
                return;
            }

            // Popis pre mutacija — AddComponent u petlji bi invalidirao bafer.
            m_LaneNeighborScratch.Clear();
            for (int i = 0; i < edges.Length; i++)
            {
                m_LaneNeighborScratch.Add(edges[i].m_Edge);
            }

            foreach (Entity other in m_LaneNeighborScratch)
            {
                if (other == sourceLane ||
                    !EntityManager.Exists(other) ||
                    EntityManager.HasComponent<Deleted>(other) ||
                    (groupLanes != null && groupLanes.Contains(other)) ||
                    !EntityManager.TryGetComponent(other, out Game.Net.Edge otherEdge) ||
                    !EntityManager.TryGetComponent(other, out Game.Net.Curve otherCurve))
                {
                    continue;
                }

                bool changed = false;
                if (otherEdge.m_Start == node)
                {
                    otherCurve.m_Bezier.a = position;
                    changed = true;
                }

                if (otherEdge.m_End == node)
                {
                    otherCurve.m_Bezier.d = position;
                    changed = true;
                }

                if (changed)
                {
                    otherCurve.m_Length = MathUtils.Length(otherCurve.m_Bezier);
                    EntityManager.SetComponentData(other, otherCurve);
                    EntityManager.AddComponent<Updated>(other);
                    EntityManager.AddComponent<BatchesUpdated>(other);
                }
            }
        }

        // Završni Updated na kraju poteza (parnjak SettleSurfaces — kod ograda
        // je Updated već išao tokom poteza, ovo je čist završni prolaz).
        private void SettleLanes()
        {
            foreach (Entity lane in m_SelectedLanes)
            {
                if (EntityManager.Exists(lane) && !EntityManager.HasComponent<Owner>(lane))
                {
                    EntityManager.AddComponent<Updated>(lane);
                    EntityManager.AddComponent<BatchesUpdated>(lane);
                }
            }
        }

        // Obris selektovane ograde: teselirana kriva (container je nevidljiv,
        // pa je linija duž krive jedina vizuelna potvrda selekcije).
        private void DrawLaneOverlays(OverlayRenderSystem.Buffer overlayBuffer)
        {
            int drawn = 0;
            foreach (Entity lane in m_SelectedLanes)
            {
                if (drawn >= kMaxOverlayCircles)
                {
                    break;
                }

                if (!EntityManager.Exists(lane) ||
                    !EntityManager.TryGetComponent(lane, out Game.Net.Curve curve))
                {
                    continue;
                }

                float3 previous = MathUtils.Position(curve.m_Bezier, 0f);
                for (int s = 1; s <= kLaneOverlaySegments; s++)
                {
                    float3 current = MathUtils.Position(curve.m_Bezier, s / (float)kLaneOverlaySegments);
                    overlayBuffer.DrawLine(kSelectedColor, new Line3.Segment(previous, current), 0.3f);
                    previous = current;
                }

                drawn++;
            }
        }

        private void CaptureLanes(float3 centroid)
        {
            m_ClipboardLanes.Clear();
            if (m_SelectedLanes.Count == 0)
            {
                return;
            }

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            Unity.Mathematics.Random previewRandom = RandomSeed.Next().GetRandom(0);
            foreach (Entity lane in m_SelectedLanes)
            {
                if (!EntityManager.Exists(lane) ||
                    !EntityManager.TryGetComponent(lane, out Game.Net.Curve curve) ||
                    !TryGetLanePrefab(lane, out Entity lanePrefab))
                {
                    continue;
                }

                bool hasSeed = EntityManager.TryGetComponent(lane, out PseudoRandomSeed seed);
                float2[] offsets = new float2[4];
                float[] heights = new float[4];
                for (int k = 0; k < 4; k++)
                {
                    float3 point = GetBezierPoint(curve.m_Bezier, k);
                    offsets[k] = point.xz - centroid.xz;
                    heights[k] = point.y - TerrainUtils.SampleHeight(ref heightData, point);
                }

                m_ClipboardLanes.Add(new LaneClipboardItem
                {
                    m_Prefab = lanePrefab,
                    m_CurveOffsets = offsets,
                    m_HeightOffsets = heights,
                    m_HasSeed = hasSeed,
                    m_Seed = hasSeed ? seed.m_Seed : (ushort)0,
                    m_PreviewSeed = previewRandom.NextInt(),
                });
            }
        }

        private static float3 GetBezierPoint(Bezier4x3 bezier, int index)
        {
            switch (index)
            {
                case 0: return bezier.a;
                case 1: return bezier.b;
                case 2: return bezier.c;
                default: return bezier.d;
            }
        }

        // Nevidljivi container NetPrefab (vanila "Lane Editor Container") —
        // jedini prefab sa EditorContainerData + NetData. Keš po sesiji.
        private Entity GetLaneContainerPrefab()
        {
            if (m_LaneContainerPrefab != Entity.Null && EntityManager.Exists(m_LaneContainerPrefab))
            {
                return m_LaneContainerPrefab;
            }

            m_LaneContainerPrefab = Entity.Null;
            NativeArray<Entity> candidates = m_LaneContainerQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (EntityManager.HasComponent<NetData>(candidates[i]))
                {
                    m_LaneContainerPrefab = candidates[i];
                    break;
                }
            }

            candidates.Dispose();

            if (m_LaneContainerPrefab == Entity.Null && !m_LaneContainerMissingLogged)
            {
                m_LaneContainerMissingLogged = true;
                Mod.Log.Warn("Copaste: lane container prefab not found — fence paste unavailable");
            }

            return m_LaneContainerPrefab;
        }

        // Paste definicije za ograde — igrin net pipeline (GenerateNodes/Edges +
        // LaneSystem) od ovoga pravi Temp preview i finalne entitete na apply.
        private void CreateLaneDefinitions(EntityCommandBuffer buffer, float3 anchor, ref TerrainHeightData heightData, ref Unity.Mathematics.Random random, bool keepLook, float baseDelta)
        {
            if (m_ClipboardLanes.Count == 0)
            {
                return;
            }

            Entity container = GetLaneContainerPrefab();
            if (container == Entity.Null)
            {
                return;
            }

            foreach (LaneClipboardItem item in m_ClipboardLanes)
            {
                if (item.m_CurveOffsets == null || item.m_CurveOffsets.Length != 4 ||
                    item.m_HeightOffsets == null || item.m_HeightOffsets.Length != 4 ||
                    !EntityManager.Exists(item.m_Prefab))
                {
                    continue;
                }

                float3[] points = new float3[4];
                for (int k = 0; k < 4; k++)
                {
                    float2 xz = anchor.xz + item.m_CurveOffsets[k];
                    float3 point = new float3(xz.x, 0f, xz.y);
                    point.y = TerrainUtils.SampleHeight(ref heightData, point) + item.m_HeightOffsets[k] + baseDelta + m_PasteHeightBoost;
                    points[k] = point;
                }

                Bezier4x3 bezier = new Bezier4x3(points[0], points[1], points[2], points[3]);

                Entity definitionEntity = buffer.CreateEntity();

                CreationDefinition creation = default;
                creation.m_Prefab = container;
                creation.m_SubPrefab = item.m_Prefab;
                creation.m_RandomSeed = keepLook && item.m_HasSeed ? item.m_PreviewSeed : random.NextInt();
                creation.m_Flags |= CreationFlags.SubElevation;

                // Podignuta ograda (PgUp pre kopiranja): elevacija ide u
                // definiciju, pa pipeline sam upiše Net.Elevation — bez toga bi
                // igra nalepljenu krivu vremenom vratila na teren.
                float startElevation = item.m_HeightOffsets[0] + baseDelta + m_PasteHeightBoost;
                float endElevation = item.m_HeightOffsets[3] + baseDelta + m_PasteHeightBoost;

                NetCourse course = default;
                course.m_Curve = bezier;
                course.m_Length = MathUtils.Length(bezier);
                course.m_FixedIndex = -1;
                course.m_Elevation = new float2(startElevation, endElevation);
                // DisableMerge na OBA kraja: bez toga se nalepljena ograda vari
                // za obližnje postojeće čvorove/krive (CourseSplitSystem overlap)
                // — isti šablon kojim igrin BuildingConstructionSystem postavlja
                // pod-mreže zgrada da se ne zavare za okolinu.
                course.m_StartPosition = LaneCoursePos(bezier, 0f, CoursePosFlags.IsFirst | CoursePosFlags.IsLeft | CoursePosFlags.IsRight | CoursePosFlags.DisableMerge);
                course.m_StartPosition.m_Elevation = new float2(startElevation, startElevation);
                course.m_EndPosition = LaneCoursePos(bezier, 1f, CoursePosFlags.IsLast | CoursePosFlags.IsLeft | CoursePosFlags.IsRight | CoursePosFlags.DisableMerge);
                course.m_EndPosition.m_Elevation = new float2(endElevation, endElevation);

                buffer.AddComponent(definitionEntity, creation);
                buffer.AddComponent(definitionEntity, course);
                buffer.AddComponent(definitionEntity, default(Updated));

                m_LastPreview.Add(new PastedRecord
                {
                    m_Prefab = item.m_Prefab,
                    m_Position = LaneMidpoint(bezier),
                    m_IsLane = true,
                    m_HasSeed = keepLook && item.m_HasSeed,
                    m_Seed = item.m_Seed,
                });
            }
        }

        private static CoursePos LaneCoursePos(Bezier4x3 bezier, float courseDelta, CoursePosFlags flags)
        {
            float3 tangent = courseDelta < 0.5f ? bezier.b - bezier.a : bezier.d - bezier.c;
            if (math.lengthsq(tangent.xz) < 1e-6f)
            {
                tangent = bezier.d - bezier.a;
            }

            CoursePos pos = default;
            pos.m_Entity = Entity.Null;
            pos.m_Position = courseDelta < 0.5f ? bezier.a : bezier.d;
            pos.m_Rotation = Game.Net.NetUtils.GetNodeRotation(tangent);
            pos.m_CourseDelta = courseDelta;
            pos.m_Flags = flags;
            pos.m_ParentMesh = -1;
            return pos;
        }

        // Post-paste rezolucija: nova container ivica po prefabu ograde +
        // sredini krive (najviše jedan entitet po zapisu — claimed pravilo).
        private void ResolvePastedLanes(float3 boundsMin, float3 boundsMax, HashSet<Entity> claimed)
        {
            NativeArray<Entity> lanes = m_LaneQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < lanes.Length; i++)
            {
                if (claimed.Contains(lanes[i]) ||
                    (m_PostPasteExclude != null && m_PostPasteExclude.Contains(lanes[i])) ||
                    !EntityManager.TryGetComponent(lanes[i], out Game.Net.Curve curve) ||
                    !TryGetLanePrefab(lanes[i], out Entity lanePrefab))
                {
                    continue;
                }

                // Poređenje u xz ravni: y sredine krive može da odluta od zapisa
                // (boost/baseDelta vs teren) — 3D match bi trajno promašio.
                float3 midpoint = LaneMidpoint(curve.m_Bezier);
                if (math.any(midpoint.xz < boundsMin.xz - 1f) || math.any(midpoint.xz > boundsMax.xz + 1f))
                {
                    continue;
                }

                for (int j = 0; j < m_PostPasteFix.Count; j++)
                {
                    PastedRecord record = m_PostPasteFix[j];
                    if (!record.m_IsLane ||
                        record.m_Resolved != Entity.Null ||
                        lanePrefab != record.m_Prefab ||
                        math.distancesq(midpoint.xz, record.m_Position.xz) > 0.25f)
                    {
                        continue;
                    }

                    record.m_Resolved = lanes[i];
                    m_PostPasteFix[j] = record;
                    claimed.Add(lanes[i]);
                    ApplyPastedLaneFix(lanes[i], record);
                    break;
                }
            }

            lanes.Dispose();
        }

        // Postojeće istovetne ograde u granicama stampa — rezolucija ih ne sme
        // usvojiti (duplikat u mestu bi undo-om obrisao original).
        private void CollectPreStampLanes(List<PastedRecord> records, float3 boundsMin, float3 boundsMax, HashSet<Entity> exclude)
        {
            NativeArray<Entity> lanes = m_LaneQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < lanes.Length; i++)
            {
                if (!EntityManager.TryGetComponent(lanes[i], out Game.Net.Curve curve) ||
                    !TryGetLanePrefab(lanes[i], out Entity lanePrefab))
                {
                    continue;
                }

                // Isti xz kriterijum kao rezolucija — čim bi se razlikovali,
                // postojeća ograda može da izmakne isključenju a ipak bude
                // "usvojena", pa bi je undo obrisao.
                float3 midpoint = LaneMidpoint(curve.m_Bezier);
                if (math.any(midpoint.xz < boundsMin.xz - 1f) || math.any(midpoint.xz > boundsMax.xz + 1f))
                {
                    continue;
                }

                foreach (PastedRecord record in records)
                {
                    if (record.m_IsLane &&
                        lanePrefab == record.m_Prefab &&
                        math.distancesq(midpoint.xz, record.m_Position.xz) <= 0.25f)
                    {
                        exclude.Add(lanes[i]);
                        break;
                    }
                }
            }

            lanes.Dispose();
        }

        // Popravke na razrešenoj ogradi: anarchy zaštita + originalni seed
        // (uz Updated — LaneSystem tek na njega ponovo izvede varijaciju
        // vidljivog lane-a iz container seed-a).
        private void ApplyPastedLaneFix(Entity lane, PastedRecord record)
        {
            if (!EntityManager.Exists(lane))
            {
                return;
            }

            if (Mod.Settings.AnarchyPaste)
            {
                if (EntityManager.HasComponent<Overridden>(lane))
                {
                    EntityManager.RemoveComponent<Overridden>(lane);
                    EntityManager.AddComponent<Updated>(lane);
                    EntityManager.AddComponent<BatchesUpdated>(lane);
                }

                if (m_HasPreventOverride && !EntityManager.HasComponent(lane, m_PreventOverrideType))
                {
                    EntityManager.AddComponent(lane, m_PreventOverrideType);
                }
            }

            if (record.m_HasSeed &&
                EntityManager.TryGetComponent(lane, out PseudoRandomSeed currentSeed) &&
                currentSeed.m_Seed != record.m_Seed)
            {
                EntityManager.SetComponentData(lane, new PseudoRandomSeed(record.m_Seed));
                EntityManager.AddComponent<Updated>(lane);
                EntityManager.AddComponent<BatchesUpdated>(lane);
            }
        }

        // Brisanje ograde: Deleted na container ivicu (LaneSystem kaskadno briše
        // vidljivi lane) + na čvorove koji posle toga ne drže nijednu drugu
        // kariku (lančane ograde dele čvorove!).
        private void DeleteLaneWithNodes(Entity lane)
        {
            if (!EntityManager.Exists(lane))
            {
                return;
            }

            bool hasEdge = EntityManager.TryGetComponent(lane, out Game.Net.Edge edge);
            EntityManager.AddComponent<Deleted>(lane);
            if (hasEdge)
            {
                TryDeleteOrphanLaneNode(edge.m_Start);
                TryDeleteOrphanLaneNode(edge.m_End);
            }
        }

        private void TryDeleteOrphanLaneNode(Entity node)
        {
            if (!EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node))
            {
                return;
            }

            if (EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> edges))
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    if (EntityManager.Exists(edges[i].m_Edge) &&
                        !EntityManager.HasComponent<Deleted>(edges[i].m_Edge))
                    {
                        return;
                    }
                }
            }

            // Čvor koji odlazi ne sme da ostane u selekciji — panel bi
            // prijavljivao "N selektovano" bez ičega na mapi.
            m_SelectedNodes.Remove(node);
            DeleteNodeSubObjects(node);
            EntityManager.AddComponent<Deleted>(node);
        }

        // Paste-undo rezerva za nerazrešene lane zapise (undo pre kraja
        // rezolucije): pozicioni match, najviše jedan entitet po zapisu.
        private void DeleteUnresolvedLanes(List<PastedRecord> records, bool[] recordUsed, float3 boundsMin, float3 boundsMax, HashSet<Entity> deleted, HashSet<Entity> exclude)
        {
            NativeArray<Entity> lanes = m_LaneQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < lanes.Length; i++)
            {
                if (deleted.Contains(lanes[i]) ||
                    (exclude != null && exclude.Contains(lanes[i])) ||
                    !EntityManager.TryGetComponent(lanes[i], out Game.Net.Curve curve) ||
                    !TryGetLanePrefab(lanes[i], out Entity lanePrefab))
                {
                    continue;
                }

                float3 midpoint = LaneMidpoint(curve.m_Bezier);
                if (math.any(midpoint.xz < boundsMin.xz - 1f) || math.any(midpoint.xz > boundsMax.xz + 1f))
                {
                    continue;
                }

                for (int j = 0; j < records.Count; j++)
                {
                    PastedRecord record = records[j];
                    if (recordUsed[j] || !record.m_IsLane || record.m_Resolved != Entity.Null ||
                        lanePrefab != record.m_Prefab ||
                        math.distancesq(midpoint.xz, record.m_Position.xz) > 0.25f)
                    {
                        continue;
                    }

                    recordUsed[j] = true;
                    deleted.Add(lanes[i]);
                    m_SelectedLanes.Remove(lanes[i]);
                    DeleteLaneWithNodes(lanes[i]);
                    break;
                }
            }

            lanes.Dispose();
        }

        private List<LaneSnapshot> SnapshotLanes(IEnumerable<Entity> lanes)
        {
            List<LaneSnapshot> snapshots = new List<LaneSnapshot>();
            foreach (Entity lane in lanes)
            {
                if (!EntityManager.Exists(lane) ||
                    !EntityManager.TryGetComponent(lane, out Game.Net.Curve curve) ||
                    !TryGetLanePrefab(lane, out Entity lanePrefab))
                {
                    continue;
                }

                bool hasSeed = EntityManager.TryGetComponent(lane, out PseudoRandomSeed seed);
                bool hadElevation = EntityManager.TryGetComponent(lane, out Game.Net.Elevation elevation);
                snapshots.Add(new LaneSnapshot
                {
                    m_Entity = lane,
                    m_Prefab = lanePrefab,
                    m_Curve = curve.m_Bezier,
                    m_HasSeed = hasSeed,
                    m_Seed = hasSeed ? seed.m_Seed : (ushort)0,
                    m_HadElevation = hadElevation,
                    m_Elevation = hadElevation ? elevation.m_Elevation : default,
                });
            }

            return snapshots;
        }

        private List<LaneSnapshot> SnapshotLaneEntities(List<LaneSnapshot> reference)
        {
            List<Entity> entities = new List<Entity>(reference.Count);
            foreach (LaneSnapshot snapshot in reference)
            {
                entities.Add(snapshot.m_Entity);
            }

            return SnapshotLanes(entities);
        }

        private List<LaneSnapshot> SnapshotResolvedPastedLanes(List<PastedRecord> records)
        {
            List<Entity> entities = new List<Entity>();
            foreach (PastedRecord record in records)
            {
                if (record.m_IsLane && record.m_Resolved != Entity.Null)
                {
                    entities.Add(record.m_Resolved);
                }
            }

            return SnapshotLanes(entities);
        }

        // Undo transformacije: vrati celu krivu + čvorove + seed. Susedne
        // karike VAN snimka prate prepis kraja (isti mehanizam kao pri potezu).
        private void ApplyLaneSnapshots(List<LaneSnapshot> snapshots)
        {
            HashSet<Entity> group = new HashSet<Entity>();
            foreach (LaneSnapshot snapshot in snapshots)
            {
                group.Add(snapshot.m_Entity);
            }

            foreach (LaneSnapshot snapshot in snapshots)
            {
                if (!EntityManager.Exists(snapshot.m_Entity) ||
                    EntityManager.HasComponent<Owner>(snapshot.m_Entity) ||
                    !EntityManager.TryGetComponent(snapshot.m_Entity, out Game.Net.Curve curve))
                {
                    continue;
                }

                curve.m_Bezier = snapshot.m_Curve;
                curve.m_Length = MathUtils.Length(curve.m_Bezier);
                EntityManager.SetComponentData(snapshot.m_Entity, curve);

                if (EntityManager.TryGetComponent(snapshot.m_Entity, out Game.Net.Edge edge))
                {
                    // ...WithElevation: susedu VAN snimka se prepisuje kraj
                    // krive, pa mu se mora korigovati i elevacija. Bez toga
                    // ostane na staroj visini i igra ga vrati gore — spoj se
                    // rascepi po visini posle undo-a podizanja.
                    MoveLaneNodeWithElevation(edge.m_Start, curve.m_Bezier.a, curve.m_Bezier.b - curve.m_Bezier.a, snapshot.m_Entity, group);
                    MoveLaneNodeWithElevation(edge.m_End, curve.m_Bezier.d, curve.m_Bezier.d - curve.m_Bezier.c, snapshot.m_Entity, group);
                }

                if (snapshot.m_HasSeed &&
                    EntityManager.TryGetComponent(snapshot.m_Entity, out PseudoRandomSeed currentSeed) &&
                    currentSeed.m_Seed != snapshot.m_Seed)
                {
                    EntityManager.SetComponentData(snapshot.m_Entity, new PseudoRandomSeed(snapshot.m_Seed));
                }

                // Elevacija se vraća na ivicu i oba čvora (visinske operacije je menjaju).
                RestoreLaneElevation(snapshot.m_Entity, snapshot.m_HadElevation, snapshot.m_Elevation);
                if (EntityManager.TryGetComponent(snapshot.m_Entity, out Game.Net.Edge elevationEdge))
                {
                    RestoreLaneElevation(elevationEdge.m_Start, snapshot.m_HadElevation, snapshot.m_Elevation);
                    RestoreLaneElevation(elevationEdge.m_End, snapshot.m_HadElevation, snapshot.m_Elevation);
                }

                EntityManager.AddComponent<Updated>(snapshot.m_Entity);
                EntityManager.AddComponent<BatchesUpdated>(snapshot.m_Entity);
            }
        }

        private void RestoreLaneElevation(Entity entity, bool hadElevation, float2 elevation)
        {
            if (!EntityManager.Exists(entity))
            {
                return;
            }

            if (hadElevation)
            {
                if (EntityManager.HasComponent<Game.Net.Elevation>(entity))
                {
                    EntityManager.SetComponentData(entity, new Game.Net.Elevation { m_Elevation = elevation });
                }
                else
                {
                    EntityManager.AddComponentData(entity, new Game.Net.Elevation { m_Elevation = elevation });
                }
            }
            else if (EntityManager.HasComponent<Game.Net.Elevation>(entity))
            {
                EntityManager.RemoveComponent<Game.Net.Elevation>(entity);
            }
        }

        // Pomeranje ograde po visini (PgUp/PgDn): kriva i čvorovi idu za delta,
        // a Net.Elevation se pomera isto toliko — bez toga bi igra vratila
        // krivu na teren pri sledećem update-u.
        private void AdjustLaneHeight(Entity lane, float delta, ICollection<Entity> groupLanes)
        {
            if (!EntityManager.Exists(lane) ||
                EntityManager.HasComponent<Owner>(lane) ||
                !EntityManager.TryGetComponent(lane, out Game.Net.Curve curve))
            {
                return;
            }

            float3 lift = new float3(0f, delta, 0f);
            curve.m_Bezier.a += lift;
            curve.m_Bezier.b += lift;
            curve.m_Bezier.c += lift;
            curve.m_Bezier.d += lift;
            curve.m_Length = MathUtils.Length(curve.m_Bezier);
            EntityManager.SetComponentData(lane, curve);

            ShiftLaneElevation(lane, delta);
            if (EntityManager.TryGetComponent(lane, out Game.Net.Edge edge))
            {
                MoveLaneNodeWithElevation(edge.m_Start, curve.m_Bezier.a, curve.m_Bezier.b - curve.m_Bezier.a, lane, groupLanes);
                MoveLaneNodeWithElevation(edge.m_End, curve.m_Bezier.d, curve.m_Bezier.d - curve.m_Bezier.c, lane, groupLanes);
            }

            EntityManager.AddComponent<Updated>(lane);
            EntityManager.AddComponent<BatchesUpdated>(lane);
        }

        // Pomeri čvor ograde i pomeri mu elevaciju za NJEGOV STVARNI pomak.
        // Ne za deltu karike: lanac od dve karike deli čvor (druga bi mu
        // udvostručila pomak), a Match H svakoj karici daje svoju deltu (čvor
        // bi dobio deltu one koja slučajno ide prva). Merenje pre/posle je
        // tačno u oba slučaja i ne traži pamćenje šta je već dirano.
        private void MoveLaneNodeWithElevation(Entity node, float3 position, float3 tangent, Entity lane, ICollection<Entity> groupLanes)
        {
            float before = EntityManager.TryGetComponent(node, out Game.Net.Node beforeData)
                ? beforeData.m_Position.y
                : position.y;

            MoveLaneNode(node, position, tangent, lane, groupLanes);

            float after = EntityManager.TryGetComponent(node, out Game.Net.Node afterData)
                ? afterData.m_Position.y
                : position.y;

            float shift = after - before;
            if (math.abs(shift) <= 0.001f)
            {
                return;
            }

            ShiftLaneElevation(node, shift);

            // Lančani sused van grupe: kraj mu je već prepisan (MoveLaneNode),
            // ali bez elevacije bi ga igra vremenom vratila na teren i
            // pocepala lanac po visini.
            ShiftNeighborSideElevation(node, lane, groupLanes, shift);
        }

        // Elevacija susednih karika na deljenom čvoru. Elevation float2 je
        // (LEVO, DESNO) na SREDINI ivice — ne start/end! — pa se susedu, kome
        // se digao samo jedan kraj, sredina pomera za POLA delte (obe strane).
        private void ShiftNeighborSideElevation(Entity node, Entity sourceLane, ICollection<Entity> groupLanes, float delta)
        {
            if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> edges))
            {
                return;
            }

            m_LaneNeighborScratch.Clear();
            for (int i = 0; i < edges.Length; i++)
            {
                m_LaneNeighborScratch.Add(edges[i].m_Edge);
            }

            foreach (Entity other in m_LaneNeighborScratch)
            {
                if (other == sourceLane ||
                    !EntityManager.Exists(other) ||
                    EntityManager.HasComponent<Deleted>(other) ||
                    (groupLanes != null && groupLanes.Contains(other)) ||
                    !EntityManager.TryGetComponent(other, out Game.Net.Edge otherEdge))
                {
                    continue;
                }

                if (otherEdge.m_Start != node && otherEdge.m_End != node)
                {
                    continue;
                }

                float2 elevation = EntityManager.TryGetComponent(other, out Game.Net.Elevation current)
                    ? current.m_Elevation
                    : float2.zero;
                elevation += delta * 0.5f;

                if (math.abs(elevation.x) <= 0.01f && math.abs(elevation.y) <= 0.01f)
                {
                    if (EntityManager.HasComponent<Game.Net.Elevation>(other))
                    {
                        EntityManager.RemoveComponent<Game.Net.Elevation>(other);
                    }
                }
                else if (EntityManager.HasComponent<Game.Net.Elevation>(other))
                {
                    EntityManager.SetComponentData(other, new Game.Net.Elevation { m_Elevation = elevation });
                }
                else
                {
                    EntityManager.AddComponentData(other, new Game.Net.Elevation { m_Elevation = elevation });
                }
            }
        }

        // Elevation prati zbir pomaka; blizu nule se skida (igra tada normalno
        // konformira na teren — isto pravilo kao WriteElevation za propove).
        private void ShiftLaneElevation(Entity entity, float delta)
        {
            if (!EntityManager.Exists(entity))
            {
                return;
            }

            float2 elevation = EntityManager.TryGetComponent(entity, out Game.Net.Elevation current)
                ? current.m_Elevation + delta
                : new float2(delta, delta);

            if (math.abs(elevation.x) <= 0.01f && math.abs(elevation.y) <= 0.01f)
            {
                if (EntityManager.HasComponent<Game.Net.Elevation>(entity))
                {
                    EntityManager.RemoveComponent<Game.Net.Elevation>(entity);
                }
            }
            else if (EntityManager.HasComponent<Game.Net.Elevation>(entity))
            {
                EntityManager.SetComponentData(entity, new Game.Net.Elevation { m_Elevation = elevation });
            }
            else
            {
                EntityManager.AddComponentData(entity, new Game.Net.Elevation { m_Elevation = elevation });
            }
        }

        // Visina za sve selektovane ograde (poziva AdjustSelectionHeight).
        private void AdjustSelectedLaneHeights(float delta)
        {
            if (m_SelectedLanes.Count == 0)
            {
                return;
            }

            HashSet<Entity> group = BuildLaneGroup();
            foreach (Entity lane in m_SelectedLanes)
            {
                AdjustLaneHeight(lane, delta, group);
            }
        }

        // End (snap to ground): kriva se spušta na teren, elevacija se skida.
        private void SnapLanesToGround()
        {
            if (m_SelectedLanes.Count == 0)
            {
                return;
            }

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            HashSet<Entity> group = BuildLaneGroup();
            foreach (Entity lane in m_SelectedLanes)
            {
                if (!EntityManager.Exists(lane) ||
                    EntityManager.HasComponent<Owner>(lane) ||
                    !EntityManager.TryGetComponent(lane, out Game.Net.Curve curve))
                {
                    continue;
                }

                curve.m_Bezier.a.y = TerrainUtils.SampleHeight(ref heightData, curve.m_Bezier.a);
                curve.m_Bezier.b.y = TerrainUtils.SampleHeight(ref heightData, curve.m_Bezier.b);
                curve.m_Bezier.c.y = TerrainUtils.SampleHeight(ref heightData, curve.m_Bezier.c);
                curve.m_Bezier.d.y = TerrainUtils.SampleHeight(ref heightData, curve.m_Bezier.d);
                curve.m_Length = MathUtils.Length(curve.m_Bezier);
                EntityManager.SetComponentData(lane, curve);

                RestoreLaneElevation(lane, hadElevation: false, default);
                if (EntityManager.TryGetComponent(lane, out Game.Net.Edge edge))
                {
                    // Isti razlog kao na undo putanji: sused van selekcije
                    // mora da siđe i po elevaciji, ne samo po krivoj.
                    MoveLaneNodeWithElevation(edge.m_Start, curve.m_Bezier.a, curve.m_Bezier.b - curve.m_Bezier.a, lane, group);
                    MoveLaneNodeWithElevation(edge.m_End, curve.m_Bezier.d, curve.m_Bezier.d - curve.m_Bezier.c, lane, group);
                    RestoreLaneElevation(edge.m_Start, hadElevation: false, default);
                    RestoreLaneElevation(edge.m_End, hadElevation: false, default);
                }

                EntityManager.AddComponent<Updated>(lane);
                EntityManager.AddComponent<BatchesUpdated>(lane);
            }
        }

        // Undo brisanja: nova container mreža direktno iz arhetipova container
        // prefaba (isti pristup kao RecreateProp/RecreateSurface) — LaneSystem
        // od nje sam napravi vidljivi lane.
        // Čvorovi napravljeni u OVOM potezu rekreacije. Quad tree ih još ne
        // zna (u njega ulaze tek kad ih igra obradi), pa bi svaka sledeća
        // karika lanca napravila svoj čvor na istom mestu: lanac izgleda ceo,
        // a topološki je isečen na pojedinačne ograde — pomeranje spoja pomeri
        // samo jednu stranu. Zato se u potezu pamti šta je već napravljeno.
        private struct RecreatedLaneNode
        {
            public float3 m_Position;
            public Entity m_Node;
        }

        private readonly List<RecreatedLaneNode> m_RecreatedLaneNodes = new List<RecreatedLaneNode>();

        private void BeginLaneRecreateBatch()
        {
            m_RecreatedLaneNodes.Clear();
        }

        private bool TryReuseRecreatedLaneNode(float3 position, out Entity node)
        {
            foreach (RecreatedLaneNode candidate in m_RecreatedLaneNodes)
            {
                if (math.distancesq(candidate.m_Position, position) <= 0.01f &&
                    EntityManager.Exists(candidate.m_Node) &&
                    !EntityManager.HasComponent<Deleted>(candidate.m_Node))
                {
                    node = candidate.m_Node;
                    return true;
                }
            }

            node = Entity.Null;
            return false;
        }

        private void RecreateLane(LaneSnapshot snapshot)
        {
            Entity container = GetLaneContainerPrefab();
            if (container == Entity.Null ||
                !EntityManager.Exists(snapshot.m_Prefab) ||
                !EntityManager.TryGetComponent(container, out NetData netData))
            {
                return;
            }

            // Deljeni čvor lančane ograde koji je preživeo brisanje (susedna
            // karika ga i dalje drži) se PONOVO KORISTI — nova dva čvora bi
            // lanac vizuelno ostavila celim a topološki ga presekla.
            // Prvo čvor iz OVOG poteza (quad tree ga još ne vidi), pa tek
            // onda onaj koji je preživeo brisanje u svetu.
            bool startReused = TryReuseRecreatedLaneNode(snapshot.m_Curve.a, out Entity startNode) ||
                TryFindLaneNodeAt(snapshot.m_Curve.a, out startNode);
            if (!startReused)
            {
                startNode = CreateLaneNode(container, netData, snapshot, snapshot.m_Curve.a, snapshot.m_Curve.b - snapshot.m_Curve.a);
            }

            bool endReused = TryReuseRecreatedLaneNode(snapshot.m_Curve.d, out Entity endNode) ||
                TryFindLaneNodeAt(snapshot.m_Curve.d, out endNode);
            if (!endReused)
            {
                endNode = CreateLaneNode(container, netData, snapshot, snapshot.m_Curve.d, snapshot.m_Curve.d - snapshot.m_Curve.c);
            }

            if (startNode == Entity.Null || endNode == Entity.Null)
            {
                return;
            }

            if (startReused)
            {
                EntityManager.AddComponent<Updated>(startNode);
            }

            if (endReused)
            {
                EntityManager.AddComponent<Updated>(endNode);
            }

            NativeArray<Entity> created = EntityManager.CreateEntity(netData.m_EdgeArchetype, 1, Allocator.Temp);
            Entity lane = created[0];
            created.Dispose();

            if (!EntityManager.HasComponent<Game.Net.Edge>(lane) ||
                !EntityManager.HasComponent<Game.Net.Curve>(lane) ||
                !EntityManager.HasComponent<Game.Tools.EditorContainer>(lane))
            {
                // Arhetip bez očekivanih komponenti — odustani čisto (obriši
                // samo ono što smo MI napravili, tuđe deljene čvorove ne).
                EntityManager.AddComponent<Deleted>(lane);
                if (!startReused)
                {
                    EntityManager.AddComponent<Deleted>(startNode);
                }

                if (!endReused)
                {
                    EntityManager.AddComponent<Deleted>(endNode);
                }

                Mod.Log.Warn("Copaste: lane edge archetype missing expected components — undo of fence delete skipped");
                return;
            }

            EntityManager.SetComponentData(lane, new PrefabRef(container));
            EntityManager.SetComponentData(lane, new Game.Net.Edge { m_Start = startNode, m_End = endNode });
            EntityManager.SetComponentData(lane, new Game.Net.Curve { m_Bezier = snapshot.m_Curve, m_Length = MathUtils.Length(snapshot.m_Curve) });
            EntityManager.SetComponentData(lane, new Game.Tools.EditorContainer { m_Prefab = snapshot.m_Prefab });
            if (EntityManager.HasComponent<PseudoRandomSeed>(lane))
            {
                EntityManager.SetComponentData(lane, snapshot.m_HasSeed
                    ? new PseudoRandomSeed(snapshot.m_Seed)
                    : new PseudoRandomSeed((ushort)RandomSeed.Next().GetRandom(0).NextInt(ushort.MaxValue)));
            }

            if (snapshot.m_HadElevation)
            {
                RestoreLaneElevation(lane, true, snapshot.m_Elevation);
                RestoreLaneElevation(startNode, true, snapshot.m_Elevation);
                RestoreLaneElevation(endNode, true, snapshot.m_Elevation);
            }

            // Tek sada u popis poteza: da naredna karika lanca nađe OVE
            // čvorove umesto da napravi svoje na istom mestu. Upis ide posle
            // provere arhetipa — odustajanje iznad briše ono što je napravilo.
            if (!startReused)
            {
                m_RecreatedLaneNodes.Add(new RecreatedLaneNode { m_Position = snapshot.m_Curve.a, m_Node = startNode });
            }

            if (!endReused)
            {
                m_RecreatedLaneNodes.Add(new RecreatedLaneNode { m_Position = snapshot.m_Curve.d, m_Node = endNode });
            }

            EntityManager.AddComponent<Updated>(lane);
            EntityManager.AddComponent<BatchesUpdated>(lane);
            RemapHistoryEntity(snapshot.m_Entity, lane);
        }

        private Entity CreateLaneNode(Entity container, NetData netData, LaneSnapshot snapshot, float3 position, float3 tangent)
        {
            NativeArray<Entity> created = EntityManager.CreateEntity(netData.m_NodeArchetype, 1, Allocator.Temp);
            Entity node = created[0];
            created.Dispose();

            if (math.lengthsq(tangent.xz) < 1e-6f)
            {
                tangent = new float3(0f, 0f, 1f);
            }

            EntityManager.SetComponentData(node, new PrefabRef(container));
            if (EntityManager.HasComponent<Game.Net.Node>(node))
            {
                EntityManager.SetComponentData(node, new Game.Net.Node
                {
                    m_Position = position,
                    m_Rotation = Game.Net.NetUtils.GetNodeRotation(tangent),
                });
            }

            if (EntityManager.HasComponent<Game.Tools.EditorContainer>(node))
            {
                EntityManager.SetComponentData(node, new Game.Tools.EditorContainer { m_Prefab = snapshot.m_Prefab });
            }

            if (EntityManager.HasComponent<PseudoRandomSeed>(node) && snapshot.m_HasSeed)
            {
                EntityManager.SetComponentData(node, new PseudoRandomSeed(snapshot.m_Seed));
            }

            EntityManager.AddComponent<Updated>(node);
            return node;
        }

        // Rotacija klipborda (RMB u paste modu): xz ofseti tačaka krive se
        // okreću istom formulom kao poligoni površina.
        private void RotateClipboardLanes(float sin, float cos)
        {
            foreach (LaneClipboardItem item in m_ClipboardLanes)
            {
                if (item.m_CurveOffsets == null)
                {
                    continue;
                }

                for (int k = 0; k < item.m_CurveOffsets.Length; k++)
                {
                    float2 p = item.m_CurveOffsets[k];
                    item.m_CurveOffsets[k] = new float2((p.x * cos) + (p.y * sin), (-p.x * sin) + (p.y * cos));
                }
            }
        }
    }
}
