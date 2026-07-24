using UnityEngine;
using System.Collections.Generic;
using DG.Tweening;

namespace Gameplay
{
    // Voiture PNJ : roule tranquille dans la map avec un comportement "humain".
    // Deplacement cinematique (transform, pas de physique) facon Pedestrian.
    // Comportements :
    //  - croisiere a vitesse variable, accel/freinage doux ;
    //  - 3 antennes raycast devant (centre + 2 lateraux) : freine/s'arrete
    //    devant un obstacle (autre voiture, pieton, mur), esquive au besoin ;
    //  - errance : change de cap de temps en temps ;
    //  - reste dans les limites de la map (demi-tour aux bords) ;
    //  - petites pauses aleatoires facon feu rouge / hesitation ;
    //  - roues qui tournent selon la vitesse.
    public class AICarController : MonoBehaviour
    {
        [Header("Conduite")]
        [SerializeField] private float cruiseSpeed = 6f;      // vitesse de croisiere de base
        [SerializeField] private float speedVariation = 2f;   // +/- aleatoire par voiture
        [SerializeField] private float accel = 6f;            // m/s^2 pour atteindre la cible
        [SerializeField] private float brake = 14f;           // m/s^2 en freinage (plus mordant)
        [SerializeField] private float turnRate = 90f;        // deg/s de rotation vers le cap voulu

        [Header("Detection obstacles")]
        [SerializeField] private float feelerLength = 7f;     // portee des antennes avant
        [SerializeField] private float stopDistance = 2.2f;   // en dessous : arret complet
        [SerializeField] private float sideAngle = 28f;       // ecart des antennes laterales
        [SerializeField] private float rayHeight = 0.5f;      // hauteur des rayons (pare-choc)
        [SerializeField] private LayerMask obstacleMask = ~0; // tout par defaut

        [Header("Circulation (reseau routier)")]
        [Tooltip("Decalage voie de droite depuis l'axe de la rue (x cellSize). Voie INTERIEURE, degagee des voitures garees au trottoir.")]
        [SerializeField] private float laneFrac = 0.12f;
        [SerializeField] private float arriveDist = 2.2f;      // distance pour valider le waypoint courant
        [SerializeField, Range(0f, 1f)] private float straightBias = 0.7f; // proba de continuer tout droit au carrefour
        [SerializeField] private float turnSlow = 0.45f;       // facteur de vitesse en approche de virage
        [SerializeField] private float carFollowGap = 4.5f;    // distance mini derriere la voiture devant
        [Tooltip("DEBUG/ambient : roule sans conducteur (sinon garee tant qu'aucun pieton).")]
        [SerializeField] private bool autoDrive = false;

        [Header("Errance (legacy, inutilise)")]
        [SerializeField] private Vector2 headingHold = new Vector2(3f, 7f); // duree avant nouveau cap
        [SerializeField] private float wanderTurn = 45f;      // ecart max du nouveau cap (deg)
        [SerializeField] private float mapBound = 90f;        // demi-taille jouable (Ground 200 -> 90)

        [Header("Pauses humaines")]
        [SerializeField] private Vector2 pauseEvery = new Vector2(10f, 25f); // intervalle entre pauses
        [SerializeField] private Vector2 pauseLength = new Vector2(1.5f, 4f);

        [Header("Collision entre voitures")]
        [SerializeField] private float recoilSpeed = 3.5f;      // vitesse de recul au choc
        [SerializeField] private float recoilTime = 0.22f;      // duree du recul
        [SerializeField] private float bumpStun = 0.7f;         // pause etourdie apres choc
        [SerializeField] private float bumpCooldownTime = 1.2f; // anti re-declenchement
        [SerializeField] private float impactCurve = 2.2f;      // >1 = reponse exponentielle (petits chocs discrets)

        [Header("Roues (optionnel)")]
        [SerializeField] private Transform[] wheels;          // tournent avec la vitesse (spin)
        [SerializeField] private float wheelRadius = 0.4f;
        [SerializeField] private Transform[] frontSteer;      // pivots des roues avant (braquage)
        [SerializeField] private float maxSteer = 30f;        // angle de braquage max (deg)
        [SerializeField] private float steerGain = 0.33f;     // braquage par deg/s de rotation
        [SerializeField] private float steerSmooth = 10f;     // lissage du braquage

