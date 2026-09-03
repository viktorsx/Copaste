// Copaste — copy/paste tool for props in Cities: Skylines II.
// Selection/highlight patterns based on MIT-licensed mods by yenyang;
// placement via the game's own definition pipeline (Apache-2.0 patterns, credited in README).

namespace Copaste
{
    using System.Collections.Generic;
    using Colossal.Entities;
    using Colossal.Mathematics;
    using Game.Audio;
    using Game.Common;
    using Game.Input;
    using Game.Prefabs;
    using Game.Rendering;
    using Game.Simulation;
    using Game.Tools;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Mathematics;
    using UnityEngine.InputSystem;
    using UnityEngine.InputSystem.Controls;

    public partial class CopasteToolSystem : ToolBaseSystem
    {
        // Zaštitni limiti — velike selekcije guše igru (definicije/overlay po
        // frejmu). Od 1.2.0 podesivi u Options; defaulti su stare konstante.
        private static int kMaxSelection => Mod.Settings != null ? Mod.Settings.MaxSelection : 1000;

        private static int kMaxOverlayCircles => Mod.Settings != null ? Mod.Settings.MaxOverlayShapes : 400;

        private static readonly UnityEngine.Color kHoverColor = new UnityEngine.Color(1f, 1f, 1f, 0.7f);
        private static readonly UnityEngine.Color kSelectedColor = new UnityEngine.Color(0.2f, 0.85f, 1f, 1f);
        private static readonly UnityEngine.Color kListFocusColor = new UnityEngine.Color(0.44f, 0.93f, 0.63f, 1f);
        private static readonly UnityEngine.Color kPasteColor = new UnityEngine.Color(0.3f, 1f, 0.45f, 0.9f);

        private enum Mode
        {
            Select,
            Paste,

            // Selektovana zgrada prati kursor (uz road snap); klik spušta, RMB otkazuje.
            Relocate,
        }

        private struct ClipboardItem
        {
            public Entity m_Prefab;
            public float3 m_Offset;
            public quaternion m_Rotation;
            public float m_HeightOffset;
            public float m_Diameter;

            // Zgrade: potpisi površina IZVORA (null za ne-zgrade; prazna lista
            // = original nema nijednu). Paste posle construction-a briše višak
            // — kopija nasleđuje igračeva brisanja placa.
            public List<SurfaceSig> m_SurfaceSigs;
            public bool m_HadTree;
            public Game.Objects.Tree m_Tree;

            // PseudoRandomSeed originala — određuje varijaciju boje/izgleda.
            public bool m_HasSeed;
            public ushort m_Seed;

            // Custom boja (Recolor mod): ColorSet sa originala, ako postoji.
            public bool m_HasCustomColor;
            public Game.Rendering.ColorSet m_CustomColor;

            // Fiksan seed za CreationDefinition — da preview ne menja boje iz frejma u frejm.
            public int m_PreviewSeed;
        }

        // Ručno farbana površina (Surface area) u clipboardu: prefab + poligon
        // relativno na centroid selekcije. Kopira se uz zgrade (IncludeBuildings).
        private struct AreaClipboardItem
        {
            public Entity m_Prefab;
            public float2[] m_NodeOffsets;
        }

        private readonly List<AreaClipboardItem> m_ClipboardAreas = new List<AreaClipboardItem>();

        // Farbane površine trenutno u selekciji (pune se marquee-em, WYSIWYG:
        // ono što je ocrtano — to se i kopira).
        private readonly List<Entity> m_SelectedSurfaces = new List<Entity>();

        public int SelectedCount => m_Selected.Count + m_SelectedSurfaces.Count + m_SelectedLanes.Count + m_SelectedNodes.Count + m_SelectedNetEdges.Count;

        // Vrste koje COPY stvarno obrađuje. Za mreže se broje IVICE koje bi
        // kopija stvarno ponela: selektovane + one čija su oba kraja u
        // selekciji (dva susedna čvora nose svoj segment). Goli čvor bez
        // takve ivice ne pali Copy.
        public int CopyableSelectedCount => m_Selected.Count + m_SelectedSurfaces.Count + m_SelectedLanes.Count + CopyableNetEdgeCount();

        // Vrste koje DELETE sme da obriše. Selektovan čvor mreže broji se jer
        // briše svoje krake.
        public int DeletableSelectedCount => m_Selected.Count + m_SelectedSurfaces.Count + m_SelectedLanes.Count + m_SelectedNodes.Count + m_SelectedNetEdges.Count;

        // Broj selektovanih NE-zgrada sa transformom — visinske i align
        // operacije deluju samo na njih, pa UI dugmad gate-uju na ovo
        // (inače bi bila upaljena dugmad koja ništa ne rade).
        public int PropTargetCount
        {
            get
            {
                EnsureDerivedSelectionData();
                return m_CachedPropTargetCount;
            }
        }

        private int ComputePropTargetCount()
        {
            int count = 0;
            foreach (Entity entity in m_Selected)
            {
                if (!IsBuilding(entity) &&
                    EntityManager.Exists(entity) &&
                    EntityManager.HasComponent<Game.Objects.Transform>(entity))
                {
                    count++;
                }
            }

            return count;
        }

        // ---------- Keš izvedenih podataka o selekciji ----------
        //
        // UI sistem radi SVAKI frejm i čita ove brojke; računanje je prolaz
        // kroz celu selekciju sa 2-3 ECS upita po objektu — izmereno 651 us
        // po frejmu na velikoj selekciji (39% budžeta pri 60 fps je odlazilo
        // na osvežavanje brojeva koji se menjaju samo kad se selekcija
        // promeni). Zato: potpis selekcije je JEFTIN prolaz bez ijednog ECS
        // upita (samo indeksi iz liste), pa se skupo računa tek kad se
        // potpis promeni. Entiteti umeju da nestanu i bez promene selekcije
        // (igra ih obriše), zato i osvežavanje na svakih 30 frejmova.
        private const int kDerivedRefreshFrames = 30;

        private int m_DerivedSignature;
        private int m_DerivedFrame = int.MinValue;
        private int m_CachedPropTargetCount;
        private int m_CachedHeightTargetCount;
        private string m_CachedSelectionList = string.Empty;

        private int SelectionSignature()
        {
            // Kombinovanje je nezavisno od redosleda (zbir + xor) — liste se
            // pune iz raznih putanja, a potpis ne sme da "zvoni" bez promene.
            int sum = m_Selected.Count + (m_SelectedSurfaces.Count << 4) + (m_SelectedLanes.Count << 8) +
                (m_SelectedNodes.Count << 12) + (m_SelectedNetEdges.Count << 16) + ((int)m_Mode << 24);
            int xor = m_ListFocusEntity.Index;
            AccumulateSignature(m_Selected, ref sum, ref xor);
            AccumulateSignature(m_SelectedSurfaces, ref sum, ref xor);
            AccumulateSignature(m_SelectedLanes, ref sum, ref xor);
            AccumulateSignature(m_SelectedNodes, ref sum, ref xor);
            AccumulateSignature(m_SelectedNetEdges, ref sum, ref xor);
            return sum ^ (xor * 397);
        }

        private static void AccumulateSignature(List<Entity> entities, ref int sum, ref int xor)
        {
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                sum += entity.Index;
                xor ^= entity.Index * 31;
            }
        }

        private void EnsureDerivedSelectionData()
        {
            int frame = UnityEngine.Time.frameCount;
            int signature = SelectionSignature();
            if (signature == m_DerivedSignature && frame - m_DerivedFrame < kDerivedRefreshFrames)
            {
                return;
            }

            m_DerivedSignature = signature;
            m_DerivedFrame = frame;
            m_CachedPropTargetCount = ComputePropTargetCount();
            m_CachedHeightTargetCount = ComputeHeightTargetCount();
            m_CachedSelectionList = ComputeSelectionList();
        }

        public int ClipboardCount => m_Clipboard.Count + m_ClipboardAreas.Count + m_ClipboardLanes.Count + m_ClipboardNetEdges.Count;

        public bool IsPasteMode => m_Mode == Mode.Paste;

        private readonly List<Entity> m_Selected = new List<Entity>();
        private readonly List<ClipboardItem> m_Clipboard = new List<ClipboardItem>();

        private ToolOutputBarrier m_ToolOutputBarrier;
        private TerrainSystem m_TerrainSystem;
        private AudioManager m_AudioManager;
        private OverlayRenderSystem m_OverlayRenderSystem;
        private EntityQuery m_SoundQuery;

        private ProxyAction m_ToggleAction;
        private ProxyAction m_CopyAction;
        private ProxyAction m_PasteAction;
        private ProxyAction m_DeleteAction;
        private ProxyAction m_RaiseAction;
        private ProxyAction m_LowerAction;
        private ProxyAction m_UndoAction;
        private ProxyAction m_RedoAction;
        private ProxyAction m_RelocateAction;
        private ProxyAction m_SelectSameAction;
        private ProxyAction m_SnapGroundAction;
        private ProxyAction m_MatchHeightAction;
        private bool m_HeightPickArmed;
        private bool m_AlignPickArmed;
        private float m_AlignPickGap = -1f;
        private float m_LastAltSpinTime;

        // Uz vreme se pamti i NAD ČIM se okretalo: nalet mora da se prekine kad
        // se promeni selekcija, inače drugi objekat u roku od sekunde uđe u
        // TUĐI undo zapis i njegovo okretanje ostane nepovratno. Pečat je XOR
        // svih članova — sidro+broj je propuštao zamenu ne-prvog člana.
        private int m_LastAltSpinStamp;

        private int AltSpinSelectionStamp()
        {
            int stamp = 17;
            foreach (Entity entity in m_Selected)
            {
                stamp = (stamp * 31) ^ entity.Index;
            }

            return stamp;
        }
        private bool m_LeftPressAlt;

        // Ctrl+klik ciklus: biranje propova "zakopanih" u druge objekte.
        private float3 m_CyclePoint = new float3(float.MaxValue);
        private int m_CycleIndex;

        // Preostali frejmovi doterivanja boja ghost preview-a posle promene definicija.
        private int m_PreviewLookFrames;
        private ProxyAction m_NudgeUpAction;
        private ProxyAction m_NudgeDownAction;
        private ProxyAction m_NudgeLeftAction;
        private ProxyAction m_NudgeRightAction;
        private ProxyAction m_AlignGapPlusAction;
        private ProxyAction m_AlignGapMinusAction;
        private float m_PasteHeightBoost;

        private enum UndoKind
        {
            Transforms,
            Delete,
            Paste,
        }

        private struct TransformSnapshot
        {
            public Entity m_Entity;
            public Entity m_Prefab;

            // Vlasnik (zgrada) za propove koji pripadaju zgradi — undo brisanja
            // vraća prop nazad zgradi, ne kao samostalan (inače bi posle undo-a
            // ispadao iz "Building props" pravila).
            public Entity m_Owner;
            public Game.Objects.Transform m_Transform;
            public float m_Elevation;
            public bool m_HadElevation;
            public bool m_HadTree;
            public Game.Objects.Tree m_Tree;
            public bool m_HasSeed;
            public ushort m_Seed;
            public bool m_HasCustomColor;
            public Game.Rendering.ColorSet m_CustomColor;

            // Zgrade: potpisi površina u trenutku snimanja — rekreacija posle
            // construction-a briše višak, pa vraćena/redo zgrada nasleđuje
            // igračeva brisanja placa umesto fabričkog kompleta.
            public List<SurfaceSig> m_SurfaceSigs;
        }

        private class UndoRecord
        {
            public UndoKind m_Kind;
            public List<TransformSnapshot> m_Snapshots;
            public List<PastedRecord> m_Pasted;

            // Farbane površine (poligoni) — samo za Transforms zapise.
            public List<SurfaceSnapshot> m_Surfaces;

            // Samostalne ograde (krive) — paralelna lista, kao površine.
            public List<LaneSnapshot> m_Lanes;

            // Mreže (samo Transforms zapisi): čvorovi + krive svih njihovih ivica.
            public List<NetNodeSnapshot> m_NetNodes;
            public List<NetEdgeSnapshot> m_NetEdges;

            // Paste zapisi: entiteti koji su POSTOJALI pri stampu i liče na
            // nalepljene — undo ih nikad ne sme obrisati (twin zaštita traje
            // i posle isteka rezolucionog prozora).
            public HashSet<Entity> m_PastedExclude;

            // Krive puteva koji su POSTOJALI pri stampu. ID-jevi iznad ne
            // prezive kad igra podeli zatecen put na nalepljenim cvorovima
            // (parcad su NOVI entiteti), pa se cuva i geometrija.
            public List<PreStampNetCurve> m_PastedPreCurves;
        }

        private const int kMaxUndo = 32;
        private readonly List<UndoRecord> m_UndoStack = new List<UndoRecord>();

        // Redo: transformacije, brisanja i paste — svaka NOVA akcija briše
        // redo stek. Delete/Paste zapisi nose pune snimke za rekreaciju.
        private readonly List<UndoRecord> m_RedoStack = new List<UndoRecord>();

        private Mode m_Mode = Mode.Select;
        private Entity m_HoverEntity = Entity.Null;
        private Entity m_LastRegenClickLogged = Entity.Null;
        private float3 m_LastAnchor;
        private bool m_PasteDirty;

        private bool m_RightHeld;
        private bool m_RightDragging;
        private float m_RightDragAccumulator;
        private float3 m_RotationCenter;

        // ALT snap rotacije: 45° koraci (PI/4).
        private const float kRotateSnap = 0.785398163f;
        private float m_RotateAccum;
        private float m_RotateApplied;

        private bool m_MarqueeHeld;
        private bool m_MarqueeActive;
        private float3 m_MarqueeStart;
        private float3 m_MarqueeEnd;
        private float3 m_MarqueeLastScan;
        private float2 m_MarqueeRight;
        private float2 m_MarqueeForward;
        private readonly List<Entity> m_MarqueeHits = new List<Entity>();
        private EntityQuery m_PropQuery;
        private EntityQuery m_OwnedPropQuery;
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_SurfaceQuery;
        private EntityQuery m_OwnedSurfaceQuery;
        private EntityQuery m_TempPreviewQuery;

        // Selection filteri: šta selekcija sme da uhvati (panel "Selection" čipovi).
        private static bool SelectProps => Mod.Settings == null || Mod.Settings.SelectProps;

        private static bool SelectTrees => Mod.Settings == null || Mod.Settings.SelectTrees;

        private static bool SelectDecals => Mod.Settings == null || Mod.Settings.SelectDecals;

        private static bool SelectSurfaces => Mod.Settings == null || Mod.Settings.SelectSurfaces;

        private static bool SelectBuildings => Mod.Settings != null && Mod.Settings.SelectBuildings;

        // Marquee i za propove koji pripadaju zgradama (klik uvek radi).
        private static bool SelectBuildingProps => Mod.Settings != null && Mod.Settings.SelectBuildingProps;

        // "Regenerišući" fabrički pod-element zgrade: igra ga ponovo stvara na
        // svaki update, pa se selekcija i brisanje ne održavaju (pouzdana
        // odbrana traži upis u save — svesno odbijeno). To su: elementi koji
        // pripadaju pod-mreži/pod-površini (dekali na prilazu, fleke na placu)
        // i elementi bez fizičke površine koji nisu vegetacija (Clothesline i
        // slične žice/dekali). Kante, klupe i drveće u dvorištu NISU ovaj tip.
        private bool IsRegeneratingSubElement(Entity entity)
        {
            // Jedini pouzdan kriterijum: vlasnik NIJE zgrada/nadogradnja
            // direktno, ali lanac vodi do zgrade — element prilaza ili placa
            // (dekali, fleke). Njih igra agresivno regeneriše na svaki update
            // površine/mreže. Direktni pod-propovi (kante, klupe, drveće,
            // pa i Clothesline) ostaju selektabilni — njihovu regeneraciju
            // registar + prune drže pod kontrolom kroz naše operacije.
            if (!EntityManager.TryGetComponent(entity, out Owner owner))
            {
                return false;
            }

            if (IsBuilding(owner.m_Owner) ||
                EntityManager.HasComponent<Game.Buildings.Extension>(owner.m_Owner))
            {
                return false;
            }

            return GetOwnerRootBuilding(entity) != Entity.Null;
        }

        // Prop pripada zgradi — direktno ILI kroz lanac vlasnika (dekal na
        // prilazu pripada pod-mreži, koja pripada zgradi; fleka na placu
        // pripada pod-površini). Do 4 skoka uz lanac.
        private bool IsOwnedByBuilding(Entity entity)
        {
            Entity current = entity;
            for (int hop = 0; hop < 4; hop++)
            {
                if (!EntityManager.TryGetComponent(current, out Owner owner))
                {
                    return false;
                }

                if (IsBuilding(owner.m_Owner))
                {
                    return true;
                }

                current = owner.m_Owner;
            }

            return false;
        }

        // Kategorija po runtime komponentama: vegetacija ima Tree/Plant, dekal
        // je objekat BEZ Game.Objects.Surface (kolizione površine kao
        // pravilo), sve ostalo je običan prop.
        private bool IsCategoryEnabled(Entity entity)
        {
            if (EntityManager.HasComponent<Game.Buildings.Building>(entity))
            {
                return SelectBuildings;
            }

            if (EntityManager.HasComponent<Game.Objects.Tree>(entity) ||
                EntityManager.HasComponent<Game.Objects.Plant>(entity))
            {
                return SelectTrees;
            }

            if (!EntityManager.HasComponent<Game.Objects.Surface>(entity))
            {
                return SelectDecals;
            }

            return SelectProps;
        }

        private bool IsBuilding(Entity entity) => EntityManager.HasComponent<Game.Buildings.Building>(entity);

        private struct PastedRecord
        {
            public Entity m_Prefab;
            public float3 m_Position;
            public bool m_HadTree;
            public Game.Objects.Tree m_Tree;
            public bool m_HasSeed;
            public ushort m_Seed;
            public bool m_HasCustomColor;
            public Game.Rendering.ColorSet m_CustomColor;

            // Ovaj zapis je farbana površina (poligon), ne objekat.
            public bool m_IsArea;

            // Ovaj zapis je samostalna ograda (container ivica) — pozicija
            // je sredina krive.
            public bool m_IsLane;

            // Ovaj zapis je SEGMENT PUTA — pozicija je sredina krive, a uz
            // njega idu i nadogradnje (drvoredi, ivičnjaci...).
            public bool m_IsNetEdge;
            public bool m_HasUpgrade;
            public CompositionFlags m_Upgrade;

            // Indeksi u tabelu čvorova klipborda — preko njih se nalepljenom
            // čvoru vrate nadogradnje (kružni tok, semafori, stop znakovi).
            public int m_StartNodeIndex;
            public int m_EndNodeIndex;

            // Generacija klipborda u trenutku stampa: tabele čvorova se
            // rebuild-uju svakim Copy/učitavanjem, pa stari indeksi ne smeju
            // da čitaju NOVU tabelu (tuđi markeri/nadogradnje).
            public int m_ClipboardGeneration;

            // Cela kriva i svetske tačke čvorova sa STAMPA. Igra ume da
            // PODELI nalepljenu deonicu (portali tunela, potporni zidovi) —
            // parčići imaju druge sredine pa ih midpoint match ne vidi: undo
            // ih briše po "sredina leži na ovoj krivoj", a redo iz ovih
            // podataka rekreira i deonice koje rezolucija nije prepoznala.
            public Bezier4x3 m_NetCurve;
            public float3 m_StartNodeWorld;
            public float3 m_EndNodeWorld;

            // Zgrade: potpisi površina izvora (vidi ClipboardItem.m_SurfaceSigs).
            public List<SurfaceSig> m_SurfaceSigs;

            // Stvarni entitet koji je paste stvorio — razrešava se u RunPostPasteFix,
            // da undo briše baš njega, a ne bilo koji istovetan prop na istom mestu.
            public Entity m_Resolved;
        }

        private struct MoveItem
        {
            public Entity m_Entity;
            public float3 m_Offset;
            public float m_HeightOffset;
        }

        private bool m_LeftHeldOnProp;
        private bool m_MoveDragging;
        private bool m_MoveOffsetsPending;
        private bool m_LeftPressShift;
        private Entity m_LeftPressEntity;
        private float3 m_MoveStart;
        private readonly List<MoveItem> m_MoveItems = new List<MoveItem>();

        private readonly List<PastedRecord> m_LastPreview = new List<PastedRecord>();
        private List<PastedRecord> m_PostPasteFix;
        private int m_PostPasteFixFrames;

        // Entiteti koji su POSTOJALI u trenutku stampa a liče na nalepljene
        // (isti prefab + pozicija) — rezolucija ih preskače da undo ne bi
        // obrisao tuđu identičnu zgradu (dupli stamp uz road snap je lak).
        private HashSet<Entity> m_PostPasteExclude;
        private List<PreStampNetCurve> m_PostPasteNetPreCurves;
        private ComponentType m_PreventOverrideType;
        private bool m_HasPreventOverride;

        public override string toolID => "Copaste Tool";

        public override PrefabBase GetPrefab()
        {
            return null;
        }

        public override bool TrySetPrefab(PrefabBase prefab)
        {
            return false;
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();

            if (m_Mode == Mode.Paste || m_Mode == Mode.Relocate || m_MoveDragging || m_MarqueeHeld || m_HandleDragging)
            {
                // Net je uključen da bi se grupa mogla nalepiti na površinu puta/staze, ne samo na teren.
                // Isti mask važi tokom pomeranja i marquee razvlačenja — da raycast ne pogađa
                // propove koje vučemo, niti "iskače" na zgrade preko kojih kursor prelazi.
                // IZUZETAK: dok se vuku MREŽE ili ručka krive, ray ne sme da pogodi
                // baš put koji pomeramo (most bi pravio paralaksni trzaj) — samo teren.
                bool draggingNetworks = (m_MoveDragging && (m_SelectedNodes.Count > 0 || m_SelectedNetEdges.Count > 0)) || m_HandleDragging;
                m_ToolRaycastSystem.typeMask = draggingNetworks ? TypeMask.Terrain : TypeMask.Terrain | TypeMask.Net;
                m_ToolRaycastSystem.netLayerMask = Game.Net.Layer.Road | Game.Net.Layer.Pathway | Game.Net.Layer.PublicTransportRoad;
                m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
            }
            else
            {
                // Raycast je NAJSKUPLJI deo alata — izmereno 167 us od 194 us
                // po frejmu — a cena raste sa brojem slojeva koje tražimo.
                // Zato se traži samo ono što trenutni filteri stvarno mogu da
                // izaberu: bez objekatskih filtera dovoljan je teren (mreže se
                // biraju preko prostornog stabla, ne odavde), dekali i
                // pod-elementi zgrada samo kad su te opcije upaljene.
                bool needObjects = SelectProps || SelectTrees || SelectDecals || SelectBuildings;

                // MREŽE MORAJU U MASKU kad se biraju: bez njih zrak prođe kroz
                // most i pogodi teren daleko iza, pa se sve što se bira "pod
                // kursorom" bira na pogrešnom mestu — čvorovi na mostu se ne
                // mogu uhvatiti, umesto čvora se hvata cela deonica, a
                // Shift+klik promaši narednu deonicu.
                bool needNets = SelectNetworks || SelectFences;

                // Teren je uključen da bi marquee imao početnu tačku na praznom tlu.
                m_ToolRaycastSystem.typeMask = TypeMask.Terrain
                    | (needObjects ? TypeMask.StaticObjects : default)
                    | (needNets ? TypeMask.Net : default);
                if (needNets)
                {
                    m_ToolRaycastSystem.netLayerMask = Game.Net.Layer.Road | Game.Net.Layer.Pathway |
                        Game.Net.Layer.PublicTransportRoad | Game.Net.Layer.TrainTrack |
                        Game.Net.Layer.TramTrack | Game.Net.Layer.SubwayTrack;
                }

                m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;

                if (SelectDecals)
                {
                    m_ToolRaycastSystem.raycastFlags |= RaycastFlags.Decals;
                }

                // SubElements spušta ray u POD-OBJEKTE. Treba svaki put kad
                // se objekti uopšte biraju: pod-objekti nisu samo zgradini —
                // propovi u vlasništvu puteva i čvorova su selektabilni i kad
                // je "Building elements" ugašen (vidi IsCopyable), pa je
                // vezivanje ove zastavice za tu opciju činilo ulični mobilijar
                // nedohvatljivim.
                if (needObjects)
                {
                    m_ToolRaycastSystem.raycastFlags |= RaycastFlags.SubElements;
                }
            }
        }

        protected override void OnCreate()
        {
            Enabled = false;
            base.OnCreate();

            m_ToolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_AudioManager = World.GetOrCreateSystemManaged<AudioManager>();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_SoundQuery = GetEntityQuery(ComponentType.ReadOnly<ToolUXSoundSettingsData>());

            m_PropQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Objects.Object>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<Game.Buildings.Extension>(),
                    ComponentType.ReadOnly<Game.Objects.Moving>(),
                    ComponentType.ReadOnly<Game.Vehicles.Vehicle>(),

                    // Živa bića (cims, životinje): Moving ih hvata samo DOK
                    // hodaju — cim koji stoji nema Moving pa bi prošao.
                    ComponentType.ReadOnly<Game.Creatures.Creature>(),
                    ComponentType.ReadOnly<Owner>(),

                    // Nevidljivi funkcionalni objekti — nikad nisu meta selekcije.
                    // SpawnLocation NIJE ovde: nose je i klupe/stolice (sedanje) —
                    // nevidljive spawn tačke filtrira IsInvisibleSpawnPoint u skenu.
                    ComponentType.ReadOnly<Game.Objects.Marker>(),
                    ComponentType.ReadOnly<Game.Objects.UtilityObject>(),
                    ComponentType.ReadOnly<Game.Objects.Placeholder>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            // Zgrade (v1.1 "Buildings"): odvojen upit — koristi se samo kad je
            // Buildings filter uključen, da marquee/postpaste ne skeniraju
            // zgrade bez potrebe. Extension i Owner isključeni (delovi celina).
            m_BuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Extension>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            // Ručno farbane površine (bez Owner-a — samostalne).
            m_SurfaceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Areas.Surface>(),
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            // Površine koje pripadaju zgradama (trava/dekoracija placa) — u
            // selekciju ulaze samo iza "Building elements" toggle-a + Surfaces
            // čipa, i samo kad lanac vlasnika vodi do zgrade (ne do puta).
            // Brisanje dekorativne pod-površine uklanja i njen spawner
            // dekoracija (Clothesline i sl.) — vanilla Deleted, ništa u save-u.
            m_OwnedSurfaceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Areas.Surface>(),
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Owner>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            // Propovi koji pripadaju zgradama (imaju Owner) — marquee ih skenira
            // samo kad je "Building props" toggle uključen; klik ih uvek vidi.
            m_OwnedPropQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Objects.Object>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Owner>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<Game.Buildings.Extension>(),
                    ComponentType.ReadOnly<Game.Objects.Moving>(),
                    ComponentType.ReadOnly<Game.Vehicles.Vehicle>(),
                    ComponentType.ReadOnly<Game.Creatures.Creature>(),

                    // Nevidljivi funkcionalni objekti zgrade: pristupni markeri
                    // (Pedestrian Access Location) i komunalni priključci
                    // (cable/pipe node) — brisanje pravi "No access" upozorenja.
                    // SpawnLocation NIJE ovde (klupe/stolice je nose) — spawn
                    // tačke bez mesha filtrira IsInvisibleSpawnPoint u skenu.
                    ComponentType.ReadOnly<Game.Objects.Marker>(),
                    ComponentType.ReadOnly<Game.Objects.UtilityObject>(),
                    ComponentType.ReadOnly<Game.Objects.Placeholder>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            OnCreateFences();

            // Preview (Temp) entiteti našeg paste-a — za doterivanje boja ghost-a.
            m_TempPreviewQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Game.Objects.Object>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            m_ToggleAction = Mod.Settings.GetAction(CopasteSettings.kToggleAction);
            m_CopyAction = Mod.Settings.GetAction(CopasteSettings.kCopyAction);
            m_PasteAction = Mod.Settings.GetAction(CopasteSettings.kPasteAction);
            m_DeleteAction = Mod.Settings.GetAction(CopasteSettings.kDeleteAction);
            m_RaiseAction = Mod.Settings.GetAction(CopasteSettings.kRaiseAction);
            m_LowerAction = Mod.Settings.GetAction(CopasteSettings.kLowerAction);
            m_UndoAction = Mod.Settings.GetAction(CopasteSettings.kUndoAction);
            m_RedoAction = Mod.Settings.GetAction(CopasteSettings.kRedoAction);
            m_RelocateAction = Mod.Settings.GetAction(CopasteSettings.kRelocateAction);
            m_SelectSameAction = Mod.Settings.GetAction(CopasteSettings.kSelectSameAction);
            m_SnapGroundAction = Mod.Settings.GetAction(CopasteSettings.kSnapGroundAction);
            m_MatchHeightAction = Mod.Settings.GetAction(CopasteSettings.kMatchHeightAction);
            m_NudgeUpAction = Mod.Settings.GetAction(CopasteSettings.kNudgeUpAction);
            m_NudgeDownAction = Mod.Settings.GetAction(CopasteSettings.kNudgeDownAction);
            m_NudgeLeftAction = Mod.Settings.GetAction(CopasteSettings.kNudgeLeftAction);
            m_NudgeRightAction = Mod.Settings.GetAction(CopasteSettings.kNudgeRightAction);
            m_AlignGapPlusAction = Mod.Settings.GetAction(CopasteSettings.kAlignGapPlusAction);
            m_AlignGapMinusAction = Mod.Settings.GetAction(CopasteSettings.kAlignGapMinusAction);

            m_ToggleAction.shouldBeEnabled = true;
            m_ToggleAction.onInteraction += OnToggleInteraction;

            Mod.Log.Info("CopasteToolSystem created");
        }

        protected override void OnDestroy()
        {
            if (m_ToggleAction != null)
            {
                m_ToggleAction.onInteraction -= OnToggleInteraction;
            }

            base.OnDestroy();
        }

        private bool m_PreventOverrideScanned;

