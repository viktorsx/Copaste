// Copaste — SAVIJANJE krivih (faza C): ručke na kontrolnim tačkama.
//
// Kad je selektovana TAČNO jedna ograda ili TAČNO jedan segment mreže,
// na krivoj se pojave ručke za direktno oblikovanje:
//  - ograda i segment mreže: 4 ručke (a, b, c, d) — krajevi pomeraju
//    kraj krive (kod ograde i container čvor uz lančane susede, kod mreže
//    uz update spoja), b/c su KONTROLNE TAČKE i prate miš jedan na jedan;
//  - peta "ručka" je samo telo krive: hvata se bilo gde po dužini.
// Povlačenje ručke prati teren uz očuvan visinski ofset tačke; undo hvata
// celu krivu kroz postojeće lane/net snimke.

namespace Copaste
{
    using Colossal.Entities;
    using Colossal.Mathematics;
    using Game.Common;
    using Game.Rendering;
    using Game.Simulation;
    using Unity.Entities;
    using Unity.Mathematics;

    public partial class CopasteToolSystem
    {
        // Radijus hvatanja/crtanja ručke (xz); srednje su manje od krajnjih.
        private const float kHandlePickRadius = 1.4f;
        private const float kHandleMidRadius = 1.05f;

        // Srednje ručke STOJE na kontrolnim tačkama b i c (van krive, vezane
        // tankom linijom za svoj kraj) i prate miš jedan na jedan — raniji
        // model ih je držao na samoj krivoj i rešavao kontrolnu tačku, što je
        // pomeralo dvostruko više od kursora.
        // Peta "ručka" nije tačka nego SAMA KRIVA: uhvatiš je bilo gde po
        // dužini i savijaš tu gde držiš. Kontrolne tačke se pomeraju obe,
        // srazmerno svom uticaju na toj tački — zato je potez gladak i
        // simetričan, za razliku od rešavanja preko jedne tačke.
        private const int kCurveHandleIndex = 4;

        // Koliko sme da se promaši kriva da bi se ipak uhvatila (metara).
        private const float kCurveGrabRadius = 6f;

        // Parametar duž krive na kom je kriva uhvaćena, i razmak od kursora
        // u tom trenutku (da hvatanje ne teleportuje krivu).
        private float m_CurveGrabT;
        private float2 m_CurveGrabOffset;

        // Tačka na kojoj je telo krive OBELEŽENO prvim klikom (dvokorak).
        private float m_StickyCurveGrabT;

        private const float kHandleT1 = 1f / 3f;
        private const float kHandleT2 = 2f / 3f;

        // "Vodič za ravno": dok se vuče srednja ručka, na tetivi (pravoj
        // između krajeva) stoji pomoćni kružić — ručka u njegovoj zoni
        // ispravlja CEO segment (obe kontrolne tačke na pravac, i po visini).
        // Hvatanje vodiča se meri u PIKSELIMA NA EKRANU, ne u metrima.
        // Fiksnih 1,5 m je pri zumiranju pokrivalo pola ekrana, pa je ručka
        // bežala na pravac baš kad se radi fino. Ovako je jednako "jako" na
        // svakom zumu: izdaleka pomaže, izbliza ne smeta.
        private const float kStraightSnapPixels = 14f;
        private const float kStraightSnapMinWorld = 0.15f;
        private const float kStraightSnapMaxWorld = 2.5f;

        // Prečnik hvatanja u metrima na datoj tački, iz vidnog ugla kamere i
        // visine prozora.
        private float StraightSnapRadius(float3 at)
        {
            UnityEngine.Camera camera = UnityEngine.Camera.main;
            if (camera == null)
            {
                return 1.5f;
            }

            float tangent = math.max(math.tan(math.radians(camera.fieldOfView * 0.5f)), 1e-4f);
            float pixelsPerMetre = UnityEngine.Screen.height / (2f * tangent);
            UnityEngine.Vector3 cameraPosition = camera.transform.position;
            float distance = math.max(math.distance(at, new float3(cameraPosition.x, cameraPosition.y, cameraPosition.z)), 1f);
            return math.clamp(kStraightSnapPixels * distance / pixelsPerMetre, kStraightSnapMinWorld, kStraightSnapMaxWorld);
        }

        // Krajnji (glavni) čvorovi = beli; MANIPULATIVNE srednje ručke =
        // ćilibar (boja koju ništa drugo u alatu ne koristi — plavo je
        // selekcija, zeleno aktivno/fokus); aktivna ručka = zeleno.
        private static readonly UnityEngine.Color kHandleColor = new UnityEngine.Color(1f, 1f, 1f, 0.9f);
        private static readonly UnityEngine.Color kHandleMidColor = new UnityEngine.Color(1f, 0.72f, 0.2f, 0.95f);
        private static readonly UnityEngine.Color kHandleGuideColor = new UnityEngine.Color(1f, 0.72f, 0.2f, 0.45f);
        private static readonly UnityEngine.Color kHandleActiveColor = new UnityEngine.Color(0.44f, 0.93f, 0.63f, 1f);