        [Header("Juice cartoon (pivot Visual)")]
        [SerializeField] private float leanFactor = 0.16f;    // roulis par deg/s de braquage
        [SerializeField] private float maxLean = 12f;         // roulis max en virage (deg)
        [SerializeField] private float pitchFactor = 0.7f;    // tangage par m/s^2 (accel/frein)
        [SerializeField] private float maxPitch = 9f;         // plongee/cabrage max (deg)
        [SerializeField] private float tiltSmooth = 9f;       // lissage inclinaison

        private float speed;            // vitesse courante (m/s)
        private float targetHeading;    // yaw voulu (deg)
        private float myCruise;         // croisiere propre a cette voiture
        private float headingTimer;
        private float pauseTimer;       // temps avant prochaine pause
        private float pauseLeft;        // temps de pause restant
        private bool blocked;           // arrete par un obstacle ce frame

        // ---- Circulation sur le reseau routier ----
        // La voiture suit la grille : cellule courante + direction (une des 4),
        // vise le point "voie de droite" de la cellule suivante, choisit un virage
        // aux carrefours. Les feux et la priorite se greffent par-dessus (Stage 2).
        private City.CityGridAuthoring cityAuth;
        private Vector2Int cell, gridDir;   // cellule + direction en coords grille
        private Vector3 waypoint;           // cible monde courante (voie de droite)
        private bool onRoad;                // a accroche la grille au demarrage
        private Vector2Int reservedInter = NoCell; // intersection reservee (priorite)
        private static readonly Vector2Int NoCell = new Vector2Int(-9999, -9999);
        private static readonly Vector2Int[] GridDirs =
            { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };
        // Qui occupe quelle intersection (une seule voiture a la fois -> priorite/stop).
        private static readonly Dictionary<Vector2Int, AICarController> InterOwner = new();

        // Reset des statics a chaque entree en play (Reload Domain peut etre off ->
        // sinon Cars/InterOwner gardent des entrees fantomes de la session precedente).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { Cars.Clear(); InterOwner.Clear(); }

        // juice : pivot cosmetique (ne touche jamais la racine physique/raycast)
        private Transform visual;
        private Vector3 vBasePos, vBaseScale;
        private float curRoll, curPitch, prevYaw, prevSpeed;
        private bool wasMoving, idleActive;
        private Tween idleTween;
        private float curSteer, steerPrevYaw;

        // Occupation : la voiture ne roule QUE si un pieton la conduit.
        private Pedestrian driver;
        public bool Occupied => driver != null;
        public Pedestrian Driver => driver;
        public bool Busy => crashSpinLeft > 0f || recoilLeft > 0f; // en plein choc : pas montable

        // collision voiture
        private static readonly List<AICarController> Cars = new();
        private BoxCollider box;
        private SpeechBubble bubble;
        private float bumpCooldown, stunLeft, recoilLeft;
        private Vector3 recoilDir;
        private float crashSpinLeft, crashSpinVel; // tete-a-queue apres choc
        private MeshRenderer bodyRend; private Color bodyBase; // flash carrosserie
        private CarFollowCamera camShake;
        private static readonly string[] bumpLines = { "Klaxon !", "Ohh !", "Regarde devant !", "Aie !", "Pousse-toi !", "Eh oh !" };

        private void Start()
        {
            targetHeading = transform.eulerAngles.y;
            myCruise = cruiseSpeed + Random.Range(-speedVariation, speedVariation);
            headingTimer = Random.Range(headingHold.x, headingHold.y);
            pauseTimer = Random.Range(pauseEvery.x, pauseEvery.y);
            // garee au depart : ne roule qu'une fois montee par un pieton
            speed = 0f;

            visual = transform.Find("Visual");
            if (visual) { vBasePos = visual.localPosition; vBaseScale = visual.localScale; }
            prevYaw = transform.eulerAngles.y;
            steerPrevYaw = prevYaw;
            prevSpeed = speed;

            box = GetComponent<BoxCollider>();
            bubble = GetComponent<SpeechBubble>();

            var bodyT = transform.Find("Visual/Body");
            if (bodyT) { bodyRend = bodyT.GetComponent<MeshRenderer>(); bodyBase = bodyRend.material.GetColor("_BaseColor"); }
            var mainCam = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
            if (mainCam) camShake = mainCam.GetComponent<CarFollowCamera>();

            cityAuth = City.CityGridAuthoring.Active;
            if (autoDrive) InitRoad(); // ambient/debug : demarre la circulation seule
        }

