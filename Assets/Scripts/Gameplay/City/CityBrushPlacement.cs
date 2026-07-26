using System.Collections.Generic;
using UnityEngine;

namespace Gameplay.City
{
    // PLAN DE VILLE, sans aucun GameObject.
    //
    // Le principe : il n'y a pas de "mode de pose". Il y a un CHAMP de placements candidats,
    // entierement determine par la graine et par le reseau routier. Le pinceau ne choisit pas
    // comment poser, il revele les candidats qui tombent sous son disque.
    //
    // Deux sources, complementaires et qui ne se recouvrent jamais :
    //   - les VOIES, calees sur chaque route (voie 0 = front de rue, voie 1+ = arriere), qui
    //     occupent la bande [bord de chaussee, LaneZone] ;
    //   - la GRILLE de coeur d'ilot, qui remplit tout le reste et s'interdit cette bande.
    // Aucune route a portee -> il ne reste que la grille, et ca marche pareil.
    //
    // Consequence utile : les candidats etant deterministes, repasser le pinceau au meme endroit
    // ne pose rien de plus, et deux traits qui se recouvrent ne se marchent pas dessus. Le rejet
    // de chevauchement contre ce qui est deja en scene suffit a rendre la peinture idempotente.
    //
    // Tout est statique et pur : le geste souris n'etant pas testable automatiquement, c'est ici
    // que vit la logique, et les menus Tools/Ville/Test l'appellent directement.
    public static class CityBrushPlacement
    {
        public delegate bool GroundProbe(Vector3 approx, out Vector3 pos, out Vector3 normal);
        public delegate bool RoadProbeFn(Vector3 world, float maxDist, out RoadNetwork.RoadProbe probe);

        public struct Placement
        {
            public CityPalette.Entry entry;
            public Vector3 position;
            public Quaternion rotation;
            public float scale;
            public Bounds footprintXZ;   // emprise au sol, deja tournee, pour le rejet
            // Enfoncement sous le sol, garde a part : le candidat est calcule sans savoir a
            // quelle hauteur est le terrain, c'est le pinceau qui pose y = sol - sink au moment
            // de l'instanciation. Melanger les deux perdait l'enfoncement.
            public float sink;
        }

        // ---------------------------------------------------------------- aleatoire

        // Pseudo-aleatoire deterministe [0,1) depuis un hash + un sel. Copie des 5 lignes de
        // CityBuilder.Rand (prive la-bas) : dupliquer vaut mieux qu'ouvrir CityBuilder, dont on
        // veut garder le diff a zero.
        public static float Rand(uint hash, int salt)
        {
            uint v = (hash ^ ((uint)salt * 2654435761u)) * 40503u + 12345u;
            v ^= v >> 13; v *= 1274126177u; v ^= v >> 16;
            return (v % 100000u) / 100000f;
        }

        private static float Signed(uint hash, int salt) => Rand(hash, salt) * 2f - 1f;

        public static uint Hash(int seed, int a, int b, int c)
        {
            unchecked
            {
                uint h = (uint)seed * 2166136261u;
                h = (h ^ (uint)a) * 16777619u;
                h = (h ^ (uint)b) * 16777619u;
                h = (h ^ (uint)c) * 16777619u;
                return h;
            }
        }

        // ---------------------------------------------------------------- chevauchement

        // Emprise au sol d'un batiment tourne, en AABB. Les jitters de yaw sont petits (~5 deg),
        // l'AABB de la boite tournee est une majoration honnete et evite un test SAT.
        public static Bounds Footprint(Vector3 pos, float yawDeg, float w, float d)
        {
            float r = yawDeg * Mathf.Deg2Rad;
            float c = Mathf.Abs(Mathf.Cos(r)), s = Mathf.Abs(Mathf.Sin(r));
            var b = new Bounds(new Vector3(pos.x, 0f, pos.z), Vector3.zero);
            b.size = new Vector3(w * c + d * s, 0f, w * s + d * c);
            return b;
        }