        // Treći kružić na SREDINI pravca (ljubičast): bilo koja srednja ručka
        // uvučena u njega ispravlja CEO segment odjednom.
        private static readonly UnityEngine.Color kHandleCenterColor = new UnityEngine.Color(0.72f, 0.5f, 1f, 0.6f);

        private bool m_HandleDragging;
        private bool m_HandleIsLane;
        private int m_HandleIndex = -1;
        private Entity m_HandleEntity = Entity.Null;
        private float m_HandleHeightOffset;
        private bool m_HandleSnappedStraight;
        private bool m_HandleSnappedCenter;

        // Oblik pre ulaska u središnji krug — izlazak bez puštanja ga vraća.
        private float3 m_HandleBackupB;
        private float3 m_HandleBackupC;

        // KLIKOM izabrana ručka (ostaje obeležena posle puštanja): PgUp/PgDn
        // dižu/spuštaju baš tu tačku — bez držanja miša. Entitet se pamti da
        // sticky ne preskoči na drugu krivu posle zamene selekcije.
        private int m_StickyHandleIndex = -1;
        private Entity m_StickyHandleEntity = Entity.Null;

        // Ručke postoje samo za selekciju od TAČNO jedne krive.
        private bool TryGetHandleTarget(out Entity entity, out bool isLane)
        {
            entity = Entity.Null;
            isLane = false;
            if (m_Mode != Mode.Select || m_Selected.Count > 0 || m_SelectedSurfaces.Count > 0)
            {
                return false;
            }

            if (m_SelectedLanes.Count == 1 && m_SelectedNodes.Count == 0 && m_SelectedNetEdges.Count == 0)
            {
                entity = m_SelectedLanes[0];
                isLane = true;
            }
            else if (m_SelectedNetEdges.Count == 1 && m_SelectedNodes.Count == 0 && m_SelectedLanes.Count == 0)
            {
                entity = m_SelectedNetEdges[0];
            }
            else
            {
                return false;
            }

            return EntityManager.Exists(entity) && EntityManager.HasComponent<Game.Net.Curve>(entity);
        }

        // Ograda nudi sve 4 ručke; segment mreže samo srednje (krajevi = čvorovi).
        // I net segment sad ima SVE ČETIRI ručke: krajnje pomeraju KRAJ KRIVE
        // (ne čvor!) — kraj legitimno ne leži na čvoru, njegov bočni ofset i
        // ugao SU doterivanje spoja. Čvor se ne dira, a
        // pomeranje čvora od sada čuva ovako naméštene krajeve.
        private static int FirstHandleIndex(bool isLane) => 0;

        private static int LastHandleIndex(bool isLane) => 3;

        // Pozicija ručke u svetu: krajevi na a/d, srednje na kontrolnim tačkama b/c.
        private float3 GetHandlePosition(Bezier4x3 bezier, int index)
        {
            if (index == kCurveHandleIndex)
            {
                return MathUtils.Position(bezier, m_CurveGrabT);
            }

            switch (index)
            {
                case 0: return bezier.a;

                // Srednje ručke stoje na SAMIM KONTROLNIM TAČKAMA, van krive,
                // povezane tankom linijom sa svojim krajem — klasične
                // "kukice". Ranije su stajale NA krivoj, pa se pri vuči
                // rešavalo da kriva prođe kroz kursor; u toj jednačini stoji
                // činilac 27/12, dakle kontrolna tačka je letela 2,25 puta
                // brže od miša, i to samo jedna od dve. Otud je bilo teško
                // dobiti gladak oblik.
                case 1: return bezier.b;
                case 2: return bezier.c;
                case 3: return bezier.d;
                default: return bezier.a;
            }
        }

        // Pritisak blizu ručke pokreće povlačenje. Ide RANO u klik-lancu:
        // ručka je mala, konkretna meta i sme da "pobedi" prop ispod sebe.
        // Klik po TERENU ispod ručke promašuje ručku na uzdignutoj deonici
        // (raycast pogodi tlo daleko u stranu pod uglom kamere). Zato se
        // kursor projektuje na horizontalnu ravan VISINE ručke — tačka ispod
        // kursora na toj visini; teren-pogodak je rezerva.
        // Vraća FALSE kad projekcija nije moguća (nema kamere, zrak
        // paralelan ravni, ili tačka iza kamere — kursor iznad horizonta).
        // Ranije je u tim slučajevima vraćana rezervna tačka, a pozivalac je
        // pri promašenom terenskom zraku prosleđivao nulu — pa je kraj krive
        // odlazio u CENTAR MAPE dok god je kursor iznad horizonta.
        private bool TryCursorAtHeight(float height, out float2 result)
        {
            result = default;
            UnityEngine.Camera camera = UnityEngine.Camera.main;
            if (camera == null || UnityEngine.InputSystem.Mouse.current == null)
            {
                return false;
            }

            UnityEngine.Vector2 mouse = UnityEngine.InputSystem.Mouse.current.position.ReadValue();
            UnityEngine.Ray ray = camera.ScreenPointToRay(new UnityEngine.Vector3(mouse.x, mouse.y, 0f));
            if (math.abs(ray.direction.y) < 1e-4f)
            {
                return false;
            }

            float t = (height - ray.origin.y) / ray.direction.y;
            if (t <= 0f)
            {
                return false;
            }

            UnityEngine.Vector3 point = ray.origin + (ray.direction * t);
            result = new float2(point.x, point.z);
            return true;
        }