        private void OnEnable() { Cars.Add(this); }

        // ---- Conduite par un pieton ----

        // Voiture vide (montable) la plus proche dans le rayon, ou null.
        public static AICarController FindEmptyNear(Vector3 pos, float radius)
        {
            AICarController best = null;
            float bestSq = radius * radius;
            foreach (var c in Cars)
            {
                if (c == null || c.Occupied || c.Busy) continue;
                float sq = (c.transform.position - pos).sqrMagnitude;
                if (sq <= bestSq) { bestSq = sq; best = c; }
            }
            return best;
        }

        public Vector3 SeatPoint => transform.position + Vector3.up * 0.4f;      // le pieton disparait ici
        public Vector3 ExitPoint => transform.position - transform.right * 1.6f; // ressort a gauche

        // Un pieton monte : la voiture se remet a rouler. False si deja prise / en choc.
        public bool TryEnter(Pedestrian ped)
        {
            if (Occupied || Busy || ped == null) return false;
            driver = ped;
            myCruise = cruiseSpeed + Random.Range(-speedVariation, speedVariation);
            targetHeading = transform.eulerAngles.y;
            headingTimer = Random.Range(headingHold.x, headingHold.y);
            pauseTimer = Random.Range(pauseEvery.x, pauseEvery.y);
            if (bubble) bubble.Show("En route !", false, 1.2f);
            InitRoad(); // accroche la grille et part sur la route
            return true;
        }

        public void ClearDriver() { driver = null; ReleaseInter(); }

        // Explosion (boost) : ejecte le conducteur puis detruit la voiture. FX
        // toujours ; shake camera + hitstop seulement si le joueur est l'auteur.
        public void Explode(Vector3 hitDir, bool playerInvolved = true)
        {
            Vector3 pos = transform.position + Vector3.up * 0.6f;
            CrashFx.Play(pos, 1f);
            CrashFx.Play(pos + Vector3.up * 0.35f, 1f); // double gerbe = plus gros boum
            if (playerInvolved)
            {
                Hitstop.Punch(0.22f, 0.12f, 0.5f);
                if (camShake != null) camShake.Shake(1.6f, 0.6f);
            }
            if (driver != null)
            {
                var d = driver; driver = null;
                d.EjectFromCar(transform.position + Vector3.up * 1.2f, hitDir);
            }
            Destroy(gameObject);
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            if (bumpCooldown > 0f) bumpCooldown -= dt;
            CheckBump();

            float desired;
            bool driving = (Occupied || autoDrive) && crashSpinLeft <= 0f && recoilLeft <= 0f;
            if (driving)
            {
                if (!onRoad) InitRoad();            // (re)accroche la grille au besoin
                desired = onRoad ? DriveRoad(dt) : 0f;
            }
            else { desired = 0f; blocked = false; } // garee / en plein choc : ne roule pas
            if (stunLeft > 0f) { stunLeft -= dt; desired = 0f; } // etourdi apres un choc

            // accel ou freinage vers la vitesse voulue
            float rate = desired < speed ? brake : accel;
            speed = Mathf.MoveTowards(speed, desired, rate * dt);

            // rotation : tete-a-queue pendant le recul du choc, sinon cap doux
            if (crashSpinLeft > 0f)
            {
                crashSpinLeft -= dt;
                transform.Rotate(0f, crashSpinVel * dt, 0f, Space.World);
            }
            else
            {
                float yaw = Mathf.MoveTowardsAngle(transform.eulerAngles.y, targetHeading, turnRate * dt);
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }

            // avance a plat sur le cap courant
            transform.position += transform.forward * (speed * dt);

            // recul du choc (recoilDir porte deja la vitesse)
            if (recoilLeft > 0f) { recoilLeft -= dt; transform.position += recoilDir * dt; }

            SpinWheels(dt);
            SteerFrontWheels(dt);
            UpdateJuice(dt);
        }