        // Sistem zivi koliko i proces, pa se pri ucitavanju DRUGOG grada
        // zatekne sa istorijom iz prethodnog. Entitetski ID-jevi tamo znace
        // nesto sasvim drugo, a undo zapisi pamte i POZICIJE — pozicioni
        // fallback bi u novom gradu brisao ZATECENE objekte koji se slucajno
        // poklope po tipu i mestu, a undo brisanja bi materijalizovao objekat
        // iz starog grada. Zato se cela istorija i sve nedovrseno odbacuje.
        //
        // Klipbord se NE dira: on drzi prefabe i relativne ofsete, a prefabi
        // prezive ucitavanje — kopiranje u jednom pa lepljenje u drugom gradu
        // je korisno i bezbedno.
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, Game.GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            DiscardWorldBoundState();
        }

        private void DiscardWorldBoundState()
        {
            m_UndoStack.Clear();
            m_RedoStack.Clear();
            m_PostPasteFix = null;
            m_PostPasteExclude = null;
            m_PostPasteNetPreCurves = null;
            m_PostPasteFixFrames = 0;
            m_SubPropFixFrames = 0;
            m_KeepDefinitionFrames = 0;
            m_KeepAliveFrame = false;
            m_PendingUndoSteps = 0;
            m_PendingRedoSteps = 0;
            m_LastPreview.Clear();

            m_PendingNetRemaps.Clear();
            m_PendingNetRemapFrames = 0;
            m_PendingMarkerAttaches.Clear();
            m_PendingMarkerFrames = 0;
            m_DelayedNetSettle.Clear();
            m_PendingSurfacePrune.Clear();

            ClearSelection();
            m_HoverEntity = Entity.Null;
            m_StickyHandleIndex = -1;
            m_StickyHandleEntity = Entity.Null;
            m_HandleDragging = false;
            m_HandleEntity = Entity.Null;
        }

        // Kuka za razvojne provere: parcijalna metoda bez tela u ovom
        // buildu, pa je prevodilac uklanja zajedno sa pozivom.
        partial void SelfTestTick();

        // Broji paljenja alata. Odloženi poslovi koji smeju da prežive
        // gašenje po njemu vide koliko su ostarili.
        private int m_ToolSessionId;

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_ToolSessionId++;

            // Sken za Anarchy tek sada — pri OnCreate drugi modovi još nisu učitani.
            if (!m_PreventOverrideScanned)
            {
                m_PreventOverrideScanned = true;
                FindAnarchyPreventOverride();
            }
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
            m_CopyAction.shouldBeEnabled = true;
            m_PasteAction.shouldBeEnabled = true;
            m_DeleteAction.shouldBeEnabled = true;
            m_RaiseAction.shouldBeEnabled = true;
            m_LowerAction.shouldBeEnabled = true;
            m_UndoAction.shouldBeEnabled = true;
            m_RedoAction.shouldBeEnabled = true;
            m_RelocateAction.shouldBeEnabled = true;
            m_SelectSameAction.shouldBeEnabled = true;
            m_SnapGroundAction.shouldBeEnabled = true;
            m_MatchHeightAction.shouldBeEnabled = true;
            m_HeightPickArmed = false;
            m_AlignPickArmed = false;
            m_NudgeUpAction.shouldBeEnabled = true;
            m_NudgeDownAction.shouldBeEnabled = true;
            m_NudgeLeftAction.shouldBeEnabled = true;
            m_NudgeRightAction.shouldBeEnabled = true;
            m_AlignGapPlusAction.shouldBeEnabled = true;
            m_AlignGapMinusAction.shouldBeEnabled = true;
            m_Mode = Mode.Select;
            m_HoverEntity = Entity.Null;
            m_PasteDirty = false;
            m_RightHeld = false;
            m_RightDragging = false;
            applyMode = ApplyMode.Clear;
        }

        protected override void OnStopRunning()
        {
            applyAction.shouldBeEnabled = false;
            cancelAction.shouldBeEnabled = false;
            m_CopyAction.shouldBeEnabled = false;
            m_PasteAction.shouldBeEnabled = false;
            m_DeleteAction.shouldBeEnabled = false;
            m_RaiseAction.shouldBeEnabled = false;
            m_LowerAction.shouldBeEnabled = false;
            m_UndoAction.shouldBeEnabled = false;
            m_RedoAction.shouldBeEnabled = false;
            m_RelocateAction.shouldBeEnabled = false;
            m_SelectSameAction.shouldBeEnabled = false;
            m_SnapGroundAction.shouldBeEnabled = false;
            m_MatchHeightAction.shouldBeEnabled = false;
            m_NudgeUpAction.shouldBeEnabled = false;
            m_NudgeDownAction.shouldBeEnabled = false;
            m_NudgeLeftAction.shouldBeEnabled = false;
            m_NudgeRightAction.shouldBeEnabled = false;
            m_AlignGapPlusAction.shouldBeEnabled = false;
            m_AlignGapMinusAction.shouldBeEnabled = false;

            // Prekid usred poteza ne sme da ostavi zgradu "u vazduhu":
            // relocate se otkazuje (zgrada nazad), aktivan drag se settle-uje,
            // viseći fix prozori se prazne.
            CleanupActiveGesture();

            // Ukloni highlight samo sa naših entiteta — ne diramo tuđe.
            foreach (Entity entity in m_Selected)
            {
                Unhighlight(entity);
            }

            if (m_HoverEntity != Entity.Null)
            {
                Unhighlight(m_HoverEntity);
            }

            foreach (Entity entity in m_MarqueeHits)
            {
                Unhighlight(entity);
            }

            m_MarqueeHits.Clear();
            m_MarqueeHeld = false;
            m_MarqueeActive = false;
            m_LeftHeldOnProp = false;
            m_MoveDragging = false;
            m_MoveOffsetsPending = false;
            m_MoveItems.Clear();
            m_MoveSurfaceItems.Clear();
            m_MoveLaneItems.Clear();
            m_Selected.Clear();
            m_SelectedSurfaces.Clear();
            m_SelectedLanes.Clear();
            m_SelectedNodes.Clear();
            m_SelectedNetEdges.Clear();
            m_NetMoveActive = false;
            m_DelayedNetSettle.Clear();

            // ID-jevi ne prežive u sledeću učitanu igru — nedovršena
            // prevezivanja se odbacuju sa alatom.
            m_PendingNetRemaps.Clear();
            m_PendingNetRemapFrames = 0;
            m_PendingMarkerAttaches.Clear();
            m_PendingMarkerFrames = 0;
            m_HandleDragging = false;
            m_HandleIndex = -1;
            m_HandleEntity = Entity.Null;
            m_StickyHandleIndex = -1;
            m_StickyHandleEntity = Entity.Null;
            m_StraightenArmed = false;
            m_HoverEntity = Entity.Null;
            if (m_Mode == Mode.Paste)
            {
                m_ToolSystem.ignoreErrors = m_PreviousIgnoreErrors;
            }

            m_Mode = Mode.Select;
            m_RelocateEntity = Entity.Null;
            m_SameFilterPrefab = Entity.Null;
            SetSameFilterName();
            m_HeightPickArmed = false;
            m_AlignPickArmed = false;
            m_UiTyping = false;
            base.OnStopRunning();
        }

        // Dok korisnik kuca u UI polju (rename), sav input alata je uspavan —
        // inače slova okidaju prečice (T = filter, Delete = brisanje, ESC = izlaz...).
        private bool m_UiTyping;

        public void SetUiTyping(bool typing)
        {
            m_UiTyping = typing;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            // Izuzetak u tool sistemu bi srušio celu igru (SceneFlow CRITICAL) — zato sve pod try.
            try
            {
                applyMode = ApplyMode.Clear;

                // Definicije emitovane VAN paste moda (redo rekreacija puteva,
                // markeri kružnih tokova) žive frejm-dva dok ih igrini Generate
                // sistemi ne obrade — applyMode=Clear bi ih pobio pre toga i
                // rekreirani putevi ne bi ni nastali.
                // Flag za CEO frejm: paste freeze (UpdatePasteMode) mora
                // da pokrije SVAKI applyMode=None frejm — čitanje brojača
                // POSLE dekrementa je poslednji frejm ostavljalo nezamrznut,
                // pa je klik u njemu štampao nepraćeni duplikat.
                m_KeepAliveFrame = m_KeepDefinitionFrames > 0;
                if (m_KeepAliveFrame)
                {
                    m_KeepDefinitionFrames--;
                    applyMode = ApplyMode.None;
                }

                // Podzemni prikaz prati prekidač (igra renderuje tunele i
                // zatamni površinu — isti mehanizam kao buldožer).
                requireUnderground = m_Mode != Mode.Relocate && UndergroundMode;

                RunPendingHistorySteps();
                RunPostPasteFix();
                RunSubPropFix();
                RunDelayedSettles();
                RunDelayedNetSettles();
                RunPendingNetRemaps();
                RunPendingMarkerAttaches();
                RunPendingSurfacePrunes();

                // Razvojni self-test (prazan u produkciji — partial bez tela).
                SelfTestTick();

                if (m_UiTyping)
                {
                    // Ovaj frejm je počeo sa ApplyMode.Clear, pa je ghost
                    // obrisan. Bez ovoga bi sledeći frejm sa istim sidrom
                    // rekao "ništa se nije promenilo" i definicije se nikad ne
                    // bi ponovo emitovale — a m_LastPreview ostaje pun, pa bi
                    // klik odštampao nulu i gurnuo prazan undo korak.
                    m_PasteDirty = true;
                    return inputDeps;
                }

                if (m_Mode == Mode.Select)
                {
                    UpdateSelectMode();
                }
                else if (m_Mode == Mode.Relocate)
                {
                    UpdateRelocateMode();
                }
                else
                {
                    UpdatePasteMode();
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Error($"Copaste OnUpdate failed, resetting tool state: {e}");
                ResetToolState();
            }

            return inputDeps;
        }

        // Deljeni prekid poteza za OnStopRunning i crash-reset putanju.
        private void CleanupActiveGesture()
        {
            try
            {
                if (m_Mode == Mode.Relocate && m_RelocateEntity != Entity.Null && EntityManager.Exists(m_RelocateEntity))
                {
                    CancelRelocate();
                }
                else if (m_Mode == Mode.Relocate)
                {
                    // Zgrada je nestala usred premeštanja a alat se gasi:
                    // knjigovodstvo se ipak rasplete — fantomski zapis dole,
                    // sklonjeni redo stek nazad.
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
                }
                else if (m_MoveDragging)
                {
                    foreach (MoveItem item in m_MoveItems)
                    {
                        if (IsBuilding(item.m_Entity))
                        {
                            SettleBuilding(item.m_Entity);
                        }
                    }

                    SettleSurfaces();
                    SettleLanes();
                    SettleNetworks();
                    m_NetMoveActive = false;
                }

                // I ove tri su unutar try/catch: iz njih je izuzetak bežao
                // kroz ResetToolState i dalje u igru — tačno onaj pad zbog kog
                // ceo blok postoji.
                FlushBuildingFixups();

                // Odloženi net settle nema ko da otkuca kad alat stane — okini odmah.
                FlushNetSettles();

                // Prekinuto povlačenje ručke: uredan kraj (undo zapis već postoji).
                EndHandleDrag();
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Copaste: gesture cleanup failed: {e.Message}");
            }

            // Prozor rezolucije se ZATVARA kad alat stane. Frejmovi otkucavaju
            // samo dok alat radi, pa bi inače nastavio tamo gde je stao — a u
            // međuvremenu korisnik igrinim alatom napravi put koji zapisu
            // odgovara po prefabu i mestu, i rezolucija bi usvojila tuđe delo
            // (undo bi ga posle obrisao). Sama lista NE nestaje: nju drži undo
            // zapis, pa se paste i dalje uredno poništava pozicionim matchom.
            m_PostPasteFix = null;
            m_PostPasteExclude = null;
            m_PostPasteNetPreCurves = null;
            m_PostPasteFixFrames = 0;
        }

        private void ResetToolState()
        {
            EndAlignSession();
            CleanupActiveGesture();
            if (m_Mode == Mode.Paste && m_ToolSystem != null)
            {
                m_ToolSystem.ignoreErrors = m_PreviousIgnoreErrors;
            }

            // Highlight se skida i na ovoj (crash) putanji — inače ostaje zauvek.
            foreach (Entity entity in m_MarqueeHits)
            {
                if (!m_Selected.Contains(entity))
                {
                    Unhighlight(entity);
                }
            }

            if (m_HoverEntity != Entity.Null && !m_Selected.Contains(m_HoverEntity))
            {
                Unhighlight(m_HoverEntity);
            }

            m_Mode = Mode.Select;
            m_PasteDirty = false;
            m_PasteHeightBoost = 0f;
            m_MarqueeHeld = false;
            m_MarqueeActive = false;
            m_MarqueeHits.Clear();
            m_LeftHeldOnProp = false;
            m_MoveDragging = false;
            m_MoveOffsetsPending = false;
            m_MoveItems.Clear();
            m_MoveSurfaceItems.Clear();
            m_MoveLaneItems.Clear();
            m_NetMoveActive = false;
            m_RightHeld = false;
            m_RightDragging = false;
            m_SameFilterPrefab = Entity.Null;
            SetSameFilterName();
            m_HeightPickArmed = false;
            m_AlignPickArmed = false;

            // Alt-tap se mora razoružati: ovo je putanja POSLE izuzetka u
            // OnUpdate, a selekcija čvorova ostaje — sledeće puštanje Alt-a bi
            // odradilo ravnanje lanca baš u trenutku kad se alat smiruje.
            m_StraightenArmed = false;
            m_PostPasteFix = null;
            m_PostPasteFixFrames = 0;
            m_PostPasteExclude = null;
            m_PostPasteNetPreCurves = null;
            m_HoverEntity = Entity.Null;
        }

        // Da li je kursor iznad UI-ja (naš panel, meniji igre) — sirove mišje akcije se tada ignorišu.
        private static bool MouseOverUI => InputManager.instance != null && InputManager.instance.mouseOverUI;

        // Klik čitamo i direktno sa miša: drugi modovi umeju da drže globalne
        // Shift/Ctrl+klik akcije koje preuzmu vanila Apply akciju pa ona ne okine.
        // Ali NE kada je kursor na UI-ju — klik na dugme panela ne sme da bude i klik na mapu.
        private bool ClickedThisFrame()
        {
            return applyAction.WasPressedThisFrame() ||
                (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame && !MouseOverUI);
        }

        // Desni taster: brzi klik = cancel, držanje + prevlačenje = rotacija.
        private void UpdateRightButton(out float rotationDelta, out bool quickClick)
        {
            rotationDelta = 0f;
            quickClick = false;

            if (Mouse.current == null)
            {
                quickClick = cancelAction.WasPressedThisFrame();
                return;
            }

            if (Mouse.current.rightButton.wasPressedThisFrame && !MouseOverUI)
            {
                m_RightHeld = true;
                m_RightDragging = false;
                m_RightDragAccumulator = 0f;
            }

            if (m_RightHeld && Mouse.current.rightButton.isPressed)
            {
                float deltaX = Mouse.current.delta.ReadValue().x;
                m_RightDragAccumulator += math.abs(deltaX);
                if (!m_RightDragging && m_RightDragAccumulator > 4f)
                {
                    m_RightDragging = true;
                    m_RotationCenter = GetSelectionCenter();
                    m_RotateAccum = 0f;
                    m_RotateApplied = 0f;

                    // Samo ako ima šta da se transformiše — selekcija od SAMO
                    // zgradinih površina bi gurala prazan zapis i pojela redo.
                    if (m_Mode == Mode.Select && SelectionHasTransformTargets())
                    {
                        PushTransformUndo();
                    }
                }

                if (m_RightDragging)
                {
                    // ALT: rotacija se lepi na korake od 45°.
                    m_RotateAccum += deltaX * 0.005f;
                    bool altHeld = Keyboard.current != null && Keyboard.current.altKey.isPressed;
                    float target = altHeld ? math.round(m_RotateAccum / kRotateSnap) * kRotateSnap : m_RotateAccum;
                    rotationDelta = target - m_RotateApplied;
                    m_RotateApplied = target;
                }
            }

            if (m_RightHeld && Mouse.current.rightButton.wasReleasedThisFrame)
            {
                m_RightHeld = false;
                if (!m_RightDragging)
                {
                    quickClick = true;
                }
                else if (m_Mode == Mode.Select)
                {
                    // Kraj rotacionog prevlačenja: čist završni update za zgrade i površine.
                    // Samo GOTOVE zgrade — under-construction nisu ni rotirane ni snimljene.
                    foreach (Entity entity in m_Selected)
                    {
                        if (IsBuilding(entity) && IsMovableBuilding(entity))
                        {
                            SettleBuilding(entity);
                        }
                    }

                    SettleSurfaces();
                    SettleLanes();
                    SettleNetworks();
                }

                m_RightDragging = false;
            }
        }

        // Keš poluprečnika gabarita po prefabu — za marquee test preseka.
        private readonly Dictionary<Entity, float> m_PrefabHalfSize = new Dictionary<Entity, float>();

        private float GetPrefabHalfSize(Entity prefabEntity)
        {
            if (m_PrefabHalfSize.TryGetValue(prefabEntity, out float half))
            {
                return half;
            }

            half = 1f;
            if (EntityManager.TryGetComponent(prefabEntity, out ObjectGeometryData geometryData))
            {
                // Pola veće horizontalne dimenzije, ograničeno da džinovski propovi ne "love" izdaleka.
                half = math.clamp(math.max(geometryData.m_Size.x, geometryData.m_Size.z) * 0.5f, 1f, 8f);
            }

            m_PrefabHalfSize[prefabEntity] = half;
            return half;
        }

        // Skupi farbane površine čiji poligon zahvata marquee okvir.
        private void CollectSurfacesInMarquee()
        {
            float2 delta = m_MarqueeEnd.xz - m_MarqueeStart.xz;
            float u = math.dot(delta, m_MarqueeRight);
            float v = math.dot(delta, m_MarqueeForward);
            float uMin = math.min(0f, u);
            float uMax = math.max(0f, u);
            float vMin = math.min(0f, v);
            float vMax = math.max(0f, v);

            ScanSurfacesInMarquee(m_SurfaceQuery, uMin, uMax, vMin, vMax, requireBuildingRoot: false);

            // Building elements: i zgradine površine, ali samo one čiji lanac
            // vlasnika vodi do ZGRADE (putevi imaju svoje površine — ne diraju se).
            if (SelectBuildingProps)
            {
                ScanSurfacesInMarquee(m_OwnedSurfaceQuery, uMin, uMax, vMin, vMax, requireBuildingRoot: true);
            }

            if (m_SelectedSurfaces.Count > 0)
            {
                Mod.Log.Info($"Copaste: {m_SelectedSurfaces.Count} painted surfaces in selection");
            }
        }

        private void ScanSurfacesInMarquee(EntityQuery query, float uMin, float uMax, float vMin, float vMax, bool requireBuildingRoot)
        {
            NativeArray<Entity> areas = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < areas.Length; i++)
            {
                // Isti limit selekcije kao za propove/mreže — marquee preko
                // ogromnog broja površina ne sme da zaobiđe kapu.
                if (SelectedCount >= kMaxSelection)
                {
                    break;
                }

                if (m_SelectedSurfaces.Contains(areas[i]) ||
                    !EntityManager.TryGetBuffer(areas[i], true, out DynamicBuffer<Game.Areas.Node> nodes) ||
                    nodes.Length < 3)
                {
                    continue;
                }

                if (UndergroundMode && !MatchesUndergroundMode(nodes[0].m_Position))
                {
                    continue;
                }

                // T-filter važi i za površine — obećanje filtera je "samo taj
                // tip", pa marquee ne sme da uvuče površine drugog prefaba.
                if (m_SameFilterPrefab != Entity.Null &&
                    (!EntityManager.TryGetComponent(areas[i], out PrefabRef areaPrefab) ||
                        areaPrefab.m_Prefab != m_SameFilterPrefab))
                {
                    continue;
                }

                bool inside = false;
                float2 centroid = float2.zero;
                for (int n = 0; n < nodes.Length; n++)
                {
                    float2 offset = nodes[n].m_Position.xz - m_MarqueeStart.xz;
                    centroid += nodes[n].m_Position.xz;
                    float pu = math.dot(offset, m_MarqueeRight);
                    float pv = math.dot(offset, m_MarqueeForward);
                    if (pu >= uMin && pu <= uMax && pv >= vMin && pv <= vMax)
                    {
                        inside = true;
                        break;
                    }
                }

                if (!inside)
                {
                    // I centroid test — velika površina može da "proguta" ceo okvir
                    // a da nijedna ivična tačka ne upadne u njega.
                    centroid = float2.zero;
                    for (int n = 0; n < nodes.Length; n++)
                    {
                        centroid += nodes[n].m_Position.xz;
                    }

                    centroid /= nodes.Length;
                    float2 centroidOffset = centroid - m_MarqueeStart.xz;
                    float cu = math.dot(centroidOffset, m_MarqueeRight);
                    float cv = math.dot(centroidOffset, m_MarqueeForward);
                    inside = cu >= uMin && cu <= uMax && cv >= vMin && cv <= vMax;
                }

                if (inside)
                {
                    // Owner-lanac provera tek POSLE geometrijskog testa —
                    // hodanje lanca za svaku površinu na mapi po pomaku miša
                    // je ista klasa regresije kao stari marquee problem.
                    if (requireBuildingRoot && GetOwnerRootBuilding(areas[i]) == Entity.Null)
                    {
                        continue;
                    }

                    m_SelectedSurfaces.Add(areas[i]);
                }
            }

            areas.Dispose();
        }

        // Jedan prolaz marquee testa preko zadatog upita (propovi, pa opciono zgrade).
        private void ScanMarqueeQuery(EntityQuery query, float uMin, float uMax, float vMin, float vMax, HashSet<Entity> previous, bool ownedByBuildingOnly = false)
        {
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms = query.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            NativeArray<PrefabRef> prefabRefs = query.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                // Same filter: marquee hvata samo objekte izabranog tipa.
                if (m_SameFilterPrefab != Entity.Null && prefabRefs[i].m_Prefab != m_SameFilterPrefab)
                {
                    continue;
                }

                float2 offset = transforms[i].m_Position.xz - m_MarqueeStart.xz;
                float pu = math.dot(offset, m_MarqueeRight);
                float pv = math.dot(offset, m_MarqueeForward);

                // Presek sa gabaritom, ne samo centrom — objekat čiji je deo u okviru se selektuje.
                float half = GetPrefabHalfSize(prefabRefs[i].m_Prefab);
                if (pu >= uMin - half && pu <= uMax + half && pv >= vMin - half && pv <= vMax + half)
                {
                    // Selection čipovi važe po entitetu — ali provera komponenti
                    // je SKUPA i sme da se radi tek POSLE odbacivanja po okviru:
                    // ovaj sken ide preko svih propova na mapi na svaki pomak
                    // miša, a okvir izbaci 99% kandidata čistom matematikom.
                    if (!IsCategoryEnabled(entities[i]))
                    {
                        continue;
                    }

                    // Nevidljive spawn tačke (bez mesha) — nikad u selekciju.
                    // Posle okvira: provera dira prefab, ne sme za celu mapu.
                    if (IsInvisibleSpawnPoint(entities[i]))
                    {
                        continue;
                    }

                    // Owned sken: samo propovi čiji je vlasnik zgrada, i NIKAD
                    // regenerišući tip (posle okvira — provere su skupe).
                    if (ownedByBuildingOnly &&
                        (!IsOwnedByBuilding(entities[i]) || IsRegeneratingSubElement(entities[i])))
                    {
                        continue;
                    }

                    // Podzemni režim: okvir tada bira samo ono ispod zemlje.
                    // U NORMALNOM režimu se ne filtrira — prop namerno ukopan
                    // ispod terena je uobičajen potez pri detaljisanju i mora
                    // da ostane u okviru (klik ga ionako hvata). Uzorkovanje
                    // terena je skupo, pa ide POSLE odbacivanja po okviru.
                    if (UndergroundMode && !MatchesUndergroundMode(transforms[i].m_Position))
                    {
                        continue;
                    }

                    if (m_MarqueeHits.Count >= kMaxSelection)
                    {
                        break;
                    }

                    m_MarqueeHits.Add(entities[i]);
                    if (!previous.Remove(entities[i]))
                    {
                        Highlight(entities[i]);
                    }
                }
            }

            entities.Dispose();
            transforms.Dispose();
            prefabRefs.Dispose();
        }

        private void UpdateMarqueeHits()
        {
            // Pravougaonik u bazi kamere: u = širina (right), v = dubina (forward).
            float2 delta = m_MarqueeEnd.xz - m_MarqueeStart.xz;
            float u = math.dot(delta, m_MarqueeRight);
            float v = math.dot(delta, m_MarqueeForward);
            float uMin = math.min(0f, u);
            float uMax = math.max(0f, u);
            float vMin = math.min(0f, v);
            float vMax = math.max(0f, v);

            HashSet<Entity> previous = new HashSet<Entity>(m_MarqueeHits);
            m_MarqueeHits.Clear();

            // Prop upit pokriva propove, drveće i dekale — po-entitetsko
            // filtriranje kategorija radi IsCopyable/IsCategoryEnabled.
            if (SelectProps || SelectTrees || SelectDecals)
            {
                ScanMarqueeQuery(m_PropQuery, uMin, uMax, vMin, vMax, previous);

                // Building props toggle: i propovi koji pripadaju zgradama
                // (samo oni čiji je vlasnik stvarno zgrada — ne ulični mobilijar
                // koji pripada putevima).
                if (SelectBuildingProps)
                {
                    ScanMarqueeQuery(m_OwnedPropQuery, uMin, uMax, vMin, vMax, previous, ownedByBuildingOnly: true);
                }
            }

            if (SelectBuildings)
            {
                ScanMarqueeQuery(m_BuildingQuery, uMin, uMax, vMin, vMax, previous);
            }

            foreach (Entity gone in previous)
            {
                if (!m_Selected.Contains(gone))
                {
                    Unhighlight(gone);
                }
            }
        }

        private void CommitMarquee(bool additive)
        {
            EndAlignSession();

            if (!additive)
            {
                ClearSelection();
            }

            // Farbane površine: uđu u selekciju ako im bilo koja tačka poligona
            // (ili centroid) upada u marquee okvir — WYSIWYG, ocrtavaju se i kopiraju.
            if (SelectSurfaces)
            {
                CollectSurfacesInMarquee();
            }

            // Ograde: uđu ako im bilo koji uzorak krive upadne u okvir.
            CollectLanesInMarquee();

            // Mreže: čvorovi u okviru + ivice čija su oba kraja unutra.
            CollectNetworksInMarquee();

            HashSet<Entity> selectedSet = new HashSet<Entity>(m_Selected);
            for (int i = 0; i < m_MarqueeHits.Count; i++)
            {
                Entity entity = m_MarqueeHits[i];

                // Limit važi za CELU selekciju: mreže i ograde su već ušle
                // (svoji kolektori), pa poređenje samo sa propovima je puštalo
                // dvostruko preko kape.
                if (SelectedCount >= kMaxSelection)
                {
                    // Višak preko limita mora da izgubi highlight — inače svetli zauvek.
                    Mod.Log.Info($"Copaste: selection capped at {kMaxSelection}");
                    for (int j = i; j < m_MarqueeHits.Count; j++)
                    {
                        if (!selectedSet.Contains(m_MarqueeHits[j]))
                        {
                            Unhighlight(m_MarqueeHits[j]);
                        }
                    }

                    break;
                }

                if (selectedSet.Add(entity))
                {
                    m_Selected.Add(entity);
                    Highlight(entity);
                }
            }

            m_MarqueeHits.Clear();
        }

        // Kontinuirano dizanje/spuštanje dok je taster pritisnut (~4 m/s).
        private float GetHeightInputDelta()
        {
            float speed = 4f * UnityEngine.Time.deltaTime;
            float delta = 0f;
            if (m_RaiseAction.IsPressed())
            {
                delta += speed;
            }

            if (m_LowerAction.IsPressed())
            {
                delta -= speed;
            }

            return delta;
        }

        // Jedinstveno održavanje Elevation komponente: postavlja/dodaje pri visini iznad
        // terena, uklanja kad je prop praktično na tlu — da sistemi igre ne vraćaju prop.
        private void WriteElevation(Entity entity, float elevation)
        {
            if (math.abs(elevation) <= 0.01f)
            {
                if (EntityManager.HasComponent<Game.Objects.Elevation>(entity))
                {
                    EntityManager.RemoveComponent<Game.Objects.Elevation>(entity);
                }
            }
            else if (EntityManager.TryGetComponent(entity, out Game.Objects.Elevation elevationData))
            {
                elevationData.m_Elevation = elevation;
                EntityManager.SetComponentData(entity, elevationData);
            }
            else
            {
                EntityManager.AddComponentData(entity, new Game.Objects.Elevation { m_Elevation = elevation });
            }
        }

        private void AdjustSelectionHeight(float delta)
        {
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();

            foreach (Entity entity in m_Selected)
            {
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                // Zgrada ide gore/dole sa CELIM placem (prilazi, pločnici) —
                // bez WriteElevation (zgrade nemaju Elevation komponentu).
                if (IsBuilding(entity))
                {
                    if (IsMovableBuilding(entity))
                    {
                        if (!m_SubPropCaptured.Contains(entity))
                        {
                            CaptureSubPropLayout(entity);
                        }

                        TransformBuilding(entity, new float3(0f, delta, 0f), 0f, default);
                        ScheduleSubPropRestore(entity);
                        m_DelayedSettle[entity] = 4;
                    }

                    continue;
                }

                transform.m_Position.y += delta;
                EntityManager.SetComponentData(entity, transform);
                WriteElevation(entity, transform.m_Position.y - TerrainUtils.SampleHeight(ref heightData, transform.m_Position));
                EntityManager.AddComponent<Updated>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }

            // Ograde idu gore/dole sa selekcijom (Net.Elevation ih drži na visini).
            AdjustSelectedLaneHeights(delta);

            // Mreže takođe — čvor nosi elevaciju, susedne deonice prave rampu.
            AdjustNetworkHeight(delta);
        }

        private void ApplyClickSelection(Entity entity, bool shiftHeld)
        {
            EndAlignSession();
            if (entity == Entity.Null || !EntityManager.Exists(entity))
            {
                return;
            }


            // Dok je filter aktivan, klik na prop prebacuje filter na njegov tip.
            if (m_SameFilterPrefab != Entity.Null &&
                EntityManager.TryGetComponent(entity, out PrefabRef clickedPrefab))
            {
                m_SameFilterPrefab = clickedPrefab.m_Prefab;
                SetSameFilterName();
            }

            if (shiftHeld)
            {
                if (m_Selected.Remove(entity))
                {
                    if (entity != m_HoverEntity)
                    {
                        Unhighlight(entity);
                    }
                }
                else if (m_Selected.Count < kMaxSelection)
                {
                    m_Selected.Add(entity);
                    Highlight(entity);
                }
            }
            else
            {
                ClearSelection();
                m_Selected.Add(entity);
                Highlight(entity);
            }
        }

        // Da li pritisak "hvata" selekciju bez propa: u radijusu selektovanog
        // čvora, blizu krive selektovanog segmenta/ograde, ili unutar poligona
        // selektovane površine. (Propovi imaju svoj put kroz raycast pogodak.)
        private bool TryGrabSelection(float3 position)
        {
            // Klik i hover moraju da se SLAŽU: ako bi klik na ovom mestu
            // pokupio NESELEKTOVAN čvor/segment/ogradu, selekcija ima
            // prednost nad hvatanjem (čvor na kraju selektovanog segmenta!).
            if (SelectNetworks && TryPickNetAt(position, out Entity pickNode, out Entity pickEdge))
            {
                if (pickNode != Entity.Null && !m_SelectedNodes.Contains(pickNode))
                {
                    return false;
                }

                if (pickNode == Entity.Null && pickEdge != Entity.Null && !m_SelectedNetEdges.Contains(pickEdge))
                {
                    return false;
                }
            }

            if (SelectFences && TryPickLaneAt(position, out Entity pickLane) && !m_SelectedLanes.Contains(pickLane))
            {
                return false;
            }

            foreach (Entity node in m_SelectedNodes)
            {
                if (EntityManager.TryGetComponent(node, out Game.Net.Node nodeData) &&
                    math.distance(nodeData.m_Position.xz, position.xz) <= GetNetNodeRadius(node))
                {
                    return true;
                }
            }

            foreach (Entity edge in m_SelectedNetEdges)
            {
                if (EntityManager.TryGetComponent(edge, out Game.Net.Curve curve))
                {
                    MathUtils.Distance(curve.m_Bezier, position, out float t);
                    if (math.distance(MathUtils.Position(curve.m_Bezier, t).xz, position.xz) <= kNetEdgePickThreshold)
                    {
                        return true;
                    }
                }
            }

            foreach (Entity lane in m_SelectedLanes)
            {
                if (EntityManager.TryGetComponent(lane, out Game.Net.Curve curve))
                {
                    MathUtils.Distance(curve.m_Bezier, position, out float t);
                    if (math.distance(MathUtils.Position(curve.m_Bezier, t).xz, position.xz) <= kLanePickThreshold)
                    {
                        return true;
                    }
                }
            }

            foreach (Entity area in m_SelectedSurfaces)
            {
                if (EntityManager.Exists(area) &&
                    !EntityManager.HasComponent<Owner>(area) &&
                    EntityManager.TryGetBuffer(area, true, out DynamicBuffer<Game.Areas.Node> nodes) &&
                    nodes.Length >= 3 &&
                    PointInPolygon(nodes, position.xz))
                {
                    return true;
                }
            }

            return false;
        }

        private void BeginMoveDrag(float3 anchor)
        {
            EndAlignSession();
            // Prop pod mišem ulazi u selekciju ako već nije u njoj.
            if (!m_Selected.Contains(m_LeftPressEntity) && EntityManager.Exists(m_LeftPressEntity))
            {
                if (!m_LeftPressShift)
                {
                    ClearSelection();
                }

                if (m_Selected.Count < kMaxSelection)
                {
                    m_Selected.Add(m_LeftPressEntity);
                    Highlight(m_LeftPressEntity);
                }
            }

            // Ofseti se NE računaju odavde: sidro iz ovog frejma je pogodak na
            // površini propa, a od sledećeg frejma raycast gađa samo teren —
            // paralaksa između ta dva pogotka je pravila vidljivo cimanje na
            // startu prevlačenja. InitMoveOffsets čeka prvi terenski pogodak
            // (i tek TAMO ide undo zapis — inače prekinut drag ostavi prazan undo).
            m_MoveItems.Clear();
            m_MoveDragging = true;
            m_MoveOffsetsPending = true;
        }

        // Popuni ofsete selekcije prema prvom TERENSKOM sidru (isti raycast kao
        // kasniji MoveSelection pozivi — nema skoka). ALT pri hvatanju = pomera
        // se SAMO uhvaćeni prop, ne cela selekcija.
        private void InitMoveOffsets(float3 anchor)
        {
            // Pomeranje sad zaista kreće — snapshot za undo ide ovde, pre prvog pomaka.
            PushTransformUndo();

            m_MoveItems.Clear();
            m_MoveSurfaceItems.Clear();
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            bool singleAltDrag = m_LeftPressAlt && m_Selected.Contains(m_LeftPressEntity);
            IEnumerable<Entity> moveTargets =
                singleAltDrag
                    ? new List<Entity> { m_LeftPressEntity }
                    : (IEnumerable<Entity>)m_Selected;
            foreach (Entity entity in moveTargets)
            {
                // Zgrade u izgradnji se preskaču; gotove se pomeraju kroz pod-tree.
                if (IsBuilding(entity) && !IsMovableBuilding(entity))
                {
                    continue;
                }

                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                m_MoveItems.Add(new MoveItem
                {
                    m_Entity = entity,
                    m_Offset = transform.m_Position - anchor,
                    m_HeightOffset = transform.m_Position.y - TerrainUtils.SampleHeight(ref heightData, transform.m_Position),
                });
            }

            // Farbane površine idu sa celom selekcijom (ne sa ALT solo dragom).
            // Zgradine površine se ne pomeraju pojedinačno — ne ulaze u stavke.
            if (!singleAltDrag)
            {
                foreach (Entity area in m_SelectedSurfaces)
                {
                    if (!EntityManager.HasComponent<Owner>(area) &&
                        TryGetSurfaceCentroid(area, out float2 centroid))
                    {
                        m_MoveSurfaceItems.Add(new SurfaceMoveItem
                        {
                            m_Entity = area,
                            m_Offset = centroid - anchor.xz,
                        });
                    }
                }

                // Ograde: sidro = sredina krive, korak po frejmu kao površine.
                m_MoveLaneItems.Clear();
                foreach (Entity lane in m_SelectedLanes)
                {
                    if (EntityManager.Exists(lane) &&
                        !EntityManager.HasComponent<Owner>(lane) &&
                        EntityManager.TryGetComponent(lane, out Game.Net.Curve laneCurve))
                    {
                        m_MoveLaneItems.Add(new LaneMoveItem
                        {
                            m_Entity = lane,
                            m_Offset = LaneMidpoint(laneCurve.m_Bezier).xz - anchor.xz,
                        });
                    }
                }

                // Mreže: jedna referentna tačka za ceo pokretni skup čvorova.
                m_NetMoveActive = TryGetNetSelectionCenter(out float3 netCenter);
                if (m_NetMoveActive)
                {
                    m_NetMoveOffset = netCenter.xz - anchor.xz;
                }
            }

            m_MoveOffsetsPending = false;
        }

        private void MoveSelection(float3 anchor)
        {
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();

            // Pun update za zgrade/površine na interval, ne svaki frejm.
            bool tick = BuildingTick();

            foreach (MoveItem item in m_MoveItems)
            {
                if (!EntityManager.Exists(item.m_Entity) ||
                    !EntityManager.TryGetComponent(item.m_Entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                float3 position = anchor + item.m_Offset;
                position.y = TerrainUtils.SampleHeight(ref heightData, position) + item.m_HeightOffset;

                // Zgrada vuče ceo pod-tree; bez WriteElevation (zgrade ga nemaju).
                if (IsBuilding(item.m_Entity))
                {
                    TransformBuilding(item.m_Entity, position - transform.m_Position, 0f, default, tick);
                    continue;
                }

                transform.m_Position = position;
                EntityManager.SetComponentData(item.m_Entity, transform);
                WriteElevation(item.m_Entity, item.m_HeightOffset);
                EntityManager.AddComponent<Updated>(item.m_Entity);
                EntityManager.AddComponent<BatchesUpdated>(item.m_Entity);
            }

            foreach (SurfaceMoveItem item in m_MoveSurfaceItems)
            {
                if (!TryGetSurfaceCentroid(item.m_Entity, out float2 current))
                {
                    continue;
                }

                float2 step = anchor.xz + item.m_Offset - current;
                if (math.lengthsq(step) < 1e-6f)
                {
                    continue;
                }

                TransformSurface(item.m_Entity, quaternion.identity, float3.zero, new float3(step.x, 0f, step.y), tick);
            }

            if (m_MoveLaneItems.Count > 0)
            {
                // HashSet umesto liste u vrućoj petlji (Contains po susedu);
                // pun Updated svaki frejm samo za male selekcije — velike idu
                // na tick + završni settle, kao zgrade/površine.
                HashSet<Entity> laneGroup = BuildLaneGroup();
                bool laneMark = m_MoveLaneItems.Count <= 100 || tick;
                foreach (LaneMoveItem item in m_MoveLaneItems)
                {
                    if (!EntityManager.Exists(item.m_Entity) ||
                        !EntityManager.TryGetComponent(item.m_Entity, out Game.Net.Curve laneCurve))
                    {
                        continue;
                    }

                    float2 laneStep = anchor.xz + item.m_Offset - LaneMidpoint(laneCurve.m_Bezier).xz;
                    if (math.lengthsq(laneStep) < 1e-6f)
                    {
                        continue;
                    }

                    TransformLane(item.m_Entity, quaternion.identity, float3.zero, new float3(laneStep.x, 0f, laneStep.y), laneGroup, laneMark);
                }
            }

            if (m_NetMoveActive)
            {
                // Jedan izgrađen skup po frejmu: i centar i transformacija ga dele.
                HashSet<Entity> movingNet = BuildMovingNodeSet();

                // ALT tokom vuče JEDNOG među-čvora: čvor se lepi na pravu
                // između svoja dva suseda i klizi samo po njoj.
                if (!TrySlideNodeAlongLine(anchor, movingNet, tick) &&
                    TryGetNetCenter(EntityManager, movingNet, out float3 currentNetCenter))
                {
                    float2 netStep = anchor.xz + m_NetMoveOffset - currentNetCenter.xz;
                    if (math.lengthsq(netStep) > 1e-6f)
                    {
                        TransformNetSelection(quaternion.identity, float3.zero, new float3(netStep.x, 0f, netStep.y), tick, movingNet);
                    }
                }
            }
        }

        private void CancelMarquee()
        {
            foreach (Entity entity in m_MarqueeHits)
            {
                if (!m_Selected.Contains(entity))
                {
                    Unhighlight(entity);
                }
            }

            m_MarqueeHits.Clear();
            m_MarqueeHeld = false;
            m_MarqueeActive = false;
        }

        private void DrawMarquee(OverlayRenderSystem.Buffer overlayBuffer)
        {
            float2 delta = m_MarqueeEnd.xz - m_MarqueeStart.xz;
            float2 uSide = m_MarqueeRight * math.dot(delta, m_MarqueeRight);
            float2 vSide = m_MarqueeForward * math.dot(delta, m_MarqueeForward);
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();

            float2 s = m_MarqueeStart.xz;
            float3 c00 = new float3(s.x, 0f, s.y);
            float3 c10 = new float3(s.x + uSide.x, 0f, s.y + uSide.y);
            float3 c11 = new float3(s.x + uSide.x + vSide.x, 0f, s.y + uSide.y + vSide.y);
            float3 c01 = new float3(s.x + vSide.x, 0f, s.y + vSide.y);
            c00.y = TerrainUtils.SampleHeight(ref heightData, c00) + 0.5f;
            c10.y = TerrainUtils.SampleHeight(ref heightData, c10) + 0.5f;
            c11.y = TerrainUtils.SampleHeight(ref heightData, c11) + 0.5f;
            c01.y = TerrainUtils.SampleHeight(ref heightData, c01) + 0.5f;

            // Debljina linije je u METRIMA sveta: fiksnih 0.3 m sa velike
            // visine padne ispod jednog piksela i okvir se praktično ne vidi.
            // Zato raste sa udaljenošću kamere (~0.25% rastojanja).
            float width = MarqueeLineWidth(c00);
            overlayBuffer.DrawLine(kSelectedColor, new Line3.Segment(c00, c10), width);
            overlayBuffer.DrawLine(kSelectedColor, new Line3.Segment(c10, c11), width);
            overlayBuffer.DrawLine(kSelectedColor, new Line3.Segment(c11, c01), width);
            overlayBuffer.DrawLine(kSelectedColor, new Line3.Segment(c01, c00), width);
        }

        private static float MarqueeLineWidth(float3 reference)
        {
            UnityEngine.Camera camera = UnityEngine.Camera.main;
            if (camera == null)
            {
                return 0.3f;
            }

            float distance = math.distance((float3)camera.transform.position, reference);
            return math.clamp(distance * 0.0025f, 0.3f, 40f);
        }

        private float3 GetSelectionCenter()
        {
            float3 center = float3.zero;
            int count = 0;
            foreach (Entity entity in m_Selected)
            {
                if (EntityManager.Exists(entity) && EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    center += transform.m_Position;
                    count++;
                }
            }

            // Čvorovi mreža ulaze u GLAVNI prosek — pivot mešovite selekcije
            // (propovi + putevi) mora da obuhvati i puteve, inače rotacija
            // "orbitira" oko pogrešnog centra.
            foreach (Entity node in BuildMovingNodeSet())
            {
                if (EntityManager.TryGetComponent(node, out Game.Net.Node nodeData))
                {
                    center += nodeData.m_Position;
                    count++;
                }
            }

            if (count > 0)
            {
                return center / count;
            }

            // Selekcija od samih površina/ograda: centar iz centroida poligona
            // i sredina krivih. Zgradine se preskaču — ne rotiraju se.
            float2 surfaceCenter = float2.zero;
            foreach (Entity area in m_SelectedSurfaces)
            {
                if (!EntityManager.HasComponent<Owner>(area) &&
                    TryGetSurfaceCentroid(area, out float2 centroid))
                {
                    surfaceCenter += centroid;
                    count++;
                }
            }

            foreach (Entity lane in m_SelectedLanes)
            {
                if (EntityManager.Exists(lane) &&
                    !EntityManager.HasComponent<Owner>(lane) &&
                    EntityManager.TryGetComponent(lane, out Game.Net.Curve laneCurve))
                {
                    surfaceCenter += LaneMidpoint(laneCurve.m_Bezier).xz;
                    count++;
                }
            }

            return count > 0 ? new float3(surfaceCenter.x / count, 0f, surfaceCenter.y / count) : float3.zero;
        }

        private void RotateSelection(float angle)
        {
            EndAlignSession();
            quaternion rotation = quaternion.RotateY(angle);
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();

            bool tick = BuildingTick();
            foreach (Entity entity in m_Selected)
            {
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                // Visina iznad terena se čuva i tokom rotacije.
                float heightOffset = transform.m_Position.y - TerrainUtils.SampleHeight(ref heightData, transform.m_Position);

                float3 position = m_RotationCenter + math.mul(rotation, transform.m_Position - m_RotationCenter);
                position.y = TerrainUtils.SampleHeight(ref heightData, position) + heightOffset;

                // Zgrada: pod-tree rotacija oko centra + korekcija visine terena.
                // Y-rotacija ne menja y, pa je delta.y = željena visina − stara.
                if (IsBuilding(entity))
                {
                    if (IsMovableBuilding(entity))
                    {
                        TransformBuilding(entity, new float3(0f, position.y - transform.m_Position.y, 0f), angle, m_RotationCenter, tick);
                    }

                    continue;
                }

                transform.m_Position = position;
                transform.m_Rotation = math.normalize(math.mul(rotation, transform.m_Rotation));
                EntityManager.SetComponentData(entity, transform);
                EntityManager.AddComponent<Updated>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }

            // Farbane površine se okreću zajedno sa grupom.
            foreach (Entity area in m_SelectedSurfaces)
            {
                TransformSurface(area, rotation, m_RotationCenter, float3.zero, tick);
            }

            // Ograde se okreću oko istog centra (krive ostaju krute).
            if (m_SelectedLanes.Count > 0)
            {
                HashSet<Entity> laneGroup = BuildLaneGroup();
                bool laneMark = m_SelectedLanes.Count <= 100 || tick;
                foreach (Entity lane in m_SelectedLanes)
                {
                    TransformLane(lane, rotation, m_RotationCenter, float3.zero, laneGroup, laneMark);
                }
            }

            // Mreže se okreću oko istog centra.
            TransformNetSelection(rotation, m_RotationCenter, float3.zero, tick);
        }

        private void RotateClipboard(float angle)
        {
            quaternion rotation = quaternion.RotateY(angle);
            for (int i = 0; i < m_Clipboard.Count; i++)
            {
                ClipboardItem item = m_Clipboard[i];
                item.m_Offset = math.mul(rotation, item.m_Offset);
                item.m_Rotation = math.normalize(math.mul(rotation, item.m_Rotation));
                m_Clipboard[i] = item;
            }

            // Poligoni površina se rotiraju zajedno sa grupom (Y rotacija u xz ravni).
            // Pažnja na smer: RotateY oko +Y ose u xz ravni odgovara rotaciji
            // (x,z) -> (x*cos + z*sin, -x*sin + z*cos).
            float sin = math.sin(angle);
            float cos = math.cos(angle);
            foreach (AreaClipboardItem area in m_ClipboardAreas)
            {
                for (int n = 0; n < area.m_NodeOffsets.Length; n++)
                {
                    float2 p = area.m_NodeOffsets[n];
                    area.m_NodeOffsets[n] = new float2((p.x * cos) + (p.y * sin), (-p.x * sin) + (p.y * cos));
                }
            }

            RotateClipboardLanes(sin, cos);
            RotateClipboardNetEdges(sin, cos);

            m_PasteDirty = true;
        }

        // Je li ovog frejma pritisnut bilo koji taster osim samog Alt-a.
        // (anyKey ovde ne pomaže: već je "pritisnut" zbog držanog Alt-a, pa
        // drugi taster ne pravi novu ivicu.)
        private static bool AnyNonAltKeyPressedThisFrame()
        {
            if (Keyboard.current == null)
            {
                return false;
            }

            foreach (KeyControl key in Keyboard.current.allKeys)
            {
                if (key.wasPressedThisFrame && key.keyCode != Key.LeftAlt && key.keyCode != Key.RightAlt)
                {
                    return true;
                }
            }

            return false;
        }

        // Vidi komentar u OnUpdate: štiti sveže emitovane definicije od
        // našeg sopstvenog ApplyMode.Clear u select modu.
        private int m_KeepDefinitionFrames;
        private bool m_KeepAliveFrame;

        // Okvir pretrage za net zapis mora da pokrije CELU krivu: sredina
        // parčeta posle igrine podele (tunel/zidovi) ume da bude i pola
        // dužine deonice daleko od sredine zapisa.
        private static void ExpandBoundsForRecord(PastedRecord record, ref float3 boundsMin, ref float3 boundsMax)
        {
            if (!record.m_IsNetEdge)
            {
                return;
            }

            boundsMin = math.min(boundsMin, math.min(math.min(record.m_NetCurve.a, record.m_NetCurve.b), math.min(record.m_NetCurve.c, record.m_NetCurve.d)));
            boundsMax = math.max(boundsMax, math.max(math.max(record.m_NetCurve.a, record.m_NetCurve.b), math.max(record.m_NetCurve.c, record.m_NetCurve.d)));
        }

        private void KeepDefinitionsAlive()
        {
            m_KeepDefinitionFrames = 3;
            applyMode = ApplyMode.None;
        }

        // Klipbord se menja dok fixup prethodnog stampa još radi: node
        // indeksi u tim zapisima pokazuju u STARU tabelu — poništavaju se da
        // nalepljena raskrsnica ne dobije nadogradnje/markere iz NOVOG
        // klipborda.
        private void InvalidatePendingNodeFixups()
        {
            if (m_PostPasteFix == null)
            {
                return;
            }

            for (int i = 0; i < m_PostPasteFix.Count; i++)
            {
                PastedRecord record = m_PostPasteFix[i];
                if (record.m_IsNetEdge)
                {
                    record.m_StartNodeIndex = -1;
                    record.m_EndNodeIndex = -1;
                    m_PostPasteFix[i] = record;
                }
            }
        }

        // Tvrda granica za brisanje ODJEDNOM. Brisanje N objekata u jednom
        // frejmu je strukturna oluja: svaka deonica vuče čvorove, pod-objekte
        // i Updated na susede, pa igrini nativni job-ovi dobiju zalogaj kakav
        // vanila nikad ne pravi — brisanje celog grada (61 čvor + 75 deonica
        // + sve ostalo, 01.09) je tvrdo srušilo igru, bez izuzetka i bez
        // dump-a. Granica se odbija uz zvuk greške; korisnik briše u delovima.
        // Granica nestaje kad brisanje pređe na rate kroz frejmove.
        private const int kMaxDeleteAtOnce = 500;

        private void DeleteSelection()
        {
            EndAlignSession();

            if (DeletableSelectedCount == 0)
            {
                return;
            }

            if (DeletableSelectedCount > kMaxDeleteAtOnce)
            {
                Mod.Log.Info($"Copaste: delete refused — {DeletableSelectedCount} selected, limit is {kMaxDeleteAtOnce} at once (one-frame delete of this size can crash the game)");
                if (!m_SoundQuery.IsEmptyIgnoreFilter)
                {
                    m_AudioManager.PlayUISound(m_SoundQuery.GetSingleton<ToolUXSoundSettingsData>().m_PlaceBuildingFailSound);
                }

                return;
            }

            // Zgrade se sad brišu kao i sve ostalo (vanila Deleted — isto što
            // radi buldožer). Undo ih vraća kroz construction trik, ali kao
            // NOVE zgrade: stanari/zaposleni se ne vraćaju (sim stanje).
            // I zgrade u izgradnji se snimaju — i one su obrisive.
            // FABRIČKI delovi zgrade (Clothesline, dekali na prilazu...) se NE
            // brišu pojedinačno: igra ih regeneriše na svaki update. Podržan
            // put je brisanje CELE dekorativne pod-površine (Building elements
            // + Surfaces) — ode i spawner, vanilla Deleted, ništa u save-u.
            List<TransformSnapshot> snapshots = SnapshotSelection(includeBuildings: true, includeUnderConstruction: true, includeBuildingOwned: false);
            List<SurfaceSnapshot> surfaceSnapshots = SnapshotSurfaces(m_SelectedSurfaces);
            List<LaneSnapshot> laneSnapshots = SnapshotLanes(m_SelectedLanes);
            List<NetEdgeSnapshot> netEdgeSnapshots = SnapshotDeletableNetEdges();
            if (snapshots.Count > 0 || surfaceSnapshots.Count > 0 || laneSnapshots.Count > 0 || netEdgeSnapshots.Count > 0)
            {
                PushUndo(new UndoRecord { m_Kind = UndoKind.Delete, m_Snapshots = snapshots, m_Surfaces = surfaceSnapshots, m_Lanes = laneSnapshots, m_NetEdges = netEdgeSnapshots });
            }

            foreach (Entity entity in m_Selected)
            {
                // Preskaču se SAMO regenerišući fabrički elementi (dekali sa
                // prilaza/placa i sl.) — kante, klupe i drveće u dvorištu se
                // brišu normalno.
                if (EntityManager.Exists(entity) && !IsRegeneratingSubElement(entity))
                {
                    RecordDeletedSubProp(entity);
                    EntityManager.AddComponent<Deleted>(entity);
                }
            }

            foreach (Entity area in m_SelectedSurfaces)
            {
                DeleteSurfaceWithChildren(area);
            }

            foreach (Entity lane in m_SelectedLanes)
            {
                DeleteLaneWithNodes(lane);
            }

            // Mreže: vanila Deleted na svaku ivicu (selektovane + kraci
            // selektovanih čvorova); čvor bez preostalih ivica ode s njima.
            foreach (NetEdgeSnapshot netEdge in netEdgeSnapshots)
            {
                DeleteNetEdgeWithNodes(netEdge.m_Entity);
            }

            m_Selected.Clear();
            m_SelectedSurfaces.Clear();
            m_SelectedLanes.Clear();
            m_SelectedNodes.Clear();
            m_SelectedNetEdges.Clear();

            // Hover koji nije bio u selekciji mora da izgubi highlight — inače svetli zauvek.
            if (m_HoverEntity != Entity.Null)
            {
                Unhighlight(m_HoverEntity);
            }

            m_HoverEntity = Entity.Null;
            if (!m_SoundQuery.IsEmptyIgnoreFilter)
            {
                m_AudioManager.PlayUISound(m_SoundQuery.GetSingleton<ToolUXSoundSettingsData>().m_BulldozeSound);
            }
        }

        // Brisanje površine sa njenim pod-objektima. Zgradina površina nosi
        // dekoracije koje sama spawn-uje — brišu se s njom, da ne ostanu
        // siročići. Samostalne farbane površine nemaju SubObject bafer.
        // Koriste je i delete i redo (posle undo-a igra respawn-uje dekoracije
        // na vraćenoj površini, pa i redo mora kaskadno).
        private readonly List<Entity> m_SurfaceChildScratch = new List<Entity>();

        private void DeleteSurfaceWithChildren(Entity area, bool updatePendingSigs = true)
        {
            if (!EntityManager.Exists(area))
            {
                return;
            }

            // Vlasnik sa zakazanom transplantacijom placa: potpis OVE površine
            // izlazi iz zakazanih — redo/ponovno brisanje ne sme da bude
            // pregaženo kasnijim sync-om. Sync sam prosleđuje false (briše
            // fabričke površine i ne sme da dira sopstvene potpise).
            if (updatePendingSigs && EntityManager.TryGetComponent(area, out Owner areaOwner))
            {
                RemovePendingLotSig(areaOwner.m_Owner, area);
            }

            if (EntityManager.TryGetBuffer(area, true, out DynamicBuffer<Game.Objects.SubObject> areaSubs))
            {
                // Popis PRE mutacija: AddComponent u petlji invalidira bafer
                // koji se u njoj i dalje čita (pravilo koje ostatak fajla
                // poštuje, ovde je bilo propušteno).
                m_SurfaceChildScratch.Clear();
                for (int i = 0; i < areaSubs.Length; i++)
                {
                    m_SurfaceChildScratch.Add(areaSubs[i].m_SubObject);
                }

                foreach (Entity sub in m_SurfaceChildScratch)
                {
                    if (EntityManager.Exists(sub) && !EntityManager.HasComponent<Deleted>(sub))
                    {
                        EntityManager.AddComponent<Deleted>(sub);
                    }
                }
            }

            EntityManager.AddComponent<Deleted>(area);
        }

        private void UpdateSelectMode()
        {
            // ESC gasi alat.
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                m_ToolSystem.activeTool = m_DefaultToolSystem;
                return;
            }

            // Jedno poređenje maske po frejmu: čipovi se menjaju i iz panela i
            // iz Options, pa se promena hvata ovde umesto na svakom mestu gde
            // se filter može promeniti.
            PurgeSelectionForDisabledFilters();

            UpdateRightButton(out float rotationDelta, out bool rightClick);

            // Rotacija selekcije desnim prevlačenjem. Ne dok se vuče ručka
            // krive — rotacija i ručka bi se otimale oko iste krive.
            if (rotationDelta != 0f && SelectedCount > 0 && !m_HandleDragging)
            {
                RotateSelection(rotationDelta);
            }

            bool raycastValid = GetRaycastResult(out Entity raycastEntity, out RaycastHit hit);
            Entity hitEntity = raycastValid && IsCopyable(raycastEntity) ? raycastEntity : Entity.Null;
            // Hover za mreže/ograde: beli obris kandidata pod kursorom.
            UpdateNetHover(raycastValid, hitEntity, hit.m_HitPosition);

            // Klik na neselektabilan entitet: objasni u logu tačno koja provera
            // ga obara, jednom po entitetu.
            // Zgrade sa ugašenim Buildings čipom i entiteti bez Object/PrefabRef
            // (lane segmenti i sl.) se preskaču — to su očekivani, česti klikovi.
            if (ClickedThisFrame() && raycastValid && hitEntity == Entity.Null &&
                raycastEntity != Entity.Null && raycastEntity != m_LastRegenClickLogged &&
                !IsBuilding(raycastEntity) &&
                EntityManager.HasComponent<Game.Objects.Object>(raycastEntity) &&
                EntityManager.HasComponent<PrefabRef>(raycastEntity))
            {
                m_LastRegenClickLogged = raycastEntity;
                string clickedName = EntityManager.TryGetComponent(raycastEntity, out PrefabRef clickPrefab) &&
                    m_PrefabSystem.TryGetPrefab(clickPrefab.m_Prefab, out PrefabBase clickBase)
                        ? clickBase.name
                        : $"e{raycastEntity.Index}";
                Mod.Log.Info($"Copaste: click on unselectable '{clickedName}' — {DescribeSelectionBlock(raycastEntity)}");
            }

            // Hover highlight.
            if (hitEntity != m_HoverEntity)
            {
                if (m_HoverEntity != Entity.Null && !m_Selected.Contains(m_HoverEntity))
                {
                    Unhighlight(m_HoverEntity);
                }

                m_HoverEntity = hitEntity;
                if (m_HoverEntity != Entity.Null)
                {
                    Highlight(m_HoverEntity);
                }
            }

            bool shiftHeld = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            bool leftHeld = Mouse.current != null && Mouse.current.leftButton.isPressed;
            bool leftReleased = Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;

            // Home: naoružaj biranje visine — sledeći klik na prop preuzima njegovu visinu.
            if (m_MatchHeightAction.WasPressedThisFrame())
            {
                m_HeightPickArmed = !m_HeightPickArmed && SelectionHasHeightTargets();
                if (m_HeightPickArmed)
                {
                    m_AlignPickArmed = false; // samo jedan pick mod istovremeno
                }
            }

            // Pick modovi ne važe bez selekcije (npr. posle Delete) — inače guta klikove.
            if (m_AlignPickArmed && m_Selected.Count == 0)
            {
                m_AlignPickArmed = false;
            }

            // Isto važi i za height-pick (Match H) — bez selekcije nema šta da
            // se poravna, a armiran pick bi progutao sledeći klik.
            if (m_HeightPickArmed && !SelectionHasHeightTargets())
            {
                m_HeightPickArmed = false;
            }

            // Pritisak: na propu = potencijalni klik ili početak pomeranja; na praznom tlu = početak marquee-a.
            bool ctrlHeld = Keyboard.current != null &&
                (Keyboard.current.leftCtrlKey.isPressed || Keyboard.current.rightCtrlKey.isPressed);
            Entity ctrlPick = Entity.Null;

            // Dok traje vuča ručke, "novi klik" ne postoji: promena
            // modifikatora (puštanje ALT-a usred klizanja spoja ograde) ume
            // da PONOVO okine apply akciju iako taster nije ni puštan — lanac
            // bi krenuo ispočetka, ručka bi odbila klik jer vuča već traje, i
            // klik bi propao do okvira: usred vuče bi se upalio marquee.
            if (ClickedThisFrame() && !m_HandleDragging)
            {
                if (m_AlignPickArmed && hitEntity != Entity.Null)
                {
                    // Klik bira uzor-prop: red kroz njega, duž njegove desne ose,
                    // svi propovi rotirani kao uzor.
                    AlignRowToReference(hitEntity, m_AlignPickGap);
                    m_AlignPickArmed = false;
                }
                else if (m_HeightPickArmed && hitEntity != Entity.Null)
                {
                    // Klik bira uzor-prop: cela selekcija preuzima njegovu visinu iznad terena.
                    MatchSelectionHeight(hitEntity);
                    m_HeightPickArmed = false;
                }
                else if (m_HeightPickArmed && raycastValid &&
                    TryPickHeightSource(hit.m_HitPosition, out float pickedSourceY))
                {
                    // Uzor može da bude i čvor puta / segment / ograda.
                    MatchSelectionHeightToY(pickedSourceY);
                    m_HeightPickArmed = false;
                }
                else if (ctrlHeld && raycastValid &&
                    (hitEntity != Entity.Null ||
                        (raycastEntity != Entity.Null && EntityManager.HasComponent<Game.Objects.Object>(raycastEntity))) &&
                    (ctrlPick = CyclePick(hit.m_HitPosition, hitEntity)) != Entity.Null)
                {
                    // Ctrl+klik: kruži kroz propove oko tačke pogotka — bira i one
                    // delimično uronjene u druge objekte/zgrade koje raycast preskače.
                    // Uslovi: posle pick grana (Match H / To prop imaju prednost),
                    // samo na pogodak OBJEKTA (Ctrl+klik na golo tlo i dalje čisti
                    // selekciju / kreće marquee — bitno jer se Ctrl drži za nudge),
                    // i samo ako kandidat postoji (inače klik normalno propada dalje).
                    ApplyClickSelection(ctrlPick, shiftHeld);
                }
                else if (TryClickLaneAlignHandle(raycastValid ? hit.m_HitPosition : default))
                {
                    // Trouglić poravnanja traka na kraju segmenta — klik
                    // ciklusira centar → levo → desno.
                }
                else if (TryBeginHandleDrag(raycastValid ? hit.m_HitPosition : default, hitEntity))
                {
                    // Pritisak na ručku krive (jedna ograda / jedan segment) —
                    // uska meta, sme da pobedi prop ispod sebe.
                }
                else if (hitEntity != Entity.Null)
                {
                    m_LeftHeldOnProp = true;
                    m_MoveDragging = false;
                    m_MoveOffsetsPending = false;
                    m_LeftPressEntity = hitEntity;
                    m_LeftPressShift = shiftHeld;
                    m_LeftPressAlt = Keyboard.current != null &&
                        (Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed);
                    m_MoveStart = hit.m_HitPosition;
                }
                else if (!shiftHeld && raycastValid && TryGrabSelection(hit.m_HitPosition))
                {
                    // "CS1 osećaj": pritisak na već selektovanu mrežu/ogradu/
                    // površinu hvata CELU selekciju u pomeranje — nije potreban
                    // prop u selekciji. Shift je izuzet (on menja selekciju).
                    EndAlignSession();
                    m_LeftHeldOnProp = true;
                    m_MoveDragging = false;
                    m_MoveOffsetsPending = false;
                    m_LeftPressEntity = Entity.Null;
                    m_LeftPressShift = false;
                    m_LeftPressAlt = false;
                    m_MoveStart = hit.m_HitPosition;
                }
                else if (raycastValid && !m_LeftHeldOnProp && hitEntity == Entity.Null)
                {
                    // Okvir kreće i kad je pod kursorom objekat koji NIJE
                    // selektabilan (ugašen čip): ranije se takav pritisak nije
                    // primao nigde, pa je nad pošumljenim delom sa ugašenim
                    // drvećem alat izgledao mrtvo — okvir ne kreće, selekcija
                    // se ne briše, puštanje ne radi ništa.
                    m_MarqueeHeld = true;
                    m_MarqueeActive = false;

                    // Ugao se i dalje sidri NA TLU: zrak koji je pogodio krov
                    // ili krošnju vraća tačku desetak metara u vazduhu, a
                    // okvir se meri po zemlji.
                    m_MarqueeStart = hit.m_HitPosition;
                    if (raycastEntity != Entity.Null && EntityManager.HasComponent<Game.Objects.Object>(raycastEntity))
                    {
                        TerrainHeightData marqueeTerrain = m_TerrainSystem.GetHeightData();
                        m_MarqueeStart.y = TerrainUtils.SampleHeight(ref marqueeTerrain, m_MarqueeStart);
                    }

                    m_MarqueeEnd = m_MarqueeStart;

                    // Okvir se poravnava sa uglom kamere.
                    UnityEngine.Camera camera = UnityEngine.Camera.main;
                    float3 forward = camera != null ? (float3)camera.transform.forward : new float3(0f, 0f, 1f);
                    m_MarqueeForward = math.normalizesafe(forward.xz, new float2(0f, 1f));
                    m_MarqueeRight = new float2(m_MarqueeForward.y, -m_MarqueeForward.x);
                }
            }

            // Pomeranje: prevlačenje sa propa vuče celu selekciju.
            // Povlačenje ručke krive: tačka prati terenski pogodak.
            if (m_HandleDragging)
            {
                // Bez uslova raycastValid: nad mostom ili vodom zrak ume da
                // promaši teren, a ručka se vodi po kursoru u SVOJOJ ravni —
                // terenski pogodak joj je samo rezerva. Ranije se ručka na
                // uzdignutoj deonici prosto nije pomerala.
                if (leftHeld)
                {
                    UpdateHandleDrag(raycastValid ? hit.m_HitPosition : default);
                }

                if (leftReleased)
                {
                    EndHandleDrag();
                }
            }

            if (m_LeftHeldOnProp && leftHeld && raycastValid)
            {
                if (!m_MoveDragging && math.distance(m_MoveStart.xz, hit.m_HitPosition.xz) > 0.4f)
                {
                    BeginMoveDrag(hit.m_HitPosition);
                }
                else if (m_MoveDragging && m_MoveOffsetsPending)
                {
                    // Prvi frejm sa terenskom raycast maskom — tek sad znamo pravo sidro.
                    InitMoveOffsets(hit.m_HitPosition);
                }
                else if (m_MoveDragging)
                {
                    MoveSelection(hit.m_HitPosition);
                }
            }

            if (m_LeftHeldOnProp && leftReleased)
            {
                if (!m_MoveDragging)
                {
                    ApplyClickSelection(m_LeftPressEntity, m_LeftPressShift);
                }
                else
                {
                    // Kraj prevlačenja: pomerene zgrade dobiju jedan čist
                    // završni update na konačnoj poziciji (put/pathfind veze).
                    bool anyBuilding = false;
                    foreach (MoveItem item in m_MoveItems)
                    {
                        if (IsBuilding(item.m_Entity))
                        {
                            SettleBuilding(item.m_Entity);
                            anyBuilding = true;
                        }
                    }

                    SettleSurfaces();
                    SettleLanes();
                    SettleNetworks();
                    m_NetMoveActive = false;

                    // Siročići (dekali placa sa mrtvim vlasnikom) vise na
                    // STAROM mestu odakle je drag krenuo.
                    if (anyBuilding)
                    {
                        SweepOrphansAround(m_MoveStart, 64f);
                    }
                }

                m_LeftHeldOnProp = false;
                m_MoveDragging = false;
                m_MoveOffsetsPending = false;
                m_MoveItems.Clear();
                m_MoveSurfaceItems.Clear();
                m_MoveLaneItems.Clear();
            }

            // Marquee: prevlačenje razvlači okvir, sve unutra se selektuje uživo.
            if (m_MarqueeHeld && leftHeld && raycastValid)
            {
                m_MarqueeEnd = hit.m_HitPosition;
                if (!m_MarqueeActive && math.distance(m_MarqueeStart.xz, m_MarqueeEnd.xz) > 1f)
                {
                    m_MarqueeActive = true;
                    m_MarqueeLastScan = m_MarqueeEnd + 1000f;
                }

                // Skeniranje svih objekata je skupo — samo kad se miš stvarno pomeri.
                if (m_MarqueeActive && math.distance(m_MarqueeEnd.xz, m_MarqueeLastScan.xz) > 0.25f)
                {
                    m_MarqueeLastScan = m_MarqueeEnd;
                    UpdateMarqueeHits();
                }
            }

            if (m_MarqueeHeld && leftReleased)
            {
                if (m_MarqueeActive)
                {
                    CommitMarquee(shiftHeld);
                }
                else if (SelectSurfaces && TryPickSurfaceAt(m_MarqueeStart, out Entity clickedSurface))
                {
                    // Klik na farbanu površinu je selektuje (Shift = dodaj/skini).
                    // Isti gate kao marquee: površine su deo Buildings moda.
                    EndAlignSession();
                    if (shiftHeld)
                    {
                        if (!m_SelectedSurfaces.Remove(clickedSurface) && SelectedCount < kMaxSelection)
                        {
                            m_SelectedSurfaces.Add(clickedSurface);
                        }
                    }
                    else
                    {
                        ClearSelection();
                        m_SelectedSurfaces.Add(clickedSurface);
                    }
                }
                else if (SelectFences && TryPickLaneAt(m_MarqueeStart, out Entity clickedLane))
                {
                    // Klik blizu ograde je selektuje — ista pravila kao površine.
                    EndAlignSession();
                    if (shiftHeld)
                    {
                        if (!m_SelectedLanes.Remove(clickedLane) && SelectedCount < kMaxSelection)
                        {
                            m_SelectedLanes.Add(clickedLane);
                        }
                    }
                    else
                    {
                        ClearSelection();
                        m_SelectedLanes.Add(clickedLane);
                    }
                }
                else if (SelectNetworks && TryPickNetAt(m_MarqueeStart, out Entity clickedNode, out Entity clickedNetEdge))
                {
                    // Klik na mrežu: čvor ima prednost u svom radijusu, inače
                    // segment. Shift dodaje/skida, isto kao ostalo.
                    EndAlignSession();
                    List<Entity> targetList = clickedNode != Entity.Null ? m_SelectedNodes : m_SelectedNetEdges;
                    Entity clickedNet = clickedNode != Entity.Null ? clickedNode : clickedNetEdge;
                    if (shiftHeld)
                    {
                        if (!targetList.Remove(clickedNet) && SelectedCount < kMaxSelection)
                        {
                            targetList.Add(clickedNet);
                        }
                    }
                    else
                    {
                        ClearSelection();
                        targetList.Add(clickedNet);
                    }
                }
                else if (!shiftHeld)
                {
                    // Običan klik na prazno tlo poništava selekciju.
                    ClearSelection();
                }

                m_MarqueeHeld = false;
                m_MarqueeActive = false;
            }

            // Brzi desni klik: prvo gasi height-pick, pa čisti selekciju, pa izlazi iz alata.
            if (rightClick)
            {
                // Tokom vuče ručke desni klik znači "prekini potez": vuča se
                // uredno završi, selekcija OSTAJE. Ranije je brisao selekciju,
                // a vuča je preživljavala — kriva je sablasno pratila miš bez
                // ijedne vidljive ručke, i bez završnog settle-a.
                if (m_HandleDragging)
                {
                    EndHandleDrag();
                    return;
                }

                if (m_AlignPickArmed)
                {
                    m_AlignPickArmed = false;
                }
                else if (m_HeightPickArmed)
                {
                    m_HeightPickArmed = false;
                }
                else if (SelectedCount > 0)
                {
                    ClearSelection();
                }
                else
                {
                    m_ToolSystem.activeTool = m_DefaultToolSystem;
                }

                return;
            }

            // Delete: obriši selektovane propove i površine.
            // Tokom aktivnog poteza (drag/rotacija/marquee) delete i undo/redo
            // se ignorišu — undo bi pojeo zapis SOPSTVENOG poteza i teleportovao
            // entitete usred vučenja.
            bool gestureActive = m_MoveDragging || m_RightDragging || m_MarqueeActive || m_HandleDragging;

            // U: podzemni režim (prikaz + šta klik/okvir biraju; copy/paste/
            // undo rade isto u oba sveta).
            if (Keyboard.current != null && Keyboard.current.uKey.wasPressedThisFrame && !m_UiTyping)
            {
                UndergroundMode = !UndergroundMode;
            }

            // ALT (tap): izravnaj selektovane među-čvorove mreže u pravu liniju
            // između suseda-sidara. ALT je već i modifikator (45°
            // snap rotacije, Alt+klik na prop, Alt+točkić), a igrin sistem
            // prečica ne ume goli modifikator kao taster — zato TAP: pritisak
            // naoruža, sve ostalo razoružava, čisto otpuštanje okida.
            // MORA pre Delete/Undo/Redo/Relocate izlaza: oni izlaze iz funkcije
            // pa bi razoružavanje bilo preskočeno i Alt bi ostao naoružan.
            if (Keyboard.current != null)
            {
                bool altPressed = Keyboard.current.leftAltKey.wasPressedThisFrame ||
                    Keyboard.current.rightAltKey.wasPressedThisFrame;
                bool altReleased = Keyboard.current.leftAltKey.wasReleasedThisFrame ||
                    Keyboard.current.rightAltKey.wasReleasedThisFrame;
                if (altPressed && !gestureActive && !m_UiTyping && m_SelectedNodes.Count > 0)
                {
                    m_StraightenArmed = true;
                }

                // Klik/skrol, bilo koji drugi taster (Alt+Tab, Ctrl+Z dok je
                // Alt dole) i gubitak fokusa prozora — sve gasi tap.
                if (m_StraightenArmed &&
                    (gestureActive ||
                    !UnityEngine.Application.isFocused ||
                    (Mouse.current != null &&
                        (Mouse.current.leftButton.isPressed || Mouse.current.rightButton.isPressed ||
                        math.abs(Mouse.current.scroll.ReadValue().y) > 0.01f)) ||
                    AnyNonAltKeyPressedThisFrame()))
                {
                    m_StraightenArmed = false;
                }

                if (altReleased && m_StraightenArmed)
                {
                    m_StraightenArmed = false;
                    StraightenSelectedNetNodes();
                    return;
                }
            }

            if (m_DeleteAction.WasPressedThisFrame() && !gestureActive &&
                DeletableSelectedCount > 0)
            {
                DeleteSelection();
                return;
            }

            // Undo (Ctrl+Z).
            if (m_UndoAction.WasPressedThisFrame() && !gestureActive)
            {
                Undo();
                return;
            }

            // Redo (Ctrl+Y) — samo transformacije, u select modu.
            if (m_RedoAction.WasPressedThisFrame() && !gestureActive)
            {
                Redo();
                return;
            }

            // Relocate (Tab) — isto kao panel dugme: tačno jedna završena
            // zgrada u selekciji. Tab je i "sledeće polje" u UI, pa typing guard.
            if (m_RelocateAction.WasPressedThisFrame() && !gestureActive && !m_UiTyping && CanRelocate)
            {
                TriggerRelocate();
                return;
            }

            // Select same (T): uključuje/isključuje filter tipa — dok je aktivan, marquee hvata samo taj tip.
            if (m_SelectSameAction.WasPressedThisFrame())
            {
                ToggleSameFilter();
            }

            // Snap na teren (End) — samo ako ima entiteta na koje deluje.
            if (m_SnapGroundAction.WasPressedThisFrame() && SelectionHasHeightTargets())
            {
                PushTransformUndo();
                SnapSelectionToGround();
            }

            // Nudge (Ctrl+strelice): fino pomeranje selekcije. Gate na stvarne
            // transform mete — owned-only površine ne pomeraju ništa.
            float3 nudgeDelta = GetNudgeDelta();
            if (!nudgeDelta.Equals(float3.zero) && SelectionHasTransformTargets())
            {
                if (AnyNudgePressedThisFrame())
                {
                    PushTransformUndo();
                }

                NudgeSelection(nudgeDelta);
            }

            // ALT + točkić: svaki selektovani prop se okreće oko svoje ose
            // (15° po koraku točkića). Ne prekida align sesiju.
            if (m_Selected.Count > 0 && !m_UiTyping &&
                Keyboard.current != null && UnityEngine.InputSystem.Mouse.current != null &&
                (Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed))
            {
                float scroll = UnityEngine.InputSystem.Mouse.current.scroll.ReadValue().y;
                if (scroll != 0f)
                {
                    // Jedan undo zapis po "naletu" okretanja, ne po svakom koraku.
                    int spinStamp = AltSpinSelectionStamp();
                    if (UnityEngine.Time.time - m_LastAltSpinTime > 1f ||
                        m_LastAltSpinStamp != spinStamp)
                    {
                        PushTransformUndo();
                    }

                    m_LastAltSpinTime = UnityEngine.Time.time;
                    m_LastAltSpinStamp = spinStamp;
                    float notches = math.abs(scroll) > 10f ? scroll / 120f : scroll;
                    SpinSelection(math.radians(15f) * notches);
                }
            }

            // Align sesija: [ i ] (rebindable) fino menjaju razmak poslednjeg
            // poravnanja — ISTOM metodom kao stepper u panelu.
            if (!m_UiTyping)
            {
                if (m_AlignGapPlusAction.WasPressedThisFrame())
                {
                    AdjustAlignSessionGap(1);
                }
                else if (m_AlignGapMinusAction.WasPressedThisFrame())
                {
                    AdjustAlignSessionGap(-1);
                }
            }

            // PageUp/PageDown: podizanje/spuštanje selekcije.
            float heightDelta = GetHeightInputDelta();

            // Dok se drži ručka krive, PgUp/PgDn diže/spušta BAŠ TU TAČKU
            // (kos segment) — ne celu selekciju.
            if (heightDelta != 0f && m_HandleDragging)
            {
                m_HandleHeightOffset += heightDelta;
            }
            else if (heightDelta != 0f &&
                TryAdjustStickyHandleHeight(heightDelta, m_RaiseAction.WasPressedThisFrame() || m_LowerAction.WasPressedThisFrame()))
            {
                // Klikom izabrana ručka: pomera se samo ta tačka.
            }
            else if (heightDelta != 0f && SelectionHasHeightTargets())
            {
                if (m_RaiseAction.WasPressedThisFrame() || m_LowerAction.WasPressedThisFrame())
                {
                    PushTransformUndo();
                }

                AdjustSelectionHeight(heightDelta);
            }

            // Copy: kopiraj selekciju u clipboard.
            if (m_CopyAction.WasPressedThisFrame() && CopyableSelectedCount > 0)
            {
                CopySelection();
            }

            // Paste: pređi u paste mod ako clipboard nije prazan.
            // NE usred vuče ručke: prelazak u paste mod bi preskočio puštanje
            // tastera (detektuje se samo u select bloku), m_HandleDragging bi
            // ostao true zauvek, a guard lanca klikova bi posle povratka
            // blokirao SVE klikove — alat izgleda mrtav do restarta.
            if (m_PasteAction.WasPressedThisFrame() && ClipboardCount > 0 && !m_HandleDragging)
            {
                EnterPasteMode();
            }

            DrawSelectOverlays();
        }

        private void UpdatePasteMode()
        {
            // ESC izlazi iz paste moda nazad u selekciju.
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                ExitPasteMode();
                return;
            }

            UpdateRightButton(out float rotationDelta, out bool rightClick);

            // Rotacija cele grupe u preview-u desnim prevlačenjem.
            // Dok road snap drži grupu, on kontroliše rotaciju — ručna se preskače.
            if (rotationDelta != 0f && !RoadSnapEngaged)
            {
                RotateClipboard(rotationDelta);
            }

            // Undo radi i u paste modu — poništava poslednji "stamp".
            if (m_UndoAction.WasPressedThisFrame())
            {
                Undo();
                m_PasteDirty = true;
            }

            // I redo: poništeni stamp se vraća bez izlaska iz paste moda.
            // (Ranije Ctrl+Y ovde NIJE bio vezan, a panel dugme je bilo
            // ugašeno u paste modu — redo posle undo-a stampa bio nemoguć.)
            if (m_RedoAction.WasPressedThisFrame())
            {
                Redo();
            }

            // End: resetuj visinski pomak preview-a.
            if (m_SnapGroundAction.WasPressedThisFrame() && m_PasteHeightBoost != 0f)
            {
                m_PasteHeightBoost = 0f;
                m_PasteDirty = true;
            }

            // PageUp/PageDown: podizanje/spuštanje cele grupe u preview-u.
            float pasteHeightDelta = GetHeightInputDelta();
            if (pasteHeightDelta != 0f)
            {
                m_PasteHeightBoost += pasteHeightDelta;
                m_PasteDirty = true;
            }

            // Brzi desni klik: nazad u selekcioni mod (preview nestaje jer je applyMode == Clear).
            if (rightClick)
            {
                ExitPasteMode();
                return;
            }

            if (!GetRaycastResult(out Entity _, out RaycastHit hit))
            {
                // Isti razlog kao kod gard-a za kucanje: ghost je ovog frejma
                // obrisan, pa sledeći mora ponovo da emituje definicije.
                m_PasteDirty = true;
                return;
            }

            // "Anarchy": kaži validaciji igre da ignoriše greške postavljanja (preklapanja itd.).
            // Namerno bez skidanja Error komponenti — ignoreErrors je zvaničan i dovoljan mehanizam,
            // a globalno skidanje je diralo i entitete koji nisu naši.
            m_ToolSystem.ignoreErrors = Mod.Settings.AnarchyPaste || m_PreviousIgnoreErrors;

            float3 anchor = hit.m_HitPosition;
            anchor = ApplyRoadSnap(anchor);
            DrawPasteOverlays(anchor);

            // Keep-alive prozor (markeri kružnog toka posle stampa / redo
            // rekreacija): applyMode je None pa se stari ghost NE čisti — novi
            // preview preko njega bi napravio DUPLI set koji bi klik ceo
            // primenio (trajni duplikat koga undo ne vidi). Zato se preview i
            // klik zamrznu par frejmova; posle prozora prvi Clear frejm briše
            // nagomilano i preview se emituje svež.
            if (m_KeepAliveFrame || m_KeepDefinitionFrames > 0)
            {
                m_PasteDirty = true;
                if (m_PreviewLookFrames > 0)
                {
                    m_PreviewLookFrames--;
                }

                return;
            }

            // Klik: potvrdi — prošlofrejmovski preview postaje trajan.
            if (ClickedThisFrame())
            {
                // Bez preview-a nema šta da se potvrdi (npr. klik u prvom frejmu paste moda).
                if (m_LastPreview.Count == 0)
                {
                    return;
                }

                applyMode = ApplyMode.Apply;
                m_PasteDirty = true;

                // Ista lista ide i u post-paste popravke i u undo zapis: popravke usput
                // razrešavaju TAČNE entitete koje je paste stvorio, pa undo briše samo njih.
                m_PostPasteFix = new List<PastedRecord>(m_LastPreview);
                m_PostPasteFixFrames = 10;
                m_EmittedNodeMarkers.Clear();

                // U ovom frejmu upiti vide samo pre-postojeće entitete (novi
                // nastaju tek posle Apply) — popis "dvojnika" za rezoluciju,
                // da undo ne obriše tuđu identičnu zgradu.
                m_PostPasteNetPreCurves = new List<PreStampNetCurve>();
                m_PostPasteExclude = CollectPreStampMatches(m_PostPasteFix, m_PostPasteNetPreCurves);

                // Isključeni "blizanci" idu i u SAM zapis: prozor rezolucije
                // nuluje m_PostPasteExclude, a kasni undo mora i dalje da zna
                // koje postojeće entitete NE sme da obriše pozicionim matchom.
                PushUndo(new UndoRecord
                {
                    m_Kind = UndoKind.Paste,
                    m_Pasted = m_PostPasteFix,
                    m_PastedExclude = m_PostPasteExclude,
                    m_PastedPreCurves = m_PostPasteNetPreCurves,
                });
                if (!m_SoundQuery.IsEmptyIgnoreFilter)
                {
                    m_AudioManager.PlayUISound(m_SoundQuery.GetSingleton<ToolUXSoundSettingsData>().m_PlacePropSound);
                }

                return;
            }

            // Preview prati miš: definicije se prave iznova samo kad se pozicija promeni.
            if (m_PasteDirty || !anchor.Equals(m_LastAnchor))
            {
                m_LastAnchor = anchor;
                m_PasteDirty = false;
                CreatePasteDefinitions(anchor);

                // Temp entiteti kaskaju za definicijama — doteruj boje ghost-a
                // još nekoliko frejmova, pa prestani (skupo je za velike grupe).
                m_PreviewLookFrames = 10;
            }
            else
            {
                applyMode = ApplyMode.None;
            }

            if (m_PreviewLookFrames > 0)
            {
                m_PreviewLookFrames--;
                UpdatePreviewLook();
            }
        }

        // Ghost preview: Temp entiteti dobijaju seed/boju originala, da preview
        // prikazuje ono što će stvarno biti nalepljeno ("Original" mod).
        private void UpdatePreviewLook()
        {
            if (m_LastPreview.Count == 0)
            {
                return;
            }

            bool anyLook = false;
            float3 boundsMin = new float3(float.MaxValue);
            float3 boundsMax = new float3(float.MinValue);
            foreach (PastedRecord record in m_LastPreview)
            {
                if (record.m_HasSeed || record.m_HasCustomColor)
                {
                    anyLook = true;
                }

                boundsMin = math.min(boundsMin, record.m_Position);
                boundsMax = math.max(boundsMax, record.m_Position);
            }

            if (!anyLook)
            {
                return;
            }

            // Širok margin: Temp entiteti kaskaju frejm za anchor-om dok se miš pomera.
            boundsMin -= 50f;
            boundsMax += 50f;

            NativeArray<Entity> entities = m_TempPreviewQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms = m_TempPreviewQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            NativeArray<PrefabRef> prefabRefs = m_TempPreviewQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            NativeArray<Temp> temps = m_TempPreviewQuery.ToComponentDataArray<Temp>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                // KRITIČNO: Temp sa m_Original je proxy POSTOJEĆEG objekta zahvaćenog
                // postavljanjem — farbanje njega bi na apply trajno prefarbalo tuđ prop.
                // Naši ghost entiteti (iz CreationDefinition) nemaju original.
                if (temps[i].m_Original != Entity.Null)
                {
                    continue;
                }

                float3 entityPosition = transforms[i].m_Position;
                if (math.any(entityPosition < boundsMin) || math.any(entityPosition > boundsMax))
                {
                    continue;
                }

                // Najbliži zapis istog prefaba — dovoljno tačno za preview.
                int bestIndex = -1;
                float bestDistance = float.MaxValue;
                for (int j = 0; j < m_LastPreview.Count; j++)
                {
                    PastedRecord record = m_LastPreview[j];
                    if (record.m_Prefab != prefabRefs[i].m_Prefab)
                    {
                        continue;
                    }

                    float distance = math.distancesq(entityPosition, record.m_Position);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = j;
                    }
                }

                if (bestIndex < 0)
                {
                    continue;
                }

                PastedRecord match = m_LastPreview[bestIndex];
                if (match.m_HasSeed &&
                    EntityManager.TryGetComponent(entities[i], out PseudoRandomSeed tempSeed) &&
                    tempSeed.m_Seed != match.m_Seed)
                {
                    EntityManager.SetComponentData(entities[i], new PseudoRandomSeed(match.m_Seed));
                    EntityManager.AddComponent<BatchesUpdated>(entities[i]);
                }

                if (match.m_HasCustomColor)
                {
                    ApplyInstanceColors(entities[i], match.m_CustomColor);
                }
            }

            entities.Dispose();
            transforms.Dispose();
            prefabRefs.Dispose();
            temps.Dispose();
        }

        // ---------- Odsecanje overlay-a ----------
        //
        // Ono što je IZA kamere ili predaleko ne može da se vidi, a svaki
        // oblik košta poziv crtanja (izmereno ~1.3 us po pozivu). Pri velikim
        // selekcijama je pola oblika redovno van vidnog polja.
        private bool m_OverlayCullValid;
        private float3 m_OverlayCameraPosition;
        private float3 m_OverlayCameraForward;

        private void BeginOverlayCull()
        {
            UnityEngine.Camera camera = UnityEngine.Camera.main;
            m_OverlayCullValid = camera != null;
            if (!m_OverlayCullValid)
            {
                return;
            }

            UnityEngine.Vector3 position = camera.transform.position;
            UnityEngine.Vector3 forward = camera.transform.forward;
            m_OverlayCameraPosition = new float3(position.x, position.y, position.z);
            m_OverlayCameraForward = new float3(forward.x, forward.y, forward.z);

        }

        private bool OverlayVisible(float3 position)
        {
            if (!m_OverlayCullValid)
            {
                return true;
            }

            // Tolerancija od 50 m pokriva oblike tik uz ivicu kadra. Granica
            // daljine je namerno preko cele mape: pri odaljenoj kameri i sama
            // visina kamere ume da premaši par kilometara, pa bi uža granica
            // sakrila selekciju koja se lepo vidi. Pravi dobitak ionako nosi
            // provera "iza kamere".
            float3 delta = position - m_OverlayCameraPosition;
            return math.dot(delta, m_OverlayCameraForward) > -50f &&
                math.lengthsq(delta) < 10000f * 10000f;
        }

        private void DrawSelectOverlays()
        {
            if (SelectedCount == 0 &&
                m_HoverEntity == Entity.Null && !m_MarqueeActive &&
                m_LaneHoverEntity == Entity.Null && m_NetHoverNode == Entity.Null && m_NetHoverEdge == Entity.Null)
            {
                return;
            }

            BeginOverlayCull();
            OverlayRenderSystem.Buffer overlayBuffer = m_OverlayRenderSystem.GetBuffer(out JobHandle _);

            // Selektovane površine: obris poligona u boji selekcije.
            // Isti overlay budžet kao za krugove — hiljade površina ne smeju
            // da sruše frejmrejt crtanjem obrisa.
            int surfacesDrawn = 0;
            foreach (Entity area in m_SelectedSurfaces)
            {
                if (surfacesDrawn >= kMaxOverlayCircles)
                {
                    break;
                }

                if (!EntityManager.Exists(area) ||
                    !EntityManager.TryGetBuffer(area, true, out DynamicBuffer<Game.Areas.Node> nodes) ||
                    nodes.Length < 2)
                {
                    continue;
                }

                surfacesDrawn++;
                for (int n = 0; n < nodes.Length; n++)
                {
                    float3 a = nodes[n].m_Position;
                    float3 b = nodes[(n + 1) % nodes.Length].m_Position;
                    overlayBuffer.DrawLine(kSelectedColor, new Line3.Segment(a, b), 0.3f);
                }
            }

            // Selektovane ograde: linija duž krive.
            DrawLaneOverlays(overlayBuffer);

            // Selektovane mreže: krug na čvoru, linija duž segmenta.
            DrawNetworkOverlays(overlayBuffer);

            // Ručke za savijanje (jedna ograda / jedan segment).
            DrawHandleOverlays(overlayBuffer);

            // EKSPERIMENT: kružići poravnanja traka.
            DrawLaneAlignHandles(overlayBuffer);

            // Hover kandidati (mreže/ograde).
            DrawNetHoverOverlays(overlayBuffer);

            if (m_MarqueeActive)
            {
                DrawMarquee(overlayBuffer);
            }

            int drawn = 0;
            foreach (Entity entity in m_Selected)
            {
                if (drawn >= kMaxOverlayCircles)
                {
                    break;
                }

                if (EntityManager.Exists(entity) &&
                    EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    if (!OverlayVisible(transform.m_Position))
                    {
                        continue;
                    }

                    // Zgrade: obris placa umesto velikog kruga.
                    if (IsBuilding(entity) &&
                        TryDrawBuildingLotOutline(overlayBuffer, entity, transform, kSelectedColor, 0.3f))
                    {
                        drawn++;
                        continue;
                    }

                    overlayBuffer.DrawCircle(kSelectedColor, default, 0.25f, 0, new float2(0f, 1f), transform.m_Position, GetDiameter(entity));
                    drawn++;
                }
            }

            if (m_HoverEntity != Entity.Null &&
                !m_Selected.Contains(m_HoverEntity) &&
                EntityManager.Exists(m_HoverEntity) &&
                EntityManager.TryGetComponent(m_HoverEntity, out Game.Objects.Transform hoverTransform))
            {
                if (!(IsBuilding(m_HoverEntity) &&
                    TryDrawBuildingLotOutline(overlayBuffer, m_HoverEntity, hoverTransform, kHoverColor, 0.3f)))
                {
                    overlayBuffer.DrawCircle(kHoverColor, default, 0.25f, 0, new float2(0f, 1f), hoverTransform.m_Position, GetDiameter(m_HoverEntity));
                }
            }

            // Fokus iz panela: hover na red u "Selected props" listi crta zeleni prsten
            // oko baš tog propa — da se identifikuje među istoimenim.
            if (m_ListFocusEntity != Entity.Null &&
                m_Selected.Contains(m_ListFocusEntity) &&
                EntityManager.Exists(m_ListFocusEntity) &&
                EntityManager.TryGetComponent(m_ListFocusEntity, out Game.Objects.Transform focusTransform))
            {
                if (!(IsBuilding(m_ListFocusEntity) &&
                    TryDrawBuildingLotOutline(overlayBuffer, m_ListFocusEntity, focusTransform, kListFocusColor, 0.5f)))
                {
                    overlayBuffer.DrawCircle(kListFocusColor, default, 0.5f, 0, new float2(0f, 1f), focusTransform.m_Position, GetDiameter(m_ListFocusEntity) + 1f);
                }
            }
        }

        private void DrawPasteOverlays(float3 anchor)
        {
            if (m_Clipboard.Count == 0)
            {
                return;
            }

            OverlayRenderSystem.Buffer overlayBuffer = m_OverlayRenderSystem.GetBuffer(out JobHandle _);
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            float baseDelta = GetAnchorHeightDelta(anchor, ref heightData);

            foreach (ClipboardItem item in m_Clipboard)
            {
                float3 position = anchor + item.m_Offset;
                position.y = TerrainUtils.SampleHeight(ref heightData, position) + item.m_HeightOffset + baseDelta + m_PasteHeightBoost;
                overlayBuffer.DrawCircle(kPasteColor, default, 0.25f, 0, new float2(0f, 1f), position, item.m_Diameter);
            }
        }

        // Ako je anchor na putu/stazi (iznad terena), cela grupa se podiže na tu površinu.
        private float GetAnchorHeightDelta(float3 anchor, ref TerrainHeightData heightData)
        {
            float delta = anchor.y - TerrainUtils.SampleHeight(ref heightData, anchor);
            return delta < 0.1f ? 0f : delta;
        }

        private float GetDiameter(Entity entity)
        {
            if (EntityManager.TryGetComponent(entity, out PrefabRef prefabRef))
            {
                return GetPrefabDiameter(prefabRef.m_Prefab);
            }

            return 2.5f;
        }

        // Keš — overlay krugovi ovo čitaju svaki frejm za svaki selektovan prop.
        private readonly Dictionary<Entity, float> m_PrefabDiameter = new Dictionary<Entity, float>();

        private float GetPrefabDiameter(Entity prefabEntity)
        {
            if (m_PrefabDiameter.TryGetValue(prefabEntity, out float cached))
            {
                return cached;
            }

            float result = GetPrefabDiameterUncached(prefabEntity);
            m_PrefabDiameter[prefabEntity] = result;
            return result;
        }

        private float GetPrefabDiameterUncached(Entity prefabEntity)
        {
            if (EntityManager.TryGetComponent(prefabEntity, out ObjectGeometryData geometryData))
            {
                float diameter;
                if ((geometryData.m_Flags & Game.Objects.GeometryFlags.Circular) != 0)
                {
                    diameter = geometryData.m_Size.x + 1f;
                }
                else
                {
                    diameter = math.max(geometryData.m_Size.x, geometryData.m_Size.z) + 1f;
                }

                return math.max(diameter, 2f);
            }

            return 2.5f;
        }

        // Prebaci SELEKTOVANE površine (marquee, WYSIWYG) u clipboard,
        // sa poligonima relativno na centroid selekcije.
        private void CaptureSurfaces(float3 centroid)
        {
            m_ClipboardAreas.Clear();
            foreach (Entity area in m_SelectedSurfaces)
            {
                if (!EntityManager.Exists(area) ||
                    !EntityManager.TryGetComponent(area, out PrefabRef prefabRef) ||
                    !EntityManager.TryGetBuffer(area, true, out DynamicBuffer<Game.Areas.Node> nodes) ||
                    nodes.Length < 3)
                {
                    continue;
                }

                // Anti-duplikat (isto kao za propove): zgradina površina čiji je
                // vlasnik TAKOĐE u selekciji se ne kopira zasebno — nalepljena
                // zgrada je dobija kroz construction.
                if (EntityManager.HasComponent<Owner>(area) &&
                    m_Selected.Contains(GetOwnerRootBuilding(area)))
                {
                    continue;
                }

                float2[] offsets = new float2[nodes.Length];
                for (int n = 0; n < nodes.Length; n++)
                {
                    offsets[n] = nodes[n].m_Position.xz - centroid.xz;
                }

                m_ClipboardAreas.Add(new AreaClipboardItem
                {
                    m_Prefab = prefabRef.m_Prefab,
                    m_NodeOffsets = offsets,
                });
            }
        }

        private void CopySelection()
        {
            // PRE čišćenja klipborda: selekcija koja ne bi proizvela NIJEDNU
            // stavku (npr. sam jedan čvor puta) ne sme da uništi sadržaj.
            // Meri se ISTIM brojačem koji pali Copy dugme — kapija i posao
            // ne smeju da se razilaze.
            if (CopyableSelectedCount == 0)
            {
                return;
            }

            InvalidatePendingNodeFixups();
            m_Clipboard.Clear();
            m_ClipboardAreas.Clear();
            m_ClipboardLanes.Clear();
            m_ClipboardNetEdges.Clear();
            ResetClipboardNetNodes(null, null, null, null);

            // Anti-duplikat: prop čiji je VLASNIK (zgrada) takođe u selekciji se
            // NE kopira zasebno — nalepljena zgrada kroz construction sagradi
            // svoje propove, pa bi kopija napravila duple.
            HashSet<Entity> selectedSet = new HashSet<Entity>(m_Selected);
            bool OwnerInSelection(Entity entity)
            {
                if (!EntityManager.TryGetComponent(entity, out Owner owner))
                {
                    return false;
                }

                if (selectedSet.Contains(owner.m_Owner))
                {
                    return true;
                }

                return EntityManager.TryGetComponent(owner.m_Owner, out Owner outer) &&
                    selectedSet.Contains(outer.m_Owner);
            }

            float3 centroid = float3.zero;
            int count = 0;
            foreach (Entity entity in m_Selected)
            {
                if (EntityManager.Exists(entity) &&
                    !OwnerInSelection(entity) &&
                    EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    centroid += transform.m_Position;
                    count++;
                }
            }

            if (count == 0)
            {
                // Selekcija bez objekata: kopiraju se samo površine/ograde,
                // centroid iz njihovih poligona i sredina krivih.
                float2 surfaceCentroid = float2.zero;
                int surfaceCount = 0;
                foreach (Entity area in m_SelectedSurfaces)
                {
                    if (TryGetSurfaceCentroid(area, out float2 areaCentroid))
                    {
                        surfaceCentroid += areaCentroid;
                        surfaceCount++;
                    }
                }

                foreach (Entity lane in m_SelectedLanes)
                {
                    if (EntityManager.Exists(lane) &&
                        !EntityManager.HasComponent<Owner>(lane) &&
                        EntityManager.TryGetComponent(lane, out Game.Net.Curve laneCurve))
                    {
                        surfaceCentroid += LaneMidpoint(laneCurve.m_Bezier).xz;
                        surfaceCount++;
                    }
                }

                // Mreže: pokretni skup čvorova ulazi u centroid.
                foreach (Entity node in BuildMovingNodeSet())
                {
                    if (EntityManager.TryGetComponent(node, out Game.Net.Node netNode))
                    {
                        surfaceCentroid += netNode.m_Position.xz;
                        surfaceCount++;
                    }
                }

                if (surfaceCount == 0)
                {
                    return;
                }

                surfaceCentroid /= surfaceCount;
                centroid = new float3(surfaceCentroid.x, 0f, surfaceCentroid.y);
            }
            else
            {
                centroid /= count;
            }

            TerrainHeightData copyHeightData = m_TerrainSystem.GetHeightData();
            Unity.Mathematics.Random previewRandom = RandomSeed.Next().GetRandom(0);

            foreach (Entity entity in m_Selected)
            {
                if (!EntityManager.Exists(entity) ||
                    OwnerInSelection(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform) ||
                    !EntityManager.TryGetComponent(entity, out PrefabRef prefabRef))
                {
                    continue;
                }

                // Stvarna visina propa iznad terena — čuva se da nalepljeni bude na istoj visini kao original.
                float heightOffset = transform.m_Position.y - TerrainUtils.SampleHeight(ref copyHeightData, transform.m_Position);
                bool hadTree = EntityManager.TryGetComponent(entity, out Game.Objects.Tree tree);
                bool hasSeed = EntityManager.TryGetComponent(entity, out PseudoRandomSeed seed);
                bool hasCustomColor = TryGetCustomColor(entity, out Game.Rendering.ColorSet customColor);

                m_Clipboard.Add(new ClipboardItem
                {
                    m_Prefab = prefabRef.m_Prefab,
                    m_Offset = transform.m_Position - centroid,
                    m_Rotation = transform.m_Rotation,
                    m_HeightOffset = heightOffset,
                    m_Diameter = GetPrefabDiameter(prefabRef.m_Prefab),
                    m_HadTree = hadTree,
                    m_Tree = tree,
                    m_HasSeed = hasSeed,
                    m_Seed = hasSeed ? seed.m_Seed : (ushort)0,
                    m_HasCustomColor = hasCustomColor,
                    m_CustomColor = customColor,
                    m_PreviewSeed = previewRandom.NextInt(),
                    m_SurfaceSigs = CaptureLotSigsForSnapshot(entity, transform),
                });
            }

            CaptureSurfaces(centroid);
            CaptureLanes(centroid);
            CaptureNetworkEdges(centroid);

            Mod.Log.Info($"Copaste: copied {m_Clipboard.Count} objects, {m_ClipboardAreas.Count} surfaces, {m_ClipboardLanes.Count} fences, {m_ClipboardNetEdges.Count} road segments");
        }

        private void CreatePasteDefinitions(float3 anchor)
        {
            EntityCommandBuffer buffer = m_ToolOutputBarrier.CreateCommandBuffer();
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            Unity.Mathematics.Random random = RandomSeed.Next().GetRandom(0);
            float baseDelta = GetAnchorHeightDelta(anchor, ref heightData);
            m_LastPreview.Clear();

            // "Original" izgled: nalepljeni prop preuzima seed (boju/varijaciju) i
            // custom boju originala. "Random varijacije": igra bira nasumično.
            bool keepLook = Mod.Settings == null || !Mod.Settings.RandomPasteVariation;

            foreach (ClipboardItem item in m_Clipboard)
            {
                float3 position = anchor + item.m_Offset;
                position.y = TerrainUtils.SampleHeight(ref heightData, position) + item.m_HeightOffset + baseDelta + m_PasteHeightBoost;
                m_LastPreview.Add(new PastedRecord
                {
                    m_Prefab = item.m_Prefab,
                    m_Position = position,
                    m_HadTree = item.m_HadTree,
                    m_Tree = item.m_Tree,
                    m_HasSeed = keepLook && item.m_HasSeed,
                    m_Seed = item.m_Seed,
                    m_HasCustomColor = keepLook && item.m_HasCustomColor,
                    m_CustomColor = item.m_CustomColor,

                    // Plac prati Paste look: "Original" = tačna kopija placa
                    // izvora; "Random" = construction-ov nasumični fabrički.
                    m_SurfaceSigs = keepLook ? item.m_SurfaceSigs : null,
                });

                Entity definitionEntity = buffer.CreateEntity();

                CreationDefinition creation = default;
                creation.m_Prefab = item.m_Prefab;

                // Fiksan seed po stavci SAMO kad imamo original seed koji će ga posle
                // pregaziti (stabilan preview bez posledica). Stavke bez sačuvanog
                // seed-a (stari blueprinti) i random mod dobijaju svež seed po stamp-u
                // — inače bi svi stampovi bili identični klonovi.
                creation.m_RandomSeed = keepLook && item.m_HasSeed ? item.m_PreviewSeed : random.NextInt();

                ObjectDefinition definition = default;
                definition.m_ParentMesh = -1;
                definition.m_Position = position;
                definition.m_Rotation = item.m_Rotation;
                definition.m_LocalPosition = position;
                definition.m_LocalRotation = item.m_Rotation;
                definition.m_Probability = 100;
                definition.m_PrefabSubIndex = -1;
                definition.m_Scale = 1f;
                definition.m_Intensity = 1f;
                definition.m_Elevation = math.max(0f, item.m_HeightOffset + baseDelta + m_PasteHeightBoost);
                definition.m_Age = 0.5f;

                buffer.AddComponent(definitionEntity, creation);
                buffer.AddComponent(definitionEntity, definition);
                buffer.AddComponent(definitionEntity, default(Updated));
            }

            // Farbane površine: definicija = CreationDefinition + poligon (Node bafer).
            // GenerateAreasSystem od toga pravi Temp preview i finalnu površinu na apply.
            foreach (AreaClipboardItem area in m_ClipboardAreas)
            {
                if (area.m_NodeOffsets == null || area.m_NodeOffsets.Length < 3)
                {
                    continue;
                }

                Entity definitionEntity = buffer.CreateEntity();

                CreationDefinition creation = default;
                creation.m_Prefab = area.m_Prefab;
                creation.m_RandomSeed = random.NextInt();
                buffer.AddComponent(definitionEntity, creation);

                DynamicBuffer<Game.Areas.Node> nodes = buffer.AddBuffer<Game.Areas.Node>(definitionEntity);
                float2 areaCentroid = float2.zero;
                foreach (float2 offset in area.m_NodeOffsets)
                {
                    float2 xz = anchor.xz + offset;
                    float3 nodePosition = new float3(xz.x, 0f, xz.y);
                    nodePosition.y = TerrainUtils.SampleHeight(ref heightData, nodePosition);
                    nodes.Add(new Game.Areas.Node { m_Position = nodePosition, m_Elevation = 0f });
                    areaCentroid += xz;
                }

                areaCentroid /= area.m_NodeOffsets.Length;
                buffer.AddComponent(definitionEntity, default(Updated));

                m_LastPreview.Add(new PastedRecord
                {
                    m_Prefab = area.m_Prefab,
                    m_Position = new float3(areaCentroid.x, 0f, areaCentroid.y),
                    m_IsArea = true,
                });
            }

            // Ograde: CreationDefinition{container, m_SubPrefab=ograda} + NetCourse.
            CreateLaneDefinitions(buffer, anchor, ref heightData, ref random, keepLook, baseDelta);

            // Putevi: isti pipeline, sa DisableMerge (inače se kopija raspadne).
            CreateNetworkDefinitions(buffer, anchor, ref heightData, ref random, baseDelta);
        }

        // Ctrl+klik: vrati sledećeg kandidata oko tačke pogotka. Ponovljeni klik
        // na (približno) isto mesto ide na sledeći prop ukrug — tako se dohvataju
        // propovi delimično uronjeni u veće objekte koje raycast uvek pogađa prvi.
        private void CollectCycleCandidates(EntityQuery query, float3 point, List<Entity> candidates, List<float> distances)
        {
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms = query.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                // Puna 3D udaljenost — inače prop na trotoaru "preglasa" prop na
                // krovu iako je kursor na krovu.
                float distance = math.distance(transforms[i].m_Position, point);

                // Gruba odbrana prvo — GetDiameter/IsCopyable su skupi po entitetu,
                // a upit pokriva sve objekte na mapi.
                if (distance > 30f)
                {
                    continue;
                }

                float radius = math.max(2.5f, (GetDiameter(entities[i]) * 0.5f) + 0.5f);
                if (distance > radius || !IsCopyable(entities[i]))
                {
                    continue;
                }

                // Umetni sortirano po udaljenosti (kandidata je šačica).
                int insertAt = distances.Count;
                for (int j = 0; j < distances.Count; j++)
                {
                    if (distance < distances[j])
                    {
                        insertAt = j;
                        break;
                    }
                }

                candidates.Insert(insertAt, entities[i]);
                distances.Insert(insertAt, distance);
            }

            entities.Dispose();
            transforms.Dispose();
        }

        private Entity CyclePick(float3 point, Entity topHit)
        {
            List<Entity> candidates = new List<Entity>();
            List<float> distances = new List<float>();

            if (SelectProps || SelectTrees || SelectDecals)
            {
                CollectCycleCandidates(m_PropQuery, point, candidates, distances);

                // Building elements: i zgradini propovi su klikabilni, pa ih i
                // cycle mora videti — inače zgrada "ukrade" klik na klupu
                // (IsCopyable u kolektoru filtrira regenerišuće i čipove).
                if (SelectBuildingProps)
                {
                    CollectCycleCandidates(m_OwnedPropQuery, point, candidates, distances);
                }
            }

            if (SelectBuildings)
            {
                CollectCycleCandidates(m_BuildingQuery, point, candidates, distances);
            }

            if (candidates.Count == 0)
            {
                return topHit;
            }

            // Isto mesto kao prošli Ctrl+klik → sledeći kandidat; novo mesto → najbliži.
            if (math.distancesq(point.xz, m_CyclePoint.xz) < 1f)
            {
                m_CycleIndex++;
            }
            else
            {
                m_CycleIndex = 0;
            }

            m_CyclePoint = point;
            return candidates[m_CycleIndex % candidates.Count];
        }

        // Nevidljiva spawn tačka zgrade: SpawnLocation bez mesha na prefabu.
        // Klupe i stolice takođe nose SpawnLocation (mesto za sedenje) ali imaju
        // mesh — one su normalni propovi i moraju ostati selektabilne.
        // Ranija zabrana cele komponente uklanjala je i njih (GardenBench02).
        private bool IsInvisibleSpawnPoint(Entity entity)
        {
            if (!EntityManager.HasComponent<Game.Objects.SpawnLocation>(entity))
            {
                return false;
            }

            return !EntityManager.TryGetComponent(entity, out PrefabRef prefabRef) ||
                !EntityManager.TryGetBuffer(prefabRef.m_Prefab, true, out DynamicBuffer<Game.Prefabs.SubMesh> subMeshes) ||
                subMeshes.Length == 0;
        }

        // Dijagnostika: koja tačno IsCopyable provera obara entitet (za log).
        private string DescribeSelectionBlock(Entity entity)
        {
            List<string> reasons = new List<string>();
            if (!EntityManager.HasComponent<Game.Objects.Object>(entity)) reasons.Add("no Object");
            if (!EntityManager.HasComponent<Game.Objects.Transform>(entity)) reasons.Add("no Transform");
            if (!EntityManager.HasComponent<PrefabRef>(entity)) reasons.Add("no PrefabRef");
            if (IsInvisibleSpawnPoint(entity)) reasons.Add("invisible spawn point (SpawnLocation, no mesh)");
            if (EntityManager.HasComponent<Game.Objects.Marker>(entity)) reasons.Add("Marker");
            if (EntityManager.HasComponent<Game.Objects.UtilityObject>(entity)) reasons.Add("UtilityObject");
            if (EntityManager.HasComponent<Game.Objects.Placeholder>(entity)) reasons.Add("Placeholder");
            if (IsRegeneratingSubElement(entity)) reasons.Add("regenerating lot-decoration (deep-owned)");
            if (!IsCategoryEnabled(entity)) reasons.Add("category chip off for its type");
            if (EntityManager.HasComponent<Game.Buildings.Extension>(entity)) reasons.Add("Extension");
            if (EntityManager.HasComponent<Game.Vehicles.Vehicle>(entity)) reasons.Add("Vehicle");
            if (EntityManager.HasComponent<Game.Objects.Moving>(entity)) reasons.Add("Moving");
            if (EntityManager.HasComponent<Game.Creatures.Creature>(entity)) reasons.Add("Creature");
            if (!SelectBuildingProps && IsOwnedByBuilding(entity)) reasons.Add("building-owned (Building elements off)");
            if (EntityManager.HasComponent<Temp>(entity)) reasons.Add("Temp");
            if (EntityManager.HasComponent<Deleted>(entity)) reasons.Add("Deleted");
            return reasons.Count > 0 ? string.Join(", ", reasons.ToArray()) : "no blocking check?!";
        }

        private bool IsCopyable(Entity entity)
        {
            return EntityManager.HasComponent<Game.Objects.Object>(entity) &&
                EntityManager.HasComponent<Game.Objects.Transform>(entity) &&
                EntityManager.HasComponent<PrefabRef>(entity) &&
                !IsInvisibleSpawnPoint(entity) &&
                !EntityManager.HasComponent<Game.Objects.Marker>(entity) &&
                !EntityManager.HasComponent<Game.Objects.UtilityObject>(entity) &&
                !EntityManager.HasComponent<Game.Objects.Placeholder>(entity) &&
                !IsRegeneratingSubElement(entity) &&
                IsCategoryEnabled(entity) &&
                !EntityManager.HasComponent<Game.Buildings.Extension>(entity) &&
                !EntityManager.HasComponent<Game.Vehicles.Vehicle>(entity) &&
                !EntityManager.HasComponent<Game.Objects.Moving>(entity) &&
                // Cim koji STOJI nema Moving — živa bića se nikad ne selektuju.
                !EntityManager.HasComponent<Game.Creatures.Creature>(entity) &&
                // Building elements ugašen = ni KLIK ne bira ništa zgradino
                // (elementi na putevima nisu obuhvaćeni — lanac ne vodi do zgrade).
                (SelectBuildingProps || !IsOwnedByBuilding(entity)) &&
                !EntityManager.HasComponent<Temp>(entity) &&
                !EntityManager.HasComponent<Deleted>(entity);
        }

        private void Highlight(Entity entity)
        {
            if (EntityManager.Exists(entity))
            {
                EntityManager.AddComponent<Highlighted>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }
        }

        private void Unhighlight(Entity entity)
        {
            if (EntityManager.Exists(entity))
            {
                EntityManager.RemoveComponent<Highlighted>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }
        }

        private void ClearSelection()
        {
            EndAlignSession();
            m_SelectedSurfaces.Clear();
            m_SelectedLanes.Clear();
            m_SelectedNodes.Clear();
            m_SelectedNetEdges.Clear();
            m_StickyHandleIndex = -1;
            m_StickyHandleEntity = Entity.Null;
            foreach (Entity entity in m_Selected)
            {
                if (entity != m_HoverEntity)
                {
                    Unhighlight(entity);
                }
            }

            m_Selected.Clear();
        }

        // Selekcija mora da prati čipove. Filteri se konsultuju samo pri
        // HVATANJU, pa je gašenje čipa ostavljalo već selektovane entitete u
        // selekciji: panel tvrdi da putevi nisu selektabilna kategorija, a Del
        // ih i dalje buldožira i brojač ih i dalje broji.
        private int m_LastFilterMask = -1;

        private static int CurrentSelectionFilterMask()
        {
            return (SelectProps ? 1 : 0) | (SelectTrees ? 2 : 0) | (SelectDecals ? 4 : 0) |
                (SelectSurfaces ? 8 : 0) | (SelectBuildings ? 16 : 0) |
                (SelectFences ? 32 : 0) | (SelectNetworks ? 64 : 0) |
                (SelectBuildingProps ? 128 : 0);
        }

        private void PurgeSelectionForDisabledFilters()
        {
            int mask = CurrentSelectionFilterMask();
            if (mask == m_LastFilterMask)
            {
                return;
            }

            bool first = m_LastFilterMask < 0;
            m_LastFilterMask = mask;
            if (first)
            {
                return;
            }

            if (!SelectNetworks && (m_SelectedNodes.Count > 0 || m_SelectedNetEdges.Count > 0))
            {
                m_SelectedNodes.Clear();
                m_SelectedNetEdges.Clear();
                EndAlignSession();
                m_StickyHandleIndex = -1;
                m_StickyHandleEntity = Entity.Null;
            }

            if (!SelectFences && m_SelectedLanes.Count > 0)
            {
                m_SelectedLanes.Clear();
                m_StickyHandleIndex = -1;
                m_StickyHandleEntity = Entity.Null;
            }

            if (!SelectSurfaces)
            {
                m_SelectedSurfaces.Clear();
            }

            for (int i = m_Selected.Count - 1; i >= 0; i--)
            {
                Entity entity = m_Selected[i];

                // Ista kapija kao pri hvatanju (IsCopyable): i "Building
                // elements" čip — gašenje mora da izbaci zgradine elemente iz
                // selekcije, inače ih Del i dalje briše.
                if (EntityManager.Exists(entity) && IsCategoryEnabled(entity) &&
                    (SelectBuildingProps || !IsOwnedByBuilding(entity)))
                {
                    continue;
                }

                if (entity != m_HoverEntity)
                {
                    Unhighlight(entity);
                }

                m_Selected.RemoveAt(i);
            }
        }

        public void ToggleTool()
        {
            if (m_ToolSystem == null)
            {
                return;
            }

            if (m_ToolSystem.activeTool == this)
            {
                m_ToolSystem.activeTool = m_DefaultToolSystem;
            }
            else
            {
                m_ToolSystem.selected = Entity.Null;
                m_ToolSystem.activeTool = this;
            }
        }

        public int UndoCount => m_UndoStack.Count;

        private bool ToolIsActive => m_ToolSystem != null && m_ToolSystem.activeTool == this;

        // Akcije koje poziva UI panel (dugmad) — ekvivalenti prečica.
        public void TriggerCopy()
        {
            if (ToolIsActive && m_Mode == Mode.Select && CopyableSelectedCount > 0)
            {
                CopySelection();
            }
        }

        public void TriggerPaste()
        {
            if (!ToolIsActive || m_Mode == Mode.Relocate)
            {
                return;
            }

            if (m_Mode == Mode.Paste)
            {
                ExitPasteMode();
                return;
            }

            if (ClipboardCount > 0)
            {
                EnterPasteMode();
            }
        }

        // Poziva UI posle učitavanja blueprinta: preview mora iznova, sa nultim pomakom visine.
        public void RefreshPastePreview()
        {
            if (m_Mode == Mode.Paste)
            {
                m_PasteDirty = true;
                m_PasteHeightBoost = 0f;
                m_LastPreview.Clear();
                ResetRoadSnap();
            }
        }

        private bool m_PreviousIgnoreErrors;

        // Jedinstven ulazak u paste mod — čisti SVA stanja selekcionog moda.
        private void EnterPasteMode()
        {
            // Aktivna vuča ručke se uredno završava PRE promene moda — posle
            // prelaska nema ko da detektuje puštanje tastera.
            if (m_HandleDragging)
            {
                EndHandleDrag();
            }

            EndAlignSession();
            CancelMarquee();
            if (m_HoverEntity != Entity.Null && !m_Selected.Contains(m_HoverEntity))
            {
                Unhighlight(m_HoverEntity);
            }

            m_HoverEntity = Entity.Null;
            m_HeightPickArmed = false;
            m_AlignPickArmed = false;
            m_LeftHeldOnProp = false;
            m_MoveDragging = false;
            m_MoveOffsetsPending = false;
            m_MoveItems.Clear();
            m_MoveSurfaceItems.Clear();
            m_MoveLaneItems.Clear();
            m_NetMoveActive = false;
            m_Mode = Mode.Paste;
            m_PasteDirty = true;
            m_PasteHeightBoost = 0f;
            m_LastPreview.Clear();
            m_PreviousIgnoreErrors = m_ToolSystem.ignoreErrors;
            ResetRoadSnap();
        }

        // Jedinstven izlazak — vraća zatečeno ignoreErrors stanje (npr. Anarchy moda).
        private void ExitPasteMode()
        {
            m_Mode = Mode.Select;
            m_PasteDirty = false;
            if (m_ToolSystem != null)
            {
                m_ToolSystem.ignoreErrors = m_PreviousIgnoreErrors;
            }
        }

        public void TriggerDelete()
        {
            if (ToolIsActive && m_Mode == Mode.Select && !m_MoveDragging && !m_RightDragging &&
                DeletableSelectedCount > 0)
            {
                DeleteSelection();
            }
        }

        // Panel dugmad NE izvršavaju istoriju odmah: UI klik stiže u fazi
        // frejma u kojoj igra ZABRANJUJE CreateCommandBuffer ("Trying to
        // create EntityCommandBuffer when it's not allowed!"), a rekreacija
        // puteva ide kroz definicije i bafer. Klik samo zakaže korak, OnUpdate
        // ga odigra u prvom sledećem frejmu — u dozvoljenoj fazi.
        private int m_PendingUndoSteps;
        private int m_PendingRedoSteps;

        private void RunPendingHistorySteps()
        {
            bool gestureActive = m_Mode == Mode.Relocate || m_MoveDragging || m_RightDragging || m_MarqueeActive || m_HandleDragging;
            if (gestureActive)
            {
                m_PendingUndoSteps = 0;
                m_PendingRedoSteps = 0;
                return;
            }

            while (m_PendingUndoSteps > 0)
            {
                m_PendingUndoSteps--;
                Undo();
                if (m_Mode == Mode.Paste)
                {
                    m_PasteDirty = true;
                }
            }

            while (m_PendingRedoSteps > 0)
            {
                m_PendingRedoSteps--;
                Redo();
            }
        }

        public void TriggerUndo()
        {
            // Tokom Relocate-a undo bi pojeo zapis samog premeštanja i teleportovao
            // zgradu — panel akcije su zamrznute dok premeštanje traje. Isto i
            // tokom aktivnog drag-a/rotacije (zapis sopstvenog poteza).
            if (ToolIsActive && m_Mode != Mode.Relocate && !m_MoveDragging && !m_RightDragging)
            {
                m_PendingUndoSteps++;
            }
        }

        public void TriggerSelectSame()
        {
            ToggleSameFilter();
        }

        public void TriggerSnapGround()
        {
            if (!ToolIsActive || m_Mode == Mode.Relocate)
            {
                return;
            }

            if (m_Mode == Mode.Paste)
            {
                m_PasteHeightBoost = 0f;
                m_PasteDirty = true;
            }
            else if (SelectionHasHeightTargets())
            {
                PushTransformUndo();
                SnapSelectionToGround();
            }
        }

        public void TriggerRotate(int degrees)
        {
            if (!ToolIsActive)
            {
                return;
            }

            if (m_Mode == Mode.Relocate)
            {
                return;
            }

            float angle = math.radians(degrees);
            if (m_Mode == Mode.Paste)
            {
                RotateClipboard(angle);
            }
            else if (SelectionHasTransformTargets())
            {
                m_RotationCenter = GetSelectionCenter();
                PushTransformUndo();
                RotateSelection(angle);

                // Dugme je single-shot — nema release događaja koji bi odradio
                // settle kao RMB rotacija, pa pun update ide odmah.
                // Samo gotove zgrade (under-construction nisu ni rotirane).
                foreach (Entity entity in m_Selected)
                {
                    if (IsBuilding(entity) && IsMovableBuilding(entity))
                    {
                        SettleBuilding(entity);
                    }
                }

                SettleSurfaces();
                SettleLanes();
                SettleNetworks();
            }
        }

        public void TriggerHeight(int steps)
        {
            if (!ToolIsActive || m_Mode == Mode.Relocate)
            {
                return;
            }

            float delta = steps * 0.5f;
            if (m_Mode == Mode.Paste)
            {
                m_PasteHeightBoost += delta;
                m_PasteDirty = true;
            }
            else if (SelectionHasHeightTargets())
            {
                PushTransformUndo();
                AdjustSelectionHeight(delta);
            }
        }

        private void FindAnarchyPreventOverride()
        {
            try
            {
                foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!assembly.GetName().Name.Contains("Anarchy"))
                    {
                        continue;
                    }

                    foreach (System.Type type in assembly.GetTypes())
                    {
                        if (type.Name == "PreventOverride" && typeof(IComponentData).IsAssignableFrom(type))
                        {
                            m_PreventOverrideType = new ComponentType(type);
                            m_HasPreventOverride = true;
                            Mod.Log.Info($"Anarchy PreventOverride found: {type.FullName}");
                            return;
                        }
                    }
                }

                Mod.Log.Info("Anarchy mod not detected; pasted props will not be protected from override");
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Anarchy PreventOverride scan failed: {e.Message}");
            }
        }

        // Nekoliko frejmova posle lepljenja: skini Overridden sa nalepljenih propova
        // i (ako je Anarchy prisutan) dodaj im PreventOverride da ostanu vidljivi.
        // Popravke jednog nalepljenog entiteta (anti-override + starost drveta).
        private void ApplyPastedFix(Entity entity, PastedRecord record)
        {
            if (!EntityManager.Exists(entity))
            {
                return;
            }

            // Površine nemaju seed/boje/drveće/overrides — zapis služi samo za undo.
            if (record.m_IsArea)
            {
                return;
            }

            // Ograda (container ivica): poseban tok — upis seed-a traži i
            // Updated da LaneSystem ponovo izvede varijaciju vidljivog lane-a.
            if (record.m_IsLane)
            {
                ApplyPastedLaneFix(entity, record);
                return;
            }

            // Segment puta: samo nadogradnje (drvoredi i sl.).
            if (record.m_IsNetEdge)
            {
                ApplyPastedNetEdgeFix(entity, record);
                return;
            }

            if (Mod.Settings.AnarchyPaste)
            {
                if (EntityManager.HasComponent<Overridden>(entity))
                {
                    EntityManager.RemoveComponent<Overridden>(entity);
                    EntityManager.AddComponent<BatchesUpdated>(entity);
                }

                if (m_HasPreventOverride && !EntityManager.HasComponent(entity, m_PreventOverrideType))
                {
                    EntityManager.AddComponent(entity, m_PreventOverrideType);
                }
            }

            // Nalepljeno drveće preuzima starost/stanje originala.
            if (record.m_HadTree &&
                EntityManager.TryGetComponent(entity, out Game.Objects.Tree currentTree) &&
                !currentTree.Equals(record.m_Tree))
            {
                EntityManager.SetComponentData(entity, record.m_Tree);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }

            // Varijacija boje/izgleda: preuzmi seed originala ("Original" mod).
            if (record.m_HasSeed &&
                EntityManager.TryGetComponent(entity, out PseudoRandomSeed currentSeed) &&
                currentSeed.m_Seed != record.m_Seed)
            {
                EntityManager.SetComponentData(entity, new PseudoRandomSeed(record.m_Seed));
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }

            // Custom boja (vanila Customization tab): prenesi ColorSet originala.
            if (record.m_HasCustomColor)
            {
                ApplyInstanceColors(entity, record.m_CustomColor);
            }
        }

        // Custom boja postoji ako prop ima UKLJUČEN CustomMeshColor bafer
        // (vanila Customization tab ga puni i enable-uje po instanci).
        private bool TryGetCustomColor(Entity entity, out Game.Rendering.ColorSet colorSet)
        {
            colorSet = default;
            if (!EntityManager.HasBuffer<Game.Rendering.CustomMeshColor>(entity) ||
                !EntityManager.IsComponentEnabled<Game.Rendering.CustomMeshColor>(entity))
            {
                return false;
            }

            DynamicBuffer<Game.Rendering.CustomMeshColor> custom = EntityManager.GetBuffer<Game.Rendering.CustomMeshColor>(entity, true);
            if (custom.Length == 0)
            {
                return false;
            }

            colorSet = custom[0].m_ColorSet;
            return true;
        }

        private static bool ColorSetEquals(Game.Rendering.ColorSet a, Game.Rendering.ColorSet b)
        {
            return a.m_Channel0 == b.m_Channel0 && a.m_Channel1 == b.m_Channel1 && a.m_Channel2 == b.m_Channel2;
        }

        // Upiši custom ColorSet na entitet: CustomMeshColor (trajno, igra ga snima)
        // + MeshColor (trenutni prikaz) + BatchesUpdated. No-op ako prefab ne
        // podržava customizaciju (nema bafer u arhetipu).
        private void ApplyInstanceColors(Entity entity, Game.Rendering.ColorSet colorSet)
        {
            if (!EntityManager.HasBuffer<Game.Rendering.CustomMeshColor>(entity))
            {
                return;
            }

            DynamicBuffer<Game.Rendering.CustomMeshColor> custom = EntityManager.GetBuffer<Game.Rendering.CustomMeshColor>(entity);
            if (EntityManager.IsComponentEnabled<Game.Rendering.CustomMeshColor>(entity) &&
                custom.Length > 0 &&
                ColorSetEquals(custom[0].m_ColorSet, colorSet))
            {
                return; // već primenjeno (post-paste prolazi više frejmova)
            }

            int count = 1;
            if (EntityManager.HasBuffer<Game.Rendering.MeshColor>(entity))
            {
                DynamicBuffer<Game.Rendering.MeshColor> meshColors = EntityManager.GetBuffer<Game.Rendering.MeshColor>(entity);
                count = math.max(1, meshColors.Length);
                for (int i = 0; i < meshColors.Length; i++)
                {
                    meshColors[i] = new Game.Rendering.MeshColor { m_ColorSet = colorSet };
                }
            }

            custom.Clear();
            for (int i = 0; i < count; i++)
            {
                custom.Add(new Game.Rendering.CustomMeshColor { m_ColorSet = colorSet });
            }

            EntityManager.SetComponentEnabled<Game.Rendering.CustomMeshColor>(entity, true);
            EntityManager.AddComponent<BatchesUpdated>(entity);
        }

        private void RunPostPasteFix()
        {
            if (m_PostPasteFixFrames <= 0 || m_PostPasteFix == null || m_PostPasteFix.Count == 0)
            {
                return;
            }

            m_PostPasteFixFrames--;

            // Već razrešeni zapisi: održavaj popravke na TAČNO tom entitetu (jeftino).
            HashSet<Entity> claimed = new HashSet<Entity>();
            int unresolvedCount = 0;
            for (int j = 0; j < m_PostPasteFix.Count; j++)
            {
                PastedRecord record = m_PostPasteFix[j];
                if (record.m_Resolved != Entity.Null)
                {
                    claimed.Add(record.m_Resolved);
                    ApplyPastedFix(record.m_Resolved, record);
                }
                else
                {
                    unresolvedCount++;
                }
            }

            // Nerazrešeni zapisi: pronađi novonastale entitete — najviše JEDAN po zapisu,
            // da zatečeni istovetni propovi na istom mestu ne budu uvučeni u undo.
            if (unresolvedCount > 0)
            {
                float3 boundsMin = new float3(float.MaxValue);
                float3 boundsMax = new float3(float.MinValue);
                foreach (PastedRecord record in m_PostPasteFix)
                {
                    if (record.m_Resolved == Entity.Null)
                    {
                        boundsMin = math.min(boundsMin, record.m_Position);
                        boundsMax = math.max(boundsMax, record.m_Position);
                        ExpandBoundsForRecord(record, ref boundsMin, ref boundsMax);
                    }
                }

                boundsMin -= 0.5f;
                boundsMax += 0.5f;

                ResolvePastedFromQuery(m_PropQuery, boundsMin, boundsMax, claimed);

                // Zgrade u clipboardu: rezolucija i preko building upita.
                // Bez uslova na IncludeBuildings — paste je mogao da se desi pre gašenja.
                foreach (PastedRecord record in m_PostPasteFix)
                {
                    if (record.m_Resolved == Entity.Null && !record.m_IsArea && !record.m_IsLane && !record.m_IsNetEdge)
                    {
                        ResolvePastedFromQuery(m_BuildingQuery, boundsMin, boundsMax, claimed);
                        break;
                    }
                }

                // Površine: nemaju Transform — rezolucija po centroidu poligona.
                foreach (PastedRecord record in m_PostPasteFix)
                {
                    if (record.m_Resolved == Entity.Null && record.m_IsArea)
                    {
                        ResolvePastedAreas(boundsMin, boundsMax, claimed);
                        break;
                    }
                }

                // Ograde: rezolucija po prefabu + sredini krive container ivice.
                foreach (PastedRecord record in m_PostPasteFix)
                {
                    if (record.m_Resolved == Entity.Null && record.m_IsLane)
                    {
                        ResolvePastedLanes(boundsMin, boundsMax, claimed);
                        break;
                    }
                }

                // Putevi: rezolucija kroz net stablo po prefabu + sredini krive.
                foreach (PastedRecord record in m_PostPasteFix)
                {
                    if (record.m_Resolved == Entity.Null && record.m_IsNetEdge)
                    {
                        ResolvePastedNetEdges(boundsMin, boundsMax, claimed);
                        break;
                    }
                }
            }

            if (m_PostPasteFixFrames == 0)
            {
                LogPastedNetTopology(m_PostPasteFix);

                // Bez Clear — istu listu drži undo zapis (sa razrešenim entitetima).
                m_PostPasteFix = null;
                m_PostPasteExclude = null;
                m_PostPasteNetPreCurves = null;
            }
        }

        // Popiši postojeće entitete koji po prefabu i poziciji odgovaraju
        // nalepljenim zapisima — kandidati koje rezolucija NE sme da usvoji.
        // Blueprint sme da bude ručno menjan: NaN i beskonačnost bi propali
        // kroz TryParse i odveli prop u nedefinisano mesto.
        private static bool IsFiniteBlueprintNumber(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && math.abs(value) < 1e6f;
        }

        // Svaka float vrednost iz blueprint fajla prolazi kroz OVO — NaN i
        // beskonačnost su ranije prolazili u sve linije osim objektnih.
        private static bool TryParseBlueprintFloat(string text, System.IFormatProvider inv, out float value)
        {
            return float.TryParse(text, System.Globalization.NumberStyles.Float, inv, out value) &&
                IsFiniteBlueprintNumber(value);
        }

        private HashSet<Entity> CollectPreStampMatches(List<PastedRecord> records, List<PreStampNetCurve> netPreCurves)
        {
            HashSet<Entity> exclude = new HashSet<Entity>();
            bool anyObject = false;
            bool anyArea = false;
            bool anyLane = false;
            bool anyNetEdge = false;
            float3 boundsMin = new float3(float.MaxValue);
            float3 boundsMax = new float3(float.MinValue);
            foreach (PastedRecord record in records)
            {
                boundsMin = math.min(boundsMin, record.m_Position);
                boundsMax = math.max(boundsMax, record.m_Position);
                anyArea |= record.m_IsArea;
                anyLane |= record.m_IsLane;
                anyNetEdge |= record.m_IsNetEdge;
                anyObject |= !record.m_IsArea && !record.m_IsLane && !record.m_IsNetEdge;
            }

            boundsMin -= 0.5f;
            boundsMax += 0.5f;

            if (anyObject)
            {
                CollectPreStampFromQuery(m_PropQuery, records, boundsMin, boundsMax, exclude);
                CollectPreStampFromQuery(m_BuildingQuery, records, boundsMin, boundsMax, exclude);
            }

            if (anyArea)
            {
                NativeArray<Entity> areas = m_SurfaceQuery.ToEntityArray(Allocator.Temp);
                NativeArray<PrefabRef> areaPrefabs = m_SurfaceQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
                for (int i = 0; i < areas.Length; i++)
                {
                    if (!EntityManager.TryGetBuffer(areas[i], true, out DynamicBuffer<Game.Areas.Node> nodes) ||
                        nodes.Length < 3)
                    {
                        continue;
                    }

                    float2 centroid = float2.zero;
                    for (int n = 0; n < nodes.Length; n++)
                    {
                        centroid += nodes[n].m_Position.xz;
                    }

                    centroid /= nodes.Length;
                    foreach (PastedRecord record in records)
                    {
                        if (record.m_IsArea &&
                            areaPrefabs[i].m_Prefab == record.m_Prefab &&
                            math.distancesq(centroid, record.m_Position.xz) <= 0.25f)
                        {
                            exclude.Add(areas[i]);
                            break;
                        }
                    }
                }

                areas.Dispose();
                areaPrefabs.Dispose();
            }

            if (anyLane)
            {
                CollectPreStampLanes(records, boundsMin, boundsMax, exclude);
            }

            if (anyNetEdge)
            {
                CollectPreStampNetEdges(records, boundsMin, boundsMax, exclude, netPreCurves);
            }

            return exclude;
        }

        private void CollectPreStampFromQuery(EntityQuery query, List<PastedRecord> records, float3 boundsMin, float3 boundsMax, HashSet<Entity> exclude)
        {
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms = query.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            NativeArray<PrefabRef> prefabRefs = query.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                float3 position = transforms[i].m_Position;
                if (math.any(position < boundsMin) || math.any(position > boundsMax))
                {
                    continue;
                }

                foreach (PastedRecord record in records)
                {
                    if (!record.m_IsArea &&
                        prefabRefs[i].m_Prefab == record.m_Prefab &&
                        math.distancesq(position, record.m_Position) <= 0.01f)
                    {
                        exclude.Add(entities[i]);
                        break;
                    }
                }
            }

            entities.Dispose();
            transforms.Dispose();
            prefabRefs.Dispose();
        }

        // Poveži nerazrešene paste zapise sa entitetima iz zadatog upita
        // (prefab + pozicija na 10 cm, najviše jedan entitet po zapisu).
        private void ResolvePastedFromQuery(EntityQuery query, float3 boundsMin, float3 boundsMax, HashSet<Entity> claimed)
        {
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms = query.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            NativeArray<PrefabRef> prefabRefs = query.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                float3 entityPosition = transforms[i].m_Position;
                if (math.any(entityPosition < boundsMin) || math.any(entityPosition > boundsMax) ||
                    claimed.Contains(entities[i]) ||
                    (m_PostPasteExclude != null && m_PostPasteExclude.Contains(entities[i])))
                {
                    continue;
                }

                for (int j = 0; j < m_PostPasteFix.Count; j++)
                {
                    PastedRecord record = m_PostPasteFix[j];
                    if (record.m_Resolved != Entity.Null ||
                        prefabRefs[i].m_Prefab != record.m_Prefab ||
                        math.distancesq(entityPosition, record.m_Position) > 0.01f)
                    {
                        continue;
                    }

                    record.m_Resolved = entities[i];
                    m_PostPasteFix[j] = record;
                    claimed.Add(entities[i]);
                    ApplyPastedFix(entities[i], record);

                    // Zgrade: pod-površine (pločnici) i pod-mreže (prilazi) nastaju
                    // SAMO kroz construction tok (BuildingConstructionSystem.CreateAreas/
                    // CreateNets). Direktno kreiranje ih preskače, pa "upalimo" izgradnju
                    // pri samom kraju — završi se za tren i igra sama izgradi sve delove.
                    // Radi se JEDNOM, ovde pri rezoluciji (ne u ApplyPastedFix koji se
                    // ponavlja 10 frejmova — ponovno dodavanje bi vrtelo izgradnju u krug).
                    if (IsBuilding(entities[i]) &&
                        !EntityManager.HasComponent<Game.Objects.UnderConstruction>(entities[i]))
                    {
                        EntityManager.AddComponentData(entities[i], new Game.Objects.UnderConstruction
                        {
                            m_NewPrefab = record.m_Prefab,
                            m_Progress = 250,
                            m_Speed = 200,
                        });

                        // Kopija nasleđuje brisanja placa izvora: kad construction
                        // izgradi fabričke površine, višak (one koje original
                        // nije imao) se briše po potpisima.
                        ScheduleSurfacePrune(entities[i], record.m_SurfaceSigs);
                        Mod.Log.Info($"Copaste: paste resolved building e{entities[i].Index}, construction kicked, sigs={(record.m_SurfaceSigs == null ? "none" : record.m_SurfaceSigs.Count.ToString())}");
                    }

                    break;
                }
            }

            entities.Dispose();
            transforms.Dispose();
            prefabRefs.Dispose();
        }

        // Rezolucija nalepljenih površina: prefab + centroid poligona na pola metra.
        private void ResolvePastedAreas(float3 boundsMin, float3 boundsMax, HashSet<Entity> claimed)
        {
            NativeArray<Entity> areas = m_SurfaceQuery.ToEntityArray(Allocator.Temp);
            NativeArray<PrefabRef> prefabRefs = m_SurfaceQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            for (int i = 0; i < areas.Length; i++)
            {
                if (claimed.Contains(areas[i]) ||
                    (m_PostPasteExclude != null && m_PostPasteExclude.Contains(areas[i])) ||
                    !EntityManager.TryGetBuffer(areas[i], true, out DynamicBuffer<Game.Areas.Node> nodes) ||
                    nodes.Length < 3)
                {
                    continue;
                }

                float2 centroid = float2.zero;
                for (int n = 0; n < nodes.Length; n++)
                {
                    centroid += nodes[n].m_Position.xz;
                }

                centroid /= nodes.Length;
                if (centroid.x < boundsMin.x - 1f || centroid.x > boundsMax.x + 1f ||
                    centroid.y < boundsMin.z - 1f || centroid.y > boundsMax.z + 1f)
                {
                    continue;
                }

                for (int j = 0; j < m_PostPasteFix.Count; j++)
                {
                    PastedRecord record = m_PostPasteFix[j];
                    if (!record.m_IsArea ||
                        record.m_Resolved != Entity.Null ||
                        prefabRefs[i].m_Prefab != record.m_Prefab ||
                        math.distancesq(centroid, record.m_Position.xz) > 0.25f)
                    {
                        continue;
                    }

                    record.m_Resolved = areas[i];
                    m_PostPasteFix[j] = record;
                    claimed.Add(areas[i]);

                    // Zatvoren poligon mora da nosi Complete flag — bez njega
                    // igrin surface alat površinu tretira kao nezavršenu (ne da edit).
                    if (EntityManager.TryGetComponent(areas[i], out Game.Areas.Area areaData) &&
                        (areaData.m_Flags & Game.Areas.AreaFlags.Complete) == 0)
                    {
                        areaData.m_Flags |= Game.Areas.AreaFlags.Complete;
                        EntityManager.SetComponentData(areas[i], areaData);
                        EntityManager.AddComponent<Updated>(areas[i]);
                    }

                    break;
                }
            }

            areas.Dispose();
            prefabRefs.Dispose();
        }

        private void PushUndo(UndoRecord record)
        {
            // Nova akcija — grana budućnosti se seče.
            m_RedoStack.Clear();

            m_UndoStack.Add(record);
            if (m_UndoStack.Count > kMaxUndo)
            {
                m_UndoStack.RemoveAt(0);
            }
        }

        // Puno stanje razrešenih nalepljenih OBJEKATA — za redo paste-a
        // (PastedRecord ne nosi rotaciju/elevaciju, pa se snima pre brisanja).
        private List<TransformSnapshot> SnapshotResolvedPasted(List<PastedRecord> records)
        {
            List<TransformSnapshot> snapshots = new List<TransformSnapshot>();
            if (records == null)
            {
                return snapshots;
            }

            foreach (PastedRecord record in records)
            {
                Entity entity = record.m_Resolved;
                if (record.m_IsArea || entity == Entity.Null ||
                    !EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform) ||
                    !EntityManager.TryGetComponent(entity, out PrefabRef prefabRef))
                {
                    continue;
                }

                bool hadElevation = EntityManager.TryGetComponent(entity, out Game.Objects.Elevation elevation);
                bool hadTree = EntityManager.TryGetComponent(entity, out Game.Objects.Tree tree);
                bool hasSeed = EntityManager.TryGetComponent(entity, out PseudoRandomSeed seed);
                bool hasCustomColor = TryGetCustomColor(entity, out Game.Rendering.ColorSet customColor);
                snapshots.Add(new TransformSnapshot
                {
                    m_Entity = entity,
                    m_Owner = EntityManager.TryGetComponent(entity, out Owner snapOwner) ? snapOwner.m_Owner : Entity.Null,
                    m_Prefab = prefabRef.m_Prefab,
                    m_Transform = transform,
                    m_HadElevation = hadElevation,
                    m_Elevation = hadElevation ? elevation.m_Elevation : 0f,
                    m_HadTree = hadTree,
                    m_Tree = tree,
                    m_HasSeed = hasSeed,
                    m_Seed = hasSeed ? seed.m_Seed : (ushort)0,
                    m_HasCustomColor = hasCustomColor,
                    m_CustomColor = customColor,
                    m_SurfaceSigs = CaptureLotSigsForSnapshot(entity, transform),
                });
            }

            return snapshots;
        }

        // Isto za nalepljene POVRŠINE (poligoni preko postojećeg snimača).
        private List<SurfaceSnapshot> SnapshotResolvedPastedAreas(List<PastedRecord> records)
        {
            List<Entity> entities = new List<Entity>();
            if (records != null)
            {
                foreach (PastedRecord record in records)
                {
                    if (record.m_IsArea && record.m_Resolved != Entity.Null)
                    {
                        entities.Add(record.m_Resolved);
                    }
                }
            }

            return SnapshotSurfaces(entities);
        }

        // Snapshot TRENUTNOG stanja entiteta iz datog zapisa — za redo/undo simetriju.
        private List<TransformSnapshot> SnapshotEntities(List<TransformSnapshot> reference)
        {
            List<TransformSnapshot> snapshots = new List<TransformSnapshot>(reference.Count);
            foreach (TransformSnapshot source in reference)
            {
                Entity entity = source.m_Entity;
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                bool hadElevation = EntityManager.TryGetComponent(entity, out Game.Objects.Elevation elevation);
                bool hadTree = EntityManager.TryGetComponent(entity, out Game.Objects.Tree tree);
                bool hasSeed = EntityManager.TryGetComponent(entity, out PseudoRandomSeed seed);
                bool hasCustomColor = TryGetCustomColor(entity, out Game.Rendering.ColorSet customColor);
                snapshots.Add(new TransformSnapshot
                {
                    m_Entity = entity,
                    m_Owner = EntityManager.TryGetComponent(entity, out Owner snapOwner) ? snapOwner.m_Owner : Entity.Null,
                    m_Prefab = source.m_Prefab,
                    m_Transform = transform,
                    m_HadElevation = hadElevation,
                    m_Elevation = hadElevation ? elevation.m_Elevation : 0f,
                    m_HadTree = hadTree,
                    m_Tree = tree,
                    m_HasSeed = hasSeed,
                    m_Seed = hasSeed ? seed.m_Seed : (ushort)0,
                    m_HasCustomColor = hasCustomColor,
                    m_CustomColor = customColor,
                    m_SurfaceSigs = CaptureLotSigsForSnapshot(entity, transform),
                });
            }

            return snapshots;
        }

        // includeUnderConstruction: transform undo preskače zgrade u izgradnji
        // (ne transformišu se), ali DELETE undo mora da ih snimi — brišu se
        // kao i sve ostalo, pa undo mora da ima šta da vrati.
        private List<TransformSnapshot> SnapshotSelection(bool includeBuildings = true, bool includeUnderConstruction = false, bool includeBuildingOwned = true)
        {
            List<TransformSnapshot> snapshots = new List<TransformSnapshot>(m_Selected.Count);
            foreach (Entity entity in m_Selected)
            {
                if (IsBuilding(entity) &&
                    (!includeBuildings || (!includeUnderConstruction && !IsMovableBuilding(entity))))
                {
                    continue;
                }

                // Delete putanja preskače regenerišuće fabričke elemente —
                // snapshot ih ne sme snimiti (undo bi napravio duplikat
                // nikad obrisanog).
                if (!includeBuildingOwned && IsRegeneratingSubElement(entity))
                {
                    continue;
                }

                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform) ||
                    !EntityManager.TryGetComponent(entity, out PrefabRef prefabRef))
                {
                    continue;
                }

                bool hadElevation = EntityManager.TryGetComponent(entity, out Game.Objects.Elevation elevation);
                bool hadTree = EntityManager.TryGetComponent(entity, out Game.Objects.Tree tree);
                bool hasSeed = EntityManager.TryGetComponent(entity, out PseudoRandomSeed seed);
                bool hasCustomColor = TryGetCustomColor(entity, out Game.Rendering.ColorSet customColor);
                snapshots.Add(new TransformSnapshot
                {
                    m_Entity = entity,
                    m_Owner = EntityManager.TryGetComponent(entity, out Owner snapOwner) ? snapOwner.m_Owner : Entity.Null,
                    m_Prefab = prefabRef.m_Prefab,
                    m_Transform = transform,
                    m_HadElevation = hadElevation,
                    m_Elevation = hadElevation ? elevation.m_Elevation : 0f,
                    m_HadTree = hadTree,
                    m_Tree = tree,
                    m_HasSeed = hasSeed,
                    m_Seed = hasSeed ? seed.m_Seed : (ushort)0,
                    m_HasCustomColor = hasCustomColor,
                    m_CustomColor = customColor,
                    m_SurfaceSigs = CaptureLotSigsForSnapshot(entity, transform),
                });
            }

            return snapshots;
        }

        // Ima li selekcija bar jedan entitet na koji visinske operacije
        // (PgUp/PgDn, End, Match H) stvarno deluju — propovi i GOTOVE zgrade
        // Ima li selekcija išta što se stvarno transformiše: objekte, ili bar
        // jednu SAMOSTALNU površinu. Selekcija od samo zgradinih površina ne
        // pomera ništa (one se ne transformišu pojedinačno) — bez ovog gate-a
        // bi rotacija/nudge gurali prazan undo zapis i pojeli redo stek.
        private bool SelectionHasTransformTargets()
        {
            foreach (Entity entity in m_Selected)
            {
                // Zgrade u izgradnji se ne transformišu — same ne čine metu.
                if (EntityManager.Exists(entity) &&
                    (!IsBuilding(entity) || IsMovableBuilding(entity)))
                {
                    return true;
                }
            }

            foreach (Entity area in m_SelectedSurfaces)
            {
                if (EntityManager.Exists(area) && !EntityManager.HasComponent<Owner>(area))
                {
                    return true;
                }
            }

            foreach (Entity lane in m_SelectedLanes)
            {
                if (EntityManager.Exists(lane) && !EntityManager.HasComponent<Owner>(lane))
                {
                    return true;
                }
            }

            foreach (Entity node in m_SelectedNodes)
            {
                if (EntityManager.Exists(node))
                {
                    return true;
                }
            }

            foreach (Entity edge in m_SelectedNetEdges)
            {
                if (EntityManager.Exists(edge))
                {
                    return true;
                }
            }

            return false;
        }

        // (visina radi i za zgrade, ceo plac ide zajedno). Bez ovoga bi
        // selekcija bez ijedne mete gurala no-op undo zapis koji usput briše
        // redo stek.
        private bool SelectionHasHeightTargets()
        {
            foreach (Entity entity in m_Selected)
            {
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.HasComponent<Game.Objects.Transform>(entity))
                {
                    continue;
                }

                if (!IsBuilding(entity) || IsMovableBuilding(entity))
                {
                    return true;
                }
            }

            foreach (Entity lane in m_SelectedLanes)
            {
                if (EntityManager.Exists(lane) && !EntityManager.HasComponent<Owner>(lane))
                {
                    return true;
                }
            }

            foreach (Entity node in m_SelectedNodes)
            {
                if (EntityManager.Exists(node))
                {
                    return true;
                }
            }

            foreach (Entity edge in m_SelectedNetEdges)
            {
                if (EntityManager.Exists(edge))
                {
                    return true;
                }
            }

            return false;
        }

        // Broj tih meta za UI (gate visinskih dugmadi).
        public int HeightTargetCount
        {
            get
            {
                EnsureDerivedSelectionData();
                return m_CachedHeightTargetCount;
            }
        }

        private int ComputeHeightTargetCount()
        {
            {
                int count = 0;
                foreach (Entity entity in m_Selected)
                {
                    if (EntityManager.Exists(entity) &&
                        EntityManager.HasComponent<Game.Objects.Transform>(entity) &&
                        (!IsBuilding(entity) || IsMovableBuilding(entity)))
                    {
                        count++;
                    }
                }

                foreach (Entity lane in m_SelectedLanes)
                {
                    if (EntityManager.Exists(lane) && !EntityManager.HasComponent<Owner>(lane))
                    {
                        count++;
                    }
                }

                foreach (Entity node in m_SelectedNodes)
                {
                    if (EntityManager.Exists(node))
                    {
                        count++;
                    }
                }

                foreach (Entity edge in m_SelectedNetEdges)
                {
                    if (EntityManager.Exists(edge))
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        private void PushTransformUndo()
        {
            List<TransformSnapshot> snapshots = SnapshotSelection();
            List<SurfaceSnapshot> surfaces = SnapshotSurfaces(m_SelectedSurfaces);
            List<LaneSnapshot> lanes = SnapshotLanes(m_SelectedLanes);
            SnapshotNetworks(out List<NetNodeSnapshot> netNodes, out List<NetEdgeSnapshot> netEdges);
            if (snapshots.Count > 0 || surfaces.Count > 0 || lanes.Count > 0 || netNodes.Count > 0 || netEdges.Count > 0)
            {
                PushUndo(new UndoRecord { m_Kind = UndoKind.Transforms, m_Snapshots = snapshots, m_Surfaces = surfaces, m_Lanes = lanes, m_NetNodes = netNodes, m_NetEdges = netEdges });
            }

            // Početak transformacije = tačka u kojoj se pamti custom raspored
            // pod-propova zgrada (igra ih posle Updated vraća na prefab šablon).
            ResetSubPropTracking();
            foreach (Entity entity in m_Selected)
            {
                if (IsBuilding(entity) && IsMovableBuilding(entity))
                {
                    CaptureSubPropLayout(entity);
                }
            }
        }

        private void Undo()
        {
            EndAlignSession();
            if (m_UndoStack.Count == 0)
            {
                return;
            }

            UndoRecord record = m_UndoStack[m_UndoStack.Count - 1];
            m_UndoStack.RemoveAt(m_UndoStack.Count - 1);

            switch (record.m_Kind)
            {
                case UndoKind.Transforms:
                    // Trenutno stanje ide na redo stek pre vraćanja starog.
                    List<TransformSnapshot> redoSnapshots = SnapshotEntities(record.m_Snapshots);
                    List<SurfaceSnapshot> redoSurfaces = record.m_Surfaces != null
                        ? SnapshotSurfaceEntities(record.m_Surfaces)
                        : new List<SurfaceSnapshot>();
                    List<LaneSnapshot> redoLanes = record.m_Lanes != null
                        ? SnapshotLaneEntities(record.m_Lanes)
                        : new List<LaneSnapshot>();
                    List<NetNodeSnapshot> redoNetNodes = record.m_NetNodes != null
                        ? SnapshotNetNodeEntities(record.m_NetNodes)
                        : new List<NetNodeSnapshot>();
                    List<NetEdgeSnapshot> redoNetEdges = record.m_NetEdges != null
                        ? SnapshotNetEdgeEntities(record.m_NetEdges)
                        : new List<NetEdgeSnapshot>();
                    // Zapis ide na redo stek UVEK — i kad su svi snimci prazni
                    // (entiteti u međuvremenu obrisani). Preskakanje bi
                    // pomerilo stekove: sledeći Ctrl+Y bi odigrao SLEDEĆI
                    // zapis, pa bi "vrati pomeranje" ispalo brisanje.
                    m_RedoStack.Add(new UndoRecord { m_Kind = UndoKind.Transforms, m_Snapshots = redoSnapshots, m_Surfaces = redoSurfaces, m_Lanes = redoLanes, m_NetNodes = redoNetNodes, m_NetEdges = redoNetEdges });
                    if (m_RedoStack.Count > kMaxUndo)
                    {
                        m_RedoStack.RemoveAt(0);
                    }

                    ApplyTransformSnapshots(record.m_Snapshots);
                    if (record.m_Surfaces != null)
                    {
                        ApplySurfaceSnapshots(record.m_Surfaces);
                    }

                    if (record.m_Lanes != null)
                    {
                        ApplyLaneSnapshots(record.m_Lanes);
                    }

                    ApplyNetworkSnapshots(record.m_NetNodes, record.m_NetEdges);

                    break;

                case UndoKind.Delete:
                    // Zapis ide na redo stek PRE rekreacije — remap iz
                    // RecreateProp/RecreateSurface tada preveže snimke u njemu
                    // na nove entitete, pa redo može ponovo da ih obriše.
                    m_RedoStack.Add(record);
                    if (m_RedoStack.Count > kMaxUndo)
                    {
                        m_RedoStack.RemoveAt(0);
                    }

                    // Indeksna petlja, ne foreach: RecreateProp kroz remap upisuje
                    // u OVU istu listu (zapis je već na redo steku), a na net48
                    // i zamena elementa po indeksu invalidira aktivan enumerator.
                    for (int i = 0; i < record.m_Snapshots.Count; i++)
                    {
                        RecreateProp(record.m_Snapshots[i]);
                    }

                    if (record.m_Surfaces != null)
                    {
                        // Zgradine površine čiji je vlasnik TAKOĐE rekreiran u
                        // ovom zapisu se preskaču: construction je svežoj zgradi
                        // već izgradio plac (i dekorativne površine iz prefaba),
                        // pa bi vraćanje stare napravilo duplikat spawner-a.
                        // Remap iz RecreateProp je već prevezao m_Owner na novu
                        // zgradu, kao i m_Entity zgradinih snapshota.
                        HashSet<Entity> recreatedOwners = new HashSet<Entity>();
                        for (int i = 0; i < record.m_Snapshots.Count; i++)
                        {
                            if (EntityManager.HasComponent<Game.Prefabs.BuildingData>(record.m_Snapshots[i].m_Prefab))
                            {
                                recreatedOwners.Add(record.m_Snapshots[i].m_Entity);
                            }
                        }

                        for (int i = 0; i < record.m_Surfaces.Count; i++)
                        {
                            if (record.m_Surfaces[i].m_Owner != Entity.Null &&
                                recreatedOwners.Contains(record.m_Surfaces[i].m_Owner))
                            {
                                continue;
                            }

                            RecreateSurface(record.m_Surfaces[i]);
                        }
                    }

                    if (record.m_Lanes != null)
                    {
                        // Indeksna petlja: RecreateLane kroz remap piše u OVU listu.
                        BeginLaneRecreateBatch();
                        for (int i = 0; i < record.m_Lanes.Count; i++)
                        {
                            RecreateLane(record.m_Lanes[i]);
                        }
                    }

                    // Obrisane mreže: rekreacija kroz Permanent definicije sa
                    // zavarenim krajevima — raskrsnica se vraća SASTAVLJENA.
                    RecreateNetEdges(record.m_NetEdges);

                    break;

                case UndoKind.Paste:
                    // Pre brisanja: PUNO stanje nalepljenih (paste zapisi nemaju
                    // rotaciju ni poligone) — redo iz ovoga ponovo stvara.
                    record.m_Snapshots = SnapshotResolvedPasted(record.m_Pasted);
                    record.m_Surfaces = SnapshotResolvedPastedAreas(record.m_Pasted);
                    record.m_Lanes = SnapshotResolvedPastedLanes(record.m_Pasted);

                    // Ako su razrešenja u međuvremenu pomrla (put je obrisan pa
                    // vraćen undo-om kao NOV entitet), svež snimak je prazan —
                    // tada se čuva stari, da redo i dalje ume da ih sagradi.
                    List<NetEdgeSnapshot> previousNetEdges = record.m_NetEdges;
                    record.m_NetEdges = SnapshotResolvedPastedNetEdges(record.m_Pasted);
                    if ((record.m_NetEdges == null || record.m_NetEdges.Count == 0) &&
                        previousNetEdges != null && previousNetEdges.Count > 0)
                    {
                        record.m_NetEdges = previousNetEdges;
                    }

                    Mod.Log.Info($"Copaste: undo paste snapshot: {record.m_NetEdges?.Count ?? 0} net edges from {record.m_Pasted?.Count ?? 0} records");

                    DeletePastedEntities(record.m_Pasted, record.m_PastedExclude, record.m_PastedPreCurves);

                    // Ako se fixup za taj stamp još vrti, prekini ga — entiteti su upravo obrisani.
                    if (ReferenceEquals(record.m_Pasted, m_PostPasteFix))
                    {
                        m_PostPasteFix = null;
                        m_PostPasteFixFrames = 0;
                        m_PostPasteExclude = null;
                        m_PostPasteNetPreCurves = null;
                    }

                    m_RedoStack.Add(record);
                    if (m_RedoStack.Count > kMaxUndo)
                    {
                        m_RedoStack.RemoveAt(0);
                    }

                    break;
            }

            Mod.Log.Info($"Copaste: undo ({record.m_Kind})");
        }

        // Vrati transformacije iz snapshota (deli je undo i redo).
        private void ApplyTransformSnapshots(List<TransformSnapshot> snapshots)
        {
            foreach (TransformSnapshot snapshot in snapshots)
            {
                if (!EntityManager.Exists(snapshot.m_Entity))
                {
                    continue;
                }

                // Zgrade idu kroz pod-tree putanju — prilazi i pločnici prate.
                if (IsBuilding(snapshot.m_Entity))
                {
                    // Raspored pod-propova se snima PRE pomeranja (relativno na
                    // trenutni transform), settle ga posle nameće na novom mestu.
                    // Ako je fix prozor za ovu zgradu još aktivan, snimljeni
                    // raspored je merodavan — snimanje usred prozora bi uhvatilo
                    // igrin prefab raspored umesto igračevog.
                    if (!m_SubPropFixBuildings.Contains(snapshot.m_Entity))
                    {
                        CaptureSubPropLayout(snapshot.m_Entity);
                    }

                    // Pozicija koju zgrada NAPUŠTA — tu ostaju siročići
                    // (undo/redo je teleport, isti slučaj kao kraj relocate-a).
                    float3 leavingPosition = EntityManager.TryGetComponent(snapshot.m_Entity, out Game.Objects.Transform preMove)
                        ? preMove.m_Position
                        : snapshot.m_Transform.m_Position;

                    MoveBuildingTo(snapshot.m_Entity, snapshot.m_Transform);
                    SettleBuilding(snapshot.m_Entity);
                    SweepOrphansAround(leavingPosition, 48f);
                    continue;
                }

                EntityManager.SetComponentData(snapshot.m_Entity, snapshot.m_Transform);
                if (snapshot.m_HadElevation)
                {
                    if (EntityManager.HasComponent<Game.Objects.Elevation>(snapshot.m_Entity))
                    {
                        EntityManager.SetComponentData(snapshot.m_Entity, new Game.Objects.Elevation { m_Elevation = snapshot.m_Elevation });
                    }
                    else
                    {
                        EntityManager.AddComponentData(snapshot.m_Entity, new Game.Objects.Elevation { m_Elevation = snapshot.m_Elevation });
                    }
                }
                else if (EntityManager.HasComponent<Game.Objects.Elevation>(snapshot.m_Entity))
                {
                    EntityManager.RemoveComponent<Game.Objects.Elevation>(snapshot.m_Entity);
                }

                EntityManager.AddComponent<Updated>(snapshot.m_Entity);
                EntityManager.AddComponent<BatchesUpdated>(snapshot.m_Entity);
            }
        }

        // Redo (Ctrl+Y): vrati poslednju poništenu akciju — transformaciju,
        // brisanje ili paste.
        private void Redo()
        {
            EndAlignSession();
            if (m_RedoStack.Count == 0)
            {
                return;
            }

            UndoRecord record = m_RedoStack[m_RedoStack.Count - 1];
            m_RedoStack.RemoveAt(m_RedoStack.Count - 1);
            Mod.Log.Info($"Copaste: redo ({record.m_Kind}) start, netEdges={record.m_NetEdges?.Count ?? -1}");

            // Sopstveni catch: UI dugme zove Redo van OnUpdate try bloka, pa
            // bi izuzetak odavde inače nestao bez traga u našem logu.
            try
            {

            switch (record.m_Kind)
            {
                case UndoKind.Transforms:
                    // Trenutno stanje nazad na undo stek — direktno, ne kroz PushUndo
                    // (PushUndo bi obrisao ostatak redo steka).
                    List<TransformSnapshot> undoSnapshots = SnapshotEntities(record.m_Snapshots);
                    List<SurfaceSnapshot> undoSurfaces = record.m_Surfaces != null
                        ? SnapshotSurfaceEntities(record.m_Surfaces)
                        : new List<SurfaceSnapshot>();
                    List<LaneSnapshot> undoLanes = record.m_Lanes != null
                        ? SnapshotLaneEntities(record.m_Lanes)
                        : new List<LaneSnapshot>();
                    List<NetNodeSnapshot> undoNetNodes = record.m_NetNodes != null
                        ? SnapshotNetNodeEntities(record.m_NetNodes)
                        : new List<NetNodeSnapshot>();
                    List<NetEdgeSnapshot> undoNetEdges = record.m_NetEdges != null
                        ? SnapshotNetEdgeEntities(record.m_NetEdges)
                        : new List<NetEdgeSnapshot>();
                    // Uvek, kao i na undo strani: preskočen zapis bi pomerio
                    // stekove i sledeći Ctrl+Z bi odigrao pogrešan korak.
                    m_UndoStack.Add(new UndoRecord { m_Kind = UndoKind.Transforms, m_Snapshots = undoSnapshots, m_Surfaces = undoSurfaces, m_Lanes = undoLanes, m_NetNodes = undoNetNodes, m_NetEdges = undoNetEdges });
                    if (m_UndoStack.Count > kMaxUndo)
                    {
                        m_UndoStack.RemoveAt(0);
                    }

                    ApplyTransformSnapshots(record.m_Snapshots);
                    if (record.m_Surfaces != null)
                    {
                        ApplySurfaceSnapshots(record.m_Surfaces);
                    }

                    if (record.m_Lanes != null)
                    {
                        ApplyLaneSnapshots(record.m_Lanes);
                    }

                    ApplyNetworkSnapshots(record.m_NetNodes, record.m_NetEdges);

                    break;

                case UndoKind.Delete:
                    // Ponovo obriši iste (rekreirane) entitete; zapis nazad na
                    // undo stek — sledeći undo ih opet vraća.
                    m_UndoStack.Add(record);
                    if (m_UndoStack.Count > kMaxUndo)
                    {
                        m_UndoStack.RemoveAt(0);
                    }

                    foreach (TransformSnapshot snapshot in record.m_Snapshots)
                    {
                        if (EntityManager.Exists(snapshot.m_Entity))
                        {
                            m_Selected.Remove(snapshot.m_Entity);

                            // Isto kao prvi delete: potpis u registar, da sweep
                            // drži regenerisane kopije mrtvim i posle redo-a
                            // (undo→redo bi inače gubio potpis).
                            RecordDeletedSubProp(snapshot.m_Entity);
                            EntityManager.AddComponent<Deleted>(snapshot.m_Entity);
                        }
                    }

                    if (record.m_Surfaces != null)
                    {
                        foreach (SurfaceSnapshot surface in record.m_Surfaces)
                        {
                            if (EntityManager.Exists(surface.m_Entity))
                            {
                                m_SelectedSurfaces.Remove(surface.m_Entity);
                                DeleteSurfaceWithChildren(surface.m_Entity);
                            }
                        }
                    }

                    if (record.m_Lanes != null)
                    {
                        foreach (LaneSnapshot laneSnapshot in record.m_Lanes)
                        {
                            if (EntityManager.Exists(laneSnapshot.m_Entity))
                            {
                                m_SelectedLanes.Remove(laneSnapshot.m_Entity);
                                DeleteLaneWithNodes(laneSnapshot.m_Entity);
                            }
                        }
                    }

                    // Mreže: rekreacija je napravila NOVE entitete — nalaze se
                    // pozicionim fallback-om pa brišu.
                    RedeleteNetEdges(record.m_NetEdges);

                    break;

                case UndoKind.Paste:
                    // Zapis PRVO na undo stek — remap iz rekreacije tada preveže
                    // i m_Pasted razrešenja na nove entitete (sledeći undo briše
                    // baš njih).
                    m_UndoStack.Add(record);
                    if (m_UndoStack.Count > kMaxUndo)
                    {
                        m_UndoStack.RemoveAt(0);
                    }

                    // Indeksne petlje iz istog razloga kao kod undo brisanja:
                    // remap piše u liste zapisa koji je već na undo steku.
                    if (record.m_Snapshots != null)
                    {
                        for (int i = 0; i < record.m_Snapshots.Count; i++)
                        {
                            RecreateProp(record.m_Snapshots[i]);
                        }
                    }

                    if (record.m_Surfaces != null)
                    {
                        for (int i = 0; i < record.m_Surfaces.Count; i++)
                        {
                            RecreateSurface(record.m_Surfaces[i]);
                        }
                    }

                    if (record.m_Lanes != null)
                    {
                        BeginLaneRecreateBatch();
                        for (int i = 0; i < record.m_Lanes.Count; i++)
                        {
                            RecreateLane(record.m_Lanes[i]);
                        }
                    }

                    // Putevi: ponovo kroz definicije (Permanent), bez remapa —
                    // sledeći undo ih nalazi pozicionim fallback-om.
                    RecreateNetEdges(record.m_NetEdges);

                    break;
            }

            }
            catch (System.Exception e)
            {
                Mod.Log.Error($"Copaste: redo ({record.m_Kind}) FAILED: {e}");
            }

            Mod.Log.Info($"Copaste: redo ({record.m_Kind}) done");
        }

        public int RedoCount => m_RedoStack.Count;

        public void TriggerRedo()
        {
            if (ToolIsActive && m_Mode != Mode.Relocate && !m_MoveDragging && !m_RightDragging)
            {
                m_PendingRedoSteps++;
            }
        }

        // Isprazni clipboard (objekti + površine); iz paste moda vrati u selekciju.
        public void TriggerClearClipboard()
        {
            if (m_Mode == Mode.Relocate)
            {
                return;
            }

            InvalidatePendingNodeFixups();
            m_Clipboard.Clear();
            m_ClipboardAreas.Clear();
            m_ClipboardLanes.Clear();
            m_ClipboardNetEdges.Clear();
            ResetClipboardNetNodes(null, null, null, null);
            if (m_Mode == Mode.Paste)
            {
                ExitPasteMode();
            }
        }

        // Posle rekreacije (undo brisanja) stari entity ID u preostalim
        // undo/redo zapisima pokazuje na mrtav entitet — svi ti koraci bi
        // postali tihi no-op. Prevezivanje na novi entitet drži istoriju živom.
        private void RemapHistoryEntity(Entity oldEntity, Entity newEntity)
        {
            RemapHistoryStack(m_UndoStack, oldEntity, newEntity);
            RemapHistoryStack(m_RedoStack, oldEntity, newEntity);
        }

        private static void RemapHistoryStack(List<UndoRecord> stack, Entity oldEntity, Entity newEntity)
        {
            foreach (UndoRecord record in stack)
            {
                if (record.m_Snapshots != null)
                {
                    for (int i = 0; i < record.m_Snapshots.Count; i++)
                    {
                        // I m_Owner: prop/površina obrisana ZAJEDNO sa svojom
                        // zgradom mora posle rekreacije zgrade da pokazuje na
                        // NOVU zgradu — inače se vraća kao samostalna.
                        if (record.m_Snapshots[i].m_Entity == oldEntity ||
                            record.m_Snapshots[i].m_Owner == oldEntity)
                        {
                            TransformSnapshot snapshot = record.m_Snapshots[i];
                            if (snapshot.m_Entity == oldEntity)
                            {
                                snapshot.m_Entity = newEntity;
                            }

                            if (snapshot.m_Owner == oldEntity)
                            {
                                snapshot.m_Owner = newEntity;
                            }

                            record.m_Snapshots[i] = snapshot;
                        }
                    }
                }

                if (record.m_Surfaces != null)
                {
                    for (int i = 0; i < record.m_Surfaces.Count; i++)
                    {
                        if (record.m_Surfaces[i].m_Entity == oldEntity ||
                            record.m_Surfaces[i].m_Owner == oldEntity)
                        {
                            SurfaceSnapshot snapshot = record.m_Surfaces[i];
                            if (snapshot.m_Entity == oldEntity)
                            {
                                snapshot.m_Entity = newEntity;
                            }

                            if (snapshot.m_Owner == oldEntity)
                            {
                                snapshot.m_Owner = newEntity;
                            }

                            record.m_Surfaces[i] = snapshot;
                        }
                    }
                }

                if (record.m_Lanes != null)
                {
                    for (int i = 0; i < record.m_Lanes.Count; i++)
                    {
                        if (record.m_Lanes[i].m_Entity == oldEntity)
                        {
                            LaneSnapshot snapshot = record.m_Lanes[i];
                            snapshot.m_Entity = newEntity;
                            record.m_Lanes[i] = snapshot;
                        }
                    }
                }

                if (record.m_Pasted != null)
                {
                    for (int i = 0; i < record.m_Pasted.Count; i++)
                    {
                        if (record.m_Pasted[i].m_Resolved == oldEntity)
                        {
                            PastedRecord pasted = record.m_Pasted[i];
                            pasted.m_Resolved = newEntity;
                            record.m_Pasted[i] = pasted;
                        }
                    }
                }

                // Twin zaštita mora da prati rekreaciju: entitet koji je
                // POSTOJAO pri stampu pa je obrisan i vraćen undo-om vraća se
                // kao NOV ID. Bez ovoga bi stari ID ostao u skupu, zaštita bi
                // pokazivala na mrtvog, a sledeći undo paste-a bi pozicionim
                // matchom obrisao korisnikov zatečeni objekat.
                if (record.m_PastedExclude != null && record.m_PastedExclude.Remove(oldEntity))
                {
                    record.m_PastedExclude.Add(newEntity);
                }

                // Mreže: bez ovoga bi posle brisanja i undo-a stariji zapisi
                // pokazivali na mrtve ivice/čvorove i ćutke ne bi radili ništa.
                if (record.m_NetNodes != null)
                {
                    for (int i = 0; i < record.m_NetNodes.Count; i++)
                    {
                        if (record.m_NetNodes[i].m_Entity == oldEntity)
                        {
                            NetNodeSnapshot snapshot = record.m_NetNodes[i];
                            snapshot.m_Entity = newEntity;
                            record.m_NetNodes[i] = snapshot;
                        }
                    }
                }

                if (record.m_NetEdges != null)
                {
                    for (int i = 0; i < record.m_NetEdges.Count; i++)
                    {
                        NetEdgeSnapshot snapshot = record.m_NetEdges[i];
                        if (snapshot.m_Entity != oldEntity &&
                            snapshot.m_StartNode != oldEntity &&
                            snapshot.m_EndNode != oldEntity)
                        {
                            continue;
                        }

                        if (snapshot.m_Entity == oldEntity)
                        {
                            snapshot.m_Entity = newEntity;
                        }

                        if (snapshot.m_StartNode == oldEntity)
                        {
                            snapshot.m_StartNode = newEntity;
                        }

                        if (snapshot.m_EndNode == oldEntity)
                        {
                            snapshot.m_EndNode = newEntity;
                        }

                        record.m_NetEdges[i] = snapshot;
                    }
                }
            }
        }

        // Ponovno kreiranje obrisanog propa direktno iz arhetipa prefaba.
        private void RecreateProp(TransformSnapshot snapshot)
        {
            if (!EntityManager.Exists(snapshot.m_Prefab) ||
                !EntityManager.TryGetComponent(snapshot.m_Prefab, out ObjectData objectData))
            {
                return;
            }

            // 3-arg varijanta namerno: CreateEntity(archetype) povlači span tipove koje net48 toolchain build nema.
            NativeArray<Entity> created = EntityManager.CreateEntity(objectData.m_Archetype, 1, Allocator.Temp);
            Entity entity = created[0];
            created.Dispose();
            EntityManager.SetComponentData(entity, new PrefabRef(snapshot.m_Prefab));
            EntityManager.SetComponentData(entity, snapshot.m_Transform);
            if (snapshot.m_HadElevation)
            {
                if (EntityManager.HasComponent<Game.Objects.Elevation>(entity))
                {
                    EntityManager.SetComponentData(entity, new Game.Objects.Elevation { m_Elevation = snapshot.m_Elevation });
                }
                else
                {
                    EntityManager.AddComponentData(entity, new Game.Objects.Elevation { m_Elevation = snapshot.m_Elevation });
                }
            }

            if (EntityManager.HasComponent<PseudoRandomSeed>(entity))
            {
                // Vraćeni prop zadržava originalnu varijaciju (boju); random samo
                // ako snapshot iz nekog razloga nema seed.
                EntityManager.SetComponentData(entity, snapshot.m_HasSeed
                    ? new PseudoRandomSeed(snapshot.m_Seed)
                    : new PseudoRandomSeed((ushort)RandomSeed.Next().GetRandom(0).NextInt(ushort.MaxValue)));
            }

            // Drveće: vrati starost/stanje — inače undo vraća sadnicu umesto odraslog stabla.
            if (snapshot.m_HadTree && EntityManager.HasComponent<Game.Objects.Tree>(entity))
            {
                EntityManager.SetComponentData(entity, snapshot.m_Tree);
            }

            // Custom boja: vrati je zajedno sa propom.
            if (snapshot.m_HasCustomColor)
            {
                ApplyInstanceColors(entity, snapshot.m_CustomColor);
            }

            // Zgrada: kao kod paste-a — pločnike i prilaze gradi SAMO
            // construction tok, pa se "upali" izgradnja koja se završi za tren.
            // Odloženi settle zatim tera konekciju na put sa svežim stablima.
            bool isBuildingPrefab = EntityManager.HasComponent<Game.Prefabs.BuildingData>(snapshot.m_Prefab);
            if (isBuildingPrefab)
            {
                EntityManager.AddComponentData(entity, new Game.Objects.UnderConstruction
                {
                    m_NewPrefab = snapshot.m_Prefab,
                    m_Progress = 250,
                    m_Speed = 200,
                });
                m_DelayedSettle[entity] = 6;

                // Vraćena zgrada nasleđuje brisanja placa iz trenutka snimka —
                // construction gradi fabrički komplet, višak se briše po potpisima.
                ScheduleSurfacePrune(entity, snapshot.m_SurfaceSigs);

                // Registar obrisanih pod-propova prati zgradu na NOVI entitet —
                // inače sweep posle rekreacije ne nalazi potpise i fabričke
                // kopije obrisanih propova prežive.
                RekeyDeletedSubProps(snapshot.m_Entity, entity);
            }

            // Bez ovoga vraćeni prop ume da ostane nevidljiv (batches) ili da ga igra
            // odmah sakrije kao Overridden ako se preklapa (zato PreventOverride).
            // Zgradama Anarchy tag ne treba — one ne bivaju Overridden.
            if (!isBuildingPrefab && m_HasPreventOverride && !EntityManager.HasComponent(entity, m_PreventOverrideType))
            {
                EntityManager.AddComponent(entity, m_PreventOverrideType);
            }

            // Prop koji je pripadao zgradi vraća se nazad zgradi: Owner veza +
            // upis u njen SubObject spisak — inače bi posle undo-a postao
            // samostalan i ispao iz "Building props" pravila.
            if (snapshot.m_Owner != Entity.Null && EntityManager.Exists(snapshot.m_Owner))
            {
                EntityManager.AddComponentData(entity, new Owner { m_Owner = snapshot.m_Owner });
                if (EntityManager.TryGetBuffer(snapshot.m_Owner, false, out DynamicBuffer<Game.Objects.SubObject> subObjects))
                {
                    subObjects.Add(new Game.Objects.SubObject { m_SubObject = entity });
                }

                // Vraćen je — briše mu se potpis iz registra obrisanih,
                // da ga prune ponovo ne skine.
                ForgetDeletedSubProp(entity);
            }

            EntityManager.AddComponent<Updated>(entity);
            EntityManager.AddComponent<BatchesUpdated>(entity);
            RemapHistoryEntity(snapshot.m_Entity, entity);
        }

        private void DeletePastedEntities(List<PastedRecord> records, HashSet<Entity> stampExclude, List<PreStampNetCurve> stampPreCurves)
        {
            if (records == null || records.Count == 0)
            {
                return;
            }

            // Twin zaštita: kombinuj živi prozorski set (ako još traje) sa
            // setom sačuvanim u zapisu — pozicioni fallback nikad ne sme da
            // obriše entitet koji je postojao pre stampa.
            HashSet<Entity> exclude = stampExclude ?? m_PostPasteExclude;
            List<PreStampNetCurve> preCurves = stampPreCurves ?? m_PostPasteNetPreCurves;

            // Razrešenje koje pokazuje na MRTAV entitet (nalepljeni put je u
            // međuvremenu obrisan pa vraćen undo-om — vratio se kao NOV
            // entitet) mora nazad u "nerazrešeno", da ga pozicioni fallback
            // pronađe. Inače bi undo paste-a tiho ostavio taj put da stoji.
            for (int i = 0; i < records.Count; i++)
            {
                PastedRecord record = records[i];
                if (record.m_Resolved != Entity.Null &&
                    (!EntityManager.Exists(record.m_Resolved) || EntityManager.HasComponent<Deleted>(record.m_Resolved)))
                {
                    record.m_Resolved = Entity.Null;
                    records[i] = record;
                }
            }

            // Prvo razrešeni zapisi: brišemo TAČNO entitete koje je paste stvorio.
            HashSet<Entity> deleted = new HashSet<Entity>();
            int unresolvedCount = 0;
            bool anyNetEdgeRecord = false;
            foreach (PastedRecord record in records)
            {
                anyNetEdgeRecord |= record.m_IsNetEdge;
                if (record.m_Resolved != Entity.Null)
                {
                    if (EntityManager.Exists(record.m_Resolved) && deleted.Add(record.m_Resolved))
                    {
                        m_Selected.Remove(record.m_Resolved);

                        // Ograda/put: i osiroteli čvorovi idu sa ivicom.
                        if (record.m_IsLane)
                        {
                            m_SelectedLanes.Remove(record.m_Resolved);
                            DeleteLaneWithNodes(record.m_Resolved);
                        }
                        else if (record.m_IsNetEdge)
                        {
                            m_SelectedNetEdges.Remove(record.m_Resolved);
                            DeleteNetEdgeWithNodes(record.m_Resolved);
                        }
                        else
                        {
                            EntityManager.AddComponent<Deleted>(record.m_Resolved);
                        }
                    }
                }
                else
                {
                    unresolvedCount++;
                }
            }

            // Rani izlaz SAMO kad nema net zapisa: drugi prolaz brisanja
            // (parčad koje je igra napravila deljenjem na tunelima/zidovima)
            // mora da radi i kad su SVI zapisi rezolvovani — sredina velikog
            // parčeta ume da rezolvuje zapis, a portalska parčad ostaju.
            if (unresolvedCount == 0 && !anyNetEdgeRecord)
            {
                return;
            }

            // Rezerva za nerazrešene (undo pre završetka razrešavanja): pozicioni match,
            // ali najviše JEDAN obrisan entitet po zapisu — originali ostaju.
            float3 boundsMin = new float3(float.MaxValue);
            float3 boundsMax = new float3(float.MinValue);
            foreach (PastedRecord record in records)
            {
                if (record.m_Resolved == Entity.Null)
                {
                    boundsMin = math.min(boundsMin, record.m_Position);
                    boundsMax = math.max(boundsMax, record.m_Position);
                }

                // Net zapisi UVEK (i rezolvovani!): drugi prolaz čisti parčad
                // podele duž cele krive, a okvir samo od nerezolvovanih ume da
                // bude sitan ili čak prazan.
                ExpandBoundsForRecord(record, ref boundsMin, ref boundsMax);
            }

            boundsMin -= 0.5f;
            boundsMax += 0.5f;

            // Fallback mora da pokrije i zgrade i površine — prop upit ih
            // isključuje, pa bi nerazrešene nalepljene zgrade/površine
            // preživele undo.
            bool[] recordUsed = new bool[records.Count];
            DeleteUnresolvedFromQuery(m_PropQuery, records, recordUsed, boundsMin, boundsMax, deleted, exclude);
            DeleteUnresolvedFromQuery(m_BuildingQuery, records, recordUsed, boundsMin, boundsMax, deleted, exclude);
            DeleteUnresolvedAreas(records, recordUsed, boundsMin, boundsMax, deleted, exclude);
            DeleteUnresolvedLanes(records, recordUsed, boundsMin, boundsMax, deleted, exclude);
            DeleteUnresolvedNetEdges(records, recordUsed, boundsMin, boundsMax, deleted, exclude, preCurves);
        }

        private void DeleteUnresolvedFromQuery(EntityQuery query, List<PastedRecord> records, bool[] recordUsed, float3 boundsMin, float3 boundsMax, HashSet<Entity> deleted, HashSet<Entity> exclude)
        {
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms = query.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            NativeArray<PrefabRef> prefabRefs = query.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                float3 position = transforms[i].m_Position;
                if (math.any(position < boundsMin) || math.any(position > boundsMax) ||
                    deleted.Contains(entities[i]) ||
                    (exclude != null && exclude.Contains(entities[i])))
                {
                    continue;
                }

                for (int j = 0; j < records.Count; j++)
                {
                    PastedRecord record = records[j];
                    if (recordUsed[j] || record.m_IsArea || record.m_IsLane || record.m_IsNetEdge || record.m_Resolved != Entity.Null ||
                        prefabRefs[i].m_Prefab != record.m_Prefab ||
                        math.distancesq(position, record.m_Position) > 0.01f)
                    {
                        continue;
                    }

                    recordUsed[j] = true;
                    deleted.Add(entities[i]);
                    m_Selected.Remove(entities[i]);
                    EntityManager.AddComponent<Deleted>(entities[i]);
                    break;
                }
            }

            entities.Dispose();
            transforms.Dispose();
            prefabRefs.Dispose();
        }

        // Nerazrešene POVRŠINE iz paste undo-a: match po prefabu + centroidu
        // poligona (isti kriterijum kao rezolucija — pola metra).
        private void DeleteUnresolvedAreas(List<PastedRecord> records, bool[] recordUsed, float3 boundsMin, float3 boundsMax, HashSet<Entity> deleted, HashSet<Entity> exclude)
        {
            NativeArray<Entity> areas = m_SurfaceQuery.ToEntityArray(Allocator.Temp);
            NativeArray<PrefabRef> prefabRefs = m_SurfaceQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            for (int i = 0; i < areas.Length; i++)
            {
                if (deleted.Contains(areas[i]) ||
                    (exclude != null && exclude.Contains(areas[i])) ||
                    !EntityManager.TryGetBuffer(areas[i], true, out DynamicBuffer<Game.Areas.Node> nodes) ||
                    nodes.Length < 3)
                {
                    continue;
                }

                float2 centroid = float2.zero;
                for (int n = 0; n < nodes.Length; n++)
                {
                    centroid += nodes[n].m_Position.xz;
                }

                centroid /= nodes.Length;
                if (math.any(centroid < boundsMin.xz) || math.any(centroid > boundsMax.xz))
                {
                    continue;
                }

                for (int j = 0; j < records.Count; j++)
                {
                    PastedRecord record = records[j];
                    if (recordUsed[j] || !record.m_IsArea || record.m_Resolved != Entity.Null ||
                        prefabRefs[i].m_Prefab != record.m_Prefab ||
                        math.distancesq(centroid, record.m_Position.xz) > 0.25f)
                    {
                        continue;
                    }

                    recordUsed[j] = true;
                    deleted.Add(areas[i]);
                    m_SelectedSurfaces.Remove(areas[i]);
                    EntityManager.AddComponent<Deleted>(areas[i]);
                    break;
                }
            }

            areas.Dispose();
            prefabRefs.Dispose();
        }

        private Entity m_SameFilterPrefab = Entity.Null;
        private string m_SameFilterName = string.Empty;
        private Entity m_SelectedNameEntity = Entity.Null;
        private string m_SelectedName = string.Empty;

        // Keširano ime — UI ga čita svaki frejm, a menja se samo pri izboru filtera.
        public string SameFilterName => m_SameFilterName;

        // Ime propa za panel — samo kad je tačno jedan prop selektovan klikom (ne marquee-em).
        public string SelectedPropName
        {
            get
            {
                // Ime se pokazuje kad god je selektovana TAČNO jedna stvar —
                // svejedno da li klikom ili marquee-jem (brojač kaže 1, pa i
                // ime treba da stoji).
                if (SelectedCount != 1)
                {
                    return string.Empty;
                }

                Entity entity;
                if (m_Selected.Count == 1)
                {
                    entity = m_Selected[0];
                }
                else if (m_SelectedSurfaces.Count == 1)
                {
                    entity = m_SelectedSurfaces[0];
                }
                else if (m_SelectedLanes.Count == 1)
                {
                    entity = m_SelectedLanes[0];
                }
                else if (m_SelectedNodes.Count == 1)
                {
                    entity = m_SelectedNodes[0];
                }
                else if (m_SelectedNetEdges.Count == 1)
                {
                    entity = m_SelectedNetEdges[0];
                }
                else
                {
                    // Nova vrsta selekcije koja još nema granu ovde — bolje
                    // prazno ime nego indeksiranje pogrešne liste.
                    return string.Empty;
                }

                if (entity != m_SelectedNameEntity)
                {
                    m_SelectedNameEntity = entity;

                    // Ograda: PrefabRef pokazuje na nevidljivi container — pravo
                    // ime nosi prefab ograde iz EditorContainer-a.
                    Entity namePrefab = Entity.Null;
                    if (EntityManager.Exists(entity))
                    {
                        if (!TryGetLanePrefab(entity, out namePrefab) &&
                            EntityManager.TryGetComponent(entity, out PrefabRef prefabRef))
                        {
                            namePrefab = prefabRef.m_Prefab;
                        }
                    }

                    m_SelectedName =
                        namePrefab != Entity.Null &&
                        m_PrefabSystem.TryGetPrefab(namePrefab, out PrefabBase prefabBase) &&
                        prefabBase != null
                            ? prefabBase.name
                            : string.Empty;
                }

                return m_SelectedName;
            }
        }

        private void SetSameFilterName()
        {
            m_SameFilterName = m_SameFilterPrefab == Entity.Null
                ? string.Empty
                : m_PrefabSystem.TryGetPrefab(m_SameFilterPrefab, out PrefabBase prefabBase) && prefabBase != null
                    ? prefabBase.name
                    : "?";
        }

        // Filter tipa: dok je aktivan, marquee selektuje samo propove tog prefaba.
        public void ToggleSameFilter()
        {
            if (!ToolIsActive || m_Mode != Mode.Select)
            {
                return;
            }

            if (m_SameFilterPrefab != Entity.Null)
            {
                m_SameFilterPrefab = Entity.Null;
                SetSameFilterName();
                return;
            }

            Entity source = m_HoverEntity != Entity.Null ? m_HoverEntity : (m_Selected.Count > 0 ? m_Selected[0] : Entity.Null);
            if (source == Entity.Null ||
                !EntityManager.Exists(source) ||
                !EntityManager.TryGetComponent(source, out PrefabRef prefabRef))
            {
                return;
            }

            m_SameFilterPrefab = prefabRef.m_Prefab;
            SetSameFilterName();
            if (m_Selected.Count > 0)
            {
                FilterSelectionToSamePrefab(source);
            }
        }

        // Suzi postojeću selekciju na propove istog prefaba kao izvor.
        private void FilterSelectionToSamePrefab(Entity sourceEntity)
        {
            // Menja sastav selekcije — živa align sesija više ne važi.
            EndAlignSession();

            if (!EntityManager.TryGetComponent(sourceEntity, out PrefabRef sourcePrefab))
            {
                return;
            }

            for (int i = m_Selected.Count - 1; i >= 0; i--)
            {
                Entity entity = m_Selected[i];
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out PrefabRef prefabRef) ||
                    prefabRef.m_Prefab != sourcePrefab.m_Prefab)
                {
                    if (entity != m_HoverEntity)
                    {
                        Unhighlight(entity);
                    }

                    m_Selected.RemoveAt(i);
                }
            }
        }

        // Fino pomeranje selekcije (Ctrl+strelice), relativno u odnosu na kameru.
        private float3 GetNudgeDelta()
        {
            float x = 0f;
            float z = 0f;
            if (m_NudgeRightAction.IsPressed())
            {
                x += 1f;
            }

            if (m_NudgeLeftAction.IsPressed())
            {
                x -= 1f;
            }

            if (m_NudgeUpAction.IsPressed())
            {
                z += 1f;
            }

            if (m_NudgeDownAction.IsPressed())
            {
                z -= 1f;
            }

            if (x == 0f && z == 0f)
            {
                return float3.zero;
            }

            UnityEngine.Camera camera = UnityEngine.Camera.main;
            float3 cameraForward = camera != null ? (float3)camera.transform.forward : new float3(0f, 0f, 1f);
            float2 forward = math.normalizesafe(cameraForward.xz, new float2(0f, 1f));
            float2 right = new float2(forward.y, -forward.x);

            float speed = 1f * UnityEngine.Time.deltaTime;
            float2 delta = ((right * x) + (forward * z)) * speed;
            return new float3(delta.x, 0f, delta.y);
        }

        private bool AnyNudgePressedThisFrame()
        {
            return m_NudgeUpAction.WasPressedThisFrame() ||
                m_NudgeDownAction.WasPressedThisFrame() ||
                m_NudgeLeftAction.WasPressedThisFrame() ||
                m_NudgeRightAction.WasPressedThisFrame();
        }

        private void NudgeSelection(float3 delta)
        {
            EndAlignSession();
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();

            foreach (Entity entity in m_Selected)
            {
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                float heightOffset = transform.m_Position.y - TerrainUtils.SampleHeight(ref heightData, transform.m_Position);
                float3 position = transform.m_Position + delta;
                position.y = TerrainUtils.SampleHeight(ref heightData, position) + heightOffset;

                if (IsBuilding(entity))
                {
                    if (IsMovableBuilding(entity))
                    {
                        // Capture-on-demand: nudge u toku držanja tastera može
                        // da zatekne zgradu koja NIJE snimljena na pritisku
                        // (selekcija promenjena u međuvremenu) — snimi je sad,
                        // pre pomeranja.
                        if (!m_SubPropCaptured.Contains(entity))
                        {
                            CaptureSubPropLayout(entity);
                        }

                        TransformBuilding(entity, position - transform.m_Position, 0f, default);
                        ScheduleSubPropRestore(entity);
                        m_DelayedSettle[entity] = 4;
                    }

                    continue;
                }

                transform.m_Position = position;
                EntityManager.SetComponentData(entity, transform);
                WriteElevation(entity, heightOffset);
                EntityManager.AddComponent<Updated>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }

            // Površine prate nudge u xz ravni (visina se sama prilagodi terenu).
            foreach (Entity area in m_SelectedSurfaces)
            {
                TransformSurface(area, quaternion.identity, float3.zero, new float3(delta.x, 0f, delta.z));
            }

            // Ograde takođe (nudge nema tick/settle pa Updated ide uvek).
            if (m_SelectedLanes.Count > 0)
            {
                HashSet<Entity> laneGroup = BuildLaneGroup();
                foreach (Entity lane in m_SelectedLanes)
                {
                    TransformLane(lane, quaternion.identity, float3.zero, new float3(delta.x, 0f, delta.z), laneGroup);
                }
            }

            // Mreže: nudge uvek sa punim update-om (mali pomaci, retko masovni).
            TransformNetSelection(quaternion.identity, float3.zero, new float3(delta.x, 0f, delta.z), true);
        }

        // Aktivna align "sesija": posle Spaced/Circle poravnanja strelice
        // levo/desno menjaju razmak uživo, dok se selekcija ne promeni.
        private enum AlignKind
        {
            None,
            Spaced,
            Circle,
        }

        private AlignKind m_AlignKind = AlignKind.None;
        private int m_AlignSource; // 1 = Line, 2 = To prop, 3 = Circle (za UI glow)
        private float m_AlignGap = -1f;
        private readonly List<Entity> m_AlignOrder = new List<Entity>();
        private float2 m_AlignOrigin;      // Spaced: sidro (prvi prop na liniji); Circle: centar kruga
        private float2 m_AlignDirection;   // Spaced: smer linije
        private float m_AlignStartAngle;   // Circle: ugao prvog propa

        public float AlignSessionGap => m_AlignKind != AlignKind.None ? m_AlignGap : -1f;

        public int AlignSessionSource => m_AlignKind != AlignKind.None ? m_AlignSource : 0;

        public bool AlignPickArmed => m_AlignPickArmed;

        // Živa promena razmaka aktivne sesije iz panela (stepper).
        public void SetAlignSessionGap(float gap)
        {
            if (m_AlignKind == AlignKind.None || gap <= 0f)
            {
                return;
            }

            m_AlignGap = math.max(0.1f, gap);
            ApplyAlignSession();
        }

        // Stepper strelice i [ ] prečice: isti korak od 0,5 m, isti minimum.
        public void AdjustAlignSessionGap(int direction)
        {
            if (m_AlignKind == AlignKind.None || direction == 0)
            {
                return;
            }

            m_AlignGap = math.max(0.1f, m_AlignGap + (0.5f * math.sign(direction)));
            ApplyAlignSession();
        }


        private void EndAlignSession()
        {
            m_AlignKind = AlignKind.None;
            m_AlignSource = 0;
            m_AlignOrder.Clear();
        }

        // "To prop" dugme: naoružaj biranje uzor-propa (kao Match H za visinu).
        public void TriggerAlignPick(float gap = -1f)
        {
            if (!ToolIsActive || m_Mode != Mode.Select)
            {
                return;
            }

            m_AlignPickArmed = !m_AlignPickArmed && m_Selected.Count > 0;
            m_AlignPickGap = gap;
            if (m_AlignPickArmed)
            {
                m_HeightPickArmed = false;
            }
        }

        // Red poravnat na uzor-prop: linija kroz njega, duž njegove DESNE ose
        // (klupe staju jedna do druge), svi propovi preuzimaju njegovu rotaciju,
        // razmaci jednaki. Pokreće align sesiju.
        private void AlignRowToReference(Entity reference, float gap)
        {
            if (!EntityManager.Exists(reference) ||
                !EntityManager.TryGetComponent(reference, out Game.Objects.Transform referenceTransform) ||
                m_Selected.Count == 0)
            {
                return;
            }

            float3 rightAxis = math.mul(referenceTransform.m_Rotation, new float3(1f, 0f, 0f));
            float2 direction = math.normalizesafe(rightAxis.xz, new float2(1f, 0f));
            float2 origin = referenceTransform.m_Position.xz;

            List<Entity> valid = new List<Entity>();
            List<float> projections = new List<float>();
            float tMin = float.MaxValue;
            float tMax = float.MinValue;
            foreach (Entity entity in m_Selected)
            {
                if (IsBuilding(entity) ||
                    !EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                float projection = math.dot(transform.m_Position.xz - origin, direction);
                valid.Add(entity);
                projections.Add(projection);
                tMin = math.min(tMin, projection);
                tMax = math.max(tMax, projection);
            }

            if (valid.Count == 0)
            {
                return;
            }

            // Jedan prop: nema sesije (nema šta da se razmiče) — samo ga zarotiraj
            // kao uzor i prisloni na njegovu liniju.
            if (valid.Count == 1)
            {
                EndAlignSession();
                PushTransformUndo();
                if (EntityManager.TryGetComponent(valid[0], out Game.Objects.Transform single))
                {
                    TerrainHeightData singleHeightData = m_TerrainSystem.GetHeightData();
                    float heightOffset = single.m_Position.y - TerrainUtils.SampleHeight(ref singleHeightData, single.m_Position);
                    float3 position = single.m_Position;
                    position.xz = origin + (direction * projections[0]);
                    position.y = TerrainUtils.SampleHeight(ref singleHeightData, position) + heightOffset;
                    single.m_Position = position;
                    single.m_Rotation = referenceTransform.m_Rotation;
                    EntityManager.SetComponentData(valid[0], single);
                    WriteElevation(valid[0], heightOffset);
                    EntityManager.AddComponent<Updated>(valid[0]);
                    EntityManager.AddComponent<BatchesUpdated>(valid[0]);
                }

                return;
            }

            List<int> order = new List<int>(valid.Count);
            for (int i = 0; i < valid.Count; i++)
            {
                order.Add(i);
            }

            order.Sort((a, b) => projections[a].CompareTo(projections[b]));

            EndAlignSession();
            m_AlignOrder.Capacity = order.Count;
            foreach (int index in order)
            {
                m_AlignOrder.Add(valid[index]);
            }

            m_AlignKind = AlignKind.Spaced;
            m_AlignSource = 2;
            m_AlignGap = math.max(0.1f, gap > 0f
                ? gap
                : (valid.Count > 1 ? (tMax - tMin) / (valid.Count - 1) : 1f));
            m_AlignOrigin = origin + (direction * tMin);
            m_AlignDirection = direction;

            PushTransformUndo();

            foreach (Entity entity in valid)
            {
                if (EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    transform.m_Rotation = referenceTransform.m_Rotation;
                    EntityManager.SetComponentData(entity, transform);

                    // I bez pomeranja pozicije rotacija mora da se vidi odmah.
                    EntityManager.AddComponent<Updated>(entity);
                    EntityManager.AddComponent<BatchesUpdated>(entity);
                }
            }

            ApplyAlignSession();
            Mod.Log.Info($"Copaste: align to reference prop on {valid.Count} props (gap {m_AlignGap:F1} m)");
        }

        // ALT + točkić: svaki selektovani prop se okreće oko SVOJE ose.
        private void SpinSelection(float angle)
        {
            quaternion spin = quaternion.RotateY(angle);
            foreach (Entity entity in m_Selected)
            {
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                // Zgrada se okreće u mestu oko svog pivota, pod-tree prati.
                if (IsBuilding(entity))
                {
                    if (IsMovableBuilding(entity))
                    {
                        // Spin burst (1s prozor) preskače PushTransformUndo —
                        // zgrada selektovana usred bursta se snima ovde.
                        if (!m_SubPropCaptured.Contains(entity))
                        {
                            CaptureSubPropLayout(entity);
                        }

                        TransformBuilding(entity, float3.zero, angle, transform.m_Position);
                        ScheduleSubPropRestore(entity);
                        m_DelayedSettle[entity] = 4;
                    }

                    continue;
                }

                transform.m_Rotation = math.mul(spin, transform.m_Rotation);
                EntityManager.SetComponentData(entity, transform);
                EntityManager.AddComponent<Updated>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }
        }

        // Postavi propove sesije na trenutni m_AlignGap (linija ili krug).
        private void ApplyAlignSession()
        {
            if (m_AlignKind == AlignKind.None || m_AlignOrder.Count < 2)
            {
                return;
            }

            int count = m_AlignOrder.Count;
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            for (int rank = 0; rank < count; rank++)
            {
                Entity entity = m_AlignOrder[rank];
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                float2 newXz;
                if (m_AlignKind == AlignKind.Spaced)
                {
                    newXz = m_AlignOrigin + (m_AlignDirection * (m_AlignGap * rank));
                }
                else
                {
                    // m_AlignGap = razmak po luku; iz njega sledi poluprečnik.
                    float radius = (m_AlignGap * count) / (2f * math.PI);
                    float angle = m_AlignStartAngle + (2f * math.PI * rank / count);
                    newXz = m_AlignOrigin + (new float2(math.cos(angle), math.sin(angle)) * radius);
                }

                if (math.distancesq(newXz, transform.m_Position.xz) < 0.000001f)
                {
                    continue;
                }

                float heightOffset = transform.m_Position.y - TerrainUtils.SampleHeight(ref heightData, transform.m_Position);
                float3 position = transform.m_Position;
                position.xz = newXz;
                position.y = TerrainUtils.SampleHeight(ref heightData, position) + heightOffset;

                transform.m_Position = position;
                EntityManager.SetComponentData(entity, transform);
                WriteElevation(entity, heightOffset);
                EntityManager.AddComponent<Updated>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }
        }

        // Align circle: rasporedi selekciju ravnomerno po krugu oko centra
        // selekcije. gap > 0 zadaje razmak po luku (određuje poluprečnik);
        // inače poluprečnik = prosečna udaljenost propova od centra.
        public void TriggerAlignCircle(float gap = -1f)
        {
            if (!ToolIsActive || m_Mode != Mode.Select || m_Selected.Count < 3)
            {
                return;
            }

            List<Entity> valid = new List<Entity>();
            List<float3> positions = new List<float3>();
            foreach (Entity entity in m_Selected)
            {
                if (!IsBuilding(entity) && EntityManager.Exists(entity) && EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    valid.Add(entity);
                    positions.Add(transform.m_Position);
                }
            }

            if (valid.Count < 3)
            {
                return;
            }

            float2 center = float2.zero;
            for (int i = 0; i < positions.Count; i++)
            {
                center += positions[i].xz;
            }

            center /= positions.Count;

            float averageRadius = 0f;
            for (int i = 0; i < positions.Count; i++)
            {
                averageRadius += math.distance(positions[i].xz, center);
            }

            averageRadius = math.max(1f, averageRadius / positions.Count);
            float radius = gap > 0f ? math.max(0.5f, (gap * valid.Count) / (2f * math.PI)) : averageRadius;

            // Redosled po trenutnom uglu — propovi zadržavaju međusobni raspored.
            List<int> order = new List<int>(positions.Count);
            float[] angles = new float[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                order.Add(i);
                float2 offset = positions[i].xz - center;
                angles[i] = math.atan2(offset.y, offset.x);
            }

            order.Sort((a, b) => angles[a].CompareTo(angles[b]));

            EndAlignSession();
            m_AlignOrder.Capacity = valid.Count;
            foreach (int index in order)
            {
                m_AlignOrder.Add(valid[index]);
            }

            m_AlignKind = AlignKind.Circle;
            m_AlignSource = 3;
            m_AlignGap = math.max(0.1f, (2f * math.PI * radius) / valid.Count);
            m_AlignOrigin = center;
            m_AlignStartAngle = angles[order[0]];

            PushTransformUndo();
            ApplyAlignSession();
            Mod.Log.Info($"Copaste: align circle on {valid.Count} props (radius {radius:F1} m)");
        }



        // Poravnanje u red: propovi na pravu liniju (kroz dva najudaljenija),
        // jednaki razmaci (gap > 0 = tačno u metrima, inače ravnomerno između
        // krajeva). alsoRotate dodatno okreće SVE propove isto — upravno na
        // liniju, na stranu na koju većina već gleda ("pravilan red"). Pokreće
        // align sesiju: [ ] tasteri i stepper menjaju razmak uživo.
        public void TriggerAlignRow(bool alsoRotate, float gap = -1f)
        {
            if (!ToolIsActive || m_Mode != Mode.Select || m_Selected.Count < 2)
            {
                return;
            }

            List<Entity> valid = new List<Entity>();
            List<float3> positions = new List<float3>();
            foreach (Entity entity in m_Selected)
            {
                if (!IsBuilding(entity) && EntityManager.Exists(entity) && EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    valid.Add(entity);
                    positions.Add(transform.m_Position);
                }
            }

            if (valid.Count < 2)
            {
                return;
            }

            // Pravac linije: dva međusobno najudaljenija propa (po tlu).
            int endA = 0;
            int endB = 1;
            float bestDistance = -1f;
            for (int i = 0; i < positions.Count; i++)
            {
                for (int j = i + 1; j < positions.Count; j++)
                {
                    float distance = math.distancesq(positions[i].xz, positions[j].xz);
                    if (distance > bestDistance)
                    {
                        bestDistance = distance;
                        endA = i;
                        endB = j;
                    }
                }
            }

            if (bestDistance < 0.0001f)
            {
                return; // svi propovi praktično na istom mestu
            }

            float2 origin = positions[endA].xz;
            float2 direction = math.normalize(positions[endB].xz - origin);

            // Projekcije na liniju + redosled duž nje.
            float[] projections = new float[positions.Count];
            float tMin = float.MaxValue;
            float tMax = float.MinValue;
            for (int i = 0; i < positions.Count; i++)
            {
                projections[i] = math.dot(positions[i].xz - origin, direction);
                tMin = math.min(tMin, projections[i]);
                tMax = math.max(tMax, projections[i]);
            }

            List<int> order = new List<int>(positions.Count);
            for (int i = 0; i < positions.Count; i++)
            {
                order.Add(i);
            }

            order.Sort((a, b) => projections[a].CompareTo(projections[b]));

            EndAlignSession();
            m_AlignOrder.Capacity = order.Count;
            foreach (int index in order)
            {
                m_AlignOrder.Add(valid[index]);
            }

            m_AlignKind = AlignKind.Spaced;
            m_AlignSource = 1;
            m_AlignGap = math.max(0.1f, gap > 0f ? gap : (tMax - tMin) / (order.Count - 1));
            m_AlignOrigin = origin + (direction * tMin);
            m_AlignDirection = direction;

            PushTransformUndo();

            if (alsoRotate)
            {
                // Ciljna rotacija: upravno na liniju, na stranu na koju većina
                // propova već gleda — red klupa ostaje okrenut ka "svojoj" strani.
                float2 perpendicular = new float2(-direction.y, direction.x);
                float side = 0f;
                foreach (Entity entity in valid)
                {
                    if (EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                    {
                        float3 forward = math.mul(transform.m_Rotation, new float3(0f, 0f, 1f));
                        side += math.dot(math.normalizesafe(forward.xz, float2.zero), perpendicular);
                    }
                }

                float2 targetForward = side >= 0f ? perpendicular : -perpendicular;
                quaternion targetRotation = quaternion.LookRotationSafe(
                    new float3(targetForward.x, 0f, targetForward.y),
                    math.up());

                foreach (Entity entity in valid)
                {
                    if (EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                    {
                        transform.m_Rotation = targetRotation;
                        EntityManager.SetComponentData(entity, transform);

                        // I bez pomeranja pozicije rotacija mora da se vidi odmah.
                        EntityManager.AddComponent<Updated>(entity);
                        EntityManager.AddComponent<BatchesUpdated>(entity);
                    }
                }
            }

            ApplyAlignSession();
            Mod.Log.Info($"Copaste: align row on {valid.Count} props (gap {m_AlignGap:F1} m, rotate {alsoRotate})");
        }

        // "Selected props" lista u panelu: samo za male selekcije (2-15) — tu ima
        // smisla birati pojedinačan prop; za veće selekcije lista se ne prikazuje.
        private static int kSelectionListMax => Mod.Settings != null ? Mod.Settings.SelectionListMax : 50;
        private Entity m_ListFocusEntity = Entity.Null;
        private readonly Dictionary<Entity, string> m_PrefabNameCache = new Dictionary<Entity, string>();

        public string GetSelectionList()
        {
            EnsureDerivedSelectionData();
            return m_CachedSelectionList;
        }

        private string ComputeSelectionList()
        {
            if (m_Mode != Mode.Select || m_Selected.Count < 2 || m_Selected.Count > kSelectionListMax)
            {
                // Lista nestaje bez mouseleave događaja — fokus prsten ne sme da ostane.
                m_ListFocusEntity = Entity.Null;
                return string.Empty;
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            foreach (Entity entity in m_Selected)
            {
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out PrefabRef prefabRef))
                {
                    continue;
                }

                if (!m_PrefabNameCache.TryGetValue(prefabRef.m_Prefab, out string name))
                {
                    name = m_PrefabSystem.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefabBase) && prefabBase != null
                        ? prefabBase.name
                        : "?";
                    m_PrefabNameCache[prefabRef.m_Prefab] = name;
                }

                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(entity.Index).Append(':').Append(entity.Version).Append(':').Append(name);
            }

            return builder.ToString();
        }

        // Hover na red liste: zeleni prsten oko tog propa (Entity.Null gasi).
        public void SetListFocus(int index, int version)
        {
            m_ListFocusEntity = new Entity { Index = index, Version = version };
        }

        public void ClearListFocus()
        {
            m_ListFocusEntity = Entity.Null;
        }

        // Klik na red liste: selekcija se svodi na SAMO taj prop.
        public void SelectOnly(int index, int version)
        {
            Entity entity = new Entity { Index = index, Version = version };
            if (m_Mode == Mode.Relocate || !m_Selected.Contains(entity) || !EntityManager.Exists(entity))
            {
                return;
            }

            ClearSelection();
            m_Selected.Add(entity);
            Highlight(entity);
            m_ListFocusEntity = Entity.Null;
        }

        public bool HeightPickArmed => m_HeightPickArmed;

        public void TriggerMatchHeight()
        {
            if (ToolIsActive && m_Mode == Mode.Select)
            {
                m_HeightPickArmed = !m_HeightPickArmed && SelectionHasHeightTargets();
                if (m_HeightPickArmed)
                {
                    m_AlignPickArmed = false; // samo jedan pick mod istovremeno
                }
            }
        }

        // Postavi visinu (iznad terena) cele selekcije prema uzor-propu.
        private void MatchSelectionHeight(Entity sourceEntity)
        {
            if (EntityManager.TryGetComponent(sourceEntity, out Game.Objects.Transform sourceTransform))
            {
                MatchSelectionHeightToY(sourceTransform.m_Position.y);
            }
        }

        // Uzor za visinu može da bude i čvor puta ili ograda pod kursorom.
        private bool TryPickHeightSource(float3 position, out float sourceY)
        {
            if (TryPickNetAt(position, out Entity sourceNode, out Entity sourceEdge))
            {
                if (sourceNode != Entity.Null &&
                    EntityManager.TryGetComponent(sourceNode, out Game.Net.Node nodeData))
                {
                    sourceY = nodeData.m_Position.y;
                    return true;
                }

                if (sourceEdge != Entity.Null &&
                    EntityManager.TryGetComponent(sourceEdge, out Game.Net.Curve edgeCurve))
                {
                    MathUtils.Distance(edgeCurve.m_Bezier, position, out float t);
                    sourceY = MathUtils.Position(edgeCurve.m_Bezier, t).y;
                    return true;
                }
            }

            if (TryPickLaneAt(position, out Entity sourceLane) &&
                EntityManager.TryGetComponent(sourceLane, out Game.Net.Curve laneCurve))
            {
                sourceY = LaneMidpoint(laneCurve.m_Bezier).y;
                return true;
            }

            sourceY = 0f;
            return false;
        }

        private void MatchSelectionHeightToY(float targetY)
        {
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();

            PushTransformUndo();

            foreach (Entity entity in m_Selected)
            {
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                // Zgrada na ciljnu visinu sa celim placem.
                if (IsBuilding(entity))
                {
                    if (IsMovableBuilding(entity) && math.abs(targetY - transform.m_Position.y) > 0.001f)
                    {
                        if (!m_SubPropCaptured.Contains(entity))
                        {
                            CaptureSubPropLayout(entity);
                        }

                        TransformBuilding(entity, new float3(0f, targetY - transform.m_Position.y, 0f), 0f, default);
                        ScheduleSubPropRestore(entity);
                        m_DelayedSettle[entity] = 4;
                    }

                    continue;
                }

                transform.m_Position.y = targetY;
                EntityManager.SetComponentData(entity, transform);

                WriteElevation(entity, targetY - TerrainUtils.SampleHeight(ref heightData, transform.m_Position));
                EntityManager.AddComponent<Updated>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }

            // Ograde na ciljnu visinu: cela kriva za razliku sredine do cilja.
            if (m_SelectedLanes.Count > 0)
            {
                HashSet<Entity> laneGroup = BuildLaneGroup();
                foreach (Entity lane in m_SelectedLanes)
                {
                    if (EntityManager.TryGetComponent(lane, out Game.Net.Curve laneCurve))
                    {
                        AdjustLaneHeight(lane, targetY - LaneMidpoint(laneCurve.m_Bezier).y, laneGroup);
                    }
                }
            }

            // Mreže: svaki selektovani čvor tačno na ciljnu visinu.
            MatchNetworkHeight(targetY);
        }

        // End: spusti selekciju na teren i resetuj elevaciju.
        private void SnapSelectionToGround()
        {
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();

            foreach (Entity entity in m_Selected)
            {
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                // Zgrada se spušta na teren kroz pod-tree (ceo plac zajedno);
                // bez WriteElevation — zgrade nemaju Elevation komponentu.
                // VAŽNO: teren POD zgradom je izravnat na njenu visinu (igra
                // nivelira plac), pa se meri OKOLNI teren van placa.
                if (IsBuilding(entity))
                {
                    float groundY = SampleGroundAroundBuilding(entity, transform, ref heightData);
                    if (IsMovableBuilding(entity) && math.abs(groundY - transform.m_Position.y) > 0.001f)
                    {
                        if (!m_SubPropCaptured.Contains(entity))
                        {
                            CaptureSubPropLayout(entity);
                        }

                        TransformBuilding(entity, new float3(0f, groundY - transform.m_Position.y, 0f), 0f, default);
                        ScheduleSubPropRestore(entity);
                        m_DelayedSettle[entity] = 4;
                    }

                    continue;
                }

                transform.m_Position.y = TerrainUtils.SampleHeight(ref heightData, transform.m_Position);
                EntityManager.SetComponentData(entity, transform);
                WriteElevation(entity, 0f);
                EntityManager.AddComponent<Updated>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }

            // Ograde se spuštaju na teren (elevacija se skida).
            SnapLanesToGround();

            // Mreže isto — čvorovi na teren, deonice prate.
            SnapNetworksToGround();
        }

        private static string BlueprintDirectory =>
            System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", "Copaste", "Blueprints");

        // Granica poverenja za imena iz UI trigger-a: bez nedozvoljenih znakova i bez ".."
        // — ime nikada ne sme da adresira fajl van Blueprints foldera.
        private static string SanitizeBlueprintName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid.ToString(), string.Empty);
            }

            name = name.Trim();
            return name.Length == 0 || name.Contains("..") ? null : name;
        }

        public List<string> GetBlueprintNames()
        {
            List<string> names = new List<string>();
            try
            {
                if (System.IO.Directory.Exists(BlueprintDirectory))
                {
                    foreach (string file in System.IO.Directory.GetFiles(BlueprintDirectory, "*.txt"))
                    {
                        names.Add(System.IO.Path.GetFileNameWithoutExtension(file));
                    }

                    names.Sort();
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Blueprint list failed: {e.Message}");
            }

            return names;
        }

        // Snima blueprint (automatsko ime); vraća ime ili null.
        // Selekcija ima prednost: čuva se ono što korisnik trenutno vidi obeleženo,
        // a clipboard tek kad selekcije nema (npr. čuvanje učitanog blueprinta).
        public string SaveBlueprint()
        {
            if (m_Mode == Mode.Relocate)
            {
                return null;
            }

            // I selekcija od samih površina mora prvo u clipboard — inače bi se
            // snimio stari sadržaj (1.0.4 lekcija). Selekcija koja ne proizvodi
            // stavke (npr. sam čvor) — bolje odbiti nego sačuvati zatečeno.
            if (SelectedCount > 0 && CopyableSelectedCount == 0)
            {
                return null;
            }

            if (CopyableSelectedCount > 0)
            {
                CopySelection();
            }

            if (ClipboardCount == 0)
            {
                return null;
            }

            try
            {
                System.IO.Directory.CreateDirectory(BlueprintDirectory);

                string name = null;
                for (int i = 1; i < 1000; i++)
                {
                    string candidate = $"Blueprint-{i:00}";
                    if (!System.IO.File.Exists(System.IO.Path.Combine(BlueprintDirectory, candidate + ".txt")))
                    {
                        name = candidate;
                        break;
                    }
                }

                if (name == null)
                {
                    return null;
                }

                System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
                List<string> lines = new List<string> { "COPASTE1" };
                foreach (ClipboardItem item in m_Clipboard)
                {
                    if (!m_PrefabSystem.TryGetPrefab(item.m_Prefab, out PrefabBase prefabBase) || prefabBase == null)
                    {
                        continue;
                    }

                    string typeName = prefabBase.GetType().Name;
                    string prefabName = prefabBase.name;
                    if (typeName.Contains("|") || prefabName.Contains("|"))
                    {
                        continue;
                    }

                    // 17. polje = hash prefaba, SAMO za PDX assete (vanila hash je
                    // prazan pa linija ostaje identična starom formatu; stariji
                    // loaderi 17-polja preskaču — ionako taj asset ne bi našli).
                    List<string> fields = new List<string>
                    {
                        typeName,
                        prefabName,
                        item.m_Offset.x.ToString("R", inv),
                        item.m_Offset.y.ToString("R", inv),
                        item.m_Offset.z.ToString("R", inv),
                        item.m_Rotation.value.x.ToString("R", inv),
                        item.m_Rotation.value.y.ToString("R", inv),
                        item.m_Rotation.value.z.ToString("R", inv),
                        item.m_Rotation.value.w.ToString("R", inv),
                        item.m_HeightOffset.ToString("R", inv),
                        item.m_Diameter.ToString("R", inv),
                        item.m_HadTree ? "1" : "0",
                        ((int)item.m_Tree.m_State).ToString(inv),
                        item.m_Tree.m_Growth.ToString(inv),
                        item.m_HasSeed ? item.m_Seed.ToString(inv) : "-1",
                        item.m_HasCustomColor ? SerializeColorSet(item.m_CustomColor, inv) : "-",
                    };

                    string prefabHash = GetPrefabHashString(prefabBase);
                    if (prefabHash.Length > 0)
                    {
                        fields.Add(prefabHash);
                    }

                    lines.Add(string.Join("|", fields));

                    // Plac zgrade: "BLOT|n" pa po jedna "BSURF|tip|ime|x,z;..."
                    // linija po površini (lokalni poligon) — važi za POSLEDNJU
                    // učitanu stavku. BLOT i sa nulom, da se razlikuje "izvor
                    // bez površina" (briši sve) od "nema podataka" (fabrički).
                    // Stariji loaderi obe linije preskaču (broj polja/tip).
                    if (item.m_SurfaceSigs != null)
                    {
                        int written = 0;
                        List<string> surfLines = new List<string>();

                        // Linije SA hashom (5 polja) idu na KRAJ bloka: stariji
                        // loader nepoznatu liniju tretira kao preskočenu STAVKU
                        // (lastItemSkipped) i oborio bi ostatak bloka iza nje.
                        List<string> hashedSurfLines = new List<string>();
                        foreach (SurfaceSig sig in item.m_SurfaceSigs)
                        {
                            if (sig.m_LocalNodes == null || sig.m_LocalNodes.Length < 3 ||
                                !m_PrefabSystem.TryGetPrefab(sig.m_Prefab, out PrefabBase sigPrefab) || sigPrefab == null)
                            {
                                continue;
                            }

                            string sigType = sigPrefab.GetType().Name;
                            string sigName = sigPrefab.name;
                            if (sigType.Contains("|") || sigName.Contains("|"))
                            {
                                continue;
                            }

                            System.Text.StringBuilder poly = new System.Text.StringBuilder();
                            foreach (float2 node in sig.m_LocalNodes)
                            {
                                if (poly.Length > 0)
                                {
                                    poly.Append(';');
                                }

                                poly.Append(node.x.ToString("R", inv)).Append(',').Append(node.y.ToString("R", inv));
                            }

                            // Hash kao umetnuto polje samo za PDX assete — stariji
                            // loaderi te linije preskaču (asset im ionako fali).
                            string sigHash = GetPrefabHashString(sigPrefab);
                            if (sigHash.Length > 0)
                            {
                                hashedSurfLines.Add(string.Join("|", new string[] { "BSURF", sigType, sigName, sigHash, poly.ToString() }));
                            }
                            else
                            {
                                surfLines.Add(string.Join("|", new string[] { "BSURF", sigType, sigName, poly.ToString() }));
                            }

                            written++;
                        }

                        lines.Add("BLOT|" + written.ToString(inv));
                        lines.AddRange(surfLines);
                        lines.AddRange(hashedSurfLines);
                    }
                }

                // Farbane površine: "AREA|tip|ime|x,z;x,z;..." — stariji loaderi
                // ovakve linije preskaču (pogrešan broj polja), pa su fajlovi kompatibilni.
                foreach (AreaClipboardItem area in m_ClipboardAreas)
                {
                    if (!m_PrefabSystem.TryGetPrefab(area.m_Prefab, out PrefabBase areaPrefab) || areaPrefab == null)
                    {
                        continue;
                    }

                    string areaType = areaPrefab.GetType().Name;
                    string areaName = areaPrefab.name;
                    if (areaType.Contains("|") || areaName.Contains("|"))
                    {
                        continue;
                    }

                    System.Text.StringBuilder polygon = new System.Text.StringBuilder();
                    foreach (float2 offset in area.m_NodeOffsets)
                    {
                        if (polygon.Length > 0)
                        {
                            polygon.Append(';');
                        }

                        polygon.Append(offset.x.ToString("R", inv)).Append(',').Append(offset.y.ToString("R", inv));
                    }

                    string areaHash = GetPrefabHashString(areaPrefab);
                    lines.Add(areaHash.Length > 0
                        ? string.Join("|", new string[] { "AREA", areaType, areaName, areaHash, polygon.ToString() })
                        : string.Join("|", new string[] { "AREA", areaType, areaName, polygon.ToString() }));
                }

                // Ograde: "LANE|tip|ime|hash|seed|x,z,h;x,z,h;x,z,h;x,z,h" —
                // 4 bezier tačke kao centroid-relativni xz + visina iznad
                // terena. Stariji loaderi liniju preskaču (nepoznat format).
                foreach (LaneClipboardItem lane in m_ClipboardLanes)
                {
                    if (lane.m_CurveOffsets == null || lane.m_CurveOffsets.Length != 4 ||
                        lane.m_HeightOffsets == null || lane.m_HeightOffsets.Length != 4 ||
                        !m_PrefabSystem.TryGetPrefab(lane.m_Prefab, out PrefabBase lanePrefab) || lanePrefab == null)
                    {
                        continue;
                    }

                    string laneType = lanePrefab.GetType().Name;
                    string laneName = lanePrefab.name;
                    if (laneType.Contains("|") || laneName.Contains("|"))
                    {
                        continue;
                    }

                    System.Text.StringBuilder points = new System.Text.StringBuilder();
                    for (int k = 0; k < 4; k++)
                    {
                        if (points.Length > 0)
                        {
                            points.Append(';');
                        }

                        points.Append(lane.m_CurveOffsets[k].x.ToString("R", inv)).Append(',')
                            .Append(lane.m_CurveOffsets[k].y.ToString("R", inv)).Append(',')
                            .Append(lane.m_HeightOffsets[k].ToString("R", inv));
                    }

                    string laneHash = GetPrefabHashString(lanePrefab);
                    lines.Add(string.Join("|", new string[]
                    {
                        "LANE",
                        laneType,
                        laneName,
                        laneHash.Length > 0 ? laneHash : "-",
                        lane.m_HasSeed ? lane.m_Seed.ToString(inv) : "-1",
                        points.ToString(),
                    }));
                }

                // Cvorovi mreze: "NETNODE|x,z,h" — jedan po IZVORNOM cvoru, redom.
                // Bez njih nalepljena raskrsnica iz blueprinta nema po cemu da
                // spoji deonice (spajanje traži bitski istu tacku).
                for (int n = 0; n < ClipboardNetNodeCount; n++)
                {
                    GetClipboardNetNode(n, out float2 nodeOffset, out float nodeHeight);
                    GetClipboardNetNodeUpgrade(n, out bool nodeHasUpgrade, out CompositionFlags nodeUpgrade);
                    lines.Add("NETNODE|" +
                        nodeOffset.x.ToString("R", inv) + "," +
                        nodeOffset.y.ToString("R", inv) + "," +
                        nodeHeight.ToString("R", inv) + "|" +
                        (nodeHasUpgrade
                            ? ((uint)nodeUpgrade.m_General).ToString(inv) + "," +
                              ((uint)nodeUpgrade.m_Left).ToString(inv) + "," +
                              ((uint)nodeUpgrade.m_Right).ToString(inv)
                            : "-"));
                }

                // Markeri cvorova: "NETMARK|indeks|tip|ime" — kruzni tok,
                // rucni semafor, stop znak. To su POD-OBJEKTI cvora, ne
                // nadogradnje, pa ne staju u NETNODE liniju; jedna linija po
                // markeru jer ih cvor moze imati vise. Stari loaderi
                // preskacu nepoznat tip linije.
                foreach (NetNodeMarker marker in m_ClipboardNetNodeMarkers)
                {
                    if (marker.m_NodeIndex < 0 ||
                        !m_PrefabSystem.TryGetPrefab(marker.m_Prefab, out PrefabBase markerPrefab) ||
                        markerPrefab == null)
                    {
                        continue;
                    }

                    string markerType = markerPrefab.GetType().Name;
                    string markerName = markerPrefab.name;
                    if (markerType.Contains("|") || markerName.Contains("|"))
                    {
                        continue;
                    }

                    // Hash kao i sve ostale linije: bez njega PDX marker
                    // (kružni tok iz workshopa) na učitavanju ne bi bio
                    // pronađen — ista zamka koju smo već rešili za propove.
                    string markerHash = GetPrefabHashString(markerPrefab);
                    lines.Add("NETMARK|" + marker.m_NodeIndex.ToString(inv) + "|" + markerType + "|" + markerName +
                        "|" + (markerHash.Length > 0 ? markerHash : "-"));
                }

                // Putevi: "ROAD|tip|ime|hash|nadogradnje|x,z,h;x4|startCvor,krajCvor"
                // — nadogradnje su tri uint flaga ("g,l,r") ili "-", a indeksi
                // cvorova pokazuju u NETNODE tabelu (-1 = nepoznat).
                foreach (NetEdgeClipboardItem road in m_ClipboardNetEdges)
                {
                    if (road.m_CurveOffsets == null || road.m_CurveOffsets.Length != 4 ||
                        road.m_HeightOffsets == null || road.m_HeightOffsets.Length != 4 ||
                        !m_PrefabSystem.TryGetPrefab(road.m_Prefab, out PrefabBase roadPrefab) || roadPrefab == null)
                    {
                        continue;
                    }

                    string roadType = roadPrefab.GetType().Name;
                    string roadName = roadPrefab.name;
                    if (roadType.Contains("|") || roadName.Contains("|"))
                    {
                        continue;
                    }

                    System.Text.StringBuilder roadPoints = new System.Text.StringBuilder();
                    for (int k = 0; k < 4; k++)
                    {
                        if (roadPoints.Length > 0)
                        {
                            roadPoints.Append(';');
                        }

                        roadPoints.Append(road.m_CurveOffsets[k].x.ToString("R", inv)).Append(',')
                            .Append(road.m_CurveOffsets[k].y.ToString("R", inv)).Append(',')
                            .Append(road.m_HeightOffsets[k].ToString("R", inv));
                    }

                    string roadHash = GetPrefabHashString(roadPrefab);
                    string upgrade = road.m_HasUpgrade
                        ? ((uint)road.m_Upgrade.m_General).ToString(inv) + "," +
                          ((uint)road.m_Upgrade.m_Left).ToString(inv) + "," +
                          ((uint)road.m_Upgrade.m_Right).ToString(inv)
                        : "-";
                    lines.Add(string.Join("|", new string[]
                    {
                        "ROAD",
                        roadType,
                        roadName,
                        roadHash.Length > 0 ? roadHash : "-",
                        upgrade,
                        roadPoints.ToString(),
                        road.m_StartNodeIndex.ToString(inv) + "," + road.m_EndNodeIndex.ToString(inv),
                    }));
                }

                if (lines.Count <= 1)
                {
                    return null;
                }

                System.IO.File.WriteAllLines(System.IO.Path.Combine(BlueprintDirectory, name + ".txt"), lines);
                Mod.Log.Info($"Copaste: blueprint '{name}' saved ({lines.Count - 1} props)");
                return name;
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Blueprint save failed: {e.Message}");
                return null;
            }
        }

        // Učitava blueprint u clipboard; posle toga Ctrl+V lepi kao i obično.
        public bool LoadBlueprint(string name)
        {
            if (m_Mode == Mode.Relocate)
            {
                return false;
            }

            try
            {
                name = SanitizeBlueprintName(name);
                if (name == null)
                {
                    return false;
                }

                string path = System.IO.Path.Combine(BlueprintDirectory, name + ".txt");
                if (!System.IO.File.Exists(path))
                {
                    return false;
                }

                string[] lines = System.IO.File.ReadAllLines(path);
                if (lines.Length < 2 || lines[0] != "COPASTE1")
                {
                    return false;
                }

                System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
                List<ClipboardItem> items = new List<ClipboardItem>();
                List<AreaClipboardItem> areaItems = new List<AreaClipboardItem>();
                List<LaneClipboardItem> laneItems = new List<LaneClipboardItem>();
                List<NetEdgeClipboardItem> roadItems = new List<NetEdgeClipboardItem>();
                List<float2> roadNodeOffsets = new List<float2>();
                List<float> roadNodeHeights = new List<float>();
                List<bool> roadNodeHasUpgrade = new List<bool>();
                List<CompositionFlags> roadNodeUpgrades = new List<CompositionFlags>();
                List<NetNodeMarker> roadNodeMarkers = new List<NetNodeMarker>();
                int missing = 0;
                Unity.Mathematics.Random loadRandom = RandomSeed.Next().GetRandom(0);

                // Da li je POSLEDNJA linija objekta preskočena (nepoznat format
                // ili nedostajući prefab) — njen BLOT/BSURF blok se tada ignoriše.
                bool lastItemSkipped = false;

                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('|');

                    // Plac zgrade: "BLOT|n" otvara (moguće praznu) listu za
                    // POSLEDNJU stavku, pa "BSURF|tip|ime|x,z;..." linije dodaju
                    // po jednu površinu sa lokalnim poligonom. Ako je linija
                    // zgrade PRESKOČENA (prefab nedostaje), njen blok se
                    // IGNORIŠE — inače bi pregazio plac prethodne zgrade
                    // (sluÄaj: deljeni blueprint sa DLC zgradom).
                    if (parts.Length == 2 && parts[0] == "BLOT")
                    {
                        if (items.Count > 0 && !lastItemSkipped)
                        {
                            ClipboardItem lotItem = items[items.Count - 1];
                            lotItem.m_SurfaceSigs = new List<SurfaceSig>();
                            items[items.Count - 1] = lotItem;
                        }

                        continue;
                    }

                    // 5 polja = varijanta sa hash-om (PDX asseti, od 1.2.0).
                    if ((parts.Length == 4 || parts.Length == 5) && parts[0] == "BSURF")
                    {
                        if (items.Count == 0 || lastItemSkipped ||
                            items[items.Count - 1].m_SurfaceSigs == null)
                        {
                            continue;
                        }

                        string sigHash = parts.Length == 5 ? parts[3] : null;
                        if (!TryResolveBlueprintPrefab(parts[1], parts[2], sigHash, out PrefabBase sigPrefab))
                        {
                            // Površina placa čiji prefab nedostaje — broji se u
                            // "missing" da korisnik vidi da nešto fali.
                            missing++;

                            // I popis se ODUSTAJE. Ne-prazan popis dole znači
                            // "izvor je imao TAČNO ove površine, ostalo obriši",
                            // pa bi krnji popis (asset na koji korisnik nije
                            // pretplaćen) obrisao fabričke staze i prilaz i
                            // ostavio kuću na goloj zemlji. Bez popisa zgrada
                            // dobija fabrički plac — to je ispravno odstupanje.
                            ClipboardItem brokenLot = items[items.Count - 1];
                            brokenLot.m_SurfaceSigs = null;
                            items[items.Count - 1] = brokenLot;
                            continue;
                        }

                        string[] sigPairs = parts[parts.Length - 1].Split(';');
                        if (sigPairs.Length < 3)
                        {
                            // Pokvarena geometrija odustaje CEO popis, isto kao
                            // nedostajući prefab — krnji popis znači "obriši
                            // fabričke staze", a to ovde niko nije hteo.
                            ClipboardItem malformedLot = items[items.Count - 1];
                            malformedLot.m_SurfaceSigs = null;
                            items[items.Count - 1] = malformedLot;
                            continue;
                        }

                        float2[] localNodes = new float2[sigPairs.Length];
                        bool sigValid = true;
                        for (int n = 0; n < sigPairs.Length; n++)
                        {
                            string[] xy = sigPairs[n].Split(',');
                            if (xy.Length != 2 ||
                                !TryParseBlueprintFloat(xy[0], inv, out float sx) ||
                                !TryParseBlueprintFloat(xy[1], inv, out float sz))
                            {
                                sigValid = false;
                                break;
                            }

                            localNodes[n] = new float2(sx, sz);
                        }

                        if (sigValid)
                        {
                            items[items.Count - 1].m_SurfaceSigs.Add(new SurfaceSig
                            {
                                m_Prefab = m_PrefabSystem.GetEntity(sigPrefab),
                                m_LocalNodes = localNodes,
                            });
                        }
                        else
                        {
                            // Neparsirljiva koordinata (odsečen/prepravljen
                            // fajl) — odustani popis kao i gore.
                            ClipboardItem invalidLot = items[items.Count - 1];
                            invalidLot.m_SurfaceSigs = null;
                            items[items.Count - 1] = invalidLot;
                        }

                        continue;
                    }

                    // Farbana površina: "AREA|tip|ime|x,z;x,z;..." (5 polja = sa hash-om).
                    if ((parts.Length == 4 || parts.Length == 5) && parts[0] == "AREA")
                    {
                        string areaHash = parts.Length == 5 ? parts[3] : null;
                        if (!TryResolveBlueprintPrefab(parts[1], parts[2], areaHash, out PrefabBase areaPrefab))
                        {
                            missing++;
                            continue;
                        }

                        string[] pairs = parts[parts.Length - 1].Split(';');
                        if (pairs.Length < 3)
                        {
                            continue;
                        }

                        float2[] offsets = new float2[pairs.Length];
                        bool valid = true;
                        for (int n = 0; n < pairs.Length; n++)
                        {
                            string[] xy = pairs[n].Split(',');
                            if (xy.Length != 2 ||
                                !TryParseBlueprintFloat(xy[0], inv, out float x) ||
                                !TryParseBlueprintFloat(xy[1], inv, out float z))
                            {
                                valid = false;
                                break;
                            }

                            offsets[n] = new float2(x, z);
                        }

                        if (valid)
                        {
                            areaItems.Add(new AreaClipboardItem
                            {
                                m_Prefab = m_PrefabSystem.GetEntity(areaPrefab),
                                m_NodeOffsets = offsets,
                            });
                        }

                        continue;
                    }

                    // Marker cvora: "NETMARK|indeks|tip|ime" (kruzni tok,
                    // semafor, stop). Indeks se proverava tek na kraju, kad je
                    // cela tabela cvorova procitana.
                    // 5 polja = varijanta sa hashom; 4 polja su fajlovi
                    // napisani pre nego što je hash dodat.
                    if ((parts.Length == 4 || parts.Length == 5) && parts[0] == "NETMARK")
                    {
                        if (int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, inv, out int markerNode) &&
                            markerNode >= 0)
                        {
                            string markerHash = parts.Length == 5 && parts[4] != "-" ? parts[4] : null;
                            if (TryResolveBlueprintPrefab(parts[2], parts[3], markerHash, out PrefabBase markerPrefab))
                            {
                                roadNodeMarkers.Add(new NetNodeMarker
                                {
                                    m_NodeIndex = markerNode,
                                    m_Prefab = m_PrefabSystem.GetEntity(markerPrefab),
                                });
                            }
                            else
                            {
                                missing++;
                            }
                        }

                        continue;
                    }

                    // Put: "ROAD|tip|ime|hash|nadogradnje|x,z,h;x4".
                    // Cvor mreze: "NETNODE|x,z,h|g,l,r" (redosled u fajlu =
                    // indeks; nadogradnje su kruzni tok/semafori/stop, ili "-").
                    if (parts.Length >= 1 && parts[0] == "NETNODE")
                    {
                        string[] nodeXzh = parts.Length >= 2 ? parts[1].Split(',') : new string[0];
                        if (nodeXzh.Length == 3 &&
                            TryParseBlueprintFloat(nodeXzh[0], inv, out float nx) &&
                            TryParseBlueprintFloat(nodeXzh[1], inv, out float nz) &&
                            TryParseBlueprintFloat(nodeXzh[2], inv, out float nh))
                        {
                            bool nodeHasUpgrade = false;
                            CompositionFlags nodeUpgrade = default;
                            if (parts.Length == 3 && parts[2] != "-")
                            {
                                string[] nodeFlags = parts[2].Split(',');
                                if (nodeFlags.Length == 3 &&
                                    uint.TryParse(nodeFlags[0], System.Globalization.NumberStyles.Integer, inv, out uint ng) &&
                                    uint.TryParse(nodeFlags[1], System.Globalization.NumberStyles.Integer, inv, out uint nl) &&
                                    uint.TryParse(nodeFlags[2], System.Globalization.NumberStyles.Integer, inv, out uint nr))
                                {
                                    nodeHasUpgrade = true;
                                    nodeUpgrade = new CompositionFlags
                                    {
                                        m_General = (CompositionFlags.General)ng,
                                        m_Left = (CompositionFlags.Side)nl,
                                        m_Right = (CompositionFlags.Side)nr,
                                    };
                                }
                            }

                            roadNodeOffsets.Add(new float2(nx, nz));
                            roadNodeHeights.Add(nh);
                            roadNodeHasUpgrade.Add(nodeHasUpgrade);
                            roadNodeUpgrades.Add(nodeUpgrade);
                        }
                        else
                        {
                            // Indeksi su POZICIONI: preskok pokvarene linije bi
                            // pomerio sve kasnije čvorove i spojio pogrešne
                            // raskrsnice. Placeholder drži poravnanje, a putevi
                            // koji ga referišu padaju na spajanje po blizini.
                            roadNodeOffsets.Add(new float2(float.MaxValue, float.MaxValue));
                            roadNodeHeights.Add(0f);
                            roadNodeHasUpgrade.Add(false);
                            roadNodeUpgrades.Add(default);
                            Mod.Log.Warn($"Copaste: blueprint '{name}' has a malformed NETNODE line — that junction falls back to proximity welding");
                        }

                        continue;
                    }

                    if ((parts.Length == 6 || parts.Length == 7) && parts[0] == "ROAD")
                    {
                        if (!TryResolveBlueprintPrefab(parts[1], parts[2], parts[3] == "-" ? null : parts[3], out PrefabBase roadPrefab))
                        {
                            missing++;
                            continue;
                        }

                        string[] roadPoints = parts[5].Split(';');
                        if (roadPoints.Length != 4)
                        {
                            continue;
                        }

                        float2[] roadOffsets = new float2[4];
                        float[] roadHeights = new float[4];
                        bool roadValid = true;
                        for (int k = 0; k < 4; k++)
                        {
                            string[] xzh = roadPoints[k].Split(',');
                            if (xzh.Length != 3 ||
                                !TryParseBlueprintFloat(xzh[0], inv, out float rx) ||
                                !TryParseBlueprintFloat(xzh[1], inv, out float rz) ||
                                !TryParseBlueprintFloat(xzh[2], inv, out float rh))
                            {
                                roadValid = false;
                                break;
                            }

                            roadOffsets[k] = new float2(rx, rz);
                            roadHeights[k] = rh;
                        }

                        if (!roadValid)
                        {
                            continue;
                        }

                        bool hasUpgrade = false;
                        CompositionFlags upgrade = default;
                        if (parts[4] != "-")
                        {
                            string[] flags = parts[4].Split(',');
                            if (flags.Length == 3 &&
                                uint.TryParse(flags[0], System.Globalization.NumberStyles.Integer, inv, out uint general) &&
                                uint.TryParse(flags[1], System.Globalization.NumberStyles.Integer, inv, out uint left) &&
                                uint.TryParse(flags[2], System.Globalization.NumberStyles.Integer, inv, out uint right))
                            {
                                hasUpgrade = true;
                                upgrade = new CompositionFlags
                                {
                                    m_General = (CompositionFlags.General)general,
                                    m_Left = (CompositionFlags.Side)left,
                                    m_Right = (CompositionFlags.Side)right,
                                };
                            }
                        }

                        // Stari blueprint (6 polja) nema tabelu cvorova: -1 znaci
                        // "nepoznat", pa paste pada na spajanje po blizini. NIKAD
                        // ne sme da ostane podrazumevana 0 — sve deonice bi se
                        // srucile u istu tacku (blob).
                        int startNodeIndex = -1;
                        int endNodeIndex = -1;
                        if (parts.Length == 7)
                        {
                            string[] nodeIdx = parts[6].Split(',');
                            if (nodeIdx.Length != 2 ||
                                !int.TryParse(nodeIdx[0], System.Globalization.NumberStyles.Integer, inv, out startNodeIndex) ||
                                !int.TryParse(nodeIdx[1], System.Globalization.NumberStyles.Integer, inv, out endNodeIndex))
                            {
                                startNodeIndex = -1;
                                endNodeIndex = -1;
                            }
                        }

                        roadItems.Add(new NetEdgeClipboardItem
                        {
                            m_Prefab = m_PrefabSystem.GetEntity(roadPrefab),
                            m_CurveOffsets = roadOffsets,
                            m_HeightOffsets = roadHeights,
                            m_HasUpgrade = hasUpgrade,
                            m_Upgrade = upgrade,
                            m_StartNodeIndex = startNodeIndex,
                            m_EndNodeIndex = endNodeIndex,
                        });
                        continue;
                    }

                    // Ograda: "LANE|tip|ime|hash|seed|x,z,h;x,z,h;x,z,h;x,z,h".
                    if (parts.Length == 6 && parts[0] == "LANE")
                    {
                        if (!TryResolveBlueprintPrefab(parts[1], parts[2], parts[3] == "-" ? null : parts[3], out PrefabBase lanePrefab))
                        {
                            missing++;
                            continue;
                        }

                        string[] lanePoints = parts[5].Split(';');
                        if (lanePoints.Length != 4)
                        {
                            continue;
                        }

                        float2[] laneOffsets = new float2[4];
                        float[] laneHeights = new float[4];
                        bool laneValid = true;
                        for (int k = 0; k < 4; k++)
                        {
                            string[] xzh = lanePoints[k].Split(',');
                            if (xzh.Length != 3 ||
                                !TryParseBlueprintFloat(xzh[0], inv, out float lx) ||
                                !TryParseBlueprintFloat(xzh[1], inv, out float lz) ||
                                !TryParseBlueprintFloat(xzh[2], inv, out float lh))
                            {
                                laneValid = false;
                                break;
                            }

                            laneOffsets[k] = new float2(lx, lz);
                            laneHeights[k] = lh;
                        }

                        if (laneValid)
                        {
                            bool laneHasSeed = int.TryParse(parts[4], System.Globalization.NumberStyles.Integer, inv, out int laneSeed) && laneSeed >= 0 && laneSeed <= ushort.MaxValue;
                            laneItems.Add(new LaneClipboardItem
                            {
                                m_Prefab = m_PrefabSystem.GetEntity(lanePrefab),
                                m_CurveOffsets = laneOffsets,
                                m_HeightOffsets = laneHeights,
                                m_HasSeed = laneHasSeed,
                                m_Seed = laneHasSeed ? (ushort)laneSeed : (ushort)0,
                                m_PreviewSeed = loadRandom.NextInt(),
                            });
                        }

                        continue;
                    }

                    // 11 polja = najstariji format, 14 = sa drvećem (v1.0.4),
                    // 15 = sa seed-om, 16 = sa custom bojom (v1.0.6),
                    // 17 = sa hash-om prefaba (v1.2.0, samo PDX asseti).
                    // PAŽNJA: polje [16] je REZERVISANO za hash — svako buduće
                    // polje mora iza njega, uz hash uvek prisutan ("-" kad ga
                    // nema), inače broj polja postaje dvosmislen.
                    if (parts.Length != 11 && parts.Length != 14 && parts.Length != 15 && parts.Length != 16 && parts.Length != 17)
                    {
                        lastItemSkipped = true;
                        continue;
                    }

                    string itemHash = parts.Length >= 17 ? parts[16] : null;
                    if (!TryResolveBlueprintPrefab(parts[0], parts[1], itemHash, out PrefabBase prefabBase))
                    {
                        missing++;
                        lastItemSkipped = true;
                        continue;
                    }

                    // TryParse sa NumberStyles.Float, kao i AREA/LANE/ROAD
                    // linije. Bacajući Parse je izuzetak iznosio iz cele petlje,
                    // pa bi jedna ručno prepravljena linija oborila UČITAVANJE
                    // CELOG fajla; uz to Parse(string, IFormatProvider) prima
                    // grupni separator, pa je "12,5" prolazilo kao 125 i prop
                    // se tiho postavljao sto metara dalje.
                    float[] numbers = new float[9];
                    bool numbersOk = true;
                    for (int n = 0; n < numbers.Length; n++)
                    {
                        if (!TryParseBlueprintFloat(parts[n + 2], inv, out numbers[n]) ||
                            !IsFiniteBlueprintNumber(numbers[n]))
                        {
                            numbersOk = false;
                            break;
                        }
                    }

                    if (!numbersOk)
                    {
                        lastItemSkipped = true;
                        continue;
                    }

                    Entity prefabEntity = m_PrefabSystem.GetEntity(prefabBase);
                    ClipboardItem item = new ClipboardItem
                    {
                        m_Prefab = prefabEntity,
                        m_Offset = new float3(numbers[0], numbers[1], numbers[2]),
                        m_Rotation = new quaternion(numbers[3], numbers[4], numbers[5], numbers[6]),
                        m_HeightOffset = numbers[7],
                        m_Diameter = numbers[8],
                    };

                    if (parts.Length >= 14 && parts[11] == "1" &&
                        int.TryParse(parts[12], System.Globalization.NumberStyles.Integer, inv, out int treeState) &&
                        byte.TryParse(parts[13], System.Globalization.NumberStyles.Integer, inv, out byte treeGrowth))
                    {
                        item.m_HadTree = true;
                        item.m_Tree = new Game.Objects.Tree
                        {
                            m_State = (Game.Objects.TreeState)treeState,
                            m_Growth = treeGrowth,
                        };
                    }

                    // Opseg, ne samo znak: (ushort)65536 je 0, pa bi seed
                    // izvan opsega tiho promenio izgled propa (ROAD/LANE grane
                    // to već ograničavaju).
                    if (parts.Length >= 15 && int.TryParse(parts[14], System.Globalization.NumberStyles.Integer, inv, out int seedValue) &&
                        seedValue >= 0 && seedValue <= ushort.MaxValue)
                    {
                        item.m_HasSeed = true;
                        item.m_Seed = (ushort)seedValue;
                    }

                    if (parts.Length >= 16 && TryParseColorSet(parts[15], inv, out Game.Rendering.ColorSet loadedColor))
                    {
                        item.m_HasCustomColor = true;
                        item.m_CustomColor = loadedColor;
                    }

                    item.m_PreviewSeed = loadRandom.NextInt();
                    items.Add(item);
                    lastItemSkipped = false;
                }

                if (items.Count == 0 && areaItems.Count == 0 && laneItems.Count == 0 && roadItems.Count == 0)
                {
                    Mod.Log.Warn($"Blueprint '{name}': nothing could be resolved ({missing} missing)");
                    return false;
                }

                m_Clipboard.Clear();
                m_Clipboard.AddRange(items);
                m_ClipboardAreas.Clear();
                m_ClipboardAreas.AddRange(areaItems);
                m_ClipboardLanes.Clear();
                m_ClipboardLanes.AddRange(laneItems);
                // Indeksi van tabele (osteceni/rucno menjani fajl) padaju na
                // spajanje po blizini — kao stari format bez tabele.
                for (int r = 0; r < roadItems.Count; r++)
                {
                    NetEdgeClipboardItem item = roadItems[r];
                    if (item.m_StartNodeIndex >= roadNodeOffsets.Count ||
                        (item.m_StartNodeIndex >= 0 && roadNodeOffsets[item.m_StartNodeIndex].x == float.MaxValue)) { item.m_StartNodeIndex = -1; }
                    if (item.m_EndNodeIndex >= roadNodeOffsets.Count ||
                        (item.m_EndNodeIndex >= 0 && roadNodeOffsets[item.m_EndNodeIndex].x == float.MaxValue)) { item.m_EndNodeIndex = -1; }
                    roadItems[r] = item;
                }

                // Marker bez ispravnog cvora se odbacuje (neispravna
                // NETNODE linija je ostavila mesto-drzac sa float.MaxValue).
                for (int m = roadNodeMarkers.Count - 1; m >= 0; m--)
                {
                    int markerIndex = roadNodeMarkers[m].m_NodeIndex;
                    if (markerIndex >= roadNodeOffsets.Count ||
                        roadNodeOffsets[markerIndex].x == float.MaxValue)
                    {
                        roadNodeMarkers.RemoveAt(m);
                    }
                }

                // Fixup prethodnog otiska još ume da radi, a njegovi node
                // indeksi pokazuju u tabelu koju upravo menjamo — bez ovoga
                // bi markeri novog blueprinta završili na prethodnoj
                // raskrsnici.
                InvalidatePendingNodeFixups();

                m_ClipboardNetEdges.Clear();
                m_ClipboardNetEdges.AddRange(roadItems);
                ResetClipboardNetNodes(roadNodeOffsets, roadNodeHeights, roadNodeHasUpgrade, roadNodeUpgrades, roadNodeMarkers);
                Mod.Log.Info($"Copaste: blueprint '{name}' loaded ({items.Count} objects, {areaItems.Count} surfaces, {laneItems.Count} fences, {roadItems.Count} road segments, {missing} missing)");
                return true;
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Blueprint load failed: {e.Message}");
                return false;
            }
        }

        // PrefabID je (tip, ime, HASH) i Equals poredi sva tri: PDX asseti se
        // registruju SA hashom (iz platformID-a), vanila sa praznim — lookup bez
        // hasha ih zato ne nalazi. Blueprint linije od sada nose i hash; čita se
        // kroz JAVNI ToUrlSegment() ("tip/ime/hash" kad hash postoji; tip i ime
        // su URL-eskejpovani pa ne mogu da sadrže '/').
        private static string GetPrefabHashString(PrefabBase prefabBase)
        {
            try
            {
                string[] segments = prefabBase.GetPrefabID().ToUrlSegment().Split('/');
                return segments.Length >= 3 ? segments[2] : string.Empty;
            }
            catch (System.Exception)
            {
                return string.Empty;
            }
        }

        // Prefab lookup za blueprint linije: prvo sa hashom (PDX asseti), pa bez
        // njega (vanila; i stariji fajlovi koji hash polje nemaju).
        private bool TryResolveBlueprintPrefab(string type, string name, string hash, out PrefabBase prefabBase)
        {
            bool hashTried = false;
            if (!string.IsNullOrEmpty(hash) &&
                Colossal.Hash128.TryParse(hash, out Colossal.Hash128 parsed) &&
                parsed.isValid)
            {
                hashTried = true;
                if (m_PrefabSystem.TryGetPrefab(new PrefabID(type, name, parsed), out prefabBase) &&
                    prefabBase != null)
                {
                    return true;
                }
            }

            if (m_PrefabSystem.TryGetPrefab(new PrefabID(type, name), out prefabBase) && prefabBase != null)
            {
                if (hashTried)
                {
                    // Vidljivost: isti tip+ime, drugi asset (hash se ne poklapa) —
                    // učitava se imenjak umesto da se broji kao nedostajući.
                    Mod.Log.Info($"Copaste: blueprint prefab '{name}' resolved by name only (asset hash mismatch)");
                }

                return true;
            }

            return false;
        }

        // ColorSet ↔ string za blueprint fajlove: "r,g,b,a;r,g,b,a;r,g,b,a".
        private static string SerializeColorSet(Game.Rendering.ColorSet colorSet, System.Globalization.CultureInfo inv)
        {
            string Channel(UnityEngine.Color c) =>
                c.r.ToString("R", inv) + "," + c.g.ToString("R", inv) + "," + c.b.ToString("R", inv) + "," + c.a.ToString("R", inv);
            return Channel(colorSet.m_Channel0) + ";" + Channel(colorSet.m_Channel1) + ";" + Channel(colorSet.m_Channel2);
        }

        private static bool TryParseColorSet(string text, System.Globalization.CultureInfo inv, out Game.Rendering.ColorSet colorSet)
        {
            colorSet = default;
            if (string.IsNullOrEmpty(text) || text == "-")
            {
                return false;
            }

            string[] channels = text.Split(';');
            if (channels.Length != 3)
            {
                return false;
            }

            UnityEngine.Color[] parsed = new UnityEngine.Color[3];
            for (int i = 0; i < 3; i++)
            {
                string[] rgba = channels[i].Split(',');
                if (rgba.Length != 4 ||
                    !TryParseBlueprintFloat(rgba[0], inv, out float r) ||
                    !TryParseBlueprintFloat(rgba[1], inv, out float g) ||
                    !TryParseBlueprintFloat(rgba[2], inv, out float b) ||
                    !TryParseBlueprintFloat(rgba[3], inv, out float a))
                {
                    return false;
                }

                parsed[i] = new UnityEngine.Color(r, g, b, a);
            }

            colorSet = new Game.Rendering.ColorSet
            {
                m_Channel0 = parsed[0],
                m_Channel1 = parsed[1],
                m_Channel2 = parsed[2],
            };
            return true;
        }

        public void DeleteBlueprint(string name)
        {
            try
            {
                name = SanitizeBlueprintName(name);
                if (name == null)
                {
                    return;
                }

                string path = System.IO.Path.Combine(BlueprintDirectory, name + ".txt");
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                    Mod.Log.Info($"Copaste: blueprint '{name}' deleted");
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Blueprint delete failed: {e.Message}");
            }
        }

        public void RenameBlueprint(string oldName, string newName)
        {
            try
            {
                oldName = SanitizeBlueprintName(oldName);
                newName = SanitizeBlueprintName(newName);
                if (oldName == null || newName == null || newName == oldName)
                {
                    return;
                }

                string oldPath = System.IO.Path.Combine(BlueprintDirectory, oldName + ".txt");
                string newPath = System.IO.Path.Combine(BlueprintDirectory, newName + ".txt");
                if (System.IO.File.Exists(oldPath) && !System.IO.File.Exists(newPath))
                {
                    System.IO.File.Move(oldPath, newPath);
                    Mod.Log.Info($"Copaste: blueprint '{oldName}' renamed to '{newName}'");
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Blueprint rename failed: {e.Message}");
            }
        }

        private void OnToggleInteraction(ProxyAction action, InputActionPhase phase)
        {
            if (phase == InputActionPhase.Performed)
            {
                ToggleTool();
            }
        }
    }
}
