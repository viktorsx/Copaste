// Copaste — pomeranje MREŽA: čvorovi i segmenti puteva/staza/šina.
//
// Pristup, pojednostavljen: nema
// kontrolnih tačaka ni savijanja — samo kruto pomeranje/rotacija selekcije.
// Jedinica selekcije su ČVOROVI i IVICE; na svakom potezu se izvede "pokretni
// skup" čvorova (selektovani čvorovi + krajevi selektovanih ivica), pa:
//  - ivica čija su OBA čvora u skupu ide KRUTO: sve 4 tačke nose istu
//    rotaciju+deltu, a visinska korekcija terena sa KRAJEVA se interpolira
//    na unutrašnje tačke — mostovi i useci zadržavaju oblik;
//  - ivica sa JEDNIM čvorom u skupu je "sused": kraj krive prati čvor, a
//    kontrolne tačke b/c se ponovo interpoliraju per-osa proporcijama
//    (klampovano) — zakrivljenost se čuva.
// Piše se ISKLJUČIVO Node/Curve/NodeGeometry (+ Updated) — EdgeGeometry,
// kompozicije i trake regeneriše igra. Ništa novo ne ide u save.
//
// VAŽNO za ceo fajl: AddComponent je STRUKTURNA izmena koja invalidira ranije
// uzete DynamicBuffer-e — zato svaki prolaz prvo POPIŠE sadržaj bafera u
// scratch liste, pa tek onda mutira (nikad AddComponent usred iteracije bafera).
//
// Svesno VAN v1: kreiranje novih mreža od nule, utility mreže
// (dalekovodi/cevi), spajanje čvorova. Kopiranje/brisanje/savijanje su ušli.

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
        // Klik: čvor pobeđuje u svom radijusu (min 6 m), inače
        // najbliža ivica po krivoj. Poređenja idu u xz ravni sa malim penalom
        // po visinskoj razlici — mostovi/tuneli su inače nedohvatljivi (klik
        // raycast pogađa teren ISPOD njih), a bez penala bi metro ispod
        // raskrsnice "krao" klik površinskom putu.
        private const float kNetNodePickMin = 6f;
        private const float kNetEdgePickThreshold = 2.5f;
        private const float kNetPickSearchRadius = 20f;
        private const float kNetPickHeightWeight = 0.05f;
        private const int kNetOverlaySegments = 12;

        // Marquee test ivice: uzorci duž krive (kao kod ograda) — uslov "oba
        // čvora u okviru" je promašivao svaki duži segment čiji krajevi vire.
        private const int kNetMarqueeSamples = 16;

        // Pun update (regeneracija geometrije) svaki frejm samo za male
        // pokretne skupove; veći idu na tick + završni settle.
        private const int kNetFullMarkLimit = 16;

        // Slojevi koje selekcija sme da dira; struja/cevi/ograde/markeri NE.
        private const Game.Net.Layer kSelectableNetLayers =
            Game.Net.Layer.Road | Game.Net.Layer.Pathway | Game.Net.Layer.PublicTransportRoad |
            Game.Net.Layer.TrainTrack | Game.Net.Layer.TramTrack | Game.Net.Layer.SubwayTrack;

        private readonly List<Entity> m_SelectedNodes = new List<Entity>();
        private readonly List<Entity> m_SelectedNetEdges = new List<Entity>();

        // Scratch strukture — pune se po pozivu, bez alokacija po frejmu.
        private readonly HashSet<Entity> m_NetMovingScratch = new HashSet<Entity>();

        // Stare pozicije cvorova u tekucem transformu — kraj krive cuva svoj
        // OFSET od cvora (end' = node' + rot*(end - node)). Lepljenje kraja NA
        // cvor je brisalo bocno poravnanje traka.
        private readonly Dictionary<Entity, float3> m_NetOldNodePos = new Dictionary<Entity, float3>();
        private readonly HashSet<Entity> m_NetSeenEdgeScratch = new HashSet<Entity>();
        private readonly List<Entity> m_NetEdgeScratch = new List<Entity>();
        private readonly List<Entity> m_NetChildScratch = new List<Entity>();

        // Move drag: referentna tačka skupa čvorova, korak po frejmu kao
        // površine/ograde.
        private bool m_NetMoveActive;
        private float2 m_NetMoveOffset;

        // Hover kandidati pod kursorom (belo) — da se vidi šta će klik uzeti.
        private Entity m_NetHoverNode = Entity.Null;
        private Entity m_NetHoverEdge = Entity.Null;
        private Entity m_LaneHoverEntity = Entity.Null;

        // Visinski pomak po čvoru u tekućem potezu — elevacija ivice je
        // SREDINA deonice, pa joj treba prosek pomaka oba kraja.
        private readonly Dictionary<Entity, float> m_NetShiftScratch = new Dictionary<Entity, float>();

        // Odloženi drugi settle (search stabla kasne frejm iza upisa).
        private readonly Dictionary<Entity, int> m_DelayedNetSettle = new Dictionary<Entity, int>();
        private readonly List<Entity> m_NetSettleKeyScratch = new List<Entity>();

        // Hover se ne preračunava dok kursor miruje (dve quad-tree pretrage
        // po frejmu nisu besplatne) — isti prag kao marquee sken.
        private float3 m_NetHoverLastPosition = new float3(float.MaxValue);
        private Entity m_NetHoverPrevLane = Entity.Null;
        private Entity m_NetHoverPrevNode = Entity.Null;
        private Entity m_NetHoverPrevEdge = Entity.Null;

        // Zavarivanje krajeva kurseva pri paste-u/rekreaciji: igra spaja
        // krajeve u JEDAN čvor samo pri bitski identičnoj poziciji (NodeKey u
        // GenerateNodesSystem poredi float3.Equals). Krive susednih ivica u
        // živim podacima to ne garantuju (endpoints nastaju MathUtils.Cut-om),
        // pa se pre pravljenja definicija svi krajevi klasterizuju i "zavare"
        // na istu reprezentativnu tačku — inače raskrsnica padne na dead-endove.
        // Klaster krajeva: reprezentativna tačka + (opciono) živi čvor na koji
        // se definicija kači umesto da pravi novi.
        private struct WeldPoint
        {
            public float3 m_Position;
            public Entity m_Node;
        }

        private readonly List<WeldPoint> m_WeldScratch = new List<WeldPoint>();
        // Zastavice krajeva nalepljenih/rekreiranih deonica — SA DisableMerge,
        // i sad znamo tacno sta koja stvar radi:
        //  - spajanje deonica u cvorove NE zavisi od ovoga: GenerateNodesSystem
        //    spaja BITSKI iste tacke iz tabele cvorova (dokazano: topologija
        //    identicna i bez flag-a);
        //  - CourseSplitSystem bez ovog flaga uzima nase kurseve u obradu
        //    preklapanja: exit krak paralelan uz autoput na par metara upada u
        //    overlap i sistem SECE/SPAJA kurseve — izmereno do 4,5 m pomaka
        //    kontrolnih tacaka i ~9 stepeni skretanja ose ("trake se pomere");
        //  - validacione ikone (Overlapping items/Invalid shape) postoje sa i
        //    bez flaga za bliske paralelne deonice — stamp ih ignorise
        //    (ignoreErrors), original ih nema jer se validira samo Temp.
        // Cena: kopija ne pravi automatske raskrsnice preko POSTOJECIH puteva
        // na mapi — lepi se tacno kako je uzeta, spajanje s okolinom je rucno.
        private const CoursePosFlags kPastedCourseStartFlags =
            CoursePosFlags.IsFirst | CoursePosFlags.IsLeft | CoursePosFlags.IsRight | CoursePosFlags.DisableMerge;
        private const CoursePosFlags kPastedCourseEndFlags =
            CoursePosFlags.IsLast | CoursePosFlags.IsLeft | CoursePosFlags.IsRight | CoursePosFlags.DisableMerge;

        private const float kWeldRadiusXZ = 0.25f;
        private const float kWeldToleranceY = 1f;

        // ALT ravnanje: lanac selektovanih među-čvorova + pomoćne strukture.
        // Armed = ALT "tap" gest (pritisak bez klika miša do otpuštanja).
        private bool m_StraightenArmed;
        private readonly HashSet<Entity> m_StraightenSet = new HashSet<Entity>();
        private readonly List<Entity> m_StraightenChain = new List<Entity>();
        private readonly List<Entity> m_StraightenSide = new List<Entity>();
        private readonly List<float3> m_StraightenTargets = new List<float3>();
        private readonly HashSet<Entity> m_StraightenEdgeSeen = new HashSet<Entity>();

        private static bool SelectNetworks => Mod.Settings != null && Mod.Settings.SelectNetworks;

        // PODZEMNI režim (kao buldožer): requireUnderground
        // prebacuje igru u podzemni prikaz, a pick/marquee tada biraju SAMO
        // ono što je ispod terena — i obrnuto, u normalnom režimu metro ne
        // sme da krade klik površinskom putu.
        public bool UndergroundMode;

        private bool MatchesUndergroundMode(float3 position)
        {
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            bool below = position.y < TerrainUtils.SampleHeight(ref heightData, position) - 1.5f;
            return below == UndergroundMode;
        }

        private struct NetNodeSnapshot
        {
            public Entity m_Entity;
            public Game.Net.Node m_Data;
            public bool m_HadElevation;
            public float2 m_Elevation;
        }

        private struct NetEdgeSnapshot
        {
            public Entity m_Entity;
            public Bezier4x3 m_Curve;
            public bool m_HadElevation;
            public float2 m_Elevation;

            // Za rekreaciju (redo paste-a, undo brisanja): prefab + nadogradnje.
            public Entity m_Prefab;
            public bool m_HasUpgrade;
            public CompositionFlags m_Upgrade;

            // Krajnji čvorovi: ako prežive brisanje, rekreirana deonica se
            // KAČI na njih (inače bi nastao dvojnik i saobraćaj ne bi prošao).
            // Njihova elevacija je zasebna od elevacije ivice — ivica nosi
            // (levo,desno) na SREDINI, čvor svoju vrednost na svom mestu.
            public Entity m_StartNode;
            public Entity m_EndNode;

            // Pozicije cvorova u trenutku snimanja: dve rekreirane deonice
            // istog cvora dele BITSKI istu tacku (weld po blizini ne pokriva
            // gap kraja krive do cvora, koji na kruznom toku ide i do 20 m).
            public bool m_HasNodePositions;
            public float3 m_StartNodePos;
            public float3 m_EndNodePos;
            public bool m_HadStartElevation;
            public float2 m_StartElevation;
            public bool m_HadEndElevation;
            public float2 m_EndElevation;

            // Stanje CVOROVA za rekreaciju: Upgraded flagovi + marker prefabi
            // (kruzni tok/rucni semafor su POD-OBJEKTI cvora — bez ovoga bi
            // undo brisanja / redo paste-a vracao raskrsnicu kao obicnu).
            public bool m_StartNodeHasUpgrade;
            public CompositionFlags m_StartNodeUpgrade;
            public bool m_EndNodeHasUpgrade;
            public CompositionFlags m_EndNodeUpgrade;
            public List<Entity> m_StartMarkers;
            public List<Entity> m_EndMarkers;
        }

        // Segment puta u clipboardu: prefab + 4 bezier tačke (xz ofseti od
        // centroida + visine iznad terena) + nadogradnje (drvoredi, ivičnjaci).
        private struct NetEdgeClipboardItem
        {
            public Entity m_Prefab;
            public float2[] m_CurveOffsets;
            public float[] m_HeightOffsets;

            // Indeksi u tabelu cvorova klipborda (-1 = nepoznat, npr. stari
            // blueprint). Identitet cvora je JEDINI pouzdan nacin da dve
            // deonice dobiju bitski isti kraj - poklapanje po poziciji pada
            // cim se kriva i cvor malo raziđu.
            public int m_StartNodeIndex;
            public int m_EndNodeIndex;
            public bool m_HasUpgrade;
            public CompositionFlags m_Upgrade;
        }

        private readonly List<NetEdgeClipboardItem> m_ClipboardNetEdges = new List<NetEdgeClipboardItem>();

        // Tabela cvorova kopirane mreze: xz ofset od centroida + visina iznad
        // terena, jedan zapis po IZVORNOM cvoru. Sve deonice koje su delile
        // cvor dobijaju istu tacku pri lepljenju.
        private readonly List<float2> m_ClipboardNetNodeOffsets = new List<float2>();
        private readonly List<float> m_ClipboardNetNodeHeights = new List<float>();

        // Nadogradnje ČVORA. Igra iz njih izvodi kružni tok, semafore, stop
        // znakove i ponašanje raskrsnice (Roundabout / TrafficLights /
        // AllWayStop / FixedNodeSize / StraightNodeEnd ...) — bez njih se
        // kopirani kružni tok zalepi kao obična raskrsnica.
        private readonly List<bool> m_ClipboardNetNodeHasUpgrade = new List<bool>();
        private readonly List<CompositionFlags> m_ClipboardNetNodeUpgrades = new List<CompositionFlags>();

        // Prefab prve deonice koja koristi cvor - za node-upgrade definiciju.
        private readonly List<Entity> m_ClipboardNetNodePrefabs = new List<Entity>();

        // MARKERI cvora: kruzni tok (i rucni semafor/stop) u CS2 NIJE flag na
        // cvoru nego POD-OBJEKAT zakacen na cvor, ciji prefab nosi
        // NetObjectData.m_CompositionFlags; CompositionSelectSystem te flagove
        // cita iz sub-objekata (GetSubObjectFlags & nodeMask) i tek iz njih
        // NetComponentsSystem izvede Roundabout/TrafficLights komponente.
        // Dokaz iz dijagnostike: RA cvorovi originala NEMAJU Upgraded.
        private struct NetNodeMarker
        {
            public int m_NodeIndex;
            public Entity m_Prefab;
        }

        private readonly List<NetNodeMarker> m_ClipboardNetNodeMarkers = new List<NetNodeMarker>();

        // Vec emitovani marker (cvor, prefab) u tekucem paste prozoru — fixup
        // se vrti vise frejmova, a objekat nastaje tek frejm-dva kasnije, pa
        // bi se marker bez ovoga duplirao.
        private readonly HashSet<(Entity, Entity)> m_EmittedNodeMarkers = new HashSet<(Entity, Entity)>();

        private void RememberNodePrefab(int index, Entity prefab)
        {
            // Gornja granica je tabela cvorova: indeks iz osteceenog/rucno
            // menjanog blueprinta (npr. 2000000000) bi inace rastao listu do
            // OOM-a. Prefab za cvor van tabele se ionako nikad ne cita.
            if (index < 0 || index >= ClipboardNetNodeCount)
            {
                return;
            }

            while (m_ClipboardNetNodePrefabs.Count <= index)
            {
                m_ClipboardNetNodePrefabs.Add(Entity.Null);
            }

            if (m_ClipboardNetNodePrefabs[index] == Entity.Null)
            {
                m_ClipboardNetNodePrefabs[index] = prefab;
            }
        }

        // Sloj prefaba mreže mora da bude u dozvoljenima (bez struje/cevi).
        private bool IsAllowedNetLayer(Entity entity)
        {
            return EntityManager.TryGetComponent(entity, out PrefabRef prefabRef) &&
                EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetData netData) &&
                (netData.m_RequiredLayers & kSelectableNetLayers) != 0;
        }

        private bool IsSelectableNetNode(Entity node)
        {
            return EntityManager.Exists(node) &&
                EntityManager.HasComponent<Game.Net.Node>(node) &&
                EntityManager.HasComponent<Game.Net.NodeGeometry>(node) &&
                !EntityManager.HasComponent<Owner>(node) &&
                !EntityManager.HasComponent<Temp>(node) &&
                !EntityManager.HasComponent<Deleted>(node) &&
                IsAllowedNetLayer(node);
        }

        private bool IsSelectableNetEdge(Entity edge)
        {
            return EntityManager.Exists(edge) &&
                EntityManager.HasComponent<Game.Net.Edge>(edge) &&
                EntityManager.HasComponent<Game.Net.EdgeGeometry>(edge) &&
                EntityManager.HasComponent<Game.Net.Curve>(edge) &&
                !EntityManager.HasComponent<Owner>(edge) &&
                !EntityManager.HasComponent<Temp>(edge) &&
                !EntityManager.HasComponent<Deleted>(edge) &&
                IsAllowedNetLayer(edge);
        }

        private float GetNetNodeRadius(Entity node)
        {
            if (EntityManager.TryGetComponent(node, out Game.Net.NodeGeometry geometry))
            {
                float3 size = geometry.m_Bounds.max - geometry.m_Bounds.min;
                return math.max(kNetNodePickMin, math.cmax(size.xz) * 0.5f);
            }

            return kNetNodePickMin;
        }

        // Klik: kroz net quad tree; čvor u svom radijusu ima prednost nad
        // ivicom (raskrsnicu je inače nemoguće uhvatiti — ivice je prekrivaju).
        private bool TryPickNetAt(float3 position, out Entity node, out Entity edge)
        {
            node = Entity.Null;
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
                    position - new float3(kNetPickSearchRadius, 1000f, kNetPickSearchRadius),
                    position + new float3(kNetPickSearchRadius, 1000f, kNetPickSearchRadius)),
                m_Results = new NativeList<Entity>(32, Allocator.Temp),
            };
            tree.Iterate(ref iterator, 0);

            float bestNodeScore = float.MaxValue;
            float bestEdgeScore = float.MaxValue;
            for (int i = 0; i < iterator.m_Results.Length; i++)
            {
                Entity candidate = iterator.m_Results[i];
                if (EntityManager.TryGetComponent(candidate, out Game.Net.Node nodeData))
                {
                    if (!IsSelectableNetNode(candidate) || !MatchesUndergroundMode(nodeData.m_Position))
                    {
                        continue;
                    }

                    float distance = math.distance(nodeData.m_Position.xz, position.xz);
                    if (distance > GetNetNodeRadius(candidate))
                    {
                        continue;
                    }

                    float score = distance + (kNetPickHeightWeight * math.abs(nodeData.m_Position.y - position.y));
                    if (score < bestNodeScore)
                    {
                        bestNodeScore = score;
                        node = candidate;
                    }
                }
                else if (EntityManager.TryGetComponent(candidate, out Game.Net.Curve curve))
                {
                    if (!IsSelectableNetEdge(candidate))
                    {
                        continue;
                    }

                    MathUtils.Distance(curve.m_Bezier, position, out float t);
                    float3 closest = MathUtils.Position(curve.m_Bezier, t);
                    float distance = math.distance(closest.xz, position.xz);
                    if (distance > kNetEdgePickThreshold || !MatchesUndergroundMode(closest))
                    {
                        continue;
                    }

                    float score = distance + (kNetPickHeightWeight * math.abs(closest.y - position.y));
                    if (score < bestEdgeScore)
                    {
                        bestEdgeScore = score;
                        edge = candidate;
                    }
                }
            }

            iterator.m_Results.Dispose();
            if (node != Entity.Null)
            {
                edge = Entity.Null;
                return true;
            }

            return edge != Entity.Null;
        }

        // Marquee: kroz net quad tree nad AABB okvirom (pun sken grada bi na
        // velikoj mapi bio štucanje od desetina ms) — čvor ulazi kad mu je
        // pozicija u okviru, ivica kad su joj OBA kraja unutra.
        private void CollectNetworksInMarquee()
        {
            if (!SelectNetworks)
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

            bool InBox(float2 point)
            {
                float2 uv = ToMarqueeSpace(point);
                return uv.x >= uMin && uv.x <= uMax && uv.y >= vMin && uv.y <= vMax;
            }

            // AABB okvira u svetu: sve 4 ćoška kamera-poravnatog boksa.
            float2 c0 = m_MarqueeStart.xz + (m_MarqueeRight * uMin) + (m_MarqueeForward * vMin);
            float2 c1 = m_MarqueeStart.xz + (m_MarqueeRight * uMax) + (m_MarqueeForward * vMin);
            float2 c2 = m_MarqueeStart.xz + (m_MarqueeRight * uMin) + (m_MarqueeForward * vMax);
            float2 c3 = m_MarqueeStart.xz + (m_MarqueeRight * uMax) + (m_MarqueeForward * vMax);
            float2 boundsMin = math.min(math.min(c0, c1), math.min(c2, c3));
            float2 boundsMax = math.max(math.max(c0, c1), math.max(c2, c3));

            if (m_NetSearchSystem == null)
            {
                m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            }

            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle dependencies);
            dependencies.Complete();

            RoadIterator iterator = new RoadIterator
            {
                m_Bounds = new Bounds3(
                    new float3(boundsMin.x, -1000f, boundsMin.y),
                    new float3(boundsMax.x, 1000f, boundsMax.y)),
                m_Results = new NativeList<Entity>(64, Allocator.Temp),
            };
            tree.Iterate(ref iterator, 0);

            HashSet<Entity> selectedNodes = new HashSet<Entity>(m_SelectedNodes);
            HashSet<Entity> selectedEdges = new HashSet<Entity>(m_SelectedNetEdges);
            for (int i = 0; i < iterator.m_Results.Length; i++)
            {
                if (SelectedCount >= kMaxSelection)
                {
                    break;
                }

                Entity candidate = iterator.m_Results[i];
                // T-filter važi i za mreže — obećanje filtera je "samo taj tip".
                if (m_SameFilterPrefab != Entity.Null &&
                    (!EntityManager.TryGetComponent(candidate, out PrefabRef candidatePrefab) ||
                        candidatePrefab.m_Prefab != m_SameFilterPrefab))
                {
                    continue;
                }

                if (EntityManager.TryGetComponent(candidate, out Game.Net.Node nodeData))
                {
                    if (!selectedNodes.Contains(candidate) &&
                        InBox(nodeData.m_Position.xz) &&
                        MatchesUndergroundMode(nodeData.m_Position) &&
                        IsSelectableNetNode(candidate))
                    {
                        m_SelectedNodes.Add(candidate);
                        selectedNodes.Add(candidate);
                    }
                }
                else if (EntityManager.HasComponent<Game.Net.Edge>(candidate))
                {
                    // Ivica upada čim je okvir PRESEČE — uzorci krive se spajaju
                    // u duži i seku sa okvirom. (Uslov "oba čvora unutra" je
                    // promašivao svaki duži segment; a sam test tačaka bi
                    // promašio mali okvir postavljen između dva uzorka.)
                    if (!selectedEdges.Contains(candidate) &&
                        EntityManager.TryGetComponent(candidate, out Game.Net.Curve edgeCurve) &&
                        IsSelectableNetEdge(candidate))
                    {
                        if (!MatchesUndergroundMode(LaneMidpoint(edgeCurve.m_Bezier)))
                        {
                            continue;
                        }

                        float2 previous = ToMarqueeSpace(MathUtils.Position(edgeCurve.m_Bezier, 0f).xz);
                        for (int s = 1; s <= kNetMarqueeSamples; s++)
                        {
                            float2 current = ToMarqueeSpace(MathUtils.Position(edgeCurve.m_Bezier, s / (float)kNetMarqueeSamples).xz);
                            if (SegmentIntersectsBox(previous, current, uMin, uMax, vMin, vMax))
                            {
                                m_SelectedNetEdges.Add(candidate);
                                selectedEdges.Add(candidate);
                                break;
                            }

                            previous = current;
                        }
                    }
                }
            }

            iterator.m_Results.Dispose();

            if (m_SelectedNodes.Count > 0 || m_SelectedNetEdges.Count > 0)
            {
                Mod.Log.Info($"Copaste: {m_SelectedNodes.Count} net nodes, {m_SelectedNetEdges.Count} net edges in selection");
            }
        }

        // Pokretni skup: selektovani čvorovi + krajevi selektovanih ivica.
        // Puni deljeni scratch — pozivalac NE sme da drži referencu preko
        // sledećeg poziva.
        private HashSet<Entity> BuildMovingNodeSet()
        {
            m_NetMovingScratch.Clear();
            foreach (Entity node in m_SelectedNodes)
            {
                if (EntityManager.Exists(node) && !EntityManager.HasComponent<Deleted>(node))
                {
                    m_NetMovingScratch.Add(node);
                }
            }

            foreach (Entity edge in m_SelectedNetEdges)
            {
                if (EntityManager.Exists(edge) &&
                    !EntityManager.HasComponent<Deleted>(edge) &&
                    EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData))
                {
                    if (EntityManager.Exists(edgeData.m_Start))
                    {
                        m_NetMovingScratch.Add(edgeData.m_Start);
                    }

                    if (EntityManager.Exists(edgeData.m_End))
                    {
                        m_NetMovingScratch.Add(edgeData.m_End);
                    }
                }
            }

            return m_NetMovingScratch;
        }

        // Svetska xz tačka u koordinate marquee okvira (u = desno, v = napred).
        private float2 ToMarqueeSpace(float2 point)
        {
            float2 offset = point - m_MarqueeStart.xz;
            return new float2(math.dot(offset, m_MarqueeRight), math.dot(offset, m_MarqueeForward));
        }

        // Preseca li duž (u,v prostor) pravougaonik okvira — Liang-Barsky
        // odsecanje parametra. Duž koja samo prolazi kroz okvir, bez ijedne
        // krajnje tačke unutra, takođe se računa.
        private static bool SegmentIntersectsBox(float2 a, float2 b, float uMin, float uMax, float vMin, float vMax)
        {
            float t0 = 0f;
            float t1 = 1f;
            float2 delta = b - a;

            for (int axis = 0; axis < 2; axis++)
            {
                float p = axis == 0 ? delta.x : delta.y;
                float origin = axis == 0 ? a.x : a.y;
                float min = axis == 0 ? uMin : vMin;
                float max = axis == 0 ? uMax : vMax;

                if (math.abs(p) < 1e-6f)
                {
                    // Paralelna osi: mimo pojasa znači bez preseka.
                    if (origin < min || origin > max)
                    {
                        return false;
                    }

                    continue;
                }

                float tA = (min - origin) / p;
                float tB = (max - origin) / p;
                if (tA > tB)
                {
                    (tA, tB) = (tB, tA);
                }

                t0 = math.max(t0, tA);
                t1 = math.min(t1, tB);
                if (t0 > t1)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryGetNetCenter(EntityManager manager, HashSet<Entity> moving, out float3 center)
        {
            center = float3.zero;
            int count = 0;
            foreach (Entity node in moving)
            {
                if (manager.TryGetComponent(node, out Game.Net.Node nodeData))
                {
                    center += nodeData.m_Position;
                    count++;
                }
            }

            if (count == 0)
            {
                return false;
            }

            center /= count;
            return true;
        }

        // Prosečna pozicija pokretnog skupa — referenca za move drag i pivot.
        private bool TryGetNetSelectionCenter(out float3 center)
        {
            return TryGetNetCenter(EntityManager, BuildMovingNodeSet(), out center);
        }

        // ALT tokom vuče: selektovan JE SAMO jedan čvor. Među-čvor (dva prava
        // kraka) klizi po duži između svoja dva suseda; krajnji čvor (jedan
        // krak) po pravcu sopstvene deonice (visina i dalje prati
        // teren sa očuvanim ofsetom; bočni ofseti krajeva se čuvaju kao i pri
        // svakom pomeranju čvora). Vraća true kad je korak potrošen.
        private bool TrySlideNodeAlongLine(float3 anchor, HashSet<Entity> moving, bool tick)
        {
            if (UnityEngine.InputSystem.Keyboard.current == null ||
                !UnityEngine.InputSystem.Keyboard.current.altKey.isPressed ||
                m_SelectedNodes.Count != 1 || m_SelectedNetEdges.Count != 0 ||
                m_Selected.Count != 0 || m_SelectedSurfaces.Count != 0 || m_SelectedLanes.Count != 0)
            {
                return false;
            }

            Entity node = m_SelectedNodes[0];
            if (!EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
            {
                return false;
            }

            int arms = GetNodeRealEdges(node, out Entity armA, out Entity armB);
            float2 desired = anchor.xz + m_NetMoveOffset;
            float2 target;
            if (arms == 2)
            {
                // MEĐU-čvor: duž između dva susedna čvora.
                if (!EntityManager.TryGetComponent(armA, out Game.Net.Edge edgeA) ||
                    !EntityManager.TryGetComponent(armB, out Game.Net.Edge edgeB))
                {
                    return false;
                }

                Entity neighborA = edgeA.m_Start == node ? edgeA.m_End : edgeA.m_Start;
                Entity neighborB = edgeB.m_Start == node ? edgeB.m_End : edgeB.m_Start;
                if (neighborA == neighborB ||
                    !EntityManager.TryGetComponent(neighborA, out Game.Net.Node aData) ||
                    !EntityManager.TryGetComponent(neighborB, out Game.Net.Node bData))
                {
                    return false;
                }

                float2 lineDelta = bData.m_Position.xz - aData.m_Position.xz;
                float lineLength = math.length(lineDelta);
                if (lineLength < 1e-2f)
                {
                    return false;
                }

                // Klamp drži čvor bar 1,5 m od suseda — nulta deonica ruši spoj.
                float tMin = math.min(0.45f, 1.5f / lineLength);
                float t = math.clamp(math.dot(desired - aData.m_Position.xz, lineDelta) / (lineLength * lineLength), tMin, 1f - tMin);
                target = math.lerp(aData.m_Position.xz, bData.m_Position.xz, t);
            }
            else if (arms == 1)
            {
                // KRAJNJI čvor (slepi kraj): referenca je UNUTRAŠNJA deonica —
                // kraj se lepi na produžetak prave kroz sledeća dva čvora
                // (srednji → sused), pa se kriva završna deonica ispravlja u
                // nastavak puta. Put od jedne jedine deonice nema unutrašnju
                // referencu, pa tada važi osa sopstvene deonice.
                if (!EntityManager.TryGetComponent(armA, out Game.Net.Edge edgeA))
                {
                    return false;
                }

                Entity neighbor = edgeA.m_Start == node ? edgeA.m_End : edgeA.m_Start;
                if (!EntityManager.TryGetComponent(neighbor, out Game.Net.Node nData))
                {
                    return false;
                }

                float2 origin = nData.m_Position.xz;
                float2 direction = nodeData.m_Position.xz - origin;
                if (GetNodeRealEdges(neighbor, out Entity nArmA, out Entity nArmB) == 2)
                {
                    Entity innerArm = nArmA == armA ? nArmB : nArmA;
                    if (EntityManager.TryGetComponent(innerArm, out Game.Net.Edge innerEdge))
                    {
                        Entity middle = innerEdge.m_Start == neighbor ? innerEdge.m_End : innerEdge.m_Start;
                        if (middle != node &&
                            EntityManager.TryGetComponent(middle, out Game.Net.Node mData))
                        {
                            direction = origin - mData.m_Position.xz;
                        }
                    }
                }

                float directionLength = math.length(direction);
                if (directionLength < 1e-2f)
                {
                    return false;
                }

                direction /= directionLength;
                float along = math.max(1.5f, math.dot(desired - origin, direction));
                target = origin + direction * along;
            }
            else
            {
                return false;
            }

            float2 step = target - nodeData.m_Position.xz;
            if (math.lengthsq(step) > 1e-6f)
            {
                TransformNetSelection(quaternion.identity, float3.zero, new float3(step.x, 0f, step.y), tick, moving);
            }

            return true;
        }

        // Jedan korak transformacije (poziva se po frejmu gesta). tick = pun
        // update interval; mali skupovi dobijaju pun update svaki frejm.
        // moving je opcioni već izgrađen skup (da se ne gradi dvaput po frejmu).
        private void TransformNetSelection(quaternion rotation, float3 pivot, float3 delta, bool tick, HashSet<Entity> moving = null)
        {
            moving ??= BuildMovingNodeSet();
            if (moving.Count == 0)
            {
                return;
            }

            // Pun update svaki frejm za male skupove. Probano je da se tokom
            // poteza svede na tick (4x u sekundi) kako bi igra ređe urezivala
            // teren ispod puta — ali se tada geometrija puta ponovo crta samo
            // na tick, pa put skače u koracima dok ga vučeš. Glatkoća poteza
            // je preča: teren koji put ureže tamo kuda prođe je ponašanje
            // igre i ne poništava se ni njenim sopstvenim alatima.
            bool fullMark = tick || moving.Count <= kNetFullMarkLimit;
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();

            // FAZA 1: popiši sve ivice pokretnih čvorova IZ bafera pre bilo
            // kakve strukturne izmene (AddComponent bi invalidirao bafere).
            m_NetEdgeScratch.Clear();
            m_NetSeenEdgeScratch.Clear();
            foreach (Entity node in moving)
            {
                if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
                {
                    continue;
                }

                for (int i = 0; i < connected.Length; i++)
                {
                    if (m_NetSeenEdgeScratch.Add(connected[i].m_Edge))
                    {
                        m_NetEdgeScratch.Add(connected[i].m_Edge);
                    }
                }
            }

            // FAZA 2: pomeri čvorove (pozicija prati teren uz očuvan visinski
            // ofset — isto pravilo kao sve ostalo u alatu) + njihove stubove.
            m_NetOldNodePos.Clear();
            foreach (Entity node in moving)
            {
                if (!EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
                {
                    continue;
                }

                float3 oldPosition = nodeData.m_Position;
                m_NetOldNodePos[node] = oldPosition;
                float3 newPosition = TransformNetNodePoint(ref heightData, node, oldPosition, rotation, pivot, delta);
                nodeData.m_Position = newPosition;
                nodeData.m_Rotation = math.normalize(math.mul(rotation, nodeData.m_Rotation));
                EntityManager.SetComponentData(node, nodeData);

                // Elevacija se osvežava uz poziciju: ona je ulaz u izbor
                // kompozicije, pa nesaglasnost između čvora i deonice daje
                // prelaz visine i završetak mosta nasred trase. Na prizemnom
                // putu vrednost je nula i SetNetElevation skine komponentu.
                SetNetElevation(node, newPosition.y - TerrainUtils.SampleHeight(ref heightData, newPosition));

                // NodeGeometry je izvedena geometrija (igra je regeneriše na
                // Updated) — pomera se samo da međufrejmovi ne "kasne".
                if (EntityManager.TryGetComponent(node, out Game.Net.NodeGeometry geometry))
                {
                    float3 shift = newPosition - oldPosition;
                    geometry.m_Bounds.min += shift;
                    geometry.m_Bounds.max += shift;
                    geometry.m_Offset += shift.y;
                    EntityManager.SetComponentData(node, geometry);
                }

                TransformNetSubObjects(node, rotation, pivot, delta, ref heightData);
            }

            // FAZA 3: ivice — krute prate ceo gest, susedi samo kraj.
            foreach (Entity edge in m_NetEdgeScratch)
            {
                if (!EntityManager.Exists(edge) ||
                    EntityManager.HasComponent<Deleted>(edge) ||
                    !EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData) ||
                    !EntityManager.TryGetComponent(edge, out Game.Net.Curve curve))
                {
                    continue;
                }

                bool startMoving = moving.Contains(edgeData.m_Start);
                bool endMoving = moving.Contains(edgeData.m_End);

                // Local-connection ivica (čvor joj nije kraj) nije ni kruta ni
                // susedna — nema šta da joj se menja.
                if (!startMoving && !endMoving)
                {
                    continue;
                }

                if (startMoving && endMoving &&
                    EntityManager.TryGetComponent(edgeData.m_Start, out Game.Net.Node rigidStart) &&
                    EntityManager.TryGetComponent(edgeData.m_End, out Game.Net.Node rigidEnd))
                {
                    // KRUTO, bez per-tačka terena: sve 4 tačke ista rotacija +
                    // delta, pa se visinska korekcija sa krajeva (teren-follow
                    // čvorova iz faze 2) interpolira na b/c — most preko doline
                    // zadržava luk umesto da se "prospe" po terenu.
                    float3 ra = pivot + math.mul(rotation, curve.m_Bezier.a - pivot) + delta;
                    float3 rb = pivot + math.mul(rotation, curve.m_Bezier.b - pivot) + delta;
                    float3 rc = pivot + math.mul(rotation, curve.m_Bezier.c - pivot) + delta;
                    float3 rd = pivot + math.mul(rotation, curve.m_Bezier.d - pivot) + delta;

                    // Kraj krive NE sme na centar čvora: njegov bočni ofset od
                    // čvora JE poravnanje traka (npr. 3-lane vezan uz levu
                    // ivicu) — kraj se transformiše kao tačka, a sa čvora se
                    // preuzima samo VISINSKA korekcija terena.
                    float3 newA = NetEndpointFollowNode(curve.m_Bezier.a, edgeData.m_Start, rigidStart, rotation, ra);
                    float3 newD = NetEndpointFollowNode(curve.m_Bezier.d, edgeData.m_End, rigidEnd, rotation, rd);
                    float dyA = newA.y - ra.y;
                    float dyD = newD.y - rd.y;
                    curve.m_Bezier.a = newA;
                    curve.m_Bezier.d = newD;
                    curve.m_Bezier.b = rb + new float3(0f, math.lerp(dyA, dyD, 1f / 3f), 0f);
                    curve.m_Bezier.c = rc + new float3(0f, math.lerp(dyA, dyD, 2f / 3f), 0f);
                    TransformNetSubObjects(edge, rotation, pivot, delta, ref heightData);
                }
                else
                {
                    // Sused: kontrolne tačke se vode kao OFSET od tetive, isto
                    // kao u MoveCurveEndpoint. Tetivne proporcije su ovde bile
                    // ista zamka: na skoro pravoj ivici t ispadne ogroman,
                    // klamp ga odseče i ručno dignut luk susedne deonice se
                    // spljošti čim se pomeri čvor.
                    float3 offsetB = curve.m_Bezier.b - math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, 1f / 3f);
                    float3 offsetC = curve.m_Bezier.c - math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, 2f / 3f);
                    float2 neighbourOldChord = (curve.m_Bezier.d - curve.m_Bezier.a).xz;

                    if (startMoving && EntityManager.TryGetComponent(edgeData.m_Start, out Game.Net.Node movedStart))
                    {
                        curve.m_Bezier.a = NetEndpointFollowNode(curve.m_Bezier.a, edgeData.m_Start, movedStart, rotation, default);
                    }

                    if (endMoving && EntityManager.TryGetComponent(edgeData.m_End, out Game.Net.Node movedEnd))
                    {
                        curve.m_Bezier.d = NetEndpointFollowNode(curve.m_Bezier.d, edgeData.m_End, movedEnd, rotation, default);
                    }

                    float2 neighbourNewChord = (curve.m_Bezier.d - curve.m_Bezier.a).xz;
                    float neighbourOldLength = math.length(neighbourOldChord);
                    float neighbourNewLength = math.length(neighbourNewChord);
                    if (neighbourOldLength > 1e-3f && neighbourNewLength > 1e-3f)
                    {
                        offsetB.xz = RotateAndScale(offsetB.xz, neighbourOldChord / neighbourOldLength, neighbourNewChord / neighbourNewLength, math.clamp(neighbourNewLength / neighbourOldLength, 0.25f, 4f));
                        offsetC.xz = RotateAndScale(offsetC.xz, neighbourOldChord / neighbourOldLength, neighbourNewChord / neighbourNewLength, math.clamp(neighbourNewLength / neighbourOldLength, 0.25f, 4f));
                    }

                    curve.m_Bezier.b = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, 1f / 3f) + offsetB;
                    curve.m_Bezier.c = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, 2f / 3f) + offsetC;
                }

                // Dužina se računa uvek: potez ume da se završi na frejmu bez
                // punog update-a, pa bi ivici ostala dužina od pre do 0.25 s
                // (pogrešna vremena putovanja i razmak traka).
                curve.m_Length = MathUtils.Length(curve.m_Bezier);
                EntityManager.SetComponentData(edge, curve);

                // I elevacija deonice prati novu krivu — iz istog razloga kao
                // kod čvora: nesaglasna visina daje prelazni komad i most se
                // "preseče" nasred trase.
                float3 edgeMiddle = LaneMidpoint(curve.m_Bezier);
                SetNetElevation(edge, edgeMiddle.y - TerrainUtils.SampleHeight(ref heightData, edgeMiddle));
            }

            // FAZA 4: strukturne izmene na kraju, kad su svi baferi pušteni.
            if (fullMark)
            {
                foreach (Entity node in moving)
                {
                    if (EntityManager.Exists(node))
                    {
                        EntityManager.AddComponent<Updated>(node);
                        EntityManager.AddComponent<BatchesUpdated>(node);
                    }
                }

                foreach (Entity edge in m_NetEdgeScratch)
                {
                    if (!EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge))
                    {
                        continue;
                    }

                    EntityManager.AddComponent<Updated>(edge);
                    EntityManager.AddComponent<BatchesUpdated>(edge);

                    // Dalji čvor suseda mora u update da se raskrsnica preračuna.
                    if (EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData))
                    {
                        Entity farNode = moving.Contains(edgeData.m_Start) ? edgeData.m_End : edgeData.m_Start;
                        if (!moving.Contains(farNode))
                        {
                            MarkFarNodeAndItsEdges(farNode);
                        }
                    }
                }
            }
        }

        // Kraj krive prati SVOJ čvor čuvajući ofset: end' = node' +
        // rot*(endStari − nodeStari). Ako stara pozicija čvora nije zapamćena
        // (defenzivno), pada se na rigidFallback (stari način) kada postoji,
        // inače na sam čvor.
        // Pomeranje ČVORA puta, po pravilu: uzdignuta mreža drži svoju visinu,
        // prizemna prati teren.
        //
        // Zašto: visina uzdignute deonice je svojstvo puta, ne terena ispod
        // njega. Kad bi se čuvao razmak do terena, čvor pomeren preko kosine
        // pomerio bi i sam most za visinsku razliku terena — spoj bi dobio
        // stepenik, a susedne deonice bi ostale na različitim visinama. Ivice
        // se pomeraju u svetskim koordinatama, pa čvor mora isto.
        //
        // Praćenje terena ostaje za prizemne puteve (i za ograde, koje po
        // prirodi leže na tlu).
        private float3 TransformNetNodePoint(ref TerrainHeightData heightData, Entity node, float3 point, quaternion rotation, float3 pivot, float3 delta)
        {
            float3 position = pivot + math.mul(rotation, point - pivot) + delta;

            bool elevated = false;
            if (EntityManager.TryGetComponent(node, out Game.Net.Elevation nodeElevation) &&
                math.max(math.abs(nodeElevation.m_Elevation.x), math.abs(nodeElevation.m_Elevation.y)) > 0.5f)
            {
                elevated = true;
            }
            else if (EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
            {
                for (int i = 0; i < connected.Length && !elevated; i++)
                {
                    if (EntityManager.TryGetComponent(connected[i].m_Edge, out Game.Net.Elevation edgeElevation) &&
                        math.max(math.abs(edgeElevation.m_Elevation.x), math.abs(edgeElevation.m_Elevation.y)) > 0.5f)
                    {
                        elevated = true;
                    }
                }
            }

            if (elevated)
            {
                position.y = point.y + delta.y;
                return position;
            }

            float heightOffset = point.y - TerrainUtils.SampleHeight(ref heightData, point);
            position.y = TerrainUtils.SampleHeight(ref heightData, position) + heightOffset;
            return position;
        }

        // Dalji čvor ulazi u preračun zajedno sa SVIM svojim deonicama:
        // geometrija spoja se računa po čvoru, pa deonica koja nije označena
        // zadrži stari oblik kraja i na spoju se vidi stepenik, iako su krive
        // neprekidne.
        private void MarkFarNodeAndItsEdges(Entity farNode)
        {
            if (!EntityManager.Exists(farNode) || EntityManager.HasComponent<Deleted>(farNode))
            {
                return;
            }

            EntityManager.AddComponent<Updated>(farNode);
            EntityManager.AddComponent<BatchesUpdated>(farNode);

            if (!EntityManager.TryGetBuffer(farNode, true, out DynamicBuffer<Game.Net.ConnectedEdge> farEdges))
            {
                return;
            }

            m_FarEdgeScratch.Clear();
            for (int i = 0; i < farEdges.Length; i++)
            {
                m_FarEdgeScratch.Add(farEdges[i].m_Edge);
            }

            foreach (Entity other in m_FarEdgeScratch)
            {
                if (EntityManager.Exists(other) && !EntityManager.HasComponent<Deleted>(other))
                {
                    EntityManager.AddComponent<Updated>(other);
                    EntityManager.AddComponent<BatchesUpdated>(other);
                }
            }
        }

        private readonly List<Entity> m_FarEdgeScratch = new List<Entity>();

        private float3 NetEndpointFollowNode(float3 endpoint, Entity node, Game.Net.Node movedNode, quaternion rotation, float3 rigidFallback)
        {
            if (m_NetOldNodePos.TryGetValue(node, out float3 oldNode))
            {
                return movedNode.m_Position + math.mul(rotation, endpoint - oldNode);
            }

            return rigidFallback.Equals(default) ? movedNode.m_Position : rigidFallback;
        }

        // Stubovi i drugi prikačeni pod-objekti čvora/ivice prate transformaciju.
        // Deca se prvo POPIŠU (AddComponent na dete bi invalidirao bafer).
        private void TransformNetSubObjects(Entity parent, quaternion rotation, float3 pivot, float3 delta, ref TerrainHeightData heightData)
        {
            if (!EntityManager.TryGetBuffer(parent, true, out DynamicBuffer<Game.Objects.SubObject> subObjects))
            {
                return;
            }

            m_NetChildScratch.Clear();
            for (int i = 0; i < subObjects.Length; i++)
            {
                m_NetChildScratch.Add(subObjects[i].m_SubObject);
            }

            foreach (Entity child in m_NetChildScratch)
            {
                if (!EntityManager.Exists(child) ||
                    !EntityManager.HasComponent<Game.Objects.Attached>(child) ||
                    !EntityManager.TryGetComponent(child, out Game.Objects.Transform transform))
                {
                    continue;
                }

                transform.m_Position = TransformLanePoint(ref heightData, transform.m_Position, rotation, pivot, delta);
                transform.m_Rotation = math.normalize(math.mul(rotation, transform.m_Rotation));
                EntityManager.SetComponentData(child, transform);
                EntityManager.AddComponent<Updated>(child);
                EntityManager.AddComponent<BatchesUpdated>(child);
            }
        }

        // Završni settle: pun Updated preko celog pokretnog skupa, njegovih
        // ivica, daljih čvorova i agregata (imena ulica), plus odloženi drugi
        // prolaz — search stabla kasne frejm iza upisa. Zgrade se NE diraju:
        // ivica sa ConnectedBuilding + Updated je okidač da igra sama preveže
        // Building.m_RoadEdge (isti mehanizam kao kod pomeranja zgrada).
        private void SettleNetworks()
        {
            HashSet<Entity> moving = BuildMovingNodeSet();
            if (moving.Count == 0)
            {
                return;
            }

            // Popis ivica pre strukturnih izmena.
            m_NetEdgeScratch.Clear();
            m_NetSeenEdgeScratch.Clear();
            foreach (Entity node in moving)
            {
                if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
                {
                    continue;
                }

                for (int i = 0; i < connected.Length; i++)
                {
                    if (m_NetSeenEdgeScratch.Add(connected[i].m_Edge))
                    {
                        m_NetEdgeScratch.Add(connected[i].m_Edge);
                    }
                }
            }

            foreach (Entity node in moving)
            {
                if (!EntityManager.Exists(node))
                {
                    continue;
                }

                EntityManager.AddComponent<Updated>(node);
                EntityManager.AddComponent<BatchesUpdated>(node);
                m_DelayedNetSettle[node] = 4;
            }

            foreach (Entity edge in m_NetEdgeScratch)
            {
                if (!EntityManager.Exists(edge))
                {
                    continue;
                }

                EntityManager.AddComponent<Updated>(edge);
                EntityManager.AddComponent<BatchesUpdated>(edge);

                if (EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData))
                {
                    Entity farNode = moving.Contains(edgeData.m_Start) ? edgeData.m_End : edgeData.m_Start;
                    MarkFarNodeAndItsEdges(farNode);
                }

                // Ime ulice prati spojene deonice.
                if (EntityManager.TryGetComponent(edge, out Game.Net.Aggregated aggregated) &&
                    EntityManager.Exists(aggregated.m_Aggregate))
                {
                    EntityManager.AddComponent<Updated>(aggregated.m_Aggregate);
                }
            }

            NetProbe("posle pomeranja mreze (settle)");
        }

        // Odloženi settle: jedan re-okidač po čvoru N frejmova posle poteza.
        private void RunDelayedNetSettles()
        {
            if (m_DelayedNetSettle.Count == 0)
            {
                return;
            }

            m_NetSettleKeyScratch.Clear();
            m_NetSettleKeyScratch.AddRange(m_DelayedNetSettle.Keys);
            foreach (Entity node in m_NetSettleKeyScratch)
            {
                int frames = m_DelayedNetSettle[node];
                if (frames > 1)
                {
                    m_DelayedNetSettle[node] = frames - 1;
                    continue;
                }

                m_DelayedNetSettle.Remove(node);
                ResettleNetNode(node);
            }
        }

        // Alat se gasi: odbrojavanje ne bi imalo ko da otkucava (OnUpdate ne
        // radi), a preživeli ID-jevi bi u SLEDEĆOJ učitanoj igri pokazivali na
        // tuđe entitete — okini odmah i očisti.
        private void FlushNetSettles()
        {
            if (m_DelayedNetSettle.Count == 0)
            {
                return;
            }

            List<Entity> pending = new List<Entity>(m_DelayedNetSettle.Keys);
            m_DelayedNetSettle.Clear();
            foreach (Entity node in pending)
            {
                ResettleNetNode(node);
            }
        }

        private void ResettleNetNode(Entity node)
        {
            if (!EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node))
            {
                return;
            }

            // Popis pa mutacija — AddComponent invalidira bafer.
            m_NetChildScratch.Clear();
            if (EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
            {
                for (int i = 0; i < connected.Length; i++)
                {
                    m_NetChildScratch.Add(connected[i].m_Edge);
                }
            }

            EntityManager.AddComponent<Updated>(node);
            foreach (Entity edge in m_NetChildScratch)
            {
                if (EntityManager.Exists(edge))
                {
                    EntityManager.AddComponent<Updated>(edge);
                }
            }
        }

        // Keširan hover kandidat sme da preživi frejm samo ako i dalje postoji
        // i nije u međuvremenu ušao u selekciju.
        private Entity HoverCandidateStillValid(Entity candidate, List<Entity> selected)
        {
            if (candidate == Entity.Null ||
                !EntityManager.Exists(candidate) ||
                EntityManager.HasComponent<Deleted>(candidate) ||
                selected.Contains(candidate))
            {
                return Entity.Null;
            }

            return candidate;
        }

        // Hover po frejmu: kandidat pod kursorom (samo kad ništa ne vučemo i
        // kad raycast nije na propu — prop ima svoj beli krug).
        private void UpdateNetHover(bool raycastValid, Entity hitEntity, float3 position)
        {
            m_NetHoverNode = Entity.Null;
            m_NetHoverEdge = Entity.Null;
            m_LaneHoverEntity = Entity.Null;

            if (!raycastValid || hitEntity != Entity.Null ||
                m_MoveDragging || m_MarqueeActive || m_RightDragging || m_HandleDragging)
            {
                m_NetHoverLastPosition = new float3(float.MaxValue);
                return;
            }

            if (math.distance(position.xz, m_NetHoverLastPosition.xz) < 0.25f)
            {
                // Kursor miruje: zadrži prošli hover bez novog picka — ali kroz
                // iste filtere, da beli hover prsten ne ostane na entitetu koji
                // je u međuvremenu selektovan (ili obrisan).
                m_LaneHoverEntity = HoverCandidateStillValid(m_NetHoverPrevLane, m_SelectedLanes);
                m_NetHoverNode = HoverCandidateStillValid(m_NetHoverPrevNode, m_SelectedNodes);
                m_NetHoverEdge = HoverCandidateStillValid(m_NetHoverPrevEdge, m_SelectedNetEdges);
                return;
            }

            m_NetHoverLastPosition = position;

            if (SelectFences && TryPickLaneAt(position, out Entity hoverLane) &&
                !m_SelectedLanes.Contains(hoverLane))
            {
                m_LaneHoverEntity = hoverLane;
            }
            else if (SelectNetworks && TryPickNetAt(position, out Entity hoverNode, out Entity hoverEdge))
            {
                if (hoverNode != Entity.Null && !m_SelectedNodes.Contains(hoverNode))
                {
                    m_NetHoverNode = hoverNode;
                }
                else if (hoverEdge != Entity.Null && !m_SelectedNetEdges.Contains(hoverEdge))
                {
                    m_NetHoverEdge = hoverEdge;
                }
            }

            m_NetHoverPrevLane = m_LaneHoverEntity;
            m_NetHoverPrevNode = m_NetHoverNode;
            m_NetHoverPrevEdge = m_NetHoverEdge;
        }

        // Beli obris hover kandidata — isti oblici kao selekcija.
        private void DrawNetHoverOverlays(OverlayRenderSystem.Buffer overlayBuffer)
        {
            if (m_LaneHoverEntity != Entity.Null &&
                EntityManager.TryGetComponent(m_LaneHoverEntity, out Game.Net.Curve laneCurve))
            {
                DrawTessellatedHover(overlayBuffer, laneCurve.m_Bezier, kNetOverlaySegments, 0.3f);
            }

            if (m_NetHoverNode != Entity.Null &&
                EntityManager.TryGetComponent(m_NetHoverNode, out Game.Net.Node nodeData))
            {
                overlayBuffer.DrawCircle(kHoverColor, default, 0.25f, 0, new float2(0f, 1f), nodeData.m_Position, GetNetNodeRadius(m_NetHoverNode) * 2f);
            }

            if (m_NetHoverEdge != Entity.Null)
            {
                if (EntityManager.TryGetComponent(m_NetHoverEdge, out Game.Net.EdgeGeometry geometry))
                {
                    DrawTessellatedHover(overlayBuffer, geometry.m_Start.m_Left, kNetOverlaySegments / 2, 0.35f);
                    DrawTessellatedHover(overlayBuffer, geometry.m_Start.m_Right, kNetOverlaySegments / 2, 0.35f);
                    DrawTessellatedHover(overlayBuffer, geometry.m_End.m_Left, kNetOverlaySegments / 2, 0.35f);
                    DrawTessellatedHover(overlayBuffer, geometry.m_End.m_Right, kNetOverlaySegments / 2, 0.35f);
                }
                else if (EntityManager.TryGetComponent(m_NetHoverEdge, out Game.Net.Curve edgeCurve))
                {
                    DrawTessellatedHover(overlayBuffer, edgeCurve.m_Bezier, kNetOverlaySegments, 0.35f);
                }
            }
        }

        private void DrawTessellatedHover(OverlayRenderSystem.Buffer overlayBuffer, Bezier4x3 bezier, int segments, float width)
        {
            segments = math.max(2, segments);
            float3 previous = MathUtils.Position(bezier, 0f);
            for (int s = 1; s <= segments; s++)
            {
                float3 current = MathUtils.Position(bezier, s / (float)segments);
                overlayBuffer.DrawLine(kHoverColor, new Line3.Segment(previous, current), width);
                previous = current;
            }
        }

        // Obris selekcije: krug na čvoru (radijus iz NodeGeometry), teselirana
        // kriva na ivici — isti stil kao ograde.
        // Deonica se UVEK ocrtava po ivicama kolovoza. Pokušaj da se iz
        // daljine crta traka širine puta je odbačen: ona prekrije put umesto
        // da ga obeleži. Ušteda se traži tamo gde se ne vidi — broj delova
        // po krivoj prati stvarnu zakrivljenost, a ono što je van kadra se
        // uopšte ne crta.
        private void DrawNetworkOverlays(OverlayRenderSystem.Buffer overlayBuffer)
        {
            int drawn = 0;
            foreach (Entity node in m_SelectedNodes)
            {
                if (drawn >= kMaxOverlayCircles)
                {
                    break;
                }

                if (EntityManager.Exists(node) &&
                    EntityManager.TryGetComponent(node, out Game.Net.Node nodeData) &&
                    OverlayVisible(nodeData.m_Position))
                {
                    overlayBuffer.DrawCircle(kSelectedColor, default, 0.25f, 0, new float2(0f, 1f), nodeData.m_Position, GetNetNodeRadius(node) * 2f);
                    drawn++;
                }
            }

            foreach (Entity edge in m_SelectedNetEdges)
            {
                if (drawn >= kMaxOverlayCircles)
                {
                    break;
                }

                if (!EntityManager.Exists(edge))
                {
                    continue;
                }

                if (!EntityManager.TryGetComponent(edge, out Game.Net.Curve edgeCurve))
                {
                    continue;
                }

                // Provera po CELOJ deonici, ne samo po sredini: duga
                // deonica je nestajala čim joj sredina prođe iza kamere, iako
                // je pola nje još na ekranu.
                if (!OverlayVisible(edgeCurve.m_Bezier.a) &&
                    !OverlayVisible(LaneMidpoint(edgeCurve.m_Bezier)) &&
                    !OverlayVisible(edgeCurve.m_Bezier.d))
                {
                    continue;
                }

                // Put se ocrtava po STVARNIM ivicama kolovoza (leva i desna
                // kriva iz EdgeGeometry, u dve polovine deonice) — ne tankom
                // linijom po sredini kao ograde.
                if (EntityManager.TryGetComponent(edge, out Game.Net.EdgeGeometry geometry))
                {
                    DrawTessellated(overlayBuffer, geometry.m_Start.m_Left, kNetOverlaySegments / 2, 0.4f);
                    DrawTessellated(overlayBuffer, geometry.m_Start.m_Right, kNetOverlaySegments / 2, 0.4f);
                    DrawTessellated(overlayBuffer, geometry.m_End.m_Left, kNetOverlaySegments / 2, 0.4f);
                    DrawTessellated(overlayBuffer, geometry.m_End.m_Right, kNetOverlaySegments / 2, 0.4f);

                    // Tanka srednja linija uz okvir — pomaže da se vidi osa.
                    DrawTessellated(overlayBuffer, edgeCurve.m_Bezier, kNetOverlaySegments, 0.2f);
                }
                else
                {
                    DrawTessellated(overlayBuffer, edgeCurve.m_Bezier, kNetOverlaySegments, 0.4f);
                }

                drawn++;
            }
        }

        // Broj delova prema STVARNOJ zakrivljenosti: prava deonica izgleda
        // isto sa dve tačke kao sa dvanaest, a većina gradskih deonica je
        // prava ili blago kriva. Mereno je 3 ms po frejmu na punom prikazu —
        // najveći deo je odlazio na deljenje pravih linija.
        private static int AdaptiveSegments(Bezier4x3 bezier, int maxSegments)
        {
            float3 chord = bezier.d - bezier.a;
            float length = math.length(chord);
            if (length < 1e-3f)
            {
                // Zatvorena kriva (kružni krak, petlja): tetiva je nula, pa
                // odstupanje od nje ne znači ništa — meri se koliko kontrolne
                // tačke odlaze od samog kraja.
                float loop = math.max(
                    math.length(bezier.b - bezier.a),
                    math.length(bezier.c - bezier.a));
                return loop < 0.25f ? 2 : math.clamp((int)math.ceil(loop), 3, maxSegments);
            }

            float3 direction = chord / length;
            float deviation = math.max(
                math.length(math.cross(bezier.b - bezier.a, direction)),
                math.length(math.cross(bezier.c - bezier.a, direction)));

            // Do 25 cm odstupanja od tetive oko se ne razlikuje od prave.
            return deviation < 0.25f ? 2 : math.clamp((int)math.ceil(deviation), 3, maxSegments);
        }

        private void DrawTessellated(OverlayRenderSystem.Buffer overlayBuffer, Bezier4x3 bezier, int segments, float width)
        {
            segments = AdaptiveSegments(bezier, math.max(2, segments));
            float3 previous = MathUtils.Position(bezier, 0f);
            for (int s = 1; s <= segments; s++)
            {
                float3 current = MathUtils.Position(bezier, s / (float)segments);
                overlayBuffer.DrawLine(kSelectedColor, new Line3.Segment(previous, current), width);
                previous = current;
            }
        }

        // Undo snimci: čvorovi (pozicija+rotacija) + SVE njihove ivice (cela
        // kriva — pokriva i krute i susede). Restore ide istim upisnim putem.
        private void SnapshotNetworks(out List<NetNodeSnapshot> nodeSnapshots, out List<NetEdgeSnapshot> edgeSnapshots)
        {
            nodeSnapshots = new List<NetNodeSnapshot>();
            edgeSnapshots = new List<NetEdgeSnapshot>();
            HashSet<Entity> edgesSeen = new HashSet<Entity>();
            foreach (Entity node in BuildMovingNodeSet())
            {
                if (!EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
                {
                    continue;
                }

                bool hadElevation = EntityManager.TryGetComponent(node, out Game.Net.Elevation nodeElevation);
                nodeSnapshots.Add(new NetNodeSnapshot
                {
                    m_Entity = node,
                    m_Data = nodeData,
                    m_HadElevation = hadElevation,
                    m_Elevation = hadElevation ? nodeElevation.m_Elevation : default,
                });

                if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
                {
                    continue;
                }

                for (int i = 0; i < connected.Length; i++)
                {
                    Entity edge = connected[i].m_Edge;
                    if (edgesSeen.Add(edge) &&
                        EntityManager.Exists(edge) &&
                        EntityManager.TryGetComponent(edge, out Game.Net.Curve curve))
                    {
                        bool edgeHadElevation = EntityManager.TryGetComponent(edge, out Game.Net.Elevation edgeElevation);
                        edgeSnapshots.Add(new NetEdgeSnapshot
                        {
                            m_Entity = edge,
                            m_Curve = curve.m_Bezier,
                            m_HadElevation = edgeHadElevation,
                            m_Elevation = edgeHadElevation ? edgeElevation.m_Elevation : default,
                        });
                    }
                }
            }
        }

        private List<NetNodeSnapshot> SnapshotNetNodeEntities(List<NetNodeSnapshot> reference)
        {
            List<NetNodeSnapshot> snapshots = new List<NetNodeSnapshot>(reference.Count);
            foreach (NetNodeSnapshot snapshot in reference)
            {
                if (EntityManager.TryGetComponent(snapshot.m_Entity, out Game.Net.Node nodeData))
                {
                    bool hadElevation = EntityManager.TryGetComponent(snapshot.m_Entity, out Game.Net.Elevation elevation);
                    snapshots.Add(new NetNodeSnapshot
                    {
                        m_Entity = snapshot.m_Entity,
                        m_Data = nodeData,
                        m_HadElevation = hadElevation,
                        m_Elevation = hadElevation ? elevation.m_Elevation : default,
                    });
                }
            }

            return snapshots;
        }

        private List<NetEdgeSnapshot> SnapshotNetEdgeEntities(List<NetEdgeSnapshot> reference)
        {
            List<NetEdgeSnapshot> snapshots = new List<NetEdgeSnapshot>(reference.Count);
            foreach (NetEdgeSnapshot snapshot in reference)
            {
                if (EntityManager.TryGetComponent(snapshot.m_Entity, out Game.Net.Curve curve))
                {
                    bool hadElevation = EntityManager.TryGetComponent(snapshot.m_Entity, out Game.Net.Elevation elevation);
                    snapshots.Add(new NetEdgeSnapshot
                    {
                        m_Entity = snapshot.m_Entity,
                        m_Curve = curve.m_Bezier,
                        m_HadElevation = hadElevation,
                        m_Elevation = hadElevation ? elevation.m_Elevation : default,
                    });
                }
            }

            return snapshots;
        }

        // PgUp/PgDn za mreže: pokretni skup čvorova ide gore/dole za delta,
        // Net.Elevation prati (dodaje se po potrebi — bez nje bi igra vratila
        // put na teren); ivica sa jednim pomerenim krajem postaje rampa.
        private void AdjustNetworkHeight(float delta)
        {
            HashSet<Entity> moving = BuildMovingNodeSet();
            if (moving.Count == 0)
            {
                return;
            }

            // Popis ivica pre strukturnih izmena (AddComponent invalidira bafere).
            m_NetEdgeScratch.Clear();
            m_NetSeenEdgeScratch.Clear();
            foreach (Entity node in moving)
            {
                if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
                {
                    continue;
                }

                for (int i = 0; i < connected.Length; i++)
                {
                    if (m_NetSeenEdgeScratch.Add(connected[i].m_Edge))
                    {
                        m_NetEdgeScratch.Add(connected[i].m_Edge);
                    }
                }
            }

            float3 lift = new float3(0f, delta, 0f);
            foreach (Entity node in moving)
            {
                if (!EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
                {
                    continue;
                }

                nodeData.m_Position += lift;
                EntityManager.SetComponentData(node, nodeData);

                if (EntityManager.TryGetComponent(node, out Game.Net.NodeGeometry geometry))
                {
                    geometry.m_Bounds.min += lift;
                    geometry.m_Bounds.max += lift;
                    geometry.m_Offset += delta;
                    EntityManager.SetComponentData(node, geometry);
                }

                ShiftLaneElevation(node, delta);
            }

            foreach (Entity edge in m_NetEdgeScratch)
            {
                if (!EntityManager.Exists(edge) ||
                    EntityManager.HasComponent<Deleted>(edge) ||
                    !EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData) ||
                    !EntityManager.TryGetComponent(edge, out Game.Net.Curve curve))
                {
                    continue;
                }

                bool startMoving = moving.Contains(edgeData.m_Start);
                bool endMoving = moving.Contains(edgeData.m_End);

                // ConnectedEdge nosi i "local connection" ivice kojima čvor
                // NIJE kraj — njih ovaj potez ne dira (bez ovoga bi im rasla
                // elevacija dok im kriva stoji, pa bi ih igra digla u vazduh).
                if (!startMoving && !endMoving)
                {
                    continue;
                }

                if (startMoving && endMoving)
                {
                    curve.m_Bezier.a += lift;
                    curve.m_Bezier.b += lift;
                    curve.m_Bezier.c += lift;
                    curve.m_Bezier.d += lift;
                    ShiftLaneElevation(edge, delta);
                }
                else
                {
                    // Rampa: pomereni kraj ide SAMO po visini — xz i oblik krive
                    // se ne diraju (poravnanje traka i luk prežive).
                    if (startMoving)
                    {
                        ShiftCurveEndHeight(ref curve.m_Bezier, delta, movingStart: true);
                    }

                    if (endMoving)
                    {
                        ShiftCurveEndHeight(ref curve.m_Bezier, delta, movingStart: false);
                    }

                    // Elevacija ivice je sredina deonice (levo/desno) — digao
                    // se jedan kraj, sredina ide za pola delte.
                    ShiftLaneElevation(edge, delta * 0.5f);
                }

                curve.m_Length = MathUtils.Length(curve.m_Bezier);
                EntityManager.SetComponentData(edge, curve);
            }

            // Strukturne izmene tek kad su svi baferi pušteni.
            foreach (Entity node in moving)
            {
                if (EntityManager.Exists(node))
                {
                    EntityManager.AddComponent<Updated>(node);
                    EntityManager.AddComponent<BatchesUpdated>(node);
                    m_DelayedNetSettle[node] = 4;
                }
            }

            MarkNetEdgesAndFarNodes(moving);
        }

        // ---------- Copy/paste puteva ----------
        //
        // Isti definicioni pipeline kojim igra gradi puteve (CreationDefinition
        // + NetCourse) — dokazan na ogradama.
        //
        // Deonice se spajaju u raskrsnicu SAMO ako im je deljeni kraj bitski
        // ista tačka (GenerateNodesSystem.NodeKey i GenerateEdgesSystem.
        // NodeMapKey porede float3 bit po bit). Krajevi krivih to ne garantuju,
        // pa se kopira IDENTITET izvornih čvorova: tabela čvorova klipborda
        // daje svim deonicama koje su delile čvor jednu te istu tačku.

        // Koje ivice ulaze u kopiju: eksplicitno selektovane + one čija su OBA
        // kraja u pokretnom skupu (marquee preko mreže ih tako prirodno nosi).
        private void CollectCopyableNetEdges(List<Entity> result)
        {
            // Scratch, ne nova alokacija: UI zove ovo svaki frejm (brojač za
            // Copy dugme), pa bi svež HashSet po frejmu bio čist otpad.
            result.Clear();
            HashSet<Entity> seen = m_NetCopySeenScratch;
            seen.Clear();
            foreach (Entity edge in m_SelectedNetEdges)
            {
                if (seen.Add(edge) && IsSelectableNetEdge(edge))
                {
                    result.Add(edge);
                }
            }

            HashSet<Entity> moving = BuildMovingNodeSet();
            foreach (Entity node in m_SelectedNodes)
            {
                if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
                {
                    continue;
                }

                for (int i = 0; i < connected.Length; i++)
                {
                    Entity edge = connected[i].m_Edge;
                    if (!seen.Contains(edge) &&
                        EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData) &&
                        moving.Contains(edgeData.m_Start) &&
                        moving.Contains(edgeData.m_End) &&
                        IsSelectableNetEdge(edge) &&
                        seen.Add(edge))
                    {
                        result.Add(edge);
                    }
                }
            }
        }

        private readonly List<Entity> m_NetCopyScratch = new List<Entity>();

        // Zaseban od m_NetSeenEdgeScratch: taj drži popis ivica u transform i
        // settle prolazima, a ovaj kratkotrajni popis kopiranja.
        private readonly HashSet<Entity> m_NetCopySeenScratch = new HashSet<Entity>();

        private bool HasCopyableNetworkEdges()
        {
            return CopyableNetEdgeCount() > 0;
        }

        // Broj ivica koje bi kopija ponela. UI ovo čita svaki frejm, a prolaz
        // šeta ConnectedEdge bafere selektovanih čvorova — zato keš po frejmu
        // i sopstveni scratch (m_NetCopyScratch drži rezultat pravog Copy-ja).
        private readonly List<Entity> m_NetCountScratch = new List<Entity>();
        private int m_CopyableNetSignature;
        private int m_CopyableNetFrame = int.MinValue;
        private int m_CopyableNetCount;

        private int CopyableNetEdgeCount()
        {
            // Keš po frejmu nije bio dovoljan: prolaz šeta ConnectedEdge
            // bafere svih selektovanih čvorova i na 100+ deonica je koštao
            // 288 us SVAKOG frejma. Sada se veže za potpis selekcije — računa
            // se tek kad se selekcija stvarno promeni.
            // Potpis ne vidi SMRT entiteta (Deleted ne menja Index ni
            // Version), a ovaj broj je KAPIJA za Copy/Save koja pre hvatanja
            // briše klipbord — ustajala jedinica je puštala Copy da obriše
            // klipbord i ne uhvati ništa. Zato isto osvežavanje na svakih 30
            // frejmova koje ima i ostatak izvedenih podataka.
            int frame = UnityEngine.Time.frameCount;
            int signature = SelectionSignature();
            if (m_CopyableNetSignature == signature && frame - m_CopyableNetFrame < kDerivedRefreshFrames)
            {
                return m_CopyableNetCount;
            }

            m_CopyableNetFrame = frame;
            m_CopyableNetSignature = signature;
            m_CopyableNetCount = 0;
            if (m_SelectedNodes.Count > 0 || m_SelectedNetEdges.Count > 0)
            {
                CollectCopyableNetEdges(m_NetCountScratch);
                m_CopyableNetCount = m_NetCountScratch.Count;
            }

            return m_CopyableNetCount;
        }

        // Kuke za razvojnu dijagnostiku, deklarisane kao PARCIJALNE METODE
        // bez tela u ovom buildu — prevodilac ih uklanja zajedno sa
        // pozivima, pa produkcijski kod ne zavisi od razvojnog alata.
        partial void DiagCaptureSource(List<Entity> edges, Dictionary<Entity, int> nodeIndices);

        partial void DiagCaptureLog(int edgeCount, int nodeCount, int distinctCurveEnds, float maxShift);

        partial void DiagPastedTopologyLog(int edges, int unresolved, int sharedNodes);

        partial void DiagWriteReport(List<PastedRecord> records);

        // Automatski snimak stanja mreze posle poteza (razvojni alat).
        partial void NetProbe(string reason);

        private void CaptureNetworkEdges(float3 centroid)
        {
            m_ClipboardNetGeneration++;
            m_ClipboardNetEdges.Clear();
            m_ClipboardNetNodeOffsets.Clear();
            m_ClipboardNetNodeHeights.Clear();
            m_ClipboardNetNodeHasUpgrade.Clear();
            m_ClipboardNetNodeUpgrades.Clear();
            m_ClipboardNetNodePrefabs.Clear();
            m_ClipboardNetNodeMarkers.Clear();
            CollectCopyableNetEdges(m_NetCopyScratch);
            if (m_NetCopyScratch.Count == 0)
            {
                return;
            }

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();

            // KRUTO TELO po visini: SVE visine su relativne prema terenu na
            // CENTROIDU originala (jedna referenca), ne prema terenu ispod
            // svake tacke. Po-tacki model je na brdovitom terenu izoblicavao
            // mrezu (izmereno: deonica razvucena 112 m, nagibi uniseni).
            float centroidTerrain = TerrainUtils.SampleHeight(ref heightData, centroid);
            Dictionary<Entity, int> nodeIndices = new Dictionary<Entity, int>();
            HashSet<int3> diagCurveEnds = new HashSet<int3>();
            foreach (Entity edge in m_NetCopyScratch)
            {
                if (!EntityManager.TryGetComponent(edge, out Game.Net.Curve curve) ||
                    !EntityManager.TryGetComponent(edge, out PrefabRef prefabRef))
                {
                    continue;
                }

                bool hasUpgrade = EntityManager.TryGetComponent(edge, out Game.Net.Upgraded upgraded);
                float2[] offsets = new float2[4];
                float[] heights = new float[4];
                for (int k = 0; k < 4; k++)
                {
                    float3 point = GetBezierPoint(curve.m_Bezier, k);
                    offsets[k] = point.xz - centroid.xz;
                    heights[k] = point.y - centroidTerrain;
                }

                diagCurveEnds.Add(math.asint(GetBezierPoint(curve.m_Bezier, 0)));
                diagCurveEnds.Add(math.asint(GetBezierPoint(curve.m_Bezier, 3)));

                EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData);
                m_ClipboardNetEdges.Add(new NetEdgeClipboardItem
                {
                    m_Prefab = prefabRef.m_Prefab,
                    m_CurveOffsets = offsets,
                    m_HeightOffsets = heights,
                    m_HasUpgrade = hasUpgrade,
                    m_Upgrade = hasUpgrade ? upgraded.m_Flags : default,
                    m_StartNodeIndex = CaptureNetNode(edgeData.m_Start, centroid, centroidTerrain, nodeIndices),
                    m_EndNodeIndex = CaptureNetNode(edgeData.m_End, centroid, centroidTerrain, nodeIndices),
                });
            }

            float diagMaxShift = 0f;
            foreach (NetEdgeClipboardItem captured in m_ClipboardNetEdges)
            {
                if (captured.m_StartNodeIndex >= 0)
                {
                    diagMaxShift = math.max(diagMaxShift, math.distance(captured.m_CurveOffsets[0], m_ClipboardNetNodeOffsets[captured.m_StartNodeIndex]));
                }

                if (captured.m_EndNodeIndex >= 0)
                {
                    diagMaxShift = math.max(diagMaxShift, math.distance(captured.m_CurveOffsets[3], m_ClipboardNetNodeOffsets[captured.m_EndNodeIndex]));
                }
            }

            DiagCaptureLog(m_ClipboardNetEdges.Count, nodeIndices.Count, diagCurveEnds.Count, diagMaxShift);
            DiagCaptureSource(m_NetCopyScratch, nodeIndices);

        }

        // DIJAGNOSTIKA (razvojni alat): topologija nalepljene mreže.
        private void LogPastedNetTopology(List<PastedRecord> records)
        {
            if (records == null)
            {
                return;
            }

            int edges = 0;
            int unresolved = 0;
            HashSet<Entity> nodes = new HashSet<Entity>();
            foreach (PastedRecord record in records)
            {
                if (!record.m_IsNetEdge)
                {
                    continue;
                }

                edges++;
                if (record.m_Resolved == Entity.Null)
                {
                    unresolved++;
                    continue;
                }

                if (EntityManager.TryGetComponent(record.m_Resolved, out Game.Net.Edge edgeData))
                {
                    nodes.Add(edgeData.m_Start);
                    nodes.Add(edgeData.m_End);
                }
            }

            if (edges > 0)
            {
                DiagPastedTopologyLog(edges, unresolved, nodes.Count);
                DiagWriteReport(records);
            }
        }

        // Vrati reprezentativnu tačku klastera — prvi kraj u krugu od 25 cm
        // (xz) i 1 m visine "usisa" sve ostale na SVOJU poziciju, pa igrin
        // NodeKey (bitsko poređenje) vidi jednu te istu i spoji ih u čvor.
        private float3 WeldCourseEndpoint(float3 point) => WeldCourseEndpoint(point, false, default);

        // forbidden = drugi kraj ISTE deonice: kraća od praga zavarivanja bi
        // se inače srušila u jednu tačku i deonica bi nestala (ostao bi samo
        // usamljen čvor), pa takva zadržava svoje dve tačke.
        private float3 WeldCourseEndpoint(float3 point, bool hasForbidden, float3 forbidden)
        {
            ResolveCourseEndpoint(point, Entity.Null, hasForbidden, forbidden, out float3 position, out _);
            return position;
        }

        private bool IsLiveNetNode(Entity node)
        {
            return node != Entity.Null &&
                EntityManager.Exists(node) &&
                !EntityManager.HasComponent<Deleted>(node) &&
                EntityManager.HasComponent<Game.Net.Node>(node);
        }

        // Upiši živi čvor kao klaster PRE deljenja pozicija — tako svaki kraj
        // koji padne u njegov krug nasledi i entitet. (Mešanje "po entitetu" i
        // "po poziciji" na istoj raskrsnici pravi dvojnika čvora.)
        private void RegisterWeldNode(Entity node)
        {
            if (!IsLiveNetNode(node) || !EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
            {
                return;
            }

            for (int i = 0; i < m_WeldScratch.Count; i++)
            {
                if (m_WeldScratch[i].m_Node == node)
                {
                    return;
                }
            }

            m_WeldScratch.Add(new WeldPoint { m_Position = nodeData.m_Position, m_Node = node });
        }

        // Reprezentativna tačka + čvor za jedan kraj kursa.
        private void ResolveCourseEndpoint(float3 point, Entity recordedNode, bool hasForbidden, float3 forbidden, out float3 position, out Entity node)
        {
            // Zapisani čvor još živi: kačimo se PRAVO na njega (Permanent +
            // CoursePos.m_Entity), pa igra ne pravi dvojnika.
            if (IsLiveNetNode(recordedNode) &&
                EntityManager.TryGetComponent(recordedNode, out Game.Net.Node liveNode))
            {
                RegisterWeldNode(recordedNode);
                position = liveNode.m_Position;
                node = recordedNode;
                return;
            }

            for (int i = 0; i < m_WeldScratch.Count; i++)
            {
                WeldPoint candidate = m_WeldScratch[i];
                if (hasForbidden && candidate.m_Position.Equals(forbidden))
                {
                    continue;
                }

                if (math.distancesq(candidate.m_Position.xz, point.xz) <= kWeldRadiusXZ * kWeldRadiusXZ &&
                    math.abs(candidate.m_Position.y - point.y) <= kWeldToleranceY)
                {
                    position = candidate.m_Position;
                    node = candidate.m_Node;
                    return;
                }
            }

            m_WeldScratch.Add(new WeldPoint { m_Position = point, m_Node = Entity.Null });
            position = point;
            node = Entity.Null;
        }

        // Blueprint: tabela cvorova mora u fajl, inace nalepljena raskrsnica
        // iz blueprinta nema po cemu da spoji deonice.
        private int ClipboardNetNodeCount => m_ClipboardNetNodeOffsets.Count;

        private void GetClipboardNetNode(int index, out float2 offset, out float height)
        {
            offset = m_ClipboardNetNodeOffsets[index];
            height = m_ClipboardNetNodeHeights[index];
        }

        private void GetClipboardNetNodeUpgrade(int index, out bool hasUpgrade, out CompositionFlags upgrade)
        {
            hasUpgrade = index >= 0 && index < m_ClipboardNetNodeHasUpgrade.Count && m_ClipboardNetNodeHasUpgrade[index];
            upgrade = hasUpgrade ? m_ClipboardNetNodeUpgrades[index] : default;
        }

        private void AddClipboardNetNode(float2 offset, float height, bool hasUpgrade, CompositionFlags upgrade)
        {
            m_ClipboardNetNodeOffsets.Add(offset);
            m_ClipboardNetNodeHeights.Add(height);
            m_ClipboardNetNodeHasUpgrade.Add(hasUpgrade);
            m_ClipboardNetNodeUpgrades.Add(upgrade);
        }

        // Nalepljenom čvoru vrati nadogradnje originala (kružni tok, semafori,
        // stop znakovi, veličina raskrsnice). Igra iz Upgraded sama napravi
        // Roundabout/TrafficLights komponente na sledeći update.
        // Nadogradnja se primeni samo ako čvor STOJI na zabeleženoj svetskoj
        // tački jednog od krajeva zapisa. Prag od dva metra: krajevi se lepe
        // bit-identično, pa je sve preko toga drugi čvor.
        private void ApplyPastedNetNodeUpgradeAt(Entity node, PastedRecord record)
        {
            if (!EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
            {
                return;
            }

            float toStart = math.distancesq(nodeData.m_Position, record.m_StartNodeWorld);
            float toEnd = math.distancesq(nodeData.m_Position, record.m_EndNodeWorld);
            if (math.min(toStart, toEnd) > 4f)
            {
                return;
            }

            ApplyPastedNetNodeUpgrade(node, toStart <= toEnd ? record.m_StartNodeIndex : record.m_EndNodeIndex);
        }

        private void ApplyPastedNetNodeUpgrade(Entity node, int index)
        {
            // Prvo markeri: kruzni tok/rucni semafor su POD-OBJEKTI cvora, ne
            // flagovi (dokaz u dijagnostici: RA cvorovi nemaju Upgraded).
            ApplyPastedNetNodeMarkers(node, index);

            GetClipboardNetNodeUpgrade(index, out bool hasUpgrade, out CompositionFlags upgrade);
            if (!hasUpgrade || !EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node))
            {
                return;
            }

            // Ista maska koju igra primenjuje pri kreiranju cvora - na cvor
            // idu samo node-flagovi (kruzni tok, semafori, stop, velicina...).
            upgrade.m_General &= CompositionFlags.nodeMask.m_General;
            upgrade.m_Left &= CompositionFlags.nodeMask.m_Left;
            upgrade.m_Right &= CompositionFlags.nodeMask.m_Right;
            if (upgrade.m_General == default(CompositionFlags.General) &&
                upgrade.m_Left == default(CompositionFlags.Side) &&
                upgrade.m_Right == default(CompositionFlags.Side))
            {
                return;
            }

            if (EntityManager.TryGetComponent(node, out Game.Net.Upgraded existing) &&
                existing.m_Flags.m_General == upgrade.m_General &&
                existing.m_Flags.m_Left == upgrade.m_Left &&
                existing.m_Flags.m_Right == upgrade.m_Right)
            {
                return;
            }

            if (EntityManager.HasComponent<Game.Net.Upgraded>(node))
            {
                EntityManager.SetComponentData(node, new Game.Net.Upgraded { m_Flags = upgrade });
            }
            else
            {
                EntityManager.AddComponentData(node, new Game.Net.Upgraded { m_Flags = upgrade });
            }

            EntityManager.AddComponent<Updated>(node);
            EntityManager.AddComponent<BatchesUpdated>(node);
        }

        private void ResetClipboardNetNodes(List<float2> offsets, List<float> heights, List<bool> hasUpgrades, List<CompositionFlags> upgrades, List<NetNodeMarker> markers = null)
        {
            m_ClipboardNetGeneration++;
            m_ClipboardNetNodeOffsets.Clear();
            m_ClipboardNetNodeHeights.Clear();
            m_ClipboardNetNodeHasUpgrade.Clear();
            m_ClipboardNetNodeUpgrades.Clear();
            m_ClipboardNetNodePrefabs.Clear();
            m_ClipboardNetNodeMarkers.Clear();
            if (markers != null)
            {
                m_ClipboardNetNodeMarkers.AddRange(markers);
            }

            if (offsets == null || heights == null || hasUpgrades == null || upgrades == null ||
                offsets.Count != heights.Count || offsets.Count != hasUpgrades.Count || offsets.Count != upgrades.Count)
            {
                return;
            }

            m_ClipboardNetNodeOffsets.AddRange(offsets);
            m_ClipboardNetNodeHeights.AddRange(heights);
            m_ClipboardNetNodeHasUpgrade.AddRange(hasUpgrades);
            m_ClipboardNetNodeUpgrades.AddRange(upgrades);
        }

        // Svetska tacka cvora iz tabele klipborda - ista za sve deonice koje
        // taj cvor dele, pa igra vidi jedan te isti kraj.
        private bool TryGetClipboardNodePoint(int index, float anchorTerrain, float3 anchor, float baseDelta, out float3 point)
        {
            if (index < 0 || index >= m_ClipboardNetNodeOffsets.Count)
            {
                point = default;
                return false;
            }

            float2 xz = anchor.xz + m_ClipboardNetNodeOffsets[index];
            point = new float3(xz.x, 0f, xz.y);
            point.y = anchorTerrain + m_ClipboardNetNodeHeights[index] + baseDelta + m_PasteHeightBoost;
            return true;
        }

        // Upisi izvorni cvor u tabelu klipborda i vrati mu indeks.
        //
        // Pamti se PRAVA pozicija cvora, ne kraj krive: igra ivicu napravi do
        // centra cvora (GenerateEdgesSystem: curve.a = node.m_Position), pa je
        // posle POTKRESE za velicinu raskrsnice. Merenje na autoputu: kraj
        // krive ume da bude i 7 m od cvora, a dva para deonica na istom cvoru
        // 14 m jedan od drugog. Kopiramo li potkresane krive, igra ih potkrese
        // jos jednom - spojevi se raziđu i put krivuda. Zato se cvor pamti kao
        // cvor, a krive se pri lepljenju produze nazad do njega.
        private int CaptureNetNode(Entity node, float3 centroid, float centroidTerrain, Dictionary<Entity, int> indices)
        {
            if (node == Entity.Null || !EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
            {
                return -1;
            }

            float3 endpoint = nodeData.m_Position;

            if (indices.TryGetValue(node, out int existing))
            {
                return existing;
            }

            int index = m_ClipboardNetNodeOffsets.Count;
            m_ClipboardNetNodeOffsets.Add(endpoint.xz - centroid.xz);
            m_ClipboardNetNodeHeights.Add(endpoint.y - centroidTerrain);

            bool hasNodeUpgrade = EntityManager.TryGetComponent(node, out Game.Net.Upgraded nodeUpgrade);
            m_ClipboardNetNodeHasUpgrade.Add(hasNodeUpgrade);
            m_ClipboardNetNodeUpgrades.Add(hasNodeUpgrade ? nodeUpgrade.m_Flags : default);

            // Markeri: pod-objekti cvora ciji prefab nosi kompozicione flagove
            // (kruzni tok, rucni semafor, stop...). Samo citanje bafera.
            if (EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Objects.SubObject> nodeSubs))
            {
                for (int i = 0; i < nodeSubs.Length; i++)
                {
                    Entity sub = nodeSubs[i].m_SubObject;
                    if (EntityManager.TryGetComponent(sub, out PrefabRef subPrefab) &&
                        EntityManager.TryGetComponent(subPrefab.m_Prefab, out NetObjectData netObject) &&
                        ((netObject.m_CompositionFlags.m_General & CompositionFlags.nodeMask.m_General) != default(CompositionFlags.General) ||
                         (netObject.m_CompositionFlags.m_Left & CompositionFlags.nodeMask.m_Left) != default(CompositionFlags.Side) ||
                         (netObject.m_CompositionFlags.m_Right & CompositionFlags.nodeMask.m_Right) != default(CompositionFlags.Side)))
                    {
                        m_ClipboardNetNodeMarkers.Add(new NetNodeMarker { m_NodeIndex = index, m_Prefab = subPrefab.m_Prefab });
                    }
                }
            }

            indices[node] = index;
            return index;
        }

        // Paste definicije za puteve.
        private void CreateNetworkDefinitions(EntityCommandBuffer buffer, float3 anchor, ref TerrainHeightData heightData, ref Unity.Mathematics.Random random, float baseDelta)
        {
            m_WeldScratch.Clear();

            // Kruta visina: jedna terenska referenca (sidro) za CELU mrezu.
            float anchorTerrain = TerrainUtils.SampleHeight(ref heightData, anchor);

            foreach (NetEdgeClipboardItem item in m_ClipboardNetEdges)
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
                    point.y = anchorTerrain + item.m_HeightOffsets[k] + baseDelta + m_PasteHeightBoost;
                    points[k] = point;
                }

                // KRIVA SE NE DIRA NIKAKO — igri kraj krive kod autoputeva
                // ionako NIJE na cvoru (GenerateEdgesSystem za ne-StrictNodes
                // uzima kraj sa KURSA, samo visinu sa cvora; izmereni gapovi
                // 1-3 m i na prostim cvorovima su NORMALNI). Nase ranije
                // lepljenje kraja na cvor rotiralo je osu na kraju i time
                // CENTRIRALO prelaze sirina. Tacka cvora ide ISKLJUCIVO u
                // CoursePos.m_Position (za spajanje).
                bool hasStartNode = TryGetClipboardNodePoint(item.m_StartNodeIndex, anchorTerrain, anchor, baseDelta, out float3 startNodePoint);
                if (!hasStartNode)
                {
                    startNodePoint = WeldCourseEndpoint(points[0]);
                }

                bool hasEndNode = TryGetClipboardNodePoint(item.m_EndNodeIndex, anchorTerrain, anchor, baseDelta, out float3 endNodePoint);
                if (!hasEndNode)
                {
                    endNodePoint = WeldCourseEndpoint(points[3], true, startNodePoint);
                }

                // Stari blueprint bez tabele: weld tacka mora i u krivu (kao
                // ranije), inace nema poklapanja.
                if (!hasStartNode)
                {
                    points[0] = startNodePoint;
                }

                if (!hasEndNode)
                {
                    points[3] = endNodePoint;
                }

                Bezier4x3 bezier = new Bezier4x3(points[0], points[1], points[2], points[3]);

                // Elevacija (bira most/tunel komade) = visina nad LOKALNIM
                // terenom nove lokacije, ne prenet broj sa stare.
                // NAPOMENA: merenje je namerno na KRAJEVIMA KRIVE, a sredina
                // kao prosek — pokušaj merenja na čvorovima i sredini krive
                // (31.08) promenio je mesta na kojima igra SECHE deonice i
                // pokidao šavove velike petlje. Elevacija ovde nije samo
                // kozmetika: ona određuje podelu kursa, i ne dira se bez
                // kontrolnog testa na velikoj petlji.
                float startElevation = points[0].y - TerrainUtils.SampleHeight(ref heightData, points[0]);
                float endElevation = points[3].y - TerrainUtils.SampleHeight(ref heightData, points[3]);

                Entity definitionEntity = buffer.CreateEntity();

                CreationDefinition creation = default;
                creation.m_Prefab = item.m_Prefab;
                creation.m_RandomSeed = random.NextInt();

                NetCourse course = default;
                course.m_Curve = bezier;
                course.m_Length = MathUtils.Length(bezier);
                course.m_FixedIndex = -1;

                // m_Elevation kursa/ivice je (LEVO, DESNO) na SREDINI deonice
                // (GenerateEdgesSystem je prepisuje u Elevation ivice; .yx swap
                // pri obrtanju smera je levo/desno ogledalo) — NE (start, kraj).
                float midElevation = (startElevation + endElevation) * 0.5f;
                course.m_Elevation = new float2(midElevation, midElevation);
                course.m_StartPosition = LaneCoursePos(bezier, 0f, kPastedCourseStartFlags);
                course.m_StartPosition.m_Position = startNodePoint;
                course.m_StartPosition.m_Elevation = new float2(startElevation, startElevation);
                course.m_EndPosition = LaneCoursePos(bezier, 1f, kPastedCourseEndFlags);
                course.m_EndPosition.m_Position = endNodePoint;
                course.m_EndPosition.m_Elevation = new float2(endElevation, endElevation);

                buffer.AddComponent(definitionEntity, creation);
                buffer.AddComponent(definitionEntity, course);
                if (item.m_HasUpgrade)
                {
                    buffer.AddComponent(definitionEntity, new Game.Net.Upgraded { m_Flags = item.m_Upgrade });
                }

                buffer.AddComponent(definitionEntity, default(Updated));

                // Prefab za node-upgrade definiciju ovog cvora (prva deonica
                // koja ga koristi).
                RememberNodePrefab(item.m_StartNodeIndex, item.m_Prefab);
                RememberNodePrefab(item.m_EndNodeIndex, item.m_Prefab);

                m_LastPreview.Add(new PastedRecord
                {
                    m_Prefab = item.m_Prefab,
                    m_Position = LaneMidpoint(bezier),
                    m_IsNetEdge = true,
                    m_HasUpgrade = item.m_HasUpgrade,
                    m_Upgrade = item.m_Upgrade,
                    m_StartNodeIndex = item.m_StartNodeIndex,
                    m_EndNodeIndex = item.m_EndNodeIndex,
                    m_ClipboardGeneration = m_ClipboardNetGeneration,
                    m_NetCurve = bezier,
                    m_StartNodeWorld = startNodePoint,
                    m_EndNodeWorld = endNodePoint,
                });
            }

            // Nadogradnje CVOROVA (kruzni tok, semafori, stop, velicina i
            // poravnanje raskrsnice) - VANILA PUT: za svaki cvor sa flagovima
            // ide poseban definicioni entitet NULTE duzine na tacki cvora
            // (isto sto NetToolSystem emituje pri upgrade-u cvora). Kurs nulte
            // duzine ne pravi ivicu (GenerateEdgesSystem: start==end -> skip),
            // a GenerateNodesSystem mu flagove OR-uje u cvor kroz nodeMask.
            for (int n = 0; n < ClipboardNetNodeCount; n++)
            {
                GetClipboardNetNodeUpgrade(n, out bool nodeHasUpgrade, out CompositionFlags nodeUpgrade);
                if (!nodeHasUpgrade ||
                    n >= m_ClipboardNetNodePrefabs.Count ||
                    m_ClipboardNetNodePrefabs[n] == Entity.Null ||
                    !TryGetClipboardNodePoint(n, anchorTerrain, anchor, baseDelta, out float3 nodePoint))
                {
                    continue;
                }

                Entity nodeDefinition = buffer.CreateEntity();

                CreationDefinition nodeCreation = default;
                nodeCreation.m_Prefab = m_ClipboardNetNodePrefabs[n];
                nodeCreation.m_RandomSeed = random.NextInt();

                NetCourse nodeCourse = default;
                nodeCourse.m_Curve = new Bezier4x3(nodePoint, nodePoint, nodePoint, nodePoint);
                nodeCourse.m_Length = 0f;
                nodeCourse.m_FixedIndex = -1;

                // Elevacija i ovde: merge kurseva na cvoru bira flagove OR-om,
                // ali kurs BEZ elevacije ume da pobedi i skine Elevation
                // povisenog cvora — most bi dobio prizemnu raskrsnicu.
                float nodeUpgradeElev = nodePoint.y - TerrainUtils.SampleHeight(ref heightData, nodePoint);
                nodeCourse.m_Elevation = new float2(nodeUpgradeElev, nodeUpgradeElev);
                nodeCourse.m_StartPosition = LaneCoursePos(nodeCourse.m_Curve, 0f, CoursePosFlags.IsFirst | CoursePosFlags.DisableMerge);
                nodeCourse.m_StartPosition.m_Position = nodePoint;
                nodeCourse.m_StartPosition.m_Elevation = new float2(nodeUpgradeElev, nodeUpgradeElev);
                nodeCourse.m_EndPosition = LaneCoursePos(nodeCourse.m_Curve, 1f, CoursePosFlags.IsLast | CoursePosFlags.DisableMerge);
                nodeCourse.m_EndPosition.m_Position = nodePoint;
                nodeCourse.m_EndPosition.m_Elevation = new float2(nodeUpgradeElev, nodeUpgradeElev);

                buffer.AddComponent(nodeDefinition, nodeCreation);
                buffer.AddComponent(nodeDefinition, nodeCourse);
                buffer.AddComponent(nodeDefinition, new Game.Net.Upgraded { m_Flags = nodeUpgrade });
                buffer.AddComponent(nodeDefinition, default(Updated));
            }
        }

        // Rezolucija nalepljenih puteva: nova ivica po prefabu + sredini krive
        // (xz), kroz net quad tree nad granicama stampa.
        private void ResolvePastedNetEdges(float3 boundsMin, float3 boundsMax, HashSet<Entity> claimed)
        {
            if (m_NetSearchSystem == null)
            {
                m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            }

            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle dependencies);
            dependencies.Complete();

            RoadIterator iterator = new RoadIterator
            {
                m_Bounds = new Bounds3(
                    new float3(boundsMin.x - 2f, -1000f, boundsMin.z - 2f),
                    new float3(boundsMax.x + 2f, 1000f, boundsMax.z + 2f)),
                m_Results = new NativeList<Entity>(32, Allocator.Temp),
            };
            tree.Iterate(ref iterator, 0);

            m_NetCandidateScratch.Clear();
            for (int i = 0; i < iterator.m_Results.Length; i++)
            {
                Entity candidate = iterator.m_Results[i];
                if (claimed.Contains(candidate) ||
                    (m_PostPasteExclude != null && m_PostPasteExclude.Contains(candidate)) ||
                    !EntityManager.HasComponent<Game.Net.Edge>(candidate) ||
                    EntityManager.HasComponent<Game.Tools.EditorContainer>(candidate) ||
                    EntityManager.HasComponent<Owner>(candidate) ||
                    EntityManager.HasComponent<Temp>(candidate) ||
                    EntityManager.HasComponent<Deleted>(candidate) ||
                    !EntityManager.TryGetComponent(candidate, out Game.Net.Curve curve) ||
                    !EntityManager.TryGetComponent(candidate, out PrefabRef prefabRef))
                {
                    continue;
                }

                m_NetCandidateScratch.Add(new NetCandidate
                {
                    m_Entity = candidate,
                    m_Prefab = prefabRef.m_Prefab,
                    m_Curve = curve.m_Bezier,
                });
            }

            iterator.m_Results.Dispose();

            foreach (NetCandidate candidate in m_NetCandidateScratch)
            {
                float3 midpoint = LaneMidpoint(candidate.m_Curve);
                for (int j = 0; j < m_PostPasteFix.Count; j++)
                {
                    PastedRecord record = m_PostPasteFix[j];
                    if (!record.m_IsNetEdge ||
                        record.m_Resolved != Entity.Null ||
                        candidate.m_Prefab != record.m_Prefab ||
                        math.distancesq(midpoint.xz, record.m_Position.xz) > 1f)
                    {
                        continue;
                    }

                    record.m_Resolved = candidate.m_Entity;
                    m_PostPasteFix[j] = record;
                    claimed.Add(candidate.m_Entity);
                    ApplyPastedNetEdgeFix(candidate.m_Entity, record);
                    break;
                }
            }

            // NAMERNO BEZ rezerve za podeljene kurseve (probano 31.08, povučeno
            // 01.09). Nerazrešen zapis NIJE kvar: undo ga briše pozicionim
            // fallback-om + drugim prolazom, a redo ga gradi IZ SAMOG ZAPISA —
            // cela kriva, pa je igra ponovo podeli. Vezivanje zapisa za JEDNO
            // parče je taj zdravi put pretvaralo u "redo vrati pola rampe".
            // Cena: parčad podeljenog kursa ostaju bez nadogradnji (drvoredi)
            // — kozmetika, ne gubitak deonica.
        }

        // Kandidat iz net quad tree-a, prepisan pre nego što se stablo pusti.
        private struct NetCandidate
        {
            public Entity m_Entity;
            public Entity m_Prefab;
            public Bezier4x3 m_Curve;
        }

        private readonly List<NetCandidate> m_NetCandidateScratch = new List<NetCandidate>();

        // Nadogradnje (drvoredi, ivičnjaci...) na razrešenoj ivici.
        private void ApplyPastedNetEdgeFix(Entity edge, PastedRecord record)
        {
            // Čvorovi prvi: kružni tok/semafori žive na njima, i ne zavise od
            // toga da li deonica ima svoje nadogradnje.
            if (EntityManager.TryGetComponent(edge, out Game.Net.Edge pastedEdge))
            {
                // Po POZICIJI, ne po smeru ivice. Dva razloga: igra ume da
                // stvorenu ivicu okrene (isti razlog zbog kog RunPendingNetRemaps
                // računa "flipped"), a kad PODELI kurs, međučvor nastao podelom
                // nije nijedan kraj zapisa — slepo mapiranje bi mu zakačilo
                // marker kružnog toka nasred deonice, dok bi prava raskrsnica
                // ostala obična.
                ApplyPastedNetNodeUpgradeAt(pastedEdge.m_Start, record);
                ApplyPastedNetNodeUpgradeAt(pastedEdge.m_End, record);
            }

            if (!record.m_HasUpgrade || !EntityManager.Exists(edge))
            {
                return;
            }

            bool hasExisting = EntityManager.TryGetComponent(edge, out Game.Net.Upgraded existing);
            CompositionFlags desired = record.m_Upgrade;
            if (hasExisting &&
                existing.m_Flags.m_General == desired.m_General &&
                existing.m_Flags.m_Left == desired.m_Left &&
                existing.m_Flags.m_Right == desired.m_Right)
            {
                return;
            }

            if (hasExisting)
            {
                EntityManager.SetComponentData(edge, new Game.Net.Upgraded { m_Flags = desired });
            }
            else
            {
                EntityManager.AddComponentData(edge, new Game.Net.Upgraded { m_Flags = desired });
            }

            EntityManager.AddComponent<Updated>(edge);
            EntityManager.AddComponent<BatchesUpdated>(edge);
        }

        // Postojeći identični putevi u granicama stampa — rezolucija ih ne sme
        // usvojiti (dupli stamp preko originala).
        // Geometrija puta koji je postojao PRE stampa. Uz entitet se čuva i
        // kriva, jer igra ume da zatečen put podeli na nalepljenim čvorovima —
        // tada nastanu novi ID-jevi, a ID iz skupa izuzetaka pokazuje na
        // mrtvog. Kriva preživi podelu: parčad i dalje leže na njoj.
        internal struct PreStampNetCurve
        {
            public Entity m_Prefab;
            public Bezier4x3 m_Curve;
        }

        // Da li kandidat CEO leži na nekom zatečenom putu istog prefaba —
        // znak da je parče onoga što je korisnik sagradio pre lepljenja.
        // Poređenje ide i po VISINI: vijadukt istog prefaba nalepljen IZNAD
        // zatečene avenije u xz leži na istoj trasi, pa bi ga xz metrika
        // proglasila tuđim radom i undo bi ga preskočio (isti razlog zbog kog
        // OnRecordCurve na redo putanji ima visinski prag od 4 m).
        private static bool LiesOnPreStampCurve(List<PreStampNetCurve> preCurves, Entity prefab, float3 mid, float3 start, float3 end)
        {
            if (preCurves == null)
            {
                return false;
            }

            foreach (PreStampNetCurve pre in preCurves)
            {
                if (pre.m_Prefab == prefab &&
                    NearCurveWithHeight(pre.m_Curve, mid) &&
                    NearCurveWithHeight(pre.m_Curve, start) &&
                    NearCurveWithHeight(pre.m_Curve, end))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool NearCurveWithHeight(Bezier4x3 curve, float3 point)
        {
            if (MathUtils.Distance(curve.xz, point.xz, out float t) > 2f)
            {
                return false;
            }

            return math.abs(MathUtils.Position(curve, t).y - point.y) <= 4f;
        }

        private void CollectPreStampNetEdges(List<PastedRecord> records, float3 boundsMin, float3 boundsMax, HashSet<Entity> exclude, List<PreStampNetCurve> preCurves)
        {
            if (m_NetSearchSystem == null)
            {
                m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            }

            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle dependencies);
            dependencies.Complete();

            RoadIterator iterator = new RoadIterator
            {
                m_Bounds = new Bounds3(
                    new float3(boundsMin.x - 2f, -1000f, boundsMin.z - 2f),
                    new float3(boundsMax.x + 2f, 1000f, boundsMax.z + 2f)),
                m_Results = new NativeList<Entity>(32, Allocator.Temp),
            };
            tree.Iterate(ref iterator, 0);

            for (int i = 0; i < iterator.m_Results.Length; i++)
            {
                Entity candidate = iterator.m_Results[i];
                if (!EntityManager.HasComponent<Game.Net.Edge>(candidate) ||
                    EntityManager.HasComponent<Game.Tools.EditorContainer>(candidate) ||
                    EntityManager.HasComponent<Owner>(candidate) ||
                    EntityManager.HasComponent<Temp>(candidate) ||
                    EntityManager.HasComponent<Deleted>(candidate) ||
                    !EntityManager.TryGetComponent(candidate, out Game.Net.Curve curve) ||
                    !EntityManager.TryGetComponent(candidate, out PrefabRef prefabRef))
                {
                    continue;
                }

                float3 midpoint = LaneMidpoint(curve.m_Bezier);
                bool samePrefabAsRecord = false;
                foreach (PastedRecord record in records)
                {
                    if (!record.m_IsNetEdge || prefabRef.m_Prefab != record.m_Prefab)
                    {
                        continue;
                    }

                    samePrefabAsRecord = true;
                    if (math.distancesq(midpoint.xz, record.m_Position.xz) <= 1f)
                    {
                        exclude.Add(candidate);
                        break;
                    }
                }

                // Krive se pamte za SVE zatečene puteve prefaba koji lepimo —
                // ne samo za one koji se poklope po sredini. Podela zatečenog
                // puta pravi parčad sa drugim sredinama i novim ID-jevima, pa
                // ih samo geometrija još veže za ono što je bilo pre stampa.
                if (samePrefabAsRecord && preCurves != null)
                {
                    preCurves.Add(new PreStampNetCurve { m_Prefab = prefabRef.m_Prefab, m_Curve = curve.m_Bezier });
                }
            }

            iterator.m_Results.Dispose();
        }

        // Brisanje nalepljenog puta (undo): Deleted na ivicu + osirotele čvorove.
        private void DeleteNetEdgeWithNodes(Entity edge)
        {
            if (!EntityManager.Exists(edge))
            {
                return;
            }

            // Pod-objekti same deonice (stajališta, znakovi) idu sa njom:
            // Deleted na mrežnom entitetu NE kaskadira na SubObject bafer, pa
            // bi stajalište ostalo da visi zakačeno na mrtvu ivicu. Popis pre
            // markiranja ivice — posle je bafer nedostupan.
            DeleteNodeSubObjects(edge);

            bool hasEdge = EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData);
            EntityManager.AddComponent<Deleted>(edge);
            if (!hasEdge)
            {
                return;
            }

            TryDeleteOrphanLaneNode(edgeData.m_Start);
            TryDeleteOrphanLaneNode(edgeData.m_End);

            // Preživela raskrsnica mora da se preračuna: bez ovoga joj ostaje
            // asfaltna lepeza za obrisani krak (T raskrsnica izgleda kao da
            // put i dalje ide u prazno).
            ResettleSurvivingNetNode(edgeData.m_Start);
            ResettleSurvivingNetNode(edgeData.m_End);
        }

        // Čvor koji je preživeo brisanje suseda: pun update njega i njegovih
        // preostalih ivica (+ agregat imena ulice) i odloženi drugi prolaz.
        private void ResettleSurvivingNetNode(Entity node)
        {
            if (!EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node))
            {
                return;
            }

            // Popis pre mutacija — AddComponent invalidira bafer.
            m_NetChildScratch.Clear();
            if (EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
            {
                for (int i = 0; i < connected.Length; i++)
                {
                    m_NetChildScratch.Add(connected[i].m_Edge);
                }
            }

            EntityManager.AddComponent<Updated>(node);
            EntityManager.AddComponent<BatchesUpdated>(node);
            m_DelayedNetSettle[node] = 4;

            foreach (Entity other in m_NetChildScratch)
            {
                if (!EntityManager.Exists(other) || EntityManager.HasComponent<Deleted>(other))
                {
                    continue;
                }

                EntityManager.AddComponent<Updated>(other);
                EntityManager.AddComponent<BatchesUpdated>(other);

                if (EntityManager.TryGetComponent(other, out Game.Net.Aggregated aggregated) &&
                    EntityManager.Exists(aggregated.m_Aggregate))
                {
                    EntityManager.AddComponent<Updated>(aggregated.m_Aggregate);
                }
            }
        }

        // Paste-undo rezerva za nerazrešene puteve.
        private void DeleteUnresolvedNetEdges(List<PastedRecord> records, bool[] recordUsed, float3 boundsMin, float3 boundsMax, HashSet<Entity> deleted, HashSet<Entity> exclude, List<PreStampNetCurve> preCurves)
        {
            if (m_NetSearchSystem == null)
            {
                m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            }

            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle dependencies);
            dependencies.Complete();

            RoadIterator iterator = new RoadIterator
            {
                m_Bounds = new Bounds3(
                    new float3(boundsMin.x - 2f, -1000f, boundsMin.z - 2f),
                    new float3(boundsMax.x + 2f, 1000f, boundsMax.z + 2f)),
                m_Results = new NativeList<Entity>(32, Allocator.Temp),
            };
            tree.Iterate(ref iterator, 0);

            for (int i = 0; i < iterator.m_Results.Length; i++)
            {
                Entity candidate = iterator.m_Results[i];
                if (deleted.Contains(candidate) ||
                    (exclude != null && exclude.Contains(candidate)) ||
                    !EntityManager.HasComponent<Game.Net.Edge>(candidate) ||
                    EntityManager.HasComponent<Game.Tools.EditorContainer>(candidate) ||
                    EntityManager.HasComponent<Owner>(candidate) ||
                    EntityManager.HasComponent<Temp>(candidate) ||
                    EntityManager.HasComponent<Deleted>(candidate) ||
                    !EntityManager.TryGetComponent(candidate, out Game.Net.Curve curve) ||
                    !EntityManager.TryGetComponent(candidate, out PrefabRef prefabRef))
                {
                    continue;
                }

                float3 midpoint = LaneMidpoint(curve.m_Bezier);
                for (int j = 0; j < records.Count; j++)
                {
                    PastedRecord record = records[j];
                    if (recordUsed[j] || !record.m_IsNetEdge || record.m_Resolved != Entity.Null ||
                        prefabRef.m_Prefab != record.m_Prefab ||
                        math.distancesq(midpoint.xz, record.m_Position.xz) > 1f)
                    {
                        continue;
                    }

                    recordUsed[j] = true;
                    deleted.Add(candidate);
                    m_SelectedNetEdges.Remove(candidate);
                    DeleteNetEdgeWithNodes(candidate);
                    break;
                }
            }

            // DRUGI PROLAZ: igra ume da PODELI nalepljenu deonicu (portali
            // tunela, potporni zidovi) — parčići imaju druge sredine, pa ih
            // midpoint match iznad promaši i undo ih je ostavljao na mapi.
            // Parče se prepoznaje po tome što mu sredina LEŽI NA KRIVOJ nekog
            // zapisa (isti prefab) I deli čvor sa već obrisanom deonicom —
            // drugi uslov čuva postojeći identičan put preko koga je korisnik
            // zalepio. Iterativno: lanac parčića se skida od krajeva.
            HashSet<Entity> deletedNodes = new HashSet<Entity>();
            foreach (Entity deletedEdge in deleted)
            {
                if (EntityManager.TryGetComponent(deletedEdge, out Game.Net.Edge deletedData))
                {
                    deletedNodes.Add(deletedData.m_Start);
                    deletedNodes.Add(deletedData.m_End);
                }
            }

            // SEED se dodaje UVEK, ne samo kad prvi prolaz ništa nije obrisao:
            // čvorovi na zabeleženim SVETSKIM tačkama krajeva zapisa su čvorovi
            // stampa — od njih lanac parčadi kreće. Raniji uslov (samo kad je
            // deletedNodes prazan) je propuštao slučaj gde stamp sadrži i
            // rezolvovanu mrežu I odvojen podeljen kurs koji sa njom ne deli
            // nijedan čvor: prvi prolaz obriše rezolvovano, seed se preskoči,
            // i parčad podeljenog kursa prežive undo — a redo onda nalepi ceo
            // kurs preko njih. Dupli unosi ne smetaju (HashSet).
            {
                for (int i = 0; i < iterator.m_Results.Length; i++)
                {
                    Entity candidate = iterator.m_Results[i];
                    if (!EntityManager.TryGetComponent(candidate, out Game.Net.Node nodeData) ||
                        EntityManager.HasComponent<Temp>(candidate) ||
                        EntityManager.HasComponent<Deleted>(candidate) ||
                        EntityManager.HasComponent<Owner>(candidate))
                    {
                        continue;
                    }

                    foreach (PastedRecord record in records)
                    {
                        if (record.m_IsNetEdge &&
                            (math.distancesq(nodeData.m_Position.xz, record.m_StartNodeWorld.xz) <= 1f ||
                             math.distancesq(nodeData.m_Position.xz, record.m_EndNodeWorld.xz) <= 1f))
                        {
                            deletedNodes.Add(candidate);
                            break;
                        }
                    }
                }
            }

            bool removedAny = true;
            while (removedAny)
            {
                removedAny = false;
                for (int i = 0; i < iterator.m_Results.Length; i++)
                {
                    Entity candidate = iterator.m_Results[i];
                    if (deleted.Contains(candidate) ||
                        (exclude != null && exclude.Contains(candidate)) ||
                        !EntityManager.TryGetComponent(candidate, out Game.Net.Edge candidateEdge) ||
                        EntityManager.HasComponent<Game.Tools.EditorContainer>(candidate) ||
                        EntityManager.HasComponent<Owner>(candidate) ||
                        EntityManager.HasComponent<Temp>(candidate) ||
                        EntityManager.HasComponent<Deleted>(candidate) ||
                        (!deletedNodes.Contains(candidateEdge.m_Start) && !deletedNodes.Contains(candidateEdge.m_End)) ||
                        !EntityManager.TryGetComponent(candidate, out Game.Net.Curve candidateCurve) ||
                        !EntityManager.TryGetComponent(candidate, out PrefabRef candidatePrefab))
                    {
                        continue;
                    }

                    float2 candidateMid = LaneMidpoint(candidateCurve.m_Bezier).xz;
                    float2 candidateA = candidateCurve.m_Bezier.a.xz;
                    float2 candidateD = candidateCurve.m_Bezier.d.xz;

                    // Parče ZATEČENOG puta se ne dira. Kad se lepi preko
                    // postojećeg puta istog prefaba, igra ga podeli na novim
                    // čvorovima — parčad su novi ID-jevi kojih nema u skupu
                    // izuzetaka, leže na krivoj zapisa i dele čvor sa
                    // nalepljenom deonicom, pa bi ih undo pojeo iako su
                    // korisnikov rad od ranije.
                    if (LiesOnPreStampCurve(preCurves, candidatePrefab.m_Prefab,
                        LaneMidpoint(candidateCurve.m_Bezier), candidateCurve.m_Bezier.a, candidateCurve.m_Bezier.d))
                    {
                        continue;
                    }

                    foreach (PastedRecord record in records)
                    {
                        if (!record.m_IsNetEdge || candidatePrefab.m_Prefab != record.m_Prefab)
                        {
                            continue;
                        }

                        // CELO parče mora ležati na krivoj zapisa (sredina i
                        // OBA kraja) — pravo split parče leži celo na njoj, a
                        // korisnikov kratki konektor istog prefaba, zakačen na
                        // nalepljeni čvor, ima daleki kraj VAN krive i ne sme
                        // biti obrisan tuđim undo-om.
                        if (DistanceToRecordCurve(record.m_NetCurve, candidateMid) > 2f ||
                            DistanceToRecordCurve(record.m_NetCurve, candidateA) > 2f ||
                            DistanceToRecordCurve(record.m_NetCurve, candidateD) > 2f)
                        {
                            continue;
                        }

                        deleted.Add(candidate);
                        deletedNodes.Add(candidateEdge.m_Start);
                        deletedNodes.Add(candidateEdge.m_End);
                        m_SelectedNetEdges.Remove(candidate);
                        DeleteNetEdgeWithNodes(candidate);
                        removedAny = true;
                        break;
                    }
                }
            }

            iterator.m_Results.Dispose();
        }

        // ---------- Prevezivanje istorije posle rekreacije ----------
        //
        // Rekreacija ide kroz definicije, pa igra napravi ivicu tek koji frejm
        // kasnije i mi joj ne znamo ID. Zato se par frejmova traži po prefabu
        // i sredini krive, pa se stari ID-jevi (u undo/redo zapisima) prevežu
        // na nove. Bez toga stariji korak istorije ćuti, a Ctrl+Y odigra
        // SLEDEĆI zapis — korisnik traži "vrati pomeranje", dobije brisanje.
        private struct PendingNetRemap
        {
            public Entity m_OldEdge;
            public Entity m_OldStartNode;
            public Entity m_OldEndNode;
            public Entity m_Prefab;
            public Bezier4x3 m_Curve;
        }

        private readonly List<PendingNetRemap> m_PendingNetRemaps = new List<PendingNetRemap>();
        private int m_PendingNetRemapFrames;

        private void RunPendingNetRemaps()
        {
            if (m_PendingNetRemapFrames <= 0)
            {
                // Prozor je istekao: nerazrešeni ostaci se BACAJU. Ako bi
                // ostali, sledeća rekreacija bi im ponovo otvorila prozor i
                // stara traženja bi se zakačila na tuđe puteve.
                m_PendingNetRemaps.Clear();
                return;
            }

            m_PendingNetRemapFrames--;
            if (m_PendingNetRemaps.Count == 0)
            {
                m_PendingNetRemapFrames = 0;
                return;
            }

            if (m_NetSearchSystem == null)
            {
                m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            }

            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle dependencies);
            dependencies.Complete();

            HashSet<Entity> claimed = new HashSet<Entity>();
            for (int i = m_PendingNetRemaps.Count - 1; i >= 0; i--)
            {
                PendingNetRemap pending = m_PendingNetRemaps[i];

                // Stari ID i dalje živ (rekreacija je bila za nešto drugo) —
                // nema šta da se prevezuje.
                if (EntityManager.Exists(pending.m_OldEdge) &&
                    !EntityManager.HasComponent<Deleted>(pending.m_OldEdge))
                {
                    m_PendingNetRemaps.RemoveAt(i);
                    continue;
                }

                float3 midpoint = LaneMidpoint(pending.m_Curve);
                RoadIterator iterator = new RoadIterator
                {
                    m_Bounds = new Bounds3(
                        midpoint - new float3(4f, 1000f, 4f),
                        midpoint + new float3(4f, 1000f, 4f)),
                    m_Results = new NativeList<Entity>(8, Allocator.Temp),
                };
                tree.Iterate(ref iterator, 0);

                Entity found = Entity.Null;
                for (int k = 0; k < iterator.m_Results.Length; k++)
                {
                    Entity candidate = iterator.m_Results[k];
                    if (claimed.Contains(candidate) ||
                        !EntityManager.HasComponent<Game.Net.Edge>(candidate) ||
                        EntityManager.HasComponent<Game.Tools.EditorContainer>(candidate) ||
                        EntityManager.HasComponent<Owner>(candidate) ||
                        EntityManager.HasComponent<Temp>(candidate) ||
                        EntityManager.HasComponent<Deleted>(candidate) ||
                        !EntityManager.TryGetComponent(candidate, out Game.Net.Curve curve) ||
                        !EntityManager.TryGetComponent(candidate, out PrefabRef prefabRef) ||
                        prefabRef.m_Prefab != pending.m_Prefab ||
                        math.distancesq(LaneMidpoint(curve.m_Bezier).xz, midpoint.xz) > 1f)
                    {
                        continue;
                    }

                    found = candidate;
                    break;
                }

                iterator.m_Results.Dispose();
                if (found == Entity.Null)
                {
                    continue;
                }

                claimed.Add(found);
                m_PendingNetRemaps.RemoveAt(i);
                RemapHistoryEntity(pending.m_OldEdge, found);

                // Čvorovi: stari kraj se prepozna po blizini novom kraju.
                if (!EntityManager.TryGetComponent(found, out Game.Net.Edge newEdge) ||
                    !EntityManager.TryGetComponent(newEdge.m_Start, out Game.Net.Node newStart) ||
                    !EntityManager.TryGetComponent(newEdge.m_End, out Game.Net.Node newEnd))
                {
                    continue;
                }

                bool flipped = math.distancesq(newStart.m_Position.xz, pending.m_Curve.a.xz) >
                    math.distancesq(newEnd.m_Position.xz, pending.m_Curve.a.xz);
                RemapDeadNetNode(pending.m_OldStartNode, flipped ? newEdge.m_End : newEdge.m_Start);
                RemapDeadNetNode(pending.m_OldEndNode, flipped ? newEdge.m_Start : newEdge.m_End);
            }

            if (m_PendingNetRemaps.Count == 0)
            {
                m_PendingNetRemapFrames = 0;
            }
        }

        private void RemapDeadNetNode(Entity oldNode, Entity newNode)
        {
            if (oldNode == Entity.Null || newNode == Entity.Null || oldNode == newNode)
            {
                return;
            }

            // Živ stari čvor znači da je deonica vraćena NA NJEGA — istorija
            // je već tačna.
            if (EntityManager.Exists(oldNode) && !EntityManager.HasComponent<Deleted>(oldNode))
            {
                return;
            }

            RemapHistoryEntity(oldNode, newNode);
        }

        // Krajnji čvorovi ivice + njihova elevacija (za rekreaciju).
        private void CaptureNetEdgeEnds(Entity edge, out Entity startNode, out Entity endNode,
            out bool hadStartElevation, out float2 startElevation,
            out bool hadEndElevation, out float2 endElevation,
            out bool hasNodePositions, out float3 startNodePos, out float3 endNodePos,
            out bool startHasUpgrade, out CompositionFlags startUpgrade,
            out bool endHasUpgrade, out CompositionFlags endUpgrade,
            out List<Entity> startMarkers, out List<Entity> endMarkers)
        {
            startNode = Entity.Null;
            endNode = Entity.Null;
            hadStartElevation = false;
            startElevation = default;
            hadEndElevation = false;
            endElevation = default;
            hasNodePositions = false;
            startNodePos = default;
            endNodePos = default;
            startHasUpgrade = false;
            startUpgrade = default;
            endHasUpgrade = false;
            endUpgrade = default;
            startMarkers = null;
            endMarkers = null;

            if (!EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData))
            {
                return;
            }

            startNode = edgeData.m_Start;
            endNode = edgeData.m_End;
            if (EntityManager.TryGetComponent(startNode, out Game.Net.Node startNodeData) &&
                EntityManager.TryGetComponent(endNode, out Game.Net.Node endNodeData))
            {
                hasNodePositions = true;
                startNodePos = startNodeData.m_Position;
                endNodePos = endNodeData.m_Position;
            }

            startHasUpgrade = EntityManager.TryGetComponent(startNode, out Game.Net.Upgraded su);
            startUpgrade = startHasUpgrade ? su.m_Flags : default;
            endHasUpgrade = EntityManager.TryGetComponent(endNode, out Game.Net.Upgraded eu);
            endUpgrade = endHasUpgrade ? eu.m_Flags : default;
            startMarkers = CollectNodeMarkerPrefabs(startNode);
            endMarkers = CollectNodeMarkerPrefabs(endNode);
            hadStartElevation = EntityManager.TryGetComponent(startNode, out Game.Net.Elevation start);
            startElevation = hadStartElevation ? start.m_Elevation : default;
            hadEndElevation = EntityManager.TryGetComponent(endNode, out Game.Net.Elevation end);
            endElevation = hadEndElevation ? end.m_Elevation : default;
        }

        // Rekreacija ivica iz snimaka (redo paste-a i undo brisanja): ponovo
        // kroz definicije, ovaj put Permanent (bez Temp faze). Bez remapa —
        // naredni undo/redo ih nalazi pozicionim fallback-om.
        private void RecreateNetEdges(List<NetEdgeSnapshot> snapshots)
        {
            if (snapshots == null || snapshots.Count == 0)
            {
                Mod.Log.Info($"Copaste: recreate roads skipped (snapshots {(snapshots == null ? "null" : "empty")})");
                return;
            }

            EntityCommandBuffer buffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Unity.Mathematics.Random random = RandomSeed.Next().GetRandom(0);
            m_WeldScratch.Clear();

            // Bez ovoga naš Clear pobije definicije pre obrade (redo je zato
            // "ćutao": ivice se nikad nisu ni stvorile).
            KeepDefinitionsAlive();

            int emitted = 0;

            // Prvo se upišu SVI preživeli čvorovi: kasnije zavarivanje po
            // poziciji tako nasledi njihov entitet i raskrsnica ostaje jedna.
            foreach (NetEdgeSnapshot snapshot in snapshots)
            {
                RegisterWeldNode(snapshot.m_StartNode);
                RegisterWeldNode(snapshot.m_EndNode);
            }

            foreach (NetEdgeSnapshot snapshot in snapshots)
            {
                if (snapshot.m_Prefab == Entity.Null || !EntityManager.Exists(snapshot.m_Prefab))
                {
                    continue;
                }

                Entity definitionEntity = buffer.CreateEntity();

                CreationDefinition creation = default;
                creation.m_Prefab = snapshot.m_Prefab;
                creation.m_RandomSeed = random.NextInt();
                creation.m_Flags |= CreationFlags.Permanent;

                NetCourse course = default;
                course.m_Curve = snapshot.m_Curve;

                // KRIVA SE NE DIRA (kraj krive kod autoputeva legitimno nije
                // na cvoru). Spajanje ide kroz CoursePos.m_Position: preziveo
                // cvor direktno, mrtav preko snimljene POZICIJE cvora (bitski
                // ista za sve deonice tog cvora), fallback stari weld.
                float3 startAnchor = snapshot.m_HasNodePositions ? snapshot.m_StartNodePos : course.m_Curve.a;
                float3 endAnchor = snapshot.m_HasNodePositions ? snapshot.m_EndNodePos : course.m_Curve.d;
                ResolveCourseEndpoint(startAnchor, snapshot.m_StartNode, false, default, out float3 startPoint, out Entity startNode);
                ResolveCourseEndpoint(endAnchor, snapshot.m_EndNode, true, startPoint, out float3 endPoint, out Entity endNode);
                course.m_Length = MathUtils.Length(course.m_Curve);
                course.m_FixedIndex = -1;
                if (snapshot.m_HadElevation)
                {
                    // Elevacija ivice = (levo, desno) na SREDINI deonice.
                    course.m_Elevation = snapshot.m_Elevation;
                }

                // DisableMerge i ovde: rekreirana raskrsnica bi se inače
                // podelila i vratila kao gomila slepih krajeva. Vezivanje za
                // PREŽIVELE čvorove ide preko m_Entity ispod (Permanent putanja
                // u TryGetNode), što DisableMerge ne dira.
                course.m_StartPosition = LaneCoursePos(course.m_Curve, 0f, kPastedCourseStartFlags);
                course.m_StartPosition.m_Position = startPoint;
                course.m_EndPosition = LaneCoursePos(course.m_Curve, 1f, kPastedCourseEndFlags);
                course.m_EndPosition.m_Position = endPoint;

                // Čvorovi nose SVOJU elevaciju (rampa: jedan kraj na tlu, drugi
                // gore) — deljenje elevacije ivice po krajevima je pravilo
                // pogrešan most na jednom i spušten kraj na drugom kraju.
                course.m_StartPosition.m_Entity = startNode;
                if (snapshot.m_HadStartElevation)
                {
                    course.m_StartPosition.m_Elevation = snapshot.m_StartElevation;
                }

                course.m_EndPosition.m_Entity = endNode;
                if (snapshot.m_HadEndElevation)
                {
                    course.m_EndPosition.m_Elevation = snapshot.m_EndElevation;
                }

                emitted++;
                buffer.AddComponent(definitionEntity, creation);
                buffer.AddComponent(definitionEntity, course);

                // Nadogradnje (drvoredi, ivičnjaci...) idu u definiciju — igra
                // ih prepiše na stvorenu ivicu (undo brisanja ih tako vraća).
                if (snapshot.m_HasUpgrade)
                {
                    buffer.AddComponent(definitionEntity, new Game.Net.Upgraded { m_Flags = snapshot.m_Upgrade });
                }

                buffer.AddComponent(definitionEntity, default(Updated));

                // Zakaži prevezivanje istorije na novi (još nepostojeći) ID.
                if (snapshot.m_Entity != Entity.Null)
                {
                    m_PendingNetRemaps.Add(new PendingNetRemap
                    {
                        m_OldEdge = snapshot.m_Entity,
                        m_OldStartNode = snapshot.m_StartNode,
                        m_OldEndNode = snapshot.m_EndNode,
                        m_Prefab = snapshot.m_Prefab,
                        m_Curve = course.m_Curve,
                    });
                }
            }

            // Node stanje: jedan zero-length upgrade kurs po JEDINSTVENOJ
            // tacki (kao pri paste-u) + zakazani marker attach-evi (cvor
            // fizicki nastaje tek za frejm-dva, pa se marker kaci kroz pending
            // prozor po poziciji).
            HashSet<int3> recreatedNodeSeen = new HashSet<int3>();
            foreach (NetEdgeSnapshot snapshot in snapshots)
            {
                if (!snapshot.m_HasNodePositions)
                {
                    continue;
                }

                EmitRecreatedNodeState(buffer, ref random, recreatedNodeSeen, snapshot.m_StartNodePos,
                    snapshot.m_StartNodeHasUpgrade, snapshot.m_StartNodeUpgrade, snapshot.m_StartMarkers, snapshot.m_Prefab);
                EmitRecreatedNodeState(buffer, ref random, recreatedNodeSeen, snapshot.m_EndNodePos,
                    snapshot.m_EndNodeHasUpgrade, snapshot.m_EndNodeUpgrade, snapshot.m_EndMarkers, snapshot.m_Prefab);
            }

            if (m_PendingNetRemaps.Count > 0)
            {
                m_PendingNetRemapFrames = 12;
            }

            if (m_PendingMarkerAttaches.Count > 0)
            {
                m_PendingMarkerFrames = 30;
            }
            Mod.Log.Info($"Copaste: recreate roads {emitted}/{snapshots.Count} definitions emitted");
        }

        private List<NetEdgeSnapshot> SnapshotResolvedPastedNetEdges(List<PastedRecord> records)
        {
            List<NetEdgeSnapshot> snapshots = new List<NetEdgeSnapshot>();
            foreach (PastedRecord record in records)
            {
                if (!record.m_IsNetEdge)
                {
                    continue;
                }

                // Nerezolvovan zapis (deonicu je igra PODELILA pa je midpoint
                // match nije našao): redo rekreira iz SAMOG zapisa — kriva i
                // tačke čvorova sa stampa su dovoljne, igra će opet podeliti.
                if (record.m_Resolved == Entity.Null ||
                    !EntityManager.Exists(record.m_Resolved) ||
                    EntityManager.HasComponent<Deleted>(record.m_Resolved))
                {
                    if (!record.m_NetCurve.a.Equals(record.m_NetCurve.d))
                    {
                        // Elevacije se izvode iz krive prema terenu (kao pri
                        // paste-u) — bez njih bi redo mosta/tunela vratio
                        // deonicu zalepljenu za teren.
                        TerrainHeightData recordTerrain = m_TerrainSystem.GetHeightData();
                        float recordStartElev = record.m_NetCurve.a.y - TerrainUtils.SampleHeight(ref recordTerrain, record.m_NetCurve.a);
                        float recordEndElev = record.m_NetCurve.d.y - TerrainUtils.SampleHeight(ref recordTerrain, record.m_NetCurve.d);
                        float recordMidElev = (recordStartElev + recordEndElev) * 0.5f;

                        // Klipbord tabele su od stampa REBUILDOVANE (novi
                        // Copy/blueprint)? Stari indeksi bi čitali NOVU tabelu
                        // — redo bi zalepio tuđe markere/nadogradnje.
                        bool sameClipboard = record.m_ClipboardGeneration == m_ClipboardNetGeneration;
                        CompositionFlags recordStartUpgrade = default;
                        CompositionFlags recordEndUpgrade = default;
                        bool recordStartHasUpgrade = sameClipboard && TryGetRecordNodeUpgrade(record.m_StartNodeIndex, out recordStartUpgrade);
                        bool recordEndHasUpgrade = sameClipboard && TryGetRecordNodeUpgrade(record.m_EndNodeIndex, out recordEndUpgrade);
                        snapshots.Add(new NetEdgeSnapshot
                        {
                            m_Entity = Entity.Null,
                            m_Curve = record.m_NetCurve,
                            m_Prefab = record.m_Prefab,
                            m_HasUpgrade = record.m_HasUpgrade,
                            m_Upgrade = record.m_Upgrade,
                            m_HasNodePositions = true,
                            m_StartNodePos = record.m_StartNodeWorld,
                            m_EndNodePos = record.m_EndNodeWorld,
                            m_HadElevation = math.abs(recordMidElev) > 0.01f,
                            m_Elevation = new float2(recordMidElev, recordMidElev),
                            m_HadStartElevation = math.abs(recordStartElev) > 0.01f,
                            m_StartElevation = new float2(recordStartElev, recordStartElev),
                            m_HadEndElevation = math.abs(recordEndElev) > 0.01f,
                            m_EndElevation = new float2(recordEndElev, recordEndElev),
                            m_StartNodeHasUpgrade = recordStartHasUpgrade,
                            m_StartNodeUpgrade = recordStartUpgrade,
                            m_EndNodeHasUpgrade = recordEndHasUpgrade,
                            m_EndNodeUpgrade = recordEndUpgrade,
                            m_StartMarkers = sameClipboard ? CollectClipboardMarkers(record.m_StartNodeIndex) : null,
                            m_EndMarkers = sameClipboard ? CollectClipboardMarkers(record.m_EndNodeIndex) : null,
                        });
                    }

                    continue;
                }

                if (!EntityManager.TryGetComponent(record.m_Resolved, out Game.Net.Curve curve) ||
                    !EntityManager.TryGetComponent(record.m_Resolved, out PrefabRef prefabRef))
                {
                    continue;
                }

                bool hadElevation = EntityManager.TryGetComponent(record.m_Resolved, out Game.Net.Elevation elevation);
                CaptureNetEdgeEnds(record.m_Resolved, out Entity startNode, out Entity endNode,
                    out bool hadStart, out float2 startElevation, out bool hadEnd, out float2 endElevation,
                    out bool hasNodePositions, out float3 startNodePos, out float3 endNodePos,
                    out bool startNodeHasUpgrade, out CompositionFlags startNodeUpgrade,
                    out bool endNodeHasUpgrade, out CompositionFlags endNodeUpgrade,
                    out List<Entity> startMarkers, out List<Entity> endMarkers);
                snapshots.Add(new NetEdgeSnapshot
                {
                    m_Entity = record.m_Resolved,
                    m_Curve = curve.m_Bezier,
                    m_Prefab = prefabRef.m_Prefab,
                    m_HasUpgrade = record.m_HasUpgrade,
                    m_Upgrade = record.m_Upgrade,
                    m_HadElevation = hadElevation,
                    m_Elevation = hadElevation ? elevation.m_Elevation : default,
                    m_StartNode = startNode,
                    m_EndNode = endNode,
                    m_HasNodePositions = hasNodePositions,
                    m_StartNodePos = startNodePos,
                    m_EndNodePos = endNodePos,
                    m_StartNodeHasUpgrade = startNodeHasUpgrade,
                    m_StartNodeUpgrade = startNodeUpgrade,
                    m_EndNodeHasUpgrade = endNodeHasUpgrade,
                    m_EndNodeUpgrade = endNodeUpgrade,
                    m_StartMarkers = startMarkers,
                    m_EndMarkers = endMarkers,
                    m_HadStartElevation = hadStart,
                    m_StartElevation = startElevation,
                    m_HadEndElevation = hadEnd,
                    m_EndElevation = endElevation,
                });
            }

            return snapshots;
        }

        // Rotacija klipborda: xz ofseti tačaka kao kod ograda/površina.
        private void RotateClipboardNetEdges(float sin, float cos)
        {
            for (int i = 0; i < m_ClipboardNetNodeOffsets.Count; i++)
            {
                float2 n = m_ClipboardNetNodeOffsets[i];
                m_ClipboardNetNodeOffsets[i] = new float2((n.x * cos) + (n.y * sin), (-n.x * sin) + (n.y * cos));
            }

            foreach (NetEdgeClipboardItem item in m_ClipboardNetEdges)
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

        // Match H za mreže: svaki čvor pokretnog skupa na TAČNO ciljnu visinu
        // (elevacija prati pomak po čvoru), deonice postaju rampe/prate.
        private void MatchNetworkHeight(float targetY)
        {
            HashSet<Entity> moving = BuildMovingNodeSet();
            if (moving.Count == 0)
            {
                return;
            }

            m_NetShiftScratch.Clear();
            m_NetEdgeScratch.Clear();
            m_NetSeenEdgeScratch.Clear();
            foreach (Entity node in moving)
            {
                if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
                {
                    continue;
                }

                for (int i = 0; i < connected.Length; i++)
                {
                    if (m_NetSeenEdgeScratch.Add(connected[i].m_Edge))
                    {
                        m_NetEdgeScratch.Add(connected[i].m_Edge);
                    }
                }
            }

            foreach (Entity node in moving)
            {
                if (!EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
                {
                    continue;
                }

                float shift = targetY - nodeData.m_Position.y;
                if (math.abs(shift) < 0.001f)
                {
                    continue;
                }

                nodeData.m_Position.y = targetY;
                EntityManager.SetComponentData(node, nodeData);

                if (EntityManager.TryGetComponent(node, out Game.Net.NodeGeometry geometry))
                {
                    geometry.m_Bounds.min.y += shift;
                    geometry.m_Bounds.max.y += shift;
                    geometry.m_Offset += shift;
                    EntityManager.SetComponentData(node, geometry);
                }

                ShiftLaneElevation(node, shift);
                m_NetShiftScratch[node] = shift;
            }

            foreach (Entity edge in m_NetEdgeScratch)
            {
                if (!EntityManager.Exists(edge) ||
                    EntityManager.HasComponent<Deleted>(edge) ||
                    !EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData) ||
                    !EntityManager.TryGetComponent(edge, out Game.Net.Curve curve))
                {
                    continue;
                }

                // Local-connection ivica (čvor joj nije kraj) se ne dira.
                if (!moving.Contains(edgeData.m_Start) && !moving.Contains(edgeData.m_End))
                {
                    continue;
                }

                bool matchStartMoving = moving.Contains(edgeData.m_Start);
                bool matchEndMoving = moving.Contains(edgeData.m_End);
                if (matchStartMoving && matchEndMoving &&
                    EntityManager.TryGetComponent(edgeData.m_Start, out Game.Net.Node matchedStart) &&
                    EntityManager.TryGetComponent(edgeData.m_End, out Game.Net.Node matchedEnd))
                {
                    // Oba kraja idu SAMO po visini — tetivni preračun bi ovde
                    // izravnao blago savijene deonice (kod skoro istog x ili z
                    // proporcije pobegnu u klamp), pa se dira isključivo y.
                    ShiftCurveHeights(ref curve.m_Bezier, matchedStart.m_Position.y, matchedEnd.m_Position.y,
                        edgeData.m_Start, edgeData.m_End);
                }
                else
                {
                    // Samo visina — xz i oblik krive netaknuti.
                    if (matchStartMoving && m_NetShiftScratch.TryGetValue(edgeData.m_Start, out float rampStartShift))
                    {
                        ShiftCurveEndHeight(ref curve.m_Bezier, rampStartShift, movingStart: true);
                    }

                    if (matchEndMoving && m_NetShiftScratch.TryGetValue(edgeData.m_End, out float rampEndShift))
                    {
                        ShiftCurveEndHeight(ref curve.m_Bezier, rampEndShift, movingStart: false);
                    }
                }

                curve.m_Length = MathUtils.Length(curve.m_Bezier);
                EntityManager.SetComponentData(edge, curve);

                // Bez ovoga deonica zadrži staru elevaciju sredine i igra je
                // i dalje crta kao most iako su joj krajevi na tlu.
                ShiftEdgeElevationFromNodeShifts(edge, edgeData);
            }

            foreach (Entity node in moving)
            {
                if (EntityManager.Exists(node))
                {
                    EntityManager.AddComponent<Updated>(node);
                    EntityManager.AddComponent<BatchesUpdated>(node);
                    m_DelayedNetSettle[node] = 4;
                }
            }

            MarkNetEdgesAndFarNodes(moving);
        }

        // End za mreže: "elevacija 0" — čvor se spusti TAČNO za koliko je
        // podignut i tu stane. (Merenje prema terenu ispod ne valja: igra
        // niveliše teren uz put, pa bi svako pritiskanje propadalo za klirens.)
        private void SnapNetworksToGround()
        {
            HashSet<Entity> moving = BuildMovingNodeSet();
            if (moving.Count == 0)
            {
                return;
            }

            m_NetShiftScratch.Clear();
            m_NetEdgeScratch.Clear();
            m_NetSeenEdgeScratch.Clear();
            foreach (Entity node in moving)
            {
                if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
                {
                    continue;
                }

                for (int i = 0; i < connected.Length; i++)
                {
                    if (m_NetSeenEdgeScratch.Add(connected[i].m_Edge))
                    {
                        m_NetEdgeScratch.Add(connected[i].m_Edge);
                    }
                }
            }

            bool anyShift = false;
            foreach (Entity node in moving)
            {
                // Čvor bez Elevation je već na tlu — ne dira se (idempotentno).
                if (!EntityManager.TryGetComponent(node, out Game.Net.Elevation elevation) ||
                    !EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
                {
                    continue;
                }

                float shift = -math.lerp(elevation.m_Elevation.x, elevation.m_Elevation.y, 0.5f);
                if (math.abs(shift) < 0.001f)
                {
                    // I skidanje mikro-elevacije je promena — mora Updated.
                    EntityManager.RemoveComponent<Game.Net.Elevation>(node);
                    anyShift = true;
                    continue;
                }

                anyShift = true;
                nodeData.m_Position.y += shift;
                EntityManager.SetComponentData(node, nodeData);
                m_NetShiftScratch[node] = shift;

                if (EntityManager.TryGetComponent(node, out Game.Net.NodeGeometry geometry))
                {
                    geometry.m_Bounds.min.y += shift;
                    geometry.m_Bounds.max.y += shift;
                    geometry.m_Offset += shift;
                    EntityManager.SetComponentData(node, geometry);
                }

                EntityManager.RemoveComponent<Game.Net.Elevation>(node);
            }

            if (!anyShift)
            {
                return;
            }

            foreach (Entity edge in m_NetEdgeScratch)
            {
                if (!EntityManager.Exists(edge) ||
                    EntityManager.HasComponent<Deleted>(edge) ||
                    !EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData) ||
                    !EntityManager.TryGetComponent(edge, out Game.Net.Curve curve))
                {
                    continue;
                }

                bool startMoving = moving.Contains(edgeData.m_Start);
                bool endMoving = moving.Contains(edgeData.m_End);

                // Local-connection ivica (čvor joj nije kraj) se ne dira.
                if (!startMoving && !endMoving)
                {
                    continue;
                }

                if (startMoving && endMoving)
                {
                    // Kao kod Match H: oba kraja se spuštaju samo po visini, pa
                    // se x/z ne preračunava (čuva se oblik krive).
                    if (EntityManager.TryGetComponent(edgeData.m_Start, out Game.Net.Node groundedStart) &&
                        EntityManager.TryGetComponent(edgeData.m_End, out Game.Net.Node groundedEnd))
                    {
                        ShiftCurveHeights(ref curve.m_Bezier, groundedStart.m_Position.y, groundedEnd.m_Position.y,
                            edgeData.m_Start, edgeData.m_End);
                    }

                    // Elevacija prati POMAK krajeva, isto kao i sama kriva —
                    // ne briše se bezuslovno. ShiftCurveHeights namerno čuva
                    // ručno namešten visinski ofset (ručka na sredini), pa je
                    // brisanje elevacije deonici čiji su oba čvora ionako bila
                    // na tlu ostavljalo krivu u vazduhu sa prizemnom
                    // kompozicijom. Kad krajevi zaista siđu, pomak je jednak
                    // njihovoj visini i komponenta sama otpada.
                    ShiftEdgeElevationFromNodeShifts(edge, edgeData);
                }
                else
                {
                    // Samo visina — xz i oblik krive netaknuti.
                    if (startMoving && m_NetShiftScratch.TryGetValue(edgeData.m_Start, out float groundStartShift))
                    {
                        ShiftCurveEndHeight(ref curve.m_Bezier, groundStartShift, movingStart: true);
                    }

                    if (endMoving && m_NetShiftScratch.TryGetValue(edgeData.m_End, out float groundEndShift))
                    {
                        ShiftCurveEndHeight(ref curve.m_Bezier, groundEndShift, movingStart: false);
                    }

                    // Rampa: sredina ide za prosek pomaka krajeva, inače bi
                    // deonica ostala "most" iako joj je jedan kraj sišao.
                    ShiftEdgeElevationFromNodeShifts(edge, edgeData);
                }

                curve.m_Length = MathUtils.Length(curve.m_Bezier);
                EntityManager.SetComponentData(edge, curve);
            }

            foreach (Entity node in moving)
            {
                if (EntityManager.Exists(node))
                {
                    EntityManager.AddComponent<Updated>(node);
                    EntityManager.AddComponent<BatchesUpdated>(node);
                    m_DelayedNetSettle[node] = 4;
                }
            }

            MarkNetEdgesAndFarNodes(moving);
        }

        // Igra ume da podeli/spoji ivice ako se gradilo preko — mrtvi entiteti
        // se tiho preskaču (isti princip kao kod zgrada).
        private void ApplyNetworkSnapshots(List<NetNodeSnapshot> nodeSnapshots, List<NetEdgeSnapshot> edgeSnapshots)
        {
            if (nodeSnapshots != null)
            {
                foreach (NetNodeSnapshot snapshot in nodeSnapshots)
                {
                    if (!EntityManager.Exists(snapshot.m_Entity) ||
                        EntityManager.HasComponent<Deleted>(snapshot.m_Entity) ||
                        !EntityManager.HasComponent<Game.Net.Node>(snapshot.m_Entity))
                    {
                        continue;
                    }

                    EntityManager.SetComponentData(snapshot.m_Entity, snapshot.m_Data);
                    RestoreLaneElevation(snapshot.m_Entity, snapshot.m_HadElevation, snapshot.m_Elevation);
                    EntityManager.AddComponent<Updated>(snapshot.m_Entity);
                    EntityManager.AddComponent<BatchesUpdated>(snapshot.m_Entity);
                    m_DelayedNetSettle[snapshot.m_Entity] = 4;
                }
            }

            if (edgeSnapshots != null)
            {
                foreach (NetEdgeSnapshot snapshot in edgeSnapshots)
                {
                    if (!EntityManager.Exists(snapshot.m_Entity) ||
                        EntityManager.HasComponent<Deleted>(snapshot.m_Entity) ||
                        !EntityManager.TryGetComponent(snapshot.m_Entity, out Game.Net.Curve curve))
                    {
                        continue;
                    }

                    curve.m_Bezier = snapshot.m_Curve;
                    curve.m_Length = MathUtils.Length(curve.m_Bezier);
                    EntityManager.SetComponentData(snapshot.m_Entity, curve);
                    RestoreLaneElevation(snapshot.m_Entity, snapshot.m_HadElevation, snapshot.m_Elevation);
                    EntityManager.AddComponent<Updated>(snapshot.m_Entity);
                    EntityManager.AddComponent<BatchesUpdated>(snapshot.m_Entity);

                    if (EntityManager.TryGetComponent(snapshot.m_Entity, out Game.Net.Edge edgeData))
                    {
                        if (EntityManager.Exists(edgeData.m_Start))
                        {
                            EntityManager.AddComponent<Updated>(edgeData.m_Start);
                        }

                        if (EntityManager.Exists(edgeData.m_End))
                        {
                            EntityManager.AddComponent<Updated>(edgeData.m_End);
                        }
                    }
                }
            }
        }

        // ---------- Brisanje mreža ----------
        //
        // Vanila princip: Deleted na ivicu (isto što radi buldožer), čvor ode
        // kad ostane bez ivica. Selektovan ČVOR briše i sve svoje ivice (
        // "obriši raskrsnicu" znači obriši i krake).

        // Ivice koje Delete stvarno briše: selektovane + svi kraci
        // selektovanih čvorova.
        private void CollectDeletableNetEdges(List<Entity> result)
        {
            result.Clear();
            HashSet<Entity> seen = new HashSet<Entity>();
            foreach (Entity edge in m_SelectedNetEdges)
            {
                if (seen.Add(edge) && IsSelectableNetEdge(edge))
                {
                    result.Add(edge);
                }
            }

            foreach (Entity node in m_SelectedNodes)
            {
                if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
                {
                    continue;
                }

                // Popis pre mutacija (pravilo fajla — baferi se ne drže).
                m_NetChildScratch.Clear();
                for (int i = 0; i < connected.Length; i++)
                {
                    m_NetChildScratch.Add(connected[i].m_Edge);
                }

                foreach (Entity edge in m_NetChildScratch)
                {
                    // ConnectedEdge ume da nosi i "local connection" ivice
                    // kojima čvor NIJE kraj — one nisu kraci i ne diraju se.
                    if (seen.Contains(edge) ||
                        !EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData) ||
                        (edgeData.m_Start != node && edgeData.m_End != node) ||
                        !IsSelectableNetEdge(edge))
                    {
                        continue;
                    }

                    seen.Add(edge);
                    result.Add(edge);
                }
            }
        }

        // Snimci za undo brisanja — isti oblik kao redo paste-a, pa rekreacija
        // ide kroz postojeći RecreateNetEdges (sa zavarivanjem krajeva).
        private List<NetEdgeSnapshot> SnapshotDeletableNetEdges()
        {
            List<NetEdgeSnapshot> snapshots = new List<NetEdgeSnapshot>();
            CollectDeletableNetEdges(m_NetCopyScratch);
            foreach (Entity edge in m_NetCopyScratch)
            {
                if (!EntityManager.TryGetComponent(edge, out Game.Net.Curve curve) ||
                    !EntityManager.TryGetComponent(edge, out PrefabRef prefabRef))
                {
                    continue;
                }

                bool hasUpgrade = EntityManager.TryGetComponent(edge, out Game.Net.Upgraded upgraded);
                bool hadElevation = EntityManager.TryGetComponent(edge, out Game.Net.Elevation elevation);
                CaptureNetEdgeEnds(edge, out Entity startNode, out Entity endNode,
                    out bool hadStart, out float2 startElevation, out bool hadEnd, out float2 endElevation,
                    out bool hasNodePositions, out float3 startNodePos, out float3 endNodePos,
                    out bool startNodeHasUpgrade, out CompositionFlags startNodeUpgrade,
                    out bool endNodeHasUpgrade, out CompositionFlags endNodeUpgrade,
                    out List<Entity> startMarkers, out List<Entity> endMarkers);
                snapshots.Add(new NetEdgeSnapshot
                {
                    m_Entity = edge,
                    m_Curve = curve.m_Bezier,
                    m_Prefab = prefabRef.m_Prefab,
                    m_HasUpgrade = hasUpgrade,
                    m_Upgrade = hasUpgrade ? upgraded.m_Flags : default,
                    m_HadElevation = hadElevation,
                    m_Elevation = hadElevation ? elevation.m_Elevation : default,
                    m_StartNode = startNode,
                    m_EndNode = endNode,
                    m_HasNodePositions = hasNodePositions,
                    m_StartNodePos = startNodePos,
                    m_EndNodePos = endNodePos,
                    m_StartNodeHasUpgrade = startNodeHasUpgrade,
                    m_StartNodeUpgrade = startNodeUpgrade,
                    m_EndNodeHasUpgrade = endNodeHasUpgrade,
                    m_EndNodeUpgrade = endNodeUpgrade,
                    m_StartMarkers = startMarkers,
                    m_EndMarkers = endMarkers,
                    m_HadStartElevation = hadStart,
                    m_StartElevation = startElevation,
                    m_HadEndElevation = hadEnd,
                    m_EndElevation = endElevation,
                });
            }

            return snapshots;
        }

        // Redo brisanja: undo je rekreirao NOVE entitete (zapis pamti mrtve),
        // pa se ivica nalazi pozicionim fallback-om — prefab + sredina krive.
        private void RedeleteNetEdges(List<NetEdgeSnapshot> snapshots)
        {
            if (snapshots == null || snapshots.Count == 0)
            {
                return;
            }

            if (m_NetSearchSystem == null)
            {
                m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            }

            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle dependencies);
            dependencies.Complete();

            foreach (NetEdgeSnapshot snapshot in snapshots)
            {
                // Originalni entitet još živ (npr. redo odmah posle undo-a
                // pre nego što je rekreacija progutala stari ID) — direktno.
                if (EntityManager.Exists(snapshot.m_Entity) &&
                    !EntityManager.HasComponent<Deleted>(snapshot.m_Entity) &&
                    !EntityManager.HasComponent<Temp>(snapshot.m_Entity) &&
                    EntityManager.HasComponent<Game.Net.Edge>(snapshot.m_Entity))
                {
                    m_SelectedNetEdges.Remove(snapshot.m_Entity);
                    DeleteNetEdgeWithNodes(snapshot.m_Entity);
                    continue;
                }

                // Bounds preko CELE krive: rekreirana deonica ume da bude
                // PODELJENA (tunel/zidovi), pa se brise svako parce koje celo
                // lezi na snimljenoj krivoj — midpoint match sam promasuje.
                float3 curveMin = math.min(math.min(snapshot.m_Curve.a, snapshot.m_Curve.b), math.min(snapshot.m_Curve.c, snapshot.m_Curve.d));
                float3 curveMax = math.max(math.max(snapshot.m_Curve.a, snapshot.m_Curve.b), math.max(snapshot.m_Curve.c, snapshot.m_Curve.d));
                RoadIterator iterator = new RoadIterator
                {
                    m_Bounds = new Bounds3(
                        curveMin - new float3(4f, 1000f, 4f),
                        curveMax + new float3(4f, 1000f, 4f)),
                    m_Results = new NativeList<Entity>(16, Allocator.Temp),
                };
                tree.Iterate(ref iterator, 0);

                for (int i = 0; i < iterator.m_Results.Length; i++)
                {
                    Entity candidate = iterator.m_Results[i];
                    if (!EntityManager.HasComponent<Game.Net.Edge>(candidate) ||
                        EntityManager.HasComponent<Game.Tools.EditorContainer>(candidate) ||
                        EntityManager.HasComponent<Owner>(candidate) ||
                        EntityManager.HasComponent<Temp>(candidate) ||
                        EntityManager.HasComponent<Deleted>(candidate) ||
                        !EntityManager.TryGetComponent(candidate, out Game.Net.Curve curve) ||
                        !EntityManager.TryGetComponent(candidate, out PrefabRef prefabRef) ||
                        prefabRef.m_Prefab != snapshot.m_Prefab)
                    {
                        continue;
                    }

                    if (!OnRecordCurve(snapshot.m_Curve, LaneMidpoint(curve.m_Bezier)) ||
                        !OnRecordCurve(snapshot.m_Curve, curve.m_Bezier.a) ||
                        !OnRecordCurve(snapshot.m_Curve, curve.m_Bezier.d))
                    {
                        continue;
                    }

                    m_SelectedNetEdges.Remove(candidate);
                    DeleteNetEdgeWithNodes(candidate);
                }

                iterator.m_Results.Dispose();
            }
        }

        // Pomak JEDNOG kraja samo po visini: kraj pun, kontrolne tacke
        // linearno opadajuce ka drugom kraju. MoveCurveEndpoint (tetivne
        // proporcije) je na degenerisanim osama (prava deonica po x ili z)
        // umeo da srusi luk — za cisto visinske operacije nema potrebe za njim.
        private static void ShiftCurveEndHeight(ref Bezier4x3 bezier, float shift, bool movingStart)
        {
            if (movingStart)
            {
                bezier.a.y += shift;
                bezier.b.y += shift * (2f / 3f);
                bezier.c.y += shift * (1f / 3f);
            }
            else
            {
                bezier.d.y += shift;
                bezier.c.y += shift * (2f / 3f);
                bezier.b.y += shift * (1f / 3f);
            }
        }

        private static float DistanceToRecordCurve(Bezier4x3 curve, float2 point)
        {
            MathUtils.Distance(curve.xz, point, out float t);
            return math.distance(MathUtils.Position(curve.xz, t), point);
        }

        // Kao gore, ali kandidat mora da bude blizu i PO VISINI tačke na
        // snimljenoj krivoj: sama xz metrika je pri redo-u brisanja hvatala
        // i tunel/most ISTOG prefaba tačno ispod ili iznad trase.
        private static bool OnRecordCurve(Bezier4x3 record, float3 point)
        {
            MathUtils.Distance(record.xz, point.xz, out float t);
            return math.distance(MathUtils.Position(record.xz, t), point.xz) <= 2f &&
                math.abs(MathUtils.Position(record, t).y - point.y) <= 4f;
        }

        // ---------- ALT: ravnanje među-čvorova u liniju ----------
        //
        // Ponašanje: selektovan čvor (ili lanac čvorova) sa TAČNO dve
        // ivice se postavlja na pravu 3D liniju između svoja dva suseda-sidra
        // (raspored duž lanca se čuva), a deonice kroz njega postaju prave.
        // Raskrsnice (3+ kraka) i slepi krajevi (1 krak) su sidra — stoje.

        // Prave ivice čvora — one kojima je čvor zaista kraj (ConnectedEdge
        // nosi i local-connection ivice, njih ne brojimo).
        private int GetNodeRealEdges(Entity node, out Entity first, out Entity second)
        {
            first = Entity.Null;
            second = Entity.Null;
            if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Net.ConnectedEdge> connected))
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < connected.Length; i++)
            {
                Entity edge = connected[i].m_Edge;
                if (!EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData) ||
                    (edgeData.m_Start != node && edgeData.m_End != node) ||
                    EntityManager.HasComponent<Deleted>(edge))
                {
                    continue;
                }

                count++;
                if (count == 1)
                {
                    first = edge;
                }
                else if (count == 2)
                {
                    second = edge;
                }
            }

            return count;
        }

        // Krak koji ravnanje sme da prepiše. Ivica u vlasništvu zgrade se NE
        // dira (StraightenNetEdge je odbija), pa čvor sa takvim krakom mora da
        // ostane SIDRO — inače bi se pomerio, a njegov krak ostao na starom
        // mestu i pukla bi veza.
        private bool IsStraightenableArm(Entity edge)
        {
            return edge != Entity.Null &&
                EntityManager.Exists(edge) &&
                !EntityManager.HasComponent<Deleted>(edge) &&
                !EntityManager.HasComponent<Owner>(edge);
        }

        private Entity GetEdgeOtherNode(Entity edge, Entity node)
        {
            if (EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData))
            {
                return edgeData.m_Start == node ? edgeData.m_End : edgeData.m_Start;
            }

            return Entity.Null;
        }

        // Hod od "from" preko "viaEdge" dokle god su čvorovi kvalifikovani;
        // usputni se dodaju u collect. Vraća sidro ili Null (petlja/rupa).
        private Entity WalkToStraightenAnchor(Entity from, Entity viaEdge, List<Entity> collect)
        {
            Entity current = GetEdgeOtherNode(viaEdge, from);
            while (current != Entity.Null && m_StraightenSet.Contains(current))
            {
                if (current == from || collect.Contains(current))
                {
                    return Entity.Null;
                }

                collect.Add(current);
                if (GetNodeRealEdges(current, out Entity e1, out Entity e2) != 2)
                {
                    return Entity.Null;
                }

                viaEdge = e1 == viaEdge ? e2 : e1;
                current = GetEdgeOtherNode(viaEdge, current);
            }

            return current;
        }

        // Jedan rešen lanac: čvorovi + njihove ciljne pozicije + sidra.
        private struct StraightenPlan
        {
            public List<Entity> m_Chain;
            public List<float3> m_Targets;
            public Entity m_AnchorA;
            public Entity m_AnchorB;
        }

        // True ako je bar jedan lanac stvarno ispravljen. Radi u DVE faze:
        // prvo se lanci reše bez ijedne izmene, pa se undo gura tek ako ima
        // šta da se ispravi — inače bi zatvoren prsten (svi čvorovi stepena 2,
        // hod se vrti u krug) pojeo redo stek a ne bi pomerio ništa.
        private bool StraightenSelectedNetNodes()
        {
            m_StraightenSet.Clear();
            foreach (Entity node in m_SelectedNodes)
            {
                if (IsSelectableNetNode(node) &&
                    GetNodeRealEdges(node, out Entity armA, out Entity armB) == 2 &&
                    IsStraightenableArm(armA) && IsStraightenableArm(armB))
                {
                    m_StraightenSet.Add(node);
                }
            }

            if (m_StraightenSet.Count == 0)
            {
                // Tap je stigao ali nijedan čvor ne kvalifikuje — bez poruke
                // ovo izgleda kao da prečica ne radi. Razlog po čvoru ide u
                // log (isti šablon kao "click on unselectable").
                // JEDNA poruka za ceo pokušaj: razlog prvog čvora je uvek
                // isti kao i ostalih u praksi, a selekcija ume da broji stotine.
                if (m_SelectedNodes.Count > 0)
                {
                    Entity first = m_SelectedNodes[0];
                    int arms = GetNodeRealEdges(first, out Entity armA, out Entity armB);
                    string reason = !IsSelectableNetNode(first) ? "node is not selectable (owned, temporary or unsupported layer)"
                        : arms != 2 ? $"node has {arms} real arms, straighten needs exactly 2"
                        : !IsStraightenableArm(armA) || !IsStraightenableArm(armB) ? "one arm belongs to a building or is being deleted"
                        : "unknown";
                    Mod.Log.Info($"Copaste: straighten did nothing for {m_SelectedNodes.Count} selected node(s) — first one: {reason}");
                }

                return false;
            }

            // FAZA 1 — rešavanje (ništa se ne menja).
            List<StraightenPlan> plans = new List<StraightenPlan>();
            HashSet<Entity> visited = new HashSet<Entity>();
            foreach (Entity start in m_SelectedNodes)
            {
                if (!m_StraightenSet.Contains(start) || visited.Contains(start) ||
                    GetNodeRealEdges(start, out Entity edgeA, out Entity edgeB) != 2)
                {
                    continue;
                }

                // Lanac: [obrnut hod na jednu stranu] + start + [hod na drugu].
                m_StraightenSide.Clear();
                Entity anchorA = WalkToStraightenAnchor(start, edgeA, m_StraightenSide);
                m_StraightenChain.Clear();
                for (int i = m_StraightenSide.Count - 1; i >= 0; i--)
                {
                    m_StraightenChain.Add(m_StraightenSide[i]);
                }

                m_StraightenChain.Add(start);
                m_StraightenSide.Clear();
                Entity anchorB = WalkToStraightenAnchor(start, edgeB, m_StraightenSide);
                m_StraightenChain.AddRange(m_StraightenSide);

                foreach (Entity node in m_StraightenChain)
                {
                    visited.Add(node);
                }

                if (anchorA == Entity.Null || anchorB == Entity.Null || anchorA == anchorB ||
                    !EntityManager.TryGetComponent(anchorA, out Game.Net.Node anchorStartData) ||
                    !EntityManager.TryGetComponent(anchorB, out Game.Net.Node anchorEndData))
                {
                    continue;
                }

                // Kumulativna dužina duž trenutne izlomljene linije — čuva
                // raspored čvorova (projekcija bi cik-cak lancu preturila red).
                List<float> cumulative = new List<float>(m_StraightenChain.Count);
                float3 previous = anchorStartData.m_Position;
                float total = 0f;
                bool broken = false;
                foreach (Entity node in m_StraightenChain)
                {
                    if (!EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
                    {
                        broken = true;
                        break;
                    }

                    total += math.distance(nodeData.m_Position, previous);
                    cumulative.Add(total);
                    previous = nodeData.m_Position;
                }

                total += math.distance(anchorEndData.m_Position, previous);
                if (broken || total < 1e-3f)
                {
                    continue;
                }

                List<float3> targets = new List<float3>(m_StraightenChain.Count);
                for (int i = 0; i < m_StraightenChain.Count; i++)
                {
                    targets.Add(math.lerp(anchorStartData.m_Position, anchorEndData.m_Position, cumulative[i] / total));
                }

                plans.Add(new StraightenPlan
                {
                    m_Chain = new List<Entity>(m_StraightenChain),
                    m_Targets = targets,
                    m_AnchorA = anchorA,
                    m_AnchorB = anchorB,
                });
            }

            if (plans.Count == 0)
            {
                return false;
            }

            // FAZA 2 — primena. Undo tek sada: istorija se ne dira uzalud.
            PushTransformUndo();

            // Stare pozicije čvorova: ravnanje ivica ide POSLE pomeranja
            // čvorova, a bočni ofset krajeva krive se meri od STARE pozicije.
            m_StraightenOldNodePos.Clear();

            bool any = false;
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            foreach (StraightenPlan plan in plans)
            {
                for (int i = 0; i < plan.m_Chain.Count; i++)
                {
                    Entity node = plan.m_Chain[i];
                    if (!EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
                    {
                        continue;
                    }

                    float3 target = plan.m_Targets[i];
                    float3 delta = target - nodeData.m_Position;
                    m_StraightenOldNodePos[node] = nodeData.m_Position;
                    nodeData.m_Position = target;
                    EntityManager.SetComponentData(node, nodeData);

                    if (EntityManager.TryGetComponent(node, out Game.Net.NodeGeometry geometry))
                    {
                        geometry.m_Bounds.min += delta;
                        geometry.m_Bounds.max += delta;
                        geometry.m_Offset += delta.y;
                        EntityManager.SetComponentData(node, geometry);
                    }

                    // Elevacija čvora = nova visina iznad terena — bez nje bi
                    // igra liniju preko udoline vratila na teren.
                    SetNetElevation(node, target.y - TerrainUtils.SampleHeight(ref heightData, target));

                    // Deca (stubovi, stajališta) idu APSOLUTNO za čvorom:
                    // teren-follow varijanta bi progutala čisto vertikalni
                    // pomak (xz se ne menja, pa je uzorak terena isti).
                    ShiftNetSubObjects(node, delta);
                    any = true;
                }

                // Sve ivice lanca (i sidro-ivice) postaju prave linije.
                m_StraightenEdgeSeen.Clear();
                foreach (Entity node in plan.m_Chain)
                {
                    if (GetNodeRealEdges(node, out Entity e1, out Entity e2) == 0)
                    {
                        continue;
                    }

                    StraightenNetEdge(e1, ref heightData);
                    StraightenNetEdge(e2, ref heightData);
                }

                // Update + settle: čvorovi lanca, njihove ivice, sidra.
                foreach (Entity node in plan.m_Chain)
                {
                    if (EntityManager.Exists(node))
                    {
                        EntityManager.AddComponent<Updated>(node);
                        EntityManager.AddComponent<BatchesUpdated>(node);
                        m_DelayedNetSettle[node] = 4;
                    }
                }

                foreach (Entity edge in m_StraightenEdgeSeen)
                {
                    if (EntityManager.Exists(edge) && !EntityManager.HasComponent<Deleted>(edge))
                    {
                        EntityManager.AddComponent<Updated>(edge);
                        EntityManager.AddComponent<BatchesUpdated>(edge);

                        // Prikačeni objekti (stajališta) sami se preračunaju iz
                        // nove krive — savijanje u pravu nije kruti pomak, pa
                        // im se pozicija NE dira, samo se traži update.
                        MarkNetSubObjectsUpdated(edge);
                    }
                }

                // Sidro dobija pun settle: njegovi OSTALI kraci moraju da
                // preseku geometriju na novi ugao, inače ostaje šav.
                foreach (Entity anchor in new[] { plan.m_AnchorA, plan.m_AnchorB })
                {
                    if (EntityManager.Exists(anchor) && !EntityManager.HasComponent<Deleted>(anchor))
                    {
                        EntityManager.AddComponent<Updated>(anchor);
                        EntityManager.AddComponent<BatchesUpdated>(anchor);
                        m_DelayedNetSettle[anchor] = 4;
                        ResettleNetNode(anchor);
                    }
                }
            }

            return any;
        }

        // Pod-objekti prate roditelja apsolutno (bez uzorka terena).
        private void ShiftNetSubObjects(Entity parent, float3 delta)
        {
            if (!EntityManager.TryGetBuffer(parent, true, out DynamicBuffer<Game.Objects.SubObject> subObjects))
            {
                return;
            }

            m_NetChildScratch.Clear();
            for (int i = 0; i < subObjects.Length; i++)
            {
                m_NetChildScratch.Add(subObjects[i].m_SubObject);
            }

            foreach (Entity child in m_NetChildScratch)
            {
                if (!EntityManager.Exists(child) ||
                    !EntityManager.HasComponent<Game.Objects.Attached>(child) ||
                    !EntityManager.TryGetComponent(child, out Game.Objects.Transform transform))
                {
                    continue;
                }

                transform.m_Position += delta;
                EntityManager.SetComponentData(child, transform);
                EntityManager.AddComponent<Updated>(child);
                EntityManager.AddComponent<BatchesUpdated>(child);
            }
        }

        // Samo zahtev za update — igra sama vrati dete na krivu po svom
        // Attached.m_CurvePosition.
        private void MarkNetSubObjectsUpdated(Entity parent)
        {
            if (!EntityManager.TryGetBuffer(parent, true, out DynamicBuffer<Game.Objects.SubObject> subObjects))
            {
                return;
            }

            m_NetChildScratch.Clear();
            for (int i = 0; i < subObjects.Length; i++)
            {
                m_NetChildScratch.Add(subObjects[i].m_SubObject);
            }

            foreach (Entity child in m_NetChildScratch)
            {
                if (EntityManager.Exists(child) && !EntityManager.HasComponent<Deleted>(child))
                {
                    EntityManager.AddComponent<Updated>(child);
                    EntityManager.AddComponent<BatchesUpdated>(child);
                }
            }
        }

        private void StraightenNetEdge(Entity edge, ref TerrainHeightData heightData)
        {
            if (edge == Entity.Null || !m_StraightenEdgeSeen.Add(edge) ||
                !EntityManager.Exists(edge) ||
                EntityManager.HasComponent<Deleted>(edge) ||
                EntityManager.HasComponent<Owner>(edge) ||
                !EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData) ||
                !EntityManager.TryGetComponent(edge, out Game.Net.Curve curve) ||
                !EntityManager.TryGetComponent(edgeData.m_Start, out Game.Net.Node startNode) ||
                !EntityManager.TryGetComponent(edgeData.m_End, out Game.Net.Node endNode))
            {
                return;
            }

            float3 a = startNode.m_Position;
            float3 d = endNode.m_Position;

            // BOČNI OFSET KRAJEVA SE ČUVA. Kraj krive ne stoji u centru
            // čvora nego pomeren u stranu — u tom ofsetu ŽIVI poravnanje
            // traka (ceo trouglić ne radi ništa drugo nego ga podešava).
            // Lepljenje krajeva na centre čvorova ga je brisalo, pa je jedan
            // ALT poništavao sve poravnate prelaze 3→2 i 2→1 trake.
            // Ofset se zadrži i samo zarotira na novu osu.
            float3 oldStart = m_StraightenOldNodePos.TryGetValue(edgeData.m_Start, out float3 previousStart)
                ? previousStart
                : startNode.m_Position;
            float3 oldEnd = m_StraightenOldNodePos.TryGetValue(edgeData.m_End, out float3 previousEnd)
                ? previousEnd
                : endNode.m_Position;

            float2 oldChord = (oldEnd - oldStart).xz;
            float2 newChord = (d - a).xz;
            float oldChordLength = math.length(oldChord);
            float newChordLength = math.length(newChord);
            if (oldChordLength > 1e-3f && newChordLength > 1e-3f)
            {
                // Klamp od dva metra: pravi ofseti poravnanja su reda pola
                // trake. Veći bi značio da je kriva već negde odlutala i ne
                // vredi ga vući u ispravljenu deonicu.
                float2 offsetStart = ClampLateralOffset((curve.m_Bezier.a - oldStart).xz);
                float2 offsetEnd = ClampLateralOffset((curve.m_Bezier.d - oldEnd).xz);
                a.xz += RotateAndScale(offsetStart, oldChord / oldChordLength, newChord / newChordLength, 1f);
                d.xz += RotateAndScale(offsetEnd, oldChord / oldChordLength, newChord / newChordLength, 1f);
            }

            curve.m_Bezier = new Bezier4x3(a, math.lerp(a, d, 1f / 3f), math.lerp(a, d, 2f / 3f), d);
            curve.m_Length = math.distance(a, d);
            EntityManager.SetComponentData(edge, curve);

            // Elevacija sredine deonice (levo/desno) = visina iznad terena.
            float3 midpoint = math.lerp(a, d, 0.5f);
            SetNetElevation(edge, midpoint.y - TerrainUtils.SampleHeight(ref heightData, midpoint));
        }

        private readonly Dictionary<Entity, float3> m_StraightenOldNodePos = new Dictionary<Entity, float3>();

        private static float2 ClampLateralOffset(float2 offset)
        {
            float length = math.length(offset);
            return length > 2f ? offset * (2f / length) : offset;
        }

        // Završno markiranje visinskih poteza: ivice + DALJI (nepokretni) kraj
        // rampe. Bez njega raskrsnica na dnu rampe zadrži geometriju računatu
        // za ravan prilaz i "poskoči" tek kad je nešto drugo takne.
        private void MarkNetEdgesAndFarNodes(HashSet<Entity> moving)
        {
            foreach (Entity edge in m_NetEdgeScratch)
            {
                if (!EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge))
                {
                    continue;
                }

                EntityManager.AddComponent<Updated>(edge);
                EntityManager.AddComponent<BatchesUpdated>(edge);

                if (!EntityManager.TryGetComponent(edge, out Game.Net.Edge edgeData))
                {
                    continue;
                }

                // Samo rampe imaju "dalji" kraj: kruta ivica ima oba u skupu,
                // local-connection ivica nijedan.
                bool startMoving = moving.Contains(edgeData.m_Start);
                if (startMoving == moving.Contains(edgeData.m_End))
                {
                    continue;
                }

                Entity farNode = startMoving ? edgeData.m_End : edgeData.m_Start;
                MarkFarNodeAndItsEdges(farNode);
            }
        }

        // Podizanje/spuštanje deonice kojoj se OBA kraja menjaju samo po
        // visini: krajevi idu na zadatu visinu, a kontrolne tačke dobijaju
        // linearno raspodeljen pomak krajeva (x i z ostaju netaknuti, pa se
        // oblik krive čuva). Očekuje popunjen m_NetShiftScratch.
        private void ShiftCurveHeights(ref Bezier4x3 bezier, float startY, float endY, Entity startNode, Entity endNode)
        {
            float startShift = m_NetShiftScratch.TryGetValue(startNode, out float s) ? s : 0f;
            float endShift = m_NetShiftScratch.TryGetValue(endNode, out float e) ? e : 0f;

            // Krajevi idu za POMAKOM svog čvora, ne NA čvor: ručno namešten
            // visinski ofset kraja (ručka/PgUp na sticky) mora da preživi.
            bezier.a.y += startShift;
            bezier.d.y += endShift;
            bezier.b.y += math.lerp(startShift, endShift, 1f / 3f);
            bezier.c.y += math.lerp(startShift, endShift, 2f / 3f);
        }

        // Pomak elevacije ivice iz pomaka njenih krajeva: elevacija je sredina
        // deonice, pa kraj koji je mirovao doprinosi nulom (obe strane rampe
        // dobiju pola). Očekuje popunjen m_NetShiftScratch.
        private void ShiftEdgeElevationFromNodeShifts(Entity edge, Game.Net.Edge edgeData)
        {
            float startShift = m_NetShiftScratch.TryGetValue(edgeData.m_Start, out float s) ? s : 0f;
            float endShift = m_NetShiftScratch.TryGetValue(edgeData.m_End, out float e) ? e : 0f;
            float delta = (startShift + endShift) * 0.5f;
            if (math.abs(delta) > 0.001f)
            {
                ShiftLaneElevation(edge, delta);
            }
        }

        private void ApplyPastedNetNodeMarkers(Entity node, int index)
        {
            if (index < 0 || !EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node) ||
                !EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
            {
                return;
            }

            foreach (NetNodeMarker marker in m_ClipboardNetNodeMarkers)
            {
                if (marker.m_NodeIndex != index ||
                    !EntityManager.Exists(marker.m_Prefab) ||
                    !m_EmittedNodeMarkers.Add((node, marker.m_Prefab)))
                {
                    continue;
                }

                // Cvor vec ima isti marker (igra ga sama dodala ili raniji
                // prolaz) — vrati dedup unos da kasniji prolaz opet proveri.
                // Bez stvarnog uklanjanja je "continue" ispod trajno zaključavao
                // par (cvor, prefab): jedan lazno pozitivan nalaz bi ostavio
                // raskrsnicu bez kruznog toka do kraja prozora.
                if (NodeHasMarker(node, marker.m_Prefab))
                {
                    m_EmittedNodeMarkers.Remove((node, marker.m_Prefab));
                    continue;
                }

                // Vanila put: objekat-definicija sa Attach na POSTOJECI cvor
                // (GenerateObjectsSystem -> CreateAttached). Permanent — stamp
                // je vec potvrdjen, nema Temp faze.
                EntityCommandBuffer buffer = m_ToolOutputBarrier.CreateCommandBuffer();
                Entity definitionEntity = buffer.CreateEntity();

                CreationDefinition creation = default;
                creation.m_Prefab = marker.m_Prefab;
                creation.m_Attached = node;
                creation.m_Flags = CreationFlags.Permanent | CreationFlags.Attach;

                ObjectDefinition objectDefinition = default;
                objectDefinition.m_Position = nodeData.m_Position;
                objectDefinition.m_Rotation = nodeData.m_Rotation;
                objectDefinition.m_LocalPosition = nodeData.m_Position;
                objectDefinition.m_LocalRotation = nodeData.m_Rotation;
                objectDefinition.m_Scale = new float3(1f, 1f, 1f);
                objectDefinition.m_ParentMesh = -1;
                objectDefinition.m_Probability = 100;
                objectDefinition.m_PrefabSubIndex = -1;

                buffer.AddComponent(definitionEntity, creation);
                buffer.AddComponent(definitionEntity, objectDefinition);
                buffer.AddComponent(definitionEntity, default(Updated));
                KeepDefinitionsAlive();

                // Updated ODMAH ne vredi: marker fizicki nastane tek frejm-dva
                // kasnije (barrier + object pipeline), pa bi se kompozicija
                // preracunala BEZ njega — kruzni tok se pojavljivao tek kad
                // korisnik pomeri nesto. ODLOZENI settle saceka da marker
                // postoji, pa tek onda tera cvor (i njegove ivice) na update.
                m_DelayedNetSettle[node] = 8;
            }
        }

        private bool TryGetRecordNodeUpgrade(int index, out CompositionFlags upgrade)
        {
            GetClipboardNetNodeUpgrade(index, out bool has, out upgrade);
            return has;
        }

        private List<Entity> CollectClipboardMarkers(int index)
        {
            List<Entity> markers = null;
            if (index >= 0)
            {
                foreach (NetNodeMarker marker in m_ClipboardNetNodeMarkers)
                {
                    if (marker.m_NodeIndex == index)
                    {
                        (markers ??= new List<Entity>()).Add(marker.m_Prefab);
                    }
                }
            }

            return markers;
        }

        // Zakazani marker attach za CVOR KOJI JOS NE POSTOJI (rekreacija):
        // po poziciji, obradjuje se u pending prozoru kad cvor ozivi.
        private struct PendingMarkerAttach
        {
            public float3 m_Position;
            public Entity m_Prefab;

            // Koliko frejmova je čvor već pronađen a markera i dalje nema.
            // Kačenje čeka ovaj period jer i igra sama ume da vrati marker
            // rekreiranog čvora — bez čekanja se dobiju DVA.
            public int m_GraceFrames;
        }

        private const int kMarkerAttachGraceFrames = 8;

        private readonly List<PendingMarkerAttach> m_PendingMarkerAttaches = new List<PendingMarkerAttach>();

        // Sopstveni prozor markera: remap brojač se nulira čim remap lista
        // opusti, a čvor za marker nastaje TEK POSLE toga — vezivanje za
        // remap prozor je čistilo red pre ijednog pokušaja kačenja (undo
        // brisanja je gubio kružne tokove).
        private int m_PendingMarkerFrames;

        // Generacija klipbord tabela čvorova — raste na svaki rebuild;
        // zapisi sa starom generacijom ne smeju da čitaju nove tabele.
        private int m_ClipboardNetGeneration = 1;

        private void EmitRecreatedNodeState(EntityCommandBuffer buffer, ref Unity.Mathematics.Random random,
            HashSet<int3> seen, float3 nodePoint, bool hasUpgrade, CompositionFlags upgrade,
            List<Entity> markers, Entity roadPrefab)
        {
            if (!seen.Add(math.asint(nodePoint)))
            {
                return;
            }

            if (hasUpgrade && roadPrefab != Entity.Null && EntityManager.Exists(roadPrefab))
            {
                Entity nodeDefinition = buffer.CreateEntity();

                CreationDefinition nodeCreation = default;
                nodeCreation.m_Prefab = roadPrefab;
                nodeCreation.m_RandomSeed = random.NextInt();
                nodeCreation.m_Flags |= CreationFlags.Permanent;

                NetCourse nodeCourse = default;
                nodeCourse.m_Curve = new Bezier4x3(nodePoint, nodePoint, nodePoint, nodePoint);
                nodeCourse.m_Length = 0f;
                nodeCourse.m_FixedIndex = -1;

                // Elevacija kao pri paste-u: kurs BEZ nje ume da pobedi merge
                // i skine Elevation povišenog čvora — undo bi mostu vratio
                // prizemnu raskrsnicu.
                TerrainHeightData nodeTerrain = m_TerrainSystem.GetHeightData();
                float nodeElev = nodePoint.y - TerrainUtils.SampleHeight(ref nodeTerrain, nodePoint);
                nodeCourse.m_Elevation = new float2(nodeElev, nodeElev);
                nodeCourse.m_StartPosition = LaneCoursePos(nodeCourse.m_Curve, 0f, CoursePosFlags.IsFirst | CoursePosFlags.DisableMerge);
                nodeCourse.m_StartPosition.m_Position = nodePoint;
                nodeCourse.m_StartPosition.m_Elevation = new float2(nodeElev, nodeElev);
                nodeCourse.m_EndPosition = LaneCoursePos(nodeCourse.m_Curve, 1f, CoursePosFlags.IsLast | CoursePosFlags.DisableMerge);
                nodeCourse.m_EndPosition.m_Position = nodePoint;
                nodeCourse.m_EndPosition.m_Elevation = new float2(nodeElev, nodeElev);

                buffer.AddComponent(nodeDefinition, nodeCreation);
                buffer.AddComponent(nodeDefinition, nodeCourse);
                buffer.AddComponent(nodeDefinition, new Game.Net.Upgraded { m_Flags = upgrade });
                buffer.AddComponent(nodeDefinition, default(Updated));
            }

            if (markers != null)
            {
                foreach (Entity markerPrefab in markers)
                {
                    if (EntityManager.Exists(markerPrefab))
                    {
                        m_PendingMarkerAttaches.Add(new PendingMarkerAttach { m_Position = nodePoint, m_Prefab = markerPrefab });
                    }
                }
            }
        }

        // Obrada zakazanih marker attach-eva: cvor se trazi po poziciji (do
        // 1 m), marker se kaci vanila putem; neuspeli pokusavaju do isteka
        // pending prozora.
        private void RunPendingMarkerAttaches()
        {
            if (m_PendingMarkerAttaches.Count == 0)
            {
                return;
            }

            if (m_PendingMarkerFrames <= 0)
            {
                m_PendingMarkerAttaches.Clear();
                return;
            }

            m_PendingMarkerFrames--;

            for (int i = m_PendingMarkerAttaches.Count - 1; i >= 0; i--)
            {
                PendingMarkerAttach pending = m_PendingMarkerAttaches[i];
                if (!TryFindNetNodeAt(pending.m_Position, out Entity node))
                {
                    continue;
                }

                // Čvor postoji, ali marker možda tek stiže: ako ga igra sama
                // vrati, naš posao otpada. Zato se prvo čeka, pa proverava.
                if (NodeHasMarker(node, pending.m_Prefab))
                {
                    m_PendingMarkerAttaches.RemoveAt(i);
                    continue;
                }

                if (pending.m_GraceFrames < kMarkerAttachGraceFrames)
                {
                    pending.m_GraceFrames++;
                    m_PendingMarkerAttaches[i] = pending;
                    continue;
                }

                if (EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
                {
                    EntityCommandBuffer buffer = m_ToolOutputBarrier.CreateCommandBuffer();
                    Entity definitionEntity = buffer.CreateEntity();

                    CreationDefinition creation = default;
                    creation.m_Prefab = pending.m_Prefab;
                    creation.m_Attached = node;
                    creation.m_Flags = CreationFlags.Permanent | CreationFlags.Attach;

                    ObjectDefinition objectDefinition = default;
                    objectDefinition.m_Position = nodeData.m_Position;
                    objectDefinition.m_Rotation = nodeData.m_Rotation;
                    objectDefinition.m_LocalPosition = nodeData.m_Position;
                    objectDefinition.m_LocalRotation = nodeData.m_Rotation;
                    objectDefinition.m_Scale = new float3(1f, 1f, 1f);
                    objectDefinition.m_ParentMesh = -1;
                    objectDefinition.m_Probability = 100;
                    objectDefinition.m_PrefabSubIndex = -1;

                    buffer.AddComponent(definitionEntity, creation);
                    buffer.AddComponent(definitionEntity, objectDefinition);
                    buffer.AddComponent(definitionEntity, default(Updated));

                    KeepDefinitionsAlive();
                    m_DelayedNetSettle[node] = 8;
                }

                m_PendingMarkerAttaches.RemoveAt(i);
            }
        }

        private bool TryFindNetNodeAt(float3 position, out Entity node)
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
                m_Bounds = new Bounds3(position - new float3(2f, 1000f, 2f), position + new float3(2f, 1000f, 2f)),
                m_Results = new NativeList<Entity>(8, Allocator.Temp),
            };
            tree.Iterate(ref iterator, 0);

            // I VISINA, i najbliži umesto prvog: kutija je visoka dva
            // kilometra, pa uzdignuti kružni tok i prizemna raskrsnica tačno
            // ispod njega imaju isti xz. Bez visinskog uslova bi marker
            // kružnog toka umeo da sleti na pogrešnu od te dve.
            float bestDistance = float.MaxValue;
            for (int i = 0; i < iterator.m_Results.Length; i++)
            {
                Entity candidate = iterator.m_Results[i];
                if (EntityManager.TryGetComponent(candidate, out Game.Net.Node nodeData) &&
                    !EntityManager.HasComponent<Temp>(candidate) &&
                    !EntityManager.HasComponent<Deleted>(candidate) &&
                    !EntityManager.HasComponent<Owner>(candidate) &&

                    // Čvorovi ograda (container mreže) nisu kandidati — marker
                    // kružnog toka na čvoru OGRADE bi raskrsnicu ostavio bez.
                    !EntityManager.HasComponent<Game.Tools.EditorContainer>(candidate) &&
                    math.distancesq(nodeData.m_Position.xz, position.xz) <= 1f &&
                    math.abs(nodeData.m_Position.y - position.y) <= 2f &&
                    math.distancesq(nodeData.m_Position, position) < bestDistance)
                {
                    bestDistance = math.distancesq(nodeData.m_Position, position);
                    node = candidate;
                    // bez break-a: bira se stvarno najbliži, ne prvi iz stabla
                }
            }

            iterator.m_Results.Dispose();
            return node != Entity.Null;
        }

        // Cvor koji odlazi mora da povede SVE svoje pod-objekte. Prvo je
        // probano samo sa markerima (kruzni tok / semafor / stop znak po
        // NetObjectData.nodeMask), ali ostrvo kruznog toka nije takav marker
        // nego obican pod-objekat cvora — pa je ostajalo da visi na mestu
        // obrisane raskrsnice. Ovo radi i vanila buldozer.
        //
        // Cena: pod-objekte koje kopiranje ne hvata (npr. stajaliste) undo ne
        // vraca. Bolje to nego sirocici na mapi koje korisnik mora rucno da
        // trazi i brise.
        private void DeleteNodeSubObjects(Entity node)
        {
            if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Objects.SubObject> subs))
            {
                return;
            }

            m_NodeSubObjectScratch.Clear();
            for (int i = 0; i < subs.Length; i++)
            {
                Entity sub = subs[i].m_SubObject;
                if (EntityManager.Exists(sub) && !EntityManager.HasComponent<Deleted>(sub))
                {
                    m_NodeSubObjectScratch.Add(sub);
                }
            }

            // Tek posle citanja bafera: AddComponent ga invalidira.
            foreach (Entity sub in m_NodeSubObjectScratch)
            {
                EntityManager.AddComponent<Deleted>(sub);
            }
        }

        private readonly List<Entity> m_NodeSubObjectScratch = new List<Entity>();

        private List<Entity> CollectNodeMarkerPrefabs(Entity node)
        {
            List<Entity> markers = null;
            if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Objects.SubObject> subs))
            {
                return null;
            }

            for (int i = 0; i < subs.Length; i++)
            {
                if (EntityManager.TryGetComponent(subs[i].m_SubObject, out PrefabRef subPrefab) &&
                    EntityManager.TryGetComponent(subPrefab.m_Prefab, out NetObjectData netObject) &&
                    ((netObject.m_CompositionFlags.m_General & CompositionFlags.nodeMask.m_General) != default(CompositionFlags.General) ||
                     (netObject.m_CompositionFlags.m_Left & CompositionFlags.nodeMask.m_Left) != default(CompositionFlags.Side) ||
                     (netObject.m_CompositionFlags.m_Right & CompositionFlags.nodeMask.m_Right) != default(CompositionFlags.Side)))
                {
                    (markers ??= new List<Entity>()).Add(subPrefab.m_Prefab);
                }
            }

            return markers;
        }

        private bool NodeHasMarker(Entity node, Entity markerPrefab)
        {
            if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<Game.Objects.SubObject> subs))
            {
                return false;
            }

            for (int i = 0; i < subs.Length; i++)
            {
                // Obrisan ili privremen marker se NE racuna: bafer ga ume drzati
                // jos koji frejm, a lazno pozitivan nalaz ovde znaci raskrsnica
                // bez kruznog toka.
                Entity sub = subs[i].m_SubObject;
                if (EntityManager.HasComponent<Deleted>(sub) ||
                    EntityManager.HasComponent<Temp>(sub))
                {
                    continue;
                }

                if (EntityManager.TryGetComponent(sub, out PrefabRef subPrefab) &&
                    subPrefab.m_Prefab == markerPrefab)
                {
                    return true;
                }
            }

            return false;
        }

        // Postavi elevaciju na zadatu SREDINU, ali zadrži postojeću razliku
        // levo/desno (put uz kej ima npr. (3,0) — poravnanje na (h,h) bi mu
        // pojelo potporni zid). Ispod praga se komponenta skida.
        private void SetNetElevation(Entity entity, float height)
        {
            float2 sideOffset = float2.zero;
            if (EntityManager.TryGetComponent(entity, out Game.Net.Elevation existing))
            {
                sideOffset = existing.m_Elevation - (math.csum(existing.m_Elevation) * 0.5f);
            }

            float2 sided = height + sideOffset;
            if (math.abs(sided.x) <= 0.01f && math.abs(sided.y) <= 0.01f)
            {
                if (EntityManager.HasComponent<Game.Net.Elevation>(entity))
                {
                    EntityManager.RemoveComponent<Game.Net.Elevation>(entity);
                }
            }
            else if (EntityManager.HasComponent<Game.Net.Elevation>(entity))
            {
                EntityManager.SetComponentData(entity, new Game.Net.Elevation { m_Elevation = sided });
            }
            else
            {
                EntityManager.AddComponentData(entity, new Game.Net.Elevation { m_Elevation = sided });
            }
        }
    }
}