        // Detecte le contact avec une autre voiture (AABB des BoxCollider) et declenche la reaction.
        private void CheckBump()
        {
            if (box == null || bumpCooldown > 0f) return;
            var b = box.bounds;
            foreach (var c in Cars)
            {
                if (c == this || c.box == null || c.bumpCooldown > 0f) continue;
                if (b.Intersects(c.box.bounds)) { Bump(c); return; }
            }
        }

        // Choc IA-IA : les DEUX voitures encaissent + un seul burst de FX partage au milieu.
        private void Bump(AICarController other)
        {
            Vector3 mid = (transform.position + other.transform.position) * 0.5f + Vector3.up * 0.8f;
            float lin = Mathf.Clamp01((speed + other.speed - 1.5f) / 12f); // selon la vitesse reelle des deux
            float hard = Mathf.Pow(lin, impactCurve);                       // reponse exponentielle
            SpawnCrashFx(mid, hard, playerInvolved: false);                 // IA-IA : FX locaux, pas de shake/hitstop
            ReactCar(other.transform.position, hard);
            other.ReactCar(transform.position, hard);
        }

        // Le joueur (ArcadeCarController, RB dynamique) percute cette voiture PNJ.
        private void OnCollisionEnter(Collision col)
        {
            if (bumpCooldown > 0f) return;
            var player = col.collider.GetComponentInParent<ArcadeCarController>();
            if (player == null) return;

            // Boost dans une voiture = elle explose (et ejecte son conducteur).
            // shake/hitstop seulement si c'est LE JOUEUR (pas le fou / une autre IA).
            if (player.IsBoosting) { Explode(player.transform.forward, player.IsPlayer); return; }

            float impact = Mathf.Max(col.relativeVelocity.magnitude, player.Speed);
            float lin = Mathf.Clamp01((impact - 1.5f) / 13f);  // 0 = frole, 1 = plein pot
            float hard = player.IsBoosting ? 1f : Mathf.Pow(lin, impactCurve); // reponse exponentielle
            if (hard <= 0.005f) { bumpCooldown = 0.2f; return; } // contact quasi nul : on ignore

            Vector3 pt = col.contactCount > 0 ? col.GetContact(0).point : transform.position + Vector3.up * 0.7f;
            SpawnCrashFx(pt, hard, playerInvolved: player.IsPlayer); // shake+hitstop seulement pour le joueur
            ReactCar(col.transform.position, hard);
        }

        // Effets du choc. FX locaux (particules/onde/mot) toujours ; shake camera + hitstop
        // SEULEMENT si le joueur est implique (un accident IA-IA a l'autre bout ne doit rien secouer).
        private void SpawnCrashFx(Vector3 point, float hard, bool playerInvolved)
        {
            CrashFx.Play(point, hard);
            if (!playerInvolved) return;
            if (hard > 0.4f) // hitstop reserve aux vrais chocs
                Hitstop.Punch(Mathf.Lerp(0.4f, 0.16f, hard), hard * 0.1f, 0.15f + hard * 0.35f);
            if (camShake != null && hard > 0.08f)
                camShake.Shake(hard * 1.3f, 0.2f + hard * 0.4f); // amplitude 100% proportionnelle
        }

        // Reaction physique + juice de CETTE voiture, TOUT scale par hard (frole=quasi rien).
        private void ReactCar(Vector3 fromPos, float hard)
        {
            bumpCooldown = bumpCooldownTime;
            stunLeft = 0.12f + hard * 1.0f;

            Vector3 away = transform.position - fromPos; away.y = 0f;
            Vector3 dir = away.sqrMagnitude > 1e-4f ? away.normalized : -transform.forward;
            recoilDir = dir * (recoilSpeed * (0.35f + hard * 2.2f)); // projection proportionnelle
            recoilLeft = recoilTime + hard * 0.35f;
            targetHeading = Quaternion.LookRotation(dir).eulerAngles.y; // repart a l'oppose apres le spin
            headingTimer = Random.Range(headingHold.x, headingHold.y);
            speed = 0f;

            // tete-a-queue : proportionnel (frole = a peine, plein pot = grosse toupie)
            crashSpinLeft = recoilLeft;
            crashSpinVel = (Random.value < 0.5f ? -1f : 1f) * Mathf.Lerp(30f, 700f, hard);

            CrashJuice(hard);
            FlashBody(hard);
            // vide = pas de klaxon parle (personne dedans) ; occupee = le conducteur rale
            if (bubble && Occupied) bubble.Show(hard > 0.55f ? PickHard() : bumpLines[Random.Range(0, bumpLines.Length)], false, 1.6f);
        }

