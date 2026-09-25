/* p8a native cook benchmark: reads a job file (P/V/C commands, the oracle protocol), keeps it in
 * memory, then cooks every job `reps` times through vphysics.so (ConvexFromPlanes/Verts ->
 * ConvertConvexToCollideParams -> CollideSize -> CollideWrite -> DestroyCollide), timing only the
 * cook calls. Single-threaded (the library is not thread-safe).
 * usage: p8a-bench <vphysics.so> <jobs.in> <reps>
 */
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef struct { float x, y, z; } vec;
typedef struct { int kind; int n; float merge; float *data; } convexjob;
typedef struct { int nconv; convexjob *conv; int outer; } job;

static double now(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec + t.tv_nsec * 1e-9; }

int main(int argc, char **argv) {
  void *h = dlopen(argv[1], RTLD_NOW);
  if (!h) { printf("dlopen %s\n", dlerror()); return 1; }
  void *(*ci)(const char *, int *) = (void *(*)(const char *, int *))dlsym(h, "CreateInterface");
  int rc; void *pc = ci("VPhysicsCollision007", &rc); void **vt = *(void ***)pc;
  FILE *f = fopen(argv[2], "r"); int reps = atoi(argv[3]);
  static char line[1 << 20];
  job *jobs = calloc(200000, sizeof(job)); int njobs = 0;
  convexjob pend[256]; int npend = 0;
  while (fgets(line, sizeof line, f)) {
    if (line[0] == 'P' || line[0] == 'V') {
      convexjob c; c.kind = line[0]; float merge = 0; sscanf(line + 1, "%d %f", &c.n, &merge); c.merge = merge;
      int w = c.kind == 'P' ? 4 : 3; c.data = malloc(sizeof(float) * w * c.n);
      for (int i = 0; i < c.n; i++) { fgets(line, sizeof line, f); char *e = line; for (int k = 0; k < w; k++) c.data[i * w + k] = strtof(e, &e); }
      pend[npend++] = c;
    } else if (line[0] == 'C') {
      int k, outer; sscanf(line + 1, "%d %d", &k, &outer);
      job j; j.nconv = k; j.outer = outer; j.conv = malloc(sizeof(convexjob) * k); memcpy(j.conv, pend + npend - k, sizeof(convexjob) * k);
      npend = 0; jobs[njobs++] = j;
    }
  }
  char *buf = malloc(1 << 24); unsigned long sum = 0;
  double best = 1e30;
  for (int r = 0; r < reps; r++) {
    double t0 = now();
    for (int i = 0; i < njobs; i++) {
      void *cv[256];
      for (int c = 0; c < jobs[i].nconv; c++) {
        convexjob *cj = &jobs[i].conv[c];
        if (cj->kind == 'P') cv[c] = ((void *(*)(void *, float *, int, float))vt[3])(pc, cj->data, cj->n, cj->merge);
        else { vec *pv[4096]; for (int q = 0; q < cj->n; q++) pv[q] = (vec *)&cj->data[q * 3]; cv[c] = ((void *(*)(void *, vec **, int))vt[2])(pc, pv, cj->n); }
      }
      unsigned char params[16]; memset(params, 0, 16); params[0] = (unsigned char)jobs[i].outer; float eps = 0.25f; memcpy(params + 4, &eps, 4);
      void *col = ((void *(*)(void *, void **, int, void *))vt[16])(pc, cv, jobs[i].nconv, params);
      if (col) {
        int n = ((int (*)(void *, void *))vt[18])(pc, col);
        ((int (*)(void *, char *, void *, int))vt[19])(pc, buf, col, 0);
        sum += (unsigned char)buf[n - 1];
        ((void (*)(void *, void *))vt[17])(pc, col);
      }
    }
    double t = now() - t0; if (t < best) best = t;
  }
  printf("jobs %d reps %d best %.4f s  %.3f us/job  %.0f jobs/s  (chk %lu)\n", njobs, reps, best, best * 1e6 / njobs, njobs / best, sum);
  return 0;
}
