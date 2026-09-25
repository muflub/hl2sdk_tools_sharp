/* p8a oracle: drive the reference collision library (vphysics.so, VPhysicsCollision007)
 * from a line protocol on stdin. Every float is printed as C99 hex (%a) so its
 * answers round-trip exactly.
 * Run it under LD_LIBRARY_PATH=<dir of the .so>.
 *
 *   usage: p8a-oracle <path/to/vphysics.so>   (commands on stdin, answers on stdout)
 *
 * Commands (one per line; floats accept decimal or hex-float):
 *   P n merge            then n lines "nx ny nz d": ConvexFromPlanes (outward planes)   -> "convex i"
 *   V n                  then n lines "x y z": ConvexFromVerts                           -> "convex i"
 *   B x0 y0 z0 x1 y1 z1  BBoxToConvex                                                    -> "convex i"
 *   CV i                 ConvexVolume / ConvexSurfaceArea of convex i                   -> "cvol a b"
 *   G i data             SetConvexGameData
 *   C k outer drag eps   ConvertConvexToCollideParams over the last k convexes, in order -> "collide j size n" + "hex ..."
 *   L hex                UnserializeCollide                                               -> "collide j size n"
 *   Q j                  volume, area, aabb, masscenter, ortho areas                     -> "q ..."
 *   E j dx dy dz         CollideGetExtent                                                -> "e x y z"
 *   T j sx sy sz ex ey ez hx hy hz   TraceBox with box half-extents h                    -> "t frac allsolid startsolid ex ey ez nx ny nz"
 *   S j k sx sy sz ex ey ez          TraceCollide: sweep collide k along start->end vs j  -> "t ..."
 *   W j                  CollideWrite again (hex)
 *   X                    exit
 */
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <xmmintrin.h>

typedef struct { float x, y, z; } vec;
typedef void *(*CI)(const char *, int *);

static void **vt;
static void *pc;
static void *convexes[1 << 16];
static int nconvex;
static void *collides[1 << 16];
static int ncollide;

static void hexout(const unsigned char *b, int n) {
  static const char *h = "0123456789abcdef";
  fputs("hex ", stdout);
  for (int i = 0; i < n; i++) { putchar(h[b[i] >> 4]); putchar(h[b[i] & 15]); }
  putchar('\n');
}

static int readline(char *buf, int n) { return fgets(buf, n, stdin) != NULL; }

static void writecollide(int j) {
  int n = ((int (*)(void *, void *))vt[18])(pc, collides[j]);
  unsigned char *buf = malloc(n);
  int w = ((int (*)(void *, char *, void *, int))vt[19])(pc, (char *)buf, collides[j], 0);
  printf("collide %d size %d written %d\n", j, n, w);
  hexout(buf, w);
  free(buf);
}

/* IVirtualMeshEvent over one global mesh (virtualmeshlist_t: verts*, indexCount, triangleCount,
 * vertexCount, surfacePropsIndex, pHull, indices[1024*3]). */
static struct { int nv, nt; vec v[4096]; unsigned short idx[1024 * 3]; } g_mesh;
static void mesh_get(void *self, void *ud, unsigned char *list) {
  vec *verts = g_mesh.v; int ic = g_mesh.nt * 3, tc = g_mesh.nt, vc = g_mesh.nv, sp = 0; void *hull = 0;
  memcpy(list, &verts, 8); memcpy(list + 8, &ic, 4); memcpy(list + 12, &tc, 4); memcpy(list + 16, &vc, 4);
  memcpy(list + 20, &sp, 4); memcpy(list + 24, &hull, 8); memcpy(list + 32, g_mesh.idx, (size_t)ic * 2);
}
static void mesh_bounds(void *self, void *ud, vec *mins, vec *maxs) {
  vec a = {99999, 99999, 99999}, b = {-99999, -99999, -99999};
  for (int i = 0; i < g_mesh.nv; i++) {
    vec v = g_mesh.v[i];
    if (v.x < a.x) a.x = v.x; if (v.y < a.y) a.y = v.y; if (v.z < a.z) a.z = v.z;
    if (v.x > b.x) b.x = v.x; if (v.y > b.y) b.y = v.y; if (v.z > b.z) b.z = v.z;
  }
  *mins = a; *maxs = b;
}
static void mesh_sphere(void *self, void *ud, vec *c, float r, unsigned char *out) { int z = 0; memcpy(out, &z, 4); }

static void printtrace(unsigned char *tr) {
  float *f = (float *)tr;
  printf("t %a %d %d %a %a %a %a %a %a\n", f[11], tr[54], tr[55], f[3], f[4], f[5], f[6], f[7], f[8]);
}