        // Squash&stretch + tonneau/secousse + saut, amplitudes toutes scalees par hard.
        private void CrashJuice(float hard)
        {
            if (!visual) return;
            visual.DOKill();
            visual.localScale = vBaseScale;
            visual.localPosition = vBasePos;
            visual.localRotation = Quaternion.identity;

            // profondeur du squash proportionnelle
            float sq = Mathf.Lerp(0.04f, 0.42f, hard);
            DOTween.Sequence()
                .Append(visual.DOScale(Vector3.Scale(vBaseScale, new Vector3(1f + sq, 1f - sq, 1f + sq)), 0.07f).SetEase(Ease.OutQuad))
                .Append(visual.DOScale(Vector3.Scale(vBaseScale, new Vector3(1f - sq * 0.6f, 1f + sq * 0.85f, 1f - sq * 0.6f)), 0.12f).SetEase(Ease.OutQuad))
                .Append(visual.DOScale(vBaseScale, 0.4f).SetEase(Ease.OutElastic));

            // saut : seulement si le choc est assez fort
            float hop = hard * 0.75f;
            if (hop > 0.03f)
                visual.DOLocalMoveY(vBasePos.y + hop, 0.16f).SetEase(Ease.OutQuad)
                    .OnComplete(() => visual.DOLocalMoveY(vBasePos.y, 0.45f).SetEase(Ease.OutBounce));

            // rotation : tonneau complet seulement sur gros choc, sinon secousse scalee
            if (hard > 0.6f)
                visual.DOLocalRotate(new Vector3(0f, 0f, (Random.value < 0.5f ? 360f : -360f)), 0.55f,
                    RotateMode.FastBeyond360).SetEase(Ease.OutCubic);
            else
                visual.DOPunchRotation(new Vector3(6f, 0f, 26f) * hard, 0.5f, 9, 0.8f);
        }

        // Flash carrosserie proportionnel (frole = a peine plus clair, gros choc = blanc).
        private void FlashBody(float hard)
        {
            if (bodyRend == null) return;
            Color peak = Color.Lerp(bodyBase, Color.white, Mathf.Clamp01(hard * 1.2f));
            bodyRend.material.SetColor("_BaseColor", peak);
            DOVirtual.Float(0f, 1f, 0.15f + hard * 0.15f, x =>
            {
                if (bodyRend != null) bodyRend.material.SetColor("_BaseColor", Color.Lerp(peak, bodyBase, x));
            });
        }

        private static readonly string[] hardLines = { "AAAAH !!", "MES PARE-CHOCS !", "NOOON !", "%@#& !!", "MA CAISSE !!" };
        private static string PickHard() => hardLines[Random.Range(0, hardLines.Length)];

        // Roues avant qui braquent dans le sens du virage (comme une vraie voiture).
        private void SteerFrontWheels(float dt)
        {
            if (frontSteer == null || frontSteer.Length == 0) return;
            float yaw = transform.eulerAngles.y;
            float rate = Mathf.DeltaAngle(steerPrevYaw, yaw) / Mathf.Max(dt, 1e-4f); // deg/s
            steerPrevYaw = yaw;
            float target = Mathf.Clamp(rate * steerGain, -maxSteer, maxSteer);
            curSteer = Mathf.Lerp(curSteer, target, steerSmooth * dt);
            var rot = Quaternion.Euler(0f, curSteer, 0f);
            foreach (var s in frontSteer)
                if (s) s.localRotation = rot;
        }