        private bool TryBeginHandleDrag(float3 position, Entity hitEntity = default)
        {
            if (m_HandleDragging ||
                !TryGetHandleTarget(out Entity entity, out bool isLane) ||
                !EntityManager.TryGetComponent(entity, out Game.Net.Curve curve))
            {
                return false;
            }

            // Shift/Ctrl klik su selekcioni gestovi — ručka kraj čvora ne
            // sme da ih pojede.
            bool altHeld = false;
            if (UnityEngine.InputSystem.Keyboard.current != null)
            {
                if (UnityEngine.InputSystem.Keyboard.current.shiftKey.isPressed ||
                    UnityEngine.InputSystem.Keyboard.current.ctrlKey.isPressed)
                {
                    return false;
                }

                altHeld = UnityEngine.InputSystem.Keyboard.current.altKey.isPressed;
            }

            float best = kHandlePickRadius;
            int bestIndex = -1;
            for (int k = FirstHandleIndex(isLane); k <= LastHandleIndex(isLane); k++)
            {
                float radius = k == 0 || k == 3 ? kHandlePickRadius : kHandleMidRadius;
                float3 handlePoint = GetHandlePosition(curve.m_Bezier, k);
                if (!TryCursorAtHeight(handlePoint.y, out float2 cursorAtHandle))
                {
                    continue;
                }

                float distance = math.distance(handlePoint.xz, cursorAtHandle);
                if (distance < radius && distance < best)
                {
                    best = distance;
                    bestIndex = k;
                }
            }

            // Nijedna ručka nije pogođena — pokušaj da se uhvati SAMA KRIVA.
            // Tako se segment savija tamo gde ga držiš, po celoj dužini.
            // NE uz ALT: telo krive se sa ALT-om ionako odbija dole, a upis
            // m_CurveGrabT/ofseta pre te kapije bi tiho PREMESTIO već
            // obeleženu tačku — sledeći PgUp bi savijao na mestu ALT-klika.
            if (bestIndex < 0 && !altHeld)
            {
                float3 cursorOnCurve = default;
                bool haveCursor = TryCursorAtHeight(MathUtils.Position(curve.m_Bezier, 0.5f).y, out float2 cursorFlat);
                if (haveCursor)
                {
                    cursorOnCurve = new float3(cursorFlat.x, 0f, cursorFlat.y);
                    MathUtils.Distance(curve.m_Bezier.xz, cursorFlat, out float grabT);

                    // Krajevi se ne hvataju ovako — tamo su krajnje ručke.
                    // Telo krive je NEVIDLJIVA meta široka šest metara i mora
                    // da ustupi: ako je pod kursorom neki drugi selektabilan
                    // objekat, klik pripada njemu. Bez ovoga se pored savijene
                    // deonice ne može selektovati ništa — klik se tiho pojede.
                    bool blockedByOther = hitEntity != Entity.Null && hitEntity != entity;
                    if (!blockedByOther && grabT > 0.12f && grabT < 0.88f &&
                        math.distance(MathUtils.Position(curve.m_Bezier.xz, grabT), cursorFlat) <= kCurveGrabRadius)
                    {
                        m_CurveGrabT = grabT;

                        // Hvatanje je RELATIVNO: pamti se razmak između tačke
                        // na krivoj i kursora u trenutku hvatanja. Bez toga
                        // kriva na prvom frejmu skoči do kursora — a kursor
                        // sme da bude i šest metara od ose, pa se deonica
                        // savije čim je dodirneš.
                        m_CurveGrabOffset = MathUtils.Position(curve.m_Bezier.xz, grabT) - cursorFlat;
                        bestIndex = kCurveHandleIndex;
                    }
                }
            }

            if (bestIndex < 0)
            {
                return false;
            }

            // ALT je inače selekcioni gest, ali NAD KRAJNJOM RUČKOM OGRADE on
            // znači "klizni spoj po liniji suseda" — dokumentovano kao
            // Alt + drag. Bez ovog izuzetka je Alt obarao hvatanje ručke, pa
            // se umesto klizanja spoja vukla cela ograda.
            if (altHeld && !(isLane && (bestIndex == 0 || bestIndex == 3)))
            {
                return false;
            }

            // DVOKORAK: prvi klik samo OBELEŽI ručku (zelena; PgUp/PgDn odmah
            // rade na njoj) — vuča kreće tek pritiskom na već obeleženu.
            // Bez ovoga je svaki mali trzaj miša pri kliku pomerao krivu.
            // Telo krive nema svoj indeks po tački — uvek je 4 — pa bi se
            // dvokorak posle prvog savijanja preskakao zauvek: svaki naredni
            // pritisak bilo gde u oreolu odmah kreće vuču, na novom mestu.
            // Zato se za njega poredi i TAČKA hvatanja.
            bool sameHandle = bestIndex == m_StickyHandleIndex && entity == m_StickyHandleEntity;
            if (sameHandle && bestIndex == kCurveHandleIndex)
            {
                float3 armed = MathUtils.Position(curve.m_Bezier, m_StickyCurveGrabT);
                float3 pressed = MathUtils.Position(curve.m_Bezier, m_CurveGrabT);
                sameHandle = math.distance(armed.xz, pressed.xz) <= kHandlePickRadius;
            }

            if (!sameHandle)
            {
                m_StickyHandleIndex = bestIndex;
                m_StickyHandleEntity = entity;
                m_StickyCurveGrabT = m_CurveGrabT;

                // Ofset hvatanja se nuluje već pri OBELEŽAVANJU: putanja
                // "obeleži pa PgUp" nikad ne prolazi kroz EndHandleDrag, pa bi
                // ustajali ofset (do 6 m — ceo promašaj klika) prvi PgUp
                // pretvorio u bočni skok krive. Vuča ga ionako računa iznova
                // pri svom pritisku.
                if (bestIndex == kCurveHandleIndex)
                {
                    m_CurveGrabOffset = float2.zero;
                }

                return true;
            }

            // Undo PRE prvog pomaka — lane/net snimci hvataju celu krivu.
            PushTransformUndo();

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            float3 point = GetHandlePosition(curve.m_Bezier, bestIndex);
            m_HandleDragging = true;
            m_HandleIsLane = isLane;
            m_HandleIndex = bestIndex;
            m_HandleEntity = entity;
            m_HandleHeightOffset = point.y - TerrainUtils.SampleHeight(ref heightData, point);
            return true;
        }

