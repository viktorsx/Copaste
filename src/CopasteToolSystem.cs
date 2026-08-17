// Copaste — copy/paste tool for props in Cities: Skylines II.
// Selection/highlight patterns based on MIT-licensed mods by yenyang;
// placement via the game's definition pipeline as used by Line Tool (Apache-2.0, algernon).

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

    public partial class CopasteToolSystem : ToolBaseSystem
    {
        // Zaštitni limiti — velike selekcije guše igru (definicije/overlay po frejmu).
        private const int kMaxSelection = 1000;
        private const int kMaxOverlayCircles = 400;

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

        public int SelectedCount => m_Selected.Count + m_SelectedSurfaces.Count;

        // Broj selektovanih NE-zgrada sa transformom — visinske i align
        // operacije deluju samo na njih, pa UI dugmad gate-uju na ovo
        // (inače bi bila upaljena dugmad koja ništa ne rade).
        public int PropTargetCount
        {
            get
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
        }

        public int ClipboardCount => m_Clipboard.Count + m_ClipboardAreas.Count;

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
        // je objekat BEZ Game.Objects.Surface (kolizione površine — MoveIt
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

            if (m_Mode == Mode.Paste || m_Mode == Mode.Relocate || m_MoveDragging || m_MarqueeHeld)
            {
                // Net je uključen da bi se grupa mogla nalepiti na površinu puta/staze, ne samo na teren.
                // Isti mask važi tokom pomeranja i marquee razvlačenja — da raycast ne pogađa
                // propove koje vučemo, niti "iskače" na zgrade preko kojih kursor prelazi.
                m_ToolRaycastSystem.typeMask = TypeMask.Terrain | TypeMask.Net;
                m_ToolRaycastSystem.netLayerMask = Game.Net.Layer.Road | Game.Net.Layer.Pathway | Game.Net.Layer.PublicTransportRoad;
                m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
            }
            else
            {
                // Teren je uključen da bi marquee imao početnu tačku na praznom tlu.
                m_ToolRaycastSystem.typeMask = TypeMask.StaticObjects | TypeMask.Terrain;
                m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
                m_ToolRaycastSystem.raycastFlags |= RaycastFlags.Decals | RaycastFlags.SubElements;
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

        protected override void OnStartRunning()
        {
            base.OnStartRunning();

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
            m_Selected.Clear();
            m_SelectedSurfaces.Clear();
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

                RunPostPasteFix();
                RunSubPropFix();
                RunDelayedSettles();
                RunPendingSurfacePrunes();

                if (m_UiTyping)
                {
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
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Copaste: gesture cleanup failed: {e.Message}");
            }

            FlushBuildingFixups();
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
            m_RightHeld = false;
            m_RightDragging = false;
            m_SameFilterPrefab = Entity.Null;
            SetSameFilterName();
            m_HeightPickArmed = false;
            m_AlignPickArmed = false;
            m_PostPasteFix = null;
            m_PostPasteFixFrames = 0;
            m_HoverEntity = Entity.Null;
        }

        // Da li je kursor iznad UI-ja (naš panel, meniji igre) — sirove mišje akcije se tada ignorišu.
        private static bool MouseOverUI => InputManager.instance != null && InputManager.instance.mouseOverUI;

        // Klik čitamo i direktno sa miša: drugi modovi (npr. Line Tool) drže globalne
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

                if (m_SelectedSurfaces.Contains(areas[i]) ||
                    !EntityManager.TryGetBuffer(areas[i], true, out DynamicBuffer<Game.Areas.Node> nodes) ||
                    nodes.Length < 3)
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
            m_SelectionFromMarquee = true;
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

            HashSet<Entity> selectedSet = new HashSet<Entity>(m_Selected);
            for (int i = 0; i < m_MarqueeHits.Count; i++)
            {
                Entity entity = m_MarqueeHits[i];
                if (m_Selected.Count >= kMaxSelection)
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
        }

        private void ApplyClickSelection(Entity entity, bool shiftHeld)
        {
            EndAlignSession();
            if (entity == Entity.Null || !EntityManager.Exists(entity))
            {
                return;
            }

            m_SelectionFromMarquee = false;

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

        private void BeginMoveDrag(float3 anchor)
        {
            EndAlignSession();
            // Prop pod mišem ulazi u selekciju ako već nije u njoj.
            if (!m_Selected.Contains(m_LeftPressEntity) && EntityManager.Exists(m_LeftPressEntity))
            {
                m_SelectionFromMarquee = false;
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

            overlayBuffer.DrawLine(kSelectedColor, new Line3.Segment(c00, c10), 0.3f);
            overlayBuffer.DrawLine(kSelectedColor, new Line3.Segment(c10, c11), 0.3f);
            overlayBuffer.DrawLine(kSelectedColor, new Line3.Segment(c11, c01), 0.3f);
            overlayBuffer.DrawLine(kSelectedColor, new Line3.Segment(c01, c00), 0.3f);
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

            if (count > 0)
            {
                return center / count;
            }

            // Selekcija od samih površina: centar iz centroida poligona.
            // Zgradine se preskaču — ne rotiraju se, pa ne pomeraju ni pivot.
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

            m_PasteDirty = true;
        }

        private void DeleteSelection()
        {
            EndAlignSession();

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
            if (snapshots.Count > 0 || surfaceSnapshots.Count > 0)
            {
                PushUndo(new UndoRecord { m_Kind = UndoKind.Delete, m_Snapshots = snapshots, m_Surfaces = surfaceSnapshots });
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

            m_Selected.Clear();
            m_SelectedSurfaces.Clear();

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
                for (int i = 0; i < areaSubs.Length; i++)
                {
                    Entity sub = areaSubs[i].m_SubObject;
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

            UpdateRightButton(out float rotationDelta, out bool rightClick);

            // Rotacija selekcije desnim prevlačenjem.
            if (rotationDelta != 0f && (m_Selected.Count > 0 || m_SelectedSurfaces.Count > 0))
            {
                RotateSelection(rotationDelta);
            }

            bool raycastValid = GetRaycastResult(out Entity raycastEntity, out RaycastHit hit);
            Entity hitEntity = raycastValid && IsCopyable(raycastEntity) ? raycastEntity : Entity.Null;

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
                m_HeightPickArmed = !m_HeightPickArmed && m_Selected.Count > 0;
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
            if (m_HeightPickArmed && m_Selected.Count == 0)
            {
                m_HeightPickArmed = false;
            }

            // Pritisak: na propu = potencijalni klik ili početak pomeranja; na praznom tlu = početak marquee-a.
            bool ctrlHeld = Keyboard.current != null &&
                (Keyboard.current.leftCtrlKey.isPressed || Keyboard.current.rightCtrlKey.isPressed);
            Entity ctrlPick = Entity.Null;

            if (ClickedThisFrame())
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
                else if (raycastValid && !m_LeftHeldOnProp &&
                    (raycastEntity == Entity.Null || !EntityManager.HasComponent<Game.Objects.Object>(raycastEntity)))
                {
                    // Marquee počinje samo na tlu — klik na zgradu ne sme da "usidri" ugao na njen krov.
                    m_MarqueeHeld = true;
                    m_MarqueeActive = false;
                    m_MarqueeStart = hit.m_HitPosition;
                    m_MarqueeEnd = m_MarqueeStart;

                    // Okvir se poravnava sa uglom kamere, kao u Move It-u.
                    UnityEngine.Camera camera = UnityEngine.Camera.main;
                    float3 forward = camera != null ? (float3)camera.transform.forward : new float3(0f, 0f, 1f);
                    m_MarqueeForward = math.normalizesafe(forward.xz, new float2(0f, 1f));
                    m_MarqueeRight = new float2(m_MarqueeForward.y, -m_MarqueeForward.x);
                }
            }

            // Pomeranje: prevlačenje sa propa vuče celu selekciju.
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
                    m_SelectionFromMarquee = false;
                    if (shiftHeld)
                    {
                        if (!m_SelectedSurfaces.Remove(clickedSurface))
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
                if (m_AlignPickArmed)
                {
                    m_AlignPickArmed = false;
                }
                else if (m_HeightPickArmed)
                {
                    m_HeightPickArmed = false;
                }
                else if (m_Selected.Count > 0)
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
            bool gestureActive = m_MoveDragging || m_RightDragging || m_MarqueeActive;

            if (m_DeleteAction.WasPressedThisFrame() && !gestureActive &&
                (m_Selected.Count > 0 || m_SelectedSurfaces.Count > 0))
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
                    if (UnityEngine.Time.time - m_LastAltSpinTime > 1f)
                    {
                        PushTransformUndo();
                    }

                    m_LastAltSpinTime = UnityEngine.Time.time;
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
            if (heightDelta != 0f && SelectionHasHeightTargets())
            {
                if (m_RaiseAction.WasPressedThisFrame() || m_LowerAction.WasPressedThisFrame())
                {
                    PushTransformUndo();
                }

                AdjustSelectionHeight(heightDelta);
            }

            // Copy: kopiraj selekciju u clipboard.
            if (m_CopyAction.WasPressedThisFrame() && (m_Selected.Count > 0 || m_SelectedSurfaces.Count > 0))
            {
                CopySelection();
            }

            // Paste: pređi u paste mod ako clipboard nije prazan.
            if (m_PasteAction.WasPressedThisFrame() && ClipboardCount > 0)
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
                return;
            }

            // "Anarchy": kaži validaciji igre da ignoriše greške postavljanja (preklapanja itd.).
            // Namerno bez skidanja Error komponenti — ignoreErrors je zvaničan i dovoljan mehanizam,
            // a globalno skidanje je diralo i entitete koji nisu naši.
            m_ToolSystem.ignoreErrors = Mod.Settings.AnarchyPaste || m_PreviousIgnoreErrors;

            float3 anchor = hit.m_HitPosition;
            anchor = ApplyRoadSnap(anchor);
            DrawPasteOverlays(anchor);

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

                // U ovom frejmu upiti vide samo pre-postojeće entitete (novi
                // nastaju tek posle Apply) — popis "dvojnika" za rezoluciju,
                // da undo ne obriše tuđu identičnu zgradu.
                m_PostPasteExclude = CollectPreStampMatches(m_PostPasteFix);

                PushUndo(new UndoRecord { m_Kind = UndoKind.Paste, m_Pasted = m_PostPasteFix });
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

        private void DrawSelectOverlays()
        {
            if (m_Selected.Count == 0 && m_SelectedSurfaces.Count == 0 &&
                m_HoverEntity == Entity.Null && !m_MarqueeActive)
            {
                return;
            }

            OverlayRenderSystem.Buffer overlayBuffer = m_OverlayRenderSystem.GetBuffer(out JobHandle _);

            // Selektovane površine: obris poligona u boji selekcije.
            foreach (Entity area in m_SelectedSurfaces)
            {
                if (!EntityManager.Exists(area) ||
                    !EntityManager.TryGetBuffer(area, true, out DynamicBuffer<Game.Areas.Node> nodes) ||
                    nodes.Length < 2)
                {
                    continue;
                }

                for (int n = 0; n < nodes.Length; n++)
                {
                    float3 a = nodes[n].m_Position;
                    float3 b = nodes[(n + 1) % nodes.Length].m_Position;
                    overlayBuffer.DrawLine(kSelectedColor, new Line3.Segment(a, b), 0.3f);
                }
            }

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
            m_Clipboard.Clear();
            m_ClipboardAreas.Clear();

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
                // Selekcija bez objekata: kopiraju se samo površine, centroid iz njih.
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

            Mod.Log.Info($"Copaste: copied {m_Clipboard.Count} objects, {m_ClipboardAreas.Count} surfaces");
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
            foreach (Entity entity in m_Selected)
            {
                if (entity != m_HoverEntity)
                {
                    Unhighlight(entity);
                }
            }

            m_Selected.Clear();
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
            if (ToolIsActive && m_Mode == Mode.Select && (m_Selected.Count > 0 || m_SelectedSurfaces.Count > 0))
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
                (m_Selected.Count > 0 || m_SelectedSurfaces.Count > 0))
            {
                DeleteSelection();
            }
        }

        public void TriggerUndo()
        {
            // Tokom Relocate-a undo bi pojeo zapis samog premeštanja i teleportovao
            // zgradu — panel akcije su zamrznute dok premeštanje traje. Isto i
            // tokom aktivnog drag-a/rotacije (zapis sopstvenog poteza).
            if (ToolIsActive && m_Mode != Mode.Relocate && !m_MoveDragging && !m_RightDragging)
            {
                Undo();
                if (m_Mode == Mode.Paste)
                {
                    m_PasteDirty = true;
                }
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
                    }
                }

                boundsMin -= 0.5f;
                boundsMax += 0.5f;

                ResolvePastedFromQuery(m_PropQuery, boundsMin, boundsMax, claimed);

                // Zgrade u clipboardu: rezolucija i preko building upita.
                // Bez uslova na IncludeBuildings — paste je mogao da se desi pre gašenja.
                foreach (PastedRecord record in m_PostPasteFix)
                {
                    if (record.m_Resolved == Entity.Null && !record.m_IsArea)
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
            }

            if (m_PostPasteFixFrames == 0)
            {
                // Bez Clear — istu listu drži undo zapis (sa razrešenim entitetima).
                m_PostPasteFix = null;
                m_PostPasteExclude = null;
            }
        }

        // Popiši postojeće entitete koji po prefabu i poziciji odgovaraju
        // nalepljenim zapisima — kandidati koje rezolucija NE sme da usvoji.
        private HashSet<Entity> CollectPreStampMatches(List<PastedRecord> records)
        {
            HashSet<Entity> exclude = new HashSet<Entity>();
            bool anyObject = false;
            bool anyArea = false;
            float3 boundsMin = new float3(float.MaxValue);
            float3 boundsMax = new float3(float.MinValue);
            foreach (PastedRecord record in records)
            {
                boundsMin = math.min(boundsMin, record.m_Position);
                boundsMax = math.max(boundsMax, record.m_Position);
                anyArea |= record.m_IsArea;
                anyObject |= !record.m_IsArea;
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

            return false;
        }

        // Broj tih meta za UI (gate visinskih dugmadi).
        public int HeightTargetCount
        {
            get
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

                return count;
            }
        }

        private void PushTransformUndo()
        {
            List<TransformSnapshot> snapshots = SnapshotSelection();
            List<SurfaceSnapshot> surfaces = SnapshotSurfaces(m_SelectedSurfaces);
            if (snapshots.Count > 0 || surfaces.Count > 0)
            {
                PushUndo(new UndoRecord { m_Kind = UndoKind.Transforms, m_Snapshots = snapshots, m_Surfaces = surfaces });
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
                    if (redoSnapshots.Count > 0 || redoSurfaces.Count > 0)
                    {
                        m_RedoStack.Add(new UndoRecord { m_Kind = UndoKind.Transforms, m_Snapshots = redoSnapshots, m_Surfaces = redoSurfaces });
                        if (m_RedoStack.Count > kMaxUndo)
                        {
                            m_RedoStack.RemoveAt(0);
                        }
                    }

                    ApplyTransformSnapshots(record.m_Snapshots);
                    if (record.m_Surfaces != null)
                    {
                        ApplySurfaceSnapshots(record.m_Surfaces);
                    }

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

                    break;

                case UndoKind.Paste:
                    // Pre brisanja: PUNO stanje nalepljenih (paste zapisi nemaju
                    // rotaciju ni poligone) — redo iz ovoga ponovo stvara.
                    record.m_Snapshots = SnapshotResolvedPasted(record.m_Pasted);
                    record.m_Surfaces = SnapshotResolvedPastedAreas(record.m_Pasted);

                    DeletePastedEntities(record.m_Pasted);

                    // Ako se fixup za taj stamp još vrti, prekini ga — entiteti su upravo obrisani.
                    if (ReferenceEquals(record.m_Pasted, m_PostPasteFix))
                    {
                        m_PostPasteFix = null;
                        m_PostPasteFixFrames = 0;
                        m_PostPasteExclude = null;
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

            switch (record.m_Kind)
            {
                case UndoKind.Transforms:
                    // Trenutno stanje nazad na undo stek — direktno, ne kroz PushUndo
                    // (PushUndo bi obrisao ostatak redo steka).
                    List<TransformSnapshot> undoSnapshots = SnapshotEntities(record.m_Snapshots);
                    List<SurfaceSnapshot> undoSurfaces = record.m_Surfaces != null
                        ? SnapshotSurfaceEntities(record.m_Surfaces)
                        : new List<SurfaceSnapshot>();
                    if (undoSnapshots.Count > 0 || undoSurfaces.Count > 0)
                    {
                        m_UndoStack.Add(new UndoRecord { m_Kind = UndoKind.Transforms, m_Snapshots = undoSnapshots, m_Surfaces = undoSurfaces });
                        if (m_UndoStack.Count > kMaxUndo)
                        {
                            m_UndoStack.RemoveAt(0);
                        }
                    }

                    ApplyTransformSnapshots(record.m_Snapshots);
                    if (record.m_Surfaces != null)
                    {
                        ApplySurfaceSnapshots(record.m_Surfaces);
                    }

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

                    break;
            }

            Mod.Log.Info($"Copaste: redo ({record.m_Kind})");
        }

        public int RedoCount => m_RedoStack.Count;

        public void TriggerRedo()
        {
            if (ToolIsActive && m_Mode == Mode.Select && !m_MoveDragging && !m_RightDragging)
            {
                Redo();
            }
        }

        // Isprazni clipboard (objekti + površine); iz paste moda vrati u selekciju.
        public void TriggerClearClipboard()
        {
            if (m_Mode == Mode.Relocate)
            {
                return;
            }

            m_Clipboard.Clear();
            m_ClipboardAreas.Clear();
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
            }
        }

        // Ponovno kreiranje obrisanog propa direktno iz arhetipa prefaba (LineToolLite pristup).
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

        private void DeletePastedEntities(List<PastedRecord> records)
        {
            if (records == null || records.Count == 0)
            {
                return;
            }

            // Prvo razrešeni zapisi: brišemo TAČNO entitete koje je paste stvorio.
            HashSet<Entity> deleted = new HashSet<Entity>();
            int unresolvedCount = 0;
            foreach (PastedRecord record in records)
            {
                if (record.m_Resolved != Entity.Null)
                {
                    if (EntityManager.Exists(record.m_Resolved) && deleted.Add(record.m_Resolved))
                    {
                        m_Selected.Remove(record.m_Resolved);
                        EntityManager.AddComponent<Deleted>(record.m_Resolved);
                    }
                }
                else
                {
                    unresolvedCount++;
                }
            }

            if (unresolvedCount == 0)
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
            }

            boundsMin -= 0.5f;
            boundsMax += 0.5f;

            // Fallback mora da pokrije i zgrade i površine — prop upit ih
            // isključuje, pa bi nerazrešene nalepljene zgrade/površine
            // preživele undo.
            bool[] recordUsed = new bool[records.Count];
            DeleteUnresolvedFromQuery(m_PropQuery, records, recordUsed, boundsMin, boundsMax, deleted);
            DeleteUnresolvedFromQuery(m_BuildingQuery, records, recordUsed, boundsMin, boundsMax, deleted);
            DeleteUnresolvedAreas(records, recordUsed, boundsMin, boundsMax, deleted);
        }

        private void DeleteUnresolvedFromQuery(EntityQuery query, List<PastedRecord> records, bool[] recordUsed, float3 boundsMin, float3 boundsMax, HashSet<Entity> deleted)
        {
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms = query.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            NativeArray<PrefabRef> prefabRefs = query.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                float3 position = transforms[i].m_Position;
                if (math.any(position < boundsMin) || math.any(position > boundsMax) ||
                    deleted.Contains(entities[i]))
                {
                    continue;
                }

                for (int j = 0; j < records.Count; j++)
                {
                    PastedRecord record = records[j];
                    if (recordUsed[j] || record.m_IsArea || record.m_Resolved != Entity.Null ||
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
        private void DeleteUnresolvedAreas(List<PastedRecord> records, bool[] recordUsed, float3 boundsMin, float3 boundsMax, HashSet<Entity> deleted)
        {
            NativeArray<Entity> areas = m_SurfaceQuery.ToEntityArray(Allocator.Temp);
            NativeArray<PrefabRef> prefabRefs = m_SurfaceQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            for (int i = 0; i < areas.Length; i++)
            {
                if (deleted.Contains(areas[i]) ||
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
        private bool m_SelectionFromMarquee;
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
                Entity entity;
                if (m_Selected.Count == 1)
                {
                    entity = m_Selected[0];
                }
                else if (m_Selected.Count == 0 && m_SelectedSurfaces.Count == 1)
                {
                    entity = m_SelectedSurfaces[0];
                }
                else
                {
                    return string.Empty;
                }

                if (entity != m_SelectedNameEntity)
                {
                    m_SelectedNameEntity = entity;
                    m_SelectedName =
                        EntityManager.Exists(entity) &&
                        EntityManager.TryGetComponent(entity, out PrefabRef prefabRef) &&
                        m_PrefabSystem.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefabBase) &&
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
        private const int kSelectionListMax = 50;
        private Entity m_ListFocusEntity = Entity.Null;
        private readonly Dictionary<Entity, string> m_PrefabNameCache = new Dictionary<Entity, string>();

        public string GetSelectionList()
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
            m_SelectionFromMarquee = false;
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
            if (!EntityManager.TryGetComponent(sourceEntity, out Game.Objects.Transform sourceTransform))
            {
                return;
            }

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();

            // Apsolutno poravnanje: svi na tačno istu svetsku visinu kao uzor-prop.
            float targetY = sourceTransform.m_Position.y;

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
            // snimio stari sadržaj (1.0.4 lekcija).
            if (m_Selected.Count > 0 || m_SelectedSurfaces.Count > 0)
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

                    lines.Add(string.Join("|", new string[]
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
                    }));

                    // Plac zgrade: "BLOT|n" pa po jedna "BSURF|tip|ime|x,z;..."
                    // linija po površini (lokalni poligon) — važi za POSLEDNJU
                    // učitanu stavku. BLOT i sa nulom, da se razlikuje "izvor
                    // bez površina" (briši sve) od "nema podataka" (fabrički).
                    // Stariji loaderi obe linije preskaču (broj polja/tip).
                    if (item.m_SurfaceSigs != null)
                    {
                        int written = 0;
                        List<string> surfLines = new List<string>();
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

                            surfLines.Add(string.Join("|", new string[] { "BSURF", sigType, sigName, poly.ToString() }));
                            written++;
                        }

                        lines.Add("BLOT|" + written.ToString(inv));
                        lines.AddRange(surfLines);
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

                    lines.Add(string.Join("|", new string[] { "AREA", areaType, areaName, polygon.ToString() }));
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

                    if (parts.Length == 4 && parts[0] == "BSURF")
                    {
                        if (items.Count == 0 || lastItemSkipped ||
                            items[items.Count - 1].m_SurfaceSigs == null)
                        {
                            continue;
                        }

                        if (!m_PrefabSystem.TryGetPrefab(new PrefabID(parts[1], parts[2]), out PrefabBase sigPrefab) ||
                            sigPrefab == null)
                        {
                            // Površina placa čiji prefab nedostaje — broji se u
                            // "missing" da korisnik vidi da nešto fali.
                            missing++;
                            continue;
                        }

                        string[] sigPairs = parts[3].Split(';');
                        if (sigPairs.Length < 3)
                        {
                            continue;
                        }

                        float2[] localNodes = new float2[sigPairs.Length];
                        bool sigValid = true;
                        for (int n = 0; n < sigPairs.Length; n++)
                        {
                            string[] xy = sigPairs[n].Split(',');
                            if (xy.Length != 2 ||
                                !float.TryParse(xy[0], System.Globalization.NumberStyles.Float, inv, out float sx) ||
                                !float.TryParse(xy[1], System.Globalization.NumberStyles.Float, inv, out float sz))
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

                        continue;
                    }

                    // Farbana površina: "AREA|tip|ime|x,z;x,z;...".
                    if (parts.Length == 4 && parts[0] == "AREA")
                    {
                        if (!m_PrefabSystem.TryGetPrefab(new PrefabID(parts[1], parts[2]), out PrefabBase areaPrefab) || areaPrefab == null)
                        {
                            missing++;
                            continue;
                        }

                        string[] pairs = parts[3].Split(';');
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
                                !float.TryParse(xy[0], System.Globalization.NumberStyles.Float, inv, out float x) ||
                                !float.TryParse(xy[1], System.Globalization.NumberStyles.Float, inv, out float z))
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

                    // 11 polja = najstariji format, 14 = sa drvećem (v1.0.4),
                    // 15 = sa seed-om, 16 = sa custom bojom (v1.0.6).
                    if (parts.Length != 11 && parts.Length != 14 && parts.Length != 15 && parts.Length != 16)
                    {
                        lastItemSkipped = true;
                        continue;
                    }

                    if (!m_PrefabSystem.TryGetPrefab(new PrefabID(parts[0], parts[1]), out PrefabBase prefabBase) || prefabBase == null)
                    {
                        missing++;
                        lastItemSkipped = true;
                        continue;
                    }

                    Entity prefabEntity = m_PrefabSystem.GetEntity(prefabBase);
                    ClipboardItem item = new ClipboardItem
                    {
                        m_Prefab = prefabEntity,
                        m_Offset = new float3(
                            float.Parse(parts[2], inv),
                            float.Parse(parts[3], inv),
                            float.Parse(parts[4], inv)),
                        m_Rotation = new quaternion(
                            float.Parse(parts[5], inv),
                            float.Parse(parts[6], inv),
                            float.Parse(parts[7], inv),
                            float.Parse(parts[8], inv)),
                        m_HeightOffset = float.Parse(parts[9], inv),
                        m_Diameter = float.Parse(parts[10], inv),
                    };

                    if (parts.Length >= 14 && parts[11] == "1")
                    {
                        item.m_HadTree = true;
                        item.m_Tree = new Game.Objects.Tree
                        {
                            m_State = (Game.Objects.TreeState)int.Parse(parts[12], inv),
                            m_Growth = byte.Parse(parts[13], inv),
                        };
                    }

                    if (parts.Length >= 15 && int.TryParse(parts[14], System.Globalization.NumberStyles.Integer, inv, out int seedValue) && seedValue >= 0)
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

                if (items.Count == 0 && areaItems.Count == 0)
                {
                    Mod.Log.Warn($"Blueprint '{name}': nothing could be resolved ({missing} missing)");
                    return false;
                }

                m_Clipboard.Clear();
                m_Clipboard.AddRange(items);
                m_ClipboardAreas.Clear();
                m_ClipboardAreas.AddRange(areaItems);
                Mod.Log.Info($"Copaste: blueprint '{name}' loaded ({items.Count} objects, {areaItems.Count} surfaces, {missing} missing)");
                return true;
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Blueprint load failed: {e.Message}");
                return false;
            }
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
                    !float.TryParse(rgba[0], System.Globalization.NumberStyles.Float, inv, out float r) ||
                    !float.TryParse(rgba[1], System.Globalization.NumberStyles.Float, inv, out float g) ||
                    !float.TryParse(rgba[2], System.Globalization.NumberStyles.Float, inv, out float b) ||
                    !float.TryParse(rgba[3], System.Globalization.NumberStyles.Float, inv, out float a))
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