        // Inclinaisons + squash/stretch + respiration a l'arret, tout sur le pivot Visual.
        private void UpdateJuice(float dt)
        {
            if (!visual) return;
            float safe = Mathf.Max(dt, 1e-4f);

            // braquage (deg/s) -> roulis, accel (m/s^2) -> tangage (plonge au frein, cabre a l'accel)
            float yaw = transform.eulerAngles.y;
            float steer = Mathf.DeltaAngle(prevYaw, yaw) / safe;
            prevYaw = yaw;
            float accelSig = (speed - prevSpeed) / safe;
            prevSpeed = speed;

            float targetRoll = Mathf.Clamp(-steer * leanFactor, -maxLean, maxLean);
            float targetPitch = Mathf.Clamp(-accelSig * pitchFactor, -maxPitch, maxPitch);
            curRoll = Mathf.Lerp(curRoll, targetRoll, tiltSmooth * dt);
            curPitch = Mathf.Lerp(curPitch, targetPitch, tiltSmooth * dt);
            // pendant le stun on laisse le DOPunchRotation du choc jouer
            if (stunLeft <= 0f) visual.localRotation = Quaternion.Euler(curPitch, 0f, curRoll);

            // evenements demarrage / arret
            bool moving = speed > 0.6f;
            if (moving && !wasMoving) Stretch();
            if (!moving && wasMoving) Squash();
            wasMoving = moving;

            // respiration cartoon a l'arret (pas pendant un stun : le saut du choc possede la position)
            if (!moving && !idleActive && stunLeft <= 0f) StartIdle();
            if ((moving || stunLeft > 0f) && idleActive) StopIdle();
        }

        private void Squash()   // ecrase au freinage/arret
        {
            if (idleActive) StopIdle();
            visual.DOKill(); visual.localScale = vBaseScale;
            visual.DOPunchScale(new Vector3(0.18f, -0.24f, 0.12f), 0.4f, 8, 0.7f);
        }

        private void Stretch()  // etire au demarrage (anticipation)
        {
            if (idleActive) StopIdle();
            visual.DOKill(); visual.localScale = vBaseScale;
            visual.DOPunchScale(new Vector3(-0.12f, 0.2f, -0.12f), 0.35f, 8, 0.7f);
        }

        private void StartIdle()
        {
            idleActive = true;
            visual.localPosition = vBasePos;
            idleTween = visual.DOLocalMoveY(vBasePos.y + 0.05f, 1.1f)
                .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo);
        }

        private void StopIdle()
        {
            idleActive = false;
            if (idleTween != null) { idleTween.Kill(); idleTween = null; }
            visual.localPosition = vBasePos;
        }

        private void OnDisable()
        {
            Cars.Remove(this);
            ReleaseInter();
            if (visual) visual.DOKill();
            idleTween = null;
        }

        // ================= Circulation sur le reseau routier =================

        // Accroche la voiture a la grille : cellule route la plus proche + direction
        // initiale alignee sur le cap actuel. Appele a l'entree d'un conducteur / autoDrive.
        private void InitRoad()
        {
            cityAuth = cityAuth != null ? cityAuth : City.CityGridAuthoring.Active;
            if (cityAuth == null || cityAuth.Grid == null) { onRoad = false; return; }
            var grid = cityAuth.Grid;
            Vector3 local = cityAuth.transform.InverseTransformPoint(transform.position);
            grid.WorldToCell(local, out int x, out int y);
            Vector2Int c = new Vector2Int(x, y);
            if (grid.At(c.x, c.y) != City.CellType.Road && !NearestRoad(c, out c)) { onRoad = false; return; }
            cell = c;
            gridDir = SnapDir(c);
            waypoint = WaypointFor(cell, gridDir);
            onRoad = true;
        }