int main(int argc, char **argv) {
  void *h = dlopen(argv[1], RTLD_NOW);
  if (!h) { printf("error dlopen %s\n", dlerror()); return 1; }
  /* vphysics.so is linked with -ffast-math: crtfastmath's constructor ORs FTZ|DAZ (0x8040) into
   * MXCSR at dlopen. P8A_MXCSR=report prints it; P8A_MXCSR=clear turns both off (experiment). */
  {
    const char *m = getenv("P8A_MXCSR");
    if (m && !strcmp(m, "report")) fprintf(stderr, "mxcsr %08x\n", _mm_getcsr());
    if (m && !strcmp(m, "clear")) _mm_setcsr(_mm_getcsr() & ~0x8040u);
  }
  CI ci = (CI)dlsym(h, "CreateInterface");
  int rc = 0;
  pc = ci("VPhysicsCollision007", &rc);
  if (!pc) { printf("error no iface\n"); return 1; }
  vt = *(void ***)pc;
  static char line[1 << 22];
  vec zero = {0, 0, 0};
  while (readline(line, sizeof line)) {
    char *p = line;
    if (line[0] == 'P') {
      int n; float merge; sscanf(p + 1, "%d %f", &n, &merge);
      float *pl = malloc(sizeof(float) * 4 * (n + 1));
      for (int i = 0; i < n; i++) {
        readline(line, sizeof line);
        char *e = line;
        for (int k = 0; k < 4; k++) pl[i * 4 + k] = strtof(e, &e);
      }
      void *cv = ((void *(*)(void *, float *, int, float))vt[3])(pc, pl, n, merge);
      free(pl);
      convexes[nconvex] = cv;
      printf("convex %d %s\n", nconvex++, cv ? "ok" : "null");
    } else if (line[0] == 'V') {
      int n; sscanf(p + 1, "%d", &n);
      vec *v = malloc(sizeof(vec) * (n + 1));
      vec **pv = malloc(sizeof(vec *) * (n + 1));
      for (int i = 0; i < n; i++) {
        readline(line, sizeof line);
        char *e = line;
        v[i].x = strtof(e, &e); v[i].y = strtof(e, &e); v[i].z = strtof(e, &e);
        pv[i] = &v[i];
      }
      void *cv = ((void *(*)(void *, vec **, int))vt[2])(pc, pv, n);
      free(v); free(pv);
      convexes[nconvex] = cv;
      printf("convex %d %s\n", nconvex++, cv ? "ok" : "null");
    } else if (line[0] == 'B') {
      vec a, b; char *e = p + 1;
      a.x = strtof(e, &e); a.y = strtof(e, &e); a.z = strtof(e, &e);
      b.x = strtof(e, &e); b.y = strtof(e, &e); b.z = strtof(e, &e);
      void *cv = ((void *(*)(void *, vec *, vec *))vt[8])(pc, &a, &b);
      convexes[nconvex] = cv;
      printf("convex %d %s\n", nconvex++, cv ? "ok" : "null");
    } else if (line[0] == 'C' && line[1] == 'V') {
      int i = atoi(p + 2);
      float vol = ((float (*)(void *, void *))vt[4])(pc, convexes[i]);
      float area = ((float (*)(void *, void *))vt[5])(pc, convexes[i]);
      printf("cvol %a %a\n", vol, area);
    } else if (line[0] == 'G') {
      int i; unsigned d; sscanf(p + 1, "%d %u", &i, &d);
      ((void (*)(void *, void *, unsigned))vt[6])(pc, convexes[i], d);
      printf("ok\n");
    } else if (line[0] == 'C') {
      int k, outer, drag; float eps; sscanf(p + 1, "%d %d %d %f", &k, &outer, &drag, &eps);
      unsigned char params[16]; memset(params, 0, sizeof params);
      params[0] = (unsigned char)outer; params[1] = (unsigned char)drag;
      memcpy(params + 4, &eps, 4);
      void *col = ((void *(*)(void *, void **, int, void *))vt[16])(pc, &convexes[nconvex - k], k, params);
      if (!col) { printf("collide null\n"); fflush(stdout); continue; }
      collides[ncollide] = col;
      writecollide(ncollide++);
    } else if (line[0] == 'L') {
      char *e = p + 2; int n = (int)(strlen(e) / 2);
      unsigned char *b = malloc(n + 1);
      for (int i = 0; i < n; i++) { unsigned x; sscanf(e + 2 * i, "%2x", &x); b[i] = (unsigned char)x; }
      void *col = ((void *(*)(void *, char *, int, int))vt[20])(pc, (char *)b, n, 0);
      /* UnserializeCollide copies the surface (CPhysCollideCompactSurface::Init allocs); keep b anyway */
      collides[ncollide] = col;
      printf("collide %d size %d\n", ncollide++, col ? ((int (*)(void *, void *))vt[18])(pc, col) : -1);
    } else if (line[0] == 'Q') {
      int j = atoi(p + 1); void *c = collides[j];
      float vol = ((float (*)(void *, void *))vt[21])(pc, c);
      float area = ((float (*)(void *, void *))vt[22])(pc, c);
      vec mn, mx, mc;
      ((void (*)(void *, vec *, vec *, void *, vec *, vec *))vt[24])(pc, &mn, &mx, c, &zero, &zero);
      ((void (*)(void *, void *, vec *))vt[25])(pc, c, &mc);
      vec oa = ((vec (*)(void *, void *))vt[27])(pc, c);
      printf("q %a %a %a %a %a %a %a %a %a %a %a %a %a %a\n", vol, area, mn.x, mn.y, mn.z, mx.x, mx.y, mx.z,
             mc.x, mc.y, mc.z, oa.x, oa.y, oa.z);
    } else if (line[0] == 'E') {
      int j; vec d; char *e = p + 1; j = (int)strtol(e, &e, 10);
      d.x = strtof(e, &e); d.y = strtof(e, &e); d.z = strtof(e, &e);
      vec r = ((vec (*)(void *, void *, vec *, vec *, vec *))vt[23])(pc, collides[j], &zero, &zero, &d);
      printf("e %a %a %a\n", r.x, r.y, r.z);
    } else if (line[0] == 'T') {
      int j; vec s, en, hh; char *e = p + 1; j = (int)strtol(e, &e, 10);
      s.x = strtof(e, &e); s.y = strtof(e, &e); s.z = strtof(e, &e);
      en.x = strtof(e, &e); en.y = strtof(e, &e); en.z = strtof(e, &e);
      hh.x = strtof(e, &e); hh.y = strtof(e, &e); hh.z = strtof(e, &e);
      vec mn = {-hh.x, -hh.y, -hh.z};
      unsigned char tr[512]; memset(tr, 0, sizeof tr);
      ((void (*)(void *, vec *, vec *, vec *, vec *, void *, vec *, vec *, void *))vt[32])(pc, &s, &en, &mn, &hh,
                                                                                          collides[j], &zero, &zero, tr);
      printtrace(tr);
    } else if (line[0] == 'S') {
      int j, k; vec s, en; char *e = p + 1; j = (int)strtol(e, &e, 10); k = (int)strtol(e, &e, 10);
      s.x = strtof(e, &e); s.y = strtof(e, &e); s.z = strtof(e, &e);
      en.x = strtof(e, &e); en.y = strtof(e, &e); en.z = strtof(e, &e);
      unsigned char tr[512]; memset(tr, 0, sizeof tr);
      ((void (*)(void *, vec *, vec *, void *, vec *, void *, vec *, vec *, void *))vt[35])(pc, &s, &en, collides[k],
                                                                                          &zero, collides[j], &zero, &zero, tr);
      printtrace(tr);
    } else if (line[0] == 'Y') {
      /* Polysoup: n lines "ax ay az bx by bz cx cy cz mat" -> ConvertPolysoupToCollide(soup, false). */
      int n = atoi(p + 1);
      void *soup = ((void *(*)(void *))vt[11])(pc);
      for (int i = 0; i < n; i++) {
        readline(line, sizeof line);
        char *e = line; vec a, b, c;
        a.x = strtof(e, &e); a.y = strtof(e, &e); a.z = strtof(e, &e);
        b.x = strtof(e, &e); b.y = strtof(e, &e); b.z = strtof(e, &e);
        c.x = strtof(e, &e); c.y = strtof(e, &e); c.z = strtof(e, &e);
        int mat = (int)strtol(e, &e, 10);
        ((void (*)(void *, void *, vec *, vec *, vec *, int))vt[13])(pc, soup, &a, &b, &c, mat);
      }
      void *col = ((void *(*)(void *, void *, int))vt[14])(pc, soup, 0);
      ((void (*)(void *, void *))vt[12])(pc, soup);
      if (!col) { printf("collide null\n"); fflush(stdout); continue; }
      collides[ncollide] = col;
      writecollide(ncollide++);
    } else if (line[0] == 'M') {
      /* Virtual mesh: "M nv nt build" then nv vertex lines and nt index-triple lines. */
      char *e = p + 1;
      g_mesh.nv = (int)strtol(e, &e, 10); g_mesh.nt = (int)strtol(e, &e, 10);
      int build = (int)strtol(e, &e, 10);
      for (int i = 0; i < g_mesh.nv; i++) {
        readline(line, sizeof line);
        char *f = line;
        g_mesh.v[i].x = strtof(f, &f); g_mesh.v[i].y = strtof(f, &f); g_mesh.v[i].z = strtof(f, &f);
      }
      for (int i = 0; i < g_mesh.nt; i++) {
        readline(line, sizeof line);
        char *f = line;
        for (int k = 0; k < 3; k++) g_mesh.idx[i * 3 + k] = (unsigned short)strtol(f, &f, 10);
      }
      static void *vtab[3] = {(void *)mesh_get, (void *)mesh_bounds, (void *)mesh_sphere};
      static void *obj[1];
      obj[0] = vtab;
      unsigned char params[24]; memset(params, 0, sizeof params);
      void *handler = obj; memcpy(params, &handler, 8);
      params[16] = (unsigned char)build;
      void *col = ((void *(*)(void *, void *))vt[47])(pc, params);
      if (!col) { printf("collide null\n"); fflush(stdout); continue; }
      collides[ncollide] = col;
      writecollide(ncollide++);
    } else if (line[0] == 'W') {
      writecollide(atoi(p + 1));
    } else if (line[0] == 'X') {
      break;
    } else if (line[0] == '#' || line[0] == '\n') {
      continue;
    } else {
      printf("error unknown %s", line);
    }
    fflush(stdout);
  }
  return 0;
}
