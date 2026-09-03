// Copaste — poravnanje traka na spoju deonica ("lane snap").
//
// Spoj dva puta igra podrazumevano centrira. Graditelji prave "exit" izgled
// tako što uža osa prilazi čvoru bočno pomerena, pa leva (ili desna) linija
// trake teče kontinualno preko spoja. Taj pomak živi u BOČNOM OFSETU KRAJA
// KRIVE od čvora — kraj legitimno ne mora da leži na čvoru — i pomeranje
// čvora ga čuva.
//
// UI: kad je selektovan TAČNO JEDAN segment, na svakom kvalifikovanom kraju
// (čvor sa tačno dva kraka) stoji trouglić; klik CIKLUSIRA poravnanje:
// centar → levo → desno → centar. Boja i vrh trougla pokazuju stranu: belo =
// centar, ljubičasto = levo, žuto = desno. Pomera se UŽA od dve deonice
// (poravnanje je svojstvo spoja, ne selekcije).
//
// Ciljevi se računaju iz rasporeda traka u KOMPOZICIJI deonice, a na
// putevima bez trotoara iz razlike ukupnih širina; oba slučaja i izbor ose
// su objašnjeni uz sam račun niže.

namespace Copaste
{
    using Colossal.Entities;
    using Colossal.Mathematics;
    using Game.Common;
    using Game.Prefabs;
    using Game.Rendering;
    using Unity.Entities;
    using Unity.Mathematics;

    public partial class CopasteToolSystem
    {
        private const float kAlignHandleRadius = 2.0f;
        private const float kAlignHandlePickRadius = 2.6f;

        // Prag: ispod pola metra razlike poravnanje nema šta da radi, pa
        // L/D prelaze u preset bočni skok.
        private const float kAlignMinWidthDelta = 0.5f;

        private enum LaneAlignState
        {
            Center = 0,
            Left = 1,
            Right = 2,
        }

        // Sve o jednom kvalifikovanom kraju spoja — puni se iz geometrije,
        // ništa se ne pamti između frejmova.
        private struct LaneAlignSpot
        {
            public Entity m_Node;
            public Entity m_WideEdge;
            public Entity m_NarrowEdge;
            public bool m_NarrowAtStart;   // uža deonica počinje (a) na ovom čvoru?
            public float3 m_HandlePosition;
            public float2 m_WideLeft;      // leva normala šire ose (xz)
            public float m_OffsetLeft;     // ciljni bočni ofset za levo poravnanje
            public float m_OffsetRight;    // isto za desno (po pravilu negativan)
            public float m_CurrentSide;    // trenutni bočni ofset kraja uže
            public LaneAlignState m_State;
        }

        private float GetNetPrefabWidth(Entity edge)
        {
            if (EntityManager.TryGetComponent(edge, out PrefabRef prefabRef) &&
                EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetGeometryData geometry))
            {
                return geometry.m_DefaultWidth;
            }

            return 0f;
        }

        // Bočni opseg VOZNIH traka deonice (kola/šine; parking i pešačke se
        // preskaču): x = leva granica, y = desna granica (levo je pozitivno).
        // Izvor je KOMPOZICIJA deonice — statičan raspored traka iz podataka
        // igre, imun na regeneraciju: bafer živih traka posle svakog pomaka
        // ume da nosi dva kompleta sa ~7 cm razlike, pa je cilj "disao".
        // Živo merenje služi samo da kalibriše znak kompozicijske ose i
        // srednji ofset (i kao rezerva kad kompozicije nema).
        private bool TryGetLaneExtents(Entity edge, out float2 extents)
        {
            extents = default;
            bool haveLive = TryGetLiveLaneExtents(edge, out float2 live);

            if (EntityManager.TryGetComponent(edge, out Game.Net.Composition composition) &&
                EntityManager.TryGetBuffer(composition.m_Edge, true, out DynamicBuffer<NetCompositionLane> compLanes))
            {
                bool any = false;
                float lo = float.MaxValue;
                float hi = float.MinValue;
                for (int i = 0; i < compLanes.Length; i++)
                {
                    NetCompositionLane lane = compLanes[i];
                    if ((!EntityManager.HasComponent<CarLaneData>(lane.m_Lane) &&
                         !EntityManager.HasComponent<TrackLaneData>(lane.m_Lane)) ||
                        EntityManager.HasComponent<ParkingLaneData>(lane.m_Lane) ||
                        !EntityManager.TryGetComponent(lane.m_Lane, out NetLaneData laneData))
                    {
                        continue;
                    }

                    float half = math.max(laneData.m_Width, 1f) * 0.5f;
                    lo = math.min(lo, lane.m_Position.x - half);
                    hi = math.max(hi, lane.m_Position.x + half);
                    any = true;
                }

                if (any)
                {
                    float middle = EntityManager.TryGetComponent(composition.m_Edge, out NetCompositionData compositionData)
                        ? compositionData.m_MiddleOffset
                        : 0f;

                    // Znak ose i strana srednjeg ofseta se ne pretpostavljaju:
                    // od četiri kandidata pobeđuje onaj najbliži živom merenju
                    // (simetrična kompozicija: svi jednaki, svejedno je).
                    float2 best = new float2(hi - middle, lo - middle);
                    if (haveLive)
                    {
                        float bestScore = float.MaxValue;
                        float2[] variants =
                        {
                            new float2(hi - middle, lo - middle),
                            new float2(-(lo - middle), -(hi - middle)),
                            new float2(hi + middle, lo + middle),
                            new float2(-(lo + middle), -(hi + middle)),
                        };
                        foreach (float2 variant in variants)
                        {
                            float score = math.abs(variant.x - live.x) + math.abs(variant.y - live.y);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = variant;
                            }
                        }
                    }

                    extents = best;
                    return true;
                }
            }