        // Suit la route : oriente vers le waypoint (voie de droite), avance de cellule
        // en cellule, choisit un virage aux carrefours. Renvoie la vitesse voulue
        // (croisiere, ralentie en virage, coupee derriere une voiture ou pour ceder).
        private float DriveRoad(float dt)
        {
            var grid = cityAuth.Grid;
            // re-accroche si projetee loin (knockback) : la cellule courante a trop devie
            Vector3 local = cityAuth.transform.InverseTransformPoint(transform.position);
            grid.WorldToCell(local, out int cx, out int cy);
            if (Mathf.Abs(cx - cell.x) > 1 || Mathf.Abs(cy - cell.y) > 1)
            {
                InitRoad();
                if (!onRoad) return 0f;
            }

            Vector3 toWp = waypoint - transform.position; toWp.y = 0f;
            float dist = toWp.magnitude;
            if (dist < arriveDist)
            {
                Vector2Int next = cell + gridDir;
                if (IsRoad(next)) cell = next;
                gridDir = ChooseDir(cell, gridDir);
                if (reservedInter != NoCell && cell != reservedInter) ReleaseInter(); // sortie d'intersection
                waypoint = WaypointFor(cell, gridDir);
                toWp = waypoint - transform.position; toWp.y = 0f; dist = toWp.magnitude;
            }

            if (toWp.sqrMagnitude > 0.01f)
                targetHeading = Quaternion.LookRotation(toWp).eulerAngles.y;

            float desired = myCruise;
            Vector2Int upcoming = cell + gridDir;
            bool nearInter = IsIntersection(upcoming) && dist < arriveDist * 2.2f;

            // ralentit a l'approche d'un carrefour (virage / prudence)
            if (nearInter) desired *= turnSlow;

            // file d'attente : ralentit / s'arrete derriere la voiture devant.
            // (on ne teste QUE les autres voitures : les rampes se franchissent, les
            //  batiments sont evites en restant dans la voie, les pietons = roadkill.)
            blocked = false;
            float ahead = CarAhead();
            if (ahead <= stopDistance) { blocked = true; desired = 0f; }
            else if (ahead < carFollowGap)
                desired = Mathf.Min(desired, myCruise * Mathf.Clamp01(Mathf.InverseLerp(stopDistance, carFollowGap, ahead)));

            // carrefour : s'arrete au feu rouge/orange, sinon cede si l'intersection
            // est deja prise par une autre voiture (priorite / stop).
            if (nearInter)
            {
                if (!City.TrafficSignal.CanGo(gridDir)) desired = 0f;      // feu non vert
                else if (!TryReserve(upcoming)) desired = 0f;             // priorite
            }

            return desired;
        }

        // ---- helpers grille ----
        private bool IsRoad(Vector2Int c) => cityAuth.Grid.At(c.x, c.y) == City.CellType.Road;

        // Carrefour : cellule route traversee par les DEUX axes (on peut tourner).
        private bool IsIntersection(Vector2Int c)
        {
            if (!IsRoad(c)) return false;
            bool ns = IsRoad(c + new Vector2Int(0, 1)) || IsRoad(c + new Vector2Int(0, -1));
            bool ew = IsRoad(c + new Vector2Int(1, 0)) || IsRoad(c + new Vector2Int(-1, 0));
            return ns && ew;
        }

        // Grille -> monde : +x = +X, +y = -Z (cf. CityGrid.CellToWorld).
        private Vector3 DirToWorld(Vector2Int d) => new Vector3(d.x, 0f, -d.y).normalized;
        private Vector3 CellCenterWorld(Vector2Int c) =>
            cityAuth.transform.TransformPoint(cityAuth.Grid.CellToWorld(c.x, c.y));
        // Decalage vers la voie de DROITE (conduite a droite) pour une direction donnee.
        private Vector3 RightLaneOffset(Vector2Int d) =>
            Vector3.Cross(Vector3.up, DirToWorld(d)) * (cityAuth.CellSize * laneFrac);
        private Vector3 WaypointFor(Vector2Int c, Vector2Int d) =>
            CellCenterWorld(c + d) + RightLaneOffset(d);

