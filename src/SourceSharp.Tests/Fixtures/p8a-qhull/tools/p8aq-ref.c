/* p8aq-ref.c - C oracle driver for the qhull 2.6 C# port.
 *
 * Reads point sets, runs exactly IVP's qhull call sequence on each
 * (first "qhull Qs Pp C-0 W1e-14 E1.0e-6", then the QJ retry loop), and
 * prints a canonical text dump that the C# port must reproduce byte for byte.
 *
 * Input format (text):
 *   set <name> <n>
 *   <x> <y> <z>          n lines, doubles as C99 hex floats (%a) or decimal
 * repeated.  Lines starting with '#' are ignored.
 *
 * Output format, per set:
 *   set <name> <n>
 *   try <k> "<command>" exit <code>        one line per attempt
 *   ok <k>   | fail                        which attempt succeeded
 *   f <id> t<toporient> s<simplicial> <n0> <n1> <n2> <offset> : <pid> <pid> ...
 *   end
 */
#include "qhull_a.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

char qh_version[] = "ipion 99/07/10";

static FILE *nullerr;
static int quiet;
#define OUT if (!quiet) printf

/* IVP's retry constants (float values widened to double) */
#define P8AQ_JOGGLE_START 9.999999960041972e-13
#define P8AQ_JOGGLE_ADD   9.999999960041972e-13
#define P8AQ_JOGGLE_MUL   1.2000000476837158
#define P8AQ_JOGGLE_MAX   0.019999999552965164

static void dump_hull(void) {
  facetT *facet;
  vertexT *vertex, **vertexp;
  setT *vertices;
  FORALLfacets {
    OUT("f %u t%d s%d %a %a %a %a :", facet->id, (int)facet->toporient,
           (int)facet->simplicial, facet->normal[0], facet->normal[1],
           facet->normal[2], facet->offset);
    vertices = qh_facet3vertex(facet);
    FOREACHvertex_(vertices)
      OUT(" %d", qh_pointid(vertex->point));
    qh_settempfree(&vertices);
    OUT("\n");
  }
}

static int run_one(int k, char *cmd, int n, coordT *coords) {
  int exitcode, curlong, totlong;
  exitcode = qh_new_qhull(3, n, coords, 0, cmd, NULL, nullerr);
  OUT("try %d \"%s\" exit %d\n", k, cmd, exitcode);
  if (exitcode == 0) {
    OUT("ok %d\n", k);
    dump_hull();
  }
  qh_freeqhull(!qh_ALL);
  qh_memfreeshort(&curlong, &totlong);
  return exitcode;
}

static void run_set(const char *name, int n, coordT *coords) {
  char cmd[256];
  double joggle;
  int k = 0;
  OUT("set %s %d\n", name, n);
#ifdef P8AQ_PROBE_SETS
  fprintf(stderr, "SET %s\n", name);
#endif
  strcpy(cmd, "qhull Qs Pp C-0 W1e-14 E1.0e-6");
  if (run_one(k++, cmd, n, coords) != 0) {
    joggle = P8AQ_JOGGLE_START;
    for (;;) {
      sprintf(cmd, "qhull Qs QJ%G C-0 Pp W1e-14 E1.0e-18", joggle);
      if (run_one(k++, cmd, n, coords) == 0)
        break;
      joggle = (P8AQ_JOGGLE_ADD + joggle) * P8AQ_JOGGLE_MUL;
      if (P8AQ_JOGGLE_MAX <= joggle) {
        OUT("fail\n");
        break;
      }
    }
  }
  OUT("end\n");
}

int main(int argc, char **argv) {
  FILE *in = stdin;
  char line[512], name[256];
  int n, i, repeat = 1, r;
  coordT *coords;
  if (argc > 1 && strcmp(argv[1], "-") != 0) {
    in = fopen(argv[1], "r");
    if (!in) { perror(argv[1]); return 2; }
  }
  if (argc > 2)
    repeat = atoi(argv[2]);   /* benchmark mode: run each set repeat times, print once */
  nullerr = fopen("/dev/null", "w");
  while (fgets(line, sizeof line, in)) {
    if (line[0] == '#' || line[0] == '\n')
      continue;
    if (sscanf(line, "set %255s %d", name, &n) != 2) {
      fprintf(stderr, "bad line: %s", line);
      return 2;
    }
    coords = (coordT *)malloc(sizeof(coordT) * 3 * (n > 0 ? n : 1));
    for (i = 0; i < n; i++) {
      if (!fgets(line, sizeof line, in) ||
          sscanf(line, "%lf %lf %lf", &coords[3*i], &coords[3*i+1], &coords[3*i+2]) != 3) {
        fprintf(stderr, "bad point %d in %s\n", i, name);
        return 2;
      }
    }
    if (repeat > 1) {
      quiet = 1;
      for (r = 1; r < repeat; r++)
        run_set(name, n, coords);
      quiet = 0;
    }
    run_set(name, n, coords);
    free(coords);
  }
  fflush(stdout);
  return 0;
}
