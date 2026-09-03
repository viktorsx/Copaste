// Pomeranje i rotacija ZGRADA i farbanih površina.
//
// Pristup: zgrada i ceo
// njen pod-tree (prilazi i staze kao pod-mreže, pločnici kao pod-površine,
// instalirane nadogradnje) pomeraju se direktnim upisom u VANILA komponente za
// istu deltu, pa se sve označi Updated — igra sama regeneriše geometriju, trake
// i konekciju na put. Ništa novo se ne upisuje u save. Propove koje igra drži
// na zgradi ne diramo pojedinačno: kad se zgradi promeni Transform i dobije
// Updated, igra ih sama premesti (pozicije su im relativne na prefab).
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
    using UnityEngine.InputSystem;

    public partial class CopasteToolSystem
    {
        // Zgrade u izgradnji se ne pomeraju — construction tok bi se pomešao
        // sa našim pomeranjem pod-elemenata.
        private bool IsMovableBuilding(Entity entity) =>
            IsBuilding(entity) && !EntityManager.HasComponent<Game.Objects.UnderConstruction>(entity);

        // Deljene scratch kolekcije za šetnju pod-tree-a — drag zove
        // TransformBuilding svaki frejm, alokacije po pozivu prave GC pritisak.
        private readonly HashSet<Entity> m_PartsVisited = new HashSet<Entity>();
        private readonly List<Entity> m_PartsScratch = new List<Entity>();

        // Jedna primitiva za sve operacije: rotacija oko pivota u xz ravni
        // (yawDelta može biti 0), pa translacija za delta. Pokriva move drag,
        // nudge, grupnu rotaciju, spin i undo/redo putanju.
        //
        // fullUpdate=false (tokom drag-a): samo upis pozicija + BatchesUpdated,
        // bez Updated okidača — pun Updated (teren, konekcije, re-layout
        // pod-propova, pathfind) svaki frejm tera igru da se "bori" sa nama.
        // Drag ga diže na interval (tick) i finalno kroz SettleBuilding.
        private void TransformBuilding(Entity building, float3 delta, float yawDelta, float3 pivot, bool fullUpdate = true)
        {
            if (!EntityManager.Exists(building) ||
                !EntityManager.TryGetComponent(building, out Game.Objects.Transform transform))
            {
                return;
            }

            quaternion rotation = quaternion.RotateY(yawDelta);
            transform.m_Position = pivot + math.mul(rotation, transform.m_Position - pivot) + delta;
            transform.m_Rotation = math.normalize(math.mul(rotation, transform.m_Rotation));
            EntityManager.SetComponentData(building, transform);
            EntityManager.AddComponent<BatchesUpdated>(building);
            if (fullUpdate)
            {
                EntityManager.AddComponent<Updated>(building);
            }

            m_PartsVisited.Clear();
            m_PartsVisited.Add(building);
            m_PartsScratch.Clear();
            CollectBuildingParts(building, m_PartsVisited, m_PartsScratch, 0);
            foreach (Entity part in m_PartsScratch)
            {
                TransformBuildingPart(part, rotation, pivot, delta, fullUpdate);
            }
        }

        // Undo/redo: vrati zgradu TAČNO na snimljeni transform. Deca idu za
        // izračunatom deltom, a na zgradu se upiše tačna vrednost da se
        // numerička greška ne akumulira kroz undo/redo cikluse.
        private void MoveBuildingTo(Entity building, Game.Objects.Transform target)
        {
            if (!EntityManager.Exists(building) ||
                !EntityManager.TryGetComponent(building, out Game.Objects.Transform current))
            {
                return;
            }

            float yawDelta = YawOf(target.m_Rotation) - YawOf(current.m_Rotation);
            TransformBuilding(building, target.m_Position - current.m_Position, yawDelta, current.m_Position);
            EntityManager.SetComponentData(building, target);
        }

        private static float YawOf(quaternion rotation)
        {
            float3 forward = math.mul(rotation, new float3(0f, 0f, 1f));
            return math.atan2(forward.x, forward.z);
        }

        // Delovi zgrade sa world-space geometrijom koje pomeramo ručno.
        // SubObject deca se preskaču (igra ih sama vodi) osim stubova (Pillar,
        // npr. nadzemne stanice).
        private void CollectBuildingParts(Entity owner, HashSet<Entity> visited, List<Entity> parts, int depth)
        {
            if (depth > 3)
            {
                return;
            }

            // Pod-mreže i pod-površine se obilaze REKURZIVNO — njihovi vlastiti
            // pod-objekti (dekali na prilazu, fleke na placu) inače ostaju na
            // starom mestu: pripadaju površini/mreži, ne zgradi, pa ih ni igrin
            // re-layout zgrade ne premešta.
            if (EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<Game.Net.SubNet> subNets))
            {
                for (int i = 0; i < subNets.Length; i++)
                {
                    if (AddBuildingPart(subNets[i].m_SubNet, visited, parts))
                    {
                        CollectBuildingParts(subNets[i].m_SubNet, visited, parts, depth + 1);
                    }
                }
            }

            if (EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<Game.Net.SubLane> subLanes))
            {
                for (int i = 0; i < subLanes.Length; i++)
                {
                    AddBuildingPart(subLanes[i].m_SubLane, visited, parts);
                }
            }

            if (EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<Game.Areas.SubArea> subAreas))
            {
                for (int i = 0; i < subAreas.Length; i++)
                {
                    if (AddBuildingPart(subAreas[i].m_Area, visited, parts))
                    {
                        CollectBuildingParts(subAreas[i].m_Area, visited, parts, depth + 1);
                    }
                }
            }

            if (EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<Game.Buildings.InstalledUpgrade> upgrades))
            {
                for (int i = 0; i < upgrades.Length; i++)
                {
                    if (AddBuildingPart(upgrades[i].m_Upgrade, visited, parts))
                    {
                        CollectBuildingParts(upgrades[i].m_Upgrade, visited, parts, depth + 1);
                    }
                }
            }

            if (EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<Game.Objects.SubObject> subObjects))
            {
                // Direktne pod-objekte ZGRADE vodi igra (prefab re-layout) —
                // ručno se pomeraju samo stubovi. Ali pod-objekte POVRŠINA i
                // MREŽA (dekali na prilazu/placu) niko drugi ne pomera — idu svi.
                bool ownerIsBuilding = EntityManager.HasComponent<Game.Buildings.Building>(owner) ||
                    EntityManager.HasComponent<Game.Buildings.Extension>(owner);
                for (int i = 0; i < subObjects.Length; i++)
                {
                    Entity sub = subObjects[i].m_SubObject;
                    if ((!ownerIsBuilding || EntityManager.HasComponent<Game.Objects.Pillar>(sub)) &&
                        AddBuildingPart(sub, visited, parts))
                    {
                        CollectBuildingParts(sub, visited, parts, depth + 1);
                    }
                }
            }
        }

        private bool AddBuildingPart(Entity part, HashSet<Entity> visited, List<Entity> parts)
        {
            if (part == Entity.Null || !visited.Add(part) || !EntityManager.Exists(part))
            {
                return false;
            }

            // ConnectionLane je virtuelna veza ka javnoj mreži — igra je sama
            // ponovo uspostavi, pomeranje bi ostavilo duh-trake.
            if (EntityManager.HasComponent<Game.Net.ConnectionLane>(part))
            {
                return false;
            }

            parts.Add(part);
            return true;
        }

        // Primeni rotaciju oko pivota + deltu na jedan deo, po tipu geometrije.
        private void TransformBuildingPart(Entity part, quaternion rotation, float3 pivot, float3 delta, bool fullUpdate = true)
        {
            if (!EntityManager.Exists(part))
            {
                return;
            }

            if (EntityManager.TryGetComponent(part, out Game.Objects.Transform transform))
            {
                transform.m_Position = pivot + math.mul(rotation, transform.m_Position - pivot) + delta;
                transform.m_Rotation = math.normalize(math.mul(rotation, transform.m_Rotation));
                EntityManager.SetComponentData(part, transform);
                EntityManager.AddComponent<BatchesUpdated>(part);
            }

            if (EntityManager.TryGetComponent(part, out Game.Net.Node node))
            {
                node.m_Position = pivot + math.mul(rotation, node.m_Position - pivot) + delta;
                node.m_Rotation = math.normalize(math.mul(rotation, node.m_Rotation));
                EntityManager.SetComponentData(part, node);

                // Ivice koje vise na ovom čvoru (i one ka javnom putu) moraju
                // da se preračunaju da se prilaz ponovo zakači.
                if (fullUpdate &&
                    EntityManager.TryGetBuffer(part, true, out DynamicBuffer<Game.Net.ConnectedEdge> edges))
                {
                    for (int i = 0; i < edges.Length; i++)
                    {
                        if (EntityManager.Exists(edges[i].m_Edge))
                        {
                            EntityManager.AddComponent<Updated>(edges[i].m_Edge);
                        }
                    }
                }
            }

            if (EntityManager.TryGetComponent(part, out Game.Net.Curve curve))
            {
                curve.m_Bezier.a = pivot + math.mul(rotation, curve.m_Bezier.a - pivot) + delta;
                curve.m_Bezier.b = pivot + math.mul(rotation, curve.m_Bezier.b - pivot) + delta;
                curve.m_Bezier.c = pivot + math.mul(rotation, curve.m_Bezier.c - pivot) + delta;
                curve.m_Bezier.d = pivot + math.mul(rotation, curve.m_Bezier.d - pivot) + delta;
                EntityManager.SetComponentData(part, curve);
            }

            if (EntityManager.TryGetBuffer(part, false, out DynamicBuffer<Game.Areas.Node> areaNodes))
            {
                for (int i = 0; i < areaNodes.Length; i++)
                {
                    Game.Areas.Node areaNode = areaNodes[i];
                    areaNode.m_Position = pivot + math.mul(rotation, areaNode.m_Position - pivot) + delta;
                    areaNodes[i] = areaNode;
                }
            }

            if (fullUpdate)
            {
                EntityManager.AddComponent<Updated>(part);
            }
        }

        // Završni "settle" po kraju prevlačenja: pod-tree + SVI pod-objekti
        // (uključujući spawn tačke vozila) dobiju Updated na KONAČNOJ poziciji.
        // Tokom drag-a Updated ide svaki frejm, pa RoadConnectionSystem i
        // pathfind prolaze kroz polupomerena stanja — "No car access" ume da
        // ostane zaglavljen. Jedan koherentan update na kraju to čisti.
        private void SettleBuilding(Entity building)
        {
            if (!EntityManager.Exists(building) || !IsBuilding(building))
            {
                return;
            }

            EntityManager.AddComponent<Updated>(building);
            EntityManager.AddComponent<BatchesUpdated>(building);

            // Stara veza sa putem: edge sa ConnectedBuilding + Updated je okidač
            // za ReplaceRoadConnectionJob — bez ovoga stara veza (i njena
            // "No road access" ikonica) ume da preživi preseljenje.
            if (EntityManager.TryGetComponent(building, out Game.Buildings.Building buildingData) &&
                buildingData.m_RoadEdge != Entity.Null &&
                EntityManager.Exists(buildingData.m_RoadEdge))
            {
                EntityManager.AddComponent<Updated>(buildingData.m_RoadEdge);
            }

            m_PartsVisited.Clear();
            m_PartsVisited.Add(building);
            m_PartsScratch.Clear();
            CollectBuildingParts(building, m_PartsVisited, m_PartsScratch, 0);
            foreach (Entity part in m_PartsScratch)
            {
                if (!EntityManager.Exists(part))
                {
                    continue;
                }

                EntityManager.AddComponent<Updated>(part);
                if (EntityManager.TryGetBuffer(part, true, out DynamicBuffer<Game.Net.ConnectedEdge> edges))
                {
                    for (int i = 0; i < edges.Length; i++)
                    {
                        if (EntityManager.Exists(edges[i].m_Edge))
                        {
                            EntityManager.AddComponent<Updated>(edges[i].m_Edge);
                        }
                    }
                }
            }

            MarkSubObjectsUpdated(building, 0);
            ScheduleSubPropRestore(building);
            SweepBuildingSubElements(building);

            // Drugi, LAKI settle par frejmova kasnije: prvi Updated ide u istom
            // frejmu kad i pomeranje, pa RoadConnectionSystem proverava nad
            // search stablima koja još ne vide pomerene prilaze — provera
            // promaši i "No car access" ume da ostane. Ponovni okidač sa svežim
            // stablima to čisti.
            m_DelayedSettle[building] = 4;
        }

        private readonly Dictionary<Entity, int> m_DelayedSettle = new Dictionary<Entity, int>();
        private readonly List<Entity> m_DelayedSettleScratch = new List<Entity>();

        private void RunDelayedSettles()
        {
            if (m_DelayedSettle.Count == 0)
            {
                return;
            }

            m_DelayedSettleScratch.Clear();
            m_DelayedSettleScratch.AddRange(m_DelayedSettle.Keys);
            foreach (Entity building in m_DelayedSettleScratch)
            {
                int frames = m_DelayedSettle[building] - 1;
                if (frames > 0)
                {
                    m_DelayedSettle[building] = frames;
                    continue;
                }

                m_DelayedSettle.Remove(building);
                if (!EntityManager.Exists(building))
                {
                    m_DelayedSettleSecondDone.Remove(building);
                    continue;
                }

                EntityManager.AddComponent<Updated>(building);
                EntityManager.AddComponent<BatchesUpdated>(building);
                bool hasRoadEdge = EntityManager.TryGetComponent(building, out Game.Buildings.Building buildingData) &&
                    buildingData.m_RoadEdge != Entity.Null &&
                    EntityManager.Exists(buildingData.m_RoadEdge);
                if (hasRoadEdge)
                {
                    EntityManager.AddComponent<Updated>(buildingData.m_RoadEdge);
                }

                // Ovaj Updated okida i prefab re-layout pod-propova — restore
                // prozor se otvara ponovo da igračev raspored pregazi šablon
                // (radi samo za zgrade sa živim snimkom — captured gate).
                ScheduleSubPropRestore(building);
                SweepBuildingSubElements(building);

                // Drugi "tap" pola sekunde kasnije: teleport (undo/redo) menja
                // sve u jednom frejmu, pa prvi re-check ume da prođe pre nego
                // što igra obnovi trake/pathfind — druga runda hvata i to.
                if (m_DelayedSettleSecondDone.Add(building))
                {
                    m_DelayedSettle[building] = 26;
                }
                else
                {
                    m_DelayedSettleSecondDone.Remove(building);

                    // Finalni tap: probudi SVE mrežne ivice u okolini (putevi
                    // I pešačke staze) — kolski pristup pokriva m_RoadEdge,
                    // ali pešački ide preko okolnih trotoara koje ništa drugo
                    // ne bi nateralo na ponovnu proveru.
                    MarkNearbyNetsUpdated(building);
                }

                Mod.Log.Info($"Copaste: delayed settle e{building.Index} roadEdge={(hasRoadEdge ? "ok" : "NULL")} secondTap={m_DelayedSettle.ContainsKey(building)}");
            }
        }

        private readonly HashSet<Entity> m_DelayedSettleSecondDone = new HashSet<Entity>();

        // Označi Updated sve mrežne ivice u radijusu oko zgrade (plac + 16 m):
        // tera igru da preračuna kolske I pešačke veze sa svežim stanjem.
        private void MarkNearbyNetsUpdated(Entity building)
        {
            if (!EntityManager.TryGetComponent(building, out Game.Objects.Transform transform))
            {
                return;
            }

            float radius = 24f;
            if (EntityManager.TryGetComponent(building, out PrefabRef prefabRef) &&
                EntityManager.TryGetComponent(prefabRef.m_Prefab, out BuildingData buildingData) &&
                buildingData.m_LotSize.x > 0)
            {
                radius = (math.max(buildingData.m_LotSize.x, buildingData.m_LotSize.y) * 4f) + 16f;
            }

            if (m_NetSearchSystem == null)
            {
                m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            }

            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle dependencies);
            dependencies.Complete();

            RoadIterator iterator = new RoadIterator
            {
                m_Bounds = new Bounds3(
                    transform.m_Position - new float3(radius, 1000f, radius),
                    transform.m_Position + new float3(radius, 1000f, radius)),
                m_Results = new NativeList<Entity>(32, Allocator.Temp),
            };
            tree.Iterate(ref iterator, 0);

            for (int i = 0; i < iterator.m_Results.Length; i++)
            {
                Entity candidate = iterator.m_Results[i];
                if (EntityManager.HasComponent<Game.Net.Edge>(candidate) &&
                    !EntityManager.HasComponent<Temp>(candidate) &&
                    !EntityManager.HasComponent<Deleted>(candidate) &&
                    GetOwnerRootBuilding(candidate) != building)
                {
                    // Sopstveni prilazi zgrade se preskaču — njihov Updated bi
                    // okinuo JOŠ jednu regeneraciju pod-elemenata (tap služi
                    // okolnim trotoarima/putevima, ne zgradi samoj).
                    EntityManager.AddComponent<Updated>(candidate);
                }
            }

            iterator.m_Results.Dispose();
        }

        // Pod-objekti (propovi, spawn tačke) se ne pomeraju ručno, ali im treba
        // Updated da SpawnLocationConnectionSystem i saradnici preračunaju veze.
        private void MarkSubObjectsUpdated(Entity owner, int depth)
        {
            if (depth > 3 ||
                !EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<Game.Objects.SubObject> subObjects))
            {
                return;
            }

            // Popis PRE mutacija: vlasnik i dete su oba Game.Objects.Object
            // sa Owner+SubObject, pa realno dele chunk — AddComponent na dete
            // ume da pomeri i vlasnika, a bafer se u petlji i dalje čita.
            List<Entity> children = new List<Entity>(subObjects.Length);
            for (int i = 0; i < subObjects.Length; i++)
            {
                children.Add(subObjects[i].m_SubObject);
            }

            foreach (Entity sub in children)
            {
                if (EntityManager.Exists(sub))
                {
                    EntityManager.AddComponent<Updated>(sub);
                    MarkSubObjectsUpdated(sub, depth + 1);
                }
            }
        }

        // ---------- Road snap za paste zgrada ----------
        //
        // Kad je uključen i clipboard sadrži zgradu, sidro-zgrada (najbliža
        // centru grupe) se lepi na najbližu ivicu puta kao kod običnog
        // plopovanja: front placa uz ivicu puta, rotacija licem ka putu,
        // cela grupa prati kruto. Putevi se traže kroz igrino net search
        // stablo (quad tree) u radijusu oko kursora.

        private const float kRoadSnapRadius = 30f;

        private Game.Net.SearchSystem m_NetSearchSystem;
        private int m_SnapAnchorIndex = -1;
        private bool m_SnapEngaged;
        private bool m_SnapHasResult;
        private float3 m_SnapLastSearchPos;
        private Entity m_SnapEdge;

        // Dijagnostika promašaja snap pretrage (prijava: ne radi tokom pauze) —
        // jedan red po PROMENI stanja, ne po frejmu.
        private string m_LastSnapMissDiag;

        private static bool RoadSnapEnabled => Mod.Settings != null && Mod.Settings.RoadSnapPaste;

        // Da li snap trenutno drži grupu (gate za ručnu RMB rotaciju u paste modu).
        private bool RoadSnapEngaged => m_SnapEngaged;

        // Poziva se na ulazu u paste mod i pri promeni clipboard-a.
        private void ResetRoadSnap()
        {
            m_SnapAnchorIndex = FindSnapAnchorIndex();
            m_SnapHasResult = false;
            m_SnapEngaged = false;
            m_SnapEdge = Entity.Null;
        }

        // Sidro grupe = zgrada najbliža centru selekcije (najmanji ofset).
        private int FindSnapAnchorIndex()
        {
            int best = -1;
            float bestLength = float.MaxValue;
            for (int i = 0; i < m_Clipboard.Count; i++)
            {
                if (!EntityManager.Exists(m_Clipboard[i].m_Prefab) ||
                    !EntityManager.TryGetComponent(m_Clipboard[i].m_Prefab, out BuildingData _))
                {
                    continue;
                }

                float length = math.lengthsq(m_Clipboard[i].m_Offset.xz);
                if (length < bestLength)
                {
                    bestLength = length;
                    best = i;
                }
            }

            return best;
        }

        // Deljena snap računica (paste i relocate): za dati prefab zgrade i
        // kursor vraća centar placa uz najbliži put i yaw licem ka putu.
        private bool TryComputeRoadSnap(float3 cursor, Entity buildingPrefab, out float3 center, out float targetYaw)
        {
            center = default;
            targetYaw = 0f;

            // Dubina placa PRE pretrage: kod velike zgrade kursor (centar)
            // legitimno stoji i 40+ m od ose puta kad je zgrada savršeno uz
            // put — fiksni domet od 30 m je baš njima odbijao snap
            // (promašaji u logu na 30-38 m).
            float halfDepth = 8f;
            if (EntityManager.TryGetComponent(buildingPrefab, out BuildingData buildingData) &&
                buildingData.m_LotSize.y > 0)
            {
                halfDepth = buildingData.m_LotSize.y * 4f;
            }

            // Pretraga stabla samo kad se kursor stvarno pomeri.
            if (!m_SnapHasResult || math.distancesq(cursor.xz, m_SnapLastSearchPos.xz) > 0.25f)
            {
                m_SnapLastSearchPos = cursor;
                m_SnapHasResult = TryFindNearestRoad(cursor, kRoadSnapRadius + halfDepth, out m_SnapEdge);
            }

            if (!m_SnapHasResult)
            {
                return false;
            }

            // Keširana ivica ume da UMRE između dva frejma: postavljanje/brisanje
            // zgrade tera igru da preseče i ponovo spoji okolne ivice puta.
            // Bez ovoga bi keš vraćao "nema puta" dok se kursor ne pomeri.
            if (!EntityManager.Exists(m_SnapEdge) ||
                !EntityManager.TryGetComponent(m_SnapEdge, out Game.Net.Curve curve))
            {
                m_SnapHasResult = false;
                m_SnapEdge = Entity.Null;
                return false;
            }

            // Najbliža tačka na krivoj prati kursor duž istog puta.
            MathUtils.Distance(curve.m_Bezier, cursor, out float t);
            float3 curvePoint = MathUtils.Position(curve.m_Bezier, t);
            float3 tangent = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, t), new float3(0f, 0f, 1f));

            // Normala na put ka strani na kojoj je kursor.
            float3 side = new float3(tangent.z, 0f, -tangent.x);
            if (math.dot(cursor - curvePoint, side) < 0f)
            {
                side = -side;
            }

            float halfWidth = 8f;
            if (EntityManager.TryGetComponent(m_SnapEdge, out PrefabRef edgePrefab) &&
                EntityManager.TryGetComponent(edgePrefab.m_Prefab, out NetGeometryData geometry))
            {
                halfWidth = geometry.m_DefaultWidth * 0.5f;
            }

            // Centar zgrade: od ivice puta ka kursoru, front placa uz put;
            // front zgrade (lokalni +z) gleda ka putu.
            center = curvePoint + (side * (halfWidth + halfDepth));
            targetYaw = math.atan2(-side.x, -side.z);
            return true;
        }

        // Prilagodi paste sidro tako da sidro-zgrada legne uz najbliži put;
        // rotira ceo clipboard da zgrada gleda ka putu. Vraća (možda) pomereno sidro.
        private float3 ApplyRoadSnap(float3 anchor)
        {
            m_SnapEngaged = false;
            if (!RoadSnapEnabled || m_SnapAnchorIndex < 0 || m_SnapAnchorIndex >= m_Clipboard.Count)
            {
                return anchor;
            }

            ClipboardItem anchorItem = m_Clipboard[m_SnapAnchorIndex];
            if (!TryComputeRoadSnap(anchor, anchorItem.m_Prefab, out float3 center, out float targetYaw))
            {
                return anchor;
            }

            float deltaYaw = targetYaw - YawOf(anchorItem.m_Rotation);
            deltaYaw = math.atan2(math.sin(deltaYaw), math.cos(deltaYaw));
            if (math.abs(deltaYaw) > 0.001f)
            {
                RotateClipboard(deltaYaw);
                anchorItem = m_Clipboard[m_SnapAnchorIndex];
            }

            m_SnapEngaged = true;
            float3 snapped = center - anchorItem.m_Offset;
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            snapped.y = TerrainUtils.SampleHeight(ref heightData, snapped);
            return snapped;
        }

        // ---------- Relocate mod ----------
        //
        // Selektovana zgrada prati kursor kao kod vanila premeštanja: road
        // snap je lepi uz put (isti toggle kao paste), klik je spušta uz
        // završni settle, RMB/ESC vraća na polaznu poziciju.

        private Entity m_RelocateEntity = Entity.Null;
        private Game.Objects.Transform m_RelocateOriginal;
        private bool m_RelocateSnapLogged;

        // Zapis koji je TriggerRelocate gurnuo — cancel ga skida po referenci,
        // nikad "šta god je na vrhu" (panel akcije mogu da promene stek).
        private UndoRecord m_RelocateUndoRecord;

        // Redo stek sačuvan na ulazu u relocate: PushUndo ga briše, a odustali
        // relocate je no-op — Tab+Tab ne sme da obriše redo istoriju.
        private List<UndoRecord> m_RelocateRedoBackup;

        public bool IsRelocating => m_Mode == Mode.Relocate;

        // Dugme radi kad je selektovana tačno jedna gotova zgrada.
        public bool CanRelocate =>
            m_Mode == Mode.Select &&
            m_Selected.Count == 1 &&
            m_SelectedSurfaces.Count == 0 &&
            m_SelectedLanes.Count == 0 &&
            m_SelectedNodes.Count == 0 &&
            m_SelectedNetEdges.Count == 0 &&
            IsBuilding(m_Selected[0]) &&
            IsMovableBuilding(m_Selected[0]);

        public void TriggerRelocate()
        {
            if (!ToolIsActive)
            {
                return;
            }

            // Klik na dugme tokom premeštanja = odustani (isto kao RMB).
            if (m_Mode == Mode.Relocate)
            {
                CancelRelocate();
                return;
            }

            if (!CanRelocate ||
                !EntityManager.TryGetComponent(m_Selected[0], out Game.Objects.Transform transform))
            {
                // Trag u logu umesto tihog odbijanja — najčešće: zgrada iz
                // sveže vraćenog undo-a još "gradi" (UnderConstruction traje
                // frejm-dva, uz pauziranu simulaciju i duže).
                if (m_Selected.Count == 1 && IsBuilding(m_Selected[0]) &&
                    EntityManager.HasComponent<Game.Objects.UnderConstruction>(m_Selected[0]))
                {
                    Mod.Log.Info("Copaste: relocate refused — building still under construction");
                }

                return;
            }

            EndAlignSession();

            // Isti skup flegova koji čisti EnterPasteMode. Dok traje Relocate
            // UpdateSelectMode ne radi, pa se puštanje tastera nikad ne vidi:
            // pritisak zadržan pre Tab-a bi posle spuštanja zgrade odradio
            // pik na ustajaloj tački (ili obrisao selekciju), a u varijanti
            // sa m_LeftHeldOnProp pokrenuo netraženo prevlačenje tek
            // spuštene zgrade, sa sopstvenim undo zapisom.
            CancelMarquee();
            m_MarqueeHeld = false;
            m_LeftHeldOnProp = false;
            m_MoveDragging = false;
            m_MoveOffsetsPending = false;
            m_LeftPressEntity = Entity.Null;
            m_LeftPressShift = false;
            m_LeftPressAlt = false;
            m_HeightPickArmed = false;
            m_AlignPickArmed = false;

            m_RelocateEntity = m_Selected[0];
            m_RelocateOriginal = transform;

            // Undo zapis + snimak rasporeda pod-propova idu na ulazu; redo stek
            // se stavlja na stranu — odustajanje ga vraća netaknutog.
            m_RelocateRedoBackup = m_RedoStack.Count > 0 ? new List<UndoRecord>(m_RedoStack) : null;

            // Sopstveni zapis se prepoznaje poređenjem VRHA steka pre/posle —
            // brojanje ne radi na punom steku (PushUndo tada izbaci najstariji
            // pa Count ostane isti).
            UndoRecord topBefore = m_UndoStack.Count > 0 ? m_UndoStack[m_UndoStack.Count - 1] : null;
            PushTransformUndo();
            UndoRecord topAfter = m_UndoStack.Count > 0 ? m_UndoStack[m_UndoStack.Count - 1] : null;
            m_RelocateUndoRecord = !ReferenceEquals(topBefore, topAfter) ? topAfter : null;
            m_SnapHasResult = false;
            m_SnapEdge = Entity.Null;
            m_RelocateSnapLogged = false;
            m_Mode = Mode.Relocate;
        }

        private void CancelRelocate()
        {
            MoveBuildingTo(m_RelocateEntity, m_RelocateOriginal);
            SettleBuilding(m_RelocateEntity);
            SweepOrphansAround(m_RelocateOriginal.m_Position, 48f);

            // Ništa se nije promenilo — SVOJ zapis se skida po referenci
            // (ako ga je neko u međuvremenu potrošio, Remove je bezopasan no-op).
            if (m_RelocateUndoRecord != null)
            {
                m_UndoStack.Remove(m_RelocateUndoRecord);
                m_RelocateUndoRecord = null;
            }

            // Ništa se nije desilo — redo stek se vraća kakav je bio pre ulaska.
            if (m_RelocateRedoBackup != null)
            {
                m_RedoStack.Clear();
                m_RedoStack.AddRange(m_RelocateRedoBackup);
                m_RelocateRedoBackup = null;
            }

            m_RelocateEntity = Entity.Null;
            m_Mode = Mode.Select;
        }

        private void UpdateRelocateMode()
        {
            if (m_RelocateEntity == Entity.Null || !EntityManager.Exists(m_RelocateEntity))
            {
                // Zgrada je nestala usred premeštanja — ista knjigovodstvena
                // obaveza kao CancelRelocate: svoj zapis dole, redo stek nazad.
                if (m_RelocateUndoRecord != null)
                {
                    m_UndoStack.Remove(m_RelocateUndoRecord);
                    m_RelocateUndoRecord = null;
                }

                if (m_RelocateRedoBackup != null)
                {
                    m_RedoStack.Clear();
                    m_RedoStack.AddRange(m_RelocateRedoBackup);
                    m_RelocateRedoBackup = null;
                }

                m_RelocateEntity = Entity.Null;
                m_Mode = Mode.Select;
                return;
            }

            UpdateRightButton(out float _, out bool rightClick);
            bool escape = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;

            // Tab (relocate keybind) tokom premeštanja = odustani, kao dugme.
            bool relocateKey = m_RelocateAction.WasPressedThisFrame() && !m_UiTyping;
            if (escape || rightClick || relocateKey)
            {
                CancelRelocate();
                return;
            }

            if (!GetRaycastResult(out Entity rayEntity, out RaycastHit hit) ||
                !EntityManager.TryGetComponent(m_RelocateEntity, out Game.Objects.Transform current))
            {
                return;
            }

            float3 pointer = hit.m_HitPosition;

            // VISOKA zgrada pod kursorom: zrak pogodi NJU (ili njen pod-objekat)
            // dok je pomeramo, a pogodak na krovu je zbog ugla kamere desetine
            // metara od tačke na tlu — zgrada bi jurila sopstveni krov i
            // skakala, a snap bi tražio put na pogrešnom mestu (log: nearest
            // 57–142 m, pa i tree=0). Tada se kursor projektuje na ravan
            // terena na visini zgrade — isti trik kao kod ručki na mostu.
            if (rayEntity != Entity.Null && RelocateRayHitSelf(rayEntity))
            {
                TerrainHeightData selfHitTerrain = m_TerrainSystem.GetHeightData();
                float planeY = TerrainUtils.SampleHeight(ref selfHitTerrain, current.m_Position);
                if (!TryCursorAtHeight(planeY, out float2 flatCursor))
                {
                    return;
                }

                pointer = new float3(flatCursor.x, planeY, flatCursor.y);
            }

            float3 target = pointer;
            float yawDelta = 0f;
            bool snapped = false;
            if (RoadSnapEnabled &&
                EntityManager.TryGetComponent(m_RelocateEntity, out PrefabRef prefabRef) &&
                TryComputeRoadSnap(pointer, prefabRef.m_Prefab, out float3 snapCenter, out float targetYaw))
            {
                snapped = true;
                target = snapCenter;
                float delta = targetYaw - YawOf(current.m_Rotation);
                yawDelta = math.atan2(math.sin(delta), math.cos(delta));
            }

            // Dijagnostika: jedan red po promeni snap stanja, ne po frejmu.
            if (snapped != m_RelocateSnapLogged)
            {
                m_RelocateSnapLogged = snapped;
                Mod.Log.Info($"Copaste: relocate snap {(snapped ? "engaged" : "released")} (roadSnap={RoadSnapEnabled})");
            }

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            target.y = TerrainUtils.SampleHeight(ref heightData, target);

            float3 moveDelta = target - current.m_Position;
            if (math.lengthsq(moveDelta) > 1e-6f || math.abs(yawDelta) > 0.001f)
            {
                // Pun update svaki frejm — glatko praćenje kursora (tick
                // varijanta je pravila trzav pomeraj i loš snap).
                // Siročiće i zaostale fabrike (stari plac/prilaz) koje churn
                // napravi čiste sweep-ovi na spuštanju.
                TransformBuilding(m_RelocateEntity, moveDelta, yawDelta, current.m_Position, true);
            }

            // Klik: spusti zgradu — završni settle pa nazad u selekciju.
            if (ClickedThisFrame())
            {
                SettleBuilding(m_RelocateEntity);

                // Siročići vise na STAROJ lokaciji — počisti krug oko nje.
                SweepOrphansAround(m_RelocateOriginal.m_Position, 48f);

                if (!m_SoundQuery.IsEmptyIgnoreFilter)
                {
                    m_AudioManager.PlayUISound(m_SoundQuery.GetSingleton<ToolUXSoundSettingsData>().m_PlacePropSound);
                }

                m_RelocateEntity = Entity.Null;
                m_RelocateUndoRecord = null;

                // Zgrada je stvarno premeštena — sečenje redo grane važi.
                m_RelocateRedoBackup = null;
                m_Mode = Mode.Select;
            }
        }

        // Da li raycast pogodak pripada zgradi koja se upravo premešta —
        // direktno ili kroz lanac vlasništva (pod-prop, pod-mreža placa).
        private bool RelocateRayHitSelf(Entity hitEntity)
        {
            Entity cursorEntity = hitEntity;
            for (int depth = 0; depth < 6 && cursorEntity != Entity.Null; depth++)
            {
                if (cursorEntity == m_RelocateEntity)
                {
                    return true;
                }

                if (!EntityManager.TryGetComponent(cursorEntity, out Owner owner))
                {
                    return false;
                }

                cursorEntity = owner.m_Owner;
            }

            return false;
        }

        private bool TryFindNearestRoad(float3 position, float radius, out Entity edge)
        {
            edge = Entity.Null;
            if (m_NetSearchSystem == null)
            {
                m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            }

            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle dependencies);
            dependencies.Complete();

            RoadIterator iterator = new RoadIterator
            {
                m_Bounds = new Bounds3(
                    position - new float3(radius, 1000f, radius),
                    position + new float3(radius, 1000f, radius)),
                m_Results = new NativeList<Entity>(16, Allocator.Temp),
            };
            tree.Iterate(ref iterator, 0);

            float bestDistance = radius;
            for (int i = 0; i < iterator.m_Results.Length; i++)
            {
                Entity candidate = iterator.m_Results[i];
                if (!EntityManager.HasComponent<Game.Net.Road>(candidate) ||
                    EntityManager.HasComponent<Owner>(candidate) ||
                    EntityManager.HasComponent<Temp>(candidate) ||
                    EntityManager.HasComponent<Deleted>(candidate) ||
                    !EntityManager.TryGetComponent(candidate, out Game.Net.Curve curve))
                {
                    continue;
                }

                float distance = MathUtils.Distance(curve.m_Bezier, position, out float _);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    edge = candidate;
                }
            }

            // Dijagnostika promašaja (prijava: snap "slabiji" tokom pauze) —
            // jedan red po epizodi promašaja (prva pretraga bez puta posle
            // uspešne), ne po svakom pomaku kursora: brojevi se menjaju duž
            // putanje pa bi dedup po sadržaju spamovao log.
            if (edge == Entity.Null)
            {
                if (m_LastSnapMissDiag == null)
                {
                    int roadCandidates = 0;
                    float nearest = float.MaxValue;
                    for (int i = 0; i < iterator.m_Results.Length; i++)
                    {
                        if (EntityManager.HasComponent<Game.Net.Road>(iterator.m_Results[i]) &&
                            !EntityManager.HasComponent<Owner>(iterator.m_Results[i]) &&
                            EntityManager.TryGetComponent(iterator.m_Results[i], out Game.Net.Curve diagCurve))
                        {
                            roadCandidates++;
                            nearest = math.min(nearest, MathUtils.Distance(diagCurve.m_Bezier, position, out float _));
                        }
                    }

                    m_LastSnapMissDiag = $"miss tree={iterator.m_Results.Length} roads={roadCandidates} nearest={(nearest == float.MaxValue ? "-" : nearest.ToString("F1"))}";
                    Mod.Log.Info($"Copaste: road snap {m_LastSnapMissDiag}");
                }
            }
            else
            {
                m_LastSnapMissDiag = null;
            }

            iterator.m_Results.Dispose();
            return edge != Entity.Null;
        }

        private struct RoadIterator : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds3 m_Bounds;
            public NativeList<Entity> m_Results;

            public bool Intersect(QuadTreeBoundsXZ bounds)
            {
                return MathUtils.Intersect(bounds.m_Bounds, m_Bounds);
            }

            public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
            {
                if (MathUtils.Intersect(bounds.m_Bounds, m_Bounds))
                {
                    m_Results.Add(item);
                }
            }
        }

        // ---------- Sesijski registar OBRISANIH pod-elemenata ----------
        //
        // Igra regeneriše pod-propove/dekale po prefab šablonu kad god se
        // zgrada, prilaz ili plac osveži — i to može da okine bilo šta, ne
        // samo naše operacije. Registar pamti šta je igrač obrisao (prefab +
        // LOKALNA pozicija na zgradi — invarijantna na pomeranje) pa capture
        // regenerisane ne unosi u snimak, a prune ih briše kad god se pojave.
        // Samo u memoriji: save ostaje čist; posle učitavanja igre šablon se
        // vrati, pa se brisanje po potrebi ponovi jednom.

        private struct DeletedSubSig
        {
            public Entity m_Prefab;
            public float3 m_LocalPos;
        }

        private readonly Dictionary<Entity, List<DeletedSubSig>> m_DeletedSubProps = new Dictionary<Entity, List<DeletedSubSig>>();

        // Koren lanca vlasnika (zgrada) ili Entity.Null.
        private Entity GetOwnerRootBuilding(Entity entity)
        {
            Entity current = entity;
            for (int hop = 0; hop < 4; hop++)
            {
                if (!EntityManager.TryGetComponent(current, out Owner owner))
                {
                    return Entity.Null;
                }

                if (IsBuilding(owner.m_Owner))
                {
                    return owner.m_Owner;
                }

                current = owner.m_Owner;
            }

            return Entity.Null;
        }

        // Zove se PRE brisanja entiteta koji pripada zgradi.
        private void RecordDeletedSubProp(Entity entity)
        {
            Entity building = GetOwnerRootBuilding(entity);
            if (building == Entity.Null ||
                !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform) ||
                !EntityManager.TryGetComponent(entity, out PrefabRef prefabRef) ||
                !EntityManager.TryGetComponent(building, out Game.Objects.Transform buildingTransform))
            {
                return;
            }

            // Degenerativna zaštita od neograničenog rasta kroz maraton sesiju.
            if (m_DeletedSubProps.Count > 256)
            {
                m_DeletedSubProps.Clear();
            }

            if (!m_DeletedSubProps.TryGetValue(building, out List<DeletedSubSig> list))
            {
                list = new List<DeletedSubSig>();
                m_DeletedSubProps[building] = list;
            }

            quaternion inverse = math.inverse(buildingTransform.m_Rotation);
            list.Add(new DeletedSubSig
            {
                m_Prefab = prefabRef.m_Prefab,
                m_LocalPos = math.mul(inverse, transform.m_Position - buildingTransform.m_Position),
            });
        }

        // Da li je sub regenerisana kopija nečega što je igrač obrisao?
        private bool MatchesDeletedSubProp(Entity building, Entity sub)
        {
            if (!m_DeletedSubProps.TryGetValue(building, out List<DeletedSubSig> list) ||
                !EntityManager.TryGetComponent(sub, out Game.Objects.Transform transform) ||
                !EntityManager.TryGetComponent(sub, out PrefabRef prefabRef) ||
                !EntityManager.TryGetComponent(building, out Game.Objects.Transform buildingTransform))
            {
                return false;
            }

            float3 local = math.mul(math.inverse(buildingTransform.m_Rotation), transform.m_Position - buildingTransform.m_Position);
            foreach (DeletedSubSig sig in list)
            {
                if (sig.m_Prefab == prefabRef.m_Prefab && math.distancesq(local, sig.m_LocalPos) < 1f)
                {
                    return true;
                }
            }

            return false;
        }

        // Undo brisanja vraća element — njegov potpis se skida iz registra.
        private void ForgetDeletedSubProp(Entity entity)
        {
            Entity building = GetOwnerRootBuilding(entity);
            if (building == Entity.Null ||
                !m_DeletedSubProps.TryGetValue(building, out List<DeletedSubSig> list) ||
                !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform) ||
                !EntityManager.TryGetComponent(entity, out PrefabRef prefabRef) ||
                !EntityManager.TryGetComponent(building, out Game.Objects.Transform buildingTransform))
            {
                return;
            }

            float3 local = math.mul(math.inverse(buildingTransform.m_Rotation), transform.m_Position - buildingTransform.m_Position);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].m_Prefab == prefabRef.m_Prefab && math.distancesq(local, list[i].m_LocalPos) < 1f)
                {
                    list.RemoveAt(i);
                }
            }

            if (list.Count == 0)
            {
                m_DeletedSubProps.Remove(building);
            }
        }

        // Lanac vlasnika sadrži MRTAV entitet — siroče: nastaje kad igra u
        // toku pomeranja ponovo izgradi prilaz/plac pa stari primerak ispadne
        // iz bafera (igrin log: "Owner has no SubObject"). Niko ga više ne
        // vodi ni premešta — ostaje da visi gde je zatečen.
        private bool HasDeadOwnerChain(Entity entity)
        {
            Entity current = entity;
            for (int hop = 0; hop < 4; hop++)
            {
                if (!EntityManager.TryGetComponent(current, out Owner owner))
                {
                    return false;
                }

                if (!EntityManager.Exists(owner.m_Owner))
                {
                    return true;
                }

                current = owner.m_Owner;
            }

            return false;
        }

        // Počisti siročiće u krugu oko date pozicije (stara lokacija zgrade
        // posle pomeranja/relocate-a — tu ostaju da vise).
        private void SweepOrphansAround(float3 position, float radius)
        {
            float radiusSq = radius * radius;
            int swept = 0;

            NativeArray<Entity> entities = m_OwnedPropQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms = m_OwnedPropQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (math.distancesq(transforms[i].m_Position, position) > radiusSq ||
                    !HasDeadOwnerChain(entities[i]) ||
                    !IsPrunableSubProp(entities[i]))
                {
                    continue;
                }

                EntityManager.AddComponent<Deleted>(entities[i]);
                swept++;
            }

            entities.Dispose();
            transforms.Dispose();

            if (swept > 0)
            {
                Mod.Log.Info($"Copaste: swept {swept} orphaned sub-elements near old position");
            }
        }

        // Sweep po UPITU, ne po baferima: igra pri re-layoutu ume da otkači
        // stare primerke iz bafera (novi entiteti nastanu, stari ostanu
        // siročići na staroj poziciji, nedostižni šetnjom kroz bafere).
        // Ovde se hvataju SVI objekti čiji lanac vlasnika vodi do zgrade:
        // - potpis iz registra obrisanih → briši (regenerisan primerak);
        // - daleko od zgrade, a nije u snimku rasporeda → siroče od pomeranja, briši.
        private void SweepBuildingSubElements(Entity building)
        {
            if (!EntityManager.Exists(building))
            {
                return;
            }

            int swept = 0;
            NativeArray<Entity> entities = m_OwnedPropQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (GetOwnerRootBuilding(entities[i]) != building)
                {
                    continue;
                }

                // SAMO potpisi iz registra obrisanih — ništa heuristički.
                // Raniji "daleko od zgrade" i "fabrika" kriterijumi su brisali
                // legitimne delove koje igra iznova gradi, pa je broj obrisanih
                // po potezu eskalirao (1→58→110).
                if (MatchesDeletedSubProp(building, entities[i]))
                {
                    EntityManager.AddComponent<Deleted>(entities[i]);
                    swept++;
                }
            }

            entities.Dispose();

            if (swept > 0)
            {
                Mod.Log.Info($"Copaste: swept {swept} stray/regenerated sub-elements of e{building.Index}");
            }
        }

        // ---------- Očuvanje custom rasporeda pod-propova ----------
        //
        // Igra na Updated zgrade ponovo rasporedi njene pod-objekte po PREFAB
        // šablonu — klupa koju je igrač pomerio u dvorištu vratila bi se na
        // fabričko mesto. Zato se na POČETKU operacije snimi relativan raspored
        // pod-propova u odnosu na zgradu, a posle operacije se nekoliko
        // frejmova nameće nazad (igrin re-layout stigne frejm-dva kasnije).

        private struct SubPropRel
        {
            public Entity m_Building;
            public float3 m_LocalPos;
            public quaternion m_LocalRot;
        }

        private readonly Dictionary<Entity, SubPropRel> m_SubPropRel = new Dictionary<Entity, SubPropRel>();
        private readonly Dictionary<Entity, Game.Objects.Transform> m_SubPropFix = new Dictionary<Entity, Game.Objects.Transform>();
        private readonly HashSet<Entity> m_SubPropFixBuildings = new HashSet<Entity>();

        // Zgrade čiji je raspored STVARNO snimljen u tekućoj operaciji.
        // Restore/prune smeju da rade SAMO nad njima — zgrada bez snimka bi
        // kroz prune izgubila celo dvorište.
        private readonly HashSet<Entity> m_SubPropCaptured = new HashSet<Entity>();
        private readonly List<Entity> m_SubPropScratch = new List<Entity>();
        private int m_SubPropFixFrames;

        // Novi op počinje — stari fix prozor i snimljeni raspored ne važe.
        // OBAVEZNO zajedno: prune sa svežim (praznim) rel skupom bi obrisao
        // legitimne pod-propove prethodne zgrade.
        private void ResetSubPropTracking()
        {
            // Snimci zgrada koje još čekaju odloženi settle preživljavaju —
            // taj settle će pustiti prefab re-layout, pa mora da ima čime da
            // vrati igračev raspored.
            if (m_DelayedSettle.Count == 0)
            {
                m_SubPropRel.Clear();
                m_SubPropCaptured.Clear();
            }
            else
            {
                m_SubPropScratch.Clear();
                m_SubPropScratch.AddRange(m_SubPropRel.Keys);
                foreach (Entity sub in m_SubPropScratch)
                {
                    if (!m_DelayedSettle.ContainsKey(m_SubPropRel[sub].m_Building))
                    {
                        m_SubPropRel.Remove(sub);
                    }
                }

                m_SubPropCaptured.RemoveWhere(building => !m_DelayedSettle.ContainsKey(building));
            }

            m_SubPropFix.Clear();
            m_SubPropFixBuildings.Clear();
            m_SubPropFixFrames = 0;
        }

        private void CaptureSubPropLayout(Entity building)
        {
            if (!EntityManager.Exists(building) ||
                !EntityManager.TryGetComponent(building, out Game.Objects.Transform transform))
            {
                return;
            }

            // I zgrada bez ijednog pod-propa je "snimljena" — prazan raspored
            // je validan (igrač je sve obrisao) i prune sme da radi nad njom.
            m_SubPropCaptured.Add(building);

            quaternion inverse = math.inverse(transform.m_Rotation);
            CaptureSubPropsRecursive(building, building, transform.m_Position, inverse, 0);
        }

        private void CaptureSubPropsRecursive(Entity building, Entity owner, float3 buildingPos, quaternion inverseRot, int depth)
        {
            if (depth > 3)
            {
                return;
            }

            if (EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<Game.Objects.SubObject> subObjects))
            {
                for (int i = 0; i < subObjects.Length; i++)
                {
                    Entity sub = subObjects[i].m_SubObject;
                    if (!EntityManager.Exists(sub) ||
                        !EntityManager.TryGetComponent(sub, out Game.Objects.Transform subTransform))
                    {
                        continue;
                    }

                    // Regenerisana kopija obrisanog elementa se ne unosi
                    // u snimak — prune je skida čim prozor proradi.
                    if (MatchesDeletedSubProp(building, sub))
                    {
                        continue;
                    }

                    m_SubPropRel[sub] = new SubPropRel
                    {
                        m_Building = building,
                        m_LocalPos = math.mul(inverseRot, subTransform.m_Position - buildingPos),
                        m_LocalRot = math.mul(inverseRot, subTransform.m_Rotation),
                    };
                    CaptureSubPropsRecursive(building, sub, buildingPos, inverseRot, depth + 1);
                }
            }

            // I kroz pod-mreže i pod-površine — dekali na prilazu/placu su
            // NJIHOVA deca, pa moraju u isti snimak (prune inače ne razlikuje
            // regenerisane od legitimnih).
            if (EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<Game.Net.SubNet> subNets))
            {
                for (int i = 0; i < subNets.Length; i++)
                {
                    CaptureSubPropsRecursive(building, subNets[i].m_SubNet, buildingPos, inverseRot, depth + 1);
                }
            }

            if (EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<Game.Areas.SubArea> subAreas))
            {
                for (int i = 0; i < subAreas.Length; i++)
                {
                    CaptureSubPropsRecursive(building, subAreas[i].m_Area, buildingPos, inverseRot, depth + 1);
                }
            }
        }

        // Iz snimljenog relativnog rasporeda i TRENUTNOG transforma zgrade
        // izračunaj ciljne svetske pozicije pod-propova i nametni ih narednih
        // frejmova (dovoljno da pregazi igrin prefab re-layout).
        private void ScheduleSubPropRestore(Entity building)
        {
            // TVRDI uslov: bez snimka iz tekuće operacije nema ni restore ni
            // prune prozora — prazan/ tuđ snimak bi obrisao celo dvorište.
            if (!m_SubPropCaptured.Contains(building) ||
                !EntityManager.Exists(building) ||
                !EntityManager.TryGetComponent(building, out Game.Objects.Transform transform))
            {
                return;
            }

            // Snimljena zgrada bez ijednog rel zapisa je legitimna (igrač je
            // sve obrisao) — ulazi u prozor da prune počisti regenerisane.
            m_SubPropFixBuildings.Add(building);
            m_SubPropFixFrames = 10;

            foreach (KeyValuePair<Entity, SubPropRel> entry in m_SubPropRel)
            {
                if (entry.Value.m_Building != building || !EntityManager.Exists(entry.Key))
                {
                    continue;
                }

                m_SubPropFix[entry.Key] = new Game.Objects.Transform
                {
                    m_Position = transform.m_Position + math.mul(transform.m_Rotation, entry.Value.m_LocalPos),
                    m_Rotation = math.normalize(math.mul(transform.m_Rotation, entry.Value.m_LocalRot)),
                };
            }
        }

        private void RunSubPropFix()
        {
            if (m_SubPropFix.Count == 0 && m_SubPropFixBuildings.Count == 0)
            {
                return;
            }

            if (m_SubPropFixFrames <= 0)
            {
                m_SubPropFix.Clear();
                m_SubPropFixBuildings.Clear();
                return;
            }

            m_SubPropFixFrames--;
            m_SubPropScratch.Clear();
            m_SubPropScratch.AddRange(m_SubPropFix.Keys);
            foreach (Entity sub in m_SubPropScratch)
            {
                if (!EntityManager.Exists(sub) ||
                    !EntityManager.TryGetComponent(sub, out Game.Objects.Transform current))
                {
                    m_SubPropFix.Remove(sub);
                    continue;
                }

                Game.Objects.Transform target = m_SubPropFix[sub];
                if (math.distancesq(current.m_Position, target.m_Position) < 1e-6f &&
                    math.abs(math.dot(current.m_Rotation, target.m_Rotation)) > 0.99999f)
                {
                    continue;
                }

                EntityManager.SetComponentData(sub, target);
                EntityManager.AddComponent<Updated>(sub);
                EntityManager.AddComponent<BatchesUpdated>(sub);
            }

            foreach (Entity building in m_SubPropFixBuildings)
            {
                if (EntityManager.Exists(building))
                {
                    PruneRegeneratedSubProps(building, building, 0);
                }
            }
        }

        // Pod-prop koji se tokom fix prozora pojavi na pomerenoj zgradi, a nije
        // postojao pri snimanju rasporeda = igra je regenerisala prop koji je
        // igrač ranije obrisao ("fali" po prefab šablonu). Brišemo ga odmah da
        // igračeva izmena ostane. Predikat NE sme da koristi IsCopyable — on
        // zavisi od živih Selection filter čipova, pa bi ugašen Trees čip
        // značio da se obrisano drveće ne čisti.
        private bool IsPrunableSubProp(Entity entity)
        {
            return EntityManager.HasComponent<Game.Objects.Object>(entity) &&
                EntityManager.HasComponent<Game.Objects.Transform>(entity) &&
                EntityManager.HasComponent<PrefabRef>(entity) &&
                !EntityManager.HasComponent<Game.Buildings.Building>(entity) &&
                !EntityManager.HasComponent<Game.Buildings.Extension>(entity) &&
                !EntityManager.HasComponent<Game.Vehicles.Vehicle>(entity) &&
                !EntityManager.HasComponent<Game.Objects.Moving>(entity) &&
                !EntityManager.HasComponent<Game.Creatures.Creature>(entity) &&
                !IsInvisibleSpawnPoint(entity) &&
                !EntityManager.HasComponent<Game.Objects.Marker>(entity) &&
                !EntityManager.HasComponent<Game.Objects.UtilityObject>(entity) &&
                !EntityManager.HasComponent<Game.Objects.Placeholder>(entity) &&
                !EntityManager.HasComponent<Temp>(entity) &&
                !EntityManager.HasComponent<Deleted>(entity);
        }

        private void PruneRegeneratedSubProps(Entity building, Entity owner, int depth)
        {
            if (depth > 3)
            {
                return;
            }

            if (EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<Game.Objects.SubObject> subObjects))
            {
                // Popis PRE mutacija: i Deleted i rekurzija menjaju strukturu,
                // a vlasnik i dete su oba Game.Objects.Object sa Owner+SubObject
                // (realno isti chunk), pa bafer ume da postane ustajao usred
                // petlje — deo regenerisanih propova bi ostao neočišćen.
                List<Entity> children = new List<Entity>(subObjects.Length);
                for (int i = 0; i < subObjects.Length; i++)
                {
                    children.Add(subObjects[i].m_SubObject);
                }

                foreach (Entity sub in children)
                {
                    if (!EntityManager.Exists(sub))
                    {
                        continue;
                    }

                    // Nije u snimku (nov u prozoru) ILI odgovara potpisu ranije
                    // obrisanog — regenerisan primerak, briše se.
                    if ((!m_SubPropRel.ContainsKey(sub) || MatchesDeletedSubProp(building, sub)) &&
                        IsPrunableSubProp(sub))
                    {
                        EntityManager.AddComponent<Deleted>(sub);
                        continue;
                    }

                    PruneRegeneratedSubProps(building, sub, depth + 1);
                }
            }

            // Isti prošireni obilazak kao kod snimanja: regenerisani dekali na
            // prilazu/placu žive ispod pod-mreža i pod-površina.
            if (EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<Game.Net.SubNet> subNets))
            {
                for (int i = 0; i < subNets.Length; i++)
                {
                    PruneRegeneratedSubProps(building, subNets[i].m_SubNet, depth + 1);
                }
            }

            if (EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<Game.Areas.SubArea> subAreas))
            {
                for (int i = 0; i < subAreas.Length; i++)
                {
                    PruneRegeneratedSubProps(building, subAreas[i].m_Area, depth + 1);
                }
            }
        }

        // Visina OKOLNOG terena oko zgrade: prosek uzoraka van četiri ugla
        // placa (+6 m margine). Uzorak u centru ne valja — igra nivelira
        // teren pod placem na visinu zgrade, pa bi vraćao visinu same zgrade.
        private float SampleGroundAroundBuilding(Entity building, Game.Objects.Transform transform, ref TerrainHeightData heightData)
        {
            float halfX = 12f;
            float halfZ = 12f;
            if (EntityManager.TryGetComponent(building, out PrefabRef prefabRef) &&
                EntityManager.TryGetComponent(prefabRef.m_Prefab, out BuildingData buildingData) &&
                buildingData.m_LotSize.x > 0 && buildingData.m_LotSize.y > 0)
            {
                halfX = (buildingData.m_LotSize.x * 4f) + 6f;
                halfZ = (buildingData.m_LotSize.y * 4f) + 6f;
            }

            float sum = 0f;
            sum += TerrainUtils.SampleHeight(ref heightData, transform.m_Position + math.mul(transform.m_Rotation, new float3(halfX, 0f, halfZ)));
            sum += TerrainUtils.SampleHeight(ref heightData, transform.m_Position + math.mul(transform.m_Rotation, new float3(-halfX, 0f, halfZ)));
            sum += TerrainUtils.SampleHeight(ref heightData, transform.m_Position + math.mul(transform.m_Rotation, new float3(halfX, 0f, -halfZ)));
            sum += TerrainUtils.SampleHeight(ref heightData, transform.m_Position + math.mul(transform.m_Rotation, new float3(-halfX, 0f, -halfZ)));
            return sum * 0.25f;
        }

        // Gašenje/prekid alata: viseći prozori se prazne ODMAH umesto da
        // zamrznu i nastave ustajali pri sledećoj aktivaciji.
        private void FlushBuildingFixups()
        {
            try
            {
                // Odloženi settle-ovi se okidaju sada (road connection).
                if (m_DelayedSettle.Count > 0)
                {
                    m_DelayedSettleScratch.Clear();
                    m_DelayedSettleScratch.AddRange(m_DelayedSettle.Keys);
                    m_DelayedSettle.Clear();
                    foreach (Entity building in m_DelayedSettleScratch)
                    {
                        if (!EntityManager.Exists(building))
                        {
                            continue;
                        }

                        EntityManager.AddComponent<Updated>(building);
                        EntityManager.AddComponent<BatchesUpdated>(building);
                        if (EntityManager.TryGetComponent(building, out Game.Buildings.Building buildingData) &&
                            buildingData.m_RoadEdge != Entity.Null &&
                            EntityManager.Exists(buildingData.m_RoadEdge))
                        {
                            EntityManager.AddComponent<Updated>(buildingData.m_RoadEdge);
                        }
                    }
                }

                // Poslednja primena restore targeta — najbolje što možemo bez
                // aktivnog OnUpdate; posle ovoga sve se čisti.
                if (m_SubPropFix.Count > 0)
                {
                    m_SubPropFixFrames = 1;
                    RunSubPropFix();
                }

                // Zakazane transplantacije placa se NE forsiraju na gašenju:
                // fabričke površine nastaju i par frejmova POSLE izgradnje, pa
                // bi prevremeni sync ostavio trajne duplikate.
                // Zapisi prežive i odbrojavanje se nastavi pri sledećem
                // uključenju alata.
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Copaste: FlushBuildingFixups failed: {e.Message}");
            }

            m_SubPropFix.Clear();
            m_SubPropFixBuildings.Clear();
            m_SubPropRel.Clear();
            m_SubPropCaptured.Clear();
            m_SubPropFixFrames = 0;
            m_DelayedSettle.Clear();
            m_DelayedSettleSecondDone.Clear();
        }

        // Obris PLACA zgrade umesto kruga: pravougaonik iz
        // BuildingData.m_LotSize prefaba (ćelija zone je 8 m), rotiran po
        // transformu i prislonjen na teren. False ako prefab nema lot podatke
        // (pozivalac tada crta običan krug).
        private bool TryDrawBuildingLotOutline(OverlayRenderSystem.Buffer overlayBuffer, Entity entity, Game.Objects.Transform transform, UnityEngine.Color color, float width)
        {
            if (!EntityManager.TryGetComponent(entity, out PrefabRef prefabRef) ||
                !EntityManager.TryGetComponent(prefabRef.m_Prefab, out BuildingData buildingData) ||
                buildingData.m_LotSize.x <= 0 || buildingData.m_LotSize.y <= 0)
            {
                return false;
            }

            float halfX = buildingData.m_LotSize.x * 4f;
            float halfZ = buildingData.m_LotSize.y * 4f;
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            float3 c00 = LotCorner(transform, -halfX, -halfZ, ref heightData);
            float3 c10 = LotCorner(transform, halfX, -halfZ, ref heightData);
            float3 c11 = LotCorner(transform, halfX, halfZ, ref heightData);
            float3 c01 = LotCorner(transform, -halfX, halfZ, ref heightData);

            overlayBuffer.DrawLine(color, new Line3.Segment(c00, c10), width);
            overlayBuffer.DrawLine(color, new Line3.Segment(c10, c11), width);
            overlayBuffer.DrawLine(color, new Line3.Segment(c11, c01), width);
            overlayBuffer.DrawLine(color, new Line3.Segment(c01, c00), width);
            return true;
        }

        private float3 LotCorner(Game.Objects.Transform transform, float x, float z, ref TerrainHeightData heightData)
        {
            float3 world = transform.m_Position + math.mul(transform.m_Rotation, new float3(x, 0f, z));
            world.y = TerrainUtils.SampleHeight(ref heightData, world) + 0.5f;
            return world;
        }

        // ---------- Farbane površine u selekciji ----------

        private struct SurfaceMoveItem
        {
            public Entity m_Entity;
            public float2 m_Offset;
        }

        private readonly List<SurfaceMoveItem> m_MoveSurfaceItems = new List<SurfaceMoveItem>();

        private bool TryGetSurfaceCentroid(Entity area, out float2 centroid)
        {
            centroid = float2.zero;
            if (!EntityManager.Exists(area) ||
                !EntityManager.TryGetBuffer(area, true, out DynamicBuffer<Game.Areas.Node> nodes) ||
                nodes.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < nodes.Length; i++)
            {
                centroid += nodes[i].m_Position.xz;
            }

            centroid /= nodes.Length;
            return true;
        }

        // Rotacija oko pivota + delta na ceo poligon; visina čvora prati teren
        // uz očuvan ofset (isto pravilo kao za propove). markUpdated=false
        // tokom drag-a — rebuild triangulacije ide na tick i na kraju poteza.
        private void TransformSurface(Entity area, quaternion rotation, float3 pivot, float3 delta, bool markUpdated = true)
        {
            if (!EntityManager.Exists(area) ||
                // Zgradine površine se NE pomeraju pojedinačno — pomera ih samo
                // pod-tree kad ide cela zgrada (TransformBuildingPart). Ovde su
                // samo za selekciju/brisanje.
                EntityManager.HasComponent<Owner>(area) ||
                !EntityManager.TryGetBuffer(area, false, out DynamicBuffer<Game.Areas.Node> nodes))
            {
                return;
            }

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            for (int i = 0; i < nodes.Length; i++)
            {
                Game.Areas.Node node = nodes[i];
                float heightOffset = node.m_Position.y - TerrainUtils.SampleHeight(ref heightData, node.m_Position);
                float3 position = pivot + math.mul(rotation, node.m_Position - pivot) + delta;
                position.y = TerrainUtils.SampleHeight(ref heightData, position) + heightOffset;
                node.m_Position = position;
                nodes[i] = node;
            }

            if (markUpdated)
            {
                EntityManager.AddComponent<Updated>(area);
            }
        }

        // Finalni Updated za selektovane površine na kraju poteza. Zgradine se
        // preskaču — nisu se ni pomerale, a Updated na placu okida ponovnu
        // randomizaciju dekoracija.
        private void SettleSurfaces()
        {
            foreach (Entity area in m_SelectedSurfaces)
            {
                if (EntityManager.Exists(area) && !EntityManager.HasComponent<Owner>(area))
                {
                    EntityManager.AddComponent<Updated>(area);
                }
            }
        }

        // Tick za pun update tokom prevlačenja (drag i rotacija dele sat).
        private float m_LastBuildingTick;

        private bool BuildingTick()
        {
            if (UnityEngine.Time.time - m_LastBuildingTick > 0.25f)
            {
                m_LastBuildingTick = UnityEngine.Time.time;
                return true;
            }

            return false;
        }

        // ---------- Potpisi površina zgrade (paste/undo nasleđuje brisanja) ----------

        // Jedna površina placa IZVORA: prefab + ceo poligon u LOKALNOM okviru
        // zgrade (nezavisno od pozicije/rotacije stampa). Kopija ne čisti
        // construction-ov nasumični raspored — ZAMENI ga tačnim kopijama ovih
        // ("Paste look: Original" i za plac).
        private struct SurfaceSig
        {
            public Entity m_Prefab;
            public float2[] m_LocalNodes;
        }

        private struct PendingSurfacePrune
        {
            public int m_Frames;
            public List<SurfaceSig> m_Sigs;

            // Sesija alata u kojoj je zakazana — zapis ne sme da tinja kroz
            // proizvoljno mnogo paljenja i gašenja.
            public int m_Session;
        }

        // Zgrade koje čekaju čišćenje viška površina posle construction-a
        // (paste rezolucija, undo brisanja, redo paste-a). RAM-only.
        private readonly Dictionary<Entity, PendingSurfacePrune> m_PendingSurfacePrune = new Dictionary<Entity, PendingSurfacePrune>();

        // Snimak placa zgrade — po jedna stavka za svaku Surface pod-površinu,
        // sa celim poligonom u lokalnom okviru. Null vraća samo pozivalac za
        // ne-zgrade; prazna lista je legitimna (igrač obrisao sve površine).
        private List<SurfaceSig> CollectBuildingSurfaceSigs(Entity building, Game.Objects.Transform transform)
        {
            List<SurfaceSig> sigs = new List<SurfaceSig>();
            if (!EntityManager.TryGetBuffer(building, true, out DynamicBuffer<Game.Areas.SubArea> subAreas))
            {
                return sigs;
            }

            quaternion inverseRotation = math.inverse(transform.m_Rotation);
            for (int i = 0; i < subAreas.Length; i++)
            {
                Entity area = subAreas[i].m_Area;
                if (!EntityManager.Exists(area) ||
                    !EntityManager.HasComponent<Game.Areas.Surface>(area) ||
                    EntityManager.HasComponent<Deleted>(area) ||
                    !EntityManager.TryGetComponent(area, out PrefabRef prefabRef) ||
                    !EntityManager.TryGetBuffer(area, true, out DynamicBuffer<Game.Areas.Node> nodes) ||
                    nodes.Length < 3)
                {
                    continue;
                }

                float2[] localNodes = new float2[nodes.Length];
                for (int n = 0; n < nodes.Length; n++)
                {
                    float3 local = math.mul(inverseRotation, nodes[n].m_Position - transform.m_Position);
                    localNodes[n] = local.xz;
                }

                sigs.Add(new SurfaceSig { m_Prefab = prefabRef.m_Prefab, m_LocalNodes = localNodes });
            }

            return sigs;
        }

        // Efektivni potpisi placa za SNIMKE (copy/delete/paste-undo): zgrada u
        // izgradnji još nema svoje površine, ali ako joj je transplantacija
        // ZAKAZANA, ti potpisi su istina o placu — snimak ih preuzima umesto
        // null (undo/redo ciklusi oko nezavršene izgradnje bi inače gubili
        // transplantaciju).
        private List<SurfaceSig> CaptureLotSigsForSnapshot(Entity entity, Game.Objects.Transform transform)
        {
            if (!IsBuilding(entity))
            {
                return null;
            }

            if (m_PendingSurfacePrune.TryGetValue(entity, out PendingSurfacePrune pending))
            {
                return pending.m_Sigs;
            }

            if (EntityManager.HasComponent<Game.Objects.UnderConstruction>(entity))
            {
                return null;
            }

            return CollectBuildingSurfaceSigs(entity, transform);
        }

        // Da li se potpis poklapa sa površinom datog prefaba i lokalnog
        // poligona (isti prefab, isti broj čvorova, prvi čvor na 1,5 m).
        private static bool LotSigMatches(SurfaceSig sig, Entity prefab, int nodeCount, float2 firstNodeLocal)
        {
            return sig.m_Prefab == prefab &&
                sig.m_LocalNodes != null &&
                sig.m_LocalNodes.Length == nodeCount &&
                math.distancesq(sig.m_LocalNodes[0], firstNodeLocal) < 2.25f;
        }

        // Ukloni potpis obrisane površine iz zakazane transplantacije vlasnika
        // (COW — lista je deljena sa zapisima istorije).
        private void RemovePendingLotSig(Entity owner, Entity area)
        {
            if (owner == Entity.Null ||
                !m_PendingSurfacePrune.TryGetValue(owner, out PendingSurfacePrune pending) ||
                !EntityManager.TryGetComponent(owner, out Game.Objects.Transform ownerTransform) ||
                !EntityManager.TryGetComponent(area, out PrefabRef areaPrefab) ||
                !EntityManager.TryGetBuffer(area, true, out DynamicBuffer<Game.Areas.Node> areaNodes) ||
                areaNodes.Length < 3)
            {
                return;
            }

            quaternion inverseRotation = math.inverse(ownerTransform.m_Rotation);
            float2 firstLocal = math.mul(inverseRotation, areaNodes[0].m_Position - ownerTransform.m_Position).xz;
            for (int s = 0; s < pending.m_Sigs.Count; s++)
            {
                if (LotSigMatches(pending.m_Sigs[s], areaPrefab.m_Prefab, areaNodes.Length, firstLocal))
                {
                    List<SurfaceSig> reduced = new List<SurfaceSig>(pending.m_Sigs);
                    reduced.RemoveAt(s);
                    m_PendingSurfacePrune[owner] = new PendingSurfacePrune
                    {
                        m_Frames = pending.m_Frames,
                        m_Sigs = reduced,

                        // Prepis MORA da ponese i pečat sesije — default 0 bi
                        // ceo zapis sledeći tik proglasio ustajalim i sync
                        // placa bi tiho otpao.
                        m_Session = pending.m_Session,
                    };
                    return;
                }
            }
        }

        // Registar obrisanih pod-propova prati zgradu kroz rekreaciju (undo
        // brisanja pravi NOVI entitet — potpisi ne smeju da ostanu na mrtvom).
        private void RekeyDeletedSubProps(Entity oldBuilding, Entity newBuilding)
        {
            if (oldBuilding != newBuilding &&
                m_DeletedSubProps.TryGetValue(oldBuilding, out List<DeletedSubSig> list))
            {
                m_DeletedSubProps.Remove(oldBuilding);
                m_DeletedSubProps[newBuilding] = list;
            }
        }

        private void ScheduleSurfacePrune(Entity building, List<SurfaceSig> sigs)
        {
            if (sigs == null || building == Entity.Null)
            {
                return;
            }

            // Odbrojavanje kreće tek kad izgradnja prođe (guard u tiku); 12
            // frejmova posle nje su sve fabričke površine tu. JEDNOKRATNO —
            // transplantacija se ne sme ponoviti (duplirala bi površine).
            m_PendingSurfacePrune[building] = new PendingSurfacePrune
            {
                m_Frames = 12,
                m_Sigs = sigs,
                m_Session = m_ToolSessionId,
            };
            Mod.Log.Info($"Copaste: lot sync scheduled for e{building.Index} ({sigs.Count} source surfaces)");
        }

        // "Paste look: Original" za plac — construction-ov nasumični raspored
        // površina se ZAMENI tačnim kopijama izvorovih: isti poligoni (staze,
        // prilaz, trava), obrisano ostaje obrisano. Fabričke Surface
        // pod-površine se brišu sa svojim dekoracijama, pa se izvorne
        // rekreiraju kao vlasništvo zgrade (isti šablon kao RecreateSurface).
        private void SyncBuildingLotSurfaces(Entity building, List<SurfaceSig> sigs)
        {
            if (!EntityManager.Exists(building) ||
                !EntityManager.TryGetComponent(building, out Game.Objects.Transform transform) ||
                !EntityManager.TryGetBuffer(building, true, out DynamicBuffer<Game.Areas.SubArea> subAreas))
            {
                return;
            }

            // 1) Popis pa brisanje SVIH fabričkih Surface pod-površina (popis
            // pre strukturnih izmena — brisanje invalidira bafer).
            List<Entity> factory = new List<Entity>();
            for (int i = 0; i < subAreas.Length; i++)
            {
                Entity area = subAreas[i].m_Area;
                if (EntityManager.Exists(area) &&
                    EntityManager.HasComponent<Game.Areas.Surface>(area) &&
                    !EntityManager.HasComponent<Deleted>(area))
                {
                    factory.Add(area);
                }
            }

            foreach (Entity area in factory)
            {
                // false: sync briše fabričke površine i ne sme da izbacuje
                // sopstvene potpise iz zakazane liste.
                DeleteSurfaceWithChildren(area, updatePendingSigs: false);
            }

            // 2) Rekreacija izvornih površina na transformu NOVE zgrade.
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            int created = 0;
            foreach (SurfaceSig sig in sigs)
            {
                if (sig.m_LocalNodes == null || sig.m_LocalNodes.Length < 3 ||
                    !EntityManager.Exists(sig.m_Prefab) ||
                    !EntityManager.TryGetComponent(sig.m_Prefab, out AreaData areaData))
                {
                    continue;
                }

                NativeArray<Entity> createdEntities = EntityManager.CreateEntity(areaData.m_Archetype, 1, Allocator.Temp);
                Entity entity = createdEntities[0];
                createdEntities.Dispose();
                EntityManager.SetComponentData(entity, new PrefabRef(sig.m_Prefab));

                DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(entity);
                nodes.Clear();
                for (int n = 0; n < sig.m_LocalNodes.Length; n++)
                {
                    float3 world = transform.m_Position + math.mul(transform.m_Rotation, new float3(sig.m_LocalNodes[n].x, 0f, sig.m_LocalNodes[n].y));
                    world.y = TerrainUtils.SampleHeight(ref heightData, world);
                    nodes.Add(new Game.Areas.Node { m_Position = world, m_Elevation = 0f });
                }

                if (EntityManager.TryGetComponent(entity, out Game.Areas.Area area))
                {
                    area.m_Flags |= Game.Areas.AreaFlags.Complete;
                    EntityManager.SetComponentData(entity, area);
                }

                EntityManager.AddComponentData(entity, new Owner { m_Owner = building });
                if (EntityManager.TryGetBuffer(building, false, out DynamicBuffer<Game.Areas.SubArea> ownerSubAreas))
                {
                    ownerSubAreas.Add(new Game.Areas.SubArea { m_Area = entity });
                }

                EntityManager.AddComponent<Updated>(entity);
                created++;
            }

            Mod.Log.Info($"Copaste: lot sync on e{building.Index} — {factory.Count} factory surfaces replaced by {created} from source");
        }

        // Tik zakazanih transplantacija. Nema force varijante: prevremeni sync
        // (pre nego što igra stvori SVE fabričke površine) pravi trajne
        // duplikate — zapis radije preživi gašenje alata.
        private void RunPendingSurfacePrunes()
        {
            if (m_PendingSurfacePrune.Count == 0)
            {
                return;
            }

            List<Entity> keys = new List<Entity>(m_PendingSurfacePrune.Keys);
            foreach (Entity building in keys)
            {
                if (!EntityManager.Exists(building))
                {
                    m_PendingSurfacePrune.Remove(building);
                    continue;
                }

                PendingSurfacePrune pending = m_PendingSurfacePrune[building];

                // Zapis sme da preživi JEDNO gašenje alata (fabričke površine
                // nastaju par frejmova posle izgradnje, pa se ne forsira) — ali
                // ne više od toga. Inače bi se odbrojavanje nastavilo sat
                // vremena kasnije i pregazilo površine koje je igrač u
                // međuvremenu sam nafarbao, i to bez undo zapisa.
                if (m_ToolSessionId - pending.m_Session > 1)
                {
                    Mod.Log.Info($"Copaste: lot sync for e{building.Index} dropped — stale across tool sessions");
                    m_PendingSurfacePrune.Remove(building);
                    continue;
                }

                // Izgradnja još traje — pauzirana simulacija je drži proizvoljno
                // dugo, a površine nastaju tek po završetku. Odbrojavanje se
                // resetuje i kreće tek kad kuća bude gotova.
                if (EntityManager.HasComponent<Game.Objects.UnderConstruction>(building))
                {
                    pending.m_Frames = 12;
                    m_PendingSurfacePrune[building] = pending;
                    continue;
                }

                pending.m_Frames--;
                if (pending.m_Frames <= 0)
                {
                    // JEDNOKRATNO: transplantacija se ne ponavlja (dupliranje).
                    SyncBuildingLotSurfaces(building, pending.m_Sigs);
                    m_PendingSurfacePrune.Remove(building);
                }
                else
                {
                    m_PendingSurfacePrune[building] = pending;
                }
            }
        }

        // ---------- Snapshot površina za undo/redo ----------

        private struct SurfaceSnapshot
        {
            public Entity m_Entity;
            public Entity m_Prefab;
            public float3[] m_Nodes;

            // Vlasnik (zgrada/nadogradnja) za zgradine površine; Entity.Null
            // za samostalne farbane. Undo brisanja vraća i Owner vezu.
            public Entity m_Owner;
        }

        private List<SurfaceSnapshot> SnapshotSurfaces(IEnumerable<Entity> areas)
        {
            List<SurfaceSnapshot> snapshots = new List<SurfaceSnapshot>();
            foreach (Entity area in areas)
            {
                if (!EntityManager.Exists(area) ||
                    !EntityManager.TryGetComponent(area, out PrefabRef prefabRef) ||
                    !EntityManager.TryGetBuffer(area, true, out DynamicBuffer<Game.Areas.Node> nodes) ||
                    nodes.Length == 0)
                {
                    continue;
                }

                float3[] copy = new float3[nodes.Length];
                for (int i = 0; i < nodes.Length; i++)
                {
                    copy[i] = nodes[i].m_Position;
                }

                Entity surfaceOwner = EntityManager.TryGetComponent(area, out Owner ownerComponent)
                    ? ownerComponent.m_Owner
                    : Entity.Null;

                snapshots.Add(new SurfaceSnapshot
                {
                    m_Entity = area,
                    m_Prefab = prefabRef.m_Prefab,
                    m_Nodes = copy,
                    m_Owner = surfaceOwner,
                });
            }

            return snapshots;
        }

        private List<SurfaceSnapshot> SnapshotSurfaceEntities(List<SurfaceSnapshot> reference)
        {
            List<Entity> entities = new List<Entity>(reference.Count);
            foreach (SurfaceSnapshot snapshot in reference)
            {
                entities.Add(snapshot.m_Entity);
            }

            return SnapshotSurfaces(entities);
        }

        // Klik-selekcija površine: tačka u poligonu (xz ravan). Kod ugnježdenih
        // površina pobeđuje najmanja — ona je "gornja" u smislu farbanja.
        // Building elements uključuje i zgradine površine u kandidate.
        private bool TryPickSurfaceAt(float3 position, out Entity surface)
        {
            surface = Entity.Null;
            float bestSize = float.MaxValue;
            PickSurfaceFromQuery(m_SurfaceQuery, position, requireBuildingRoot: false, ref surface, ref bestSize);
            if (SelectBuildingProps)
            {
                PickSurfaceFromQuery(m_OwnedSurfaceQuery, position, requireBuildingRoot: true, ref surface, ref bestSize);
            }

            return surface != Entity.Null;
        }

        private void PickSurfaceFromQuery(EntityQuery query, float3 position, bool requireBuildingRoot, ref Entity surface, ref float bestSize)
        {
            NativeArray<Entity> areas = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < areas.Length; i++)
            {
                if (!EntityManager.TryGetBuffer(areas[i], true, out DynamicBuffer<Game.Areas.Node> nodes) ||
                    nodes.Length < 3 ||
                    !PointInPolygon(nodes, position.xz))
                {
                    continue;
                }

                // Owner-lanac tek POSLE geometrije — jeftin test prvo.
                if (requireBuildingRoot && GetOwnerRootBuilding(areas[i]) == Entity.Null)
                {
                    continue;
                }

                float size = PolygonSize(nodes);
                if (size < bestSize)
                {
                    bestSize = size;
                    surface = areas[i];
                }
            }

            areas.Dispose();
        }

        // Standardni crossing test u xz ravni.
        private static bool PointInPolygon(DynamicBuffer<Game.Areas.Node> nodes, float2 point)
        {
            bool inside = false;
            for (int i = 0, j = nodes.Length - 1; i < nodes.Length; j = i++)
            {
                float2 a = nodes[i].m_Position.xz;
                float2 b = nodes[j].m_Position.xz;
                if ((a.y > point.y) != (b.y > point.y) &&
                    point.x < ((b.x - a.x) * (point.y - a.y) / (b.y - a.y)) + a.x)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        // Približna veličina poligona (shoelace, bez deljenja — samo za poređenje).
        private static float PolygonSize(DynamicBuffer<Game.Areas.Node> nodes)
        {
            float sum = 0f;
            for (int i = 0, j = nodes.Length - 1; i < nodes.Length; j = i++)
            {
                float2 a = nodes[j].m_Position.xz;
                float2 b = nodes[i].m_Position.xz;
                sum += (a.x * b.y) - (b.x * a.y);
            }

            return math.abs(sum);
        }

        // Undo brisanja površine: nova instanca iz prefab arhetipa sa istim
        // poligonom — ista mehanika kao RecreateProp za objekte.
        private void RecreateSurface(SurfaceSnapshot snapshot)
        {
            if (!EntityManager.Exists(snapshot.m_Prefab) ||
                !EntityManager.TryGetComponent(snapshot.m_Prefab, out AreaData areaData) ||
                snapshot.m_Nodes == null || snapshot.m_Nodes.Length < 3)
            {
                return;
            }

            // Vlasnik (npr. nadogradnja zgrade) je mrtav i NIJE rekreiran u ovom
            // undo-u — površina bi se vratila kao vlasnički siroče van Building
            // elements pravila. Preskače se (undo zgrada je ionako izgubio
            // nadogradnje — sim stanje se ne vraća).
            if (snapshot.m_Owner != Entity.Null && !EntityManager.Exists(snapshot.m_Owner))
            {
                return;
            }

            NativeArray<Entity> created = EntityManager.CreateEntity(areaData.m_Archetype, 1, Allocator.Temp);
            Entity entity = created[0];
            created.Dispose();
            EntityManager.SetComponentData(entity, new PrefabRef(snapshot.m_Prefab));

            DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(entity);
            nodes.Clear();
            for (int i = 0; i < snapshot.m_Nodes.Length; i++)
            {
                nodes.Add(new Game.Areas.Node { m_Position = snapshot.m_Nodes[i], m_Elevation = 0f });
            }

            // Complete flag — bez njega poligon nije editabilan (isto kao kod paste-a).
            if (EntityManager.TryGetComponent(entity, out Game.Areas.Area area))
            {
                area.m_Flags |= Game.Areas.AreaFlags.Complete;
                EntityManager.SetComponentData(entity, area);
            }

            // Zgradina površina se vraća NAZAD zgradi: Owner veza + upis u njen
            // SubArea spisak — isti šablon kao RecreateProp za pod-propove.
            // Bez toga bi posle undo-a postala samostalna i ispala iz pod-tree-a.
            if (snapshot.m_Owner != Entity.Null && EntityManager.Exists(snapshot.m_Owner))
            {
                EntityManager.AddComponentData(entity, new Owner { m_Owner = snapshot.m_Owner });
                if (EntityManager.TryGetBuffer(snapshot.m_Owner, false, out DynamicBuffer<Game.Areas.SubArea> ownerSubAreas))
                {
                    ownerSubAreas.Add(new Game.Areas.SubArea { m_Area = entity });
                }

                // Vlasnik ima ZAKAZANU transplantaciju placa (npr. i sam je tek
                // vraćen undo-om) — vraćena površina se dopisuje u potpise da je
                // sync ne pregazi. COPY-ON-WRITE: lista potpisa je deljena sa
                // zapisima istorije, ne sme se mutirati u mestu.
                if (m_PendingSurfacePrune.TryGetValue(snapshot.m_Owner, out PendingSurfacePrune pendingSync) &&
                    EntityManager.TryGetComponent(snapshot.m_Owner, out Game.Objects.Transform pendingOwnerTransform))
                {
                    quaternion invRotation = math.inverse(pendingOwnerTransform.m_Rotation);
                    float2[] localNodes = new float2[snapshot.m_Nodes.Length];
                    for (int i = 0; i < snapshot.m_Nodes.Length; i++)
                    {
                        float3 local = math.mul(invRotation, snapshot.m_Nodes[i] - pendingOwnerTransform.m_Position);
                        localNodes[i] = local.xz;
                    }

                    // Idempotentno: undo→redo→undo istog zapisa ne sme da
                    // dupliramo potpis (sync bi napravio dve kopije površine).
                    bool alreadyListed = false;
                    for (int s = 0; s < pendingSync.m_Sigs.Count; s++)
                    {
                        if (LotSigMatches(pendingSync.m_Sigs[s], snapshot.m_Prefab, localNodes.Length, localNodes[0]))
                        {
                            alreadyListed = true;
                            break;
                        }
                    }

                    if (!alreadyListed)
                    {
                        List<SurfaceSig> extended = new List<SurfaceSig>(pendingSync.m_Sigs)
                        {
                            new SurfaceSig { m_Prefab = snapshot.m_Prefab, m_LocalNodes = localNodes },
                        };
                        m_PendingSurfacePrune[snapshot.m_Owner] = new PendingSurfacePrune
                        {
                            m_Frames = pendingSync.m_Frames,
                            m_Sigs = extended,

                            // Isto kao kod skidanja potpisa: pečat sesije ide
                            // uz prepis, inače se zapis odmah odbaci.
                            m_Session = pendingSync.m_Session,
                        };
                    }
                }
            }

            EntityManager.AddComponent<Updated>(entity);
            RemapHistoryEntity(snapshot.m_Entity, entity);
        }

        private void ApplySurfaceSnapshots(List<SurfaceSnapshot> snapshots)
        {
            foreach (SurfaceSnapshot snapshot in snapshots)
            {
                // Zgradine površine se ne transformišu, pa nema šta da se
                // vraća — a nepotreban Updated na placu bi samo ponovo
                // randomizovao dekoracije.
                if (!EntityManager.Exists(snapshot.m_Entity) ||
                    EntityManager.HasComponent<Owner>(snapshot.m_Entity) ||
                    !EntityManager.TryGetBuffer(snapshot.m_Entity, false, out DynamicBuffer<Game.Areas.Node> nodes) ||
                    nodes.Length != snapshot.m_Nodes.Length)
                {
                    continue;
                }

                for (int i = 0; i < nodes.Length; i++)
                {
                    Game.Areas.Node node = nodes[i];
                    node.m_Position = snapshot.m_Nodes[i];
                    nodes[i] = node;
                }

                EntityManager.AddComponent<Updated>(snapshot.m_Entity);
            }
        }
    }
}
