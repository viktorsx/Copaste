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
        private static readonly UnityEngine.Color kPasteColor = new UnityEngine.Color(0.3f, 1f, 0.45f, 0.9f);

        private enum Mode
        {
            Select,
            Paste,
        }

        private struct ClipboardItem
        {
            public Entity m_Prefab;
            public float3 m_Offset;
            public quaternion m_Rotation;
            public float m_HeightOffset;
            public float m_Diameter;
            public bool m_HadTree;
            public Game.Objects.Tree m_Tree;

            // PseudoRandomSeed originala — određuje varijaciju boje/izgleda.
            public bool m_HasSeed;
            public ushort m_Seed;
        }

        public int SelectedCount => m_Selected.Count;

        public int ClipboardCount => m_Clipboard.Count;

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
        private ProxyAction m_SelectSameAction;
        private ProxyAction m_SnapGroundAction;
        private ProxyAction m_MatchHeightAction;
        private bool m_HeightPickArmed;
        private ProxyAction m_NudgeUpAction;
        private ProxyAction m_NudgeDownAction;
        private ProxyAction m_NudgeLeftAction;
        private ProxyAction m_NudgeRightAction;
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
            public Game.Objects.Transform m_Transform;
            public float m_Elevation;
            public bool m_HadElevation;
            public bool m_HadTree;
            public Game.Objects.Tree m_Tree;
            public bool m_HasSeed;
            public ushort m_Seed;
        }

        private class UndoRecord
        {
            public UndoKind m_Kind;
            public List<TransformSnapshot> m_Snapshots;
            public List<PastedRecord> m_Pasted;
        }

        private const int kMaxUndo = 32;
        private readonly List<UndoRecord> m_UndoStack = new List<UndoRecord>();

        private Mode m_Mode = Mode.Select;
        private Entity m_HoverEntity = Entity.Null;
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

        private struct PastedRecord
        {
            public Entity m_Prefab;
            public float3 m_Position;
            public bool m_HadTree;
            public Game.Objects.Tree m_Tree;
            public bool m_HasSeed;
            public ushort m_Seed;

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

            if (m_Mode == Mode.Paste || m_MoveDragging || m_MarqueeHeld)
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
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Temp>(),
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
            m_SelectSameAction = Mod.Settings.GetAction(CopasteSettings.kSelectSameAction);
            m_SnapGroundAction = Mod.Settings.GetAction(CopasteSettings.kSnapGroundAction);
            m_MatchHeightAction = Mod.Settings.GetAction(CopasteSettings.kMatchHeightAction);
            m_NudgeUpAction = Mod.Settings.GetAction(CopasteSettings.kNudgeUpAction);
            m_NudgeDownAction = Mod.Settings.GetAction(CopasteSettings.kNudgeDownAction);
            m_NudgeLeftAction = Mod.Settings.GetAction(CopasteSettings.kNudgeLeftAction);
            m_NudgeRightAction = Mod.Settings.GetAction(CopasteSettings.kNudgeRightAction);

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
            m_SelectSameAction.shouldBeEnabled = true;
            m_SnapGroundAction.shouldBeEnabled = true;
            m_MatchHeightAction.shouldBeEnabled = true;
            m_HeightPickArmed = false;
            m_NudgeUpAction.shouldBeEnabled = true;
            m_NudgeDownAction.shouldBeEnabled = true;
            m_NudgeLeftAction.shouldBeEnabled = true;
            m_NudgeRightAction.shouldBeEnabled = true;
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
            m_SelectSameAction.shouldBeEnabled = false;
            m_SnapGroundAction.shouldBeEnabled = false;
            m_MatchHeightAction.shouldBeEnabled = false;
            m_NudgeUpAction.shouldBeEnabled = false;
            m_NudgeDownAction.shouldBeEnabled = false;
            m_NudgeLeftAction.shouldBeEnabled = false;
            m_NudgeRightAction.shouldBeEnabled = false;

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
            m_Selected.Clear();
            m_HoverEntity = Entity.Null;
            if (m_Mode == Mode.Paste)
            {
                m_ToolSystem.ignoreErrors = m_PreviousIgnoreErrors;
            }

            m_Mode = Mode.Select;
            m_SameFilterPrefab = Entity.Null;
            SetSameFilterName();
            m_HeightPickArmed = false;
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

                if (m_UiTyping)
                {
                    return inputDeps;
                }

                if (m_Mode == Mode.Select)
                {
                    UpdateSelectMode();
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

        private void ResetToolState()
        {
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
            m_RightHeld = false;
            m_RightDragging = false;
            m_SameFilterPrefab = Entity.Null;
            SetSameFilterName();
            m_HeightPickArmed = false;
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
                    if (m_Mode == Mode.Select && m_Selected.Count > 0)
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

            NativeArray<Entity> entities = m_PropQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms = m_PropQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            NativeArray<PrefabRef> prefabRefs = m_PropQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                // Same filter: marquee hvata samo propove izabranog tipa.
                if (m_SameFilterPrefab != Entity.Null && prefabRefs[i].m_Prefab != m_SameFilterPrefab)
                {
                    continue;
                }

                float2 offset = transforms[i].m_Position.xz - m_MarqueeStart.xz;
                float pu = math.dot(offset, m_MarqueeRight);
                float pv = math.dot(offset, m_MarqueeForward);

                // Presek sa gabaritom propa, ne samo centrom — prop čiji je deo u okviru se selektuje.
                float half = GetPrefabHalfSize(prefabRefs[i].m_Prefab);
                if (pu >= uMin - half && pu <= uMax + half && pv >= vMin - half && pv <= vMax + half)
                {
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

            if (!additive)
            {
                ClearSelection();
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

                transform.m_Position.y += delta;
                EntityManager.SetComponentData(entity, transform);
                WriteElevation(entity, transform.m_Position.y - TerrainUtils.SampleHeight(ref heightData, transform.m_Position));
                EntityManager.AddComponent<Updated>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }
        }

        private void ApplyClickSelection(Entity entity, bool shiftHeld)
        {
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

            PushTransformUndo();

            // Ofseti se NE računaju odavde: sidro iz ovog frejma je pogodak na
            // površini propa, a od sledećeg frejma raycast gađa samo teren —
            // paralaksa između ta dva pogotka je pravila vidljivo cimanje na
            // startu prevlačenja. InitMoveOffsets čeka prvi terenski pogodak.
            m_MoveItems.Clear();
            m_MoveDragging = true;
            m_MoveOffsetsPending = true;
        }

        // Popuni ofsete selekcije prema prvom TERENSKOM sidru (isti raycast kao
        // kasniji MoveSelection pozivi — nema skoka).
        private void InitMoveOffsets(float3 anchor)
        {
            m_MoveItems.Clear();
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            foreach (Entity entity in m_Selected)
            {
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

            m_MoveOffsetsPending = false;
        }

        private void MoveSelection(float3 anchor)
        {
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();

            foreach (MoveItem item in m_MoveItems)
            {
                if (!EntityManager.Exists(item.m_Entity) ||
                    !EntityManager.TryGetComponent(item.m_Entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                float3 position = anchor + item.m_Offset;
                position.y = TerrainUtils.SampleHeight(ref heightData, position) + item.m_HeightOffset;

                transform.m_Position = position;
                EntityManager.SetComponentData(item.m_Entity, transform);
                WriteElevation(item.m_Entity, item.m_HeightOffset);
                EntityManager.AddComponent<Updated>(item.m_Entity);
                EntityManager.AddComponent<BatchesUpdated>(item.m_Entity);
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

            return count > 0 ? center / count : float3.zero;
        }

        private void RotateSelection(float angle)
        {
            quaternion rotation = quaternion.RotateY(angle);
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();

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

                transform.m_Position = position;
                transform.m_Rotation = math.normalize(math.mul(rotation, transform.m_Rotation));
                EntityManager.SetComponentData(entity, transform);
                EntityManager.AddComponent<Updated>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
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

            m_PasteDirty = true;
        }

        private void DeleteSelection()
        {
            List<TransformSnapshot> snapshots = SnapshotSelection();
            if (snapshots.Count > 0)
            {
                PushUndo(new UndoRecord { m_Kind = UndoKind.Delete, m_Snapshots = snapshots });
            }

            foreach (Entity entity in m_Selected)
            {
                if (EntityManager.Exists(entity))
                {
                    EntityManager.AddComponent<Deleted>(entity);
                }
            }

            m_Selected.Clear();

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
            if (rotationDelta != 0f && m_Selected.Count > 0)
            {
                RotateSelection(rotationDelta);
            }

            bool raycastValid = GetRaycastResult(out Entity raycastEntity, out RaycastHit hit);
            Entity hitEntity = raycastValid && IsCopyable(raycastEntity) ? raycastEntity : Entity.Null;

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
            }

            // Pritisak: na propu = potencijalni klik ili početak pomeranja; na praznom tlu = početak marquee-a.
            if (ClickedThisFrame())
            {
                if (m_HeightPickArmed && hitEntity != Entity.Null)
                {
                    // Klik bira uzor-prop: cela selekcija preuzima njegovu visinu iznad terena.
                    MatchSelectionHeight(hitEntity);
                    m_HeightPickArmed = false;
                }
                else if (hitEntity != Entity.Null)
                {
                    m_LeftHeldOnProp = true;
                    m_MoveDragging = false;
                    m_MoveOffsetsPending = false;
                    m_LeftPressEntity = hitEntity;
                    m_LeftPressShift = shiftHeld;
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

                m_LeftHeldOnProp = false;
                m_MoveDragging = false;
                m_MoveOffsetsPending = false;
                m_MoveItems.Clear();
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
                if (m_HeightPickArmed)
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

            // Delete: obriši selektovane propove.
            if (m_DeleteAction.WasPressedThisFrame() && m_Selected.Count > 0)
            {
                DeleteSelection();
                return;
            }

            // Undo (Ctrl+Z).
            if (m_UndoAction.WasPressedThisFrame())
            {
                Undo();
                return;
            }

            // Select same (T): uključuje/isključuje filter tipa — dok je aktivan, marquee hvata samo taj tip.
            if (m_SelectSameAction.WasPressedThisFrame())
            {
                ToggleSameFilter();
            }

            // Snap na teren (End).
            if (m_SnapGroundAction.WasPressedThisFrame() && m_Selected.Count > 0)
            {
                PushTransformUndo();
                SnapSelectionToGround();
            }

            // Nudge (Ctrl+strelice): fino pomeranje selekcije.
            float3 nudgeDelta = GetNudgeDelta();
            if (!nudgeDelta.Equals(float3.zero) && m_Selected.Count > 0)
            {
                if (AnyNudgePressedThisFrame())
                {
                    PushTransformUndo();
                }

                NudgeSelection(nudgeDelta);
            }

            // PageUp/PageDown: podizanje/spuštanje selekcije.
            float heightDelta = GetHeightInputDelta();
            if (heightDelta != 0f && m_Selected.Count > 0)
            {
                if (m_RaiseAction.WasPressedThisFrame() || m_LowerAction.WasPressedThisFrame())
                {
                    PushTransformUndo();
                }

                AdjustSelectionHeight(heightDelta);
            }

            // Copy: kopiraj selekciju u clipboard.
            if (m_CopyAction.WasPressedThisFrame() && m_Selected.Count > 0)
            {
                CopySelection();
            }

            // Paste: pređi u paste mod ako clipboard nije prazan.
            if (m_PasteAction.WasPressedThisFrame() && m_Clipboard.Count > 0)
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
            if (rotationDelta != 0f)
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
            }
            else
            {
                applyMode = ApplyMode.None;
            }
        }

        private void DrawSelectOverlays()
        {
            if (m_Selected.Count == 0 && m_HoverEntity == Entity.Null && !m_MarqueeActive)
            {
                return;
            }

            OverlayRenderSystem.Buffer overlayBuffer = m_OverlayRenderSystem.GetBuffer(out JobHandle _);

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
                    overlayBuffer.DrawCircle(kSelectedColor, default, 0.25f, 0, new float2(0f, 1f), transform.m_Position, GetDiameter(entity));
                    drawn++;
                }
            }

            if (m_HoverEntity != Entity.Null &&
                !m_Selected.Contains(m_HoverEntity) &&
                EntityManager.Exists(m_HoverEntity) &&
                EntityManager.TryGetComponent(m_HoverEntity, out Game.Objects.Transform hoverTransform))
            {
                overlayBuffer.DrawCircle(kHoverColor, default, 0.25f, 0, new float2(0f, 1f), hoverTransform.m_Position, GetDiameter(m_HoverEntity));
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

        private void CopySelection()
        {
            m_Clipboard.Clear();

            float3 centroid = float3.zero;
            int count = 0;
            foreach (Entity entity in m_Selected)
            {
                if (EntityManager.Exists(entity) && EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    centroid += transform.m_Position;
                    count++;
                }
            }

            if (count == 0)
            {
                return;
            }

            centroid /= count;

            TerrainHeightData copyHeightData = m_TerrainSystem.GetHeightData();

            foreach (Entity entity in m_Selected)
            {
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform) ||
                    !EntityManager.TryGetComponent(entity, out PrefabRef prefabRef))
                {
                    continue;
                }

                // Stvarna visina propa iznad terena — čuva se da nalepljeni bude na istoj visini kao original.
                float heightOffset = transform.m_Position.y - TerrainUtils.SampleHeight(ref copyHeightData, transform.m_Position);
                bool hadTree = EntityManager.TryGetComponent(entity, out Game.Objects.Tree tree);
                bool hasSeed = EntityManager.TryGetComponent(entity, out PseudoRandomSeed seed);

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
                });
            }

            Mod.Log.Info($"Copaste: copied {m_Clipboard.Count} props");
        }

        private void CreatePasteDefinitions(float3 anchor)
        {
            EntityCommandBuffer buffer = m_ToolOutputBarrier.CreateCommandBuffer();
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            Unity.Mathematics.Random random = RandomSeed.Next().GetRandom(0);
            float baseDelta = GetAnchorHeightDelta(anchor, ref heightData);
            m_LastPreview.Clear();

            // "Original" izgled: nalepljeni prop preuzima seed (boju/varijaciju) originala.
            // "Random varijacije": igra bira nasumično, kao do sada.
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
                });

                Entity definitionEntity = buffer.CreateEntity();

                CreationDefinition creation = default;
                creation.m_Prefab = item.m_Prefab;
                creation.m_RandomSeed = random.NextInt();

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
        }

        private bool IsCopyable(Entity entity)
        {
            return EntityManager.HasComponent<Game.Objects.Object>(entity) &&
                EntityManager.HasComponent<Game.Objects.Transform>(entity) &&
                EntityManager.HasComponent<PrefabRef>(entity) &&
                !EntityManager.HasComponent<Game.Buildings.Building>(entity) &&
                !EntityManager.HasComponent<Game.Buildings.Extension>(entity) &&
                !EntityManager.HasComponent<Game.Vehicles.Vehicle>(entity) &&
                !EntityManager.HasComponent<Game.Objects.Moving>(entity) &&
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
            if (ToolIsActive && m_Mode == Mode.Select && m_Selected.Count > 0)
            {
                CopySelection();
            }
        }

        public void TriggerPaste()
        {
            if (!ToolIsActive)
            {
                return;
            }

            if (m_Mode == Mode.Paste)
            {
                ExitPasteMode();
                return;
            }

            if (m_Clipboard.Count > 0)
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
            }
        }

        private bool m_PreviousIgnoreErrors;

        // Jedinstven ulazak u paste mod — čisti SVA stanja selekcionog moda.
        private void EnterPasteMode()
        {
            CancelMarquee();
            if (m_HoverEntity != Entity.Null && !m_Selected.Contains(m_HoverEntity))
            {
                Unhighlight(m_HoverEntity);
            }

            m_HoverEntity = Entity.Null;
            m_HeightPickArmed = false;
            m_LeftHeldOnProp = false;
            m_MoveDragging = false;
            m_MoveOffsetsPending = false;
            m_MoveItems.Clear();
            m_Mode = Mode.Paste;
            m_PasteDirty = true;
            m_PasteHeightBoost = 0f;
            m_LastPreview.Clear();
            m_PreviousIgnoreErrors = m_ToolSystem.ignoreErrors;
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
            if (ToolIsActive && m_Mode == Mode.Select && m_Selected.Count > 0)
            {
                DeleteSelection();
            }
        }

        public void TriggerUndo()
        {
            if (ToolIsActive)
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
            if (!ToolIsActive)
            {
                return;
            }

            if (m_Mode == Mode.Paste)
            {
                m_PasteHeightBoost = 0f;
                m_PasteDirty = true;
            }
            else if (m_Selected.Count > 0)
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

            float angle = math.radians(degrees);
            if (m_Mode == Mode.Paste)
            {
                RotateClipboard(angle);
            }
            else if (m_Selected.Count > 0)
            {
                m_RotationCenter = GetSelectionCenter();
                PushTransformUndo();
                RotateSelection(angle);
            }
        }

        public void TriggerHeight(int steps)
        {
            if (!ToolIsActive)
            {
                return;
            }

            float delta = steps * 0.5f;
            if (m_Mode == Mode.Paste)
            {
                m_PasteHeightBoost += delta;
                m_PasteDirty = true;
            }
            else if (m_Selected.Count > 0)
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

                NativeArray<Entity> entities = m_PropQuery.ToEntityArray(Allocator.Temp);
                NativeArray<Game.Objects.Transform> transforms = m_PropQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
                NativeArray<PrefabRef> prefabRefs = m_PropQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);

                for (int i = 0; i < entities.Length; i++)
                {
                    float3 entityPosition = transforms[i].m_Position;
                    if (math.any(entityPosition < boundsMin) || math.any(entityPosition > boundsMax) ||
                        claimed.Contains(entities[i]))
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
                        break;
                    }
                }

                entities.Dispose();
                transforms.Dispose();
                prefabRefs.Dispose();
            }

            if (m_PostPasteFixFrames == 0)
            {
                // Bez Clear — istu listu drži undo zapis (sa razrešenim entitetima).
                m_PostPasteFix = null;
            }
        }

        private void PushUndo(UndoRecord record)
        {
            m_UndoStack.Add(record);
            if (m_UndoStack.Count > kMaxUndo)
            {
                m_UndoStack.RemoveAt(0);
            }
        }

        private List<TransformSnapshot> SnapshotSelection()
        {
            List<TransformSnapshot> snapshots = new List<TransformSnapshot>(m_Selected.Count);
            foreach (Entity entity in m_Selected)
            {
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform) ||
                    !EntityManager.TryGetComponent(entity, out PrefabRef prefabRef))
                {
                    continue;
                }

                bool hadElevation = EntityManager.TryGetComponent(entity, out Game.Objects.Elevation elevation);
                bool hadTree = EntityManager.TryGetComponent(entity, out Game.Objects.Tree tree);
                bool hasSeed = EntityManager.TryGetComponent(entity, out PseudoRandomSeed seed);
                snapshots.Add(new TransformSnapshot
                {
                    m_Entity = entity,
                    m_Prefab = prefabRef.m_Prefab,
                    m_Transform = transform,
                    m_HadElevation = hadElevation,
                    m_Elevation = hadElevation ? elevation.m_Elevation : 0f,
                    m_HadTree = hadTree,
                    m_Tree = tree,
                    m_HasSeed = hasSeed,
                    m_Seed = hasSeed ? seed.m_Seed : (ushort)0,
                });
            }

            return snapshots;
        }

        private void PushTransformUndo()
        {
            List<TransformSnapshot> snapshots = SnapshotSelection();
            if (snapshots.Count > 0)
            {
                PushUndo(new UndoRecord { m_Kind = UndoKind.Transforms, m_Snapshots = snapshots });
            }
        }

        private void Undo()
        {
            if (m_UndoStack.Count == 0)
            {
                return;
            }

            UndoRecord record = m_UndoStack[m_UndoStack.Count - 1];
            m_UndoStack.RemoveAt(m_UndoStack.Count - 1);

            switch (record.m_Kind)
            {
                case UndoKind.Transforms:
                    foreach (TransformSnapshot snapshot in record.m_Snapshots)
                    {
                        if (!EntityManager.Exists(snapshot.m_Entity))
                        {
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

                    break;

                case UndoKind.Delete:
                    foreach (TransformSnapshot snapshot in record.m_Snapshots)
                    {
                        RecreateProp(snapshot);
                    }

                    break;

                case UndoKind.Paste:
                    DeletePastedEntities(record.m_Pasted);

                    // Ako se fixup za taj stamp još vrti, prekini ga — entiteti su upravo obrisani.
                    if (ReferenceEquals(record.m_Pasted, m_PostPasteFix))
                    {
                        m_PostPasteFix = null;
                        m_PostPasteFixFrames = 0;
                    }

                    break;
            }

            Mod.Log.Info($"Copaste: undo ({record.m_Kind})");
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

            // Bez ovoga vraćeni prop ume da ostane nevidljiv (batches) ili da ga igra
            // odmah sakrije kao Overridden ako se preklapa (zato PreventOverride).
            if (m_HasPreventOverride && !EntityManager.HasComponent(entity, m_PreventOverrideType))
            {
                EntityManager.AddComponent(entity, m_PreventOverrideType);
            }

            EntityManager.AddComponent<Updated>(entity);
            EntityManager.AddComponent<BatchesUpdated>(entity);
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

            NativeArray<Entity> entities = m_PropQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Game.Objects.Transform> transforms = m_PropQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
            NativeArray<PrefabRef> prefabRefs = m_PropQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            bool[] recordUsed = new bool[records.Count];
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
                    if (recordUsed[j] || record.m_Resolved != Entity.Null ||
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
                if (m_Selected.Count != 1 || m_SelectionFromMarquee)
                {
                    return string.Empty;
                }

                Entity entity = m_Selected[0];
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

                transform.m_Position = position;
                EntityManager.SetComponentData(entity, transform);
                WriteElevation(entity, heightOffset);
                EntityManager.AddComponent<Updated>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }
        }

        // Align center: poravnaj sve selektovane propove na liniju kroz centar
        // selekcije. Osa je relativna kameri, kao nudge i marquee:
        // horizontal=true → horizontalna linija na ekranu (poravnanje po dubini),
        // horizontal=false → vertikalna linija (poravnanje po širini).
        public void TriggerAlignCenter(bool horizontal)
        {
            if (!ToolIsActive || m_Mode != Mode.Select || m_Selected.Count < 2)
            {
                return;
            }

            UnityEngine.Camera camera = UnityEngine.Camera.main;
            float3 cameraForward = camera != null ? (float3)camera.transform.forward : new float3(0f, 0f, 1f);
            float2 forward = math.normalizesafe(cameraForward.xz, new float2(0f, 1f));
            float2 right = new float2(forward.y, -forward.x);
            float2 axis = horizontal ? forward : right;

            // Centar selekcije po izabranoj osi.
            float2 centroid = float2.zero;
            int count = 0;
            foreach (Entity entity in m_Selected)
            {
                if (EntityManager.Exists(entity) && EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    centroid += transform.m_Position.xz;
                    count++;
                }
            }

            if (count < 2)
            {
                return;
            }

            centroid /= count;
            float centroidAlongAxis = math.dot(centroid, axis);

            PushTransformUndo();

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            foreach (Entity entity in m_Selected)
            {
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.TryGetComponent(entity, out Game.Objects.Transform transform))
                {
                    continue;
                }

                float2 xz = transform.m_Position.xz;
                float delta = centroidAlongAxis - math.dot(xz, axis);
                if (delta == 0f)
                {
                    continue;
                }

                float heightOffset = transform.m_Position.y - TerrainUtils.SampleHeight(ref heightData, transform.m_Position);
                float3 position = transform.m_Position;
                position.xz = xz + (axis * delta);
                position.y = TerrainUtils.SampleHeight(ref heightData, position) + heightOffset;

                transform.m_Position = position;
                EntityManager.SetComponentData(entity, transform);
                WriteElevation(entity, heightOffset);
                EntityManager.AddComponent<Updated>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
            }

            Mod.Log.Info($"Copaste: align center ({(horizontal ? "H" : "V")}) on {count} props");
        }

        public bool HeightPickArmed => m_HeightPickArmed;

        public void TriggerMatchHeight()
        {
            if (ToolIsActive && m_Mode == Mode.Select)
            {
                m_HeightPickArmed = !m_HeightPickArmed && m_Selected.Count > 0;
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
            if (m_Selected.Count > 0)
            {
                CopySelection();
            }

            if (m_Clipboard.Count == 0)
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
                int missing = 0;

                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('|');

                    // 11 polja = najstariji format, 14 = sa drvećem (v1.0.4), 15 = sa seed-om (v1.0.6).
                    if (parts.Length != 11 && parts.Length != 14 && parts.Length != 15)
                    {
                        continue;
                    }

                    if (!m_PrefabSystem.TryGetPrefab(new PrefabID(parts[0], parts[1]), out PrefabBase prefabBase) || prefabBase == null)
                    {
                        missing++;
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

                    items.Add(item);
                }

                if (items.Count == 0)
                {
                    Mod.Log.Warn($"Blueprint '{name}': no props could be resolved ({missing} missing)");
                    return false;
                }

                m_Clipboard.Clear();
                m_Clipboard.AddRange(items);
                Mod.Log.Info($"Copaste: blueprint '{name}' loaded ({items.Count} props, {missing} missing)");
                return true;
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Blueprint load failed: {e.Message}");
                return false;
            }
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
