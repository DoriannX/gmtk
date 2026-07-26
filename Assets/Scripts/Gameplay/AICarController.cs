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
        [SerializeField] private float stopDistance = 2.2f;   // en dessous : arret complet

        [Header("Circulation (reseau routier)")]
        [Tooltip("Decalage voie de droite depuis l'axe de la rue, en fraction de la DEMI-CHAUSSEE " +
                 "(RoadNetwork.RoadwayHalfWidth). 0.5 = milieu de la voie de droite.")]
        [SerializeField] private float laneFrac = 0.5f;
        [SerializeField] private float lookAhead = 5f;         // distance du point de voie vise devant
        [SerializeField] private float rideHeight = 0.12f;     // hauteur de la caisse au-dessus de l'axe
        [SerializeField] private float arriveDist = 2.2f;      // marge d'approche d'un carrefour
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
        // La voiture suit les SPLINES du RoadNetwork : segment courant, sens de parcours et
        // abscisse curviligne. Elle vise un point de voie quelques metres devant, et choisit
        // sa suite dans le graphe a chaque noeud. Les feux et la priorite se greffent
        // par-dessus. L'abscisse avance a l'estime (distance parcourue) : la voiture roule sur
        // son cap, pas sur la spline, et c'est le point de voie qui la ramene dans l'axe.
        private City.RoadNetwork net;
        private int seg = -1;               // segment courant
        private int segDir = 1;             // +1 = du noeud a vers le noeud b
        private float segPos;               // abscisse curviligne sur le segment (m)
        private Vector3 waypoint;           // cible monde courante (voie de droite)
        private bool onRoad;                // a accroche le reseau au demarrage
        private float roadY, roadPitch;     // altitude et pente de la voie sous la voiture
        private float curRoadPitch;         // pente lissee, appliquee a la caisse
        private int reservedNode = -1;      // carrefour reserve (priorite)
        // Qui occupe quel carrefour (une seule voiture a la fois -> priorite/stop).
        private static readonly Dictionary<int, AICarController> InterOwner = new();
        private static readonly List<int> ScratchTurns = new();
        private static City.RoadNetwork[] nets;
        private Transform playerT;          // camion du joueur : obstacle a part entiere

        // Reset des statics a chaque entree en play (Reload Domain peut etre off ->
        // sinon Cars/InterOwner gardent des entrees fantomes de la session precedente).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { Cars.Clear(); InterOwner.Clear(); nets = null; }

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

            net = FirstNetwork();
            var playerGo = GameObject.FindGameObjectWithTag("Player");
            if (playerGo != null) playerT = playerGo.transform;
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
                if (!onRoad) InitRoad();            // (re)accroche le reseau au besoin
                desired = onRoad ? DriveRoad(dt) : 0f;
            }
            else { desired = 0f; blocked = false; roadPitch = 0f; } // garee / en plein choc : ne roule pas
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
                // La caisse se couche dans la pente de la voie : le cap suit alors la rampe et
                // l'avance se fait DANS la pente au lieu de traverser le bitume.
                curRoadPitch = Mathf.LerpAngle(curRoadPitch, roadPitch, 1f - Mathf.Exp(-6f * dt));
                transform.rotation = Quaternion.Euler(curRoadPitch, yaw, 0f);
            }

            // avance sur le cap courant
            transform.position += transform.forward * (speed * dt);

            // recul du choc (recoilDir porte deja la vitesse)
            if (recoilLeft > 0f) { recoilLeft -= dt; transform.position += recoilDir * dt; }

            // Recale l'altitude sur la voie : l'avance sur le cap ne suit pas exactement la
            // spline (virages, changements de pente), et la derive s'accumulerait.
            if (driving && onRoad)
            {
                Vector3 p = transform.position;
                p.y = Mathf.Lerp(p.y, roadY + rideHeight, 1f - Mathf.Exp(-8f * dt));
                transform.position = p;
            }

            SpinWheels(dt);
            SteerFrontWheels(dt);
            UpdateJuice(dt);
        }

        // Detecte le contact avec une autre voiture et declenche la reaction. Deux passes :
        // l'AABB pour degrossir, puis le contact reel entre boites ORIENTEES.
        private void CheckBump()
        {
            if (box == null || bumpCooldown > 0f) return;
            var b = box.bounds;
            foreach (var c in Cars)
            {
                if (c == this || c.box == null || c.bumpCooldown > 0f) continue;
                if (!b.Intersects(c.box.bounds)) continue;
                if (!Touching(box, c.box)) continue;
                Bump(c); return;
            }
        }

        // Contact reel entre deux boites orientees. bounds.Intersects ne compare que des AABB :
        // un vehicule long en diagonale gonfle la sienne de plusieurs metres, et le trafic se
        // declenchait des chocs a vide en se croisant sur des voies opposees.
        private static bool Touching(BoxCollider a, BoxCollider b) =>
            Physics.ComputePenetration(a, a.transform.position, a.transform.rotation,
                                       b, b.transform.position, b.transform.rotation,
                                       out _, out _);

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

        // Accroche la voiture au reseau : segment le plus proche + sens aligne sur le cap
        // actuel. Appele a l'entree d'un conducteur / autoDrive, et en rattrapage apres un
        // knockback. ProbeRoad fait la projection : `t` est le parametre normalise sur la
        // spline, dont on tire l'abscisse curviligne.
        private void InitRoad()
        {
            onRoad = false;
            net = net != null ? net : FirstNetwork();
            if (net == null || net.SegmentCount == 0) return;
            if (!net.ProbeRoad(transform.position, 60f, out var probe) || !probe.valid) return;

            if (probe.segment >= 0)
            {
                seg = probe.segment;
                segPos = probe.t * net.SegmentLength(seg);
            }
            else
            {
                // Pile sur un carrefour : on repart par une de ses branches, au hasard.
                var nb = net.NodeNeighbours(probe.node);
                if (nb.Count == 0) return;
                seg = net.SegmentBetween(probe.node, nb[Random.Range(0, nb.Count)]);
                if (seg < 0) return;
                segPos = net.SegmentAt(seg).a == probe.node ? 0f : net.SegmentLength(seg);
            }

            // Sens de parcours : celui qui colle le mieux au cap actuel.
            segDir = Vector3.Dot(transform.forward, TravelDirAt(seg, segPos, 1)) < 0f ? -1 : 1;
            waypoint = LanePoint(seg, segPos + lookAhead * segDir, segDir);
            roadY = transform.position.y;
            onRoad = true;
        }

        // Suit la route : vise le point de voie `lookAhead` metres devant, avance l'abscisse
        // de la distance parcourue, choisit sa suite a chaque noeud. Renvoie la vitesse voulue
        // (croisiere, ralentie en virage, coupee derriere une voiture ou pour ceder).
        private float DriveRoad(float dt)
        {
            // Re-accroche si projetee loin de sa voie (knockback, explosion voisine).
            Vector3 off = transform.position - LanePoint(seg, segPos, segDir); off.y = 0f;
            if (off.sqrMagnitude > 64f)
            {
                InitRoad();
                if (!onRoad) return 0f;
            }

            segPos += speed * dt * segDir;

            // Franchissement de noeud : on reporte le depassement sur le segment suivant. La
            // boucle (et son garde-fou) couvre le cas d'un segment plus court qu'un pas.
            float len = net.SegmentLength(seg);
            for (int guard = 0; (segPos > len || segPos < 0f) && guard < 4; guard++)
            {
                int node = segDir > 0 ? net.SegmentAt(seg).b : net.SegmentAt(seg).a;
                float over = segPos > len ? segPos - len : -segPos;
                if (!StepThroughNode(node)) { onRoad = false; return 0f; }
                len = net.SegmentLength(seg);
                segPos = segDir > 0 ? over : len - over;
            }

            waypoint = LanePoint(seg, segPos + lookAhead * segDir, segDir);
            Vector3 toWp = waypoint - transform.position; toWp.y = 0f;
            if (toWp.sqrMagnitude > 0.01f)
                targetHeading = Quaternion.LookRotation(toWp).eulerAngles.y;

            // Assiette : altitude de la voie sous la voiture et pente locale. Le reseau monte
            // a +18 et descend a -15 dans cette ville -- sans ca les voitures decollent des
            // rampes et s'enfoncent dans les descentes.
            Vector3 here = LanePoint(seg, segPos, segDir);
            roadY = here.y;
            Vector3 back = LanePoint(seg, segPos - 2f * segDir, segDir);
            Vector3 front = LanePoint(seg, segPos + 2f * segDir, segDir);
            roadPitch = -Mathf.Atan2(front.y - back.y, 4f) * Mathf.Rad2Deg;

            float desired = myCruise;
            int nextNode = segDir > 0 ? net.SegmentAt(seg).b : net.SegmentAt(seg).a;
            float toNode = segDir > 0 ? len - segPos : segPos;
            // Un noeud de degre 2 n'est qu'un raccord de splines : ni feu ni priorite, sinon
            // les voitures pileraient au milieu d'une ligne droite.
            bool nearInter = net.NodeDegree(nextNode) >= 3
                             && toNode < net.JunctionRadius(nextNode) + arriveDist * 2.2f;

            // ralentit a l'approche d'un carrefour (virage / prudence)
            if (nearInter) desired *= turnSlow;

            // Cul-de-sac : le demi-tour se joue sur place, au bout de la voie. A pleine
            // vitesse la voiture depasse le bout de rue de plusieurs metres avant d'avoir
            // pivote (mesure : jusqu'a 8 m hors emprise) -- on ferme les gaz en approche.
            //
            // Le plancher n'est PAS cosmetique : le demi-tour se declenche quand l'abscisse
            // DEPASSE le bout du segment, et l'abscisse n'avance qu'a la vitesse. Tomber a
            // zero, c'est ne jamais atteindre le bout -- mesure avant plancher : 25 voitures
            // sur 29 garees a vie en bout de rue.
            if (net.NodeDegree(nextNode) <= 1)
                desired *= Mathf.Max(0.25f, Mathf.Clamp01(toNode / (arriveDist * 4f)));

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
                if (!City.TrafficSignal.CanGo(AxisOf(TravelDirAt(seg, segPos, segDir)))) desired = 0f;
                else if (!TryReserve(nextNode)) desired = 0f;
            }

            return desired;
        }

        // ---- helpers reseau ----

        // Decalage vers la voie de DROITE (conduite a droite), en metres.
        private float LaneOffset => net.RoadwayHalfWidth * laneFrac;

        // Point de la voie de droite a l'abscisse `s` du segment `k` parcouru dans le sens `d`.
        private Vector3 LanePoint(int k, float s, int d)
        {
            float len = net.SegmentLength(k);
            if (!net.SampleSegment(k, Mathf.Clamp(s, 0f, len), out Vector3 axis, out Vector3 tan, out _))
                return transform.position;
            return axis + Vector3.Cross(Vector3.up, tan * d) * LaneOffset;
        }

        // Sens de circulation (a plat, normalise) a l'abscisse `s`.
        private Vector3 TravelDirAt(int k, float s, int d)
        {
            float len = net.SegmentLength(k);
            if (!net.SampleSegment(k, Mathf.Clamp(s, 0f, len), out _, out Vector3 tan, out _))
                return transform.forward;
            return tan * d;
        }

        // TrafficSignal raisonne en axes de grille : composante dominante = axe emprunte.
        private static Vector2Int AxisOf(Vector3 travel) =>
            Mathf.Abs(travel.x) >= Mathf.Abs(travel.z) ? new Vector2Int(1, 0) : new Vector2Int(0, 1);

        // Choix de la suite au noeud `node` : tout droit privilegie (straightBias), sinon un
        // virage au hasard, demi-tour seulement en cul-de-sac. Met a jour seg/segDir.
        private bool StepThroughNode(int node)
        {
            var nb = net.NodeNeighbours(node);
            if (nb.Count == 0) return false;
            int from = segDir > 0 ? net.SegmentAt(seg).a : net.SegmentAt(seg).b;
            Vector3 inDir = TravelDirAt(seg, segDir > 0 ? net.SegmentLength(seg) : 0f, segDir);
            Vector3 nodePos = net.NodeWorld(node);

            ScratchTurns.Clear();
            int straight = -1; float bestDot = -2f;
            foreach (int n in nb)
            {
                if (n == from) continue;                 // demi-tour : reserve au cul-de-sac
                int k = net.SegmentBetween(node, n);
                if (k < 0) continue;
                ScratchTurns.Add(k);
                Vector3 outDir = net.NodeWorld(n) - nodePos; outDir.y = 0f;
                if (outDir.sqrMagnitude < 1e-4f) continue;
                float dot = Vector3.Dot(inDir, outDir.normalized);
                if (dot > bestDot) { bestDot = dot; straight = k; }
            }

            int pick;
            if (ScratchTurns.Count == 0) pick = net.SegmentBetween(node, from);   // cul-de-sac
            else if (straight >= 0 && (ScratchTurns.Count == 1 || Random.value < straightBias)) pick = straight;
            else pick = ScratchTurns[Random.Range(0, ScratchTurns.Count)];
            if (pick < 0) return false;

            seg = pick;
            segDir = net.SegmentAt(pick).a == node ? 1 : -1;
            ReleaseInter();   // le carrefour est derriere nous
            return true;
        }

        // ponytail: premier reseau non vide de la scene. A raffiner si un jour la ville en
        // porte plusieurs disjoints -- il faudrait alors prendre le plus proche.
        private static City.RoadNetwork FirstNetwork()
        {
            if (nets == null || nets.Length == 0 || nets[0] == null)
                nets = FindObjectsByType<City.RoadNetwork>(FindObjectsSortMode.None);
            foreach (var n in nets)
                if (n != null && n.SegmentCount > 0) return n;
            return null;
        }

        // Distance au vehicule qui ROULE devant, DANS MA VOIE (filtre lateral), sinon
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
                Gap(c.transform.position, fwd, right, ref best);
            }
            // Le camion du joueur n'est pas dans Cars : sans lui, le trafic lui rentre dedans
            // sans jamais lever le pied.
            if (playerT != null) Gap(playerT.position, fwd, right, ref best);
            return best;
        }

        // Retient `target` comme obstacle devant si elle est dans ma voie et plus proche que
        // le meilleur candidat courant. Le filtre VERTICAL n'est pas cosmetique : le reseau
        // est etage (ponts, rampes), et sans lui une voiture pile derriere une autre qui passe
        // 10 m au-dessus d'elle.
        private void Gap(Vector3 target, Vector3 fwd, Vector3 right, ref float best)
        {
            Vector3 to = target - transform.position;
            if (Mathf.Abs(to.y) > 3f) return;                       // autre etage du reseau
            to.y = 0f;
            float lon = Vector3.Dot(to, fwd);
            if (lon <= 0.5f || lon >= best) return;                 // devant, dans la portee
            if (Mathf.Abs(Vector3.Dot(to, right)) > 1.6f) return;   // dans ma voie
            best = lon;
        }

        // ---- priorite aux carrefours (une voiture a la fois) ----
        private bool TryReserve(int node)
        {
            if (reservedNode == node) return true;
            if (InterOwner.TryGetValue(node, out var owner) && owner != null && owner != this) return false;
            ReleaseInter();
            InterOwner[node] = this; reservedNode = node; return true;
        }

        private void ReleaseInter()
        {
            if (reservedNode < 0) return;
            if (InterOwner.TryGetValue(reservedNode, out var o) && o == this) InterOwner.Remove(reservedNode);
            reservedNode = -1;
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

        // Voie suivie et cible visee : le seul moyen de voir si une voiture s'est decrochee
        // de sa spline sans lancer le jeu.
        private void OnDrawGizmosSelected()
        {
            if (!onRoad || net == null) return;
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(transform.position, waypoint);
            Gizmos.DrawWireSphere(waypoint, 0.4f);
        }
    }
}