        // Po frejmu tokom povlačenja: tačka na terenski pogodak + očuvan ofset.
        private void UpdateHandleDrag(float3 position)
        {
            if (!m_HandleDragging ||
                !EntityManager.Exists(m_HandleEntity) ||
                !EntityManager.TryGetComponent(m_HandleEntity, out Game.Net.Curve curve))
            {
                EndHandleDrag();
                return;
            }

            // Kursor u RAVNI VISINE ručke — isto kao pri biranju. Sirovi
            // terenski pogodak je na mostu padao desetine metara IZA ručke
            // (paralaksa), pa je prvi frejm vuče teleportovao kraj.
            float3 handleNow = GetHandlePosition(curve.m_Bezier, m_HandleIndex);
            if (!TryCursorAtHeight(handleNow.y, out float2 cursor))
            {
                // Kursor iznad horizonta ili bez kamere: ovaj frejm se
                // preskače. Ručka ostaje gde jeste dok se miš ne vrati na
                // stranu na kojoj projekcija postoji.
                return;
            }

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData();
            float3 target = new float3(cursor.x, 0f, cursor.y);
            target.y = TerrainUtils.SampleHeight(ref heightData, target) + m_HandleHeightOffset;

            ApplyHandleTarget(m_HandleEntity, m_HandleIsLane, m_HandleIndex, target, allowSnaps: true);
        }