        // Direction initiale : parmi les voisins route, celle la plus alignee au cap actuel.
        private Vector2Int SnapDir(Vector2Int c)
        {
            Vector3 fwd = transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude > 1e-4f) fwd.Normalize();
            Vector2Int best = GridDirs[0]; float bestDot = -2f;
            foreach (var d in GridDirs)
            {
                if (!IsRoad(c + d)) continue;
                float dot = Vector3.Dot(fwd, DirToWorld(d));
                if (dot > bestDot) { bestDot = dot; best = d; }
            }
            return best;
        }

        // Choix a un carrefour : tout droit privilegie (straightBias), sinon un virage
        // valide au hasard, jamais de demi-tour sauf cul-de-sac.
        private Vector2Int ChooseDir(Vector2Int c, Vector2Int cur)
        {
            bool straightOk = IsRoad(c + cur);
            Vector2Int back = new Vector2Int(-cur.x, -cur.y);
            var turns = new List<Vector2Int>();
            foreach (var d in GridDirs)
            {
                if (d == cur || d == back) continue;
                if (IsRoad(c + d)) turns.Add(d);
            }
            if (straightOk && (turns.Count == 0 || Random.value < straightBias)) return cur;
            if (turns.Count > 0) return turns[Random.Range(0, turns.Count)];
            if (straightOk) return cur;
            return IsRoad(c + back) ? back : cur; // cul-de-sac : demi-tour
        }

        private bool NearestRoad(Vector2Int from, out Vector2Int found)
        {
            for (int r = 1; r <= 2; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        var c = new Vector2Int(from.x + dx, from.y + dy);
                        if (cityAuth.Grid.At(c.x, c.y) == City.CellType.Road) { found = c; return true; }
                    }
            found = from; return false;
        }

        // Distance a la voiture qui ROULE devant, DANS MA VOIE (filtre lateral), sinon
        // carFollowGap*1.5. On ignore les voitures garees (decor au trottoir, hors voie)
        // et celles des voies transverses/opposees.
        private bool IsCirculating => driver != null || autoDrive;
        private float CarAhead()
        {
            float best = carFollowGap * 1.5f;
            Vector3 fwd = transform.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            foreach (var c in Cars)
            {
                if (c == this || c == null || !c.IsCirculating) continue;
                Vector3 to = c.transform.position - transform.position; to.y = 0f;
                float lon = Vector3.Dot(to, fwd);
                if (lon <= 0.5f || lon >= best) continue;         // devant, dans la portee
                if (Mathf.Abs(Vector3.Dot(to, right)) > 1.6f) continue; // dans ma voie
                best = lon;
            }
            return best;
        }

        // ---- priorite aux intersections (une voiture a la fois) ----
        private bool TryReserve(Vector2Int inter)
        {
            if (reservedInter == inter) return true;
            if (InterOwner.TryGetValue(inter, out var owner) && owner != null && owner != this) return false;
            ReleaseInter();
            InterOwner[inter] = this; reservedInter = inter; return true;
        }

        private void ReleaseInter()
        {
            if (reservedInter == NoCell) return;
            if (InterOwner.TryGetValue(reservedInter, out var o) && o == this) InterOwner.Remove(reservedInter);
            reservedInter = NoCell;
        }

        // Renvoie la distance au premier obstacle (feelerLength si rien), en ignorant soi-meme.
        private float CastFeeler(Vector3 origin, float angleDeg)
        {
            Vector3 dir = Quaternion.Euler(0f, angleDeg, 0f) * transform.forward;
            if (Physics.Raycast(origin, dir, out RaycastHit hit, feelerLength, obstacleMask, QueryTriggerInteraction.Ignore))
            {
                // ignore ses propres colliders
                if (!hit.collider.transform.IsChildOf(transform))
                    return hit.distance;
            }
            return feelerLength;
        }

        private void SpinWheels(float dt)
        {
            if (wheels == null || wheels.Length == 0) return;
            float deg = (speed * dt) / (2f * Mathf.PI * wheelRadius) * 360f;
            // roues essieu le long du X voiture (localRotation Z=90) -> axe de roulis = local up.
            // signe negatif : roulis vers l'avant quand la voiture avance (si inverse, flip le signe).
            foreach (var w in wheels)
                if (w) w.Rotate(Vector3.up, -deg, Space.Self);
        }

        private void OnDrawGizmosSelected()
        {
            Vector3 origin = transform.position + Vector3.up * rayHeight + transform.forward * 1.2f;
            Gizmos.color = Color.yellow;
            foreach (float a in new[] { -sideAngle, 0f, sideAngle })
            {
                Vector3 dir = Quaternion.Euler(0f, a, 0f) * transform.forward;
                Gizmos.DrawLine(origin, origin + dir * feelerLength);
            }
        }
    }
}