        // Emprise d'un objet DEJA pose, reconstruite exactement comme celle d'un candidat.
        //
        // Point non evident : il faut repasser par la palette (footprint x echelle x yaw) et NON
        // par les bounds des renderers. Les deux mesures different de quelques centimetres --
        // debords de toit, antennes -- et ca suffit a ce qu'un candidat rejete a la premiere
        // passe soit accepte a la seconde. La peinture derivait alors d'un objet a chaque
        // repassage au lieu de ne rien faire.
        public static Bounds PlacedBounds(CityPalette palette, Transform t, System.Func<GameObject, GameObject> source)
        {
            if (palette != null && source != null)
            {
                GameObject src = source(t.gameObject);
                var e = src != null ? palette.Find(src) : null;
                if (e != null)
                {
                    float sc = t.localScale.x;
                    Bounds box = Footprint(t.position, t.eulerAngles.y,
                                           Mathf.Max(0.2f, e.footprint.x * sc),
                                           Mathf.Max(0.2f, e.footprint.z * sc));
                    // Marge de 3 %. La rotation posee vaut Euler(tangage, yaw, roulis) et la
                    // decomposition eulerAngles ne rend pas EXACTEMENT le yaw d'origine des que
                    // le tangage est non nul -- assez pour faire basculer un cas limite de
                    // chevauchement d'une passe a l'autre. Gonfler stabilise ces cas-la.
                    //
                    // Ca ne rend PAS la peinture strictement idempotente, et c'est voulu : le
                    // multi-essai de la grille s'arrete a la premiere position qui rentre, donc
                    // repasser explore des essais jamais atteints et bouche encore quelques
                    // trous (~1 % en plus) avant de se stabiliser. C'est le seul moyen de
                    // densifier a la main une zone deja peinte.
                    box.Expand(new Vector3(box.size.x * 0.03f, 0f, box.size.z * 0.03f));
                    return box;
                }
            }

            // Repli pour tout ce qui n'est pas de la palette (pose a la main, autre lot).
            var rs = t.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(new Vector3(t.position.x, 0f, t.position.z), Vector3.zero);
            Bounds b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return new Bounds(new Vector3(b.center.x, 0f, b.center.z),
                              new Vector3(b.size.x, 0f, b.size.z));
        }