            extents = live;
            return haveLive;
        }

        // Živo merenje traka na sredini deonice u njenom a→d frejmu — samo za
        // kalibraciju kompozicije i kao rezerva.
        private bool TryGetLiveLaneExtents(Entity edge, out float2 extents)
        {
            extents = default;
            if (!EntityManager.TryGetComponent(edge, out Game.Net.Curve curve) ||
                !EntityManager.TryGetBuffer(edge, true, out DynamicBuffer<Game.Net.SubLane> subLanes))
            {
                return false;
            }

            float3 center = MathUtils.Position(curve.m_Bezier, 0.5f);
            float2 tangent = math.normalizesafe(
                (MathUtils.Position(curve.m_Bezier, 0.55f) - MathUtils.Position(curve.m_Bezier, 0.45f)).xz,
                new float2(0f, 1f));
            float2 left = new float2(-tangent.y, tangent.x);

            bool any = false;
            float lo = float.MaxValue;
            float hi = float.MinValue;
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity lane = subLanes[i].m_SubLane;
                if ((!EntityManager.HasComponent<Game.Net.CarLane>(lane) &&
                     !EntityManager.HasComponent<Game.Net.TrackLane>(lane)) ||
                    EntityManager.HasComponent<Game.Net.ParkingLane>(lane) ||
                    // Bajate/duplirane trake: posle svakog pomaka u baferu
                    // nakratko žive i stare (Deleted) i privremene (Temp) i
                    // sekundarne kopije — merenje po njima je klizilo cilj.
                    EntityManager.HasComponent<Deleted>(lane) ||
                    EntityManager.HasComponent<Game.Tools.Temp>(lane) ||
                    EntityManager.HasComponent<Game.Net.SecondaryLane>(lane) ||
                    !EntityManager.TryGetComponent(lane, out Game.Net.Curve laneCurve) ||
                    !EntityManager.TryGetComponent(lane, out PrefabRef lanePrefab) ||
                    !EntityManager.TryGetComponent(lanePrefab.m_Prefab, out NetLaneData laneData))
                {
                    continue;
                }

                float offset = math.dot(MathUtils.Position(laneCurve.m_Bezier, 0.5f).xz - center.xz, left);
                float half = math.max(laneData.m_Width, 1f) * 0.5f;
                lo = math.min(lo, offset - half);
                hi = math.max(hi, offset + half);
                any = true;
            }

            if (!any)
            {
                return false;
            }

            extents = new float2(hi, lo);
            return true;
        }

        // Ima li deonica pešačke trake (trotoare)? Bez njih je ukupna širina
        // prefaba čist kolovoz, pa poravnanje ide preko širina.
        private bool HasPedestrianLanes(Entity edge)
        {
            if (!EntityManager.TryGetBuffer(edge, true, out DynamicBuffer<Game.Net.SubLane> subLanes))
            {
                return false;
            }

            for (int i = 0; i < subLanes.Length; i++)
            {
                if (EntityManager.HasComponent<Game.Net.PedestrianLane>(subLanes[i].m_SubLane))
                {
                    return true;
                }
            }

            return false;
        }

        // Kvalifikovan kraj selektovanog segmenta: čvor sa TAČNO dva prava
        // kraka čije se širine razlikuju. selectedEnd: 0 = start (a), 1 = end (d).
        private bool TryGetLaneAlignSpot(Entity selectedEdge, int selectedEnd, out LaneAlignSpot spot)
        {
            spot = default;
            if (!EntityManager.TryGetComponent(selectedEdge, out Game.Net.Edge edgeData) ||
                !EntityManager.TryGetComponent(selectedEdge, out Game.Net.Curve curve))
            {
                return false;
            }

            Entity node = selectedEnd == 0 ? edgeData.m_Start : edgeData.m_End;
            if (GetNodeRealEdges(node, out Entity armA, out Entity armB) != 2)
            {
                return false;
            }

            Entity other = armA == selectedEdge ? armB : armA;
            if (other == Entity.Null || other == selectedEdge ||
                EntityManager.HasComponent<Owner>(other) ||
                !EntityManager.TryGetComponent(other, out Game.Net.Curve otherCurve) ||
                !EntityManager.TryGetComponent(other, out Game.Net.Edge otherData))
            {
                return false;
            }

            float widthSelected = GetNetPrefabWidth(selectedEdge);
            float widthOther = GetNetPrefabWidth(other);
            if (widthSelected <= 0f || widthOther <= 0f)
            {
                return false;
            }

            bool selectedIsWide = widthSelected > widthOther;
            spot.m_Node = node;
            spot.m_WideEdge = selectedIsWide ? selectedEdge : other;
            spot.m_NarrowEdge = selectedIsWide ? other : selectedEdge;

            // Kraj šire i kraj uže krive NA OVOM čvoru + tangenta šire ka čvoru.
            Game.Net.Curve wideCurve = selectedIsWide ? curve : otherCurve;
            Game.Net.Edge wideData = selectedIsWide ? edgeData : otherData;
            Game.Net.Curve narrowCurve = selectedIsWide ? otherCurve : curve;
            Game.Net.Edge narrowData = selectedIsWide ? otherData : edgeData;

            bool wideAtStart = wideData.m_Start == node;
            float3 wideEnd = wideAtStart ? wideCurve.m_Bezier.a : wideCurve.m_Bezier.d;

            spot.m_NarrowAtStart = narrowData.m_Start == node;
            float3 narrowEnd = spot.m_NarrowAtStart ? narrowCurve.m_Bezier.a : narrowCurve.m_Bezier.d;

            // SMER "levo/desno" diktira smer VOŽNJE uže deonice (jednosmerni
            // putevi: ista fizička strana na oba kraja). Ali OSA mora biti po
            // ŠIROJ deonici — nju poravnanje nikad ne pomera, pa je stabilna
            // iz klika u klik. Osa po užoj se rotira sa svakim pomakom njenog
            // kraja: merenja klize (2.37→2.44...), kraj šeta i uzduž puta,
            // i deonica se talasa.
            float3 narrowControl = spot.m_NarrowAtStart ? narrowCurve.m_Bezier.b : narrowCurve.m_Bezier.c;
            float2 travelTangent = math.normalizesafe(
                spot.m_NarrowAtStart ? (narrowControl - narrowEnd).xz : (narrowEnd - narrowControl).xz,
                new float2(0f, 1f));

            float3 wideControl = wideAtStart ? wideCurve.m_Bezier.b : wideCurve.m_Bezier.c;
            float2 wideAxis = math.normalizesafe(
                (wideAtStart ? (wideControl - wideEnd) : (wideEnd - wideControl)).xz,
                travelTangent);
            bool wideReversed = math.dot(wideAxis, travelTangent) < 0f;
            if (wideReversed)
            {
                wideAxis = -wideAxis;
            }

            spot.m_WideLeft = new float2(-wideAxis.y, wideAxis.x);

            // RAZDVOJENO po tipu puta: bez pešačkih traka (autoput, seoski
            // put) ukupna širina prefaba JESTE kolovoz, pa pola razlike širina
            // daje savršeno ivično poravnanje — staro ponašanje, ostaje. Sa
            // trotoarima ukupna širina laže, pa se cilj meri iz voznih traka.
            bool widthBased = !HasPedestrianLanes(spot.m_WideEdge) && !HasPedestrianLanes(spot.m_NarrowEdge);
            float2 wideExtents = default;
            float2 narrowExtents = default;
            bool haveLanes = !widthBased &&
                TryGetLaneExtents(spot.m_WideEdge, out wideExtents) &&
                TryGetLaneExtents(spot.m_NarrowEdge, out narrowExtents);
            if (haveLanes)
            {
                // Šira crtana u suprotnom smeru od uže: njeno levo/desno su u
                // zajedničkom frejmu fizički zamenjeni.
                if (wideReversed)
                {
                    wideExtents = new float2(-wideExtents.y, -wideExtents.x);
                }

                spot.m_OffsetLeft = wideExtents.x - narrowExtents.x;
                spot.m_OffsetRight = wideExtents.y - narrowExtents.y;

                // Isti raspored traka sa obe strane (isti put): L/D su preset
                // bočni skok od četvrtine kolovoza (brzi "chicane").
                if (math.max(math.abs(spot.m_OffsetLeft), math.abs(spot.m_OffsetRight)) < kAlignMinWidthDelta)
                {
                    float band = math.max(narrowExtents.x - narrowExtents.y, 2f);
                    spot.m_OffsetLeft = band * 0.25f;
                    spot.m_OffsetRight = -band * 0.25f;
                }
            }
            else
            {
                // Autoput i sve bez trotoara (i retki slučaj bez voznih
                // traka): pola razlike ukupnih širina — ivica uz ivicu.
                float widthDelta = math.abs(widthSelected - widthOther);
                float fallback = widthDelta >= kAlignMinWidthDelta
                    ? widthDelta * 0.5f
                    : math.min(widthSelected, widthOther) * 0.25f;
                spot.m_OffsetLeft = fallback;
                spot.m_OffsetRight = -fallback;
            }

            spot.m_CurrentSide = math.dot(narrowEnd.xz - wideEnd.xz, spot.m_WideLeft);

            // Trenutno stanje = najbliži od tri cilja (centar / leva / desna).
            float distCenter = math.abs(spot.m_CurrentSide);
            float distLeft = math.abs(spot.m_CurrentSide - spot.m_OffsetLeft);
            float distRight = math.abs(spot.m_CurrentSide - spot.m_OffsetRight);
            spot.m_State = distLeft < distCenter && distLeft <= distRight ? LaneAlignState.Left
                : distRight < distCenter ? LaneAlignState.Right
                : LaneAlignState.Center;

            // Trouglić stoji BOČNO od kraja (2,2 m po levoj normali šire ose):
            // na samom kraju sada sedi krajnja ručka za vuču, pa se ne sudaraju.
            float3 selectedEndPoint = selectedEnd == 0 ? curve.m_Bezier.a : curve.m_Bezier.d;
            spot.m_HandlePosition = selectedEndPoint +
                new float3(spot.m_WideLeft.x, 0f, spot.m_WideLeft.y) * 5f +
                new float3(0f, 0.5f, 0f);
            return true;
        }

        private bool LaneAlignAvailable => m_SelectedNetEdges.Count == 1 && m_SelectedNodes.Count == 0 &&
            m_SelectedLanes.Count == 0 && m_Selected.Count == 0 && m_SelectedSurfaces.Count == 0 &&
            m_Mode == Mode.Select;

        // Klik na kružić: ciklusiraj poravnanje. Vraća true kad je klik pojeden.
        private bool TryClickLaneAlignHandle(float3 position)
        {
            if (!LaneAlignAvailable)
            {
                return false;
            }

            // Shift/Ctrl/Alt klik su selekcioni gestovi — propadaju dalje.
            if (UnityEngine.InputSystem.Keyboard.current != null &&
                (UnityEngine.InputSystem.Keyboard.current.shiftKey.isPressed ||
                 UnityEngine.InputSystem.Keyboard.current.ctrlKey.isPressed ||
                 UnityEngine.InputSystem.Keyboard.current.altKey.isPressed))
            {
                return false;
            }

            Entity selected = m_SelectedNetEdges[0];
            for (int end = 0; end < 2; end++)
            {
                if (!TryGetLaneAlignSpot(selected, end, out LaneAlignSpot spot) ||
                    !TryCursorAtHeight(spot.m_HandlePosition.y, out float2 cursorAtSpot) ||
                    math.distance(cursorAtSpot, spot.m_HandlePosition.xz) > kAlignHandlePickRadius)
                {
                    continue;
                }

                LaneAlignState next = spot.m_State == LaneAlignState.Center ? LaneAlignState.Left
                    : spot.m_State == LaneAlignState.Left ? LaneAlignState.Right
                    : LaneAlignState.Center;

                PushTransformUndo();
                ApplyLaneAlign(spot, next);
                return true;
            }

            return false;
        }

        // Postavi kraj UŽE krive na ciljni bočni ofset od šire ose. Kraj se
        // pomera kao tačka (MoveCurveEndpoint čuva oblik ostatka), čvor i
        // šira deonica se NE diraju — a pomeranje čvora ovo od sada čuva.
        private void ApplyLaneAlign(LaneAlignSpot spot, LaneAlignState target)
        {
            if (!EntityManager.TryGetComponent(spot.m_NarrowEdge, out Game.Net.Curve narrowCurve))
            {
                return;
            }

            float targetSide = target == LaneAlignState.Left ? spot.m_OffsetLeft
                : target == LaneAlignState.Right ? spot.m_OffsetRight
                : 0f;
            float2 shift = spot.m_WideLeft * (targetSide - spot.m_CurrentSide);

            // Pomeraju se i KRAJ i njegova kontrolna tačka za isti vektor:
            // pravac na spoju ostaje paralelan sa širom deonicom (linija trake
            // nastavlja ravno), a S-prelaz se dešava dalje u segmentu. Samo
            // kraj (MoveCurveEndpoint) je rotirao tangentu na spoju — linija
            // je dolazila pod uglom i lomila se.
            float3 shift3 = new float3(shift.x, 0f, shift.y);
            if (spot.m_NarrowAtStart)
            {
                narrowCurve.m_Bezier.a += shift3;
                narrowCurve.m_Bezier.b += shift3;
            }
            else
            {
                narrowCurve.m_Bezier.d += shift3;
                narrowCurve.m_Bezier.c += shift3;
            }

            narrowCurve.m_Length = MathUtils.Length(narrowCurve.m_Bezier);
            EntityManager.SetComponentData(spot.m_NarrowEdge, narrowCurve);

            EntityManager.AddComponent<Updated>(spot.m_NarrowEdge);
            EntityManager.AddComponent<BatchesUpdated>(spot.m_NarrowEdge);
            if (EntityManager.Exists(spot.m_Node))
            {
                EntityManager.AddComponent<Updated>(spot.m_Node);
                m_DelayedNetSettle[spot.m_Node] = 4;
            }

            if (EntityManager.Exists(spot.m_WideEdge))
            {
                EntityManager.AddComponent<Updated>(spot.m_WideEdge);
                EntityManager.AddComponent<BatchesUpdated>(spot.m_WideEdge);
            }
        }

        // Kružići na kvalifikovanim krajevima selektovanog segmenta.
        private void DrawLaneAlignHandles(OverlayRenderSystem.Buffer overlayBuffer)
        {
            if (!LaneAlignAvailable)
            {
                return;
            }

            Entity selected = m_SelectedNetEdges[0];
            for (int end = 0; end < 2; end++)
            {
                if (!TryGetLaneAlignSpot(selected, end, out LaneAlignSpot spot))
                {
                    continue;
                }

                UnityEngine.Color color = spot.m_State == LaneAlignState.Left ? kHandleCenterColor
                    : spot.m_State == LaneAlignState.Right ? kHandleMidColor
                    : kHandleColor;

                // Trouglić POKAZUJE stranu: vrh ka levoj ivici (ljubičasto),
                // ka desnoj (žuto), ili simetrično duž ose (belo = centar).
                float2 left = spot.m_WideLeft;
                float2 forward = new float2(left.y, -left.x);
                float2 tip = spot.m_State == LaneAlignState.Left ? left
                    : spot.m_State == LaneAlignState.Right ? -left
                    : forward;
                DrawAlignTriangle(overlayBuffer, color, spot.m_HandlePosition, tip, kAlignHandleRadius);
            }
        }

        // Jednakokraki trouglić u xz ravni: vrh u smeru tipDir, baza pozadi.
        private static void DrawAlignTriangle(OverlayRenderSystem.Buffer overlayBuffer, UnityEngine.Color color, float3 center, float2 tipDir, float size)
        {
            float2 side = new float2(tipDir.y, -tipDir.x);
            float3 tip = center + new float3(tipDir.x, 0f, tipDir.y) * size;
            float3 baseA = center - new float3(tipDir.x, 0f, tipDir.y) * (size * 0.6f) + new float3(side.x, 0f, side.y) * (size * 0.8f);
            float3 baseB = center - new float3(tipDir.x, 0f, tipDir.y) * (size * 0.6f) - new float3(side.x, 0f, side.y) * (size * 0.8f);

            overlayBuffer.DrawLine(color, new Line3.Segment(tip, baseA), 0.45f);
            overlayBuffer.DrawLine(color, new Line3.Segment(baseA, baseB), 0.45f);
            overlayBuffer.DrawLine(color, new Line3.Segment(baseB, tip), 0.45f);
        }
    }
}