        // Zajednički upis ručke: koriste ga i drag (sa snapovima) i PgUp/PgDn
        // nad KLIKOM izabranom ručkom (bez snapova — čist vertikalni pomak).
        private void ApplyHandleTarget(Entity entity, bool isLane, int index, float3 target, bool allowSnaps)
        {
            if (!EntityManager.TryGetComponent(entity, out Game.Net.Curve curve))
            {
                return;
            }

            // Snap logika za srednje ručke, po prioritetu:
            // 1) SREDIŠNJI (ljubičasti) kružić na polovini pravca — bilo koja
            //    srednja ručka u njemu ispravlja CEO segment odjednom;
            // 2) sopstveni (narandžasti) vodič — legne samo OVA ručka, druga
            //    zadržava luk; ako je druga već (skoro) na pravcu, egzaktno
            //    se ispravi ceo segment.
            bool wasCenterSnapped = m_HandleSnappedCenter;
            bool wasStraightSnapped = m_HandleSnappedStraight;
            m_HandleSnappedStraight = false;
            m_HandleSnappedCenter = false;
            bool fullyStraightened = false;
            if (allowSnaps && (index == 1 || index == 2))
            {
                // HISTEREZA: ulazi se u hvatanje na jednom pragu, a izlazi na
                // širem. Bez toga, tik uz granicu se svaki frejm smenjuju
                // "uhvaćeno" (kriva se ispravi, kontrolna tačka skoči na
                // tetivu) i "pušteno" (oblik se vrati) — a pošto ručka stoji
                // NA kontrolnoj tački, to se vidi kao treperenje.
                float snapRadius = StraightSnapRadius(target);
                float3 centerGuide = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, 0.5f);
                float centerThreshold = wasCenterSnapped ? snapRadius * 1.6f : snapRadius;
                if (math.distance(target.xz, centerGuide.xz) <= centerThreshold)
                {
                    // PROBNO ispravljanje: pri ULASKU se zapamti zatečeni oblik,
                    // da izlazak bez puštanja može da ga vrati.
                    if (!wasCenterSnapped)
                    {
                        m_HandleBackupB = curve.m_Bezier.b;
                        m_HandleBackupC = curve.m_Bezier.c;
                    }

                    curve.m_Bezier.b = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, kHandleT1);
                    curve.m_Bezier.c = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, kHandleT2);
                    m_HandleSnappedCenter = true;
                    fullyStraightened = true;
                }
                else
                {
                    // Izlazak iz središnjeg kruga bez puštanja: vrati oblik pre
                    // ulaska (vučena tačka se odmah ponovo reši na kursor).
                    if (wasCenterSnapped)
                    {
                        curve.m_Bezier.b = m_HandleBackupB;
                        curve.m_Bezier.c = m_HandleBackupC;
                    }

                    float t = index == 1 ? kHandleT1 : kHandleT2;
                    float3 guide = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, t);
                    float guideThreshold = wasStraightSnapped ? snapRadius * 1.6f : snapRadius;
                    if (math.distance(target.xz, guide.xz) <= guideThreshold)
                    {
                        m_HandleSnappedStraight = true;
                        target = guide;

                        // Meri se DRUGA KONTROLNA TAČKA, ne tačka na krivoj.
                        // Tačka na krivoj zavisi od OBE kontrolne, pa se kod
                        // izraženog S njihova odstupanja ponište i test slaže
                        // "već je pravo" — a onda se obe zakucaju na tetivu i
                        // namerni luk prve polovine nestane u jednom frejmu.
                        float tOther = index == 1 ? kHandleT2 : kHandleT1;
                        float3 otherControl = index == 1 ? curve.m_Bezier.c : curve.m_Bezier.b;
                        float3 otherGuide = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, tOther);
                        if (math.distance(otherControl.xz, otherGuide.xz) <= 0.25f)
                        {
                            curve.m_Bezier.b = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, kHandleT1);
                            curve.m_Bezier.c = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, kHandleT2);
                            fullyStraightened = true;
                        }
                    }
                }
            }

            // ALT nad krajnjom ručkom OGRADE: spoj klizi po pravoj između
            // svoja dva suseda — isto što Alt radi sa čvorom puta. Kod ograda
            // spoj nema svoju ručku, pa se hvata preko krajnje ručke koja
            // stoji tačno na njemu.
            if (allowSnaps && isLane && (index == 0 || index == 3) &&
                UnityEngine.InputSystem.Keyboard.current != null &&
                UnityEngine.InputSystem.Keyboard.current.altKey.isPressed)
            {
                bool innerJoint = TryGetLaneJointLine(entity, index == 0, out float3 lineFrom, out float3 lineTo);
                if (!innerJoint)
                {
                    // SPOLJNI kraj lanca nema drugog suseda, pa klizi po
                    // produžetku SOPSTVENE karike (prava kroz oba njena
                    // kraja) — isto što Alt radi sa krajnjim čvorom puta.
                    // Sme i PREKO trenutnog kraja (produžavanje ograde).
                    lineFrom = index == 0 ? curve.m_Bezier.d : curve.m_Bezier.a;
                    lineTo = index == 0 ? curve.m_Bezier.a : curve.m_Bezier.d;
                }

                float2 lineDelta = lineTo.xz - lineFrom.xz;
                float lineLength = math.length(lineDelta);
                if (lineLength > 1e-3f)
                {
                    float2 direction = lineDelta / lineLength;

                    // Klamp drži kraj bar pola metra od druge tačke — nulta
                    // karika ruši lanac. Spoj između dva suseda je ograničen
                    // i sa gornje strane; spoljni kraj sme da produžava.
                    float along = math.dot(target.xz - lineFrom.xz, direction);
                    along = innerJoint
                        ? math.clamp(along, 0.5f, lineLength - 0.5f)
                        : math.max(along, 0.5f);
                    float2 onLine = lineFrom.xz + (direction * along);
                    target.x = onLine.x;
                    target.z = onLine.y;

                    // Visina pod TAČKOM NA PRAVOJ, ne pod kursorom: na nagibu
                    // je kursor i desetine metara od spoja, pa bi spoj dobio
                    // visinu tuđeg parčeta terena.
                    TerrainHeightData slideTerrain = m_TerrainSystem.GetHeightData();
                    target.y = TerrainUtils.SampleHeight(ref slideTerrain, new float3(onLine.x, 0f, onLine.y)) + m_HandleHeightOffset;
                }
            }

            // Inače: reši kontrolnu tačku tako da kriva PROLAZI kroz kursor na
            // t ručke — B(t) = (1-t)³a + 3(1-t)²t·b + 3(1-t)t²·c + t³d.
            if (!fullyStraightened)
            {
                switch (index)
                {
                    case 0:
                        MoveCurveEndpoint(ref curve.m_Bezier, target, movingStart: true);
                        break;
                    case 1:
                        // Jedan na jedan: koliko miš, toliko kontrolna tačka.
                        curve.m_Bezier.b = target;
                        break;
                    case 2:
                        curve.m_Bezier.c = target;
                        break;
                    case kCurveHandleIndex:
                        BendCurveAt(ref curve.m_Bezier, m_CurveGrabT,
                            new float3(target.x + m_CurveGrabOffset.x, target.y, target.z + m_CurveGrabOffset.y));
                        break;
                    default:
                        MoveCurveEndpoint(ref curve.m_Bezier, target, movingStart: false);
                        break;
                }
            }

            curve.m_Length = MathUtils.Length(curve.m_Bezier);
            EntityManager.SetComponentData(entity, curve);

            // ELEVACIJA. Kriva sama po sebi ne drži deonicu u vazduhu — igra
            // je preračunava iz Game.Net.Elevation i bez ovog upisa podignutu
            // tačku vrati na teren čim je nešto takne. Visina se meri na
            // SREDINI (tako je definisan float2: levo/desno na sredini), a
            // SetNetElevation čuva zatečenu razliku strana i skida komponentu
            // kad deonica legne na tlo.
            TerrainHeightData handleHeightData = m_TerrainSystem.GetHeightData();
            float3 curveMiddle = LaneMidpoint(curve.m_Bezier);
            SetNetElevation(entity, curveMiddle.y - TerrainUtils.SampleHeight(ref handleHeightData, curveMiddle));

            // Kraj ograde vuče i container čvor + lančanog suseda.
            if (isLane &&
                (index == 0 || index == 3) &&
                EntityManager.TryGetComponent(entity, out Game.Net.Edge edge))
            {
                // ...WithElevation: čvor i lančani sused moraju da ponesu i
                // visinu, inače ostanu na terenu i lanac se pocepa po visini.
                if (index == 0)
                {
                    MoveLaneNodeWithElevation(edge.m_Start, curve.m_Bezier.a, curve.m_Bezier.b - curve.m_Bezier.a, entity, null);
                }
                else
                {
                    MoveLaneNodeWithElevation(edge.m_End, curve.m_Bezier.d, curve.m_Bezier.d - curve.m_Bezier.c, entity, null);
                }
            }

            // Kraj NET segmenta: čvor ostaje, ali mora na update da igra
            // preračuna geometriju spoja dok se kraj vuče.
            if (!isLane &&
                (index == 0 || index == 3) &&
                EntityManager.TryGetComponent(entity, out Game.Net.Edge netEdge))
            {
                Entity joinNode = index == 0 ? netEdge.m_Start : netEdge.m_End;
                if (EntityManager.Exists(joinNode))
                {
                    EntityManager.AddComponent<Updated>(joinNode);
                    m_DelayedNetSettle[joinNode] = 4;
                }
            }

            EntityManager.AddComponent<Updated>(entity);
            EntityManager.AddComponent<BatchesUpdated>(entity);
        }

        // Pomeranje KRAJA sa "zaključanim" sredinama: proporcije b/c u odnosu
        // na tetivu se očuvaju (per-osa, klampovano — ista formula kao susedne
        // ivice mreža), pa kriva zadržava oblik dok kraj putuje.
        private static void MoveCurveEndpoint(ref Bezier4x3 bezier, float3 target, bool movingStart)
        {
            // Klik na krajnju ručku bez pomeranja NE sme da dira krivu:
            // tetivne proporcije bi na degenerisanoj osi (prava deonica po x
            // ili z, ili ručno dignut luk) i za nulti pomak srušile oblik.
            float3 currentEnd = movingStart ? bezier.a : bezier.d;
            if (math.distancesq(currentEnd, target) < 1e-6f)
            {
                return;
            }

            // Kontrolne tačke se vode kao OFSET od tetive u 1/3 i 2/3, po
            // svim osama. Raniji model je koristio tetivne proporcije
            // (offsetB / span) i tu je bio bug: kad je span po nekoj osi mali
            // ali ne i nula — recimo ručno dignut luk od 8 m na deonici čiji
            // se krajevi razlikuju pola metra po visini — proporcija ispadne
            // 16, klamp je odseče na 2, i luk se spljošti. Ofset od tetive
            // nema tu rupu: oblik preživi svaki pomak kraja, a b i c glatko
            // prate pomak jer se i sama tetiva pomera.
            float3 offsetFromChordB = bezier.b - math.lerp(bezier.a, bezier.d, 1f / 3f);
            float3 offsetFromChordC = bezier.c - math.lerp(bezier.a, bezier.d, 2f / 3f);
            float2 oldChord = (bezier.d - bezier.a).xz;

            if (movingStart)
            {
                bezier.a = target;
            }
            else
            {
                bezier.d = target;
            }

            // Ofset se VRTI I SKALIRA sa tetivom. Čuvanje u svetskim osama je
            // pri okretanju kraja za 90° spljoštavalo luk, a preko 90° ga
            // preslikavalo — bočno savijanje pripada tetivi, ne svetu.
            // Visina (luk) ostaje kakva jeste: rastezanje deonice ne treba da
            // joj menja uzdignuće.
            float2 newChord = (bezier.d - bezier.a).xz;
            float oldLength = math.length(oldChord);
            float newLength = math.length(newChord);
            if (oldLength > 1e-3f && newLength > 1e-3f)
            {
                float2 oldDirection = oldChord / oldLength;
                float2 newDirection = newChord / newLength;
                float scale = math.clamp(newLength / oldLength, 0.25f, 4f);
                offsetFromChordB.xz = RotateAndScale(offsetFromChordB.xz, oldDirection, newDirection, scale);
                offsetFromChordC.xz = RotateAndScale(offsetFromChordC.xz, oldDirection, newDirection, scale);
            }

            bezier.b = math.lerp(bezier.a, bezier.d, 1f / 3f) + offsetFromChordB;
            bezier.c = math.lerp(bezier.a, bezier.d, 2f / 3f) + offsetFromChordC;
        }

        // Zarotiraj vektor za ugao između dva jedinična pravca i skaliraj ga.
        private static float2 RotateAndScale(float2 value, float2 from, float2 to, float scale)
        {
            float cos = math.dot(from, to);
            float sin = (from.x * to.y) - (from.y * to.x);
            return new float2(
                (value.x * cos) - (value.y * sin),
                (value.x * sin) + (value.y * cos)) * scale;
        }

        // Pomeri tačku krive na parametru t do cilja, tako što se potreban
        // pomak podeli na OBE kontrolne tačke srazmerno njihovom uticaju na
        // toj tački (rešenje najmanjeg pomaka). Zato je potez gladak: na
        // sredini se obe pomere podjednako, bliže kraju više ona bliža.
        private static void BendCurveAt(ref Bezier4x3 bezier, float t, float3 target)
        {
            float oneMinus = 1f - t;
            float weightB = 3f * oneMinus * oneMinus * t;
            float weightC = 3f * oneMinus * t * t;
            float norm = (weightB * weightB) + (weightC * weightC);
            if (norm < 1e-6f)
            {
                return;
            }

            float3 delta = target - MathUtils.Position(bezier, t);
            bezier.b += delta * (weightB / norm);
            bezier.c += delta * (weightC / norm);
        }

        private void EndHandleDrag()
        {
            if (!m_HandleDragging)
            {
                return;
            }

            // Segment mreže: čist završni update preko oba čvora (raskrsnice).
            if (!m_HandleIsLane && m_SelectedNetEdges.Count == 1)
            {
                SettleNetworks();
            }

            // Puštena ručka ostaje IZABRANA (klik = selekcija tačke).
            m_StickyHandleIndex = m_HandleIndex;
            m_StickyHandleEntity = m_HandleEntity;
            m_StickyCurveGrabT = m_CurveGrabT;

            // Ofset hvatanja se poništava: obeležena tačka od sada LEŽI na
            // krivoj, pa PgUp/PgDn nad njom sme da je diže bez bočnog skoka.
            m_CurveGrabOffset = float2.zero;

            m_HandleDragging = false;
            m_HandleIndex = -1;
            m_HandleEntity = Entity.Null;
            m_HandleSnappedStraight = false;
            m_HandleSnappedCenter = false;

            NetProbe("posle vuce rucke");
        }

        // PgUp/PgDn nad klikom izabranom ručkom: čist vertikalni pomak te
        // tačke (bez snapova). Undo se gura na prvi pritisak u nizu.
        private bool TryAdjustStickyHandleHeight(float delta, bool pushUndo)
        {
            // kCurveHandleIndex je van opsega ručki po tački, ali JESTE
            // obeležena tačka: bez njega je PgUp posle savijanja tela tiho
            // padao na "podigni celu selekciju".
            bool stickyIsCurveBody = m_StickyHandleIndex == kCurveHandleIndex;
            if (m_StickyHandleIndex < 0 ||
                !TryGetHandleTarget(out Entity entity, out bool isLane) ||
                entity != m_StickyHandleEntity ||
                (!stickyIsCurveBody && m_StickyHandleIndex < FirstHandleIndex(isLane)) ||
                (!stickyIsCurveBody && m_StickyHandleIndex > LastHandleIndex(isLane)) ||
                !EntityManager.TryGetComponent(entity, out Game.Net.Curve curve))
            {
                m_StickyHandleIndex = -1;
                m_StickyHandleEntity = Entity.Null;
                return false;
            }

            if (pushUndo)
            {
                PushTransformUndo();
            }

            float3 target = GetHandlePosition(curve.m_Bezier, m_StickyHandleIndex) + new float3(0f, delta, 0f);
            ApplyHandleTarget(entity, isLane, m_StickyHandleIndex, target, allowSnaps: false);
            return true;
        }

        // Ručke u overlay-u: puni krug za aktivnu, prsten za ostale.
        private void DrawHandleOverlays(OverlayRenderSystem.Buffer overlayBuffer)
        {
            if (!TryGetHandleTarget(out Entity entity, out bool isLane) ||
                !EntityManager.TryGetComponent(entity, out Game.Net.Curve curve))
            {
                return;
            }

            // Vodiči za ravno dok se vuče srednja ručka: sopstveni (narandžast,
            // poravnava samo ovu ručku) + središnji ljubičasti (ispravlja ceo
            // segment). Aktivan snap boji svoj kružić zeleno.
            if (m_HandleDragging && (m_HandleIndex == 1 || m_HandleIndex == 2))
            {
                float t = m_HandleIndex == 1 ? kHandleT1 : kHandleT2;
                float3 guide = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, t);
                overlayBuffer.DrawCircle(
                    m_HandleSnappedStraight ? kHandleActiveColor : kHandleGuideColor,
                    default,
                    0.2f,
                    0,
                    new float2(0f, 1f),
                    guide,
                    StraightSnapRadius(guide) * 2f);

                float3 centerGuide = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, 0.5f);
                overlayBuffer.DrawCircle(
                    m_HandleSnappedCenter ? kHandleActiveColor : kHandleCenterColor,
                    default,
                    0.25f,
                    0,
                    new float2(0f, 1f),
                    centerGuide,
                    StraightSnapRadius(centerGuide) * 2f);
            }

            // Kukice: tanka linija veže svaku kontrolnu tačku sa njenim krajem,
            // da se vidi kojoj strani pripada.
            if (FirstHandleIndex(isLane) <= 1 && LastHandleIndex(isLane) >= 2)
            {
                overlayBuffer.DrawLine(kHandleGuideColor,
                    new Colossal.Mathematics.Line3.Segment(curve.m_Bezier.a, curve.m_Bezier.b), 0.15f);
                overlayBuffer.DrawLine(kHandleGuideColor,
                    new Colossal.Mathematics.Line3.Segment(curve.m_Bezier.d, curve.m_Bezier.c), 0.15f);
            }

            // Obeležena tačka na TELU krive (dvokorak, PgUp meta): bez ovoga
            // prvi klik izgleda progutan, a PgUp radi na nevidljivoj tački.
            bool bodyActive = (m_HandleDragging && m_HandleIndex == kCurveHandleIndex) ||
                (!m_HandleDragging && m_StickyHandleIndex == kCurveHandleIndex && entity == m_StickyHandleEntity);
            if (bodyActive)
            {
                float3 bodyPoint = MathUtils.Position(curve.m_Bezier, m_HandleDragging ? m_CurveGrabT : m_StickyCurveGrabT);
                overlayBuffer.DrawCircle(
                    kHandleActiveColor,
                    default,
                    0.25f,
                    0,
                    new float2(0f, 1f),
                    bodyPoint,
                    kHandleMidRadius * 1.6f);
            }

            for (int k = FirstHandleIndex(isLane); k <= LastHandleIndex(isLane); k++)
            {
                bool active = (m_HandleDragging && k == m_HandleIndex) ||
                    (!m_HandleDragging && k == m_StickyHandleIndex && entity == m_StickyHandleEntity);
                bool endpoint = k == 0 || k == 3;
                overlayBuffer.DrawCircle(
                    active ? kHandleActiveColor : endpoint ? kHandleColor : kHandleMidColor,
                    default,
                    active ? 0.5f : endpoint ? 0.35f : 0.25f,
                    0,
                    new float2(0f, 1f),
                    GetHandlePosition(curve.m_Bezier, k),
                    (endpoint ? kHandlePickRadius : kHandleMidRadius) * 2f);
            }
        }
    }
}