        // Rejet si l'intersection XZ depasse 20 % de la PLUS PETITE des deux emprises. Meme
        // critere que CityBuilder.OverlapsPlaced : un leger chevauchement est souhaitable
        // (batiments mitoyens), un empilement ne l'est pas.
        public static bool Overlaps(Bounds b, List<Bounds> placed)
        {
            float area = b.size.x * b.size.z;
            if (area <= 0f) return false;

            foreach (var p in placed)
            {
                float ox = Mathf.Min(b.max.x, p.max.x) - Mathf.Max(b.min.x, p.min.x);
                if (ox <= 0f) continue;
                float oz = Mathf.Min(b.max.z, p.max.z) - Mathf.Max(b.min.z, p.min.z);
                if (oz <= 0f) continue;

                float other = p.size.x * p.size.z;
                float small = Mathf.Min(area, other);
                if (small > 0f && (ox * oz) / small > 0.2f) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- voies

        // Polyligne DECALEE le long de laquelle les facades d'une voie s'alignent.
        //
        // Pourquoi ne pas simplement parcourir l'axe : dans un virage, l'abscisse curviligne du
        // trottoir n'est pas celle de l'axe. Marcher l'axe fait chevaucher les batiments a
        // l'interieur de la courbe et beer a l'exterieur. On construit donc la ligne decalee, et
        // c'est SON abscisse qu'on parcourt.
        public class RowPath
        {
            public Vector3[] pts;
            public Vector3[] nrm;
            public float[] cum;
            public float total;
            public int segment;
            public int side;
            public RoadNetwork net;
        }

        const float RowSampleStep = 0.5f;

        // Aucun reglage de pinceau n'intervient ici : la polyligne ne depend que de la route et du
        // decalage demande. C'est ce qui permet a CityPropsSeeder, qui n'a pas de CityBrush sous
        // la main, de reutiliser exactement la meme geometrie de trottoir que le front de rue.
        public static RowPath BuildRowPath(RoadNetwork net, int segment, int side, float offset)
        {
            if (net == null || segment < 0) return null;
            float len = net.SegmentLength(segment);
            if (len <= 0.01f) return null;

            int n = Mathf.Clamp(Mathf.CeilToInt(len / RowSampleStep), 2, 4096);
            var path = new RowPath
            {
                pts = new Vector3[n + 1],
                nrm = new Vector3[n + 1],
                cum = new float[n + 1],
                segment = segment,
                side = side,
                net = net,
            };

            for (int i = 0; i <= n; i++)
            {
                if (!net.SampleSegment(segment, len * i / n, out Vector3 p, out Vector3 t, out _))
                    return null;
                Vector3 nr = Vector3.Cross(Vector3.up, t).normalized * side;
                path.nrm[i] = nr;
                path.pts[i] = p + nr * offset;
                path.cum[i] = i == 0 ? 0f : path.cum[i - 1] + Vector3.Distance(path.pts[i - 1], path.pts[i]);
            }
            path.total = path.cum[n];
            return path;
        }

        public static void SampleRowPath(RowPath path, float s, out Vector3 pos, out Vector3 nrm)
        {
            s = Mathf.Clamp(s, 0f, path.total);
            int i = 1;
            while (i < path.cum.Length - 1 && path.cum[i] < s) i++;

            float span = path.cum[i] - path.cum[i - 1];
            float u = span > 1e-4f ? (s - path.cum[i - 1]) / span : 0f;
            pos = Vector3.Lerp(path.pts[i - 1], path.pts[i], u);
            nrm = Vector3.Slerp(path.nrm[i - 1], path.nrm[i], u).normalized;
        }

        // TOUS les candidats "voie" du reseau : chaque segment, chaque cote, chaque voie en
        // profondeur. C'est le plan de ville cote rues, calcule d'un coup et reutilise pendant
        // tout le trait. Quelques centaines d'entrees sur un reseau normal -- inutile de cacher
        // plus finement, on le rebatit au debut de chaque trait pour ramasser les reglages.
        public static void BuildLaneCandidates(CityBrush b, RoadNetwork[] nets, List<Placement> outp)
        {
            if (b.palette == null || nets == null) return;
            int budget = b.maxPerStroke * 8;   // filet contre un reseau demesure

            for (int ni = 0; ni < nets.Length; ni++)
            {
                var net = nets[ni];
                if (net == null) continue;

                for (int k = 0; k < net.SegmentCount; k++)
                {
                    for (int s = 0; s < 2; s++)
                    {
                        int side = s == 0 ? 1 : -1;
                        for (int lane = 0; lane < b.lanes; lane++)
                        {
                            float offset = net.RoadHalfWidth + b.setback + lane * (b.laneDepth + b.laneGap);
                            var path = BuildRowPath(net, k, side, offset);
                            if (path == null) continue;
                            WalkLane(b, path, ni, lane, outp);
                            if (outp.Count >= budget) return;
                        }
                    }
                }
            }
        }

        // Parcourt une voie et pose des batiments bout a bout. Les largeurs variees, les espaces
        // irreguliers et les trous occasionnels sont ce qui rend la rangee habitee plutot que
        // tiree au cordeau -- bien plus que le bruit de rotation, qu'on garde minuscule.
        private static void WalkLane(CityBrush b, RowPath path, int netIndex, int lane, List<Placement> outp)
        {
            float cursor = 0f;
            int index = 0;

            while (index < 512)
            {
                uint h = 0;
                CityPalette.Entry e = null;
                float sc = 1f, w = 0f, d = 0f;

                // Retire jusqu'a 4 fois quand le batiment ne rentre pas dans ce qui reste de la
                // voie : sans ca on abandonnait la voie entiere des le premier trop-gros, et la
                // queue de chaque segment restait vide -- un trou par bout de rue.
                bool rentre = false;
                for (int attempt = 0; attempt < 4 && !rentre; attempt++)
                {
                    h = Hash(b.seed, netIndex * 4096 + path.segment, path.side * 16 + lane, index * 8 + attempt);
                    e = b.palette.Pick(BrushLayer.Batiments, Rand(h, 0));
                    if (e == null) return;

                    sc = 1f + Signed(h, 1) * Mathf.Min(0.9f, b.scaleJitter + e.scaleJitter);
                    w = Mathf.Max(0.5f, e.footprint.x * sc);
                    d = Mathf.Max(0.5f, e.footprint.z * sc);
                    rentre = cursor + w <= path.total;
                }
                if (!rentre) return;

                SampleRowPath(path, cursor + w * 0.5f, out Vector3 p, out Vector3 nr);

                // Le decalage de la polyligne pose le BORD de facade ; reste la demi-profondeur a
                // pousser, plus le bruit qui empeche l'alignement au cordeau.
                Vector3 pos = p + nr * (d * 0.5f + Signed(h, 2) * 0.4f);

                // La FACADE regarde la route, dans la direction -nr. On retranche le facadeYaw de
                // l'entree : chaque prefab regarde la rue quel que soit son modelage.
                float yaw = Mathf.Atan2(-nr.x, -nr.z) * Mathf.Rad2Deg
                          - e.facadeYaw + Signed(h, 3) * b.yawJitter;

                // La polyligne suit l'axe du SEGMENT ; elle ne sait rien de l'emprise des
                // croisements, qui deborde bien au-dela de la demi-chaussee. Sans ce rejet, les
                // batiments de bout de voie montent sur le carrefour.
                bool surLaRoute = path.net != null
                    && path.net.ProbeRoad(pos, 200f, out RoadNetwork.RoadProbe rp)
                    && rp.valid && rp.clearance < d * 0.5f;

                if (!surLaRoute) outp.Add(Make(b, e, pos, yaw, sc, w, d, h));

                cursor += w + b.gapMin + Rand(h, 7) * b.gapRange;
                if (Rand(h, 8) < b.holeChance) cursor += b.holeSize;
                index++;
            }
        }

        // ---------------------------------------------------------------- mobilier de rue

        // Desordre du mobilier PONCTUEL, jamais des lignes continues (voir WalkStreet).
        const float RunChance = 0.45f;       // proportion de troncons qui recoivent une ligne continue
        const float StepJitter = 0.35f;      // +-35 % sur le pas
        const float SkipChance = 0.15f;      // probabilite de sauter une place
        const float LateralJitter = 0.18f;   // metres de flottement perpendiculaire au trottoir

        // PLAN DU MOBILIER DE RUE : lampadaires, poteaux, barrieres, bancs, cales sur les
        // trottoirs de tout le reseau. Meme nature que BuildLaneCandidates -- un champ complet et
        // deterministe, calcule d'un coup -- et c'est ce qui permet aux DEUX outils de s'en
        // servir sans diverger : CityPropsSeeder l'instancie en entier d'un bouton, le pinceau
        // n'en revele que ce qui tombe sous son disque. Meme graine -> meme ville dans les deux
        // cas, et repasser le pinceau ne pose rien de plus.
        public static void BuildStreetCandidates(CityPalette palette, int seed, RoadNetwork[] nets,
                                                 int budget, List<Placement> outp)
        {
            if (palette == null || nets == null) return;

            var continus = new List<CityPalette.Entry>();
            var ponctuels = new List<CityPalette.Entry>();
            foreach (var e in palette.entries)
            {
                if (e.layer != BrushLayer.PropsRue || !e.Usable) continue;
                (e.Continu ? continus : ponctuels).Add(e);
            }
            if (continus.Count == 0 && ponctuels.Count == 0) return;

            var placed = new List<Bounds>();

            for (int ni = 0; ni < nets.Length; ni++)
            {
                var net = nets[ni];
                if (net == null) continue;

                for (int k = 0; k < net.SegmentCount; k++)
                {
                    for (int s = 0; s < 2; s++)
                    {
                        int side = s == 0 ? 1 : -1;

                        // Une polyligne PAR RECUL, et non une seule pour tout le monde : c'est ce
                        // qui range le trottoir en profondeur au lieu d'aligner lampadaires,
                        // bancs et abribus sur le meme cordeau. Le cache evite de reechantillonner
                        // le segment pour chaque prop -- les reculs distincts se comptent sur les
                        // doigts d'une main, les entrees de palette non.
                        var byInset = new Dictionary<int, RowPath>();
                        RowPath PathFor(float inset)
                        {
                            int key = Mathf.RoundToInt(inset * 100f);
                            if (byInset.TryGetValue(key, out var cached)) return cached;
                            float off = net.RoadHalfWidth - inset;
                            var built = off <= 0.1f ? null : BuildRowPath(net, k, side, off);
                            byInset[key] = built;
                            return built;
                        }

                        uint h = Hash(seed, ni * 4096 + k, side, 5501);

                        // La ligne continue passe EN PREMIER : c'est elle qui doit rester
                        // ininterrompue (une barriere trouee se lit comme un bug, et une ligne de
                        // grind trouee fait decrocher). Le mobilier ponctuel se pose ensuite et
                        // cede la place quand il tombe dessus -- perdre un lampadaire de temps en
                        // temps ne se voit pas.
                        if (continus.Count > 0 && Rand(h, 0) < RunChance)
                        {
                            var e = PickWeighted(continus, Rand(h, 1));
                            var p = e != null ? PathFor(e.inset) : null;
                            if (p != null) WalkStreet(e, p, net, 0f, h, 11, placed, outp, budget);
                        }

                        for (int i = 0; i < ponctuels.Count; i++)
                        {
                            var e = ponctuels[i];
                            var p = PathFor(e.inset);
                            if (p == null) continue;
                            // Phase propre a (segment, cote, prop) : sans elle, tous les props
                            // ponctuels d'une rue demarreraient au meme metre et s'empileraient au
                            // debut de chaque troncon.
                            float phase = Rand(h, 20 + i) * e.spacing;
                            WalkStreet(e, p, net, phase, h, 40 + i * 8, placed, outp, budget);
                        }

                        if (outp.Count >= budget) return;
                    }
                }
            }
        }

        // Parcourt un cote de segment et aligne `e` le long de la polyligne.
        //
        // Deux regimes, et la difference n'est PAS cosmetique :
        //
        //   CONTINU (barriere, rambarde) -> pas exact, aucun bruit, aucun trou. Le pas vaut la
        //   largeur de l'element : le jitterer les ferait se chevaucher ou beer, et surtout la
        //   ligne de grind se recouperait -- c'est ce qui a fait tomber une rue de 70 m a des
        //   morceaux de 2.6 m lors du premier essai.
        //
        //   PONCTUEL (lampadaire, banc, poteau) -> pas bruite, places sautees, flottement
        //   perpendiculaire. Rien ne s'enchaine, donc rien ne casse, et c'est ce qui empeche la
        //   rue de battre la mesure.
        private static void WalkStreet(CityPalette.Entry e, RowPath path, RoadNetwork net,
                                       float phase, uint h, int salt,
                                       List<Bounds> placed, List<Placement> outp, int budget)
        {
            float spacing = e.spacing;
            if (spacing < 0.05f) return;
            bool continu = e.Continu;
            float offset = net.RoadHalfWidth - e.inset;   // distance nominale du prop a SON axe

            // Les emprises de CE parcours sont mises de cote et versees dans `placed` a la fin,
            // pour que la marche ne se rejette pas elle-meme.
            //
            // Sans ca la ligne continue se troue une fois sur trois : Footprint rend l'AABB de la
            // boite TOURNEE, et en courbe deux barrieres voisines, chacune yawee differemment, ont
            // des AABB qui se recouvrent alors que les rectangles reels ne se touchent pas. Le
            // rejet en supprimait une, le trou de 2.6 m depassait le seuil de recollage des rails,
            // et la ligne de grind tombait a 4 m au lieu de faire la rue. Les elements sont poses
            // tous les `spacing` metres le long de la MEME polyligne : ils ne peuvent pas se
            // chevaucher entre eux, c'est la geometrie du parcours qui le garantit.
            var mine = new List<Bounds>();
            int index = 0;

            for (float s = phase; s <= path.total && outp.Count < budget; index++)
            {
                SampleRowPath(path, s, out Vector3 p, out Vector3 nr);
                uint ph = Hash((int)h, salt, index, 991);

                // Avance decidee ICI, avant tout rejet : la position suivante ne doit pas dependre
                // du fait que celle-ci ait abouti, sinon un carrefour saute decalerait toute la
                // suite de la rue.
                float step = spacing;
                if (!continu)
                {
                    step *= 1f + Signed(ph, 10) * StepJitter;
                    if (Rand(ph, 11) < SkipChance) step += spacing;
                    // `nr` sort du trottoir vers les batiments : le prop recule ou avance dans la
                    // profondeur au lieu de rester colle au cordeau de son recul.
                    p += nr * (Signed(ph, 12) * LateralJitter);
                }
                s += Mathf.Max(0.05f, step);

                // Deux rejets, et il faut les DEUX : la polyligne suit l'axe d'UN segment et ne
                // sait rien du reste du reseau.
                if (net.ProbeRoad(p, 80f, out RoadNetwork.RoadProbe probe) && probe.valid)
                {
                    // 1. Emprise du carrefour, qui deborde bien au-dela de la demi-chaussee :
                    //    quand le point le plus proche du reseau est un NOEUD, on est dedans.
                    if (probe.node >= 0) continue;

                    // 2. Chaussee d'une AUTRE rue. Aux abords d'un croisement la perpendiculaire
                    //    passe a portee sans que son noeud soit le plus proche, et le prop se
                    //    plantait au milieu de la voie transversale -- mesure avant correction :
                    //    des barrieres a 0.08 m d'un axe, pour une chaussee large de 2.25. Sur son
                    //    propre trottoir un prop est a `offset` de l'axe ; nettement plus pres
                    //    d'un axe quelconque, c'est qu'il en mord un autre.
                    if (probe.distance < offset - 0.5f) continue;
                }

                // AUCUNE variation d'echelle sur une ligne continue : une largeur qui varie de
                // +-12 % fait deborder une barriere sur sa voisine, le rejet en supprime une, et
                // le trou casse la ligne de grind. L'irregularite de ce lot vient des ruptures de
                // famille d'un troncon a l'autre, pas de la taille des elements.
                float sc = continu ? 1f : 1f + Signed(ph, 0) * Mathf.Min(0.9f, e.scaleJitter);
                float w = Mathf.Max(0.15f, e.footprint.x * sc);
                float d = Mathf.Max(0.15f, e.footprint.z * sc);

                // -nr regarde la chaussee : le devant du prop lui fait face, comme une facade.
                float yaw = Mathf.Atan2(-nr.x, -nr.z) * Mathf.Rad2Deg - e.facadeYaw;

                var box = Footprint(p, yaw, w, d);
                if (Overlaps(box, placed)) continue;

                // Hauteur du trottoir, pas un raycast : la polyligne est echantillonnee sur l'AXE
                // de la route, et SidewalkHeight est par definition l'elevation du trottoir
                // au-dessus de cet axe. Un raycast dependrait des colliders generes et de l'ordre
                // d'instanciation pour retrouver le meme nombre.
                outp.Add(new Placement
                {
                    entry = e,
                    position = new Vector3(p.x, p.y + net.SidewalkHeight, p.z),
                    rotation = Quaternion.Euler(0f, yaw, 0f),
                    scale = sc,
                    footprintXZ = box,
                });
                mine.Add(box);
            }

            placed.AddRange(mine);
        }

        // Tirage pondere sur une SOUS-LISTE deja filtree. CityPalette.Pick balaie toute la
        // palette par couche ; ici les deux familles de rue sont deja separees.
        private static CityPalette.Entry PickWeighted(List<CityPalette.Entry> list, float u)
        {
            float total = 0f;
            foreach (var e in list) total += e.weight;
            if (total <= 0f) return null;

            float target = Mathf.Clamp01(u) * total;
            float acc = 0f;
            CityPalette.Entry last = null;
            foreach (var e in list)
            {
                last = e;
                acc += e.weight;
                if (target < acc) return e;
            }
            return last;
        }

        // ---------------------------------------------------------------- coeur d'ilot

        // Grille deterministe, generee A LA DEMANDE sur les cases qui touchent le disque : elle
        // couvre un plan infini, donc impossible de la precalculer comme les voies. Chaque case
        // est hachee par ses coordonnees -> deux traits differents produisent le meme batiment
        // pour la meme case.
        //
        // La grille n'evite PAS une bande nominale autour des routes. C'etait la premiere
        // version et ca creusait un anneau mort de ~20 m : la bande reservee valait
        // setback + voies * (laneDepth + laneGap), alors que les voies n'occupent en vrai que la
        // PROFONDEUR DES BATIMENTS, bien moindre. Entre l'arriere de la derniere voie et le bord
        // de la bande, plus rien n'avait le droit de se poser -- et comme le plan est
        // deterministe, repasser le pinceau ne remplissait jamais ce vide.
        // Maintenant elle evite les batiments REELS : `reserved` (tout le plan des rues, meme
        // hors du disque) et `placed` (ce qui est deja en scene). Elle vient donc se serrer
        // contre l'arriere des rangees, et bouche les queues de segment que la marche de voie
        // n'atteint pas.
        //
        // `gridTries` : chaque case retente avec une autre position et un autre prefab quand le
        // premier ne rentre pas. C'est ce qui evite qu'une collision creuse un trou definitif.
        public static void GridCandidates(CityBrush b, Vector3 center, float radius,
                                          RoadProbeFn road, List<Bounds> reserved,
                                          List<Bounds> placed, List<Placement> outp)
        {
            if (b.palette == null || b.gridSpacing <= 0.1f) return;

            float cell = b.gridSpacing;
            int gx0 = Mathf.FloorToInt((center.x - radius) / cell);
            int gx1 = Mathf.CeilToInt((center.x + radius) / cell);
            int gz0 = Mathf.FloorToInt((center.z - radius) / cell);
            int gz1 = Mathf.CeilToInt((center.z + radius) / cell);
            float r2 = radius * radius;
            int tries = Mathf.Max(1, b.gridTries);

            for (int gx = gx0; gx <= gx1; gx++)
            {
                for (int gz = gz0; gz <= gz1; gz++)
                {
                    uint cellHash = Hash(b.seed, gx, gz, 7717);
                    if (Rand(cellHash, 0) < b.gridHoleChance) continue;   // cour, parking, respiration

                    for (int attempt = 0; attempt < tries; attempt++)
                    {
                        uint h = Hash(b.seed, gx * 31 + attempt, gz * 17 - attempt, 7717);

                        var pos = new Vector3(
                            (gx + 0.5f + Signed(h, 1) * b.gridJitter) * cell, center.y,
                            (gz + 0.5f + Signed(h, 2) * b.gridJitter) * cell);

                        float dx = pos.x - center.x, dz = pos.z - center.z;
                        if (dx * dx + dz * dz > r2) break;   // case hors du disque, inutile de retenter

                        var e = b.palette.Pick(BrushLayer.Batiments, Rand(h, 3));
                        if (e == null) return;

                        float sc = 1f + Signed(h, 4) * Mathf.Min(0.9f, b.scaleJitter + e.scaleJitter);
                        float w = Mathf.Max(0.5f, e.footprint.x * sc);
                        float d = Mathf.Max(0.5f, e.footprint.z * sc);

                        // Orientation : alignee sur la route la plus proche tant qu'elle est a
                        // portee -> les coeurs d'ilot restent paralleles aux rues et le quartier
                        // se lit comme voulu. Au-dela, plus rien a suivre, axes du monde.
                        float yaw = Mathf.Round(Rand(h, 5) * 4f) * 90f;
                        if (road != null && road(pos, b.alignRange, out RoadNetwork.RoadProbe rp) && rp.valid)
                        {
                            // Jamais sur la chaussee ni sur un carrefour, mais on s'autorise a
                            // venir juste derriere le trottoir : c'est la que les queues de
                            // rangee laissent des trous.
                            if (rp.clearance < Mathf.Max(w, d) * 0.5f + b.setback) continue;

                            if (rp.clearance < b.laneDepth)
                            {
                                // Assez pres pour lire comme du front de rue : on tourne la
                                // facade vers la chaussee au lieu de s'aligner a 90 deg pres.
                                Vector3 toRoad = rp.point - pos;
                                toRoad.y = 0f;
                                if (toRoad.sqrMagnitude > 1e-4f)
                                    yaw = Mathf.Atan2(toRoad.x, toRoad.z) * Mathf.Rad2Deg;
                            }
                            else if (rp.segment >= 0)
                            {
                                yaw = Mathf.Atan2(rp.tangent.x, rp.tangent.z) * Mathf.Rad2Deg
                                    + Mathf.Round(Rand(h, 5) * 4f) * 90f;
                            }
                        }
                        yaw += -e.facadeYaw + Signed(h, 6) * b.yawJitter;

                        var box = Footprint(pos, yaw, w, d);
                        if (Overlaps(box, reserved) || Overlaps(box, placed)) continue;

                        outp.Add(Make(b, e, pos, yaw, sc, w, d, h));
                        placed.Add(box);
                        break;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- props

        // Saupoudrage sur ce que `ground` accepte, et RIEN d'autre : c'est la sonde qui decide du
        // domaine, pas cette fonction. Couche Props -> la sonde ne rend que le conteneur, donc on
        // seme sur les toits et les facades deja peints. Couche PropsSol -> elle rend aussi le
        // sol de la ville mais jamais la chaussee.
        // Bruit de cap des props. Bien plus large que le yawJitter des batiments (5 deg) : une
        // facade doit rester d'aplomb sur sa rangee, un baril non. Sans ce bruit l'alignement
        // route arrondi a 90 deg fait du carrelage.
        const float PropYawJitter = 15f;

        public static void PropCandidates(CityBrush b, BrushLayer layer, Vector3 center,
                                          int strokeId, int already,
                                          GroundProbe ground, RoadProbeFn road,
                                          List<Bounds> placed, List<Placement> outp)
        {
            if (b.palette == null || ground == null) return;
            if (b.palette.CountUsable(layer) == 0) return;

            int attempts = Mathf.Clamp(
                Mathf.CeilToInt(b.propDensity * Mathf.PI * b.radius * b.radius * 3f), 1, 300);

            for (int j = 0; j < attempts; j++)
            {
                uint h = Hash(b.seed, strokeId, already * 97 + j, 4242);

                float a = Rand(h, 0) * Mathf.PI * 2f;
                // sqrt : uniforme en AIRE. Sans lui, tout se tasse au centre du disque.
                float rr = b.radius * Mathf.Sqrt(Rand(h, 1));
                var probe = new Vector3(center.x + Mathf.Cos(a) * rr, center.y, center.z + Mathf.Sin(a) * rr);
                if (!ground(probe, out Vector3 pos, out Vector3 nrm)) continue;

                var e = b.palette.Pick(layer, Rand(h, 2));
                if (e == null) return;

                float sc = 1f + Signed(h, 3) * Mathf.Min(0.9f, b.scaleJitter + e.scaleJitter);
                float w = Mathf.Max(0.2f, e.footprint.x * sc);
                float d = Mathf.Max(0.2f, e.footprint.z * sc);

                // Cap aligne sur la rue, meme regle que les coeurs d'ilot de GridCandidates : la
                // tangente de la route la plus proche tant qu'elle est a portee, les axes du
                // monde au-dela, le tout arrondi au quart de tour. Un cap uniforme dans [0,360[
                // -- ce qu'on faisait avant -- laissait les props plats (distributeur 0.51 de
                // profondeur, benne) en travers de tout, alors qu'ils ont un DOS et se rangent
                // le long de quelque chose.
                float yaw = Mathf.Round(Rand(h, 4) * 4f) * 90f;
                if (road != null && road(pos, b.alignRange, out RoadNetwork.RoadProbe rp)
                    && rp.valid && rp.segment >= 0)
                {
                    yaw = Mathf.Atan2(rp.tangent.x, rp.tangent.z) * Mathf.Rad2Deg
                        + Mathf.Round(Rand(h, 5) * 4f) * 90f;
                }
                yaw += Signed(h, 6) * PropYawJitter;

                var box = Footprint(pos, yaw, w, d);
                if (Overlaps(box, placed)) continue;

                Quaternion rot = OrientProp(layer, e, nrm, yaw, out _);
                outp.Add(new Placement
                {
                    entry = e, position = pos, rotation = rot, scale = sc, footprintXZ = box,
                });
                placed.Add(box);
            }
        }

        // ---------------------------------------------------------------- commun

        private static Placement Make(CityBrush b, CityPalette.Entry e, Vector3 pos, float yaw,
                                      float sc, float w, float d, uint h)
        {
            float tiltX = Signed(h, 4) * b.tiltJitter;
            float tiltZ = Signed(h, 5) * b.tiltJitter;
            // Enfonce de quoi ne pas laisser de jour sous le coin releve par le tangage.
            float sink = Rand(h, 6) * b.sinkMax
                       + 0.5f * Mathf.Max(w, d)
                         * Mathf.Tan(Mathf.Deg2Rad * Mathf.Max(Mathf.Abs(tiltX), Mathf.Abs(tiltZ)));

            return new Placement
            {
                entry = e,
                position = pos,
                rotation = Quaternion.Euler(tiltX, yaw, tiltZ),
                scale = sc,
                footprintXZ = Footprint(pos, yaw, w, d),
                sink = sink,
            };
        }

        // Orientation finale selon la couche. Sur la couche Batiments on reste debout : un
        // immeuble couche sur la pente d'un talus se lit comme un bug, pas comme du relief.
        public static Quaternion OrientProp(BrushLayer layer, CityPalette.Entry e, Vector3 normal,
                                            float yaw, out float tiltPad)
        {
            tiltPad = 0f;
            if (layer == BrushLayer.Batiments) return Quaternion.Euler(0f, yaw, 0f);

            PropAlign align = e.align;
            // Une facade est verticale : on bascule en Mur meme si l'entree dit Normale, sinon
            // le prop pique du nez dans le mur.
            if (Mathf.Abs(normal.y) < 0.5f) align = PropAlign.Mur;

            switch (align)
            {
                case PropAlign.Sol:
                    return Quaternion.Euler(0f, yaw, 0f);
                case PropAlign.Mur:
                    return Quaternion.LookRotation(-normal, Vector3.up);
                default:
                    tiltPad = Mathf.Tan(Mathf.Deg2Rad * Vector3.Angle(Vector3.up, normal));
                    return Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(0f, yaw, 0f);
            }
        }
    }
}
